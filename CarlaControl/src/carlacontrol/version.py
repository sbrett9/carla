"""The release version of CarlaControl: the distribution's one release number.

carlacontrol, carlanet and the server carry the same number, `CARLA_VERSION` in the top-level
`CMakeLists.txt`, with the short CARLA commit as a PEP 440 local part on a build that is not the tagged
release (`0.10.0+g1a2b3c4d5`). An installed wheel reads the number its build stamped into
`carlacontrol/_version.py` (`CarlaControl/setup.py`). A package run from a checkout, which holds no
stamp, reads the checkout it sits in (`Util/ReleaseVersion.py`), so the number is never a stale
stamp's. Outside both, it is `unknown`, never a guess.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path

try:  # the stamp exists only in a built package (CarlaControl/setup.py)
    from carlacontrol._version import __version__ as _stamped
except ImportError:
    _stamped = None

UNKNOWN = "unknown"


def _from_checkout() -> str:
    """The version of the checkout this file sits in, or `unknown` outside one."""
    # CarlaControl/src/carlacontrol/version.py: the checkout's root is three directories up.
    checkout = Path(__file__).resolve().parents[3]
    path = checkout / "Util" / "ReleaseVersion.py"
    if not path.is_file():
        return UNKNOWN
    try:
        spec = importlib.util.spec_from_file_location("carla_release_version", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        if not module.ReleaseVersion.is_checkout_root(checkout):
            return UNKNOWN
        return module.ReleaseVersion.of_checkout(checkout)
    except Exception:  # noqa: BLE001 -- a version that cannot be read is unknown, never a failed import
        return UNKNOWN


__version__ = _stamped or _from_checkout()

# The release alone, MAJOR.MINOR.PATCH, without the commit: for text inside files that two builds of
# one release must write alike -- a compiled route file's header -- where the commit would change the
# file's digest at every commit while the traffic stays the same.
RELEASE = __version__.split("+", 1)[0]
