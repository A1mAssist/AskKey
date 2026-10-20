#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\AskKey.Guard.Service.exe'),

    [Parameter(ParameterSetName = 'Install')]
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'AskKey\Guard'),

    [Parameter(ParameterSetName = 'Uninstall')]
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AskKeyGuard'
$serviceAccount = 'NT AUTHORITY\LocalService'
$dataDirectory = Join-Path $env:ProgramData 'AskKey\Guard'

function Invoke-Sc {
    & sc.exe @args | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Stop-And-RemoveService {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { return }
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
    Invoke-Sc delete $serviceName
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Service $serviceName did not disappear after deletion."
}

if ($Uninstall) {
    Stop-And-RemoveService
    Write-Host "Removed service $serviceName. Guard binaries and ProgramData state were kept."
    return
}

$exe = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Guard service executable not found: $exe"
}

$sourceDirectory = Split-Path -Parent $exe
$targetDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
$targetExe = Join-Path $targetDirectory 'AskKey.Guard.Service.exe'
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
if (-not [string]::Equals($sourceDirectory, $targetDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $targetDirectory -Recurse -Force
}

# The service runs as LocalService. ProgramData is pre-created with an owner and
# DACL that a standard user cannot replace before the service starts.
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
& icacls.exe $targetDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)RX' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX' | Out-Null
& icacls.exe $targetDirectory /setowner '*S-1-5-32-544' /T | Out-Null
& icacls.exe $dataDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)F' | Out-Null
& icacls.exe $dataDirectory /setowner '*S-1-5-32-544' /T | Out-Null
if (($LASTEXITCODE -ne 0)) { throw 'icacls failed while securing the Guard directories.' }

Stop-And-RemoveService
New-Service -Name $serviceName `
    -BinaryPathName ('"{0}" --service' -f $targetExe) `
    -DisplayName 'Ask Key Guard' `
    -Description 'Verifies Windows Hello signatures and releases the per-user Ask Key guard KEK.' `
    -StartupType Automatic | Out-Null
Invoke-Sc config $serviceName obj= $serviceAccount
Invoke-Sc failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
Write-Host "Installed $serviceName as $serviceAccount from $targetExe."
