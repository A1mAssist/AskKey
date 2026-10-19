# AskKey for Windows

This tree is the Windows port of AskKey. Credential management, the local Broker, the `askkey` helper, approval details, lock-screen privacy and credential templates live here; the macOS app remains the source of truth for shared behavior.

## Requirements

- Windows 10 2004 or later, x64 (the default publish target; `win-arm64` is also available).
- .NET 10 SDK for building and testing. The published package is self-contained and does not require a separate .NET runtime.
- Windows Hello or a PIN for management and credential reveal operations.

## Security model

The Agent is treated as only partly trusted: it may run commands as your Windows user, but
it must not receive a stored value without the approval flow.

- **Default mode** wraps the vault key with Windows DPAPI (`CurrentUser`). That protects the
  files at rest — copying `%APPDATA%\AskKey` to another account or machine reveals nothing.
  It does **not** isolate one program from another. Any process running as you, including an
  Agent's shell, can call DPAPI and unwrap the same key. DPAPI's boundary is the user
  account, not the Ask Key executable.
- **High security mode** (Settings) wraps the vault key with a passphrase you choose and
  deletes the DPAPI copy. Reading the key files is then not enough to decrypt the library,
  and Ask Key asks for the passphrase every time it starts. The passphrase cannot be
  recovered: if you forget it, the library cannot be opened.
- In both modes the Agent never gets a stored value without the approval flow. What neither
  mode defends against is malicious code running as you while Ask Key is unlocked and
  holding the key in memory.

The helper `askkey.exe` must sit beside the host inside an official install directory; a
relocated copy is refused. Unsigned builds trust the helper through an embedded integrity
manifest instead of a publisher signature.

## Build and test

```powershell
dotnet test .\windows\AskKey.slnx
dotnet run --project .\windows\AskKey.WinUI\AskKey.WinUI.csproj -p:Platform=x64
```

Debug builds set `ASKKEY_DEVELOPMENT=1` and use the `dev` data roots, so they do not touch a normal installed vault.

## Publish

```powershell
.\windows\publish-windows.ps1
```

The script runs the tests, builds a self-contained `win-x64` payload, verifies the required `AskKey.exe` and `Helpers\askkey.exe` layout, then hands it to Velopack (`vpk pack`). It takes the version from `AskKeyVersion.Current` in `AskKey.Integrations\AskKeyVersion.cs`; bump that file to cut a release.

The payload ships Simplified Chinese satellite assemblies only: every locale folder
except `zh-CN` is deleted before packing. Those folders hold nothing but
`Microsoft.ui.xaml` `*.mui` pairs, and WinUI falls back to the neutral resources in
the root binaries when a satellite is absent, so startup is unaffected. To ship more
languages, extend `$keepSatellites` in the script.

`vpk pack` writes the whole release set into `windows\publish\release`: `AskKey-win-Setup.exe`, `AskKey-win.msi`, `AskKey-win-Portable.zip`, `AskKey-<version>-full.nupkg`, plus the update feed `assets.win.json`, `releases.win.json` and `RELEASES`. Upload all of them together and never hand-edit the feed files.

To sign the host and helper with a certificate from the current user's certificate store:

```powershell
.\windows\publish-windows.ps1 -CertificateThumbprint <thumbprint>
```

Signed builds require both files to be signed by the same publisher. Without a signature the whole release set ships unsigned. That build embeds a manifest of every file under `Helpers\` and verifies it before Agent connections, so automatic setup still works; editing the helper payload invalidates the manifest and requires a rebuild. Windows SmartScreen still warns for unsigned downloads. `ASKKEY_DEVELOPMENT=1` remains a local-only bypass.

## Install

Two official installers ship in the release set, and they install to different places:

| Installer | Install scope | Location | Elevation |
|---|---|---|---|
| `AskKey-win.msi` | Per-machine | `%ProgramFiles%\Ask Key\current\` | Yes (UAC) |
| `AskKey-win-Setup.exe` | Per-user | `%LocalAppData%\AskKey\current\` | No |

Both extract the payload into a `current\` subfolder behind a small execution stub, so the real host and helper are at:

```
<install root>\current\AskKey.exe
<install root>\current\Helpers\askkey.exe
```

`OfficialInstallTopology` accepts exactly these two roots; a copy anywhere else is refused by the helper so an Agent cannot open a tampered host.

To install machine-wide into Program Files, run the MSI. To place a per-user copy elsewhere, `Setup.exe --installto <dir>` overrides the default folder at install time, but the topology gate then requires the folder be named `AskKey` or `Ask Key`.

Credentials and key material are stored under `%APPDATA%\AskKey`; settings and the broker pipe live under `%LOCALAPPDATA%\AskKey`. Neither follows the install folder, so deleting the install folder does not delete credentials.

Live Windows Hello prompts, session lock/unlock, Authenticode signing and hardware-specific behavior must be verified on the target machine; unit tests do not establish those.
