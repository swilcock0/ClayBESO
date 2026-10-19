# Builds the Rhino 8 Yak package (.yak) for ClayBESO into ./artifacts.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/package.ps1
# The version comes from <Version> in src/ClayBeso.GH/ClayBeso.GH.csproj.
# The package holds both builds: Rhino 8 loads net7.0/ under .NET Core (default) and net48/
# under .NET Framework.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
$yak = "C:\Program Files\Rhino 8\System\Yak.exe"
if (-not (Test-Path $yak)) { throw "Yak.exe not found at $yak (needs Rhino 8)." }

$proj = Join-Path $root "src\ClayBeso.GH\ClayBeso.GH.csproj"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $proj." }

& $dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts "yak"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

$bin = Join-Path $root "src\ClayBeso.GH\bin\Release"
foreach ($pair in @(@("net7.0-windows", "net7.0"), @("net48", "net48"))) {
    $dst = Join-Path $stage $pair[1]
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item (Join-Path $bin "$($pair[0])\*") $dst -Recurse -Exclude *.pdb
}

$manifest = @"
---
name: ClayBESO
version: $version
authors:
- Sam Wilcock
description: >
  Shell analysis and BESO thickness optimisation for 3D-printed clay, with non-planar,
  variable-height layer toolpaths and per-point flow factors.
url: https://github.com/swilcock0/ClayBESO
icon: icon.png
keywords:
- clay
- 3d-printing
- beso
- topology-optimization
- shell
- non-planar
- fem
"@
Set-Content -Path (Join-Path $stage "manifest.yml") -Value $manifest -Encoding utf8
Copy-Item (Join-Path $root "src\ClayBeso.GH\Icons\icon.png") (Join-Path $stage "icon.png")

Push-Location $stage
try {
    & $yak build --platform win
    if ($LASTEXITCODE -ne 0) { throw "yak build failed." }
} finally { Pop-Location }

Get-ChildItem $stage -Filter *.yak | ForEach-Object {
    Move-Item $_.FullName (Join-Path $artifacts $_.Name) -Force
    Write-Host "Built artifacts\$($_.Name)"
}
