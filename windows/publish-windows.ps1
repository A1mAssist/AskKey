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
$project = Join-Path $PSScriptRoot 'AskKey.App\AskKey.App.csproj'
$versionSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AskKey.Integrations\AskKeyVersion.cs') -Raw
if ($versionSource -notmatch 'Current\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"') {
    throw 'Could not read AskKeyVersion.Current.'
}
$version = $Matches.version
$artifactName = "AskKey-$version-$Runtime"
$publishDir = Join-Path $OutputDirectory $artifactName
$zipName = if ($CertificateThumbprint) { "$artifactName.zip" } else { "$artifactName-unsigned.zip" }
$zipPath = Join-Path $OutputDirectory $zipName

if (Test-Path -LiteralPath $publishDir) {
    throw "Publish directory already exists; choose an empty output directory: $publishDir"
}
if (Test-Path -LiteralPath $zipPath) {
    throw "Archive already exists; choose an empty output directory: $zipPath"
}

if (-not $SkipTests) {
    & dotnet test $solution -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$unsigned = -not $CertificateThumbprint
$publishArgs = @(
    'publish', $project, '-c', 'Release', '-r', $Runtime, '--self-contained', 'true',
    '-p:PublishSingleFile=false', '-o', $publishDir, '--nologo', '-v', 'minimal'
)
if ($unsigned) { $publishArgs += '-p:AskKeyUnsigned=true' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$hostPath = Join-Path $publishDir 'AskKey.exe'
$helperPath = Join-Path $publishDir 'Helpers\askkey.exe'
foreach ($required in @($hostPath, $helperPath, (Join-Path $publishDir 'Helpers\hostfxr.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
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

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $zipName" -Encoding ascii

Write-Output "Published: $publishDir"
Write-Output "Archive:   $zipPath"
if ($unsigned) {
    Write-Warning 'This build is unsigned. Agent connections use the embedded helper integrity manifest instead of Authenticode.'
}
