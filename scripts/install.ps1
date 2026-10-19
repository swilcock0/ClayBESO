# Builds ClayBESO and copies it into the Grasshopper Libraries folder.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/install.ps1 [-Framework net48]
# Restart Rhino afterwards.
param(
    [ValidateSet("net7.0-windows", "net48")]
    [string]$Framework = "net7.0-windows"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }

& $dotnet build "$root\src\ClayBeso.GH\ClayBeso.GH.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$src = Join-Path $root "src\ClayBeso.GH\bin\Release\$Framework"
$dst = Join-Path $env:APPDATA "Grasshopper\Libraries\ClayBESO"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$src\*" $dst -Recurse -Force
Write-Host "Installed $Framework build to $dst. Restart Rhino to load it."
