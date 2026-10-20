# AskKey Guard per-machine MSI

## Result

`installer/Guard` builds a WiX Toolset 6 MSI for the AskKey Guard service. The package installs the self-contained `win-x64` service payload to `%ProgramFiles%\AskKey\Guard`, registers `AskKeyGuard` under `NT AUTHORITY\LocalService`, sets it to automatic startup, and starts it after installation.

Normal users install `AskKeySetup-<version>.exe`, the Burn bundle in `installer/Bundle` that chains this MSI with the app MSI. This MSI remains the repair and advanced-deployment path.

The MSI does not install, modify, or remove the per-user `%APPDATA%\AskKey` data. The Guard state directory is created with a protected DACL that grants full control to SYSTEM and Administrators, and full control to LocalService. That component is permanent so uninstall does not discard the service's `%ProgramData%\AskKey\Guard` state.

## Build

From the repository root:

```powershell
pwsh .\installer\Guard\build-guard-msi.ps1
```

The default version is `1.0.0`. To build another MSI version:

```powershell
pwsh .\installer\Guard\build-guard-msi.ps1 -Version 1.0.1
```

Add `-Unsigned` when the release uses an unsigned `AskKey.exe`; this passes `AskKeyUnsigned=true` to the service publish. The MSI itself is still unsigned.

The script publishes `AskKey.Guard.Service` as a self-contained `win-x64` application into:

```text
src\AskKey.Guard.Service\bin\Release\net10.0-windows
```

It then builds:

```text
installer\Guard\bin\Release\AskKeyGuard-<version>.msi
```

The WiX 6.0.2 SDK and its build tasks are restored by NuGet/MSBuild. No global `wix.exe` is required. The SDK includes its own `wix.exe` under the restored package.

The packaging project excludes PDBs from the installation payload. All remaining published files are harvested, including runtime files under `runtimes\`.

`GuardPublishDir` must point at a publish directory, not at the service's `bin` tree: the `bin` tree also contains the RID-specific intermediate copy of the whole payload, and the harvest is recursive. `src\AskKey.Build` passes a clean publish directory explicitly, and `build-guard-msi.ps1` clears and republishes its directory before building.

## Installation behavior

- Scope: per-machine (`ALLUSERS=1`, x64 package).
- Install directory: `%ProgramFiles%\AskKey\Guard`.
- Service name: `AskKeyGuard`.
- Display name: `Ask Key Guard`.
- Account: `NT AUTHORITY\LocalService`.
- Start mode: automatic.
- Service command line: `AskKey.Guard.Service.exe --service`.
- Install and upgrade: stop an existing `AskKeyGuard` service before installation, then start it after installation.
- Uninstall: stop and delete the service.
- Major upgrade: the same UpgradeCode is reused; `MajorUpgrade` blocks downgrades and shows a clear error message.
- Data directory: `%ProgramData%\AskKey\Guard`.
- DACL: `O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;LS)`. This removes inherited access, sets the owner to Administrators, grants SYSTEM and Administrators full control, and grants LocalService full control (FA) with inheritance to child files and directories.
- `%APPDATA%\AskKey` is not in the MSI directory table.

The data-directory component is marked permanent, so uninstalling the MSI removes the service and binaries but retains Guard state under `%ProgramData%\AskKey\Guard`. A clean reset must be done explicitly by an administrator.

## Verification after an elevated install

```powershell
Get-Service AskKeyGuard | Select-Object Name, Status, StartType
sc.exe qc AskKeyGuard
icacls "$env:ProgramData\AskKey\Guard"
```

`sc.exe qc` should report `NT AUTHORITY\LocalService` and `AUTO_START`. `icacls` should show SYSTEM, Administrators and LocalService with full control. The DACL should not include inherited per-user entries.

## Boundaries and risks

- The produced MSI is unsigned. Sign the MSI and the installed binaries before a production release.
- The Guard service keeps its normal caller policy. Use the `-Unsigned` build switch for an unsigned `AskKey.exe`; otherwise Guard rejects that caller.
- Installing or upgrading the MSI requires elevation. Building and inspecting it does not.
- When using the split MSIs instead of the bundle, install the app MSI first and this Guard MSI second; uninstall Guard first so the app uninstall cannot remove a running service's binary.
- This document and the build script do not install the MSI or change the service, ProgramData, or user data.
- A rebuilt package with the same `ProductVersion` is not a major upgrade. Increment the version before installing over an existing 1.0.0 install.
- The MSI does not include an MSI UI. It can be installed from an elevated prompt with `msiexec /i .\installer\Guard\bin\Release\AskKeyGuard-1.0.0.msi`.
