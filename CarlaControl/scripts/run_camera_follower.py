#!/usr/bin/env python3
"""Watch a running CARLA world through one camera of your own, live, without driving anything.

The tool is `carla-camera-follower`, installed with the carlacontrol wheel (`carlacontrol.commands.camera_follower`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.camera_follower import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
