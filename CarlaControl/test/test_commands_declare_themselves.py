"""Every command whose run writes through CarlaNet names itself in the record of what made each file.

The CarlaNet writers -- the stills and their sidecars, the world truth track, the run manifest, the
world package -- record the tool the process declared (`carlacontrol.ProducerRecord.declare_tool`).
A command that drives them declares its own name, the console command pyproject.toml installs, and
carlacontrol's release, so none of its files records `tool_version: null`. Checked for each such
command, through its entry point as an installed command runs it, with the CarlaNet producer stood in
for: `main()` declares the command's name before it reaches a server, so every file the run goes on
to write carries it. The stand-in stops the run at the declaration, so nothing after it runs here.

The commands that write nothing through CarlaNet are not here: `carla-free-camera` and
`carla-camera-follower` record nothing, `carla-cot-telemetry` and `carla-compile-scenario` write
their files in Python under their component's own name, and `carla-capture`'s session declares
`carlacontrol.CaptureSession`.
"""
from __future__ import annotations

import importlib.metadata
import sys
import tomllib
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ProducerRecord import ProducerRecord  # noqa: E402
from carlacontrol.version import __version__  # noqa: E402

PYPROJECT = _REPO / "CarlaControl" / "pyproject.toml"

# Each command whose run writes through CarlaNet, and arguments it would accept.
WRITERS = {
    "carla-drive": ["--scenario", "s.sumocfg", "--world-package", "w.cwp"],
    "carla-sctmv": [],
    "carla-build-world": [],
}


class _DeclarationReachedError(Exception):
    """Raised where the command declares itself, so nothing after the declaration runs."""


class _Producer:
    """CarlaNet's `Producer`, as far as a declaration reaches it."""

    declared: list[tuple[str, str | None]] = []

    @classmethod
    def DeclareTool(cls, tool: str, version: str | None) -> None:  # noqa: N802 -- the .NET member name
        cls.declared.append((tool, version))
        raise _DeclarationReachedError


def _no_server(*_arguments):
    raise AssertionError("the command reached a server before it declared itself")


@pytest.fixture
def commands_load():
    """The commands import carlanet, so they need its assemblies."""
    pytest.importorskip("carlanet", reason="the commands need carlanet and its assemblies")


@pytest.mark.parametrize("name", sorted(WRITERS))
def test_a_command_that_writes_through_carlanet_declares_its_own_name_first(name, commands_load,
                                                                           monkeypatch):
    target = tomllib.loads(PYPROJECT.read_text(encoding="utf-8"))["project"]["scripts"][name]
    main = importlib.metadata.EntryPoint(name, target, "console_scripts").load()
    module = sys.modules[main.__module__]
    _Producer.declared = []
    monkeypatch.setattr(ProducerRecord, "_carlanet_producer", staticmethod(lambda: _Producer))
    monkeypatch.setattr(module, "carla", SimpleNamespace(Client=_no_server))
    monkeypatch.setattr(sys, "argv", [name, *WRITERS[name]])

    with pytest.raises(_DeclarationReachedError):
        main(WRITERS[name])

    assert _Producer.declared == [(name, __version__)]
    assert module.TOOL == name
