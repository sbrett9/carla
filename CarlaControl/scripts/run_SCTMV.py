#!/usr/bin/env python3
"""SCTMV -- Single Client Traffic Manager & Viewer: build a world, then fly, drive and record in it.

The tool is `carla-sctmv`, installed with the carlacontrol wheel (`carlacontrol.commands.sctmv`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import importlib.util
import sys
from pathlib import Path

_SOURCES = Path(__file__).resolve().parents[2] / "CarlaControl" / "src"

# The staged build's netconvert, PROJ data and SUMO go on the environment before carlanet starts the
# .NET runtime, which on Linux reads the environment once, when it starts; importing carlacontrol
# starts it. ToolLayout imports nothing from its package, so it is loaded on its own to apply the
# same defaults the command applies again once it runs.
_spec = importlib.util.spec_from_file_location("tool_layout", _SOURCES / "carlacontrol" / "ToolLayout.py")
_layout = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_layout)
_layout.ToolLayout(_SOURCES.parents[1]).apply_sumo_environment()

sys.path.insert(0, str(_SOURCES))

from carlacontrol.commands.sctmv import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
