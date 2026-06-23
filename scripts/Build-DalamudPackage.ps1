$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "GposeCast.sln"
$project = Join-Path $root "GposeCast\GposeCast.csproj"
$repoJsonPath = Join-Path $root "repo.json"
$packageSource = Join-Path $root "GposeCast\bin\x64\Release\GposeCast\latest.zip"
$dist = Join-Path $root "dist"
$plugins = Join-Path $root "plugins"
$distPackage = Join-Path $dist "GposeCast-latest.zip"
$repoPackage = Join-Path $plugins "GposeCast.zip"
$zipCheck = Join-Path $root "zipcheck"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $solution)) { throw "Solution not found: $solution" }
if (-not (Test-Path $project)) { throw "Project not found: $project" }
if (-not (Test-Path $repoJsonPath)) { throw "Repo manifest not found: $repoJsonPath" }

[xml]$projectXml = Get-Content $project
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read <Version> from $project"
}

$repoEntries = @(Get-Content $repoJsonPath -Raw | ConvertFrom-Json)
if ($repoEntries.Count -lt 1) { throw "repo.json has no plugin entries." }
$repoEntry = $repoEntries[0]

if ($repoEntry.InternalName -ne "GposeCast") {
    throw "repo.json InternalName mismatch. Expected GposeCast, found '$($repoEntry.InternalName)'."
}
if ($repoEntry.AssemblyVersion -ne $version) {
    throw "repo.json AssemblyVersion '$($repoEntry.AssemblyVersion)' does not match project version '$version'."
}
if ([int]$repoEntry.DalamudApiLevel -ne 15) {
    throw "repo.json DalamudApiLevel '$($repoEntry.DalamudApiLevel)' does not match expected API 15."
}
if ($repoEntry.DownloadLinkInstall -notlike "*/plugins/GposeCast.zip") {
    throw "repo.json DownloadLinkInstall should point at plugins/GposeCast.zip. Found '$($repoEntry.DownloadLinkInstall)'."
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Remove-Item $distPackage -Force -ErrorAction SilentlyContinue
Remove-Item $repoPackage -Force -ErrorAction SilentlyContinue
Remove-Item $packageSource -Force -ErrorAction SilentlyContinue
Remove-Item $zipCheck -Recurse -Force -ErrorAction SilentlyContinue

Invoke-Checked -Name "dotnet restore" -Command { dotnet restore $solution }
Invoke-Checked -Name "dotnet build" -Command { dotnet build $solution -c Release -p:Platform=x64 --no-restore }

if (-not (Test-Path $packageSource)) {
    throw "DalamudPackager did not create expected package: $packageSource"
}

Copy-Item $packageSource $distPackage -Force
Copy-Item $packageSource $repoPackage -Force

Expand-Archive $repoPackage $zipCheck -Force
foreach ($required in @("GposeCast.dll", "GposeCast.deps.json", "GposeCast.json")) {
    $requiredPath = Join-Path $zipCheck $required
    if (-not (Test-Path $requiredPath)) {
        throw "Package is missing required file: $required"
    }
}

$zipManifest = Get-Content (Join-Path $zipCheck "GposeCast.json") -Raw | ConvertFrom-Json
if ($zipManifest.InternalName -ne "GposeCast") {
    throw "Package InternalName mismatch. Expected GposeCast, found '$($zipManifest.InternalName)'."
}
if ($zipManifest.AssemblyVersion -ne $version) {
    throw "Package AssemblyVersion '$($zipManifest.AssemblyVersion)' does not match project version '$version'."
}
if ([int]$zipManifest.DalamudApiLevel -ne 15) {
    throw "Package DalamudApiLevel '$($zipManifest.DalamudApiLevel)' does not match expected API 15."
}

$dllPath = Resolve-Path (Join-Path $zipCheck "GposeCast.dll")
$dllVersion = [System.Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString()
if ($dllVersion -ne $version) {
    throw "DLL assembly version '$dllVersion' does not match project version '$version'."
}

Write-Host "Created release package: $repoPackage"
Write-Host "Created local copy:      $distPackage"
Write-Host "Verified version:        $version"
Write-Host "Verified DalamudApiLevel: 15"
