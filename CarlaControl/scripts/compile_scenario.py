#!/usr/bin/env python3
"""Compile a scenario specification against its world package into a scenario package.

The tool is `carla-compile-scenario`, installed with the carlacontrol wheel (`carlacontrol.commands.compile_scenario`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.compile_scenario import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
