"""A small world package and a scenario specification over it, for the compiler's tests.

The world is the CarlaNet test fixture network (`street_names_true.net.xml`: West Street, East Street
in two edges and Cross Street through a signalised junction), packaged as `CarlaNet.Map` writes a
world -- manifest, OpenDRIVE header, network -- with the authoring reference set published into it by
`AuthoringReferenceSet`, exactly as a world build publishes it: the place index, the solar frame, and
one area of interest, `kerb`, on East Street's eastbound lane. The specification exercises every block
the compiler reads: the epoch, the illumination default, places of each form, a named instant, a
flow, actors with a stop, a rota with a skip, and supervision of every kind.
"""
from __future__ import annotations

import copy
import json
import zipfile
from pathlib import Path

from carlacontrol.AreaOfInterestSource import AreaOfInterestSource
from carlacontrol.AuthoringReferenceSet import AuthoringReferenceSet
from carlacontrol.GeodeticFrame import GeodeticFrame
from carlacontrol.NetworkFingerprint import NetworkFingerprint
from carlacontrol.OsmClipper import OsmClipper
from carlacontrol.SumoInstallation import SumoInstallation

REPO = Path(__file__).resolve().parents[2]
FIXTURES = REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network"
NETWORK = (FIXTURES / "street_names_true.net.xml").read_text(encoding="utf-8")
EXTRACT = FIXTURES / "street_layout.osm"
CATALOGUE = REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
STAGED_SUMO = REPO / "Build" / "sumo-install"
WORLD_ARGV = [line for line in (FIXTURES / "world_build_argv.txt").read_text(encoding="utf-8")
              .splitlines() if line]

GEOREFERENCE = ("+proj=tmerc +lat_0=39.5 +lon_0=-104.9 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m "
                "+no_defs")
MANIFEST = {
    "MapName": "StreetLayout",
    "OriginLatitude": 39.5,
    "OriginLongitude": -104.9,
    "GeoReferenceString": GEOREFERENCE,
    "StagingMinXMeters": -201.27, "StagingMinYMeters": -99.92,
    "StagingMaxXMeters": 201.27, "StagingMaxYMeters": 99.92,
    "StagingMarginMeters": 30.0,
    "NetconvertVersion": "Eclipse SUMO netconvert 1.27.0",
    "NetconvertArgv": WORLD_ARGV,
    "NetworkFingerprint": NetworkFingerprint.of_text(NETWORK),
    "OpenDriveSha256": "0" * 64,
    "SourceOsmSha256": "1" * 64,
}
OPENDRIVE = ('<OpenDRIVE><header north="99.92" south="-99.92" east="201.27" west="-201.27">'
             f"<geoReference><![CDATA[{GEOREFERENCE}]]></geoReference></header></OpenDRIVE>")

# Colorado in March, after daylight saving began: -06:00 with DST in effect. The world's
# georeference configures -104.9 / 15 = -6.99 h, 0.99 h away, inside check 40's hour.
EPOCH = {
    "epoch_version": 1,
    "civil_datetime": "2026-03-21T06:00:00-06:00",
    "utc_offset_hours": -6.0,
    "utc_datetime": "2026-03-21T12:00:00Z",
    "calendar_advances": True,
    "dst_in_effect": True,
    "time_zone_id": "America/Denver",
}

SPECIFICATION = {
    "spec_version": 1,
    "scenario_id": "street_layout_probe",
    "scenario_name": "Street layout probe",
    "description": "A probe halts at the kerb on East Street while ambient traffic passes; a patrol "
                   "checks the kerb and misses its second visit.",
    "world": {"package": "StreetLayout.cwp",
              "network_fingerprint": NetworkFingerprint.of_text(NETWORK)},
    "epoch": EPOCH,
    "illumination": {"illumination_version": 1, "policy": "freeze_at_window_start",
                     "note": "one lighting condition per window"},
    "seeds": {"sumo": 42},
    "simulation": {"end": "d0 09:00", "step_length_s": 0.05},
    "catalogue": str(CATALOGUE),
    "vehicle_classes": [
        {"class_id": "car", "blueprints": ["vehicle.lincoln.mkz", "vehicle.dodge.charger",
                                           "vehicle.mini.cooper"],
         "sumo_vclass": "passenger", "behaviour": {"maxSpeed": "40"}, "share": 1.0},
        {"class_id": "saloon", "blueprints": ["vehicle.lincoln.mkz"], "sumo_vclass": "passenger",
         "behaviour": {"maxSpeed": "40", "speedDev": "0"}, "share": 0.0},
    ],
    "vehicle_mix": "ambient_mix",
    "places": {
        "west_gate": {"edge": "900"},
        "east_end": {"edge": "901#1"},
        "cross_south": {"edge": "902#1"},
        "kerb": {"area": "kerb"},
        "east_before_cross": {"street": "East Street", "direction": "east", "at": "Cross Street"},
    },
    "instants": {"probe_time": "d0 07:00"},
    "flows": [
        {"id": "ambient", "type": "ambient_mix", "from": "west_gate", "to": "east_end",
         "vehs_per_hour": 200, "begin": "d0 06:00", "end": "d0 09:00"},
    ],
    "actors": [
        {"id": "probe", "type": "saloon", "depart": {"instant": "probe_time"},
         "from": "west_gate", "to": "east_end", "via": ["east_before_cross"],
         "stops": [{"place": "kerb", "duration": "5m"}]},
        {"id": "hauler", "type": "car", "depart": "d0 07:30", "from": "west_gate",
         "to": "cross_south"},
    ],
    "rotas": [
        {"id": "patrol", "days": [0], "at": ["06:15", "08:15"], "subjects": ["kerb"],
         "id_pattern": "patrol_d{day}_h{hour}",
         "template": {"type": "car", "from": "west_gate", "to": "east_end",
                      "stops": [{"place": "$subject", "duration": "10m", "parking": True}]},
         "skip": [{"day": 0, "at": "08:15", "subject_index": 0,
                   "because": "the second patrol does not come"}]},
    ],
    "vocabulary": {"namespaces": [{
        "namespace": "fixture", "version": 1, "authority": "the compiler's test fixture",
        "terms": [
            {"term": "fixture:standoff", "definition": "A vehicle halts at the kerb for minutes "
             "and leaves.", "applies_to": ["entity"], "realisation": ["present"], "since": 1,
             "status": "active"},
            {"term": "fixture:routine_patrol", "definition": "A scheduled patrol that halts at "
             "the kerb.", "applies_to": ["entity"], "realisation": ["present"], "since": 1,
             "status": "active", "hard_negative_for": ["fixture:standoff"]},
            {"term": "fixture:patrol_missed", "definition": "A scheduled patrol that never came.",
             "applies_to": ["slot"], "realisation": ["absent"], "since": 1, "status": "active",
             "counterfactual": {"kind": "term", "ref": "fixture:routine_patrol"}},
        ],
        "roles": [{"role": "fixture:patroller", "definition": "the vehicle making a patrol"}],
        "area_kinds": [{"kind": "fixture:kerb"}],
    }]},
    "supervision": {
        "instances": [
            {"name": "probe_standoff", "supervision": "annotated", "labels": ["fixture:standoff"],
             "participants": [{"actor": "probe", "role": "subject"}],
             "intervals": [{"participant": "probe", "phase": "wait", "begin": "d0 07:00",
                            "end": "d0 07:06"}],
             "aoi_refs": ["kerb"]},
            {"name": "hauler_nominal", "supervision": "nominal", "labels": [],
             "participants": [{"actor": "hauler", "role": "subject"}]},
        ],
        "cohorts": [{"flow": "ambient", "supervision": "unlabelled"}],
        "series": [{"series_id": "kerb_patrol", "rota": "patrol",
                    "member_role": "fixture:patroller", "slot_length": "10m",
                    "slot_aoi_refs": {"kerb": "kerb"}, "supervision": "nominal",
                    "labels": ["fixture:routine_patrol"]}],
        "absences": [{"name": "patrol_missed_d0_h8", "series": "kerb_patrol",
                      "entry": "patrol_d0_h8", "labels": ["fixture:patrol_missed"]}],
    },
    "capture_windows": [{"id": "morning", "begin": "d0 07:00", "length": "15m"}],
}


class ScenarioWorldFixture:
    """Builds the fixture world package and writes specifications against it."""

    def __init__(self, directory: Path, installation: SumoInstallation) -> None:
        self.directory = directory
        self.installation = installation
        self.package = self._write_package()

    @staticmethod
    def locate_sumo() -> SumoInstallation:
        found = SumoInstallation.locate(STAGED_SUMO if STAGED_SUMO.exists() else None)
        found.duarouter  # noqa: B018 -- raises when the installation has no duarouter
        return found

    def _write_package(self) -> Path:
        self.directory.mkdir(parents=True, exist_ok=True)
        path = self.directory / f"{MANIFEST['MapName']}.cwp"
        with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
            archive.writestr("world.json", json.dumps(MANIFEST, indent=2))
            archive.writestr("map.xodr", OPENDRIVE)
            archive.writestr("map.net.xml", NETWORK)
        longitude, latitude = GeodeticFrame(39.5, -104.9).to_geodetic(50.0, 1.68)
        raw = json.dumps({"type": "FeatureCollection", "features": [{
            "type": "Feature", "geometry": {"type": "Point", "coordinates": [longitude, latitude]},
            "properties": {"id": "kerb", "name": "Kerb", "kind": "fixture:kerb",
                           "radius_m": 1.5}}]}).encode()
        source = AreaOfInterestSource.from_bytes(raw, "street_layout.aoi.geojson",
                                                 OsmClipper.read_bounds(EXTRACT))
        report = AuthoringReferenceSet(path, self.installation, source).publish()
        if not report.areas_published:
            raise RuntimeError(f"the fixture area was not published: {report.refusals}")
        return path

    def specification(self, **changes) -> dict:
        """The fixture specification with top-level blocks replaced."""
        document = copy.deepcopy(SPECIFICATION)
        document.update(copy.deepcopy(changes))
        return document

    def write(self, document: dict, name: str = "fixture.scenario.json") -> Path:
        path = self.directory / name
        path.write_text(json.dumps(document, indent=2), encoding="utf-8")
        return path
