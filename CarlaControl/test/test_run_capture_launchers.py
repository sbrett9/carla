"""The Windows and Linux capture launchers describe the same options and return the same status.

Plan 12 §10, D12.19: the launchers ship together, their `--help` comes from the schema, and a check
compares the two -- the Windows distribution once shipped a launcher for a script that had been deleted,
and nothing noticed because nothing compared them. So both launchers are run here: with `--help`, whose
option sets must be equal and must name every field; with a configuration the offline checks refuse, whose exit
status must be 2 from both and equal to the status in the result each wrote at the `--result` path it
was passed; and with a malformed override, which must be 1 from both.

The interpreter is pinned with `--python-exe` so the two run the same Python. Skipped where PowerShell
7 or bash is not installed.
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from RunCaptureFixture import Layout, run_document  # noqa: E402

from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402

WINDOWS = _REPO / "Scripts" / "Windows" / "RunCapture.ps1"
LINUX = _REPO / "Scripts" / "Linux" / "RunCapture.sh"
PWSH = shutil.which("pwsh")
BASH = shutil.which("bash")
PYTHON = Path(sys.executable).as_posix()

pytestmark = pytest.mark.skipif(PWSH is None or BASH is None,
                                reason="needs PowerShell 7 (pwsh) and bash to run both launchers")


def launch(launcher: Path, *arguments: str) -> subprocess.CompletedProcess:
    if launcher.suffix == ".ps1":
        command = [PWSH, "-NoProfile", "-NonInteractive", "-File", str(launcher)]
    else:
        command = [BASH, launcher.as_posix()]
    return subprocess.run([*command, "--python-exe", PYTHON, *arguments], capture_output=True,
                          text=True, timeout=300, encoding="utf-8", errors="replace")


def options(text: str) -> set[str]:
    return set(re.findall(r"(?<![\w-])--[a-z][a-z-]*", text))


@pytest.fixture(scope="module")
def helps() -> dict[str, str]:
    return {launcher.suffix: launch(launcher, "--help").stdout for launcher in (WINDOWS, LINUX)}


def test_both_launchers_offer_the_same_options(helps):
    assert options(helps[".ps1"]) == options(helps[".sh"])


def test_the_help_names_every_field_and_alias(helps):
    for text in helps.values():
        for path, spec in RunConfiguration.FIELDS.items():
            assert path in text, path
            if spec.alias:
                assert spec.alias in text, spec.alias


def write_run(layout: Layout, overrides: dict) -> Path:
    document = run_document()
    document["capture"].update(overrides)
    path = layout.root / "launcher.run.json"
    path.write_text(json.dumps(document), encoding="utf-8")
    return path


@pytest.mark.parametrize("launcher", [WINDOWS, LINUX], ids=["windows", "linux"])
def test_an_offline_refusal_returns_2_and_the_result_says_so(tmp_path, launcher):
    layout = Layout(tmp_path)
    run_path = write_run(layout, {"world_delta_s": 0.03})
    result = tmp_path / "out" / "refused.result.json"
    completed = launch(launcher, "--run", str(run_path), "--site-profile",
                       str(layout.write_profile()), "--result", str(result))
    assert completed.returncode == 2, completed.stdout + completed.stderr
    written = json.loads(result.read_text(encoding="utf-8"))
    assert (written["outcome"], written["exit_status"]) == ("refused_offline", 2)


@pytest.mark.parametrize("launcher", [WINDOWS, LINUX], ids=["windows", "linux"])
def test_a_malformed_override_returns_1_from_both(tmp_path, launcher):
    layout = Layout(tmp_path)
    run_path = write_run(layout, {})
    result = tmp_path / "out" / "usage.result.json"
    completed = launch(launcher, "--run", str(run_path), "--site-profile",
                       str(layout.write_profile()), "--result", str(result),
                       "--set", "capture.prewarm_ss=3")
    assert completed.returncode == 1, completed.stdout + completed.stderr
    assert json.loads(result.read_text(encoding="utf-8"))["outcome"] == "usage_error"
