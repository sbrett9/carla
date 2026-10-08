"""Where a CarlaControl command finds its inputs and writes its outputs when it is not told.

A command runs in one of two ways, and its defaults follow from which:

* **From a source checkout** -- a script under `CarlaControl/scripts/`, or a `carla-*` command whose
  `carlacontrol` was imported from `CarlaControl/src/` (an editable install). The defaults are the
  repository's: inputs under `Import/`, outputs and the staged SUMO toolchain under `Build/`, the
  vehicle catalogue under `CarlaControl/catalogue/`.
* **Installed from the wheel.** There is no repository. The current folder stands in for both
  `Import/` and `Build/`: what a checkout writes under `Build/` (captures, runs, world packages,
  recordings) is written under the current folder, and the inputs `Import/` holds there are looked for
  in the current folder or named by argument. The vehicle catalogue and the schemas are the copies
  installed with the package. SUMO is whichever installation the environment names --
  `CARLA_NETCONVERT`, `SUMO_HOME`, `PROJ_LIB`/`PROJ_DATA` -- which a CARLA distribution's environment
  step sets.

Which way is decided by where `carlacontrol` itself was imported from, not by the current folder: a
checkout keeps the package at `<root>/CarlaControl/src/carlacontrol`, beside the catalogue, and an
installed copy sits in `site-packages` with nothing of the kind around it.
"""
from __future__ import annotations

import atexit
import os
import shutil
from contextlib import ExitStack
from importlib import resources
from importlib.resources.abc import Traversable
from pathlib import Path

PACKAGE = "carlacontrol"
# Where the wheel puts its data, inside the installed package (pyproject.toml maps the repository's
# CarlaControl/catalogue/ and CarlaControl/schemas/ folders here).
PACKAGE_DATA = "data"
CATALOGUE = ("catalogue", "vehicles.catalogue.json")
VEHICLE_TYPES = ("catalogue", "vehicles.vtypes.rou.xml")
RUN_CONFIGURATION_SCHEMA = ("schemas", "run_configuration.schema.json")
EXECUTABLE_SUFFIX = ".exe" if os.name == "nt" else ""


class ToolLayout:
    """The default folders and data files of one run of a command."""

    # Package data that is not a file on disk -- a zipped installation -- is extracted once for the
    # life of the process, because the paths are handed to code that opens them by name.
    _extracted = ExitStack()
    atexit.register(_extracted.close)

    def __init__(self, checkout: str | Path | None, work: str | Path | None = None,
                 package_root: Traversable | Path | None = None) -> None:
        """
        Args:
            checkout: the repository root, or None when carlacontrol is installed.
            work: the folder an installed command reads and writes under; the current folder when
                not given. Unused from a checkout.
            package_root: the installed package's own folder, where its data is read from; the
                imported carlacontrol's when not given.
        """
        self.checkout = Path(checkout).resolve() if checkout is not None else None
        self.work = Path(work).resolve() if work is not None else Path.cwd()
        self._package_root = package_root

    @classmethod
    def current(cls) -> ToolLayout:
        """The layout of the carlacontrol being run: its checkout's, or the current folder's."""
        return cls(cls.find_checkout())

    @staticmethod
    def find_checkout(package_directory: str | Path | None = None) -> Path | None:
        """The repository root when the package was imported from a source checkout, else None.

        Args:
            package_directory: the folder holding carlacontrol's `__init__.py`; this module's own
                when not given.
        """
        package = Path(package_directory).resolve() if package_directory is not None \
            else Path(__file__).resolve().parent
        source = package.parent
        project = source.parent
        if source.name != "src" or project.name != "CarlaControl":
            return None
        root = project.parent
        if not (project.joinpath(*CATALOGUE)).is_file():
            return None
        return root

    # -- which way, said in words -----------------------------------------------------------------

    @property
    def installed(self) -> bool:
        """True when there is no checkout and the current folder stands in for it."""
        return self.checkout is None

    def describe(self) -> str:
        """Where the defaults come from, for a log line or a help text."""
        if self.checkout is not None:
            return f"the source checkout at {self.checkout}"
        return f"the current folder, {self.work} (carlacontrol is installed, not run from a checkout)"

    # -- folders ------------------------------------------------------------------------------------

    @property
    def build_directory(self) -> Path:
        """Where outputs go: the checkout's `Build/`, or the current folder."""
        return self.checkout / "Build" if self.checkout is not None else self.work

    @property
    def import_directory(self) -> Path:
        """Where inputs are looked for: the checkout's `Import/`, or the current folder."""
        return self.checkout / "Import" if self.checkout is not None else self.work

    @property
    def world_package_directory(self) -> Path:
        """Where world packages are written and looked for."""
        return self.build_directory / "world-packages"

    @property
    def capture_directory(self) -> Path:
        """Where a capture's frames and truth are written."""
        return self.build_directory / "captures"

    def site_paths(self) -> dict[str, tuple[Path, str]]:
        """A capture run's machine paths, each with where it came from, keyed by its field."""
        if self.checkout is not None:
            said = f"derived from the layout at {self.checkout}"
            catalogue_said = said
        else:
            said = f"derived from the installed layout: the current folder {self.work}"
            catalogue_said = "the catalogue installed with carlacontrol"
        return {
            "paths.scenario_root": (self.build_directory / "scenarios", said),
            "paths.world_package_root": (self.world_package_directory, said),
            "paths.catalogue": (self.catalogue, catalogue_said),
            "paths.capture_root": (self.capture_directory, said),
            "paths.runs_root": (self.build_directory / "runs", said),
        }

    # -- the SUMO toolchain -------------------------------------------------------------------------

    @property
    def staged_sumo(self) -> Path | None:
        """The SUMO installation CarlaSetup stages in the checkout, or None when installed."""
        return self.checkout / "Build" / "sumo-install" if self.checkout is not None else None

    def repository_sumo_builds(self) -> tuple[tuple[str, Path], ...]:
        """The checkout's SUMO builds, staged then source, each with how a log names it; none when
        installed."""
        if self.checkout is None:
            return ()
        return (("this repository's staged build", self.checkout / "Build" / "sumo-install"),
                ("this repository's source build", self.checkout / "Build" / "sumo-src"))

    def netconvert(self) -> Path | None:
        """The netconvert a world is converted with.

        `CARLA_NETCONVERT` first. Then, from a checkout, the staged build's, named whether or not
        it is there so that a missing one is reported by its path; installed, `SUMO_HOME`'s, then
        one on PATH, and None when there is none.
        """
        explicit = os.environ.get("CARLA_NETCONVERT")
        if explicit:
            return Path(explicit)
        name = f"netconvert{EXECUTABLE_SUFFIX}"
        if self.staged_sumo is not None:
            return self.staged_sumo / "bin" / name
        home = os.environ.get("SUMO_HOME")
        if home and (Path(home) / "bin" / name).is_file():
            return Path(home) / "bin" / name
        found = shutil.which("netconvert")
        return Path(found) if found else None

    def proj_data(self) -> Path | None:
        """PROJ's data folder for netconvert: `PROJ_LIB` or `PROJ_DATA`, else the staged build's;
        None when installed and neither is set, which leaves PROJ to find its own."""
        explicit = os.environ.get("PROJ_LIB") or os.environ.get("PROJ_DATA")
        if explicit:
            return Path(explicit)
        if self.staged_sumo is not None:
            return self.staged_sumo / "share" / "proj"
        return None

    def apply_sumo_environment(self) -> tuple[Path | None, Path | None]:
        """Name the toolchain in the environment for everything this process starts, and return the
        netconvert and PROJ data it named.

        Defaults only: a variable already set keeps its value, so an operator who sets one
        deliberately keeps that choice. From a checkout `SUMO_HOME` defaults to the staged build, so
        a machine with no SUMO installed still authors against the build that converted the world;
        installed, it is left to the environment.
        """
        netconvert = self.netconvert()
        proj = self.proj_data()
        if netconvert is not None:
            os.environ.setdefault("CARLA_NETCONVERT", str(netconvert))
        if proj is not None:
            os.environ.setdefault("PROJ_LIB", str(proj))
            os.environ.setdefault("PROJ_DATA", str(proj))
        if self.staged_sumo is not None:
            os.environ.setdefault("SUMO_HOME", str(self.staged_sumo))
        return netconvert, proj

    # -- data files ---------------------------------------------------------------------------------

    @property
    def catalogue(self) -> Path:
        """The measured vehicle catalogue: the checkout's, or the copy installed with the package."""
        return self._data_file(CATALOGUE)

    @property
    def vehicle_types(self) -> Path:
        """The SUMO vehicle types generated from the catalogue."""
        return self._data_file(VEHICLE_TYPES)

    @property
    def run_configuration_schema(self) -> Path:
        """The JSON schema of a capture run's configuration."""
        return self._data_file(RUN_CONFIGURATION_SCHEMA)

    def _data_file(self, parts: tuple[str, ...]) -> Path:
        if self.checkout is not None:
            return self.checkout.joinpath("CarlaControl", *parts)
        return self.package_data(*parts)

    def package_data(self, *parts: str) -> Path:
        """A file installed with the package, as a path that can be opened by name."""
        root = self._package_root if self._package_root is not None else resources.files(PACKAGE)
        resource = root.joinpath(PACKAGE_DATA, *parts)
        if isinstance(resource, Path):
            return resource
        return self._extracted.enter_context(resources.as_file(resource))
