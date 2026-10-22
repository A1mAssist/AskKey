# Building Ask Key

Everything below is for working on Ask Key, not for using it. For installation and
day-to-day use, see [README](../README.md).

## Requirements

- Windows 10 2004 or later, or Windows 11, x64.
- .NET 10 SDK.
- [WiX Toolset 6](https://wixtoolset.org/) SDK — restored through NuGet by the installer
  projects; no global `wix.exe` is needed.
- The Velopack CLI: `dotnet tool install --global vpk`.
- Windows Hello or a PIN on the development machine if you exercise unlock flows.

## Repository layout

| Path | Purpose |
|---|---|
| `src\AskKey.WinUI` | WinUI 3 desktop app, tray menu, onboarding and settings |
| `src\AskKey.Broker` | Local approval, policy and delivery broker |
| `src\AskKey.Vault` | Encrypted credential store and key management |
| `src\AskKey.Helper` | `askkey.exe` MCP helper and command wrapper |
| `src\AskKey.Integrations` | Codex, Claude Code, Cursor and Grok client setup |
| `src\AskKey.System` | Restricted process and Windows runtime integration |
| `src\AskKey.Build` | Release tool: helper manifest, publish, signing and packaging |
| `src\installer` | WiX bundle, Guard MSI and installer text |
| `src\*.Tests` | Unit and integration tests |
| `docs` | Guard service notes and this file |

## Build and test

From the repository root:

```text
dotnet test .\AskKey.slnx
dotnet run --project .\src\AskKey.WinUI\AskKey.WinUI.csproj -p:Platform=x64
```

`-p:Platform=x64` is not optional for the app project: it is a Windows App SDK
self-contained build and refuses to build without a supported architecture.

The Broker test suite binds named pipes and occasionally collides when several test
projects run in parallel. If a run fails in `AskKey.Broker.Tests` on a socket or
timeout assertion, re-run that project alone before treating it as a regression:

```text
dotnet test .\src\AskKey.Broker.Tests\AskKey.Broker.Tests.csproj -c Release
```

Pass `-m:1` to `dotnet test` for the same reason when you need a deterministic full run.

### A development build with separate data

A Debug build uses the normal `%APPDATA%\AskKey` and `%LOCALAPPDATA%\AskKey` paths
unless you isolate it:

```powershell
$env:ASKKEY_DEVELOPMENT = '1'
$env:ASKKEY_DEBUG_RUN_DIRECTORY = "$env:TEMP\askkey-debug"
```

`ASKKEY_DEVELOPMENT=1` switches the vault to the `dev` subfolder and the single-instance
mutex to a `.dev` name. `ASKKEY_DEBUG_RUN_DIRECTORY` is honoured by **Debug builds only**
and must be a fully qualified directory whose ACL grants only you and inherits nothing,
otherwise the Broker refuses to start:

```powershell
$dir = "$env:TEMP\askkey-debug"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    $me, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl -Path $dir -AclObject $acl
```

Release builds ignore `ASKKEY_DEBUG_RUN_DIRECTORY` entirely. Never point a Release build
at the default namespace while the installed app is running: it overwrites
`%LOCALAPPDATA%\AskKey\daemon.pipe` and agents will connect to the wrong Broker.

## Publish

The release tool is a .NET project, not a PowerShell script:

```text
dotnet run --project .\src\AskKey.Build -- publish
```

To sign the host and helper with a certificate from the current user's certificate store:

```text
dotnet run --project .\src\AskKey.Build -- publish --certificate-thumbprint <thumbprint>
```

The version comes from `src\AskKey.Integrations\AskKeyVersion.cs`; `Directory.Build.props`
does not carry the shipping version.

The tool runs the tests, builds the self-contained x64 payload, verifies `AskKey.exe` and
`Helpers\askkey.exe`, trims satellite resources to `zh-CN`, signs when requested, runs
`vpk pack`, publishes the Guard service, builds the Guard MSI, and finally produces
`AskKeySetup-<version>.exe` containing both the app and Guard.

`--skip-tests` exists for re-running packaging after a green run; do not use it to skip a
red one.

### Output

Default output is `artifacts\publish\release`:

- `AskKeySetup-<version>.exe` — **recommended installer**, one UAC prompt for app and Guard
- `AskKey-win.msi` — app only, per-machine install in `%ProgramFiles%\AskKey\current\`
- `AskKeyGuard-<version>.msi` — Guard only, for repair or advanced deployment
- `AskKey-<version>.sha256` — SHA-256 of every other file in the folder
- `AskKey-<version>-full.nupkg`, `-delta.nupkg`, `releases.win.json`, `assets.win.json`,
  `RELEASES` — the Velopack update feed

The build also produces `AskKey-win-Setup.exe` (per-user, no Guard) and
`AskKey-win-Portable.zip`. Do not publish either: the per-user layout is a weaker security
boundary, and the topology gate rejects a portable copy once it has been moved.

## Release

1. Bump `AskKeyVersion.Current`.
2. Write the bilingual release notes, English first, matching the previous release's shape.
3. `dotnet run --project .\src\AskKey.Build -- publish`.
4. Upload `AskKeySetup-<version>.exe`, `AskKey-win.msi`, `AskKeyGuard-<version>.msi`,
   `AskKey-<version>.sha256`, `AskKey-<version>-full.nupkg`, `releases.win.json`,
   `assets.win.json` and `RELEASES` to the GitHub release.

The installed app reads the feed files from the GitHub release, so a release without them
can never be found by **Check for updates**. Do not hand-edit the feed files.

## Windows Hello Guard service

`AskKeySetup-<version>.exe` installs the app first and Guard second, and removes both on
uninstall. Run a newer setup over an existing install to upgrade: both MSIs keep a stable
UpgradeCode, so Windows Installer replaces them in place. The standalone MSIs exist for
repair and scripted deployment — install the app first, Guard second, and uninstall Guard
first. The per-user `AskKey-win-Setup.exe` cannot use Hello unlock, because it installs no
Guard.

For development, publish the service separately:

```text
dotnet run --project .\src\AskKey.Build -- guard
```

`src\AskKey.Guard.Service\Install-GuardService.ps1` is the repair/development path;
`src\installer\Guard\build-guard-msi.ps1 -Unsigned` builds the unsigned Guard MSI. With
both MSIs present, `dotnet run --project .\src\AskKey.Build -- bundle` regenerates
`AskKeySetup`. See [guard-msi.md](guard-msi.md) and [guard-service.md](guard-service.md).

Unsigned builds embed a SHA-256 manifest of the `Helpers\` payload and verify it before
Agent connections. Windows SmartScreen still warns for unsigned downloads. A signed build
must sign the host and helper with the same publisher.

## Troubleshooting packaging

- **`The payload contains more than one host executable: Helpers\win-x64\askkey.exe`** —
  an earlier `-r win-x64` build left a RID subtree in
  `src\AskKey.Helper\bin\x64\Release\net10.0-windows\`. Delete it (and
  `...\AskKey.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0\Helpers\win-x64`) and
  rebuild.
- **`Could not find a part of the path '<somewhere unrelated>\obj\...'`** — a stale MSBuild
  node. Run `dotnet build-server shutdown` and retry.
- **`vpk.exe was not found on PATH`** — `%USERPROFILE%\.dotnet\tools` is missing from
  `PATH`, or the global tool is not installed.

## What tests cannot establish

Live Windows Hello prompts, screen lock/unlock behaviour, Authenticode signing, installer
elevation and hardware-specific behaviour have to be verified on a real machine. A green
test run says nothing about them.
