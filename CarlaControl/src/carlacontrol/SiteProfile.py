"""Layer 2 of a capture run's configuration: facts about this machine, not about the science.

`12_Operator_Control_Surface.md` §3.5: the server's address, the SUMO installation, and where
scenario packages, world packages, the catalogue, captures and run records live. Separated so a run
configuration moves between machines unedited. **Host dependence is allowed to live here and not to
hide here** (D12.28): every value carries where it came from, and a value read from an environment
variable names the variable.

Where the values come from, in order:

1. a site-profile file, when one is named (`--site-profile`): JSON, `site_profile_version` 1, with
   `server`, `sumo` and `paths` blocks and an `environment` list naming the variables the profile
   allows a value to be read from. A relative path in it is relative to the file;
2. otherwise, derived from the layout the tool runs from (12 §13 question 2's recommendation): in the
   source tree, `Build/scenarios`, `Build/world-packages`, `CarlaControl/catalogue/
   vehicles.catalogue.json`, `Build/captures` and `Build/runs` under the repository root;
3. `sumo.home` alone may come from `CARLANET_SUMO_HOME` when neither of the above sets it, and then
   from the layout's staged build (`Build/sumo-install`, then `Build/sumo-src`) -- the order the
   session itself searches (`CarlaNet.Sumo.SumoInstallation`). Where nothing holds a `sumo`, it is
   unset and the session searches `SUMO_HOME` and then `PATH`, which the profile records as a
   resolution the host decides.
"""
from __future__ import annotations

import json
import os
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path

SITE_PROFILE_VERSION = 1
SUMO_OVERRIDE_VARIABLE = "CARLANET_SUMO_HOME"
SUMO_SEARCHED_VARIABLES = ("SUMO_HOME", "PATH")
SUMO_EXECUTABLE = "sumo.exe" if os.name == "nt" else "sumo"

# Where the layout keeps each path, relative to its root.
LAYOUT_PATHS = {
    "paths.scenario_root": ("Build", "scenarios"),
    "paths.world_package_root": ("Build", "world-packages"),
    "paths.catalogue": ("CarlaControl", "catalogue", "vehicles.catalogue.json"),
    "paths.capture_root": ("Build", "captures"),
    "paths.runs_root": ("Build", "runs"),
}
LAYOUT_SUMO_BUILDS = (("Build", "sumo-install"), ("Build", "sumo-src"))
FILE_FIELDS = ("server.host", "server.port", "server.timeout_s", "sumo.home",
               *LAYOUT_PATHS)


@dataclass(frozen=True)
class SiteValue:
    """One machine fact: its value, where it came from, and the variable it was read from, if any."""

    value: object
    provenance: str
    environment_variable: str | None = None


class SiteProfile:
    """The machine facts one launch resolved, each with its provenance."""

    def __init__(self, values: dict[str, SiteValue], source: str,
                 declared_environment: tuple[str, ...] = ()) -> None:
        self.values = dict(values)
        self.source = source
        self.declared_environment = tuple(declared_environment)
        # Variables the host decides that no field of the profile holds: where a SUMO the profile
        # does not name will be searched for.
        self.host_searched: tuple[str, ...] = ()

    @classmethod
    def discover(cls, layout_root: str | Path, profile_path: str | Path | None = None,
                 environ: Mapping[str, str] | None = None) -> SiteProfile:
        """The profile for this machine: the named file, or the layout, then the SUMO search.

        Raises:
            ValueError: when a named profile file cannot be read or is not a site profile.
        """
        environ = os.environ if environ is None else environ
        layout_root = Path(layout_root).resolve()
        values: dict[str, SiteValue] = {}
        declared: tuple[str, ...] = ()
        if profile_path is not None:
            profile_path = Path(profile_path).resolve()
            document = cls._read(profile_path)
            declared = tuple(document.get("environment", []))
            values.update(cls._from_file(document, profile_path))
            source = f"site profile {profile_path}"
        else:
            source = f"the layout at {layout_root}"
        for path, parts in LAYOUT_PATHS.items():
            if path not in values:
                values[path] = SiteValue(str(layout_root.joinpath(*parts)),
                                         f"derived from the layout at {layout_root}")
        profile = cls(values, source, declared)
        if "sumo.home" not in values:
            profile._discover_sumo(layout_root, environ)
        return profile

    def _discover_sumo(self, layout_root: Path, environ: Mapping[str, str]) -> None:
        override = environ.get(SUMO_OVERRIDE_VARIABLE)
        if override and self.holds_sumo(Path(override)):
            self.values["sumo.home"] = SiteValue(
                str(Path(override).resolve()), f"environment {SUMO_OVERRIDE_VARIABLE}",
                SUMO_OVERRIDE_VARIABLE)
            return
        for parts in LAYOUT_SUMO_BUILDS:
            home = layout_root.joinpath(*parts)
            if self.holds_sumo(home):
                self.values["sumo.home"] = SiteValue(
                    str(home), f"derived from the layout at {layout_root}: its staged SUMO")
                return
        self.values["sumo.home"] = SiteValue(
            None, "nothing named one: the session searches SUMO_HOME, then PATH")
        self.host_searched = SUMO_SEARCHED_VARIABLES

    @staticmethod
    def holds_sumo(home: Path) -> bool:
        return (home / "bin" / SUMO_EXECUTABLE).is_file()

    @staticmethod
    def _read(path: Path) -> dict:
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as failure:
            raise ValueError(f"cannot read the site profile {path}: {failure}") from None
        if not isinstance(document, dict) or document.get("site_profile_version") \
                != SITE_PROFILE_VERSION:
            raise ValueError(f"{path} is not a site profile at site_profile_version "
                             f"{SITE_PROFILE_VERSION}")
        known = {"site_profile_version", "server", "sumo", "paths", "environment", "note"}
        unknown = sorted(set(document) - known)
        if unknown:
            raise ValueError(f"{path}: {', '.join(unknown)} is not a site-profile block")
        return document

    @classmethod
    def _from_file(cls, document: dict, path: Path) -> dict[str, SiteValue]:
        values: dict[str, SiteValue] = {}
        for field_path in FILE_FIELDS:
            group, name = field_path.split(".", 1)
            block = document.get(group) or {}
            if name not in block:
                continue
            value = block[name]
            # An empty path stays empty so that check 37 refuses it rather than it resolving to the
            # profile's own directory.
            is_path = group == "paths" or field_path == "sumo.home"
            if is_path and isinstance(value, str) and value != "":
                value = str((path.parent / value).resolve())
            values[field_path] = SiteValue(value, f"site profile {path}")
        for group in ("server", "sumo", "paths"):
            extra = sorted(set(document.get(group) or {}) -
                           {p.split(".", 1)[1] for p in FILE_FIELDS if p.startswith(group + ".")})
            if extra:
                raise ValueError(f"{path}: {', '.join(extra)} is not a field of '{group}'")
        return values

    # -- reading -------------------------------------------------------------------------------------

    def value(self, path: str) -> object:
        return self.values[path].value

    def get(self, path: str) -> SiteValue | None:
        return self.values.get(path)

    def undeclared_environment(self) -> list[tuple[str, str]]:
        """(field, variable) for every value this profile read from a variable it does not declare,
        and for every variable the host will be searched by because no field names a value."""
        found = [(path, value.environment_variable) for path, value in self.values.items()
                 if value.environment_variable
                 and value.environment_variable not in self.declared_environment]
        found += [("sumo.home", variable) for variable in self.host_searched
                  if variable not in self.declared_environment]
        return found

    def to_record(self) -> dict:
        """Every value with its provenance, for the lock."""
        return {"source": self.source, "declared_environment": list(self.declared_environment),
                "host_searched": list(self.host_searched),
                "values": {path: {"value": value.value, "provenance": value.provenance,
                                  "environment_variable": value.environment_variable}
                           for path, value in sorted(self.values.items())}}

    def to_template(self) -> dict:
        """A site-profile file holding this profile's values, to be edited for another machine."""
        document: dict = {"site_profile_version": SITE_PROFILE_VERSION,
                          "note": "Machine facts for run_capture. Relative paths are relative to "
                                  "this file.",
                          "environment": []}
        for path in FILE_FIELDS:
            if path not in self.values:
                continue
            group, name = path.split(".", 1)
            document.setdefault(group, {})[name] = self.values[path].value
        return document

    def write_template(self, path: str | Path) -> Path:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.to_template(), indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")
        return path
