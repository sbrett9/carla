#!/usr/bin/env python3
"""Write, and compile, the supervision check: a six-minute scenario on the Arapahoe / I-25 world that
exercises the whole supervision path, from the plan to every capture's truth.

Supervision is checked live on the Arapahoe world, so this is written for it and for a camera placed
over one curb of South Yosemite Street, just north of East Arapahoe Road, where the
northbound roadway runs straight for 100 m in two lanes. All three planned vehicles enter from the
map's west edge on East Arapahoe Road and turn left up Yosemite at the signal; measured in SUMO alone:

  * a car departing at 20 s pulls to the curb at 109 s and waits two minutes, to 229 s -- an annotated
    **dwell**, its interval anchored to its stop, from arriving to leaving, so it declares its length
    and no instant;
  * a second car departing at 90 s drives through, past the waiting car between 144 s and 160 s, and
    leaves the map's north edge at 259 s -- an annotated **transit**, anchored to its departure, its
    route written in three phases so the stretch past the curb is an interval of its own, from
    entering that phase to entering the next;
  * a van departing at 200 s pulls to the same curb, 30 m short of where the car waited, from 254 s to
    274 s and drives on -- a **nominal** stop carrying a label that is a matched negative for the
    dwell (`hard_negative_for`);
  * and around them, ordinary traffic on Arapahoe Road and South Yosemite Street, drawn from the
    Arapahoe dwell's own flows and vehicle classes at the rates that scenario was measured at.

The terms are this check's own, in namespace `check`. **No recurring series**: a series sites each
of its slots at an area of interest, and the Arapahoe package publishes none, so it cannot
be declared without inventing an area. The first scenario of a world that does publish areas can carry
one.

**Time.** Simulated second zero is 07:26:00 Mountain Daylight Time on 29 September 2026, the morning
the Arapahoe dwell starts on, chosen so the sun crosses the +6 degree line between the `golden` and
`day` illumination bands between the two capture windows: the first opens at 07:27:00 under a 5.69
degree sun, the second at 07:30:00 under a 6.26 degree one. The dwell and the transit's pass lie in the first
window and the van's stop in the second; the run lasts 360 s.

**A run configuration is written beside the scenario**, `<scenario>.run.json`: `run_capture` captures
the first window through one camera, `Check_Overhead_1`, looking down on the curb from 70 m, and writes
the capture's sidecars, its manifest with the plan's interval rows and the world truth track.

Usage:
    python make_supervision_check_scenario.py [--out-dir ../../Import]
Then, from the repository root:
    python CarlaControl/scripts/run_capture.py --run Import/Arapahoe_I25_SupervisionCheck.run.json
"""
import argparse
import json
import logging
import math
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))
sys.path.insert(0, _THIS)

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

# One world, one measured traffic: the places, the vehicle classes, the mixes and the ambient flows are
# the Arapahoe dwell's own, so the check runs among the traffic that scenario was tuned to.
from make_arapahoe_scenario import (  # noqa: E402
    AMBIENT_FLOWS,
    CATALOGUE,
    EPOCH,
    ILLUMINATION,
    MAP_NAME,
    PLACES,
    STAGED_SUMO,
    VEHICLE_CLASSES,
    VEHICLE_MIXES,
    WORLD_PACKAGE,
    ArapahoeDwellSpecification,
)

SCENARIO_NAME = f"{MAP_NAME}_SupervisionCheck"

# 07:26:00 Mountain Daylight Time: the sun crosses +6 degrees, doc 11's edge between `golden` and
# `day`, between the two capture windows below.
CHECK_EPOCH = {
    **EPOCH,
    "civil_datetime": "2026-09-29T07:26:00-06:00",
    "utc_datetime": "2026-09-29T13:26:00Z",
    "note": "Arapahoe County, Colorado: simulated second zero is 07:26:00 Mountain Daylight Time on "
            "29 September 2026, when the sun is about to cross +6 degrees",
}

END_S = 360

# The two candidate windows, each a different illumination band: the dwell lies in the first, the
# van's stop in the second.
CAPTURE_WINDOWS = [
    {"id": "dwell_golden", "begin": 60, "length": 180},
    {"id": "stop_day", "begin": 240, "length": 120},
]

# The kerb the camera looks down on: South Yosemite Street northbound between East Arapahoe Road and
# South Yosemite Circle, 101 m in two lanes. Offsets are along its rightmost lane.
KERB_EDGE = "427819553#0"
DWELL_OFFSET_M = 70.0
BRIEF_STOP_OFFSET_M = 40.0

# The transit's route in its three phases, each edge named as a place: in along East Arapahoe Road
# eastbound from the map's west edge; left onto South Yosemite Street and up past the kerb; and north
# along Yosemite to the map's north edge. The route is the one `duarouter` gives the dwelling car,
# written out so the stretch past the kerb is a phase an interval can be anchored to.
TRANSIT_PHASES = (
    ("arapahoe_west_in", "arapahoe_eastbound_at_yosemite"),
    ("yosemite_northbound_from_arapahoe", "yosemite_kerb_stretch"),
    ("yosemite_north_1", "yosemite_north_2", "yosemite_north_3", "yosemite_north_4",
     "yosemite_north_5", "yosemite_north_6", "yosemite_north_7", "yosemite_north_8",
     "yosemite_north_9", "yosemite_north_out"),
)

CHECK_PLACES = {
    "kerb_dwell": {"lane": f"{KERB_EDGE}_0", "offset_m": DWELL_OFFSET_M},
    "kerb_brief_stop": {"lane": f"{KERB_EDGE}_0", "offset_m": BRIEF_STOP_OFFSET_M},
    "yosemite_kerb_stretch": {"edge": KERB_EDGE},
    "arapahoe_eastbound_at_yosemite": {"edge": "427819539#0"},
    **{f"yosemite_north_{index}": {"edge": edge} for index, edge in enumerate(
        ("427819555#0", "427819546", "427819548", "427819545#0", "427819558#0", "16999198#0",
         "-427819557", "-427819554#2", "-714754657#0"), start=1)},
}

# The camera over the kerb: looking down from 70 m, 25 m to the east, on the point midway between
# the van's stop and the car's, so both stops and the traffic passing them are in frame.
CAMERA = {"sensor_id": "Check_Overhead_1", "stare_altitude_m": 70.0, "stare_standoff_m": 25.0,
          "stare_bearing_deg": 90.0, "fov": 50.0, "width": 1920, "height": 1080}
CAMERA_OFFSET_M = (DWELL_OFFSET_M + BRIEF_STOP_OFFSET_M) / 2.0

# The ambient flows kept from the Arapahoe dwell: every one that drives Arapahoe Road west of the
# freeway or South Yosemite Street, so the kerb has traffic passing it, at the rates that scenario was
# measured at.
CHECK_FLOWS = ("arapahoe_east_to_west", "arapahoe_west_to_east", "yosemite_north_to_south",
               "yosemite_south_to_north", "yosemite_north_to_arapahoe_east",
               "arapahoe_east_to_yosemite_north", "clinton_to_arapahoe_west",
               "arapahoe_west_to_clinton", "wabash_to_arapahoe_west")

VOCABULARY = {"namespaces": [{
    "namespace": "check",
    "version": 1,
    "authority": "The supervision check on Arapahoe; "
                 "CarlaControl/scripts/make_supervision_check_scenario.py",
    "terms": [
        {"term": "check:kerbside_dwell", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "A car pulls to the curb and waits there for minutes, with nothing to "
                       "deliver or collect, then drives on.",
         "parameters": {"dwell_s": {"type": "number", "unit": "s",
                                    "definition": "the authored length of the wait at the curb"}},
         "contrast_with": ["check:brief_kerb_stop"],
         "counterfactual": {"kind": "term", "ref": "check:brief_kerb_stop"},
         "exemplar_instances": ["kerbside_dwell"]},
        {"term": "check:through_transit", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "A car drives through the scene without stopping: in from East Arapahoe "
                       "Road and out to the north along South Yosemite Street."},
        {"term": "check:brief_kerb_stop", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "A van pulls to the same curb for twenty seconds, as a delivery or a "
                       "pick-up does, and drives on: an ordinary stop where the dwell happens.",
         "hard_negative_for": ["check:kerbside_dwell"]},
    ],
}]}

AT_THE_STOP = {"start": "stop:0", "end": "stop_end:0"}


class SupervisionCheckSpecification:
    """The supervision check as a specification the scenario compiler compiles."""

    def __init__(self, step_length_s: float, seed: int) -> None:
        self.step_length_s = step_length_s
        self.seed = seed

    def build(self, world_package: Path, network_text: str, catalogue: Path, base: Path) -> dict:
        """The specification, with every path relative to `base`, the directory it is written in."""
        flows = [flow for flow in AMBIENT_FLOWS if flow.flow_id in CHECK_FLOWS]
        dwell = ArapahoeDwellSpecification(0.0, 0, True, None, 1.0, END_S, self.step_length_s,
                                           self.seed)
        return {
            "spec_version": 1,
            "scenario_id": SCENARIO_NAME,
            "scenario_name": "Arapahoe supervision check",
            "description": (
                "Six minutes on South Yosemite Street just north of East Arapahoe Road: a car waits "
                "two minutes at the curb, a second car drives through, and a van stops at the same "
                "curb for twenty seconds, among the Arapahoe dwell's own traffic on Arapahoe Road "
                "and Yosemite, so every kind of supervision the plan carries for a vehicle can be "
                "checked live. Written by CarlaControl/scripts/make_supervision_check_scenario.py; "
                "edit that, not this."),
            "world": {"package": ArapahoeDwellSpecification._relative(world_package, base),
                      "network_fingerprint": NetworkFingerprint.of_text(network_text)},
            "epoch": CHECK_EPOCH,
            "illumination": ILLUMINATION,
            "seeds": {"sumo": self.seed},
            "simulation": {"end": END_S, "step_length_s": self.step_length_s},
            "catalogue": ArapahoeDwellSpecification._relative(catalogue, base),
            "vehicle_classes": [ArapahoeDwellSpecification._vehicle_class(c)
                                for c in VEHICLE_CLASSES],
            "vehicle_mixes": [{"id": mix_id, "shares": dict(shares), "note": note}
                              for mix_id, shares, note in VEHICLE_MIXES],
            "places": {**PLACES, **CHECK_PLACES},
            "flows": [dwell._flow(flow) for flow in flows],
            "actors": self.actors(),
            "vocabulary": VOCABULARY,
            "supervision": self.supervision(),
            "capture_windows": CAPTURE_WINDOWS,
        }

    @staticmethod
    def actors() -> list[dict]:
        """The three vehicles the plan names, each an ordinary body of its kind: the cars draw the
        nine measured cars, so neither is told apart by its shape, and the van is the one van."""
        return [
            {"id": "dweller", "type": "car", "depart": 20, "from": "arapahoe_west_in",
             "to": "yosemite_north_out", "via": ["yosemite_kerb_stretch"],
             "stops": [{"place": "kerb_dwell", "duration": 120, "parking": True}],
             "depart_lane": "best", "depart_speed": "max", "arrival_speed": "current"},
            {"id": "transit", "type": "car", "depart": 90,
             "phases": [{"route": list(phase)} for phase in TRANSIT_PHASES],
             "depart_lane": "best", "depart_speed": "max", "arrival_speed": "current"},
            {"id": "brief_stopper", "type": "van", "depart": 200, "from": "arapahoe_west_in",
             "to": "yosemite_north_out", "via": ["yosemite_kerb_stretch"],
             "stops": [{"place": "kerb_brief_stop", "duration": 20, "parking": True}],
             "depart_lane": "best", "depart_speed": "max", "arrival_speed": "current"},
        ]

    @staticmethod
    def supervision() -> dict:
        return {"instances": [
            {"name": "kerbside_dwell", "supervision": "annotated",
             "labels": ["check:kerbside_dwell"],
             "participants": [{"actor": "dweller", "role": "subject"}],
             "intervals": [{"participant": "dweller", "phase": "dwell", "anchor": AT_THE_STOP}],
             "parameters": {"dwell_s": 120}},
            {"name": "through_transit", "supervision": "annotated",
             "labels": ["check:through_transit"],
             "participants": [{"actor": "transit", "role": "subject"}],
             "intervals": [{"participant": "transit", "phase": "transit",
                            "anchor": {"start": "depart"}},
                           {"participant": "transit", "phase": "past_the_kerb",
                            "anchor": {"start": "phase:1", "end": "phase:2"}}]},
            {"name": "brief_stop", "supervision": "nominal", "labels": ["check:brief_kerb_stop"],
             "participants": [{"actor": "brief_stopper", "role": "subject"}],
             "intervals": [{"participant": "brief_stopper", "phase": "stop",
                            "anchor": AT_THE_STOP}]},
        ]}


def kerb_point(network_text: str, offset_m: float) -> tuple[float, float]:
    """Where a position along the kerb lane lies in CARLA's frame (x east, y south): SUMO's lane
    position scaled onto the lane's shape, and SUMO (x, y) is CARLA (x, -y)."""
    lane = ET.fromstring(network_text).find(f"edge[@id='{KERB_EDGE}']/lane[@index='0']")
    shape = [tuple(float(v) for v in point.split(",")[:2]) for point in lane.get("shape").split()]
    segments = list(zip(shape, shape[1:], strict=False))
    lengths = [math.dist(a, b) for a, b in segments]
    along = offset_m * sum(lengths) / float(lane.get("length"))
    for (a, b), length in zip(segments, lengths, strict=True):
        if along <= length or (a, b) == segments[-1]:
            t = min(1.0, along / length) if length else 0.0
            return round(a[0] + t * (b[0] - a[0]), 1), round(-(a[1] + t * (b[1] - a[1])), 1)
        along -= length
    raise ValueError("the curb lane has no shape")


def run_configuration(network_text: str) -> dict:
    """What `run_capture` reads to capture the dwell's window through the camera over the kerb. It
    names the scenario by its lock relative to the repository root, where the owner runs it."""
    x, y = kerb_point(network_text, CAMERA_OFFSET_M)
    return {
        "run_configuration_version": 1,
        "caller_label": "supervision check",
        "scenario_package": f"Import/{SCENARIO_NAME}.lock.json",
        "capture": {
            "window": CAPTURE_WINDOWS[0]["id"],
            "prewarm_s": 30.0,
            "channels": [{**CAMERA, "stare_look_at_x_m": x, "stare_look_at_y_m": y}],
        },
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out-dir", default=os.path.join(_REPO, "Import"),
                        help="where the specification, the compiled scenario and its run "
                             "configuration go (default: carla/Import)")
    parser.add_argument("--world-package", default=WORLD_PACKAGE,
                        help="the Arapahoe world package, whose map.net.xml this scenario runs")
    parser.add_argument("--step-length", type=float, default=0.05,
                        help="simulation step in seconds, the Arapahoe dwell's (default 0.05)")
    parser.add_argument("--seed", type=int, default=42, help="SUMO seed (default 42)")
    parser.add_argument("--sumo-home", default=STAGED_SUMO if os.path.exists(STAGED_SUMO) else None,
                        help="SUMO installation providing duarouter (default: Build/sumo-install)")
    parser.add_argument("--allow-sumo-version-mismatch", action="store_true",
                        help="compile when that SUMO is not the release that converted the world; "
                             "the lock records the acceptance")
    parser.add_argument("--skip-dry-run", action="store_true",
                        help="skip the SUMO-only run that refuses a planned vehicle SUMO never "
                             "inserts (check 59), for quick iteration; the lock records that it "
                             "was skipped")
    parser.add_argument("--catalogue", default=CATALOGUE,
                        help="measured vehicle catalogue every vehicle type is sized from")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    try:
        installation = SumoInstallation.locate(args.sumo_home)
        network_text = WorldPackageReader(args.world_package).network_text()
    except (FileNotFoundError, ValueError) as error:
        logging.error("%s", error)
        return 1
    out_dir = Path(args.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    specification = SupervisionCheckSpecification(args.step_length, args.seed).build(
        Path(args.world_package).resolve(), network_text, Path(args.catalogue).resolve(), out_dir)
    spec_path = out_dir / f"{SCENARIO_NAME}.scenario.json"
    spec_path.write_text(json.dumps(specification, indent=2, ensure_ascii=False) + "\n",
                         encoding="utf-8", newline="\n")
    logging.info("specification %s (%d flows, %d planned vehicles, ends at %d s)", spec_path,
                 len(specification["flows"]), len(specification["actors"]), END_S)
    result = ScenarioCompiler(installation, args.allow_sumo_version_mismatch,
                              args.skip_dry_run).compile(spec_path, out_dir)
    for finding in result.findings.findings:
        (logging.error if finding.outcome == "refuse" else logging.warning)("%s", finding)
    for role, path in sorted(result.files.items()):
        logging.info("%-13s %s", role, path)
    if not result.refused:
        run_path = out_dir / f"{SCENARIO_NAME}.run.json"
        run_path.write_text(json.dumps(run_configuration(network_text), indent=2) + "\n",
                            encoding="utf-8", newline="\n")
        logging.info("%-13s %s", "run", run_path)
    logging.info("%s", "REFUSED" if result.refused else "compiled")
    return 1 if result.refused else 0


if __name__ == "__main__":
    sys.exit(main())
