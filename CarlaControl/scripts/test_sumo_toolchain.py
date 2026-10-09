#!/usr/bin/env python3
"""Check which SUMO a tool on this machine resolves, and that the staged toolchain is complete.

The tool is `carla-check-sumo`, installed with the carlacontrol wheel (`carlacontrol.commands.check_sumo`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.check_sumo import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
