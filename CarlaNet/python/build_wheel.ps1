[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Editable,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$scriptDir     = $PSScriptRoot
$carlaNetRoot  = (Resolve-Path (Join-Path $scriptDir '..')).Path
$pkgDir        = Join-Path $scriptDir 'carlanet'
$dllsDir       = Join-Path $pkgDir   'dlls'
$buildDir      = Join-Path $scriptDir 'build'
$distDir       = Join-Path $scriptDir 'dist'
$csproj        = Join-Path $carlaNetRoot 'src/CarlaNet.Python/CarlaNet.Python.csproj'

Write-Host "[build_wheel] script dir : $scriptDir"
Write-Host "[build_wheel] carlanet   : $carlaNetRoot"
Write-Host "[build_wheel] csproj     : $csproj"

if ($Clean) {
    Write-Host "[build_wheel] cleaning previous build artifacts"
    if (Test-Path $dllsDir)  { Remove-Item -Recurse -Force $dllsDir }
    if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
    if (Test-Path $distDir)  { Remove-Item -Recurse -Force $distDir }
    Get-ChildItem -Path $scriptDir -Filter '*.egg-info' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
    New-Item -ItemType Directory -Force -Path $dllsDir | Out-Null
}

if (-not (Test-Path $dllsDir)) {
    New-Item -ItemType Directory -Force -Path $dllsDir | Out-Null
}

Write-Host "[build_wheel] running dotnet publish -> $dllsDir"
dotnet publish $csproj -c Release -o $dllsDir
if ($LASTEXITCODE -ne 0) {
    throw "[build_wheel] dotnet publish failed with exit code $LASTEXITCODE"
}

# Shim is python/carlanet/__init__.py (canonical); no stray carlanet.py is published.

if ($Editable) {
    Write-Host "[build_wheel] performing editable install (pip install -e .)"
    python -m pip install -e $scriptDir
    if ($LASTEXITCODE -ne 0) {
        throw "[build_wheel] editable pip install failed with exit code $LASTEXITCODE"
    }
    Write-Host "[build_wheel] editable install complete"
    return
}

Write-Host "[build_wheel] ensuring 'build' package is available"
python -m pip install --upgrade build
if ($LASTEXITCODE -ne 0) {
    throw "[build_wheel] failed to install/upgrade 'build' (exit $LASTEXITCODE)"
}

Write-Host "[build_wheel] building wheel"
python -m build --wheel $scriptDir
if ($LASTEXITCODE -ne 0) {
    throw "[build_wheel] python -m build failed with exit code $LASTEXITCODE"
}

$wheelPath = Get-ChildItem -Path (Join-Path $distDir '*.whl') -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $wheelPath) {
    throw "[build_wheel] no wheel produced in $distDir"
}

Write-Host "[build_wheel] wheel built: $($wheelPath.FullName)"

if ($Install) {
    Write-Host "[build_wheel] installing wheel with --force-reinstall"
    python -m pip install --force-reinstall $wheelPath.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "[build_wheel] wheel install failed with exit code $LASTEXITCODE"
    }
    Write-Host "[build_wheel] install complete"
}
