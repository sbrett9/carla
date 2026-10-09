#!/usr/bin/env python3
"""Publish a world's authoring reference set into an existing world package, without rebuilding it.

The tool is `carla-publish-reference-set`, installed with the carlacontrol wheel (`carlacontrol.commands.publish_reference_set`). This
script runs the same tool from this checkout, with the checkout's sources ahead of any installed
copy, so its defaults are the repository's. `--help` describes it.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "CarlaControl" / "src"))

from carlacontrol.commands.publish_reference_set import main  # noqa: E402  (needs the path above)

if __name__ == "__main__":
    sys.exit(main())
