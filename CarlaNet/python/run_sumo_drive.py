#!/usr/bin/env python3
"""Drive a CARLA world's vehicles from a SUMO microsimulation and record what a camera sees.

The tool is `carla-drive`, installed with the carlacontrol wheel (`carlacontrol.commands.drive`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.drive import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
