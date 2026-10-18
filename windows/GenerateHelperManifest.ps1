[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not [IO.Directory]::Exists($rootPath)) {
    throw "Helper root does not exist: $rootPath"
}
$prefix = $rootPath + [IO.Path]::DirectorySeparatorChar
$files = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Helper payload contains a reparse point: $($file.FullName)"
    }
    $full = [IO.Path]::GetFullPath($file.FullName)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Helper file escaped the root: $full"
    }
    $relative = $full.Substring($prefix.Length).Replace('\', '/')
    $stream = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }
    $files[$relative] = $hash
}
if ($files.Count -eq 0) {
    throw "Helper root contains no files: $rootPath"
}
$json = [ordered]@{
    version = 1
    files = $files
} | ConvertTo-Json -Depth 4
$outputPath = [IO.Path]::GetFullPath($Output)
$parent = [IO.Path]::GetDirectoryName($outputPath)
if ($parent) {
    [IO.Directory]::CreateDirectory($parent) | Out-Null
}
[IO.File]::WriteAllText($outputPath, $json, [Text.UTF8Encoding]::new($false))
