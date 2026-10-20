#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [switch]$Unsigned
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,5}$') {
    throw "Version must be a three-part Windows Installer version such as 1.0.0: $Version"
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$serviceProject = Join-Path $repoRoot 'src\AskKey.Guard.Service\AskKey.Guard.Service.csproj'
$publishDir = Join-Path $repoRoot 'src\AskKey.Guard.Service\bin\Release\net10.0-windows'
$wixProject = Join-Path $PSScriptRoot 'AskKey.Guard.Installer.wixproj'
$msiPath = Join-Path $PSScriptRoot "bin\Release\AskKeyGuard-$Version.msi"

if (-not (Test-Path -LiteralPath $serviceProject -PathType Leaf)) {
    throw "Guard service project not found: $serviceProject"
}

Write-Host "Publishing AskKey Guard Service to $publishDir"
# The MSI harvest takes every file under this directory, so a stale file from an
# earlier publish would ship inside the package.
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
& dotnet publish $serviceProject -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -p:AskKeyUnsigned=$($Unsigned.IsPresent) -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$serviceExe = Join-Path $publishDir 'AskKey.Guard.Service.exe'
if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) {
    throw "Published service executable not found: $serviceExe"
}

Write-Host "Building MSI with WiX SDK; version $Version"
# MSBuild does not track GuardPublishDir passed as a command-line property, so an
# unchanged .wxs would keep the previous MSI instead of re-harvesting the payload.
& dotnet build $wixProject -c Release -t:Rebuild -p:ProductVersion=$Version -p:GuardPublishDir=$publishDir
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
    throw "Expected MSI was not produced: $msiPath"
}

Write-Host "MSI: $msiPath"
