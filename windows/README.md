# AskKey for Windows

This tree is the Windows port of AskKey. Credential management, the local Broker, the `askkey` helper, approval details, lock-screen privacy and credential templates live here; the macOS app remains the source of truth for shared behavior.

## Requirements

- Windows 10 2004 or later, x64 (the default publish target; `win-arm64` is also available).
- .NET 10 SDK for building and testing. The published package is self-contained and does not require a separate .NET runtime.
- Windows Hello or a PIN for management and credential reveal operations.

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
