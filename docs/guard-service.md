# AskKey Guard (B) implementation contract

## Goal

Cold start with Windows Hello. A per-machine guard service holds a per-user KEK. The app gets a fresh nonce, signs it with a Hello key, the service verifies the signature and caller, and returns the KEK. The app unwraps the vault key. The passphrase-wrapped copy remains as recovery.

## Service

- Executable: `AskKey.Guard.Service.exe`
- Installed path: `%ProgramFiles%\AskKey\Guard\AskKey.Guard.Service.exe` (outside Velopack's `current\`, so app updates do not replace it)
- Account: `NT AUTHORITY\LocalService`; do not switch accounts after `master.bin` exists, because DPAPI is per-account
- Pipe: `askkey-guard` (fixed, per-machine)
- Modes: `--service` (SCM), `--console` (development), `--dev` (allow development paths and local data root)
- Master KEK: `%ProgramData%\AskKey\Guard\master.bin`, 32 bytes, DPAPI `CurrentUser` under the service account; ACL service account + Administrators
- Per-user KEK: HKDF-SHA256(master, salt = SHA256(UTF8(SID)), info = `AskKey Guard v1`, 32 bytes)
- Public key: `%ProgramData%\AskKey\Guard\users\<SID>\hello.pub` (DER SubjectPublicKeyInfo); ACL service account
- Caller SID: derived from the pipe client process token, never from the request body
- Caller path: `AskKey.exe` under an official install root; verify Authenticode when signed; `--dev` allows the repo build path
- Server identity: the client matches the pipe server PID to the running `AskKeyGuard` service, and checks the image path/account when Windows exposes them; a user-started process cannot impersonate the service
- Pipe creation uses `PIPE_REJECT_REMOTE_CLIENTS`, so the service is local-only even before caller verification
- `unenroll` is proof-only: the proof is an HMAC of the vault key, so a caller that can present it already holds the vault key; unenroll never returns key material
- Directory safety: Guard refuses reparse points and directories owned by another non-administrator, so a pre-created `%ProgramData%\AskKey` cannot redirect state
- Nonce: 32 random bytes, 5-minute TTL, one-time, max 4 pending per SID

Development mode stores guard state under `%LOCALAPPDATA%\AskKey\Guard` instead of ProgramData.

## Install

The release `AskKeySetup-<version>.exe` installs the app and this service together and uninstalls both. The elevated script below is the repair/development path:

```powershell
& .\AskKey.Guard.Service\Install-GuardService.ps1
```

The script copies the publish folder to `%ProgramFiles%\AskKey\Guard`, secures the binary and ProgramData directories, registers `AskKeyGuard` as `LocalService`, and starts it. `-Uninstall` stops and deletes the service but keeps user vault material and Guard state.

`vpk pack --msi` cannot install this service: its per-machine lifecycle hook runs impersonated/unelevated. A production release needs a dedicated per-machine Guard MSI with WiX `ServiceInstall`/`ServiceControl`, or the elevated script above as the repair path. Never point the service at a per-user `%LOCALAPPDATA%` install.

## Protocol

- 4-byte little-endian length + UTF-8 JSON, max 64 KiB
- Request: `{ "v":1, "op":"status"|"challenge"|"enroll"|"unlock"|"unenroll", "nonce":base64?, "publicKey":base64?, "signature":base64?, "proof":base64? }`
- Response: `{ "v":1, "ok":true|false, "error":string?, "nonce":base64?, "kek":base64?, "enrolled":bool? }`
- `status`: returns whether the caller SID has a public key
- `challenge`: creates a nonce for the caller SID
- `enroll(publicKey, nonce, signature, proof)`: verifies the signature over the nonce; if a prior public key exists, requires `proof` to match the stored enrollment proof; stores the public key/proof for the SID; returns the per-user KEK
- `unlock(nonce, signature)`: verifies, consumes the nonce, returns the per-user KEK
- `unenroll(proof)`: verifies the stored vault-key proof, then removes only that SID's enrollment files
- Errors: `caller_rejected`, `not_enrolled`, `bad_nonce`, `bad_signature`, `protocol`

The public key is DER SPKI. The signature is verified with RSA PKCS#1 v1.5/SHA-256 first, then PSS/SHA-256 for compatibility.

## Vault

- File: `%APPDATA%\AskKey\key-material\vault-key.app.guard`
- Format: `AKGD` | version 1 | keyNameLen u16 | keyName UTF-8 | nonce 12 | tag 16 | ciphertext 32
- AES-GCM with a 32-byte KEK
- `Vault` additions:
  - `IsGuardUnlockEnabled`
  - `GuardHelloKeyName`
  - `IsPassphraseProtected`
  - `IsHighSecurityEnabled` = passphrase || guard
  - `UnlockWithGuard(byte[] kek)`
  - `EnableGuardUnlock(byte[] kek, string helloKeyName)`
  - `CreateGuardEnrollmentProof()` (HMAC-SHA256 of `AskKey Guard enroll v1` under the vault key)
  - `DisableGuardUnlock()`
- Existing `PassphraseAppKeyStore` behavior and files are unchanged. The guard file never replaces the passphrase copy.

## App flow

- Startup: if guard enabled, status -> open Hello key -> challenge -> sign -> unlock -> KEK -> `Vault.UnlockWithGuard`; on any failure/cancel, fall back to the passphrase dialog.
- Enrollment: after passphrase high security is enabled, status -> create Hello key -> public key -> challenge -> sign -> enroll (returns KEK) -> `Vault.EnableGuardUnlock(kek, helloKeyName)`.
- Disable: remove the guard copy, unenroll with the current vault proof, then delete the Hello key; keep the passphrase copy.
- Erase local data: unenroll with the current vault proof before the vault key disappears, then delete the guard file and Hello key.
- OOBE/settings: add a Hello unlock toggle. If the guard service is unavailable, keep passphrase-only and explain the per-machine requirement.

## Security boundary

If app data was erased while the service was unavailable, an administrator can remove `%ProgramData%\AskKey\Guard\users\<SID>`; this only clears that user's Guard enrollment, not the vault.

The service prevents same-user processes from getting the KEK without a fresh Hello signature and a valid caller path. After the KEK is released, the app process holds the vault key; code injected into the signed app is out of scope. A local administrator or SYSTEM compromise can extract the service master key.
