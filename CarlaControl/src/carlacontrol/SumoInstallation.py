"""Locate a SUMO installation and the executables and Python tools inside it.

SUMO is not a Python package: its command-line tools, its `traci`/`sumolib` modules and its PROJ data
all live inside an installation directory that SUMO's own convention names with the `SUMO_HOME`
environment variable. This resolves that directory once so callers do not each hard-code a path, and
looks in three places in order:

  1. a path given explicitly by the caller,
  2. `SUMO_HOME`, which is what a SUMO install sets and what `traci` itself checks,
  3. any additional candidate directories the caller offers (a source build inside a repository,
     say), and finally the executable search path.

That order is deliberate and is not changed by the reporting below: matching what raw `traci` resolves
on the same machine is worth more than always preferring a repository's own build, because tooling that
disagrees with `traci` about which SUMO is in use is a worse kind of silent divergence than the one
being reported.

**Every resolution is announced.** More than one SUMO can be installed on a machine, and the
executables give no hint of which one a tool picked. A repository-pinned build converting a map while
an unrelated system-wide install authors the scenarios against it is a real, observed failure: it
produces a network and a scenario that are not from the same converter, and nothing says so. So
`locate` reports the path, the version and *which rule matched* at `INFO`, once per distinct
resolution, with no flag to turn it off, and `require_version` turns a known mismatch against a
recorded converter into a refusal rather than a note in a log nobody reads.
"""
from __future__ import annotations

import logging
import os
import re
import shutil
import subprocess
import sys
from collections.abc import Iterable
from dataclasses import dataclass
from functools import cached_property
from pathlib import Path

EXECUTABLE_SUFFIX = ".exe" if os.name == "nt" else ""

# `Eclipse SUMO netconvert 1.27.0` / `Eclipse SUMO sumo 1.27.1` — the first line of any SUMO tool's
# `--version` output. The trailing dot-separated number is the release; anything after it (a git
# description on a development build, for example) is not part of the comparison.
_VERSION_LINE = re.compile(r"Eclipse SUMO \S+ v?(\d+(?:\.\d+)*)")

# Resolutions already announced this process, keyed by the path and the rule that matched, so a
# long-running tool does not repeat the line while a *second*, different resolution is still reported
# rather than swallowed.
_ANNOUNCED: set[tuple[str, str]] = set()

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class SumoInstallation:
    """One SUMO installation on this machine, and how it came to be the one in use."""

    home: Path

    #: Which rule in `locate` matched: `explicit`, `SUMO_HOME`, `candidate` or `PATH`. Knowing the
    #: path alone says what resolved but not why, which is the question asked when it is the wrong one.
    source: str = "explicit"

    @classmethod
    def locate(cls, explicit: str | Path | None = None,
               extra_candidates: Iterable[str | Path] = ()) -> SumoInstallation:
        """Find a usable installation, or raise saying where it looked.

        The resolved path, version and matching rule are logged at `INFO` before returning.
        """
        searched: list[Path] = []
        rules: list[tuple[str, str | Path | None]] = [("explicit", explicit),
                                                      ("SUMO_HOME", os.environ.get("SUMO_HOME"))]
        rules += [("candidate", candidate) for candidate in extra_candidates]
        for source, candidate in rules:
            if not candidate:
                continue
            home = Path(candidate)
            searched.append(home)
            if cls._is_installation(home):
                return cls(home, source)._announce()

        # Nothing declared one, so fall back to a `sumo` on the executable search path and take
        # the directory above it as the installation.
        found = shutil.which("sumo") or shutil.which("netconvert")
        if found:
            home = Path(found).resolve().parent.parent
            if cls._is_installation(home):
                return cls(home, "PATH")._announce()

        raise FileNotFoundError(
            "no SUMO installation found. Set SUMO_HOME to the directory holding SUMO's bin/ and "
            "tools/ (for example C:\\Program Files (x86)\\Eclipse\\Sumo), put SUMO's bin on PATH, "
            "or pass one explicitly. Looked in: "
            + (", ".join(str(p) for p in searched) or "nothing was given"))

    @staticmethod
    def _is_installation(home: Path) -> bool:
        """A directory counts when it holds at least one SUMO executable."""
        return any((home / "bin" / f"{name}{EXECUTABLE_SUFFIX}").exists()
                   for name in ("sumo", "netconvert"))

    def _announce(self) -> SumoInstallation:
        """Report this resolution once, then return self so `locate` can return it inline."""
        key = (str(self.home), self.source)
        if key in _ANNOUNCED:
            return self
        _ANNOUNCED.add(key)
        message = (f"SUMO installation: {self.home} (version {self.version or 'unknown'}, "
                   f"matched by {self.source})")
        logger.info(message)
        # This line exists so that nobody has to already suspect a mismatch to find out about one, so
        # it must survive a caller that never configured logging at all.
        if not logger.isEnabledFor(logging.INFO):
            print(message, file=sys.stderr)
        return self

    @cached_property
    def version(self) -> str | None:
        """The SUMO release of this installation, e.g. `1.27.0`, or None when it cannot be read.

        Read from `netconvert`, not `sumo`: `_is_installation` deliberately accepts a directory
        holding only `netconvert` (the converter is all some callers need), so asking `sumo` would
        report nothing for an installation that is perfectly usable.
        """
        netconvert = self.home / "bin" / f"netconvert{EXECUTABLE_SUFFIX}"
        if not netconvert.exists():
            return None
        try:
            completed = subprocess.run([str(netconvert), "--version"], capture_output=True,
                                       text=True, timeout=30, check=False)
        except OSError as error:
            logger.warning("could not run %s to read its version: %s", netconvert, error)
            return None
        match = _VERSION_LINE.search(completed.stdout or completed.stderr or "")
        if not match:
            logger.warning("could not parse a version from %s --version", netconvert)
            return None
        return match.group(1)

    def require_version(self, expected: str | None, allow_mismatch: bool = False) -> None:
        """Refuse when this installation is not the SUMO release `expected`.

        `expected` is the version recorded by whatever produced the artifact being worked on — a
        world package records the netconvert that built it. Pass None when nothing recorded one: that
        warns and proceeds, because an older artifact that predates the recording is not evidence of a
        mismatch. `allow_mismatch` warns and proceeds for a caller that has a reason to accept the
        risk, and is what a `--allow-version-mismatch` flag sets.
        """
        if expected is None:
            logger.warning(
                "nothing records which SUMO produced the artifact being worked on, so the "
                "installation at %s (version %s) cannot be checked against it. Proceeding; rebuild "
                "the world to record its converter.", self.home, self.version or "unknown")
            return

        resolved = self.version
        if resolved == expected:
            return

        detail = (f"this world was built with SUMO {expected}; the installation at {self.home} "
                  f"(matched by {self.source}) is "
                  + (f"SUMO {resolved}" if resolved else "of an unreadable version"))
        if allow_mismatch:
            logger.warning("%s. Proceeding because the version mismatch was explicitly allowed.",
                           detail)
            return
        raise RuntimeError(
            f"{detail}. Two SUMO releases are not guaranteed to convert the same OSM into the same "
            "network. Pass --sumo-home to point at a matching installation, rebuild the world with "
            "this one, or pass --allow-version-mismatch to accept the risk.")

    def executable(self, name: str) -> Path:
        path = self.home / "bin" / f"{name}{EXECUTABLE_SUFFIX}"
        if not path.exists():
            raise FileNotFoundError(f"{name} is missing from {self.home / 'bin'}")
        return path

    @property
    def sumo(self) -> Path:
        return self.executable("sumo")

    @property
    def sumo_gui(self) -> Path:
        return self.executable("sumo-gui")

    @property
    def netconvert(self) -> Path:
        return self.executable("netconvert")

    @property
    def duarouter(self) -> Path:
        """The route validator, which scenario compilation runs on every authored route."""
        return self.executable("duarouter")

    @property
    def tools(self) -> Path:
        """The directory holding `traci` and `sumolib`."""
        return self.home / "tools"

    @property
    def proj_data(self) -> Path | None:
        """PROJ's data directory, which netconvert needs to reproject. None when absent."""
        share = self.home / "share" / "proj"
        return share if (share / "proj.db").exists() else None

    def add_tools_to_path(self) -> None:
        """Make `traci` and `sumolib` importable from this installation."""
        tools = str(self.tools)
        if tools not in sys.path:
            sys.path.insert(0, tools)

    def import_traci(self):
        """Import `traci` from this installation, reporting it when it comes from somewhere else."""
        self.add_tools_to_path()
        try:
            import traci
        except ImportError as error:
            raise ImportError(
                f"traci was not found under {self.tools}. The installation at {self.home} looks "
                "incomplete; point SUMO_HOME at one that includes its tools directory.") from error
        # An installation with no tools/ directory, or a traci already imported by something else,
        # leaves Python free to satisfy the import from another SUMO on PYTHONPATH -- so the module
        # driving the simulation can come from a different release than the binaries running it.
        origin = getattr(traci, "__file__", None)
        if origin and self.tools.resolve() not in Path(origin).resolve().parents:
            logger.warning("traci was imported from %s, which is not under %s; the Python modules "
                           "and the SUMO binaries in use are from different installations.",
                           origin, self.tools)
        return traci
