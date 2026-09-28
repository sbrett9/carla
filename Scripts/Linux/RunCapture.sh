#!/usr/bin/env bash
#
# RunCapture.sh — Linux equivalent of Scripts/Windows/RunCapture.ps1
#
# Capture a window of a compiled SUMO scenario in a running CARLA world: the source-tree launcher for
# CarlaControl/scripts/run_capture.py.
#
# Runs run_capture with every argument passed through unchanged -- --result included -- and exits with
# its exit status, which run_capture reads from the run result it writes. The run configuration, its
# fields, their defaults and the exit statuses are run_capture's own; --help prints its help, generated
# from the run configuration's schema, so this launcher and RunCapture.ps1 (Windows) cannot describe
# different options.
#
# The world must already be running and loaded (RunCarlaServer.sh, then the world built and loaded).
# The scenario must be compiled (compile_scenario.py): a capture binds a compiled scenario and its
# world package, and never compiles a scenario or builds a world.
#
# The launcher replaces itself with run_capture (exec), so a signal sent to it reaches run_capture
# directly: SIGINT or SIGTERM stops a run cleanly, and a second one abandons the shutdown.
#
# Paths are derived from this script's location (it lives at carla/Scripts/Linux/, so the CARLA
# repository root is two directories up).

set -uo pipefail

script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"
carla_root="$(cd "$script_dir/../.." && pwd)"
run_capture="$carla_root/CarlaControl/scripts/run_capture.py"
python_exe=""

if [[ -t 1 ]]; then
    green=$'\033[32m'; yellow=$'\033[33m'; red=$'\033[31m'; reset=$'\033[0m'
else
    green=""; yellow=""; red=""; reset=""
fi
info() { printf '%s%s%s\n' "$green" "$*" "$reset"; }
warn() { printf '%s%s%s\n' "$yellow" "$*" "$reset" >&2; }
fail() { printf '%s%s%s\n' "$red" "$*" "$reset" >&2; }

usage() {
    cat <<'EOF'
Usage: RunCapture.sh [--python-exe <path>] [run_capture arguments...]

Capture a window of a compiled SUMO scenario in a running CARLA world. Every argument but
--python-exe is passed to run_capture unchanged; its help follows.

Launcher option:
  --python-exe <path>   The Python interpreter to run run_capture with (-PythonExe in the Windows
                        launcher). Default: the platform's Python 3 -- python3, then python, on
                        Linux; python, then the py launcher, on Windows.

EOF
}

# --python-exe is this launcher's own option; every other argument is run_capture's, unchanged.
passed=()
show_help=0
while (($#)); do
    case "$1" in
        --python-exe=*) python_exe="${1#*=}" ;;
        --python-exe)
            if (($# < 2)); then fail "--python-exe needs a path"; exit 1; fi
            python_exe="$2"; shift ;;
        -h|--help|-Help) show_help=1 ;;
        *) passed+=("$1") ;;
    esac
    shift
done

if [[ ! -f "$run_capture" ]]; then
    fail "run_capture.py not found at $run_capture"
    exit 1
fi

if [[ -z "$python_exe" ]]; then
    if command -v python3 >/dev/null 2>&1; then
        python_exe="python3"
    elif command -v python >/dev/null 2>&1; then
        python_exe="python"
    else
        fail "no Python found: install Python 3.11 or later with the carlanet wheel, or pass --python-exe"
        exit 1
    fi
fi

if ((show_help)); then
    usage
    exec "$python_exe" "$run_capture" --help
fi

info "run_capture: $python_exe $run_capture"
exec "$python_exe" "$run_capture" "${passed[@]+"${passed[@]}"}"
