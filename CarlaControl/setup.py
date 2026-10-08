"""Stamps the distribution's release version into the carlacontrol wheel, and requires its carlanet.

Everything else about the package is in pyproject.toml. The version is not: it is the release number
the top-level CMakeLists.txt sets (`Util/ReleaseVersion.py`), with the short CARLA commit as a PEP 440
local part on a build that is not the tagged release, so carlacontrol, carlanet and the server carry
one number. It is written into the built package as `carlacontrol/_version.py`, never into the source
tree, so a checkout never holds a stale one; `carlacontrol.version` reads it.

The dependencies are not in pyproject.toml either, because one of them is that same number:
carlacontrol requires the carlanet of its own release, `carlanet==0.10.0` for any build of 0.10.0. The
requirement has no local part, so under PEP 440 it matches every build of that release --
`0.10.0`, `0.10.0+g1a2b3c4d5`, `0.10.0+g1a2b3c4d5.dirty` -- and no other release.

Built from a checkout, the number is read from CMakeLists.txt; CMake itself is not needed. Built from a
source distribution, which carries the `_version.py` stamped when it was made, that is read instead.
With neither, the build fails and says why: a wheel with no release number is one nothing could trace.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path

from setuptools import setup
from setuptools.command.build_py import build_py
from setuptools.command.sdist import sdist

HERE = Path(__file__).resolve().parent
PACKAGE = "carlacontrol"
PACKAGE_DIR = HERE / "src" / PACKAGE
# CarlaControl/setup.py: the checkout's root is one directory up.
CHECKOUT = HERE.parent
STAMP = "_version.py"


def _release_version_module():
    """`Util/ReleaseVersion.py` of the checkout this file sits in, or None outside one."""
    path = CHECKOUT / "Util" / "ReleaseVersion.py"
    if not path.is_file():
        return None
    spec = importlib.util.spec_from_file_location("carla_release_version", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _resolve() -> tuple[str, str]:
    """The version, and where it was read from."""
    release = _release_version_module()
    if release is not None and release.ReleaseVersion.is_checkout_root(CHECKOUT):
        return release.ReleaseVersion.of_checkout(CHECKOUT), "CMakeLists.txt and git"
    stamped = PACKAGE_DIR / STAMP
    if stamped.is_file():
        for line in stamped.read_text(encoding="utf-8").splitlines():
            if line.startswith("__version__"):
                return line.split("=", 1)[1].strip().strip("'\""), "the stamped _version.py"
    raise SystemExit(
        f"cannot tell which release this {PACKAGE} is: {CHECKOUT} holds no CARLA CMakeLists.txt, "
        f"and {stamped} was never stamped. Build the wheel from a CARLA checkout.")


VERSION, SOURCE = _resolve()
# The release alone, MAJOR.MINOR.PATCH, without the commit.
RELEASE = VERSION.split("+", 1)[0]

DEPENDENCIES = [
    # The carlanet of this release, whichever commit built it.
    f"carlanet=={RELEASE}",
    # The scenario compiler and the SUMO vehicle-type writer parse and write XML with it.
    "lxml>=5.0",
    "numpy>=1.24.0",
    # The pygame API, from the maintained pygame-ce distribution: it publishes wheels for every
    # Python this package supports, where the original pygame has none past 3.13. Both install the
    # same `pygame` module, so an environment holds one or the other, never both.
    "pygame-ce>=2.5",
]


def _stamp(directory: Path) -> None:
    from textwrap import dedent

    directory.mkdir(parents=True, exist_ok=True)
    (directory / STAMP).write_text(dedent(f'''\
        """The release version this package was built as, stamped by its build. Not edited by hand."""

        __version__ = {VERSION!r}
        # Read from {SOURCE}.
        '''), encoding="utf-8", newline="\n")


class StampedBuildPy(build_py):
    """Writes `_version.py` into the built package, beside the copied sources."""

    def run(self) -> None:
        super().run()
        _stamp(Path(self.build_lib) / PACKAGE)


class StampedSdist(sdist):
    """Writes `_version.py` into a source distribution, which carries no CMakeLists.txt."""

    def make_release_tree(self, base_dir, files) -> None:
        super().make_release_tree(base_dir, files)
        _stamp(Path(base_dir) / "src" / PACKAGE)


setup(version=VERSION, install_requires=DEPENDENCIES,
      cmdclass={"build_py": StampedBuildPy, "sdist": StampedSdist})
