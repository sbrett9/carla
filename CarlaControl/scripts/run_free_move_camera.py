#!/usr/bin/env python3
"""Fly a camera of your own around a running CARLA world, live, without changing anything in it.

The tool is `carla-free-camera`, installed with the carlacontrol wheel (`carlacontrol.commands.free_camera`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.free_camera import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
