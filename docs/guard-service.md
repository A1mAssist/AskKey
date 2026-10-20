# AskKey Guard (B) implementation contract

## Goal

Cold start with Windows Hello. A per-machine guard service holds a per-user KEK. The app asks the
user for Windows Hello, gets a fresh nonce from the service, and asks the service to release the
KEK. The app unwraps the vault key. The passphrase-wrapped copy remains as recovery.

## Why the app does not sign

`KeyCredentialManager.RequestCreateAsync` / `RequestSignAsync` need a UWP `CoreWindow`. A packaged
desktop WinUI app cannot provide one: the credential UI opens in a separate process, never reaches
the app's foreground window, and the call fails with `0x80098044` after the user passes the PIN.
`UserConsentVerifierInterop.RequestVerificationForWindowAsync` takes an `HWND`, so the prompt is
owned by the Ask Key window and stays modal to it. The app therefore verifies the user with
`UserConsentVerifier` and the service trusts the caller identity plus a fresh one-time nonce.

## Service

- Executable: `AskKey.Guard.Service.exe`
- Installed path: `%ProgramFiles%\AskKey\Guard\AskKey.Guard.Service.exe` (outside Velopack's `current\`, so app updates do not replace it)
- Account: `NT AUTHORITY\LocalService`; do not switch accounts after `master.bin` exists, because DPAPI is per-account
- Pipe: `askkey-guard` (fixed, per-machine)
- Modes: `--service` (SCM), `--console` (development), `--dev` (allow development paths and local data root)
- Master KEK: `%ProgramData%\AskKey\Guard\master.bin`, 32 bytes, DPAPI `CurrentUser` under the service account; ACL service account + Administrators
- Per-user KEK: HKDF-SHA256(master, salt = SHA256(UTF8(SID)), info = `AskKey Guard v1`, 32 bytes)
- Enrollment proof: `%ProgramData%\AskKey\Guard\users\<SID>\hello.proof` (32 bytes); ACL service account
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

## Uninstall residue

Uninstalling Ask Key removes the service and `%ProgramFiles%\AskKey`, but intentionally leaves `%ProgramData%\AskKey\Guard` in place. This is not a leak:

- `master.bin` is DPAPI-protected under the `LocalService` account. Deleting it invalidates every per-user KEK, so every Windows Hello enrollment in `users\` becomes permanently unusable.
- Deleting `users\<SID>` on uninstall would also destroy an account that only has Guard unlock (no passphrase copy) with no warning.

So the data-directory component is Permanent in the Guard MSI, and a normal uninstall leaves an empty or populated `ProgramData\AskKey\Guard` owned by `LocalService`, `SYSTEM` and `Administrators`. A standard user cannot read it after the service is stopped; that is expected, not a permissions bug.

To fully reset Guard state on a test machine, run the following elevated **after** uninstalling, and only when no account still relies on Windows Hello unlock:

```powershell
sc.exe delete AskKeyGuard   # already removed by the uninstall; ignore "not found"
Remove-Item 'C:\ProgramData\AskKey\Guard' -Recurse -Force
```

`vpk pack --msi` cannot install this service: its per-machine lifecycle hook runs impersonated/unelevated. A production release needs a dedicated per-machine Guard MSI with WiX `ServiceInstall`/`ServiceControl`, or the elevated script above as the repair path. Never point the service at a per-user `%LOCALAPPDATA%` install.

## Protocol

- 4-byte little-endian length + UTF-8 JSON, max 64 KiB
- Request: `{ "v":2, "op":"status"|"challenge"|"consume"|"consume-enroll"|"unenroll"|"reset", "nonce":base64?, "proof":base64? }`
- Response: `{ "v":2, "ok":true|false, "error":string?, "nonce":base64?, "kek":base64?, "enrolled":bool? }`
- `status`: returns whether the caller SID has an enrollment
- `challenge`: creates a nonce for the caller SID
- `consume(nonce)`: requires an existing enrollment, consumes the nonce once, returns the per-user KEK
- `consume-enroll(nonce, proof)`: first enrollment. Checks the proof (32 bytes) against the stored one before consuming the nonce, stores the proof for the SID, returns the per-user KEK
- `unenroll(proof)`: verifies the stored vault-key proof, then removes only that SID's enrollment files
- `reset(nonce)`: consumes a fresh nonce and removes that SID's enrollment without the old proof. The passphrase reset uses it because the forgotten passphrase destroyed the key that produced the proof; the caller check still requires a signed Ask Key image
- Errors: `caller_rejected`, `not_enrolled`, `bad_nonce`, `bad_proof`, `protocol`

The proof is an HMAC of the vault key, so it binds the enrollment to one vault. A first enrollment
needs no proof to match; every later one must present the same proof, so a different vault can never
take over an existing SID's enrollment.

## Vault

- File: `%APPDATA%\AskKey\key-material\vault-key.app.guard`
- Format: `AKGD` | version 1 | nonce 12 | tag 16 | ciphertext 32
- AES-GCM with a 32-byte KEK
- `Vault` additions:
  - `IsGuardUnlockEnabled`
  - `IsPassphraseProtected`
  - `IsHighSecurityEnabled` = passphrase || guard
  - `UnlockWithGuard(byte[] kek)`
  - `EnableGuardUnlock(byte[] kek)`
  - `CreateGuardEnrollmentProof()` (HMAC-SHA256 of `AskKey Guard enroll v1` under the vault key)
  - `DisableGuardUnlock()`
- Existing `PassphraseAppKeyStore` behavior and files are unchanged. The guard file never replaces the passphrase copy.

## App flow

- Startup: if guard enabled, challenge -> Windows Hello (`UserConsentVerifier` on the Ask Key window) -> consume -> KEK -> `Vault.UnlockWithGuard`; on any failure/cancel, fall back to the passphrase dialog.
- Enrollment: after passphrase high security is enabled, challenge -> Windows Hello -> consume-enroll (returns KEK) -> `Vault.EnableGuardUnlock(kek)`.
- Disable: remove the guard copy, unenroll with the current vault proof; keep the passphrase copy.
- Erase local data: unenroll with the current vault proof before the vault key disappears, then delete the guard file.
- Forgot passphrase: the passphrase is the only factor that opens the vault, so the app resets the Guard enrollment, erases the local library without the key, and returns to onboarding.
- OOBE/settings: add a Hello unlock toggle. If the guard service is unavailable, keep passphrase-only and explain the per-machine requirement.

The nonce is taken before the Hello prompt, so a slow prompt cannot outlive its challenge.

## Security boundary

If app data was erased while the service was unavailable, an administrator can remove `%ProgramData%\AskKey\Guard\users\<SID>`; this only clears that user's Guard enrollment, not the vault. The app cannot clear it itself: the stored enrollment proof is an HMAC of the vault key that erase is about to destroy, so after a new vault key exists the old proof can never be presented again. When erase cannot reach the service, the app now says so and names the directory to delete instead of leaving the user to discover the dead enrollment later.

The service prevents same-user processes from getting the KEK without a fresh Hello confirmation from
the app and a valid caller path. Same-user malware can still trigger the Hello prompt itself and
read the KEK out of the app's memory, so this is a strong-user-presence gate, not a sandbox. After
the KEK is released, the app process holds the vault key; code injected into the signed app is out
of scope. A local administrator or SYSTEM compromise can extract the service master key.
