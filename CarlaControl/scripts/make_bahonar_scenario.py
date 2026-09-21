#!/usr/bin/env python3
"""Write a week-long pattern-of-life SUMO scenario for the Shahid Bahonar Port world.

Shahid Bahonar is Bandar Abbas's older multipurpose and passenger port on the Strait of Hormuz, and
also a naval harbour: a military airfield with a guard-tower perimeter to the west, a commercial and
ferry port with an oil depot and drydock to the east, joined by a coastal trunk corridor. Almost
every road inside the wire carries OpenStreetMap `access=private`, so the network is built as two
populations that meet only at gates -- civilian traffic on the public coastal corridor, and
authorised traffic (naval `army`, port `authority`) everywhere inside -- with the fence enforced by
SUMO vehicle-class edge permissions rather than asserted.

Over seven simulated days the scenario establishes a rhythm: diurnal corridor traffic, ferry
sailings that pulse the port, three-shift gate changes at the airfield, a guard posted at each of
the sixteen towers and relieved every eight hours, and routine air-freight hauls from the apron to
the port. That baseline exists so that six planted anomalies stand out against it, each a different
kind of deviation a detector would have to catch:

  * guard no-show -- one tower is left unmanned for a shift (a gap in a perfect cadence);
  * escort-to-drydock -- a high-value air-freight shipment gets a dense military escort from the
    apron to the drydock, where the routine haul never goes (excess, formation, and a spike in a
    normally quiet corner -- one event chain, both signatures);
  * gate probe -- a civilian vehicle approaches the airfield sentry, waits, and leaves without
    entering, twice on different days (approach without entry);
  * perimeter shadow -- during the pre-dawn dead hours one vehicle slowly circles the fence line
    (temporal and spatial outlier);
  * ferry stay-behind -- a vehicle arrives on a ferry pulse and never leaves (persistence).

The marked vehicles are labelled in a sidecar `*.labels.json` the telemetry tool reads, so the CoT
dataset carries ground truth: civilian traffic neutral, military friendly, every anomaly unknown.

Usage:
    python make_bahonar_scenario.py [--days 7] [--out-dir ../../Import]
Preview a slice in the GUI before committing to the full week:
    python make_bahonar_scenario.py --days 1
    sumo-gui -c Shahid_Bahonar_Port_PatternOfLife.sumocfg
"""
import argparse
import json
import logging
import os
import sys

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402  (needs the path above)
from carlacontrol.SumoPatternOfLifeBuilder import (  # noqa: E402
    ScheduledVehicle,
    ScheduleStop,
    SumoPatternOfLifeBuilder,
)
from carlacontrol.SumoScenarioBuilder import (  # noqa: E402
    AmbientFlow,
    NetconvertSettings,
    RoadNetwork,
    SumoScenarioBuilder,
)

MAP_NAME = "Shahid_Bahonar_Port"
SCENARIO_NAME = f"{MAP_NAME}_PatternOfLife"
REPO_SUMO = os.path.join(_REPO, "Build", "sumo-src")
SOURCE_OSM = os.path.join(_REPO, "Build", "sumo-smoketest", f"{MAP_NAME}_clipped.osm")
WORLD_PACKAGE = os.path.join(_REPO, "Build", "world-packages", f"{MAP_NAME}.cwp")

# Origin from the world package: the centre of the extract, pinned to (0,0). The map spans
# x -3607..3607, y -1915..2108, matching the CARLA map's OpenDRIVE header.
NETCONVERT_SETTINGS = NetconvertSettings(
    origin_lat=27.15012, origin_lon=56.18065,
    # Keep the private (inside-the-wire) roads -- they are the whole scenario -- but drop genuine
    # pedestrian ways so they do not become drivable vehicle edges once the passenger filter is off.
    drivable_edges_only=False,
    remove_edge_types=("highway.footway", "highway.path", "highway.steps",
                       "highway.cycleway", "highway.pedestrian", "highway.bridleway"))

HOUR = 3600
DAY = 24 * HOUR

# Anchor edges, found and route-validated against the built network (see the scenario notes in the
# README). Corridor edges are public; the rest are inside the wire.
CORRIDOR_WEST_IN, CORRIDOR_WEST_OUT = "26417705#0", "175815458#5"     # Shahid Rajaei Highway, west
CORRIDOR_EAST_IN, CORRIDOR_EAST_OUT = "26401342#0", "26401454#6"      # Pasdaran Boulevard, east
CORRIDOR_NORTH_IN, CORRIDOR_NORTH_OUT = "181931491", "1396607732"     # freeway spur, north-east
APRON = "26413425#5"          # airfield apron / cargo, inside the wire
DRYDOCK = "206870533#2"       # ship repair drydock, far east, normally near-dead
FERRY = "900954912#2"         # Bahonar ferry terminal
# The civilian-reachable approach to the eastern port checkpoint (an OSM gate node). Civilians
# cannot get near the airfield sentry -- that road is inside the wire -- so the gate probe loiters
# here, on the public side of a port gate, which is as close as an uncleared vehicle can get.
PORT_GATE_APPROACH = "-431672573#2"

# Civilian corridor through-movements, each confirmed routable by SUMO's own router on this
# network. The public corridor is fragmented and effectively one-way in places, so westbound exits
# are not reachable and are left out rather than faked.
CORRIDOR_PAIRS = [
    (CORRIDOR_WEST_IN, CORRIDOR_EAST_OUT),
    (CORRIDOR_WEST_IN, CORRIDOR_NORTH_OUT),
    (CORRIDOR_EAST_IN, CORRIDOR_NORTH_OUT),
]

# The base a guard drives out from and is relieved back to.
GUARD_BASE = APRON

# The sixteen guard towers, each as the army-drivable edge nearest it and the position along that
# edge to park (metres from the edge start). Every one is round-trip reachable from the base by
# SUMO's own router; the four northern towers share the long perimeter road at distinct offsets.
# Found by projecting each tower (from the Google Earth survey) onto the nearest army edge and
# validating the round trip with duarouter.
TOWER_POSTS = [
    ("-26413411", 5.0), ("-26413426", 5.0), ("26413427", 62.0), ("26413459", 58.9),
    ("-26413460", 5.0), ("-26413425#5", 278.7), ("26413338#6", 381.0), ("-26413338#6", 848.2),
    ("26413338#5", 442.2), ("-26413274#2", 5.0), ("26413274#0", 16.4), ("-26413409#2", 6.2),
    ("26413409#1", 2409.9), ("26413409#1", 920.5), ("26413409#1", 538.1), ("26413409#1", 306.1),
]

# The southern fence line, west to east, for the roving perimeter-shadow anomaly (which drives past
# the posts without stopping).
FENCE_LINE = ["26413338#6", "-26413425#5", "-26413460", "26413459",
              "26413427", "-26413426", "-26413411"]

VEHICLE_TYPES = """\
    <!-- Civilian traffic on the public coastal corridor (passenger class; neutral affiliation). -->
    <vType id="civ_car" vClass="passenger" length="4.4" maxSpeed="35" color="0.80,0.80,0.82"/>
    <vType id="civ_pickup" vClass="passenger" length="5.2" width="1.95" maxSpeed="33" color="0.55,0.58,0.60"/>
    <vType id="civ_taxi" vClass="taxi" length="4.5" maxSpeed="35" color="0.90,0.80,0.20"/>
    <vType id="civ_truck" vClass="truck" length="10.0" maxSpeed="25" color="0.60,0.50,0.35"/>
    <vType id="civ_bus" vClass="bus" length="12.0" maxSpeed="24" color="0.85,0.85,0.70"/>
    <vTypeDistribution id="civ_mix" vTypes="civ_car civ_pickup civ_taxi civ_truck civ_bus"
                       probabilities="0.50 0.22 0.12 0.10 0.06"/>

    <!-- Port-cleared traffic that passes a checkpoint to reach the ferry and quays (authority
         class; neutral affiliation: ferry passengers and port freight, access-controlled). -->
    <vType id="port_vehicle" vClass="authority" length="4.6" maxSpeed="30" color="0.55,0.70,0.85"/>
    <vType id="port_truck" vClass="authority" length="9.0" maxSpeed="24" color="0.45,0.60,0.75"/>
    <vTypeDistribution id="port_mix" vTypes="port_vehicle port_truck" probabilities="0.7 0.3"/>

    <!-- Naval / base traffic inside the wire (army class; friendly affiliation). -->
    <vType id="mil_jeep" vClass="army" length="4.8" width="2.0" maxSpeed="33" color="0.30,0.38,0.25"/>
    <vType id="mil_truck" vClass="army" length="7.5" width="2.4" maxSpeed="24" color="0.28,0.34,0.22"/>
    <vType id="guard" vClass="army" length="4.8" width="2.0" maxSpeed="30" color="0.35,0.45,0.30"/>
    <vTypeDistribution id="mil_mix" vTypes="mil_jeep mil_truck" probabilities="0.6 0.4"/>

    <!-- Anomalies (unknown affiliation; flagged as ground truth in the labels sidecar). -->
    <vType id="anomaly_probe" vClass="passenger" length="4.4" maxSpeed="35" color="1.00,0.45,0.00"/>
    <vType id="anomaly_escort" vClass="army" length="6.0" width="2.3" maxSpeed="28" color="1.00,0.10,0.10"/>
    <vType id="anomaly_shadow" vClass="army" length="4.6" maxSpeed="33" color="1.00,0.20,0.60"
           speedFactor="0.45" speedDev="0"/>
    <vType id="anomaly_staybehind" vClass="authority" length="4.6" maxSpeed="30" color="1.00,0.30,0.00"/>
"""

# CoT affiliation per vehicle-type id: civilian and port traffic neutral, military friendly, every
# anomaly unknown.
AFFILIATION_BY_TYPE = {
    "civ_car": "n", "civ_pickup": "n", "civ_taxi": "n", "civ_truck": "n", "civ_bus": "n",
    "port_vehicle": "n", "port_truck": "n",
    "mil_jeep": "f", "mil_truck": "f", "guard": "f",
    "anomaly_probe": "u", "anomaly_escort": "u",
    "anomaly_shadow": "u", "anomaly_staybehind": "u",
}

# Ferry sailings (local hours) -- daylight only, none overnight.
FERRY_HOURS = [6, 8, 10, 12, 14, 16, 18]
# Airfield shift changes.
SHIFT_HOURS = [7, 15, 23]


def diurnal_corridor_flows(days: int) -> list[AmbientFlow]:
    """Civilian corridor traffic with a day/night rhythm, per pair, per day."""
    # (start_hour, end_hour, vehicles/hour) -- quiet pre-dawn, busy daytime, moderate evening.
    windows = [(0, 6, 20), (6, 10, 180), (10, 16, 120), (16, 20, 200), (20, 24, 50)]
    flows = []
    for day in range(days):
        for pair_index, (src, dst) in enumerate(CORRIDOR_PAIRS):
            for start, end, rate in windows:
                begin = day * DAY + start * HOUR
                flows.append(AmbientFlow(
                    flow_id=f"corridor_d{day}_p{pair_index}_h{start}",
                    from_edge=src, to_edge=dst, vehicles_per_hour=rate,
                    vehicle_type="civ_mix", begin=begin, end=day * DAY + end * HOUR))
    return flows


def ferry_pulse_flows(days: int) -> list[AmbientFlow]:
    """Each sailing draws a pulse in from the corridor and releases one out, over ~12 minutes."""
    flows = []
    for day in range(days):
        for hour in FERRY_HOURS:
            begin = day * DAY + hour * HOUR
            flows.append(AmbientFlow(
                flow_id=f"ferry_in_d{day}_h{hour}", from_edge=CORRIDOR_EAST_IN, to_edge=FERRY,
                vehicles_per_hour=600, vehicle_type="port_mix", begin=begin, end=begin + 12 * 60))
            flows.append(AmbientFlow(
                flow_id=f"ferry_out_d{day}_h{hour}", from_edge=FERRY, to_edge=CORRIDOR_EAST_OUT,
                vehicles_per_hour=600, vehicle_type="port_mix",
                begin=begin + 30 * 60, end=begin + 42 * 60))
    return flows


def shift_change_flows(days: int) -> list[AmbientFlow]:
    """Base traffic surges through the airfield gate at each shift change."""
    flows = []
    for day in range(days):
        for hour in SHIFT_HOURS:
            begin = day * DAY + hour * HOUR
            flows.append(AmbientFlow(
                flow_id=f"shift_in_d{day}_h{hour}", from_edge=CORRIDOR_WEST_IN, to_edge=APRON,
                vehicles_per_hour=240, vehicle_type="mil_mix", begin=begin, end=begin + 20 * 60))
            flows.append(AmbientFlow(
                flow_id=f"shift_out_d{day}_h{hour}", from_edge=APRON, to_edge=CORRIDOR_NORTH_OUT,
                vehicles_per_hour=240, vehicle_type="mil_mix",
                begin=begin + 15 * 60, end=begin + 35 * 60))
    return flows


def tower_postings(days: int, no_show_day: int, no_show_hour: int,
                   no_show_tower: int) -> list[ScheduledVehicle]:
    """Man every tower each shift: a guard drives out, parks on the roadside, is relieved next shift.

    At each shift change a guard is dispatched to each of the sixteen towers; it drives from the
    base to the tower's road point, parks at the kerb for the length of the shift, then drives back
    -- so at any hour there is a guard parked at every post, and the shift change is a wave of
    arrivals and departures across the fence. The parking is off the running lane, so a guard does
    not obstruct the perimeter road while it sits.

    The no-show anomaly is one guard, on one shift of one day, simply not dispatched: that tower
    stands unmanned while the other fifteen are relieved as usual. It is a missing event, so it has
    no vehicle to flag -- it is recorded in the labels sidecar as a described gap instead.
    """
    postings = []
    shift = 8 * HOUR
    for day in range(days):
        for hour in SHIFT_HOURS:
            depart = day * DAY + hour * HOUR
            for tower, (edge, pos) in enumerate(TOWER_POSTS):
                if day == no_show_day and hour == no_show_hour and tower == no_show_tower:
                    continue                    # the no-show: this post is not manned this shift
                postings.append(ScheduledVehicle(
                    veh_id=f"guard_d{day}_h{hour}_t{tower}", vehicle_type="guard",
                    depart=depart, from_edge=GUARD_BASE, to_edge=GUARD_BASE, via=(edge,),
                    stops=(ScheduleStop(lane=f"{edge}_0", end_pos=pos, duration=shift,
                                        parking=True),)))
    return postings


def routine_hauls(days: int) -> list[ScheduledVehicle]:
    """Light air-freight runs from the apron to the port, a few a day."""
    hauls = []
    for day in range(days):
        for k, hour in enumerate((9, 13, 17)):
            hauls.append(ScheduledVehicle(
                veh_id=f"haul_d{day}_{k}", vehicle_type="mil_truck",
                depart=day * DAY + hour * HOUR, from_edge=APRON, to_edge=FERRY))
    return hauls


def anomaly_vehicles(days: int) -> list[ScheduledVehicle]:
    """The anomalies that are individual vehicles (the guard no-show is an absence, in the labels)."""
    out = []

    # Escort-to-drydock (day 3, 10:00): a five-vehicle military escort from the apron to the
    # drydock, in tight formation. This is both the excess-escort and the drydock-spike signature.
    if days > 3:
        base = 3 * DAY + 10 * HOUR
        for i in range(5):
            out.append(ScheduledVehicle(
                veh_id=f"escort_{i}", vehicle_type="anomaly_escort", depart=base + i * 4,
                from_edge=APRON, to_edge=DRYDOCK, marked=True))

    # Gate probe (day 2 and day 5): a civilian vehicle approaches the airfield sentry on the public
    # side, waits five minutes without entering, and leaves.
    for day in (2, 5):
        if days > day:
            out.append(ScheduledVehicle(
                veh_id=f"probe_d{day}", vehicle_type="anomaly_probe",
                depart=day * DAY + 11 * HOUR + (day * 137),
                from_edge=CORRIDOR_WEST_IN, to_edge=CORRIDOR_EAST_OUT, via=(PORT_GATE_APPROACH,),
                stops=(ScheduleStop(lane=f"{PORT_GATE_APPROACH}_0", end_pos=30.0, duration=300),),
                marked=True))

    # Perimeter shadow (day 6, 02:30): a vehicle slowly follows the fence line when nothing else
    # moves, and does not stop at any post.
    if days > 6:
        out.append(ScheduledVehicle(
            veh_id="shadow", vehicle_type="anomaly_shadow",
            depart=6 * DAY + 2 * HOUR + 30 * 60, from_edge=GUARD_BASE, to_edge=GUARD_BASE,
            via=tuple(FENCE_LINE), marked=True))

    # Ferry stay-behind (day 1, 08:00 sailing): a vehicle arrives at the ferry and never leaves.
    if days > 1:
        remaining = days * DAY - (1 * DAY + 8 * HOUR + 600)
        out.append(ScheduledVehicle(
            veh_id="staybehind", vehicle_type="anomaly_staybehind",
            depart=1 * DAY + 8 * HOUR, from_edge=CORRIDOR_EAST_IN, to_edge=FERRY,
            stops=(ScheduleStop(lane=f"{FERRY}_0", end_pos=20.0, duration=remaining, parking=True),),
            marked=True))
    return out


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out-dir", default=os.path.join(_REPO, "Import"),
                        help="where the network, routes, config and labels go (default: carla/Import)")
    parser.add_argument("--osm", default=SOURCE_OSM,
                        help="clipped OpenStreetMap extract the CARLA world was built from")
    parser.add_argument("--days", type=int, default=7,
                        help="length of the run in days (default 7; use 1 for a GUI preview)")
    parser.add_argument("--no-show-day", type=int, default=4,
                        help="day on which one tower is left unmanned for a shift (default 4)")
    parser.add_argument("--no-show-hour", type=int, default=7,
                        help="shift hour (7, 15 or 23) whose guard fails to appear (default 7)")
    parser.add_argument("--no-show-tower", type=int, default=3,
                        help="which tower, 0-15, is left unmanned (default 3)")
    parser.add_argument("--step-length", type=float, default=1.0,
                        help="simulation step in seconds; a week of pattern-of-life does not need "
                             "physics fidelity (default 1.0)")
    parser.add_argument("--seed", type=int, default=42, help="random seed (default 42)")
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

    builder = SumoScenarioBuilder(installation.netconvert, installation.proj_data)
    out_dir = args.out_dir
    network_name = f"{MAP_NAME}.net.xml"
    network_path = os.path.join(out_dir, network_name)

    if args.reuse_network:
        logging.info("reusing network %s", network_path)
    else:
        builder.build_network(args.osm, network_path, NETCONVERT_SETTINGS)
        # Turn the OSM-private roads into an army/authority-only interior; the gate junctions where a
        # public road meets a private one become the only crossing points between the two populations.
        builder.restrict_private_roads(network_path, args.osm, allow="army authority")
    network = RoadNetwork.from_file(network_path)

    end_time = args.days * DAY
    flows = (diurnal_corridor_flows(args.days) + ferry_pulse_flows(args.days)
             + shift_change_flows(args.days))
    scheduled = (tower_postings(args.days, args.no_show_day, args.no_show_hour,
                                args.no_show_tower)
                 + routine_hauls(args.days) + anomaly_vehicles(args.days))

    pol = SumoPatternOfLifeBuilder()
    routes_name = f"{SCENARIO_NAME}.rou.xml"
    marked = pol.write_routes(os.path.join(out_dir, routes_name), network, VEHICLE_TYPES,
                              flows, scheduled, end_time, title=MAP_NAME)
    config_path = builder.write_config(os.path.join(out_dir, f"{SCENARIO_NAME}.sumocfg"),
                                       network_name, routes_name, end_time, args.step_length,
                                       args.seed)

    # The telemetry tool reads this to label the dataset: which vehicles are anomalies, and which
    # affiliation each vehicle type carries. The guard no-show has no vehicle -- it is an absence --
    # so it is recorded as a described gap: which tower, and the window during which it stood
    # unmanned while the others were relieved.
    no_show_edge, no_show_pos = TOWER_POSTS[args.no_show_tower]
    gap_begin = args.no_show_day * DAY + args.no_show_hour * HOUR
    anomaly_notes = []
    if args.days > args.no_show_day:
        anomaly_notes.append({
            "kind": "guard_no_show", "tower_index": args.no_show_tower,
            "edge": no_show_edge, "edge_pos_m": no_show_pos,
            "begin_s": gap_begin, "end_s": gap_begin + 8 * HOUR,
            "note": "one tower left unmanned for a shift; the detectable signal is a missing guard "
                    "arrival while the other fifteen towers are relieved as usual"})
    labels_path = os.path.join(out_dir, f"{SCENARIO_NAME}.labels.json")
    with open(labels_path, "w", encoding="utf-8") as handle:
        json.dump({"marked_ids": marked, "affiliation_by_type": AFFILIATION_BY_TYPE,
                   "anomaly_notes": anomaly_notes}, handle, indent=1)

    logging.info("network  %s", network_path)
    logging.info("         %d edges, %.1f km of road", len(network.edge_ids),
                 sum(network.length_of(e) for e in network.edge_ids) / 1000.0)
    logging.info("routes   %s", os.path.join(out_dir, routes_name))
    logging.info("         %d flows, %d scheduled vehicles, %d planted anomalies over %d day(s)",
                 len(flows), len(scheduled), len(marked), args.days)
    logging.info("labels   %s  (%d marked ids)", labels_path, len(marked))
    logging.info("config   %s  (ends at %d s = %d day(s))", config_path, end_time, args.days)
    return 0


if __name__ == "__main__":
    sys.exit(main())
