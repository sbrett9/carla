#Requires -Version 5.1
<#
.SYNOPSIS
    Package an Unreal Engine Installed Build into a redistributable Windows archive.

.DESCRIPTION
    Windows peer of Scripts\Linux\MakeEngineDistribution.sh. Turns the directory produced by
        RunUAT.bat BuildGraph -Script=Engine/Build/InstalledEngineBuild.xml `
            -Target="Make Installed Build Win64" -set:WithWin64=true -set:WithDDC=true
    (which lands under  <engine>\LocalBuilds\Engine\Windows ) into a single, versioned archive plus a
    checksum and a provenance sidecar, ready to publish to Artifactory. A colleague extracts the
    archive, points CARLA_UNREAL_ENGINE_PATH at the extracted directory, and builds CARLA WITHOUT
    checking out or compiling the engine from source.

        UnrealEngine-<engineversion>-<branch>-<commit>-Win64.zip
        UnrealEngine-<engineversion>-<branch>-<commit>-Win64.zip.sha256
        UnrealEngine-<engineversion>-<branch>-<commit>-Win64.zip.metadata.txt

    Build-capability note: a Windows Installed Build compiles project C++ (the CARLA editor, cesium
    -native) using the TARGET machine's own Visual Studio 2022 + Windows SDK -- the engine does not
    bundle a compiler on Windows (unlike Linux, where it ships its own clang). So the archive is build
    -capable when it is a proper Installed Build (UnrealBuildTool + editor + InstalledBuild.txt present,
    which the preflight checks); the consumer must additionally have VS2022 installed. This script does
    not build CARLA itself -- -VerifyExtract only proves the archive round-trips.

.PARAMETER EngineRoot
    Source engine root (has .git and LocalBuilds\). Env: CARLA_UNREAL_ENGINE_PATH.

.PARAMETER InstalledDir
    The Installed Build tree. Default: <EngineRoot>\LocalBuilds\Engine\Windows.

.PARAMETER OutputDir
    Where to write the archive. Default: current directory.

.PARAMETER Name
    Override the archive base name (no extension).

.PARAMETER Compress
    zip (default) or 7z. zip prefers tar.exe (clean <base>\ top-level), else 7-Zip, else Compress-Archive.
    7z uses 7-Zip (fast, better ratio) and stores a 'Windows\' top-level.

.PARAMETER NoChecksum
    Skip the .sha256 sidecar.

.PARAMETER VerifyExtract
    After packaging, extract to a temp dir and re-check the tree (integrity, not a CARLA build).

.PARAMETER Strict
    Treat missing build-capability markers (InstalledBuild.txt / UBT / editor) as a hard error.

.EXAMPLE
    .\MakeEngineDistribution.ps1 -VerifyExtract
    Package <CARLA_UNREAL_ENGINE_PATH>\LocalBuilds\Engine\Windows and round-trip verify it.

.EXAMPLE
    .\MakeEngineDistribution.ps1 -Compress 7z -OutputDir D:\artifacts
    Fast 7-Zip archive written to the Artifactory-fallback disk path.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$EngineRoot,
    [string]$InstalledDir,
    [string]$OutputDir = (Get-Location).Path,
    [string]$Name,
    [ValidateSet('zip', '7z')]
    [string]$Compress = 'zip',
    [switch]$NoChecksum,
    [switch]$VerifyExtract,
    [switch]$Strict,

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
    @'
MakeEngineDistribution.ps1 - package an Unreal Engine Installed Build into a redistributable archive.

USAGE:
  .\MakeEngineDistribution.ps1 [options]

OPTIONS (PowerShell-native | legacy alias):
  -EngineRoot <dir>       --engine-dir=<dir>      Source engine root (default $CARLA_UNREAL_ENGINE_PATH).
  -InstalledDir <dir>     --installed-dir=<dir>   Installed Build tree (default <engine>\LocalBuilds\Engine\Windows).
  -OutputDir <dir>        --output-dir=<dir>      Where to write the archive (default current dir).
  -Name <name>            --name=<name>           Override the archive base name.
  -Compress <zip|7z>      --compress=<...>        Archive format (default zip).
  -NoChecksum             --no-checksum           Skip the .sha256 sidecar.
  -VerifyExtract          --verify-extract        Extract to a temp dir and re-check (integrity).
  -Strict                 --strict                Fail if build-capability markers are missing.
  -Help                   / -h  --help            Show this help.
'@ | Write-Host
}

# -- Normalize legacy "--flag" / "--flag=value" arguments (matches MakeDistribution.ps1) ----------
if ($Remaining) {
    for ($idx = 0; $idx -lt $Remaining.Count; $idx++) {
        $arg = $Remaining[$idx]
        if ($arg -match '^(--[^=]+)=(.*)$') { $key = $matches[1]; $val = $matches[2] }
        else { $key = $arg; $val = $null }
        if ($null -ne $val) { $next = $val }
        elseif ($idx + 1 -lt $Remaining.Count) { $next = $Remaining[$idx + 1] }
        else { $next = $null }
        switch -Regex ($key) {
            '^(--help|/\?|help)$'      { $Help = $true }
            '^(--no-checksum)$'        { $NoChecksum = $true }
            '^(--verify-extract)$'     { $VerifyExtract = $true }
            '^(--strict)$'             { $Strict = $true }
            '^(--engine-dir|--engine-root)$' { if ($null -eq $next) { throw "Argument '$key' requires a value." } $EngineRoot = $next; if ($null -eq $val) { $idx++ } }
            '^(--installed-dir)$'      { if ($null -eq $next) { throw "Argument '$key' requires a value." } $InstalledDir = $next; if ($null -eq $val) { $idx++ } }
            '^(--output-dir)$'         { if ($null -eq $next) { throw "Argument '$key' requires a value." } $OutputDir = $next; if ($null -eq $val) { $idx++ } }
            '^(--name)$'               { if ($null -eq $next) { throw "Argument '$key' requires a value." } $Name = $next; if ($null -eq $val) { $idx++ } }
            '^(--compress)$'           { if ($null -eq $next) { throw "Argument '$key' requires a value." } $Compress = $next; if ($null -eq $val) { $idx++ } }
            default { Show-Usage; throw "Unknown argument '$arg'." }
        }
    }
}

if ($Help) { Show-Usage; return }
if ($Compress -notin @('zip', '7z')) { throw "Invalid -Compress '$Compress'. Expected zip or 7z." }

# Console colour convention (matches MakeDistribution.ps1): green = info, red = failure.
function Write-Info { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-Fail { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Red }

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments)][string[]]$GitArgs)
    try { $out = & git @GitArgs 2>$null; if ($LASTEXITCODE -eq 0) { return ($out | Out-String).Trim() } } catch {}
    return $null
}

# --- Resolve the Installed Build tree and the source engine root (for git provenance) ----------------
if (-not $EngineRoot) { $EngineRoot = $env:CARLA_UNREAL_ENGINE_PATH }
if (-not $InstalledDir) {
    if (-not $EngineRoot) { throw "No engine dir: set CARLA_UNREAL_ENGINE_PATH or pass -EngineRoot / -InstalledDir." }
    $InstalledDir = Join-Path $EngineRoot 'LocalBuilds\Engine\Windows'
}
if (-not (Test-Path $InstalledDir)) {
    throw "Installed Build not found: $InstalledDir`n       Run Make Installed Build Win64 first (produces <engine>\LocalBuilds\Engine\Windows)."
}
$InstalledDir = (Resolve-Path $InstalledDir).Path
# If only -InstalledDir was given, infer the source engine root three levels up (for the git commit).
if (-not $EngineRoot) { $EngineRoot = (Resolve-Path (Join-Path $InstalledDir '..\..\..')).Path }

Write-Info "installed build : $InstalledDir"
Write-Info "source engine   : $EngineRoot"

# --- Preflight: is this a real, build-capable Installed Build? ---------------------------------------
if (-not (Test-Path (Join-Path $InstalledDir 'Engine\Build\InstalledBuild.txt'))) {
    Write-Warning "no Engine\Build\InstalledBuild.txt -- '$InstalledDir' may be a source tree, not an Installed Build."
}
$bvfile = Join-Path $InstalledDir 'Engine\Build\Build.version'
if (-not (Test-Path $bvfile)) { throw "no Engine\Build\Build.version under $InstalledDir -- not a usable engine tree." }

# On Windows the archive is build-capable when it carries UnrealBuildTool + the editor (the compilers
# themselves come from the consumer's VS2022, not the engine). Warn if either is absent.
$editorExe = Join-Path $InstalledDir 'Engine\Binaries\Win64\UnrealEditor.exe'
$ubtDir    = Join-Path $InstalledDir 'Engine\Binaries\DotNET\UnrealBuildTool'
$buildCapable = $true
foreach ($chk in @(@{ p = $editorExe; d = 'UnrealEditor.exe' }, @{ p = $ubtDir; d = 'UnrealBuildTool' })) {
    if (-not (Test-Path $chk.p)) {
        $buildCapable = $false
        if ($Strict) { throw "build-capability marker MISSING: $($chk.d) ($($chk.p))." }
        Write-Warning "build-capability marker MISSING: $($chk.d) ($($chk.p)) -- the archive may not build CARLA."
    }
}
if ($buildCapable) { Write-Info "build markers   : UnrealBuildTool + editor present" }

# --- Version / provenance stamp ----------------------------------------------------------------------
$bv = Get-Content -Raw $bvfile | ConvertFrom-Json
$engineVer = "$($bv.MajorVersion).$($bv.MinorVersion).$($bv.PatchVersion)"

$gitHash = Invoke-Git '-C' $EngineRoot 'rev-parse' '--short' 'HEAD'
if (-not $gitHash) { $gitHash = 'unknown' }
$branchRaw = Invoke-Git '-C' $EngineRoot 'rev-parse' '--abbrev-ref' 'HEAD'
if (-not $branchRaw -or $branchRaw -eq 'HEAD') { $branchRaw = 'detached' }
# Sanitize the branch for a filename (slashes/spaces -> '-').
$branch = ($branchRaw -replace '[/ ]', '-') -replace '[^A-Za-z0-9._-]', ''
# A build box dirties the source tree with regenerated binaries; note it in metadata, not the name.
$dirty = if ((Invoke-Git '-C' $EngineRoot 'status' '--porcelain' '--ignore-submodules')) { 'dirty' } else { '' }

$base = if ($Name) { $Name } else { "UnrealEngine-$engineVer-$branch-$gitHash-Win64" }
Write-Info "archive base    : $base"

# --- Resolve output path -----------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$ext = if ($Compress -eq '7z') { '7z' } else { 'zip' }
$archive = Join-Path $OutputDir "$base.$ext"
if (Test-Path $archive) { Remove-Item -Force $archive }

$leaf   = Split-Path $InstalledDir -Leaf     # "Windows"
$parent = Split-Path $InstalledDir -Parent   # ...\LocalBuilds\Engine

# Uncompressed size (informational; enumerates the whole tree).
$sizeBytes = (Get-ChildItem $InstalledDir -Recurse -File -Force -ErrorAction SilentlyContinue |
    Measure-Object -Property Length -Sum).Sum
$sizeGB = [math]::Round(($sizeBytes / 1GB), 2)
Write-Info "packaging $sizeGB GB -> $archive  (compress=$Compress)"

# Locate 7-Zip once (used for 7z, and as a zip fallback).
$sevenZip = $null
foreach ($cand in @('7z.exe',
                    (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
                    (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'))) {
    $c = Get-Command $cand -ErrorAction SilentlyContinue
    if ($c) { $sevenZip = $c.Source; break }
    if (Test-Path $cand) { $sevenZip = $cand; break }
}

# --- Package -----------------------------------------------------------------------------------------
if ($Compress -eq '7z') {
    if (-not $sevenZip) { throw "-Compress 7z requires 7-Zip (7z.exe) on PATH or under Program Files." }
    Write-Info "[engine-dist] using 7-Zip: $sevenZip  (top-level '$leaf\')"
    Push-Location $parent
    try { & $sevenZip a -t7z -mmt=on $archive $leaf | Out-Null; $rc = $LASTEXITCODE }
    finally { Pop-Location }
    if ($rc -ne 0) { throw "7-Zip failed (exit $rc)." }
}
elseif (Get-Command tar.exe -ErrorAction SilentlyContinue) {
    # bsdtar renames the leading path component to <base>\ so extraction yields a self-describing dir.
    # The '^Windows' anchor never matches the tree's relative/absolute symlink targets, so links survive.
    Write-Info "[engine-dist] using tar.exe (libarchive), top-level '$base\'"
    & tar.exe -a -c -f $archive -s "|^$leaf|$base|" -C $parent $leaf
    if ($LASTEXITCODE -ne 0) { throw "tar.exe failed (exit $LASTEXITCODE)." }
}
elseif ($sevenZip) {
    Write-Warning "tar.exe not found; using 7-Zip (top-level '$leaf\' instead of '$base\')."
    Push-Location $parent
    try { & $sevenZip a -tzip -mmt=on $archive $leaf | Out-Null; $rc = $LASTEXITCODE }
    finally { Pop-Location }
    if ($rc -ne 0) { throw "7-Zip failed (exit $rc)." }
}
else {
    Write-Warning "neither tar.exe nor 7-Zip found; using Compress-Archive (slow; top-level '$leaf\')."
    Compress-Archive -Path $InstalledDir -DestinationPath $archive -CompressionLevel Optimal
}
$compGB = [math]::Round((Get-Item $archive).Length / 1GB, 2)
Write-Info "[engine-dist] wrote $compGB GB  ($archive)"

# --- Checksum ----------------------------------------------------------------------------------------
$sha = ''
if (-not $NoChecksum) {
    $sha = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    "$sha  $([System.IO.Path]::GetFileName($archive))" | Set-Content -Path "$archive.sha256" -Encoding ASCII
    Write-Info "[engine-dist] sha256: $sha"
}

# --- Provenance sidecar ------------------------------------------------------------------------------
$meta = @"
archive           : $([System.IO.Path]::GetFileName($archive))
engine_version    : $engineVer
source_branch     : $branch
source_commit     : $gitHash$(if ($dirty) { " ($dirty)" })
build_capable     : $(if ($buildCapable) { 'yes (consumer also needs VS2022)' } else { 'unverified (missing markers)' })
packaged_utc      : $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))
packaged_host     : $env:COMPUTERNAME
uncompressed_size : $sizeGB GB
compressed_size   : $compGB GB
sha256            : $(if ($sha) { $sha } else { '(skipped)' })
"@
Set-Content -Path "$archive.metadata.txt" -Value $meta -Encoding UTF8
Write-Info "[engine-dist] wrote provenance: $([System.IO.Path]::GetFileName($archive)).metadata.txt"

# --- Optional round-trip integrity check -------------------------------------------------------------
if ($VerifyExtract) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("engdist-" + [System.IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        Write-Info "[engine-dist] verify-extract: extracting to $tmp ..."
        if ($Compress -eq '7z') { & $sevenZip x -y "-o$tmp" $archive | Out-Null; $top = $leaf }
        elseif (Get-Command tar.exe -ErrorAction SilentlyContinue) { & tar.exe -x -f $archive -C $tmp; $top = $base }
        else { Expand-Archive -Path $archive -DestinationPath $tmp -Force; $top = $leaf }
        if (-not (Test-Path (Join-Path $tmp "$top\Engine\Build\Build.version"))) {
            throw "verify-extract: Build.version missing after extraction."
        }
        Write-Info "[engine-dist] verify-extract: OK (tree intact)"
    }
    finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}

# --- Summary + next steps ----------------------------------------------------------------------------
Write-Info "[engine-dist] done."
Write-Host ""
Write-Host "  Artifact : $archive"
Write-Host "  Verify build-capability (does the archive actually build CARLA?):"
Write-Host "    tar.exe -x -f `"$archive`" -C C:\Temp"
Write-Host "    `$env:CARLA_UNREAL_ENGINE_PATH = 'C:\Temp\$base'"
Write-Host "    cd <carla checkout>; .\Scripts\Windows\BuildCarla.ps1"
Write-Host "    cmake --build Build --target package-development"
Write-Host "  (the consumer machine must have Visual Studio 2022 + the Windows SDK installed)"
