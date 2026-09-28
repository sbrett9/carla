"""A compiled scenario, re-bound by its lock rather than recompiled.

`ScenarioCompiler` writes a scenario package beside one another -- the route file, the SUMO
configuration, the world's network, the supervision plan, the resolution report and
`<scenario_id>.lock.json` -- and the lock digests every file it names (`07_Scenario_Authoring.md`
§5, D7.6). A capture run binds that package: it reads the lock, and refuses a package whose files are
not the ones the lock digests, because a route file edited after the compile would run traffic the
resolution report never described (`12_Operator_Control_Surface.md` §6.1, check 49).

What the run reads from the lock is layer 4 of its configuration (12 §3.5): the epoch, the declared
capture windows, the SUMO step and seed, the scenario's end, the illumination default and the
catalogue it was compiled against; and the world binding it checks the world package against.
"""
from __future__ import annotations

import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path

from carlacontrol.VehicleCatalogue import BLUEPRINT_PARAM

LOCK_SUFFIX = ".lock.json"
READABLE_LOCK_VERSIONS = (1,)


class ScenarioPackage:
    """One compiled scenario package, opened for binding."""

    def __init__(self, lock_path: str | Path) -> None:
        self.lock_path = Path(lock_path).resolve()
        self.directory = self.lock_path.parent
        raw = self.lock_path.read_bytes()
        self.lock_sha256 = hashlib.sha256(raw).hexdigest()
        self.lock: dict = json.loads(raw.decode("utf-8"))
        if not isinstance(self.lock, dict):
            raise ValueError(f"{self.lock_path} is not a scenario lock")

    @classmethod
    def locate(cls, reference: str, scenario_root: str | Path | None) -> ScenarioPackage:
        """The package a reference names: a `.lock.json`, a package directory, or a scenario id.

        A directory must hold exactly one lock. An id is looked up as `<id>/<id>.lock.json` under
        `scenario_root`, which is where `compile_scenario.py --out-dir <root>/<id>` puts it.

        Raises:
            FileNotFoundError: naming every place looked.
        """
        path = Path(reference)
        if path.name.endswith(LOCK_SUFFIX) and path.is_file():
            return cls(path)
        if path.is_dir():
            locks = sorted(path.glob(f"*{LOCK_SUFFIX}"))
            if len(locks) == 1:
                return cls(locks[0])
            raise FileNotFoundError(f"{path} holds {len(locks)} scenario locks; name one")
        looked = [str(path)]
        if scenario_root is not None and not path.is_absolute() and path.parent == Path("."):
            candidate = Path(scenario_root) / reference / f"{reference}{LOCK_SUFFIX}"
            if candidate.is_file():
                return cls(candidate)
            looked.append(str(candidate))
        raise FileNotFoundError(f"no compiled scenario package at {' or '.join(looked)}. Compile "
                                "the specification with compile_scenario.py first; a capture run "
                                "binds a compiled package and never compiles one")

    # -- the lock's content ---------------------------------------------------------------------

    @property
    def lock_version(self) -> object:
        return self.lock.get("lock_version")

    @property
    def scenario_id(self) -> str:
        return str(self.lock.get("scenario_id", ""))

    @property
    def world(self) -> dict:
        return dict(self.lock.get("world") or {})

    @property
    def traffic(self) -> dict:
        return dict(self.lock.get("traffic") or {})

    @property
    def epoch(self) -> dict | None:
        return self.lock.get("epoch")

    @property
    def epoch_block_sha256(self) -> str | None:
        return self.lock.get("epoch_block_sha256")

    @property
    def illumination(self) -> dict | None:
        return self.lock.get("illumination")

    @property
    def catalogue_digest(self) -> str | None:
        return (self.lock.get("catalogue") or {}).get("catalogue_digest")

    @property
    def windows(self) -> dict[str, dict]:
        """The capture windows the scenario declares, by id."""
        return {str(w["id"]): dict(w) for w in self.lock.get("capture_windows", [])}

    # -- the files it names ----------------------------------------------------------------------

    def file(self, role: str) -> Path:
        """The file the lock records for a role -- `routes`, `config`, `network`, `supervision`."""
        entry = (self.lock.get("files") or {}).get(role)
        if not entry:
            raise KeyError(f"the scenario lock {self.lock_path} names no '{role}' file")
        return self.directory / entry["path"]

    @property
    def config_path(self) -> Path:
        """The `.sumocfg` the session launches SUMO with."""
        return self.file("config")

    def file_problems(self) -> list[str]:
        """Every file the lock names that is missing or not the file it digested. Empty when bound."""
        problems = []
        for role, entry in sorted((self.lock.get("files") or {}).items()):
            path = self.directory / entry.get("path", "")
            if not path.is_file():
                problems.append(f"the lock names {role} file {path.name}, which is not there")
                continue
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            if digest != entry.get("sha256"):
                problems.append(f"{role} file {path.name} digests {digest[:12]}, not the "
                                f"{str(entry.get('sha256'))[:12]} its lock recorded: it changed "
                                "after the compile. Recompile the specification")
        if not self.lock.get("files"):
            problems.append("the lock names no files")
        return problems

    def vtype_blueprints(self) -> dict[str, str]:
        """Each vehicle type the route file declares, and the blueprint it binds to by its
        `carla:blueprint` parameter. A type naming none is simulated and never rendered."""
        root = ET.parse(self.file("routes")).getroot()
        bound: dict[str, str] = {}
        for vtype in root.iter("vType"):
            for param in vtype.iter("param"):
                if param.get("key") == BLUEPRINT_PARAM:
                    bound[str(vtype.get("id"))] = str(param.get("value"))
        return bound

    def describe(self) -> str:
        return f"{self.scenario_id}@{self.lock_sha256[:12]}"
