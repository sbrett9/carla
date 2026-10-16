#!/usr/bin/env python3
"""Fail if two runs of one scenario name different supervision rows in their run manifests.

The tool is `carla-diff-manifests`, installed with the carlacontrol wheel (`carlacontrol.commands.diff_manifests`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.diff_manifests import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
