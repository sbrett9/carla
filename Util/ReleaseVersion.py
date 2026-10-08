"""The distribution's release version, read from the one place it is set.

The server and both wheels, `carlanet` and `carlacontrol`, carry one release number: `CARLA_VERSION`,
set in the repository's top-level `CMakeLists.txt` as `CARLA_VERSION_MAJOR`, `_MINOR` and `_PATCH`, and
bumped there once per release. Nothing else states it. The wheels' build stamps it into each package
(`setup.py`), the CarlaNet assemblies are given it (`CarlaNet/Directory.Build.props`), and a package
run straight from a checkout reads it from here.

A build that is not a tagged release says which commit made it, as a PEP 440 local version:

* `0.10.0` -- the checkout's HEAD carries the release tag, `0.10.0` or `v0.10.0`, and has no changes;
* `0.10.0+g1a2b3c4d5` -- any other commit, by its short hash;
* `0.10.0+g1a2b3c4d5.dirty` -- that commit with changes to tracked files on top, so the build is not
  exactly any commit (`0.10.0+dirty` on the tagged release);
* `0.10.0+unknown` -- git could not be asked, so whether this is the release cannot be said.

CMake is not involved: the number is read from `CMakeLists.txt` as text, so a checkout that was never
configured gives the same answer. A tree with no `CMakeLists.txt` -- an unpacked source distribution --
has no number here, and the caller falls back to the one stamped into the package, or fails loudly.

Run as a script, it prints the version of the checkout it sits in, or of `--root`.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

# The three lines of the top-level CMakeLists.txt that set the release.
PART_PATTERN = r"set\s*\(\s*CARLA_VERSION_{name}\s+(\d+)\s*\)"
PARTS = ("MAJOR", "MINOR", "PATCH")

# The short commit's length: the same commit named the same way by every wheel and every assembly.
SHORT_HASH_LENGTH = 9

# The local part of a build whose commit git could not name.
UNKNOWN_COMMIT = "unknown"


class ReleaseVersionError(RuntimeError):
    """The release version cannot be read from what is there."""


class ReleaseVersion:
    """Reads the release version a checkout sets, and names a build that is not the release."""

    @staticmethod
    def read_carla_version(cmakelists: Path) -> str:
        """`MAJOR.MINOR.PATCH` as the top-level `CMakeLists.txt` sets it.

        Raises:
            ReleaseVersionError: the file is missing, or does not set all three parts.
        """
        cmakelists = Path(cmakelists)
        try:
            text = cmakelists.read_text(encoding="utf-8")
        except OSError as unreadable:
            raise ReleaseVersionError(f"cannot read {cmakelists}: {unreadable}") from unreadable
        parts = []
        for name in PARTS:
            found = re.search(PART_PATTERN.format(name=name), text)
            if found is None:
                raise ReleaseVersionError(
                    f"{cmakelists} does not set CARLA_VERSION_{name}; it is not CARLA's top-level "
                    "CMakeLists.txt, which sets the release version")
            parts.append(str(int(found.group(1))))
        return ".".join(parts)

    @staticmethod
    def is_checkout_root(root: Path) -> bool:
        """Whether `root` holds CARLA's top-level `CMakeLists.txt`."""
        cmakelists = Path(root) / "CMakeLists.txt"
        if not cmakelists.is_file():
            return False
        try:
            return "CARLA_VERSION_MAJOR" in cmakelists.read_text(encoding="utf-8")
        except OSError:
            return False

    @staticmethod
    def git(root: Path, *arguments: str) -> str | None:
        """git's answer in `root`, or None where git is missing or refuses."""
        try:
            done = subprocess.run(["git", *arguments], cwd=str(root), capture_output=True, text=True,
                                  timeout=30, check=False)
        except (OSError, subprocess.SubprocessError):
            return None
        if done.returncode != 0:
            return None
        return done.stdout.strip()

    @classmethod
    def local_part(cls, root: Path, release: str) -> str | None:
        """The PEP 440 local part of the checkout at `root`: None for the clean, tagged release."""
        commit = cls.git(root, "rev-parse", f"--short={SHORT_HASH_LENGTH}", "HEAD")
        if not commit:
            return UNKNOWN_COMMIT
        changed = cls.git(root, "status", "--porcelain", "--untracked-files=no")
        # A tree whose state git would not report is not known to be clean.
        dirty = changed is None or changed != ""
        tags = cls.git(root, "tag", "--points-at", "HEAD") or ""
        tagged = bool({release, f"v{release}"} & set(tags.split()))
        if tagged:
            return "dirty" if dirty else None
        return f"g{commit}.dirty" if dirty else f"g{commit}"

    @classmethod
    def of_checkout(cls, root: Path) -> str:
        """The PEP 440 version of the checkout at `root`: the release, with a local part unless HEAD
        is the clean, tagged release.

        Raises:
            ReleaseVersionError: `root` holds no CARLA `CMakeLists.txt` that sets the release.
        """
        root = Path(root)
        release = cls.read_carla_version(root / "CMakeLists.txt")
        local = cls.local_part(root, release)
        return release if local is None else f"{release}+{local}"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1],
                        help="the checkout to read (default: the one this script is in)")
    parser.add_argument("--release-only", action="store_true",
                        help="print MAJOR.MINOR.PATCH alone, without the local part")
    arguments = parser.parse_args(argv)
    try:
        if arguments.release_only:
            print(ReleaseVersion.read_carla_version(arguments.root / "CMakeLists.txt"))
        else:
            print(ReleaseVersion.of_checkout(arguments.root))
    except ReleaseVersionError as failed:
        print(f"ReleaseVersion: {failed}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
