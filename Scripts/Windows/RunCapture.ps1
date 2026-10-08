#Requires -Version 5.1
<#
.SYNOPSIS
    Capture a window of a compiled SUMO scenario in a running CARLA world: the source-tree launcher
    for CarlaControl/scripts/run_capture.py.

.DESCRIPTION
    Runs run_capture with every argument passed through unchanged -- --result included -- and exits
    with its exit status, which run_capture reads from the run result it writes. The run
    configuration, its fields, their defaults and the exit statuses are run_capture's own; -Help
    prints its help, generated from the run configuration's schema, so this launcher and
    RunCapture.sh (Linux) cannot describe different options.

    The world must already be running and loaded (RunCarlaServer.ps1, then the world built and
    loaded). The scenario must be compiled (compile_scenario.py): a capture binds a compiled scenario
    and its world package, and never compiles a scenario or builds a world.

    To stop a run cleanly press Ctrl+C, or send CTRL_BREAK_EVENT to its process group from another
    process; a second one abandons the shutdown. taskkill without /F delivers nothing a Python process
    can catch.

.PARAMETER PythonExe
    The Python interpreter to run run_capture with (--python-exe <path> in the Linux launcher's
    spelling is accepted too). Default: python on PATH, then the py launcher.
.PARAMETER Help
    Print run_capture's help and exit.

.EXAMPLE
    .\RunCapture.ps1 --run Import\Arapahoe_I25_SupervisionCheck.run.json --validate-only
.EXAMPLE
    .\RunCapture.ps1 --run Import\Arapahoe_I25_SupervisionCheck.run.json --caller unattended --result out\arapahoe.result.json
.EXAMPLE
    .\RunCapture.ps1 -Help
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$PythonExe,

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Info { param([string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-Fail { param([string]$Message) Write-Host $Message -ForegroundColor Red }

# The CARLA repository root is two directories up from this script (carla/Scripts/Windows).
$CarlaRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$RunCapture = Join-Path $CarlaRoot "CarlaControl\scripts\run_capture.py"
if (-not (Test-Path $RunCapture)) {
    Write-Fail "run_capture.py not found at $RunCapture"
    exit 1
}

# --python-exe is this launcher's own option; every other argument is run_capture's, unchanged.
$Passed = @()
if ($null -ne $Arguments) {
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $Argument = $Arguments[$i]
        if ($Argument -match '^--python-exe=(.+)$') { $PythonExe = $matches[1]; continue }
        if ($Argument -eq '--python-exe') {
            if ($i + 1 -ge $Arguments.Count) { Write-Fail "--python-exe needs a path"; exit 1 }
            $PythonExe = $Arguments[$i + 1]
            $i++
            continue
        }
        $Passed += $Argument
    }
}
$ShowHelp = $Help -or ($Passed -contains '--help') -or ($Passed -contains '-h') -or ($Passed -contains '-Help')
if ($ShowHelp) { $Passed = @('--help') }

$InterpreterArgs = @()
if ($PythonExe) {
    $Interpreter = $PythonExe
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
    # Not python3: on Windows that name is usually the Microsoft Store's installer stub.
    $Interpreter = 'python'
} elseif (Get-Command py -ErrorAction SilentlyContinue) {
    $Interpreter = 'py'; $InterpreterArgs = @('-3')
} else {
    Write-Fail "no Python found: install Python 3.11 or later with the carlanet wheel, or pass -PythonExe"
    exit 1
}

if ($ShowHelp) {
    @'
Usage: RunCapture.ps1 [--python-exe <path>] [run_capture arguments...]

Capture a window of a compiled SUMO scenario in a running CARLA world. Every argument but
--python-exe is passed to run_capture unchanged; its help follows.

Launcher option:
  --python-exe <path>   The Python interpreter to run run_capture with (-PythonExe in the Windows
                        launcher). Default: the platform's Python 3 -- python3, then python, on
                        Linux; python, then the py launcher, on Windows.

'@ | Write-Host
} else {
    Write-Info "run_capture: $Interpreter $RunCapture"
}
# run_capture's exit status is the launcher's, unchanged: it is read from the run result.
& $Interpreter @InterpreterArgs $RunCapture @Passed
$Status = $LASTEXITCODE
if ($null -eq $Status) { $Status = 1 }
exit $Status
