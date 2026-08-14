[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Editable,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# Console colour convention: green = info/success, yellow = warning (Write-Warning), red = error.
# (pip colour their own output; this only affects this script's [build_wheel] messages.)
function Write-Info { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Green }

# Run a native command with $ErrorActionPreference temporarily relaxed to 'Continue', then
# gate on its exit code. Why: under 'Stop', a native tool writing ANY line to stderr -- even a
# benign warning (a slow/unresponsive NuGet restore mirror, pip's "Ignoring invalid distribution",
# "A new release of pip is available", dotnet NU#### notices) -- gets promoted to a terminating
# NativeCommandError, especially once a caller merges streams with 2>&1. That turns a successful
# build into a reported failure. 'Continue' lets the warning through as a warning (still printed/
# logged) while the EXIT CODE remains the real success/failure signal. cmdlet errors elsewhere in
# this script keep the stricter 'Stop' behavior.
function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory)][string]$What,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Action } finally { $ErrorActionPreference = $prevEAP }
    if ($LASTEXITCODE -ne 0) { throw "[build_wheel] $What failed with exit code $LASTEXITCODE" }
}

$scriptDir     = $PSScriptRoot
$buildDir      = Join-Path $scriptDir 'build'
$distDir       = Join-Path $scriptDir 'dist'

Write-Info "[build_wheel] script dir : $scriptDir"

if ($Clean) {
    Write-Info "[build_wheel] cleaning previous build artifacts"
    if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
    if (Test-Path $distDir)  { Remove-Item -Recurse -Force $distDir }
    Get-ChildItem -Path $scriptDir -Filter '*.egg-info' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
}

if ($Editable) {
    Write-Info "[build_wheel] performing editable install (pip install -e .)"
    Invoke-NativeChecked 'editable pip install' { python -m pip install -e $scriptDir }
    Write-Info "[build_wheel] editable install complete"
    return
}

Write-Info "[build_wheel] ensuring 'build' package is available"
Invoke-NativeChecked "install/upgrade 'build'" { python -m pip install --upgrade build }

# Always wipe build/ before building the wheel. setuptools' package discovery would otherwise pick up
# any stale carlacontrol copy left under build/lib and re-nest it (build/lib/build/lib/.../carlacontrol),
# compounding every run and polluting the wheel. (pyproject also filters discovery to carlacontrol*.)
if (Test-Path $buildDir) {
    Write-Info "[build_wheel] wiping stale build/ before wheel build"
    Remove-Item -Recurse -Force -LiteralPath $buildDir
}

Write-Info "[build_wheel] building wheel"
Invoke-NativeChecked 'python -m build' { python -m build --wheel $scriptDir }

$wheelPath = Get-ChildItem -Path (Join-Path $distDir '*.whl') -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $wheelPath) {
    throw "[build_wheel] no wheel produced in $distDir"
}

Write-Info "[build_wheel] wheel built: $($wheelPath.FullName)"

if ($Install) {
    Write-Info "[build_wheel] installing wheel with --force-reinstall"
    Invoke-NativeChecked 'wheel install' { python -m pip install --force-reinstall $wheelPath.FullName }
    Write-Info "[build_wheel] install complete"
}
