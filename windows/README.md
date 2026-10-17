# AskKey for Windows

This tree is the Windows port of AskKey. Credential management, the local Broker, the `askkey` helper, approval details, lock-screen privacy and credential templates live here; the macOS app remains the source of truth for shared behavior.

## Requirements

- Windows 10 2004 or later, x64 (the default publish target; `win-arm64` is also available).
- .NET 10 SDK for building and testing. The published package is self-contained and does not require a separate .NET runtime.
- Windows Hello or a PIN for management and credential reveal operations.

## Build and test

```powershell
dotnet test .\windows\AskKey.slnx
dotnet run --project .\windows\AskKey.App\AskKey.App.csproj
```

Debug builds set `ASKKEY_DEVELOPMENT=1` and use the `dev` data roots, so they do not touch a normal installed vault.

## Publish

```powershell
.\windows\publish-windows.ps1
```

The script runs the tests, publishes a self-contained `win-x64` package, verifies the required `AskKey.exe` and `Helpers\askkey.exe` layout, and writes a versioned ZIP plus SHA-256 file under `windows\publish`.

To sign the host and helper with a certificate from the current user's certificate store:

```powershell
.\windows\publish-windows.ps1 -CertificateThumbprint <thumbprint>
```

Both files must be signed by the same publisher. Without a signature the script writes an artifact named `...-unsigned.zip`; a Release build rejects agent connections because the helper trust check requires matching Authenticode signatures. For local testing only, an unsigned published build can be launched with `ASKKEY_DEVELOPMENT=1`; never distribute that build.

## Install

1. Quit AskKey from the tray before replacing files.
2. Extract the package into `%LOCALAPPDATA%\AskKey`, so the layout is `%LOCALAPPDATA%\AskKey\AskKey.exe` and `%LOCALAPPDATA%\AskKey\Helpers\askkey.exe`.
3. Launch `%LOCALAPPDATA%\AskKey\AskKey.exe`.

The official helper topology requires that exact install location. Running from the download folder is only for development.

Credentials and key material are stored under `%APPDATA%\AskKey`; settings, delivery files and the installed app live under `%LOCALAPPDATA%\AskKey`. Deleting the install folder does not delete credentials. There is no automatic updater or installer yet.

Live Windows Hello prompts, session lock/unlock, Authenticode signing and hardware-specific behavior must be verified on the target machine; unit tests do not establish those.
