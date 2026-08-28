"""Locate a SUMO installation and the executables and Python tools inside it.

SUMO is not a Python package: its command-line tools, its `traci`/`sumolib` modules and its PROJ data
all live inside an installation directory that SUMO's own convention names with the `SUMO_HOME`
environment variable. This resolves that directory once so callers do not each hard-code a path, and
looks in three places in order:

  1. a path given explicitly by the caller,
  2. `SUMO_HOME`, which is what a SUMO install sets and what `traci` itself checks,
  3. any additional candidate directories the caller offers (a source build inside a repository,
     say), and finally the executable search path.
"""
from __future__ import annotations

import os
import shutil
import sys
from collections.abc import Iterable
from dataclasses import dataclass
from pathlib import Path

EXECUTABLE_SUFFIX = ".exe" if os.name == "nt" else ""


@dataclass(frozen=True)
class SumoInstallation:
    """One SUMO installation on this machine."""

    home: Path

    @classmethod
    def locate(cls, explicit: str | Path | None = None,
               extra_candidates: Iterable[str | Path] = ()) -> SumoInstallation:
        """Find a usable installation, or raise saying where it looked."""
        searched: list[Path] = []
        for candidate in (explicit, os.environ.get("SUMO_HOME"), *extra_candidates):
            if not candidate:
                continue
            home = Path(candidate)
            searched.append(home)
            if cls._is_installation(home):
                return cls(home)

        # Nothing declared one, so fall back to a `sumo` on the executable search path and take
        # the directory above it as the installation.
        found = shutil.which("sumo") or shutil.which("netconvert")
        if found:
            home = Path(found).resolve().parent.parent
            if cls._is_installation(home):
                return cls(home)

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
        """Import `traci` from this installation."""
        self.add_tools_to_path()
        try:
            import traci
        except ImportError as error:
            raise ImportError(
                f"traci was not found under {self.tools}. The installation at {self.home} looks "
                "incomplete; point SUMO_HOME at one that includes its tools directory.") from error
        return traci
