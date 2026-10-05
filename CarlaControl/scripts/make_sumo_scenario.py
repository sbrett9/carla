#!/usr/bin/env python3
"""Write, and compile, the orbit scenario for the Gardnerville Centerville Lane world.

Gardnerville Centerville Lane is a 1.7 x 0.9 km rural extract of Douglas County, Nevada. Centerville
Lane, posted 45 mph, crosses it west to east at y = -50 and turns north-east at the far end;
everything else is residential, and the only closed circuit on the map is the Rock Terrace Drive
block in the north half, an 848 m perimeter of Rock Terrace Drive and Keystone Court around the
Cobblestone Drive / Lost River Lane cross streets.

The scenario this writes:

  * one marked vehicle, `orbiter`, enters at the west edge of the map on Centerville Lane at the
    posted limit, turns north up Cobblestone Drive, drives that perimeter 20 times at a residential
    11 m/s, then comes back down Cobblestone Drive and leaves west along Centerville Lane at 1.25
    times its posted limit -- 25.2 m/s against a signed 20.1,
  * ambient traffic runs across the whole map, weighted to the Centerville Lane corridor, with a
    share routed through the neighbourhood so the orbiting vehicle is not the only thing moving
    there. Insertion totals about 700 vehicles/hour, directional as a real corridor is at peak:
    roughly 430 per hour east out of the west gateway against 180 coming back. That is well above
    the real peak for a connector of this class, and the lighter westbound side is what leaves the
    marked vehicle room to exceed the limit on its way out.

Every vehicle in it is a body somebody measured. A vehicle class names the CARLA blueprints it
draws, and every type the compiler writes from it takes its length, width and height from the
spawn-and-measure sweep of that body, so the vehicle SUMO reserves road for is the vehicle CARLA
draws; AMBIENT_CLASSES below sets each measured body against the size the traffic was designed
around. The content build has no pickup, so this corridor has none.

**This script writes a specification, not SUMO XML** (`07_Scenario_Authoring.md` D7.2), and compiles
it with the scenario compiler, so the generated scenario faces every check a hand-written one does.
The specification, `<out-dir>/Gardnerville_Centerville_Lane_NeighborhoodOrbit.scenario.json`, names
every edge it uses as a place; the orbit is one actor in three phases -- in, held to each edge's
posted limit; round the loop 20 times, held to 11 m/s; out, on the vehicle's own speedFactor -- which
the compiler turns into one waypoint per held edge. The compiled package is written beside it: the
`.sumocfg`, the routed `.rou.xml`, the world package's own network byte for byte, the supervision
plan, the lock and the resolution report. The marked vehicle keeps its SUMO id, `orbiter`, which is
what `sumo_cot_telemetry.py --marked-vehicle` names by default; the specification asserts no label.

Usage:
    python make_sumo_scenario.py [--laps 20] [--out-dir ../../Import]
Then, from the output directory:
    sumo-gui -c Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg
"""
import argparse
import json
import logging
import os
import sys
from pathlib import Path

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402  (needs the path above)
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioVehicleMix import VehicleClassSpec  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.SumoScenarioBuilder import (  # noqa: E402
    AmbientFlow,
    OrbitRoute,
    OrbitSettings,
    RoadNetwork,
    SumoScenarioBuilder,
)
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

MAP_NAME = "Gardnerville_Centerville_Lane"
SCENARIO_NAME = f"{MAP_NAME}_NeighborhoodOrbit"

# The SUMO this repository stages and the distribution ships, the release every shipped world was
# converted by. The compiler refuses a routing SUMO of another release (check 6).
STAGED_SUMO = os.path.join(_REPO, "Build", "sumo-install")

# The measured vehicle catalogue every vehicle type in this scenario is sized from.
CATALOGUE = os.path.join(_REPO, "CarlaControl", "catalogue", "vehicles.catalogue.json")

# The world package, which carries the SUMO network this scenario runs, byte for byte.
WORLD_PACKAGE = os.path.join(_REPO, "Build", "world-packages", f"{MAP_NAME}.cwp")

# What simulated second zero means in civil time at the site: 10:00 Pacific Daylight Time on the 2026
# summer solstice, the epoch the live watch of this scenario has been run under. The offset is the
# declaration, daylight saving included; the zone name is carried for a reader and never resolved.
EPOCH = {
    "epoch_version": 1,
    "civil_datetime": "2026-06-21T10:00:00-07:00",
    "utc_offset_hours": -7,
    "utc_datetime": "2026-06-21T17:00:00Z",
    "calendar_advances": False,
    "dst_in_effect": True,
    "time_zone_id": "America/Los_Angeles",
    "note": "Gardnerville, Nevada: simulated second zero is 10:00 Pacific Daylight Time on the "
            "summer solstice",
}

# The sun holds still for each capture window, at the instant it opens: one lighting condition per
# window. An authored default the operator may override at run start.
ILLUMINATION = {"illumination_version": 1, "policy": "freeze_at_window_start"}

# Edge IDs are OSM way IDs; a leading '-' is the reverse direction of that way. Coordinates below
# are SUMO metres, so positive y is north.
ORBIT_ROUTE = OrbitRoute(
    # West map edge (-838.9, -52.0) east along Centerville Lane, then left up Cobblestone Drive.
    approach=("108141475#0", "108141475#2", "108141475#3", "108141475#4", "-219060582#2"),
    # The neighbourhood perimeter, clockwise from the Cobblestone Drive / Rock Terrace Drive corner
    # at (491, 43): Rock Terrace Dr west, Keystone Ct north, then Rock Terrace Dr round the top and
    # back down. 848 m enclosing the Lost River Lane / Cobblestone Drive block.
    loop=("219060581#4", "-219060584#0", "219060581#1", "219060581#2", "219060581#3"),
    # Back down Cobblestone Drive and west along Centerville Lane to the west map edge.
    exit=("219060582#2", "-108141475#4", "-108141475#3", "-108141475#2", "-108141475#1"),
)

# Gateways are the network's fringe dead ends: Centerville Lane west (-838.9, -52.0) and north-east
# (838.8, 355.4), Pleasantview Drive east (838.9, -45.2), and the residential stubs that run off the
# map edge to the south and north.
#
# Rates are directional, as a real corridor's are at peak: about 430 vehicles/hour head east out of
# the west gateway against about 180 coming back. Besides being the more honest shape, it is what
# leaves the marked vehicle room to exceed the limit on its way out -- Centerville Lane is one lane
# each way with no overtaking, so a westbound stream as dense as the eastbound one simply queues it
# behind a slower leader for the whole 1.2 km. Raise the westbound rates if a busier exit matters
# more than the exit overspeed being visible.
AMBIENT_FLOWS = [
    # Centerville Lane through traffic, the bulk of the map's movement.
    AmbientFlow("corridor_west_to_northeast", "108141475#0", "1236933617#2", 210),
    AmbientFlow("corridor_northeast_to_west", "-1236933617#2", "-108141475#1", 55),
    AmbientFlow("corridor_west_to_east", "108141475#0", "1419984783#1", 240),
    AmbientFlow("corridor_east_to_west", "-1419984783#1", "-108141475#1", 60),
    AmbientFlow("corridor_northeast_to_east", "-1236933617#2", "1419984783#1", 55),
    AmbientFlow("corridor_east_to_northeast", "-1419984783#1", "1236933617#2", 55),
    # Residential stubs running off the map to the south and north.
    AmbientFlow("edna_to_west", "14286827", "-108141475#1", 8),
    AmbientFlow("west_to_edna", "108141475#0", "-14286827", 25),
    AmbientFlow("marianne_to_east", "14288285", "1419984783#1", 28),
    AmbientFlow("west_to_marianne", "108141475#0", "-14288285", 28),
    AmbientFlow("rubio_to_west", "14289737", "-108141475#1", 9),
    AmbientFlow("east_to_rubio", "-1419984783#1", "-14289737", 28),
    AmbientFlow("heavenlyview_to_west", "-14286316", "-108141475#1", 8),
    AmbientFlow("west_to_heavenlyview", "108141475#0", "14286316", 25),
    AmbientFlow("northstub_to_west", "-1428171646", "-108141475#1", 9),
    AmbientFlow("west_to_northstub", "108141475#0", "1428171646", 28),
    AmbientFlow("turningcircle_to_east", "14285460", "1419984783#1", 20),
    AmbientFlow("east_to_turningcircle", "-1419984783#1", "-14285460", 20),
    # Traffic inside the neighbourhood itself, so the marked vehicle is circling among other cars
    # rather than alone. The `via` edges are what force these off Centerville Lane, which is
    # otherwise always the faster path; without them the whole block sees no ambient traffic at all.
    AmbientFlow("neighbourhood_through_north", "108141475#0", "1428171646", 40,
                via=("219060581#0", "219060581#1", "219060581#2")),
    AmbientFlow("neighbourhood_through_west", "-1236933617#2", "-108141475#1", 20,
                via=("-219060581#2", "-219060581#1", "-219060581#0")),
    AmbientFlow("neighbourhood_south_to_north", "14288285", "1428171646", 30,
                via=("219060581#0", "219060584#0", "219060581#4", "-219060581#3")),
    AmbientFlow("neighbourhood_north_to_south", "-1428171646", "-14288285", 30,
                via=("219060581#3", "-219060582#1", "-219060582#0")),
    AmbientFlow("cobblestone_to_east", "219060582#0", "1419984783#1", 30),
    AmbientFlow("east_to_cobblestone", "-1419984783#1", "-219060582#0", 30),
    AmbientFlow("lostriver_to_east", "219060583", "1419984783#1", 25),
    AmbientFlow("west_to_lostriver", "108141475#0", "-219060583", 25),
    AmbientFlow("rockterrace_local_north", "-219060581#4", "1428171646", 25),
    AmbientFlow("rockterrace_local_in", "108141475#0", "219060581#2", 25),
    AmbientFlow("keystone_to_west", "-219060584#1", "-108141475#1", 12),
    AmbientFlow("west_to_keystone", "108141475#0", "219060584#1", 30),
]

# What the ambient traffic is made of. Each class names the CARLA blueprints it draws, and every
# vehicle type written from it takes its length, width and height from the sweep that measured that
# body, so the vehicle SUMO reserves road for is the vehicle CARLA draws. `share` is this corridor's
# traffic composition as it was first authored; the emitted probabilities are normalised, so the
# missing pickup share below is redistributed across the surviving classes in the proportions the
# composition already held rather than being handed to whichever class happens to suit it.
#
# The classes this scenario was first written with declared their own lengths, and those numbers are
# recorded against the measurements here because they are what the traffic was designed around. Two
# are close and two are not, and the difference is a difference in body, not in pose: a type's length
# is now the measured length of the body it renders as, so nothing is placed away from where SUMO
# believes it is. What moves is the size of the vehicles this corridor is made of.
#
# There is no pickup. The measured catalogue holds seventeen vehicles and not one of them has a bed,
# so the class that carried a quarter of this corridor is absent rather than rendered as something
# else: a substituted body makes the imagery and the behavioural record disagree while each stays
# internally consistent, and nothing downstream can detect that. A rural Nevada corridor without
# pickups is a visibly incomplete population, and closing that needs a pickup in the content, not a
# different choice here.
AMBIENT_CLASSES = (
    VehicleClassSpec(
        class_id="car",
        # Every measured passenger body except the Patrol, which is the sport utility below.
        blueprints=("vehicle.ue4.audi.tt", "vehicle.mini.cooper", "vehicle.ue4.bmw.grantourer",
                    "vehicle.ue4.mercedes.ccc", "vehicle.ue4.ford.mustang", "vehicle.lincoln.mkz",
                    "vehicle.dodge.charger", "vehicle.ue4.chevrolet.impala",
                    "vehicle.ue4.ford.crown"),
        sumo_vclass="passenger",
        behaviour={"maxSpeed": "55", "speedFactor": "normc(1.00,0.10,0.80,1.20)"},
        share=0.45,
        gui_shape="passenger",
        note="Saloons, hatchbacks and coupes. Designed around a 4.6 m car; the nine measured bodies "
             "run 4.18 m to 5.37 m and average 4.82 m, so the class is 0.22 m longer on average "
             "than it was drawn up as and considerably more varied than the single body it used to "
             "be."),
    VehicleClassSpec(
        class_id="suv",
        blueprints=("vehicle.nissan.patrol",),
        sumo_vclass="passenger",
        behaviour={"maxSpeed": "52", "speedFactor": "normc(1.00,0.10,0.80,1.20)"},
        share=0.18,
        gui_shape="passenger",
        note="The only sport utility in the content build: a 5.59 x 2.15 x 2.06 m body, against the "
             "5.0 x 1.95 m this class was designed around, so every SUV here is 0.59 m longer than "
             "intended. One body means every SUV on the map looks the same, which is a property of "
             "the content and not of this scenario."),
    VehicleClassSpec(
        class_id="van",
        blueprints=("vehicle.sprinter.mercedes",),
        sumo_vclass="delivery",
        behaviour={"maxSpeed": "45", "speedFactor": "normc(0.95,0.08,0.75,1.10)"},
        share=0.07,
        gui_shape="delivery",
        note="A panel van measuring 5.92 m against the 5.9 m designed around: the closest agreement "
             "of any class here, at 0.02 m."),
    VehicleClassSpec(
        class_id="truck",
        blueprints=("vehicle.carlacola.actors", "vehicle.carlamotors.european_hgv"),
        sumo_vclass="truck",
        behaviour={"maxSpeed": "35", "speedFactor": "normc(0.90,0.06,0.75,1.05)"},
        share=0.05,
        gui_shape="truck",
        note="The two rigid lorries in the content build, a two-axle box truck measuring 8.00 m and "
             "a three-axle heavy goods vehicle measuring 7.92 m, drawn equally. Both are about 1.5 m "
             "shorter than the 9.5 m this class was designed around. The Fuso Rosa is a light bus, "
             "not a lorry, and is not drawn as one."),
)

# The marked vehicle's body. Everything about how it drives is set from the command line and written
# alongside this; what is fixed here is which body it is. The saloon measures 4.89 m against the
# 4.8 m the vehicle was designed around, the closest match in the catalogue, and it is also one of
# the nine bodies the ambient cars draw from, so the vehicle under observation is not the only one
# of its kind on the map.
ORBITER_BLUEPRINT = "vehicle.lincoln.mkz"


class GardnervilleOrbitSpecification:
    """The orbit scenario as a specification the scenario compiler compiles."""

    def __init__(self, network: RoadNetwork, orbit: OrbitSettings, end_s: int, step_length_s: float,
                 seed: int) -> None:
        self.network = network
        self.orbit = orbit
        self.end_s = end_s
        self.step_length_s = step_length_s
        self.seed = seed

    def build(self, world_package: Path, network_text: str, catalogue: Path, base: Path) -> dict:
        """The specification, with every path relative to `base`, the directory it is written in."""
        posted = self.network.speed_of(ORBIT_ROUTE.approach[0])
        return {
            "spec_version": 1,
            "scenario_id": SCENARIO_NAME,
            "scenario_name": "Gardnerville Centerville Lane neighbourhood orbit",
            "description": (
                f"One marked vehicle, orbiter, enters on Centerville Lane at the posted limit, drives "
                f"the Rock Terrace Drive block {self.orbit.laps} times held to "
                f"{self.orbit.loop_speed:g} m/s, and leaves west at {self.orbit.exit_speed_factor:g} "
                "times the posted limit, among directional corridor traffic and neighbourhood "
                "through traffic. Written by CarlaControl/scripts/make_sumo_scenario.py; edit that, "
                "not this."),
            "world": {"package": self._relative(world_package, base),
                      "network_fingerprint": NetworkFingerprint.of_text(network_text)},
            "epoch": EPOCH,
            "illumination": ILLUMINATION,
            "seeds": {"sumo": self.seed},
            "simulation": {"end": self.end_s, "step_length_s": self.step_length_s},
            "catalogue": self._relative(catalogue, base),
            "vehicle_classes": [self._vehicle_class(c) for c in (*AMBIENT_CLASSES, self.orbiter())],
            "vehicle_mix": "ambient_mix",
            "places": {edge: {"edge": edge} for edge in self._edges()},
            "flows": [self._flow(flow) for flow in AMBIENT_FLOWS],
            "actors": [{
                "id": "orbiter", "type": "orbiter", "depart": self.orbit.depart_time,
                "depart_lane": "free", "depart_speed": f"{posted:.2f}",
                "arrival_speed": "current",
                "phases": [
                    {"route": list(ORBIT_ROUTE.approach), "hold": "posted"},
                    {"route": list(ORBIT_ROUTE.loop), "repeat": self.orbit.laps,
                     "hold": self.orbit.loop_speed},
                    {"route": list(ORBIT_ROUTE.exit)},
                ],
            }],
        }

    def orbiter(self) -> VehicleClassSpec:
        """The marked vehicle: a class of one body, bound to a measurement by the same rule as the
        ambient traffic. Its share of zero keeps it out of the ambient mix."""
        return VehicleClassSpec(
            class_id="orbiter",
            blueprints=(ORBITER_BLUEPRINT,),
            sumo_vclass="passenger",
            behaviour={"maxSpeed": "55", "speedFactor": f"{self.orbit.exit_speed_factor:.2f}",
                       "speedDev": "0", "sigma": "0.20", "tau": "1.20"},
            # Conspicuous in sumo-gui so the author can follow it there, and nowhere else: the
            # rendered colour is drawn from the blueprint's own palette.
            gui_colour="#FF8C00",
            gui_shape="passenger",
            note="The marked vehicle. speedDev is zeroed so its speed multiple is exact rather "
                 "than drawn from a distribution around it.")

    @staticmethod
    def _relative(path: Path, base: Path) -> str:
        """`path` as the specification names it: relative to `base`, or whole where the two are on
        different drives and no relative path exists."""
        try:
            return os.path.relpath(path, base).replace(os.sep, "/")
        except ValueError:
            return path.as_posix()

    @staticmethod
    def _vehicle_class(spec: VehicleClassSpec) -> dict:
        entry = {"class_id": spec.class_id, "blueprints": list(spec.blueprints),
                 "sumo_vclass": spec.sumo_vclass, "behaviour": dict(spec.behaviour),
                 "share": spec.share}
        for key in ("gui_shape", "gui_colour", "note"):
            if getattr(spec, key):
                entry[key] = getattr(spec, key)
        if spec.weights:
            entry["weights"] = list(spec.weights)
        return entry

    def _flow(self, flow: AmbientFlow) -> dict:
        entry = {"id": flow.flow_id, "type": flow.vehicle_type, "from": flow.from_edge,
                 "to": flow.to_edge, "vehs_per_hour": flow.vehicles_per_hour,
                 "begin": flow.begin, "end": self.end_s if flow.end is None else flow.end,
                 "depart_lane": "free", "depart_speed": "max"}
        if flow.via:
            entry["via"] = list(flow.via)
        return entry

    @staticmethod
    def _edges() -> list[str]:
        """Every edge the scenario names, once each, in the order it first names them."""
        named = [*ORBIT_ROUTE.approach, *ORBIT_ROUTE.loop, *ORBIT_ROUTE.exit]
        for flow in AMBIENT_FLOWS:
            named += [flow.from_edge, *flow.via, flow.to_edge]
        return list(dict.fromkeys(named))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out-dir", default=os.path.join(_REPO, "Import"),
                        help="where the specification and the compiled scenario go "
                             "(default: carla/Import)")
    parser.add_argument("--world-package", default=WORLD_PACKAGE,
                        help="world package the CARLA map was generated from. Its map.net.xml is "
                             "the network this scenario runs; netconvert is not run here, because a "
                             "second run would produce a different graph")
    parser.add_argument("--laps", type=int, default=20,
                        help="times round the neighbourhood (default 20)")
    parser.add_argument("--loop-speed", type=float, default=11.0,
                        help="speed held while circling, in metres/second, about 25 mph "
                             "(default 11.0)")
    parser.add_argument("--exit-speed-factor", type=float, default=1.25,
                        help="multiple of the posted limit on the way out (default 1.25)")
    parser.add_argument("--depart", type=int, default=60,
                        help="second the marked vehicle enters, after ambient traffic has spread "
                             "across the map (default 60)")
    parser.add_argument("--end", type=int, default=0,
                        help="simulation end in seconds (default: sized to the orbit)")
    parser.add_argument("--step-length", type=float, default=0.05,
                        help="simulation step in seconds (default 0.05)")
    parser.add_argument("--seed", type=int, default=42,
                        help="SUMO seed, which fixes where the gaps in the ambient stream fall "
                             "(default 42)")
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
                        help="measured vehicle catalogue every vehicle type is sized from. A class "
                             "naming a body this catalogue does not hold is refused")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    try:
        installation = SumoInstallation.locate(args.sumo_home)
        package = WorldPackageReader(args.world_package)
        network_text = package.network_text()
    except (FileNotFoundError, ValueError) as error:
        logging.error("%s", error)
        return 1
    network = RoadNetwork.from_text(network_text)
    orbit = OrbitSettings(laps=args.laps, loop_speed=args.loop_speed,
                          exit_speed_factor=args.exit_speed_factor, depart_time=args.depart,
                          vehicle_type="orbiter")
    end_s = args.end or SumoScenarioBuilder().estimate_end_time(network, ORBIT_ROUTE, orbit)
    out_dir = Path(args.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    specification = GardnervilleOrbitSpecification(
        network, orbit, end_s, args.step_length, args.seed).build(
        Path(args.world_package).resolve(), network_text, Path(args.catalogue).resolve(), out_dir)
    spec_path = out_dir / f"{SCENARIO_NAME}.scenario.json"
    spec_path.write_text(json.dumps(specification, indent=2, ensure_ascii=False) + "\n",
                         encoding="utf-8", newline="\n")
    logging.info("specification %s (%d places, %d flows, the orbit %d + %d x %d + %d edges, ends "
                 "at %d s)", spec_path, len(specification["places"]), len(AMBIENT_FLOWS),
                 len(ORBIT_ROUTE.approach), args.laps, len(ORBIT_ROUTE.loop),
                 len(ORBIT_ROUTE.exit), end_s)
    result = ScenarioCompiler(installation, args.allow_sumo_version_mismatch,
                              args.skip_dry_run).compile(
        spec_path, out_dir)
    for finding in result.findings.findings:
        (logging.error if finding.outcome == "refuse" else logging.warning)("%s", finding)
    for role, path in sorted(result.files.items()):
        logging.info("%-13s %s", role, path)
    logging.info("%s", "REFUSED" if result.refused else "compiled")
    return 1 if result.refused else 0


if __name__ == "__main__":
    sys.exit(main())
