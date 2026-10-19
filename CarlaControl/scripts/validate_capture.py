#!/usr/bin/env python3
"""Check every file of a capture folder, a world package or a vehicle catalogue against its published
schema.

The tool is `carla-validate`, installed with the carlacontrol wheel (`carlacontrol.commands.validate`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed copy, so
it checks against the checkout's schemas. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.validate import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
