"""A compiled scenario package, a world package and a site profile for the run_capture tests.

Nothing here runs SUMO, the scenario compiler or a CARLA server. The scenario package is written
directly in the shape `ScenarioCompiler._lock` and `_stage_emission` give it -- a lock naming a route
file, a SUMO configuration, the world's network and a supervision plan, each with its SHA-256 -- so
the binding code reads exactly what a compile writes. The world package is a zip of `world.json`,
`map.xodr` and `map.net.xml` with the manifest keys `CarlaNet.Map` writes, at Gardnerville's origin.
The catalogue is the repository's measured one, so its digest is real.

The epoch is Pacific daylight time on 21 March 2026: 07:00 local is 14:00 UTC. The scenario declares
two windows, `morning` (07:00 for 30 minutes) and `night` (23:00 for 30 minutes), and ends at local
midnight.
"""
from __future__ import annotations

import copy
import hashlib
import json
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CATALOGUE = REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
CATALOGUE_DIGEST = json.loads(CATALOGUE.read_text(encoding="utf-8"))["catalogue_digest"]

MAP_NAME = "Gardnerville_Fixture"
ORIGIN = (38.91108, -119.7645965)
NETWORK = ('<?xml version="1.0" encoding="UTF-8"?>\n<net version="1.20">\n'
           '<location netOffset="0.00,0.00" convBoundary="-100.00,-100.00,100.00,100.00"/>\n'
           '<edge id="e1" from="a" to="b"><lane id="e1_0" index="0" speed="13.89" '
           'length="200.00" shape="-100.00,0.00 100.00,0.00"/></edge>\n</net>\n')
OPENDRIVE = '<OpenDRIVE><header north="100" south="-100" east="100" west="-100"/></OpenDRIVE>'
NETWORK_FINGERPRINT = hashlib.sha256(NETWORK.encode("utf-8")).hexdigest()
OPENDRIVE_SHA256 = hashlib.sha256(OPENDRIVE.encode("utf-8")).hexdigest()

MANIFEST = {
    "MapName": MAP_NAME,
    "OriginLatitude": ORIGIN[0],
    "OriginLongitude": ORIGIN[1],
    "GeoReferenceString": f"+proj=tmerc +lat_0={ORIGIN[0]} +lon_0={ORIGIN[1]} +k=1 +x_0=0 "
                          "+y_0=0 +ellps=WGS84 +units=m +no_defs",
    "OpenDriveSha256": OPENDRIVE_SHA256,
    "NetworkFingerprint": NETWORK_FINGERPRINT,
    "NetconvertArgv": ["--osm-files", "extract.osm"],
    "NetconvertVersion": "Eclipse SUMO netconvert 1.27.0",
}

EPOCH = {
    "epoch_version": 1,
    "civil_datetime": "2026-03-21T00:00:00-07:00",
    "utc_offset_hours": -7,
    "utc_datetime": "2026-03-21T07:00:00Z",
    "calendar_advances": True,
    "dst_in_effect": True,
    "time_zone_id": "America/Los_Angeles",
}
ILLUMINATION = {"illumination_version": 1, "policy": "freeze_at_window_start",
                "freeze_date_advances": False, "note": "one lighting condition per window"}
SCENARIO_ID = "gardnerville_fixture"
WINDOWS = {"morning": (25200.0, 27000.0), "night": (82800.0, 84600.0)}
END_S = 86400.0
STEP_S = 1.0
SEED = 42

ROUTES = ('<?xml version="1.0" encoding="UTF-8"?>\n<routes>\n'
          '  <vType id="car.lincoln" vClass="passenger">'
          '<param key="carla:blueprint" value="vehicle.lincoln.mkz"/></vType>\n'
          '  <vType id="car.charger" vClass="passenger">'
          '<param key="carla:blueprint" value="vehicle.dodge.charger"/></vType>\n'
          '  <vehicle id="v0" type="car.lincoln" depart="25300.00"><route edges="e1"/></vehicle>\n'
          '</routes>\n')
CONFIG = ('<?xml version="1.0" encoding="UTF-8"?>\n<configuration>\n'
          f'  <input><net-file value="{MAP_NAME}.net.xml"/>'
          f'<route-files value="{SCENARIO_ID}.rou.xml"/></input>\n'
          f'  <time><step-length value="{STEP_S!r}"/></time>\n'
          '  <processing><time-to-teleport value="-1"/></processing>\n'
          f'  <random_number><seed value="{SEED}"/></random_number>\n</configuration>\n')
SUPERVISION = json.dumps({"supervision_plan_version": 1, "plan_id": SCENARIO_ID,
                          "instances": [], "cohorts": [], "series": []}, indent=2) + "\n"


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_world_package(directory: Path, manifest: dict | None = None,
                        name: str = f"{MAP_NAME}.cwp") -> Path:
    """A world package as CarlaNet.Map writes one, without the bare-earth grid."""
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / name
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as package:
        package.writestr("world.json", json.dumps(manifest or MANIFEST))
        package.writestr("map.xodr", OPENDRIVE)
        package.writestr("map.net.xml", NETWORK)
    return path


def lock_document(world_package_name: str, files: dict[str, dict], **changes) -> dict:
    """The lock ScenarioCompiler._lock writes, for this fixture."""
    lock = {
        "lock_version": 1,
        "scenario_id": SCENARIO_ID,
        "scenario_name": "Gardnerville fixture",
        "spec_version": 1,
        "specification": f"{SCENARIO_ID}.scenario.json",
        "specification_sha256": "0" * 64,
        "compiler": {"name": "carlacontrol.ScenarioCompiler", "version": "1.0.0"},
        "files": files,
        "world": {"package": world_package_name, "map_name": MAP_NAME,
                  "network_fingerprint": NETWORK_FINGERPRINT,
                  "netconvert_argv": MANIFEST["NetconvertArgv"],
                  "netconvert_version": MANIFEST["NetconvertVersion"],
                  "opendrive_sha256": OPENDRIVE_SHA256, "source_osm_sha256": "1" * 64,
                  "origin_latitude": ORIGIN[0], "origin_longitude": ORIGIN[1],
                  "georeference": MANIFEST["GeoReferenceString"]},
        "catalogue": {"catalogue_id": "carla-0.10.0-windows", "catalogue_digest": CATALOGUE_DIGEST,
                      "blueprint_set_digest": "2" * 64, "content_build_id": "fixture"},
        "vocabulary": {"core_version": 1, "namespaces": {}, "vocabulary_digest": "3" * 64},
        "traffic": {"sumo_seed": SEED, "step_length_s": STEP_S, "end_s": END_S,
                    "processing": {"time-to-teleport": "-1"},
                    "routed_by": {"tool": "duarouter", "version": "1.27.0",
                                  "world_converter": MANIFEST["NetconvertVersion"],
                                  "release_agreement": "SameRelease",
                                  "mismatch_accepted": False}},
        "epoch": EPOCH,
        "epoch_block_sha256": hashlib.sha256(
            json.dumps(EPOCH, sort_keys=True, indent=2).encode("utf-8")).hexdigest(),
        "illumination": ILLUMINATION,
        "capture_windows": [{"id": name, "begin_s": begin, "end_s": end,
                             "civil_begin": "", "civil_end": "", "civil_date": "2026-03-21"}
                            for name, (begin, end) in WINDOWS.items()],
        "ephemeris": "CarlaNet.CoSim.DeclaredSun and SolarPositionModel.AtInstant",
        "illumination_label_association": {},
    }
    for key, value in changes.items():
        lock[key] = value
    return lock


def write_scenario_package(directory: Path, world_package_name: str = f"{MAP_NAME}.cwp",
                           **lock_changes) -> Path:
    """A compiled scenario package under `directory/<scenario_id>/`; returns its lock."""
    package = directory / SCENARIO_ID
    package.mkdir(parents=True, exist_ok=True)
    written = {
        "routes": (package / f"{SCENARIO_ID}.rou.xml", ROUTES),
        "config": (package / f"{SCENARIO_ID}.sumocfg", CONFIG),
        "network": (package / f"{MAP_NAME}.net.xml", NETWORK),
        "supervision": (package / f"{SCENARIO_ID}.supervision.json", SUPERVISION),
    }
    files = {}
    for role, (path, text) in written.items():
        path.write_text(text, encoding="utf-8", newline="\n")
        files[role] = {"path": path.name, "sha256": sha256_of(path)}
    lock = lock_document(world_package_name, files, **lock_changes)
    lock_path = package / f"{SCENARIO_ID}.lock.json"
    lock_path.write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8", newline="\n")
    return lock_path


class Layout:
    """A throwaway site layout: scenario root, world-package root, capture root, runs root."""

    def __init__(self, root: Path) -> None:
        self.root = root
        self.scenario_root = root / "scenarios"
        self.world_root = root / "world-packages"
        self.capture_root = root / "captures"
        self.runs_root = root / "runs"
        for directory in (self.scenario_root, self.world_root, self.capture_root, self.runs_root):
            directory.mkdir(parents=True, exist_ok=True)
        self.world_package = write_world_package(self.world_root)
        self.lock = write_scenario_package(self.scenario_root)

    def profile_document(self, **changes) -> dict:
        document = {
            "site_profile_version": 1,
            "server": {"host": "127.0.0.1", "port": 2000, "timeout_s": 30.0},
            "sumo": {"home": str(self.root / "sumo")},
            "paths": {"scenario_root": str(self.scenario_root),
                      "world_package_root": str(self.world_root),
                      "catalogue": str(CATALOGUE),
                      "capture_root": str(self.capture_root),
                      "runs_root": str(self.runs_root)},
            "environment": [],
        }
        for key, value in changes.items():
            document[key] = value
        return document

    def write_profile(self, **changes) -> Path:
        path = self.root / "site.profile.json"
        path.write_text(json.dumps(self.profile_document(**changes), indent=2), encoding="utf-8")
        return path


A_STARE = {"sensor_id": "OVERWATCH-1", "stare_look_at_x_m": 120.0, "stare_look_at_y_m": -340.0,
           "stare_altitude_m": 300.0, "stare_standoff_m": 400.0}
AN_ORBIT = {"sensor_id": "ORBIT-1", "pattern": "orbit", "orbit_centre_x_m": 50.0,
            "orbit_centre_y_m": -80.0}
RUN_DOCUMENT = {
    "run_configuration_version": 1,
    "scenario_package": SCENARIO_ID,
    "capture": {"window": "morning", "channels": [A_STARE]},
}


def run_document(**changes) -> dict:
    """A run configuration for the fixture scenario, with top-level blocks replaced by `changes`."""
    document = copy.deepcopy(RUN_DOCUMENT)
    for key, value in changes.items():
        document[key] = value
    return document
