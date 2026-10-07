#!/usr/bin/env python3
"""Emit Cursor-on-Target telemetry for every vehicle in a SUMO scenario.

The tool is `carla-cot-telemetry`, installed with the carlacontrol wheel (`carlacontrol.commands.cot_telemetry`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.cot_telemetry import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
