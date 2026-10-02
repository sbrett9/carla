#!/usr/bin/env python3
"""Write, and compile, the dwell scenario for the Arapahoe / I-25 world.

Arapahoe I-25 is a 1.9 x 0.95 km extract of Arapahoe County, Colorado, a tall narrow strip running
along Interstate 25 where East Arapahoe Road crosses it. I-25 -- named South Valley Highway in
OpenStreetMap -- runs the full length of the map at 29.1 m/s across five and six lanes, Arapahoe
Road crosses it east to west at 17.9 m/s across as many as seven, and South Yosemite Street runs
north from Arapahoe up the west side.

The scenario this writes:

  * one marked vehicle enters at the southern end of I-25 heading north, leaves at the Arapahoe
    interchange, runs west along Arapahoe Road, turns north up South Yosemite Street and pulls off
    the roadway under the Yosemite Street road bridge, where it waits 30 minutes before returning
    to I-25 and leaving at the northern end of the map,
  * heavy freeway traffic in both directions with a wide spread of speeds, so faster vehicles work
    their way to the left and slower ones sit right,
  * an incident that closes five of the six northbound lanes for three minutes, which backs traffic
    up behind it and lets it drain again once the lanes reopen,
  * dense traffic on Arapahoe Road carrying a much larger share of vans and trucks than the freeway,
  * residential streets on the west and south edges feeding commuters onto the freeway and taking
    them home again.

The intent is that everything on the map behaves ordinarily except the vehicle parked under the
bridge, which is the only thing doing something a traffic model would not produce on its own.

**This script writes a specification, not SUMO XML** (`07_Scenario_Authoring.md` D7.2), and compiles
it with the scenario compiler, so the generated scenario faces every check a hand-written one does.
The specification, `<out-dir>/Arapahoe_I25_UnderpassDwell.scenario.json`, names every road it uses
as a place: the four ends of I-25 as the world's own gateways on South Valley Highway, the dwell as
the point it was surveyed at, and every other road as the edge it was reconnoitred on. The compiled
package is written beside it: the `.sumocfg`, the routed `.rou.xml`, the incident as a SUMO rerouter
in `.add.xml`, the world package's own network byte for byte, the supervision plan, the lock and the
resolution report. The marked vehicle keeps its SUMO id, `marked`.

**Bodies.** Every vehicle is a body somebody measured: each type takes its length, width and height
from the catalogue's spawn-and-measure sweep, so the vehicle SUMO reserves road for is the vehicle
CARLA draws. The seven types this scenario was designed with keep their driving models -- the spread
of `speedFactor` and the lane-change parameters that sort the freeway's lanes by speed -- and each is
now the class of the bodies VEHICLE_CLASSES gives it, set against the size it was designed around.
The content build has no articulated lorry, so the semitrailer is the heaviest rigid one.

**Routes.** The compiler routes each flow once, where the SUMO-XML version let SUMO route every
vehicle as it entered, on travel times that follow the congestion. Ten flows are therefore held, by
a via, to the way most of their vehicles took when SUMO routed them one by one; without that the map
fills behind a deadlocked ramp. Measured in SUMO with the holds, the population is the one the
scenario was tuned to: peak 440 live vehicles and median 338, against 437 and 336; with the
compiler's three-second lane changes, peak 441 and median 345.

**Time.** Simulated second zero is 07:00 Mountain Daylight Time on 29 September 2026, the date of the
Bahonar pattern of life, in the morning peak; the run lasts 45 minutes.

**What a specification cannot carry.** The roadway under the bridge is one lane each way, and the
SUMO-XML version of this scenario let a driver cross the centre line to pass something stopped there
by naming the two directions as each other's opposite lanes -- an edit to the network. A compiled
scenario runs the world's network byte for byte, so the pairs are not applied: the marked vehicle
parks off the running lane, which is what keeps the underpass open, and under `--stop-in-lane` no
lane lets a driver cross the centre line to pass it.

Usage:
    python make_arapahoe_scenario.py [--dwell-minutes 30] [--out-dir ../../Import]
Then, from the output directory:
    sumo-gui -c Arapahoe_I25_UnderpassDwell.sumocfg
"""
import argparse
import json
import logging
import os
import sys
from dataclasses import dataclass
from pathlib import Path

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402  (needs the path above)
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioVehicleMix import VehicleClassSpec  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

MAP_NAME = "Arapahoe_I25"
SCENARIO_NAME = f"{MAP_NAME}_UnderpassDwell"

# The SUMO this repository stages and the distribution ships, the release every shipped world was
# converted by. The compiler refuses a routing SUMO of another release (check 6).
STAGED_SUMO = os.path.join(_REPO, "Build", "sumo-install")

# The measured vehicle catalogue every vehicle type in this scenario is sized from.
CATALOGUE = os.path.join(_REPO, "CarlaControl", "catalogue", "vehicles.catalogue.json")

# The world package, which carries the SUMO network this scenario runs, byte for byte.
WORLD_PACKAGE = os.path.join(_REPO, "Build", "world-packages", f"{MAP_NAME}.cwp")

# What simulated second zero means in civil time at the site: 07:00 Mountain Daylight Time on 29
# September 2026, the date the Bahonar pattern of life starts on, so the two are comparable. The
# offset is the declaration, daylight saving included; the zone name is carried for a reader and
# never resolved.
EPOCH = {
    "epoch_version": 1,
    "civil_datetime": "2026-09-29T07:00:00-06:00",
    "utc_offset_hours": -6,
    "utc_datetime": "2026-09-29T13:00:00Z",
    "calendar_advances": True,
    "dst_in_effect": True,
    "time_zone_id": "America/Denver",
    "note": "Arapahoe County, Colorado: simulated second zero is 07:00 Mountain Daylight Time on "
            "29 September 2026, in the morning peak",
}

# The sun holds still for each capture window, at the instant it opens: one lighting condition per
# window. An authored default the operator may override at run start.
ILLUMINATION = {"illumination_version": 1, "policy": "freeze_at_window_start"}

# Every place the scenario names. I-25 is South Valley Highway; the two carriageways enter and leave
# at opposite corners because the freeway runs diagonally across the extract, and each end is the
# world's one gateway on that side carrying that street. Everything else is the edge (an OSM way id;
# a leading '-' is the way's reverse direction) it was reconnoitred on. "_in" enters the world from
# the road named and "_out" leaves it.
PLACES = {
    "i25_north_in": {"gateway": "south", "travel": "in", "street": "South Valley Highway"},
    "i25_north_out": {"gateway": "north", "travel": "out", "street": "South Valley Highway"},
    "i25_south_in": {"gateway": "north", "travel": "in", "street": "South Valley Highway"},
    "i25_south_out": {"gateway": "south", "travel": "out", "street": "South Valley Highway"},
    # Northbound I-25 south of the Arapahoe interchange, five lanes, where a driver learns of the
    # incident; and the six-lane stretch north of it the incident closes, which every through
    # vehicle northbound is held to.
    "i25_north_short_of_arapahoe": {"edge": "907700111"},
    "i25_north_past_arapahoe": {"edge": "1001791386"},
    # East Arapahoe Road at the map's east and west edges.
    "arapahoe_east_in": {"edge": "131933384"},
    "arapahoe_east_out": {"edge": "427819527"},
    "arapahoe_west_in": {"edge": "427819540#0"},
    "arapahoe_west_out": {"edge": "427819541#0"},
    # Enough of the marked vehicle's route named for SUMO to reproduce it: off at the interchange,
    # west along Arapahoe Road, north up Yosemite, then the roadway under the bridge. Everything
    # between is left to the router, which is why the network's own connections decide the ramps
    # rather than a hand-copied list.
    "arapahoe_westbound_west_of_i25": {"edge": "629675735"},
    # Where flows are held to the way most of their vehicles took (AMBIENT_FLOWS): the northbound
    # exit's direct ramp onto Arapahoe Road eastbound, and the South Xanthia Street cut-through
    # north of the estates.
    "i25_north_exit_to_arapahoe_east": {"edge": "131933449#0"},
    "xanthia_street_northbound": {"edge": "-17003602#7"},
    "arapahoe_westbound_at_yosemite": {"edge": "427819537#1"},
    "yosemite_northbound_from_arapahoe": {"edge": "1026993839#0"},
    # The ground-level roadway under the bridge carrying South Yosemite Street, one lane each way.
    "underpass_westbound": {"edge": "218965860#0"},
    "underpass_eastbound": {"edge": "-223315781"},
    # Where the marked vehicle waits: 39.600357 N, 104.886490 W. Nothing there is tagged as a
    # tunnel -- South Yosemite Street is carried over on a bridge, and this is the roadway under it.
    # Measured on the world package: 0.09 m from the westbound lane, 88.6 m along it.
    "underpass_dwell": {"lat": 39.600357, "lon": -104.886490, "max_snap_m": 2.0,
                        "vclass": "passenger"},
    # South Yosemite Street at the map's north and south edges.
    "yosemite_north_in": {"edge": "427819547"},
    "yosemite_north_out": {"edge": "-427819547"},
    "yosemite_south_in": {"edge": "-629629570"},
    "yosemite_south_out": {"edge": "629629570"},
    # The surrounding street grid: Clinton, Caley, Peakview, Boston Court, Arbor, Willow, Wabash.
    "clinton_in": {"edge": "-629634784"},
    "clinton_out": {"edge": "629634784"},
    "caley_in": {"edge": "132833790"},
    "caley_out": {"edge": "629653938"},
    "peakview_west_in": {"edge": "16999218"},
    "peakview_east_out": {"edge": "292396861#1"},
    "peakview_east_in": {"edge": "633447921"},
    "peakview_west_out": {"edge": "46107902#0"},
    "boston_court_in": {"edge": "17000739"},
    "arbor_in": {"edge": "16998684#0"},
    "willow_in": {"edge": "550665536#0"},
    "wabash_in": {"edge": "427479206"},
    # The residential streets on the west and south edges.
    "davies_avenue_in": {"edge": "16993828#0"},
    "davies_avenue_out": {"edge": "-16993828#0"},
    "costilla_place_in": {"edge": "16996647"},
    "costilla_place_out": {"edge": "-16996647"},
    "costilla_avenue_in": {"edge": "16996898"},
    "costilla_avenue_out": {"edge": "-16996898"},
    "davies_place_in": {"edge": "17001552#0"},
    "davies_place_out": {"edge": "-17001552#1"},
    "briarwood_place_in": {"edge": "17003522"},
    "briarwood_place_out": {"edge": "-17003522"},
    "briarwood_boulevard_in": {"edge": "17007347#0"},
    "briarwood_boulevard_out": {"edge": "-17007347#0"},
    "briarwood_avenue_in": {"edge": "224876698"},
    "briarwood_avenue_out": {"edge": "-224876698"},
    "easter_place_in": {"edge": "17006662#0"},
    "easter_place_out": {"edge": "-17006662#0"},
    "fremont_circle_in": {"edge": "-16991914"},
    "fremont_circle_out": {"edge": "16991914"},
    "xanthia_street_in": {"edge": "-17003598#2"},
    "xanthia_way_in": {"edge": "-17006541"},
    "alton_way_in": {"edge": "-17003147#17"},
    "alton_way_out": {"edge": "17003147#12"},
}

# The marked vehicle's anchors, in driving order.
DWELL_VIA = ("arapahoe_westbound_west_of_i25", "arapahoe_westbound_at_yosemite",
             "yosemite_northbound_from_arapahoe", "underpass_westbound")


@dataclass(frozen=True)
class Flow:
    """One stream of ambient traffic between two places, drawn from one of the named mixes."""

    flow_id: str
    origin: str
    destination: str
    vehicles_per_hour: int
    mix: str
    via: tuple[str, ...] = ()


# Ambient traffic. The rates are what the map was tuned to and measured at (peak 437 live vehicles,
# median 336, doc 10 section 3.2): it is the heaviest population of any shipped scenario.
#
# The compiler routes each flow once, and every vehicle of it drives that route (D7.8). The SUMO-XML
# scenario these rates were tuned in left SUMO to route each vehicle as it entered, on travel times
# that follow the congestion, so a flow spread itself over several routes and kept off a road that
# had jammed. Routed once, ten flows were sent a way most of their vehicles had not taken, and measured
# on the world package the map filled -- median 430 live vehicles against 336 -- behind one ramp that
# deadlocked and one collector carrying more than it could discharge. So where most of a flow's
# vehicles took another way at a fork, the flow is held to that way by a via. The shares quoted are
# those of SUMO routing this scenario's own vehicles as they entered, measured over the whole run.
AMBIENT_FLOWS = [
    # Interstate 25 through traffic, the bulk of the map's movement. Six lanes northbound and five
    # southbound at 65 mph carry this comfortably: it is the signalised Arapahoe Road corridor
    # that limits this map, not the freeway, so the freeway is where the volume goes.
    Flow("i25_north_through", "i25_north_in", "i25_north_out", 3300, "freeway_mix",
         via=("i25_north_past_arapahoe",)),
    Flow("i25_south_through", "i25_south_in", "i25_south_out", 3000, "freeway_mix"),
    # Freeway traffic that turns at the Arapahoe Road interchange rather than passing straight
    # through. Everything here has to clear a signal, so these rates are what the junctions can
    # actually discharge; set much above this the corridor fills up and never recovers.
    # 81% of its vehicles took the direct ramp onto Arapahoe Road eastbound.
    Flow("i25_north_to_arapahoe_east", "i25_north_in", "arapahoe_east_out", 150, "freeway_mix",
         via=("i25_north_exit_to_arapahoe_east",)),
    Flow("i25_north_to_arapahoe_west", "i25_north_in", "arapahoe_west_out", 130, "freeway_mix"),
    Flow("i25_south_to_arapahoe_east", "i25_south_in", "arapahoe_east_out", 130, "freeway_mix"),
    Flow("i25_south_to_arapahoe_west", "i25_south_in", "arapahoe_west_out", 120, "freeway_mix"),
    Flow("arapahoe_east_to_i25_north", "arapahoe_east_in", "i25_north_out", 140, "freeway_mix"),
    Flow("arapahoe_west_to_i25_north", "arapahoe_west_in", "i25_north_out", 130, "freeway_mix"),
    # All of its vehicles took the southbound ramp from Arapahoe Road west of the freeway, whose
    # single lane becomes I-25's added sixth lane. Routed once, the flow takes the loop ramp that
    # merges into the five lanes already carrying 3000 vehicles an hour instead, and measured, that
    # ramp deadlocks within ten minutes and never clears: with teleporting forbidden the queue backs
    # up Arapahoe Road for the rest of the run.
    Flow("arapahoe_east_to_i25_south", "arapahoe_east_in", "i25_south_out", 120, "freeway_mix",
         via=("arapahoe_westbound_west_of_i25",)),
    Flow("arapahoe_west_to_i25_south", "arapahoe_west_in", "i25_south_out", 110, "freeway_mix"),
    # East Arapahoe Road, crossing the map under the freeway.
    Flow("arapahoe_east_to_west", "arapahoe_east_in", "arapahoe_west_out", 350, "arterial_mix"),
    Flow("arapahoe_west_to_east", "arapahoe_west_in", "arapahoe_east_out", 330, "arterial_mix"),
    # South Yosemite Street, the north-south arterial on the west side.
    Flow("yosemite_north_to_south", "yosemite_north_in", "yosemite_south_out", 130, "arterial_mix"),
    Flow("yosemite_south_to_north", "yosemite_south_in", "yosemite_north_out", 120, "arterial_mix"),
    Flow("yosemite_north_to_arapahoe_east", "yosemite_north_in", "arapahoe_east_out", 90,
         "arterial_mix"),
    Flow("arapahoe_east_to_yosemite_north", "arapahoe_east_in", "yosemite_north_out", 85,
         "arterial_mix"),
    # The surrounding street grid.
    Flow("clinton_to_arapahoe_west", "clinton_in", "arapahoe_west_out", 75, "arterial_mix"),
    Flow("arapahoe_west_to_clinton", "arapahoe_west_in", "clinton_out", 70, "arterial_mix"),
    Flow("caley_to_arapahoe_east", "caley_in", "arapahoe_east_out", 65, "arterial_mix"),
    Flow("arapahoe_east_to_caley", "arapahoe_east_in", "caley_out", 60, "arterial_mix"),
    Flow("peakview_west_to_east", "peakview_west_in", "peakview_east_out", 75, "arterial_mix"),
    Flow("peakview_east_to_west", "peakview_east_in", "peakview_west_out", 70, "arterial_mix"),
    Flow("boston_court_to_arapahoe_east", "boston_court_in", "arapahoe_east_out", 45,
         "arterial_mix"),
    Flow("arbor_to_arapahoe_east", "arbor_in", "arapahoe_east_out", 40, "arterial_mix"),
    Flow("willow_to_yosemite_south", "willow_in", "yosemite_south_out", 45, "arterial_mix"),
    Flow("wabash_to_arapahoe_west", "wabash_in", "arapahoe_west_out", 40, "arterial_mix"),
    # Local traffic that uses the roadway under the Yosemite Street bridge -- the same stretch the
    # marked vehicle stops on. Without these the underpass carries nothing, and the parked vehicle
    # is an outlier with nothing to be an outlier among.
    Flow("underpass_north_to_south", "yosemite_north_in", "yosemite_south_out", 120, "arterial_mix",
         via=("underpass_westbound",)),
    Flow("underpass_south_to_north", "yosemite_south_in", "yosemite_north_out", 110, "arterial_mix",
         via=("underpass_eastbound",)),
    Flow("underpass_caley_to_yosemite", "caley_in", "yosemite_south_out", 80, "arterial_mix",
         via=("underpass_westbound",)),
    # Commuters: out of the residential streets on the west and south edges and onto the freeway,
    # and the same trips in reverse coming home. Those leaving the estates split between South
    # Yosemite Street and the South Xanthia Street cut-through; the flows most of whose vehicles took
    # Xanthia Street (58% to 61% of them) are held to it, and the rest (47%) drive Yosemite, which
    # leaves the two roads near the split SUMO gave them.
    Flow("davies_avenue_to_i25_north", "davies_avenue_in", "i25_north_out", 32, "residential_mix",
         via=("xanthia_street_northbound",)),
    Flow("i25_south_to_davies_avenue", "i25_south_in", "davies_avenue_out", 30, "residential_mix"),
    Flow("costilla_place_to_i25_north", "costilla_place_in", "i25_north_out", 27,
         "residential_mix"),
    Flow("i25_south_to_costilla_place", "i25_south_in", "costilla_place_out", 25,
         "residential_mix"),
    Flow("costilla_avenue_to_i25_south", "costilla_avenue_in", "i25_south_out", 27,
         "residential_mix"),
    Flow("i25_north_to_costilla_avenue", "i25_north_in", "costilla_avenue_out", 25,
         "residential_mix"),
    Flow("davies_place_to_i25_north", "davies_place_in", "i25_north_out", 25, "residential_mix",
         via=("xanthia_street_northbound",)),
    Flow("i25_south_to_davies_place", "i25_south_in", "davies_place_out", 22, "residential_mix"),
    Flow("briarwood_place_to_i25_north", "briarwood_place_in", "i25_north_out", 27,
         "residential_mix"),
    Flow("i25_south_to_briarwood_place", "i25_south_in", "briarwood_place_out", 25,
         "residential_mix"),
    Flow("briarwood_boulevard_to_i25_south", "briarwood_boulevard_in", "i25_south_out", 27,
         "residential_mix"),
    Flow("i25_north_to_briarwood_boulevard", "i25_north_in", "briarwood_boulevard_out", 25,
         "residential_mix"),
    Flow("briarwood_avenue_to_arapahoe_east", "briarwood_avenue_in", "arapahoe_east_out", 32,
         "residential_mix", via=("xanthia_street_northbound",)),
    Flow("arapahoe_east_to_briarwood_avenue", "arapahoe_east_in", "briarwood_avenue_out", 30,
         "residential_mix"),
    Flow("easter_place_to_i25_north", "easter_place_in", "i25_north_out", 22, "residential_mix",
         via=("xanthia_street_northbound",)),
    Flow("i25_south_to_easter_place", "i25_south_in", "easter_place_out", 20, "residential_mix"),
    Flow("fremont_circle_to_i25_north", "fremont_circle_in", "i25_north_out", 25,
         "residential_mix", via=("xanthia_street_northbound",)),
    Flow("i25_south_to_fremont_circle", "i25_south_in", "fremont_circle_out", 22,
         "residential_mix"),
    Flow("xanthia_street_to_arapahoe_east", "xanthia_street_in", "arapahoe_east_out", 22,
         "residential_mix", via=("xanthia_street_northbound",)),
    Flow("xanthia_way_to_i25_north", "xanthia_way_in", "i25_north_out", 20, "residential_mix",
         via=("xanthia_street_northbound",)),
    Flow("alton_way_to_arapahoe_east", "alton_way_in", "arapahoe_east_out", 25, "residential_mix",
         via=("xanthia_street_northbound",)),
    Flow("arapahoe_east_to_alton_way", "arapahoe_east_in", "alton_way_out", 22, "residential_mix"),
]

# Every measured passenger body but the Nissan Patrol, the sport utility below, and the Jeep
# Wrangler, which is the marked vehicle's alone; the taxi and the police car are liveried and are not
# ordinary traffic.
CARS = ("vehicle.ue4.audi.tt", "vehicle.mini.cooper", "vehicle.ue4.bmw.grantourer",
        "vehicle.ue4.mercedes.ccc", "vehicle.ue4.ford.mustang", "vehicle.lincoln.mkz",
        "vehicle.dodge.charger", "vehicle.ue4.chevrolet.impala", "vehicle.ue4.ford.crown")

# What the traffic is made of. The behaviour is each type's driving model as this scenario was
# designed and measured with it, copied through verbatim; the dimensions are the measured bodies'.
# The freeway types exist to produce a speed gradient across the lanes: SUMO has no per-lane speed
# setting, so the gradient has to come from what the drivers want and how willing they are to move
# over for it. lcKeepRight above 1 pushes a type right when it is not overtaking, lcSpeedGain above 1
# makes it change lanes for speed more readily. A class's share is zero because every flow draws
# from one of the named mixes below, never from a whole-scenario mix. A colour is for sumo-gui only.
#
# Two wheelers are outside the vehicle mapping contract and no class below declares one: a riderless
# motorcycle is not something worth rendering, and this content build registers no two wheeled
# blueprint to render it with.
VEHICLE_CLASSES = (
    VehicleClassSpec(
        class_id="car_quick", blueprints=CARS, sumo_vclass="passenger",
        behaviour={"maxSpeed": "60", "speedFactor": "normc(1.18,0.06,1.05,1.35)",
                   "lcSpeedGain": "3.0", "lcKeepRight": "0.3", "lcAssertive": "1.5",
                   "sigma": "0.4", "tau": "0.9"},
        gui_shape="passenger", gui_colour="#D9D9E6",
        note="Quick freeway drivers, who work their way to the left lanes. A driver, not a body: "
             "they draw the same nine measured cars as car, so a fast vehicle is not told apart by "
             "its shape. Designed around a 4.6 m car; the nine bodies run 4.18 m to 5.37 m and "
             "average 4.82 m."),
    VehicleClassSpec(
        class_id="car", blueprints=CARS, sumo_vclass="passenger",
        behaviour={"maxSpeed": "55", "speedFactor": "normc(1.02,0.06,0.90,1.15)",
                   "lcSpeedGain": "1.2", "lcKeepRight": "1.0", "sigma": "0.5", "tau": "1.1"},
        gui_shape="passenger", gui_colour="#B3B8C7",
        note="Ordinary cars: saloons, hatchbacks and coupes, the nine measured bodies car_quick "
             "draws too. Designed around a 4.6 m car; they average 4.82 m."),
    VehicleClassSpec(
        class_id="suv", blueprints=("vehicle.nissan.patrol",), sumo_vclass="passenger",
        behaviour={"maxSpeed": "52", "speedFactor": "normc(1.00,0.06,0.88,1.12)",
                   "lcSpeedGain": "1.0", "lcKeepRight": "1.5", "sigma": "0.5", "tau": "1.1"},
        gui_shape="passenger", gui_colour="#596673",
        note="The full-size sport utility: a 5.59 x 2.15 m body against the 5.0 x 1.95 m this "
             "type was designed around, so every SUV is 0.59 m longer than intended. One body, so "
             "every SUV on the map looks the same: a property of the content."),
    VehicleClassSpec(
        class_id="van", blueprints=("vehicle.sprinter.mercedes",), sumo_vclass="delivery",
        behaviour={"maxSpeed": "45", "speedFactor": "normc(0.94,0.05,0.82,1.05)",
                   "lcSpeedGain": "0.6", "lcKeepRight": "2.5", "sigma": "0.5", "tau": "1.2"},
        gui_shape="delivery", gui_colour="#E6E6E6",
        note="A panel van measuring 5.92 m against the 5.9 m designed around. The ambulance is a "
             "van too, and liveried, so it is not drawn as one."),
    VehicleClassSpec(
        class_id="truck", blueprints=("vehicle.carlacola.actors",), sumo_vclass="truck",
        behaviour={"maxSpeed": "35", "speedFactor": "normc(0.86,0.04,0.78,0.95)",
                   "lcSpeedGain": "0.4", "lcKeepRight": "4.0", "sigma": "0.5", "tau": "1.4"},
        gui_shape="truck", gui_colour="#99734D",
        note="The two-axle box truck, 8.00 m, the catalogue's civilian lorry: 4.0 m shorter than "
             "the 12.0 m rigid lorry this type was designed around."),
    VehicleClassSpec(
        class_id="semi", blueprints=("vehicle.carlamotors.european_hgv",), sumo_vclass="truck",
        behaviour={"maxSpeed": "32", "speedFactor": "normc(0.84,0.03,0.78,0.92)",
                   "lcSpeedGain": "0.3", "lcKeepRight": "5.0", "sigma": "0.5", "tau": "1.6"},
        gui_shape="truck", gui_colour="#735940",
        note="The content build has no articulated lorry, so the 16.5 m semitrailer this type was "
             "designed around is drawn as the heaviest rigid one, the three-axle heavy goods "
             "vehicle: 7.92 m, so each semitrailer reserves 8.6 m less road than designed. It keeps "
             "the slowest and most right-keeping driving model on the freeway."),
)

# The populations the flows draw from: each type's share of the corridor, as designed.
VEHICLE_MIXES = (
    ("freeway_mix", (("car_quick", 0.26), ("car", 0.35), ("suv", 0.20), ("van", 0.08),
                     ("truck", 0.07), ("semi", 0.04)),
     "Freeway mix: mostly cars, a realistic tail of heavy vehicles."),
    ("arterial_mix", (("car", 0.45), ("suv", 0.24), ("van", 0.20), ("truck", 0.11)),
     "Arterial mix: Arapahoe Road is lined with commercial frontage, so vans and box trucks make up "
     "a much larger share of it than they do of the freeway."),
    ("residential_mix", (("car", 0.58), ("suv", 0.33), ("van", 0.09)),
     "Residential mix: commuters, nothing heavy."),
)

# The marked vehicle's body: an ordinary private car nothing else on the map draws, so it can be
# followed by eye. The Wrangler is short, tall and open-topped, with a fixed paint scheme, and as
# common as anything on a Colorado road.
MARKED_BLUEPRINT = "vehicle.jeep.wrangler_rubicon"


class ArapahoeDwellSpecification:
    """The underpass dwell as a specification the scenario compiler compiles."""

    def __init__(self, dwell_seconds: float, depart: int, parking: bool, incident: dict | None,
                 traffic_scale: float, end_s: int, step_length_s: float, seed: int) -> None:
        self.dwell_seconds = dwell_seconds
        self.depart = depart
        self.parking = parking
        self.incident = incident
        self.traffic_scale = traffic_scale
        self.end_s = end_s
        self.step_length_s = step_length_s
        self.seed = seed

    def build(self, world_package: Path, network_text: str, catalogue: Path, base: Path) -> dict:
        """The specification, with every path relative to `base`, the directory it is written in."""
        minutes = self.dwell_seconds / 60.0
        specification = {
            "spec_version": 1,
            "scenario_id": SCENARIO_NAME,
            "scenario_name": "Arapahoe I-25 underpass dwell",
            "description": (
                f"One marked vehicle enters northbound on I-25, leaves at the Arapahoe interchange, "
                f"runs west on Arapahoe Road and north up South Yosemite Street, waits {minutes:g} "
                "minutes under the Yosemite Street bridge and leaves north on I-25, among heavy "
                "freeway traffic with a wide spread of speeds, dense arterial traffic heavy in vans "
                "and trucks, residential commuters on the west and south edges"
                + (", and an incident closing "
                   f"{len(self.incident['lanes'])} of the 6 northbound lanes for "
                   f"{(self.incident['end'] - self.incident['begin']) / 60.0:g} minutes"
                   if self.incident else "")
                + ". Written by CarlaControl/scripts/make_arapahoe_scenario.py; edit that, not "
                  "this."),
            "world": {"package": self._relative(world_package, base),
                      "network_fingerprint": NetworkFingerprint.of_text(network_text)},
            "epoch": EPOCH,
            "illumination": ILLUMINATION,
            "seeds": {"sumo": self.seed},
            "simulation": {"end": self.end_s, "step_length_s": self.step_length_s},
            "catalogue": self._relative(catalogue, base),
            "vehicle_classes": [self._vehicle_class(c) for c in (*VEHICLE_CLASSES, self.marked())],
            "vehicle_mixes": [{"id": mix_id, "shares": dict(shares), "note": note}
                              for mix_id, shares, note in VEHICLE_MIXES],
            "places": PLACES,
            "flows": [self._flow(flow) for flow in AMBIENT_FLOWS],
            "actors": [{
                "id": "marked", "type": "marked", "depart": self.depart,
                "from": "i25_north_in", "to": "i25_north_out", "via": list(DWELL_VIA),
                "stops": [{"place": "underpass_dwell", "duration": self.dwell_seconds,
                           "parking": self.parking}],
                "depart_lane": "best", "depart_speed": "max", "arrival_speed": "current",
            }],
        }
        if self.incident:
            specification["lane_closures"] = [self.incident]
        return specification

    @staticmethod
    def marked() -> VehicleClassSpec:
        """The marked vehicle: a class of one body, bound to a measurement by the same rule as the
        ambient traffic. It drives at the posted limit throughout: what makes it an outlier is where
        it stops, not how fast it goes."""
        return VehicleClassSpec(
            class_id="marked", blueprints=(MARKED_BLUEPRINT,), sumo_vclass="passenger",
            behaviour={"maxSpeed": "55", "speedFactor": "1.00", "speedDev": "0", "sigma": "0.20",
                       "tau": "1.20"},
            # Conspicuous in sumo-gui so the author can follow it there; the rendered colour is the
            # blueprint's own.
            gui_shape="passenger", gui_colour="#FF8C00",
            note="The marked vehicle, the Jeep Wrangler: 3.87 m against the 4.8 m it was designed "
                 "around. speedDev is zeroed so it holds the posted limit exactly.")

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
        return entry

    def _flow(self, flow: Flow) -> dict:
        rate = flow.vehicles_per_hour
        if self.traffic_scale != 1.0:
            rate = max(1, round(rate * self.traffic_scale))
        entry = {"id": flow.flow_id, "type": flow.mix, "from": flow.origin,
                 "to": flow.destination, "vehs_per_hour": rate, "begin": 0, "end": self.end_s,
                 "depart_lane": "free", "depart_speed": "max"}
        if flow.via:
            entry["via"] = list(flow.via)
        return entry


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
    parser.add_argument("--dwell-minutes", type=float, default=30.0,
                        help="how long the marked vehicle waits under the bridge (default 30)")
    parser.add_argument("--depart", type=int, default=120,
                        help="second the marked vehicle enters, after the traffic has filled the "
                             "map (default 120)")
    parser.add_argument("--stop-in-lane", action="store_true",
                        help="leave the marked vehicle standing in the running lane instead of "
                             "pulling off it. Measured with overtaking across the centre line "
                             "allowed, doing so blocked the underpass for the whole dwell -- "
                             "traffic dropped from 12.9 m/s to 0.6 and queued in both directions. "
                             "The world's network names no opposite lanes there, so no lane lets a "
                             "driver cross the centre line to pass it")
    parser.add_argument("--incident-start", type=float, default=900.0,
                        help="second the northbound lane closure begins (default 900)")
    parser.add_argument("--incident-seconds", type=float, default=180.0,
                        help="how long the closure lasts (default 180)")
    parser.add_argument("--incident-lanes", type=int, default=5,
                        help="how many of the six northbound lanes to close, from the right. Four "
                             "still leaves more capacity than the freeway is carrying, so it "
                             "produces no queue worth seeing (default 5)")
    parser.add_argument("--no-incident", action="store_true", help="leave the freeway clear")
    parser.add_argument("--traffic-scale", type=float, default=1.0,
                        help="multiply every ambient flow rate. The rates are set so the network "
                             "runs at a steady population; raising this past the point where "
                             "arrivals exceed what the junctions discharge makes the map fill up "
                             "and never recover (default 1.0)")
    parser.add_argument("--end", type=int, default=0,
                        help="simulation end in seconds (default: sized to the marked vehicle)")
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
    parser.add_argument("--catalogue", default=CATALOGUE,
                        help="measured vehicle catalogue every vehicle type is sized from. A class "
                             "naming a body this catalogue does not hold is refused")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    if not 1 <= args.incident_lanes <= 6:
        logging.error("--incident-lanes must be between 1 and the six lanes northbound I-25 has")
        return 1
    try:
        installation = SumoInstallation.locate(args.sumo_home)
        network_text = WorldPackageReader(args.world_package).network_text()
    except (FileNotFoundError, ValueError) as error:
        logging.error("%s", error)
        return 1
    dwell_seconds = args.dwell_minutes * 60.0
    # The marked vehicle drives about 5.6 km either side of its wait; the wait dominates. Round up
    # to the next minute with room for the queue it may sit in.
    end_s = args.end or int(round((args.depart + dwell_seconds + 700) / 60.0 + 1) * 60)
    incident = None if args.no_incident else {
        "id": "incident", "place": "i25_north_past_arapahoe",
        "lanes": list(range(args.incident_lanes)), "notify": ["i25_north_short_of_arapahoe"],
        "begin": args.incident_start, "end": args.incident_start + args.incident_seconds}
    out_dir = Path(args.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    specification = ArapahoeDwellSpecification(
        dwell_seconds, args.depart, not args.stop_in_lane, incident, args.traffic_scale, end_s,
        args.step_length, args.seed).build(
        Path(args.world_package).resolve(), network_text, Path(args.catalogue).resolve(), out_dir)
    spec_path = out_dir / f"{SCENARIO_NAME}.scenario.json"
    spec_path.write_text(json.dumps(specification, indent=2, ensure_ascii=False) + "\n",
                         encoding="utf-8", newline="\n")
    logging.info("specification %s (%d places, %d flows at %d vehicles/hour, the marked vehicle "
                 "waiting %g minutes, %s, ends at %d s)", spec_path, len(PLACES),
                 len(specification["flows"]),
                 sum(f["vehs_per_hour"] for f in specification["flows"]), args.dwell_minutes,
                 "no incident" if incident is None else
                 f"{len(incident['lanes'])} of 6 northbound lanes closed from "
                 f"{incident['begin']:g} s to {incident['end']:g} s", end_s)
    result = ScenarioCompiler(installation, args.allow_sumo_version_mismatch).compile(
        spec_path, out_dir)
    for finding in result.findings.findings:
        (logging.error if finding.outcome == "refuse" else logging.warning)("%s", finding)
    for role, path in sorted(result.files.items()):
        logging.info("%-13s %s", role, path)
    logging.info("%s", "REFUSED" if result.refused else "compiled")
    return 1 if result.refused else 0


if __name__ == "__main__":
    sys.exit(main())
