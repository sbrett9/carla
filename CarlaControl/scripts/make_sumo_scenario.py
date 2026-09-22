#!/usr/bin/env python3
"""Write the SUMO network and orbit scenario for the Gardnerville Centerville Lane world.

Gardnerville Centerville Lane is a 1.7 x 0.9 km rural extract of Douglas County, Nevada. Centerville
Lane, posted 45 mph, crosses it west to east at y = -50 and turns north-east at the far end;
everything else is residential, and the only closed circuit on the map is the Rock Terrace Drive
block in the north half, an 848 m perimeter of Rock Terrace Drive and Keystone Court around the
Cobblestone Drive / Lost River Lane cross streets.

The scenario this writes:

  * one marked vehicle enters at the west edge of the map on Centerville Lane at the posted limit,
    turns north up Cobblestone Drive, drives that perimeter 20 times at a residential 11 m/s, then
    comes back down Cobblestone Drive and leaves west along Centerville Lane at 1.25 times its
    posted limit -- 25.2 m/s against a signed 20.1,
  * ambient traffic runs across the whole map, weighted to the Centerville Lane corridor, with a
    share routed through the neighbourhood so the orbiting vehicle is not the only thing moving
    there. Insertion totals about 700 vehicles/hour, directional as a real corridor is at peak:
    roughly 430 per hour east out of the west gateway against 180 coming back. That is well above
    the real peak for a connector of this class, and the lighter westbound side is what leaves the
    marked vehicle room to exceed the limit on its way out.

Every vehicle in it is a body somebody measured. A vehicle type names one CARLA blueprint and takes
its length, width and height from the spawn-and-measure sweep of that blueprint, so the vehicle SUMO
reserves road for is the vehicle CARLA draws; what kind of vehicle a class is made of is declared in
AMBIENT_CLASSES below, with each measured body set against the size the traffic was designed around.
The content build has no pickup, so this corridor has none.

The network is rebuilt from the same clipped OSM and the same netconvert flags the CARLA world
package records, so its coordinates line up with the CARLA map: SUMO (x, y) is CARLA (x, -y).

Usage:
    python make_sumo_scenario.py [--laps 20] [--out-dir ../Import]
Then, from the output directory:
    sumo-gui -c Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg
"""
import argparse
import logging
import os
import sys

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.ScenarioVehicleMix import ScenarioVehicleMix, VehicleClassSpec  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402  (needs the path above)
from carlacontrol.SumoScenarioBuilder import (  # noqa: E402
    AmbientFlow,
    NetconvertSettings,
    OrbitRoute,
    OrbitSettings,
    SumoScenarioBuilder,
)
from carlacontrol.SumoVehicleTypeWriter import (  # noqa: E402
    ROUTES_SCHEMA_RELATIVE_PATH,
    SumoVehicleTypeWriter,
)
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402

MAP_NAME = "Gardnerville_Centerville_Lane"
SCENARIO_NAME = f"{MAP_NAME}_NeighborhoodOrbit"

# Falls back to the SUMO built inside this repository when SUMO_HOME is not set.
REPO_SUMO = os.path.join(_REPO, "Build", "sumo-src")

# The SUMO this repository stages and the distribution ships. Its route schema is what the written
# scenario is validated against, in preference to whichever SUMO is installed on this machine: the
# two have been observed to differ by a patch release, and the scenario has to load under the one
# that ships.
STAGED_SUMO = os.path.join(_REPO, "Build", "sumo-install")

# The measured vehicle catalogue every vehicle type in this scenario is sized from.
CATALOGUE = os.path.join(_REPO, "CarlaControl", "catalogue", "vehicles.catalogue.json")

# The clipped OSM the shipped world was built from; its SHA-256 is recorded in
# Build/world-packages/Gardnerville_Centerville_Lane.world.json as SourceOsmSha256.
SOURCE_OSM = os.path.join(_REPO, "Build", "sumo-smoketest", f"{MAP_NAME}_clipped.osm")

# The world package beside it, which carries the SUMO network this scenario is built against.
WORLD_PACKAGE = os.path.join(_REPO, "Build", "world-packages", f"{MAP_NAME}.cwp")

# Origin from that same world package. Pinning it puts the map's centre at (0,0), spanning
# x -838.9..838.9 and y -455.0..391.3.
NETCONVERT_SETTINGS = NetconvertSettings(origin_lat=38.91108, origin_lon=-119.76459650000001)

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
# There is no pickup. The content build registers seventeen vehicles and not one of them has a bed,
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
        blueprints=("vehicle.carlacola.actors", "vehicle.fuso.mitsubishi"),
        sumo_vclass="truck",
        behaviour={"maxSpeed": "35", "speedFactor": "normc(0.90,0.06,0.75,1.05)"},
        share=0.05,
        gui_shape="truck",
        note="The two rigid lorries in the content build, measuring 8.00 m and 10.17 m. The 9.5 m "
             "this class was designed around falls between them, so a lorry here is either 1.50 m "
             "shorter or 0.67 m longer than intended and the two are drawn equally."),
)

# The marked vehicle's body. Everything about how it drives is set from the command line and written
# alongside this; what is fixed here is which body it is. The saloon measures 4.89 m against the
# 4.8 m the vehicle was designed around, the closest match in the catalogue, and it is also one of
# the nine bodies the ambient cars draw from, so the vehicle under observation is not the only one
# of its kind on the map.
ORBITER_BLUEPRINT = "vehicle.lincoln.mkz"

VEHICLE_MIX_HEADER = (
    "Every vehicle type below names one CARLA blueprint in a carla:blueprint parameter and takes its "
    "length, width and height from the measurement of that body, so SUMO reserves road for the "
    "vehicle that is actually drawn. Variety inside a class is SUMO's own seeded draw from the "
    "class distribution, not a choice made at playback.\n"
    "A colour here is read by sumo-gui and reaches nothing rendered: the rendered colour comes from "
    "the blueprint's own measured palette, so highlighting a vehicle on screen cannot make the "
    "highlight a property of what was highlighted.\n"
    "This content build has no pickup, so this corridor has none. See "
    "CarlaControl/scripts/make_sumo_scenario.py for what each class is made of and how far each "
    "measured body sits from the size the traffic was designed around.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out-dir", default=os.path.join(_REPO, "Import"),
                        help="where the .net.xml, .rou.xml and .sumocfg go (default: carla/Import)")
    parser.add_argument("--osm", default=SOURCE_OSM,
                        help="clipped OpenStreetMap extract the CARLA world was built from")
    parser.add_argument("--world-package", default=WORLD_PACKAGE,
                        help="world package the CARLA map was generated from. Its map.net.xml is "
                             "the network this scenario is built against; netconvert is not run "
                             "here, because a second run would produce a different graph")
    parser.add_argument("--laps", type=int, default=20,
                        help="times round the neighbourhood (default 20)")
    parser.add_argument("--loop-speed", type=float, default=11.0,
                        help="speed cap while circling, in metres/second, about 25 mph "
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
                        help="random seed written into the config, which fixes where the gaps in "
                             "the ambient stream fall (default 42)")
    parser.add_argument("--sumo-home",
                        help="SUMO installation providing netconvert. Defaults to $SUMO_HOME, "
                             "then this repository's own build, then PATH")
    parser.add_argument("--reuse-network", action="store_true",
                        help="keep the .net.xml already in the output directory instead of "
                             "running netconvert again")
    parser.add_argument("--catalogue", default=CATALOGUE,
                        help="measured vehicle catalogue every vehicle type is sized from. A class "
                             "naming a body this catalogue does not hold stops the build")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    try:
        installation = SumoInstallation.locate(args.sumo_home, extra_candidates=[REPO_SUMO])
    except FileNotFoundError as error:
        logging.error("%s", error)
        return 1
    logging.info("SUMO from %s", installation.home)
    builder = SumoScenarioBuilder()
    try:
        catalogue = VehicleCatalogue.load(args.catalogue)
        # The marked vehicle is a class of one body, so it is bound to a measurement by the same
        # rule as the ambient traffic. Its share of zero keeps it out of the ambient mix.
        orbiter = VehicleClassSpec(
            class_id="orbiter",
            blueprints=(ORBITER_BLUEPRINT,),
            sumo_vclass="passenger",
            behaviour={"maxSpeed": "55", "speedFactor": f"{args.exit_speed_factor:.2f}",
                       "speedDev": "0", "sigma": "0.20", "tau": "1.20"},
            # Conspicuous in sumo-gui so the author can follow it there, and nowhere else: the
            # rendered colour is drawn from the blueprint's own palette.
            gui_colour="#FF8C00",
            gui_shape="passenger",
            note="The marked vehicle. speedDev is zeroed so its speed multiple is exact rather "
                 "than drawn from a distribution around it.")
        mix = ScenarioVehicleMix(catalogue, (*AMBIENT_CLASSES, orbiter), mix_id="ambient_mix",
                                 header=VEHICLE_MIX_HEADER)
        logging.info("vehicle catalogue %s (%d measured bodies)",
                     catalogue.catalogue_id, len(catalogue.blueprint_ids))
        for line in mix.summary():
            logging.info("  %s", line)

        paths = builder.build(
            world_package=args.world_package,
            out_dir=args.out_dir,
            map_name=MAP_NAME,
            scenario_name=SCENARIO_NAME,
            netconvert_settings=NETCONVERT_SETTINGS,
            route=ORBIT_ROUTE,
            orbit_settings=OrbitSettings(laps=args.laps, loop_speed=args.loop_speed,
                                         exit_speed_factor=args.exit_speed_factor,
                                         depart_time=args.depart,
                                         vehicle_type=orbiter.class_id),
            flows=AMBIENT_FLOWS,
            vehicle_types=mix.to_xml(),
            end_time=args.end,
            step_length=args.step_length,
            seed=args.seed,
            reuse_network=args.reuse_network,
        )
        # Read the written file back rather than trusting what was just written to it. The schema
        # says whether SUMO will load it at all; the catalogue check says whether every type in it
        # names a body that exists and is the size the type claims.
        schema = os.path.join(STAGED_SUMO, "data", str(ROUTES_SCHEMA_RELATIVE_PATH))
        if not os.path.exists(schema):
            schema = os.path.join(str(installation.home), "data", str(ROUTES_SCHEMA_RELATIVE_PATH))
        if os.path.exists(schema):
            SumoVehicleTypeWriter.validate(paths.routes, schema)
        else:
            logging.warning("no route schema found at %s; the scenario was not schema-checked",
                            schema)
        ScenarioVehicleMix.check_route_file(paths.routes, catalogue)
    except (FileNotFoundError, LookupError, RuntimeError, ValueError) as error:
        logging.error("%s", error)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
