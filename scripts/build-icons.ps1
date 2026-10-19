# Renders the SVG icon sources in src/ClayBeso.GH/Icons to PNG.
#   - every <Name>.svg -> <Name>.png at 24x24 (Grasshopper component size, embedded in the .gha)
#   - ClayBeso.svg     -> icon.png at 128x128 (Rhino Package Manager icon, used by package.ps1)
# The PNGs are committed, so building the plugin does NOT need this script.
# Run it only after editing an SVG. It needs rsvg-convert, e.g. from conda-forge:
#   micromamba create -n icons -c conda-forge librsvg
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/build-icons.ps1 [-Env icons]
param([string]$Env = "icons")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root "src\ClayBeso.GH\Icons"

if (Get-Command rsvg-convert -ErrorAction SilentlyContinue) {
    $render = { param($svg, $png, $size) & rsvg-convert -w $size -h $size $svg -o $png }
} else {
    # micromamba is often a shell function or .bat wrapper, so look for the real executable.
    $candidates = @($env:MAMBA_EXE, (Join-Path $env:LOCALAPPDATA "micromamba\micromamba.exe")) +
        @(Get-Command micromamba.exe, micromamba -CommandType Application -ErrorAction SilentlyContinue | ForEach-Object Source)
    $mmPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $mmPath) { throw "Need rsvg-convert on PATH or micromamba with an '$Env' env containing librsvg." }
    $render = { param($svg, $png, $size) & $mmPath run -n $Env rsvg-convert -w $size -h $size $svg -o $png }
}

foreach ($svg in Get-ChildItem $dir -Filter *.svg) {
    $png = [IO.Path]::ChangeExtension($svg.FullName, ".png")
    & $render $svg.FullName $png 24
    if ($LASTEXITCODE -ne 0) { throw "Failed to render $($svg.Name)" }
    Write-Host "  $($svg.BaseName).png"
}
& $render (Join-Path $dir "ClayBeso.svg") (Join-Path $dir "icon.png") 128
Write-Host "  icon.png (128 px, package icon)"
