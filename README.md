<p align="center">
  <img src="assets/icon.png" width="96" alt="Ask Key icon">
</p>

<h1 align="center">Ask Key for Windows</h1>

<p align="center"><strong>A local credential manager for AI agents on Windows.</strong></p>

<p align="center">
  <a href="README.zh-CN.md">简体中文</a> · <a href="README.md">English</a>
</p>

Ask Key keeps credentials encrypted on your PC. Agents discover credentials by name and metadata, then ask the local Broker for access. You decide whether a request may proceed. Approved text values are delivered as environment variables to the target process, and file values as short-lived private files. The agent never receives the stored plaintext.

This repository contains the Windows implementation only. The release tooling is .NET; there are no PowerShell build scripts.

> **macOS upstream:** The original macOS app and shared product/security documentation live at [sudoHG/AskKey](https://github.com/sudoHG/AskKey). This repository does not vendor the macOS source tree.

## What it does

- **Find keys:** agents can look up credential names, usage notes and variable mappings without receiving the values.
- **Control access:** use **Allow**, **Ask every time** or **Hidden** for each credential.
- **Review writes:** creating, changing, deleting and reorganizing credentials can require approval. The approval is bound to the exact frozen contents shown to you.
- **Keep values local:** Windows Hello or your PIN is required to reveal management values and frozen file contents.
- **Protect the approval screen:** detailed approvals are dismissed when the session locks, disconnects or logs off, then restored when the session returns.
- **Start quickly:** use presets for API access, SSH, GitHub Apps, cloud accounts and database connections.

## How it works

```mermaid
flowchart LR
    A[Agent] --> H[askkey helper]
    H --> B[Broker in the app]
    B --> P{Your approval}
    P -->|approved| T[Target process<br/>environment or private temp file]
    P -->|denied| X[Nothing delivered]
```

The helper runs the target command with only the credentials selected for that request. The Broker enforces the credential permission and owns the approval queue. The Agent sees the target's output or exit status, not the stored credential values.

## Repository layout

| Path | Purpose |
|---|---|
| `AskKey.WinUI` | WinUI 3 desktop app, tray menu, onboarding and settings |
| `AskKey.Broker` | Local approval, policy and delivery broker |
| `AskKey.Vault` | Encrypted credential store and key management |
| `AskKey.Helper` | `askkey.exe` MCP helper and command wrapper |
| `AskKey.Integrations` | Codex, Claude Code, Cursor and Grok client setup |
| `AskKey.System` | Restricted process and Windows runtime integration |
| `*.Tests` | Unit and integration tests |
| `tools/AskKey.Build` | .NET release tool: helper manifest, build, signing and Velopack packaging |

## Requirements

- Windows 10 2004 or later, or Windows 11.
- x64.
- .NET 10 SDK for building and testing. Published packages are self-contained.
- Windows Hello or a PIN for management and credential-reveal operations.

## Build and test

Run these commands from the repository root:

```text
dotnet test .\AskKey.slnx
dotnet run --project .\AskKey.WinUI\AskKey.WinUI.csproj -p:Platform=x64
```

Debug builds set `ASKKEY_DEVELOPMENT=1` and use separate development data roots, so they do not touch a normally installed vault.

## Publish

The release tool is a .NET project, not a PowerShell script:

```text
dotnet run --project .\tools\AskKey.Build -- publish
```

To sign the host and helper with a certificate from the current user's certificate store:

```text
dotnet run --project .\tools\AskKey.Build -- publish --certificate-thumbprint <thumbprint>
```

The tool reads the version from `AskKey.Integrations\AskKeyVersion.cs`, runs the tests, builds the self-contained x64 payload, verifies `AskKey.exe` and `Helpers\askkey.exe`, trims satellite resources to `zh-CN`, signs when requested, runs `vpk pack`, publishes the Guard service, builds the Guard MSI, and finally produces `AskKeySetup-<version>.exe` with both the app and Guard.

The default output is `artifacts\publish\release`:

- `AskKeySetup-<version>.exe` — **recommended installer**, one UAC prompt for the app and Guard
- `AskKey-win.msi` — app only, per-machine install in `%ProgramFiles%\AskKey\current\`
- `AskKeyGuard-<version>.msi` — Guard only, for repair or advanced deployment
- `AskKey-win-Setup.exe` — per-user install without Guard; Hello unlock is unavailable
- `AskKey-win-Portable.zip`
- `AskKey-<version>-full.nupkg`
- `assets.win.json`, `releases.win.json`, `RELEASES`

Upload the complete release set together. Do not hand-edit the feed files.

### Windows Hello Guard service

Normal installation uses `AskKeySetup-<version>.exe`: it installs the app first, then Guard, and removes both when uninstalled from Apps & Features. The two standalone MSIs are for repair or advanced deployment; manually install the app first and `AskKeyGuard-<version>.msi` second, and uninstall Guard first. The per-user `AskKey-win-Setup.exe` cannot use Hello unlock.

For development, publish the service separately:

```text
dotnet run --project .\tools\AskKey.Build -- guard
```

`AskKey.Guard.Service\Install-GuardService.ps1` is the repair/development path; `installer\Guard\build-guard-msi.ps1 -Unsigned` builds the unsigned Guard MSI. After both MSIs exist, `dotnet run --project .\tools\AskKey.Build -- bundle` regenerates `AskKeySetup`. See `docs/guard-msi.md`. A production release still needs code signing.

Unsigned builds embed a SHA-256 manifest of the `Helpers\` payload and verify it before Agent connections. Windows SmartScreen still warns for unsigned downloads. Signed builds require the host and helper to carry signatures from the same publisher.

## Install

| Installer | Scope | Location | Elevation |
|---|---|---|---|
| `AskKeySetup-<version>.exe` | Per-machine, app + Guard | `%ProgramFiles%\AskKey\current\` + `%ProgramFiles%\AskKey\Guard\` | Yes (UAC) |
| `AskKey-win.msi` + `AskKeyGuard-<version>.msi` | Per-machine, split/repair | Same as above | Yes (UAC) |
| `AskKey-win-Setup.exe` | Per-user, no Guard | `%LocalAppData%\AskKey\current\` | No |

Run `AskKeySetup-<version>.exe` for normal installation. Uninstalling it from Apps & Features removes both the app and Guard. Use the split MSIs only for repair or advanced deployment: install the app first and Guard second; uninstall Guard first.

The installer extracts the payload behind a `current\` folder:

```text
<install root>\current\AskKey.exe
<install root>\current\Helpers\askkey.exe
```

Credentials and key material live under `%APPDATA%\AskKey`. Settings and the Broker pipe live under `%LOCALAPPDATA%\AskKey`. Removing the install folder does not delete the credential library.

## Agent access

The app includes setup for Claude Code, Codex, Cursor and Grok CLI. Open **Agent access** in the app, check the client and confirm. Setup adds Ask Key as an MCP server, keeps the existing client settings, and verifies the helper and Broker before reporting the client as connected.

Any MCP stdio client can be configured manually with the installed helper:

```json
{
  "command": "C:\\Users\\<you>\\AppData\\Local\\AskKey\\current\\Helpers\\askkey.exe",
  "args": ["mcp"]
}
```

For a per-machine install, use `C:\\Program Files\\AskKey\\current\\Helpers\\askkey.exe` instead. Manual setup does not install discovery hooks or run the in-app connection check. Broker permissions, approval prompts and credential delivery protections still apply.

## Security model

The Agent is treated as only partly trusted: it may run commands as your Windows user, but it must not receive a stored value without the approval flow.

- **Default mode** wraps the vault key with Windows DPAPI (`CurrentUser`). This protects the files at rest, but any process running as you can unwrap the same key.
- **High security mode** wraps the vault key with a passphrase using PBKDF2-HMAC-SHA256 (600,000 iterations) and deletes the DPAPI copy. If the per-machine AskKey Guard service is installed, the passphrase unlocks the first launch and later starts can use Windows Hello; the passphrase remains the recovery path. Without the service, Ask Key asks for the passphrase every time. The passphrase cannot be recovered.
- Revealing or copying a stored value requires device confirmation. After one Windows Hello confirmation, the configured unlock window (5–60 minutes) covers further in-app management actions.
- The helper must sit beside the host inside an official install root; a relocated copy is refused.
- Ask Key has no backup or recovery feature. Keep the originals of your credentials somewhere else.

Live Windows Hello prompts, session lock/unlock, Authenticode signing and hardware-specific behavior must be verified on the target machine; unit tests do not establish those.

## License

MIT. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
