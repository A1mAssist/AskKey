[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish'),
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'AskKey.slnx'
$project = Join-Path $PSScriptRoot 'AskKey.WinUI\AskKey.WinUI.csproj'
$buildDir = Join-Path $PSScriptRoot 'AskKey.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0'
$versionSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AskKey.Integrations\AskKeyVersion.cs') -Raw
if ($versionSource -notmatch 'Current\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"') {
    throw 'Could not read AskKeyVersion.Current.'
}
$version = $Matches.version
$artifactName = "AskKey-$version-$Runtime"
$publishDir = Join-Path $OutputDirectory $artifactName
$releaseDir = Join-Path $OutputDirectory 'release'

if (Test-Path -LiteralPath $publishDir) {
    throw "Publish directory already exists; choose an empty output directory: $publishDir"
}

if (-not $SkipTests) {
    & dotnet test $solution -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
$unsigned = -not $CertificateThumbprint

$buildArgs = @(
    'build', $project, '-c', 'Release', '-p:Platform=x64', "-p:Version=$version", '--nologo', '-v', 'minimal'
)
if ($unsigned) { $buildArgs += '-p:AskKeyUnsigned=true' }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

# Copy the BUILD output, not `dotnet publish`. Publish drops every *.xbf and the Views\
# folder, and the packaged app then fails at startup with XAML parsing failed.
if (-not (Test-Path -LiteralPath $buildDir -PathType Container)) {
    throw "Build output directory is missing: $buildDir"
}
Copy-Item -Path (Join-Path $buildDir '*') -Destination $publishDir -Recurse -Force

# Ship Simplified Chinese satellite assemblies only. Every other locale folder is a
# Microsoft.ui.xaml *.mui pair; WinUI falls back to the neutral resources next to the
# root binaries when a satellite is absent, so dropping them cannot break startup.
# "Neutral" means the invariant copy inside the root DLLs, not the en-us folder.
$keepSatellites = 'zh-CN'
foreach ($satellite in Get-ChildItem -LiteralPath $publishDir -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'Microsoft.ui.xaml.dll.mui') }) {
    if ($keepSatellites -notcontains $satellite.Name) {
        Remove-Item -LiteralPath $satellite.FullName -Recurse -Force
    }
}

$hostPath = Join-Path $publishDir 'AskKey.exe'
$helperPath = Join-Path $publishDir 'Helpers\askkey.exe'
foreach ($required in @($hostPath, $helperPath, (Join-Path $publishDir 'App.xbf'), (Join-Path $publishDir 'Views'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "The published layout is missing $required."
    }
}

if ($CertificateThumbprint) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -eq $signtool) {
        $signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
            -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object FullName -match '\\x64\\signtool\.exe$' |
            Sort-Object FullName -Descending |
            Select-Object -First 1
    }
    if ($null -eq $signtool) { throw 'signtool.exe was not found. Install the Windows SDK or sign manually.' }
    $signToolPath = if ($signtool -is [System.Management.Automation.ApplicationInfo]) { $signtool.Source } else { $signtool.FullName }

    foreach ($file in @($hostPath, $helperPath)) {
        & $signToolPath sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $file
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for $file." }
        & $signToolPath verify /pa /all $file
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $file." }
    }

    $hostSignature = Get-AuthenticodeSignature -LiteralPath $hostPath
    $helperSignature = Get-AuthenticodeSignature -LiteralPath $helperPath
    if ($hostSignature.Status -ne 'Valid' -or $helperSignature.Status -ne 'Valid' `
        -or $hostSignature.SignerCertificate.Subject -ne $helperSignature.SignerCertificate.Subject) {
        throw 'Host and helper must both carry a valid Authenticode signature from the same publisher.'
    }
}

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# Velopack: one `vpk pack` emits the whole set (Setup.exe, MSI, Portable.zip, full.nupkg) plus
# assets.win.json / releases.win.json / RELEASES. Never rename or hand-edit those files.
# --msi --instLocation PerMachine is what installs into Program Files; the one-click
# Setup.exe is always per-user (%LocalAppData%\AskKey) and cannot be redirected at pack time.
$packArgs = @(
    'pack', '--packId', 'AskKey', '--packVersion', $version, '--packDir', $publishDir,
    '--packTitle', 'Ask Key', '--packAuthors', 'A1mAssist',
    '--mainExe', 'AskKey.exe', '--outputDir', $releaseDir,
    '--msi', '--instLocation', 'PerMachine'
)
# Velopack signs the installer packages itself; the payload is signed above.
if (-not $unsigned) {
    $packArgs += '--signParams', "/sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256"
}
& vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE." }

Write-Output "Published: $publishDir"
Write-Output "Release:   $releaseDir"
Get-ChildItem -LiteralPath $releaseDir | Select-Object Name, Length | Format-Table -AutoSize
if ($unsigned) {
    Write-Warning 'Unsigned release: SmartScreen will warn on install. Agent connections rely on the embedded helper integrity manifest.'
}
