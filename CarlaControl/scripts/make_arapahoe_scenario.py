#!/usr/bin/env python3
"""Write the SUMO network and dwell scenario for the Arapahoe / I-25 world.

Arapahoe I-25 is a 1.9 x 0.95 km extract of Arapahoe County, Colorado, a tall narrow strip running
along Interstate 25 where East Arapahoe Road crosses it. I-25 -- named South Valley Highway in
OpenStreetMap -- runs the full length of the map at 29.1 m/s across five and six lanes, Arapahoe
Road crosses it east to west at 17.9 m/s across as many as seven, and South Yosemite Street runs
north from Arapahoe up the west side.

The scenario this writes:

  * one marked vehicle enters at the southern end of I-25 heading north, leaves at the Arapahoe
    interchange, runs west along Arapahoe Road, turns north up South Yosemite Street and stops
    under the Yosemite Street road bridge, where it waits 30 minutes before returning to I-25 and
    leaving at the northern end of the map,
  * heavy freeway traffic in both directions with a wide spread of speeds, so faster vehicles work
    their way to the left and slower ones sit right,
  * an incident that closes four of the six northbound lanes for four minutes, which backs traffic
    up behind it and lets it drain again once the lanes reopen,
  * dense traffic on Arapahoe Road carrying a much larger share of vans and trucks than the freeway,
  * residential streets on the west and south edges feeding commuters onto the freeway and taking
    them home again.

The intent is that everything on the map behaves ordinarily except the vehicle parked under the
bridge, which is the only thing doing something a traffic model would not produce on its own.

Usage:
    python make_arapahoe_scenario.py [--dwell-minutes 30] [--out-dir ../../Import]
Then, from the output directory:
    sumo-gui -c Arapahoe_I25_UnderpassDwell.sumocfg
"""
import argparse
import logging
import math
import os
import sys
import zipfile

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402  (needs the path above)
from carlacontrol.SumoScenarioBuilder import (  # noqa: E402
    AmbientFlow,
    DwellTrip,
    LaneClosure,
    NetconvertSettings,
    RoadNetwork,
    SumoScenarioBuilder,
)

MAP_NAME = "Arapahoe_I25"
SCENARIO_NAME = f"{MAP_NAME}_UnderpassDwell"
REPO_SUMO = os.path.join(_REPO, "Build", "sumo-src")

# The clipped extract the shipped world was built from, and the world package beside it. The package
# is a zip here rather than the loose files the Gardnerville world uses.
SOURCE_OSM = os.path.join(_REPO, "Build", "sumo-smoketest", f"{MAP_NAME}_clipped.osm")
WORLD_PACKAGE = os.path.join(_REPO, "Build", "world-packages", f"{MAP_NAME}.cwp")

# Origin from that world package. Pinning it puts the centre of the extract at (0,0); the map then
# spans x -476.8..476.7 and y -969.3..969.3, which matches the CARLA map's OpenDRIVE header exactly.
NETCONVERT_SETTINGS = NetconvertSettings(origin_lat=39.59431, origin_lon=-104.88449)

# Where the marked vehicle waits: 39.600357 N, 104.886490 W, which is x=-171.8, y=+671.4 in the
# map's metres. Nothing there is tagged as a tunnel -- South Yosemite Street is carried over on a
# bridge, and this is the ground-level roadway underneath it, 87.9 m along a 154.9 m lane.
DWELL_LANE = "218965860#0_0"
DWELL_POSITION = 87.93

# I-25 is South Valley Highway; the two carriageways enter and leave at opposite corners because the
# freeway runs diagonally across the extract.
I25_NORTHBOUND_IN, I25_NORTHBOUND_OUT = "37722905", "106308386"
I25_SOUTHBOUND_IN, I25_SOUTHBOUND_OUT = "472478085", "908324823"
I25_NORTHBOUND_MIDDLE = "1001791386"
I25_NORTHBOUND_UPSTREAM = "907700111"

# Enough of the route named for SUMO to reproduce it: off at the interchange, west along Arapahoe
# Road, north up Yosemite, then the roadway under the bridge. Everything between is left to the
# router, which is why the network's own connections decide the ramps rather than a hand-copied list.
DWELL_VIA = ("629675735", "427819537#1", "1026993839#0", "218965860#0")

# Vehicle types. The freeway ones exist to produce a speed gradient across the lanes: SUMO has no
# per-lane speed setting, so the gradient has to come from what the drivers want and how willing
# they are to move over for it. lcKeepRight above 1 pushes a type right when it is not overtaking,
# lcSpeedGain above 1 makes it change lanes for speed more readily.
VEHICLE_TYPES = """\
    <!-- Freeway traffic. The spread of speedFactor across these three, together with the lane-change
         parameters, is what puts the quick vehicles in the left lanes and the heavy ones on the
         right; SUMO has no per-lane limit to set directly. -->
    <vType id="car_quick" vClass="passenger" length="4.6" maxSpeed="60" color="0.85,0.85,0.90"
           speedFactor="normc(1.18,0.06,1.05,1.35)" lcSpeedGain="3.0" lcKeepRight="0.3"
           lcAssertive="1.5" sigma="0.4" tau="0.9"/>
    <vType id="car" vClass="passenger" length="4.6" maxSpeed="55" color="0.70,0.72,0.78"
           speedFactor="normc(1.02,0.06,0.90,1.15)" lcSpeedGain="1.2" lcKeepRight="1.0"
           sigma="0.5" tau="1.1"/>
    <vType id="suv" vClass="passenger" length="5.0" width="1.95" maxSpeed="52" color="0.35,0.40,0.45"
           speedFactor="normc(1.00,0.06,0.88,1.12)" lcSpeedGain="1.0" lcKeepRight="1.5"
           sigma="0.5" tau="1.1"/>
    <vType id="van" vClass="delivery" length="5.9" maxSpeed="45" color="0.90,0.90,0.90"
           speedFactor="normc(0.94,0.05,0.82,1.05)" lcSpeedGain="0.6" lcKeepRight="2.5"
           sigma="0.5" tau="1.2"/>
    <vType id="truck" vClass="truck" length="12.0" maxSpeed="35" color="0.60,0.45,0.30"
           speedFactor="normc(0.86,0.04,0.78,0.95)" lcSpeedGain="0.4" lcKeepRight="4.0"
           sigma="0.5" tau="1.4"/>
    <vType id="semi" vClass="truck" length="16.5" maxSpeed="32" color="0.45,0.35,0.25"
           speedFactor="normc(0.84,0.03,0.78,0.92)" lcSpeedGain="0.3" lcKeepRight="5.0"
           sigma="0.5" tau="1.6"/>
    <vType id="motorcycle" vClass="motorcycle" length="2.2" width="0.90" maxSpeed="60" color="0.20,0.20,0.20"
           speedFactor="normc(1.20,0.10,1.00,1.40)" lcSpeedGain="4.0" lcKeepRight="0.2"
           sigma="0.4" tau="0.8"/>

    <!-- Freeway mix: mostly cars, a realistic tail of heavy vehicles. -->
    <vTypeDistribution id="freeway_mix"
                       vTypes="car_quick car suv van truck semi motorcycle"
                       probabilities="0.26 0.32 0.20 0.08 0.07 0.04 0.03"/>

    <!-- Arterial mix: Arapahoe Road is lined with commercial frontage, so vans and box trucks make
         up a much larger share of it than they do of the freeway. -->
    <vTypeDistribution id="arterial_mix"
                       vTypes="car suv van truck motorcycle"
                       probabilities="0.42 0.24 0.20 0.11 0.03"/>

    <!-- Residential mix: commuters, nothing heavy. -->
    <vTypeDistribution id="residential_mix"
                       vTypes="car suv van motorcycle"
                       probabilities="0.55 0.33 0.09 0.03"/>
"""

# Ambient traffic. Gateways are the fringe edges where a road leaves the extract.
AMBIENT_FLOWS = [
    # Interstate 25 through traffic, the bulk of the map's movement. Six lanes northbound and five
    # southbound at 65 mph, so these rates are busy rather than congested until the incident hits.
    AmbientFlow("i25_north_through", I25_NORTHBOUND_IN, I25_NORTHBOUND_OUT, 3300,
                via=(I25_NORTHBOUND_MIDDLE,), vehicle_type="freeway_mix"),
    AmbientFlow("i25_south_through", I25_SOUTHBOUND_IN, I25_SOUTHBOUND_OUT, 3000, vehicle_type="freeway_mix"),
    # Freeway traffic that uses the Arapahoe Road interchange rather than passing straight through.
    AmbientFlow("i25_north_to_arapahoe_east", I25_NORTHBOUND_IN, "427819527", 420, vehicle_type="freeway_mix"),
    AmbientFlow("i25_north_to_arapahoe_west", I25_NORTHBOUND_IN, "427819541#0", 380, vehicle_type="freeway_mix"),
    AmbientFlow("i25_south_to_arapahoe_east", I25_SOUTHBOUND_IN, "427819527", 360, vehicle_type="freeway_mix"),
    AmbientFlow("i25_south_to_arapahoe_west", I25_SOUTHBOUND_IN, "427819541#0", 340, vehicle_type="freeway_mix"),
    AmbientFlow("arapahoe_east_to_i25_north", "131933384", I25_NORTHBOUND_OUT, 400, vehicle_type="freeway_mix"),
    AmbientFlow("arapahoe_west_to_i25_north", "427819540#0", I25_NORTHBOUND_OUT, 360, vehicle_type="freeway_mix"),
    AmbientFlow("arapahoe_east_to_i25_south", "131933384", I25_SOUTHBOUND_OUT, 340, vehicle_type="freeway_mix"),
    AmbientFlow("arapahoe_west_to_i25_south", "427819540#0", I25_SOUTHBOUND_OUT, 320, vehicle_type="freeway_mix"),
    # East Arapahoe Road, crossing the map under the freeway.
    AmbientFlow("arapahoe_east_to_west", "131933384", "427819541#0", 700, vehicle_type="arterial_mix"),
    AmbientFlow("arapahoe_west_to_east", "427819540#0", "427819527", 680, vehicle_type="arterial_mix"),
    # South Yosemite Street, the north-south arterial on the west side.
    AmbientFlow("yosemite_north_to_south", "427819547", "629629570", 260, vehicle_type="arterial_mix"),
    AmbientFlow("yosemite_south_to_north", "-629629570", "-427819547", 240, vehicle_type="arterial_mix"),
    AmbientFlow("yosemite_north_to_arapahoe_east", "427819547", "427819527", 180, vehicle_type="arterial_mix"),
    AmbientFlow("arapahoe_east_to_yosemite_north", "131933384", "-427819547", 170, vehicle_type="arterial_mix"),
    # The surrounding street grid: Boston, Clinton, Caley, Peakview, Willow, Wabash.
    AmbientFlow("clinton_to_arapahoe_west", "-629634784", "427819541#0", 150, vehicle_type="arterial_mix"),
    AmbientFlow("arapahoe_west_to_clinton", "427819540#0", "629634784", 140, vehicle_type="arterial_mix"),
    AmbientFlow("caley_to_arapahoe_east", "132833790", "427819527", 130, vehicle_type="arterial_mix"),
    AmbientFlow("arapahoe_east_to_caley", "131933384", "629653938", 120, vehicle_type="arterial_mix"),
    AmbientFlow("peakview_west_to_east", "16999218", "292396861#1", 150, vehicle_type="arterial_mix"),
    AmbientFlow("peakview_east_to_west", "633447921", "46107902#0", 140, vehicle_type="arterial_mix"),
    AmbientFlow("boston_court_to_arapahoe_east", "17000739", "427819527", 90, vehicle_type="arterial_mix"),
    AmbientFlow("arbor_to_arapahoe_east", "16998684#0", "427819527", 80, vehicle_type="arterial_mix"),
    AmbientFlow("willow_to_yosemite_south", "550665536#0", "629629570", 90, vehicle_type="arterial_mix"),
    AmbientFlow("wabash_to_arapahoe_west", "427479206", "427819541#0", 80, vehicle_type="arterial_mix"),
    # Commuters: out of the residential streets on the west and south edges and onto the freeway,
    # and the same trips in reverse coming home.
    AmbientFlow("davies_avenue_to_i25_north", "16993828#0", I25_NORTHBOUND_OUT, 70, vehicle_type="residential_mix"),
    AmbientFlow("i25_south_to_davies_avenue", I25_SOUTHBOUND_IN, "-16993828#0", 65, vehicle_type="residential_mix"),
    AmbientFlow("costilla_place_to_i25_north", "16996647", I25_NORTHBOUND_OUT, 60, vehicle_type="residential_mix"),
    AmbientFlow("i25_south_to_costilla_place", I25_SOUTHBOUND_IN, "-16996647", 55, vehicle_type="residential_mix"),
    AmbientFlow("costilla_avenue_to_i25_south", "16996898", I25_SOUTHBOUND_OUT, 60, vehicle_type="residential_mix"),
    AmbientFlow("i25_north_to_costilla_avenue", I25_NORTHBOUND_IN, "-16996898", 55, vehicle_type="residential_mix"),
    AmbientFlow("davies_place_to_i25_north", "17001552#0", I25_NORTHBOUND_OUT, 55, vehicle_type="residential_mix"),
    AmbientFlow("i25_south_to_davies_place", I25_SOUTHBOUND_IN, "-17001552#1", 50, vehicle_type="residential_mix"),
    AmbientFlow("briarwood_place_to_i25_north", "17003522", I25_NORTHBOUND_OUT, 60, vehicle_type="residential_mix"),
    AmbientFlow("i25_south_to_briarwood_place", I25_SOUTHBOUND_IN, "-17003522", 55, vehicle_type="residential_mix"),
    AmbientFlow("briarwood_boulevard_to_i25_south", "17007347#0", I25_SOUTHBOUND_OUT, 60, vehicle_type="residential_mix"),
    AmbientFlow("i25_north_to_briarwood_boulevard", I25_NORTHBOUND_IN, "-17007347#0", 55, vehicle_type="residential_mix"),
    AmbientFlow("briarwood_avenue_to_arapahoe_east", "224876698", "427819527", 70, vehicle_type="residential_mix"),
    AmbientFlow("arapahoe_east_to_briarwood_avenue", "131933384", "-224876698", 65, vehicle_type="residential_mix"),
    AmbientFlow("easter_place_to_i25_north", "17006662#0", I25_NORTHBOUND_OUT, 50, vehicle_type="residential_mix"),
    AmbientFlow("i25_south_to_easter_place", I25_SOUTHBOUND_IN, "-17006662#0", 45, vehicle_type="residential_mix"),
    AmbientFlow("fremont_circle_to_i25_north", "-16991914", I25_NORTHBOUND_OUT, 55, vehicle_type="residential_mix"),
    AmbientFlow("i25_south_to_fremont_circle", I25_SOUTHBOUND_IN, "16991914", 50, vehicle_type="residential_mix"),
    AmbientFlow("xanthia_street_to_arapahoe_east", "-17003598#2", "427819527", 50, vehicle_type="residential_mix"),
    AmbientFlow("xanthia_way_to_i25_north", "-17006541", I25_NORTHBOUND_OUT, 45, vehicle_type="residential_mix"),
    AmbientFlow("alton_way_to_arapahoe_east", "-17003147#17", "427819527", 55, vehicle_type="residential_mix"),
    AmbientFlow("arapahoe_east_to_alton_way", "131933384", "17003147#12", 50, vehicle_type="residential_mix"),
]


def read_bare_earth_origin(package_path: str) -> tuple[float, float] | None:
    """The origin latitude and longitude recorded inside a world package, for a sanity check."""
    if not os.path.exists(package_path):
        return None
    with zipfile.ZipFile(package_path) as package:
        import json
        manifest = json.loads(package.read("world.json"))
    return manifest["OriginLatitude"], manifest["OriginLongitude"]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out-dir", default=os.path.join(_REPO, "Import"),
                        help="where the network, routes, incident and config go "
                             "(default: carla/Import)")
    parser.add_argument("--osm", default=SOURCE_OSM,
                        help="clipped OpenStreetMap extract the CARLA world was built from")
    parser.add_argument("--dwell-minutes", type=float, default=30.0,
                        help="how long the marked vehicle waits under the bridge (default 30)")
    parser.add_argument("--depart", type=int, default=120,
                        help="second the marked vehicle enters, after the traffic has filled the "
                             "map (default 120)")
    parser.add_argument("--parking", action="store_true",
                        help="take the marked vehicle off the carriageway while it waits. Off by "
                             "default because a parked vehicle stops being reported, and being "
                             "reported for the whole dwell is the point")
    parser.add_argument("--incident-start", type=float, default=900.0,
                        help="second the northbound lane closure begins (default 900)")
    parser.add_argument("--incident-seconds", type=float, default=240.0,
                        help="how long the closure lasts (default 240)")
    parser.add_argument("--incident-lanes", type=int, default=4,
                        help="how many of the six northbound lanes to close (default 4)")
    parser.add_argument("--no-incident", action="store_true", help="leave the freeway clear")
    parser.add_argument("--end", type=int, default=0,
                        help="simulation end in seconds (default: sized to the marked vehicle)")
    parser.add_argument("--step-length", type=float, default=0.05,
                        help="simulation step in seconds (default 0.05)")
    parser.add_argument("--seed", type=int, default=42,
                        help="random seed written into the config (default 42)")
    parser.add_argument("--sumo-home",
                        help="SUMO installation providing netconvert. Defaults to $SUMO_HOME, "
                             "then this repository's own build, then PATH")
    parser.add_argument("--reuse-network", action="store_true",
                        help="keep the network already in the output directory")
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

    recorded = read_bare_earth_origin(WORLD_PACKAGE)
    if recorded and not all(math.isclose(a, b, abs_tol=1e-9) for a, b in
                            zip(recorded, (NETCONVERT_SETTINGS.origin_lat,
                                           NETCONVERT_SETTINGS.origin_lon), strict=True)):
        logging.error("origin here (%s) disagrees with the world package (%s); the network would "
                      "not share the CARLA map's frame", (NETCONVERT_SETTINGS.origin_lat,
                                                          NETCONVERT_SETTINGS.origin_lon), recorded)
        return 1

    builder = SumoScenarioBuilder(installation.netconvert, installation.proj_data)
    out_dir = args.out_dir
    network_name = f"{MAP_NAME}.net.xml"
    routes_name = f"{SCENARIO_NAME}.rou.xml"
    incident_name = f"{SCENARIO_NAME}.add.xml"
    network_path = os.path.join(out_dir, network_name)

    if args.reuse_network:
        logging.info("reusing network %s", network_path)
    else:
        builder.build_network(args.osm, network_path, NETCONVERT_SETTINGS)
    network = RoadNetwork.from_file(network_path)

    dwell_seconds = args.dwell_minutes * 60.0
    trip = DwellTrip(from_edge=I25_NORTHBOUND_IN, to_edge=I25_NORTHBOUND_OUT,
                     dwell_lane=DWELL_LANE, dwell_seconds=dwell_seconds, via=DWELL_VIA,
                     depart_time=args.depart, dwell_position=DWELL_POSITION,
                     parking=args.parking)

    # The marked vehicle drives about 5.6 km either side of its wait; the wait dominates. Round up
    # to the next minute with room for the queue it may sit in.
    end_time = args.end or int(round((args.depart + dwell_seconds + 700) / 60.0 + 1) * 60)

    additional: tuple[str, ...] = ()
    if not args.no_incident:
        lanes = tuple(f"{I25_NORTHBOUND_MIDDLE}_{i}" for i in range(args.incident_lanes))
        closure = LaneClosure(trigger_edge=I25_NORTHBOUND_UPSTREAM, closed_lanes=lanes,
                              begin=args.incident_start,
                              end=args.incident_start + args.incident_seconds)
        builder.write_additional(os.path.join(out_dir, incident_name), closure)
        additional = (incident_name,)
        logging.info("incident: %d of 6 northbound lanes closed from %.0f s to %.0f s",
                     len(lanes), closure.begin, closure.end)

    routes_path = builder.write_dwell_routes(os.path.join(out_dir, routes_name), network, trip,
                                             AMBIENT_FLOWS, end_time, title=MAP_NAME,
                                             vehicle_types=VEHICLE_TYPES)
    config_path = builder.write_config(os.path.join(out_dir, f"{SCENARIO_NAME}.sumocfg"),
                                       network_name, routes_name, end_time, args.step_length,
                                       args.seed, additional)

    normal = network.edge_ids
    logging.info("network  %s", network_path)
    logging.info("         %d edges, %.1f km of road", len(normal),
                 sum(network.length_of(e) for e in normal) / 1000.0)
    logging.info("routes   %s", routes_path)
    logging.info("         marked vehicle waits %.0f minutes on %s at %.1f m along the lane",
                 args.dwell_minutes, network.street_name.get("218965860#0"), DWELL_POSITION)
    logging.info("         ambient %d flows, %d vehicles/hour",
                 len(AMBIENT_FLOWS), sum(f.vehicles_per_hour for f in AMBIENT_FLOWS))
    logging.info("config   %s  (ends at %d s)", config_path, end_time)
    return 0


if __name__ == "__main__":
    sys.exit(main())
