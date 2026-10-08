#!/usr/bin/env python3
"""Fail if a SUMO drive's truth sidecars list parked bodies as vehicles or lose track of who is who.

The tool is `carla-audit-sidecars`, installed with the carlacontrol wheel (`carlacontrol.commands.audit_sidecars`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.audit_sidecars import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
