$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"

$packageSource = Join-Path $root "GposeCast\bin\x64\Release\GposeCast\latest.zip"
$packageTarget = Join-Path $dist "GposeCast-latest.zip"

if (-not (Test-Path $packageSource)) {
    throw "Package not found: $packageSource"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

Copy-Item $packageSource $packageTarget -Force

Write-Host "Created package: $packageTarget"