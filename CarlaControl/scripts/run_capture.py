#!/usr/bin/env python3
"""Capture a window of a compiled SUMO scenario in a generated CARLA world.

The tool is `carla-capture`, installed with the carlacontrol wheel (`carlacontrol.commands.capture`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.capture import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
