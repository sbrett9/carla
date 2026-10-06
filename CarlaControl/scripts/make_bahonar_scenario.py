#!/usr/bin/env python3
"""Write, and compile, the week-long pattern-of-life scenario for the Shahid Bahonar Port world.

Shahid Bahonar is Bandar Abbas's older multipurpose and passenger port on the Strait of Hormuz, and
also a naval harbour: a military airfield with a guard-tower perimeter to the west, a commercial and
ferry port with an oil depot and drydock to the east, joined by a coastal trunk corridor.

Over seven simulated days the scenario establishes a rhythm: diurnal corridor traffic, ferry
sailings that pulse the port, three-shift gate changes at the airfield, a guard posted at each of
the sixteen towers and relieved every eight hours, and routine air-freight hauls from the apron to
the port. That baseline exists so that six planted anomalies stand out against it, each a different
kind of deviation a detector would have to catch:

  * posting not taken up -- the guard due to relieve one tower sets out on time and parks elsewhere
    inside the wire for the shift, so the tower is left unmanned (a gap in a perfect cadence, carried
    by the vehicle that leaves it);
  * escort-to-drydock -- a high-value air-freight shipment gets a dense military escort from the
    apron to the drydock, where the routine haul never goes (excess, formation, and a spike in a
    normally quiet corner -- one event chain, both signatures);
  * gate probe -- a civilian vehicle approaches the port gate from the public side, waits, and
    leaves without entering, twice on different days (approach without entry);
  * perimeter shadow -- during the pre-dawn dead hours one vehicle slowly circles the fence line
    (temporal and spatial outlier);
  * ferry stay-behind -- a vehicle arrives on a ferry pulse and never leaves (persistence).

**This script writes a specification, not SUMO XML** (`07_Scenario_Authoring.md` D7.2), and compiles
it with the scenario compiler, so the generated scenario faces every check a hand-written one does.
The specification, `<out-dir>/Shahid_Bahonar_Port_PatternOfLife.scenario.json`, is compiled against
the world package, whose own network SUMO runs byte for byte: netconvert is not run here and no
network is rewritten. What a road admits is the world's, set when it was built by its type map
(`Import/Shahid_Bahonar_Port.typ.xml`, D7.33), so the naval traffic (vehicle class `army`) and the
port's cleared traffic (`authority`) drive the airfield's and the port's service roads because the
world admits them there. A fence keyed on OpenStreetMap's `access=private` is not in the world -- a
type map cannot key on access -- so a civilian may drive a private residential or tertiary road
inside the wire; every civilian route is fixed at compile time and stated in the resolution report.

**Time.** Simulated second zero is 07:00 at Bahonar on 29 September 2026, at +03:30 (Iran has kept
no daylight saving since 2022), and the civil date advances at midnight. The schedule is written in
civil clocks -- `d3 10:00` is 10:00 on 2 October -- and the compiler turns each into seconds under the
epoch, so every guard shift, ferry sailing and anomaly keeps its local time. The run covers seven
whole days from the first morning shift change, `d0 07:00` to `d7 07:00`; a daily rhythm is written
for every civil day the run touches and cut to the run, so the 06:00 sailing of day 0 falls before
it and the 06:00 sailing of day 7 inside it.

**Bodies.** Every vehicle type is bound to a measured CARLA body through the catalogue, and a body
the catalogue has not measured is refused by name (check 14): civilian cars draw the catalogue's own
civilian cars (its `civ_car` class); the port's cleared cars the Nissan Patrol; taxis the taxi;
lorries the European heavy goods vehicle; buses the Mitsubishi Fuso Rosa. Pickups, military jeeps,
guards and the escort share one body, the first of `JEEP_BODIES` the catalogue has measured: the
Jeep Wrangler, and until the catalogue measures it the Nissan Patrol, the owner's stated
alternative. The specification names the one chosen, so a catalogue that gains the Wrangler changes
the scenario when the generator is next run, and says so. A planted vehicle is drawn from the class
of the population it moves among wherever its driving model is that population's -- the gate probe
is a civilian car, the escort is military jeeps -- so no vehicle type is carried by planted vehicles
alone except where the difference is the behaviour itself: the perimeter shadow's crawl, and the
stay-behind, a civilian car cleared into the port.

**Supervision** goes to the supervision plan and nowhere else: the six anomalies as pattern instances,
the guard postings and the air-freight hauls as nominal hard negatives, and the ferry pulses as a
cleared-gate cohort. A label follows a vehicle (`06_Truth_And_Annotation.md` §3.5, the owner's ruling
of 2026-10-05), so the omission is carried by the vehicle that deviates, as the owner ruled on
2026-10-06: the guard schedule skips the posting, which writes no trip and no row, and the guard who
should have taken it is a planted vehicle of the guards' own type that departs the apron at the shift
change, parks for the shift on the airside spur between the western aprons and returns, labeled
`bahonar:posting_not_taken_up` with the tower and the shift it was due at. The terms are the
scenario's own, in namespace `bahonar` (06 §9.4). A slot and an instance are sited at the world's
areas of interest, `Import/Shahid_Bahonar_Port.aoi.geojson`.
Each anomaly's interval is anchored to the event of its vehicle that commits it: the escort's and the
shadow's transits to their departures, each probe's standoff, the stay-behind's dwell and the guard's
parked shift to their stop.

Usage:
    python make_bahonar_scenario.py [--days 7] [--out-dir ../../Import]
Preview a slice in the GUI before committing to the full week:
    python make_bahonar_scenario.py --days 1 --out-dir <scratch>
    sumo-gui -c <scratch>/Shahid_Bahonar_Port_PatternOfLife.sumocfg
"""
import argparse
import json
import logging
import os
import sys
from datetime import datetime, timedelta
from pathlib import Path

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402  (needs the path above)
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

MAP_NAME = "Shahid_Bahonar_Port"
SCENARIO_NAME = f"{MAP_NAME}_PatternOfLife"

# The SUMO this repository stages and the distribution ships, the release every shipped world was
# converted by. The compiler refuses a routing SUMO of another release (check 6).
STAGED_SUMO = os.path.join(_REPO, "Build", "sumo-install")

# The measured vehicle catalogue every vehicle type in this scenario is sized from.
CATALOGUE = os.path.join(_REPO, "CarlaControl", "catalogue", "vehicles.catalogue.json")

# The world package, which carries the SUMO network this scenario runs, byte for byte, and the areas
# of interest its supervision is sited at.
WORLD_PACKAGE = os.path.join(_REPO, "Build", "world-packages", f"{MAP_NAME}.cwp")

# What simulated second zero means in civil time at the site: 07:00 at Bahonar on 29 September 2026.
# Iran has observed no daylight saving since 2022, so the offset is +03:30 all year. The offset is
# the declaration; the zone name is carried for a reader and never resolved.
EPOCH = {
    "epoch_version": 1,
    "civil_datetime": "2026-09-29T07:00:00+03:30",
    "utc_offset_hours": 3.5,
    "utc_datetime": "2026-09-29T03:30:00Z",
    "calendar_advances": True,
    "dst_in_effect": False,
    "time_zone_id": "Asia/Tehran",
    "note": "Shahid Bahonar Port, Bandar Abbas: simulated second zero is 07:00 local time on "
            "29 September 2026, the first morning shift change; the civil date advances",
}

# The civil clock simulated second zero falls on, and on which the run ends `days` later.
RUN_CLOCK = "07:00"

# The sun holds still for each capture window, at the instant it opens: one lighting condition per
# window. An authored default the operator may override at run start.
ILLUMINATION = {"illumination_version": 1, "policy": "freeze_at_window_start"}

# The class of the catalogue whose members are this scenario's civilian cars.
CIVILIAN_CAR_CLASS = "civ_car"

# The body of the civilian pickups, the military jeeps, the guards and the escort, in the owner's
# order of preference: the Jeep Wrangler Rubicon, and the Nissan Patrol where the catalogue has not
# measured the Wrangler. The first the catalogue holds is the one every such vehicle is drawn as.
JEEP_BODIES = ("vehicle.jeep.wrangler_rubicon", "vehicle.nissan.patrol")

# The bodies the scenario names directly. The heavy goods vehicle is registered in the content as
# vehicle.carlamotors.european_hgv, a rigid six-wheeler; the Rosa minibus is registered as
# vehicle.fuso.mitsubishi. The content holds one Nissan Patrol.
HGV = "vehicle.carlamotors.european_hgv"
PATROL = "vehicle.nissan.patrol"
TAXI = "vehicle.taxi.ford"
MINIBUS = "vehicle.fuso.mitsubishi"

# Anchor places: each the edge (an OSM way id; a leading '-' is the way's reverse direction) the
# scenario was reconnoitred on. The corridor edges are public; the rest are inside the wire.
PLACE_EDGES = {
    "corridor_west_in": "26417705#0",     # Shahid Rajaei Highway, west
    "corridor_east_in": "26401342#0",     # Pasdaran Boulevard, east
    "corridor_east_out": "26401454#6",    # Pasdaran Boulevard, east
    "corridor_north_out": "1396607732",   # freeway spur, north-east
    "apron": "26413425#5",                # airfield apron and cargo; the guards' base
    "drydock": "206870533#2",             # ship repair drydock, far east, normally near-dead
    "ferry": "900954912#2",               # Bahonar ferry terminal
    # The civilian-reachable approach to the eastern port checkpoint, an OSM gate node: as close
    # as an uncleared vehicle gets to a gate into the port.
    "port_gate_approach": "-431672573#2",
}

# Where a vehicle halts, as a lane and the distance along it in metres.
STOPS = {
    "port_gate_standoff": ("-431672573#2_0", 30.0),
    "ferry_berth": ("900954912#2_0", 20.0),
    # A dead-end airside road between the air base's two western aprons, beside the main taxiway,
    # which no scheduled route drives: where the guard who does not take up a posting parks for the
    # shift instead, in the open, under 400 m from the guards' base, where a camera over the base
    # can see it.
    "west_apron_spur": ("-441624290#0_0", 70.0),
}

# Civilian corridor through-movements. The public corridor is fragmented and effectively one-way in
# places, so the westbound exits are left out rather than faked.
CORRIDOR_PAIRS = [
    ("corridor_west_in", "corridor_east_out"),
    ("corridor_west_in", "corridor_north_out"),
    ("corridor_east_in", "corridor_north_out"),
]

# The sixteen guard towers, each as the lane of the army-drivable edge nearest it and the position
# along that lane where a guard parks (metres from the lane's start). Found by projecting each tower
# of a Google Earth survey onto the nearest army edge; the four northern towers share the long
# perimeter road at distinct offsets. Each tower's area of interest is centred on the same point.
TOWER_POSTS = [
    ("-26413411", 5.0), ("-26413426", 5.0), ("26413427", 62.0), ("26413459", 58.9),
    ("-26413460", 5.0), ("-26413425#5", 278.7), ("26413338#6", 381.0), ("-26413338#6", 848.2),
    ("26413338#5", 442.2), ("-26413274#2", 5.0), ("26413274#0", 16.4), ("-26413409#2", 6.2),
    ("26413409#1", 2409.9), ("26413409#1", 920.5), ("26413409#1", 538.1), ("26413409#1", 306.1),
]

# The southern fence line, west to east, which the perimeter shadow drives past the posts without
# stopping.
FENCE_LINE = ["26413338#6", "-26413425#5", "-26413460", "26413459",
              "26413427", "-26413426", "-26413411"]

# The diurnal corridor rhythm: (from hour, to hour, vehicles per hour) -- quiet pre-dawn, busy
# daytime, moderate evening.
CORRIDOR_WINDOWS = [(0, 6, 20), (6, 10, 180), (10, 16, 120), (16, 20, 200), (20, 24, 50)]
# Ferry sailings -- daylight only, none overnight. Each draws a twelve-minute pulse in from the
# corridor and, half an hour later, releases one out.
FERRY_HOURS = [6, 8, 10, 12, 14, 16, 18]
FERRY_PULSE_PER_HOUR = 600
# Airfield shift changes: base traffic surges through the gate, and a guard is posted at every tower
# for the shift.
SHIFT_CLOCKS = ["07:00", "15:00", "23:00"]
SHIFT_LENGTH = "8h"
SHIFT_SURGE_PER_HOUR = 240
# Light air-freight runs from the apron to the port, a few a day.
HAUL_HOURS = [9, 13, 17]

# The planted vehicles, at their local times.
ESCORT_DEPARTS = "d3 10:00"
ESCORT_SIZE = 5
ESCORT_SPACING_S = 4
PROBE_DAYS = (2, 5)
PROBE_CLOCK = "11:00"
PROBE_JITTER_S = 137          # per day, so the two probes are not at one clock time
PROBE_DWELL = "5m"
SHADOW_DEPARTS = "d6 02:30"
STAYBEHIND_DEPARTS = "d1 08:00"
# The guard who does not take up the skipped posting departs at its shift change and parks here.
OFF_POST_PLACE = "west_apron_spur"

# Each vehicle kind: its catalogue bodies (a list; JEEP_BODIES for the one jeep body; or
# CIVILIAN_CAR_CLASS for the catalogue's civilian cars), its SUMO vehicle class, its own driving
# attributes, and its sumo-gui shape and colour.
VEHICLE_CLASSES = [
    ("civ_car", CIVILIAN_CAR_CLASS, "passenger", {"maxSpeed": "35"}, "passenger", "#CCCCD1",
     "Civilian cars on the public coastal corridor: the catalogue's civilian cars. The gate probe "
     "is one of them."),
    ("civ_pickup", JEEP_BODIES, "passenger", {"maxSpeed": "33"}, "passenger", "#8C9499",
     "Civilian pickups and utility vehicles: the jeep body."),
    ("civ_taxi", [TAXI], "taxi", {"maxSpeed": "35"}, "taxi", "#E6CC33", "Taxis."),
    ("civ_truck", [HGV], "truck", {"maxSpeed": "25"}, "truck", "#998059",
     "Civilian lorries: the European heavy goods vehicle."),
    ("civ_bus", [MINIBUS], "bus", {"maxSpeed": "24"}, "bus", "#D9D9B3",
     "Buses: the Mitsubishi Fuso Rosa."),
    ("port_vehicle", [PATROL], "authority", {"maxSpeed": "30"}, "passenger", "#8CB3D9",
     "Port-cleared cars that pass a checkpoint to reach the ferry and the quays: the Nissan "
     "Patrol."),
    ("port_truck", [HGV], "authority", {"maxSpeed": "24"}, "truck", "#7399BF",
     "Port-cleared freight: the European heavy goods vehicle."),
    ("mil_jeep", JEEP_BODIES, "army", {"maxSpeed": "33"}, "passenger", "#4D6140",
     "Naval and base jeeps inside the wire: the jeep body. The escort convoy is five of them."),
    ("mil_truck", [HGV], "army", {"maxSpeed": "24"}, "truck", "#475738",
     "Naval and base lorries, and the routine air-freight hauls: the European heavy goods "
     "vehicle."),
    ("guard", JEEP_BODIES, "army", {"maxSpeed": "30"}, "passenger", "#59734D",
     "The guard posted at a tower for a shift: the jeep body."),
    ("army_car_crawl", CIVILIAN_CAR_CLASS, "army",
     {"maxSpeed": "33", "speedFactor": "0.45", "speedDev": "0"}, "passenger", "#FF3399",
     "A civilian car admitted inside the wire and driven at 0.45 of each limit, exactly: the "
     "perimeter shadow's crawl, which is the behaviour itself."),
    ("port_car", CIVILIAN_CAR_CLASS, "authority", {"maxSpeed": "30"}, "passenger", "#FF4D00",
     "A civilian car cleared into the port: the ferry stay-behind."),
]

# The populations the flows draw from, as shares of the classes above.
VEHICLE_MIXES = [
    ("civ_mix", {"civ_car": 0.50, "civ_pickup": 0.22, "civ_taxi": 0.12, "civ_truck": 0.10,
                 "civ_bus": 0.06}, "Civilian traffic on the public coastal corridor."),
    ("port_mix", {"port_vehicle": 0.7, "port_truck": 0.3},
     "Port-cleared traffic: ferry passengers and port freight."),
    ("mil_mix", {"mil_jeep": 0.6, "mil_truck": 0.4}, "Naval and base traffic inside the wire."),
]

# What commits each anomaly's interval (06_Truth_And_Annotation.md §3.3): a transit opens when SUMO
# inserts the vehicle; a standoff or a dwell is the vehicle's one stop, from arriving to leaving, so
# a probe's standoff declares its five minutes and no instant, wherever the queue lets it arrive.
FROM_DEPARTURE = {"start": "depart"}
AT_THE_STOP = {"start": "stop:0", "end": "stop_end:0"}

# The scenario's own vocabulary (06_Truth_And_Annotation.md §9.4): its terms, roles and area kinds.
VOCABULARY = {"namespaces": [{
    "namespace": "bahonar",
    "version": 2,
    "authority": "Shahid Bahonar Port pattern of life; CarlaControl/scripts/make_bahonar_scenario.py",
    "terms": [
        {"term": "bahonar:coordinated_group_transit", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "Several vehicles depart together and travel as one group in tight "
                       "formation: a dense military escort around a shipment.",
         "parameters": {
             "group_size": {"type": "integer", "definition": "vehicles in the group"},
             "departure_spread_s": {"type": "number", "unit": "s",
                                    "definition": "first to last departure"}},
         "counterfactual": {"kind": "term", "ref": "bahonar:routine_freight_haul"}},
        {"term": "bahonar:destination_off_pattern", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "A vehicle travels to a place the routine traffic of its kind never goes: "
                       "an air-freight shipment taken to the drydock rather than to the port."},
        {"term": "bahonar:standoff_dwell_at_access_point", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "A vehicle approaches a controlled access point from the public side, halts "
                       "short of it for several minutes, and departs without transiting.",
         "parameters": {"dwell_s": {"type": "number", "unit": "s",
                                    "definition": "authored halt length at the access point"}},
         "contrast_with": ["bahonar:cleared_gate_transit"],
         "counterfactual": {"kind": "term", "ref": "bahonar:cleared_gate_transit"}},
        {"term": "bahonar:perimeter_transit_off_cadence", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "During the pre-dawn dead hours one vehicle slowly follows the fence line "
                       "past the guard posts without stopping at any of them.",
         "parameters": {
             "speed_factor": {"type": "number", "definition": "fraction of each limit driven"},
             "circuit_edges": {"type": "integer", "definition": "fence-line roads driven"}}},
        {"term": "bahonar:arrival_without_departure", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "A vehicle arrives with a ferry sailing's traffic and never leaves; its "
                       "dwell is still open when the scenario ends."},
        {"term": "bahonar:posting_not_taken_up", "since": 2, "status": "active",
         "applies_to": ["entity"],
         "definition": "A guard due to relieve a tower departs on schedule but parks elsewhere for "
                       "the shift; the tower it was due at goes unmanned.",
         "parameters": {
             "expected_tower": {"type": "string",
                                "definition": "the tower the guard was due to relieve, as the id "
                                              "of its area of interest"},
             "expected_shift_start": {"type": "string",
                                      "definition": "the civil date and time, with its UTC offset, "
                                                    "at which the shift it was due to take up "
                                                    "began"}},
         "counterfactual": {"kind": "term", "ref": "bahonar:tower_posting"}},
        # A tower posting is the matched negative for the dwell-shaped terms, and for the posting
        # not taken up above all: the same guard type, departing the same base at the same shift
        # change and parked for the same eight hours, at the tower rather than elsewhere.
        {"term": "bahonar:tower_posting", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "An eight-hour authored guard posting at a perimeter tower: a long parked "
                       "dwell, in a legitimate place, for a legitimate reason.",
         "hard_negative_for": ["bahonar:standoff_dwell_at_access_point",
                               "bahonar:arrival_without_departure",
                               "bahonar:posting_not_taken_up"]},
        {"term": "bahonar:routine_freight_haul", "since": 1, "status": "active",
         "applies_to": ["entity"],
         "definition": "A scheduled air-freight run by one lorry from the apron to the port.",
         "hard_negative_for": ["bahonar:coordinated_group_transit",
                               "bahonar:destination_off_pattern"]},
        {"term": "bahonar:cleared_gate_transit", "since": 1, "status": "active",
         "applies_to": ["cohort"],
         "definition": "Port-cleared traffic that transits a checkpoint and enters: every member "
                       "of a ferry sailing's pulse, for its whole life."},
    ],
    "roles": [
        {"role": "bahonar:lead", "definition": "the escort vehicle that departs first"},
        {"role": "bahonar:follower", "definition": "an escort vehicle behind the lead"},
        {"role": "bahonar:guard", "definition": "the vehicle that mans a tower for a shift"},
    ],
    "area_kinds": [{"kind": "bahonar:guard_post"}, {"kind": "bahonar:gate"},
                   {"kind": "bahonar:drydock"}, {"kind": "bahonar:ferry_terminal"}],
}]}


class BahonarPatternOfLifeSpecification:
    """The pattern of life as a specification the scenario compiler compiles."""

    def __init__(self, catalogue: VehicleCatalogue, days: int, no_show_day: int, no_show_hour: int,
                 no_show_tower: int, step_length_s: float, seed: int) -> None:
        self.catalogue = catalogue
        self.days = days
        self.no_show_day = no_show_day
        self.no_show_hour = no_show_hour
        self.no_show_tower = no_show_tower
        self.step_length_s = step_length_s
        self.seed = seed

    def build(self, world_package: Path, network_fingerprint: str, catalogue: Path,
              base: Path) -> dict:
        """The specification, with every path relative to `base`, the directory it is written in."""
        return {
            "spec_version": 1,
            "scenario_id": SCENARIO_NAME,
            "scenario_name": "Shahid Bahonar Port pattern of life",
            "description": (
                f"{self.days} day(s) of pattern of life at Shahid Bahonar Port from 07:00 on "
                "29 September 2026: diurnal corridor traffic, ferry pulses, airfield shift changes, "
                "a guard at each of sixteen towers relieved every eight hours and routine "
                "air-freight hauls, with a guard who parks elsewhere instead of relieving a tower, "
                "an escort to the drydock, two gate probes, a perimeter shadow and a ferry "
                "stay-behind planted against it. Written by "
                "CarlaControl/scripts/make_bahonar_scenario.py; edit that, not this."),
            "world": {"package": self._relative(world_package, base),
                      "network_fingerprint": network_fingerprint},
            "epoch": EPOCH,
            "illumination": ILLUMINATION,
            "seeds": {"sumo": self.seed},
            "simulation": {"end": self.run_end, "step_length_s": self.step_length_s},
            "catalogue": self._relative(catalogue, base),
            "vehicle_classes": self.vehicle_classes(),
            "vehicle_mixes": [{"id": mix_id, "shares": dict(shares), "note": note}
                              for mix_id, shares, note in VEHICLE_MIXES],
            "places": self.places(),
            "place_sets": {"guard_towers": self.towers()},
            "instants": {"escort_departs": ESCORT_DEPARTS, "shadow_departs": SHADOW_DEPARTS,
                         "staybehind_departs": STAYBEHIND_DEPARTS, "run_end": self.run_end},
            "flows": self.corridor_flows() + self.ferry_flows() + self.shift_flows(),
            "actors": self.hauls() + self.anomalies(),
            "rotas": [self.guard_rota()],
            "vocabulary": VOCABULARY,
            "supervision": self.supervision(),
        }

    # -- time ---------------------------------------------------------------------------------------

    @property
    def run_end(self) -> str:
        return f"d{self.days} {RUN_CLOCK}"

    def in_run(self, day: int, clock: str) -> bool:
        """Whether `d<day> <clock>` falls in the run, which starts at `d0 07:00` and ends `days`
        later at the same clock. Clocks are zero-padded, so they order as text."""
        return (0, RUN_CLOCK) <= (day, clock) < (self.days, RUN_CLOCK)

    def window(self, day: int, start_hour: int, end_hour: int) -> tuple[str, str] | None:
        """A daily window's begin and end on one civil day, cut to the run; None outside it."""
        run_start = (0, int(RUN_CLOCK[:2]))
        run_end = (self.days, int(RUN_CLOCK[:2]))
        begin = max((day, start_hour), run_start)
        end = min((day, end_hour), run_end)
        if end <= begin:
            return None
        return f"d{begin[0]} {begin[1]:02d}:00", f"d{end[0]} {end[1]:02d}:00"

    # -- vehicles -----------------------------------------------------------------------------------

    @property
    def jeep_body(self) -> str:
        """The first of `JEEP_BODIES` the catalogue has measured; the last when it holds none, so
        the compile refuses it by name."""
        measured = set(self.catalogue.blueprint_ids)
        return next((body for body in JEEP_BODIES if body in measured), JEEP_BODIES[-1])

    def vehicle_classes(self) -> list[dict]:
        civilian_cars = [member["blueprint_id"]
                         for member in self.catalogue.classes[CIVILIAN_CAR_CLASS]["members"]]
        classes = []
        for class_id, bodies, vclass, behaviour, shape, colour, note in VEHICLE_CLASSES:
            if bodies == CIVILIAN_CAR_CLASS:
                blueprints = civilian_cars
            elif bodies == JEEP_BODIES:
                blueprints = [self.jeep_body]
            else:
                blueprints = list(bodies)
            classes.append({"class_id": class_id, "blueprints": blueprints, "sumo_vclass": vclass,
                            "behaviour": dict(behaviour), "share": 0.0, "gui_shape": shape,
                            "gui_colour": colour, "note": note})
        return classes

    # -- places -------------------------------------------------------------------------------------

    def places(self) -> dict:
        places = {name: {"edge": edge} for name, edge in PLACE_EDGES.items()}
        places.update({name: {"lane": lane, "offset_m": offset}
                       for name, (lane, offset) in STOPS.items()})
        for name, (edge, offset) in zip(self.towers(), TOWER_POSTS, strict=True):
            places[name] = {"lane": f"{edge}_0", "offset_m": offset}
        for edge in FENCE_LINE:
            places.setdefault(self.fence_place(edge), {"edge": edge})
        return places

    @staticmethod
    def towers() -> list[str]:
        return [f"tower_{index:02d}" for index in range(len(TOWER_POSTS))]

    @staticmethod
    def fence_place(edge: str) -> str:
        return f"fence_{edge.replace('-', 'r').replace('#', '_')}"

    # -- flows --------------------------------------------------------------------------------------

    def corridor_flows(self) -> list[dict]:
        """Civilian corridor traffic with a day/night rhythm, per pair, per civil day."""
        flows = []
        for day in range(self.days + 1):
            for pair_index, (source, sink) in enumerate(CORRIDOR_PAIRS):
                for start, end, rate in CORRIDOR_WINDOWS:
                    window = self.window(day, start, end)
                    if window is not None:
                        flows.append(self._flow(f"corridor_d{day}_p{pair_index}_h{start}",
                                                "civ_mix", source, sink, rate, *window))
        return flows

    def ferry_flows(self) -> list[dict]:
        """Each sailing draws a pulse in from the corridor and releases one out, over 12 minutes."""
        flows = []
        for day in range(self.days + 1):
            for hour in FERRY_HOURS:
                if not (self.in_run(day, f"{hour:02d}:00") and self.in_run(day, f"{hour:02d}:42")):
                    continue
                flows.append(self._flow(f"ferry_in_d{day}_h{hour}", "port_mix", "corridor_east_in",
                                        "ferry", FERRY_PULSE_PER_HOUR, f"d{day} {hour:02d}:00",
                                        f"d{day} {hour:02d}:12"))
                flows.append(self._flow(f"ferry_out_d{day}_h{hour}", "port_mix", "ferry",
                                        "corridor_east_out", FERRY_PULSE_PER_HOUR,
                                        f"d{day} {hour:02d}:30", f"d{day} {hour:02d}:42"))
        return flows

    def shift_flows(self) -> list[dict]:
        """Base traffic surges through the airfield gate at each shift change."""
        flows = []
        for day in range(self.days):
            for clock in SHIFT_CLOCKS:
                hour = int(clock[:2])
                flows.append(self._flow(f"shift_in_d{day}_h{hour}", "mil_mix", "corridor_west_in",
                                        "apron", SHIFT_SURGE_PER_HOUR, f"d{day} {hour:02d}:00",
                                        f"d{day} {hour:02d}:20"))
                flows.append(self._flow(f"shift_out_d{day}_h{hour}", "mil_mix", "apron",
                                        "corridor_north_out", SHIFT_SURGE_PER_HOUR,
                                        f"d{day} {hour:02d}:15", f"d{day} {hour:02d}:35"))
        return flows

    @staticmethod
    def _flow(flow_id: str, mix: str, source: str, sink: str, rate: float, begin: str,
              end: str) -> dict:
        return {"id": flow_id, "type": mix, "from": source, "to": sink, "vehs_per_hour": rate,
                "begin": begin, "end": end, "depart_lane": "free", "depart_speed": "max"}

    # -- the guard rota, the hauls and the planted vehicles -----------------------------------------

    def guard_rota(self) -> dict:
        """A guard drives out to every tower at every shift change and parks at the kerb for the
        shift, off the running lane, so at any hour a guard stands at every post and the shift
        change is a wave of arrivals and departures across the fence. The no-show is one occasion
        the rota skips: that tower stands unmanned for a shift while the other fifteen are
        relieved as usual. The skip writes no trip and no supervision row (06 §3.5); the guard who
        should have taken the posting is a planted vehicle (`anomalies`), and the label is that
        vehicle's."""
        rota = {
            "id": "guard_posting",
            "days": f"0..{self.days - 1}",
            "at": list(SHIFT_CLOCKS),
            "subjects": {"place_set": "guard_towers"},
            "id_pattern": "guard_d{day}_h{hour}_t{subject_index}",
            "template": {"type": "guard", "from": "apron", "to": "apron", "via": ["$subject"],
                         "stops": [{"place": "$subject", "duration": SHIFT_LENGTH,
                                    "parking": True}],
                         "depart_lane": "best", "depart_speed": "max",
                         "arrival_speed": "current"},
        }
        if self.no_show_in_run:
            rota["skip"] = [{"day": self.no_show_day, "at": self.no_show_clock,
                             "subject_index": self.no_show_tower,
                             "because": f"the guard due here this shift, {self.off_post_id}, parks "
                                        "elsewhere: this post is not manned"}]
        return rota

    @property
    def no_show_in_run(self) -> bool:
        return self.no_show_day < self.days

    @property
    def no_show_clock(self) -> str:
        return f"{self.no_show_hour:02d}:00"

    @property
    def off_post_id(self) -> str:
        """The guard who should have taken the skipped posting, named for the occasion it leaves."""
        return f"offpost_d{self.no_show_day}_h{self.no_show_hour}_t{self.no_show_tower}"

    @property
    def no_show_shift_start(self) -> str:
        """The civil instant the skipped posting's shift begins, written as the epoch writes one:
        day `d` is the epoch's civil date plus `d` days, since the calendar advances at midnight."""
        midnight = datetime.fromisoformat(EPOCH["civil_datetime"]).replace(hour=0, minute=0,
                                                                            second=0)
        return (midnight + timedelta(days=self.no_show_day, hours=self.no_show_hour)).isoformat()

    def hauls(self) -> list[dict]:
        """Light air-freight runs from the apron to the port, a few a day."""
        return [self._actor(f"haul_d{day}_{index}", "mil_truck", f"d{day} {hour:02d}:00", "apron",
                            "ferry")
                for day in range(self.days) for index, hour in enumerate(HAUL_HOURS)]

    def escort_ids(self) -> list[str]:
        return [f"escort_{index}" for index in range(ESCORT_SIZE)] if self._escort_in_run else []

    @property
    def _escort_in_run(self) -> bool:
        day, clock = ESCORT_DEPARTS[1:].split(" ")
        return self.in_run(int(day), clock)

    def probe_days(self) -> list[int]:
        return [day for day in PROBE_DAYS if self.in_run(day, PROBE_CLOCK)]

    def shadow_in_run(self) -> bool:
        day, clock = SHADOW_DEPARTS[1:].split(" ")
        return self.in_run(int(day), clock)

    def staybehind_in_run(self) -> bool:
        day, clock = STAYBEHIND_DEPARTS[1:].split(" ")
        return self.in_run(int(day), clock)

    def anomalies(self) -> list[dict]:
        """The planted vehicles: every anomaly is a vehicle's, the posting the schedule skips
        included."""
        out = []
        # Escort-to-drydock: five military jeeps from the apron to the drydock in tight formation.
        for index, escort in enumerate(self.escort_ids()):
            out.append(self._actor(escort, "mil_jeep", self.escort_departure(index), "apron",
                                   "drydock"))
        # Gate probe: a civilian car approaches the port gate on the public side, waits five
        # minutes without entering, and leaves.
        for day in self.probe_days():
            out.append(self._actor(f"probe_d{day}", "civ_car", self.probe_departure(day),
                                   "corridor_west_in", "corridor_east_out",
                                   via=["port_gate_approach"],
                                   stops=[{"place": "port_gate_standoff", "duration": PROBE_DWELL}]))
        # Perimeter shadow: a vehicle slowly follows the fence line when nothing else moves.
        if self.shadow_in_run():
            out.append(self._actor("shadow", "army_car_crawl", {"instant": "shadow_departs"},
                                   "apron", "apron",
                                   via=[self.fence_place(edge) for edge in FENCE_LINE]))
        # Ferry stay-behind: a car arrives with the 08:00 sailing and stays until the run ends.
        if self.staybehind_in_run():
            out.append(self._actor("staybehind", "port_car", {"instant": "staybehind_departs"},
                                   "corridor_east_in", "ferry",
                                   stops=[{"place": "ferry_berth", "until": {"instant": "run_end"},
                                           "parking": True}]))
        # Posting not taken up: the guard due at the skipped posting departs the apron at its shift
        # change, as every guard does, parks for the shift elsewhere inside the wire, and returns;
        # the schedule writes no posting there, so the tower stands unmanned.
        if self.no_show_in_run:
            out.append(self._actor(self.off_post_id, "guard",
                                   f"d{self.no_show_day} {self.no_show_clock}", "apron", "apron",
                                   via=[OFF_POST_PLACE],
                                   stops=[{"place": OFF_POST_PLACE, "duration": SHIFT_LENGTH,
                                           "parking": True}]))
        return out

    @staticmethod
    def escort_departure(index: int) -> dict:
        if index == 0:
            return {"instant": "escort_departs"}
        return {"instant": "escort_departs", "plus": index * ESCORT_SPACING_S}

    @staticmethod
    def probe_departure(day: int) -> dict:
        return {"at": f"d{day} {PROBE_CLOCK}", "plus": day * PROBE_JITTER_S}

    @staticmethod
    def _actor(actor_id: str, vehicle_type: str, depart: object, source: str, sink: str,
               via: list[str] | None = None, stops: list[dict] | None = None) -> dict:
        actor = {"id": actor_id, "type": vehicle_type, "depart": depart, "from": source,
                 "to": sink, "depart_lane": "best", "depart_speed": "max",
                 "arrival_speed": "current"}
        if via:
            actor["via"] = via
        if stops:
            actor["stops"] = stops
        return actor

    # -- supervision --------------------------------------------------------------------------------

    def supervision(self) -> dict:
        instances = []
        escorts = self.escort_ids()
        if escorts:
            instances.append({
                "name": "pi_escort_drydock_d3", "supervision": "annotated",
                "labels": ["bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"],
                "participants": [{"actor": escort,
                                  "role": "bahonar:lead" if index == 0 else "bahonar:follower"}
                                 for index, escort in enumerate(escorts)],
                "intervals": [{"participant": escort, "phase": "transit",
                               "anchor": dict(FROM_DEPARTURE)} for escort in escorts],
                "aoi_refs": ["drydock"],
                "parameters": {"group_size": len(escorts),
                               "departure_spread_s": (len(escorts) - 1) * ESCORT_SPACING_S},
                "counterfactual": {"kind": "term", "ref": "bahonar:routine_freight_haul"}})
        for day in self.probe_days():
            instances.append(self._subject_instance(
                f"pi_gate_probe_d{day}", f"probe_d{day}", "bahonar:standoff_dwell_at_access_point",
                "standoff", AT_THE_STOP, ["port_gate"], {"dwell_s": 300}))
        if self.shadow_in_run():
            instances.append(self._subject_instance(
                "pi_perimeter_shadow_d6", "shadow", "bahonar:perimeter_transit_off_cadence",
                "transit", FROM_DEPARTURE, [],
                {"speed_factor": 0.45, "circuit_edges": len(FENCE_LINE)}))
        if self.staybehind_in_run():
            instances.append(self._subject_instance(
                "pi_ferry_stay_behind_d1", "staybehind", "bahonar:arrival_without_departure",
                "dwell", AT_THE_STOP, ["ferry_terminal"], {}))
        if self.no_show_in_run:
            instances.append(self._subject_instance(
                f"pi_posting_not_taken_up_d{self.no_show_day}", self.off_post_id,
                "bahonar:posting_not_taken_up", "dwell", AT_THE_STOP, [],
                {"expected_tower": f"tower_{self.no_show_tower:02d}",
                 "expected_shift_start": self.no_show_shift_start}))
        for haul in self.hauls():
            instances.append({"name": haul["id"], "supervision": "nominal",
                              "labels": ["bahonar:routine_freight_haul"],
                              "participants": [{"actor": haul["id"], "role": "subject"}]})
        return {
            "instances": instances,
            "cohorts": [{"flow": flow["id"], "supervision": "annotated",
                         "labels": ["bahonar:cleared_gate_transit"]}
                        for flow in self.ferry_flows()],
            "series": [{"series_id": "tower_relief", "rota": "guard_posting",
                        "member_role": "bahonar:guard", "slot_length": SHIFT_LENGTH,
                        "slot_aoi_refs": {tower: tower for tower in self.towers()},
                        "supervision": "nominal", "labels": ["bahonar:tower_posting"]}],
        }

    @staticmethod
    def _subject_instance(name: str, actor: str, label: str, phase: str, anchor: dict,
                          aoi_refs: list[str], parameters: dict) -> dict:
        instance = {"name": name, "supervision": "annotated", "labels": [label],
                    "participants": [{"actor": actor, "role": "subject"}],
                    "intervals": [{"participant": actor, "phase": phase, "anchor": dict(anchor)}]}
        if aoi_refs:
            instance["aoi_refs"] = aoi_refs
        if parameters:
            instance["parameters"] = parameters
        return instance

    @staticmethod
    def _relative(path: Path, base: Path) -> str:
        """`path` as the specification names it: relative to `base`, or whole where the two are on
        different drives and no relative path exists."""
        try:
            return os.path.relpath(path, base).replace(os.sep, "/")
        except ValueError:
            return path.as_posix()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out-dir", default=os.path.join(_REPO, "Import"),
                        help="where the specification and the compiled scenario go "
                             "(default: carla/Import)")
    parser.add_argument("--world-package", default=WORLD_PACKAGE,
                        help="world package the CARLA map was generated from. Its map.net.xml is "
                             "the network this scenario runs and its areas of interest are where "
                             "the supervision is sited; netconvert is not run here")
    parser.add_argument("--days", type=int, default=7,
                        help="length of the run in days from 07:00 on day 0 (default 7; use 1 for "
                             "a GUI preview)")
    parser.add_argument("--no-show-day", type=int, default=4,
                        help="day on which one tower is left unmanned for a shift (default 4)")
    parser.add_argument("--no-show-hour", type=int, default=7,
                        help="shift hour (7, 15 or 23) whose guard parks elsewhere instead of "
                             "relieving the tower (default 7)")
    parser.add_argument("--no-show-tower", type=int, default=3,
                        help="which tower, 0-15, is left unmanned (default 3)")
    parser.add_argument("--step-length", type=float, default=1.0,
                        help="simulation step in seconds, the step this scenario's traffic was "
                             "measured at; the co-simulation interpolates between steps along each "
                             "lane (default 1.0)")
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
                        help="measured vehicle catalogue every vehicle type is sized from. A class "
                             "naming a body this catalogue does not hold is refused")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    if args.days < 1:
        logging.error("--days must be at least 1")
        return 1
    try:
        installation = SumoInstallation.locate(args.sumo_home)
        network_text = WorldPackageReader(args.world_package).network_text()
        catalogue = VehicleCatalogue.load(args.catalogue)
    except (FileNotFoundError, ValueError) as error:
        logging.error("%s", error)
        return 1
    if CIVILIAN_CAR_CLASS not in catalogue.classes:
        logging.error("%s declares no class %r, which names this scenario's civilian cars",
                      args.catalogue, CIVILIAN_CAR_CLASS)
        return 1
    out_dir = Path(args.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    specification = BahonarPatternOfLifeSpecification(
        catalogue, args.days, args.no_show_day, args.no_show_hour, args.no_show_tower,
        args.step_length, args.seed).build(
        Path(args.world_package).resolve(), NetworkFingerprint.of_text(network_text),
        Path(args.catalogue).resolve(), out_dir)
    spec_path = out_dir / f"{SCENARIO_NAME}.scenario.json"
    spec_path.write_text(json.dumps(specification, indent=2, ensure_ascii=False) + "\n",
                         encoding="utf-8", newline="\n")
    logging.info("specification %s (%d places, %d flows, %d actors, a %d-tower guard rota over %d "
                 "day(s), ends at %s)", spec_path, len(specification["places"]),
                 len(specification["flows"]), len(specification["actors"]), len(TOWER_POSTS),
                 args.days, specification["simulation"]["end"])
    jeep = next(c for c in specification["vehicle_classes"] if c["class_id"] == "mil_jeep")
    logging.info("pickups, military jeeps, guards and the escort are drawn as %s, the first of %s "
                 "catalogue %s has measured", jeep["blueprints"][0], ", ".join(JEEP_BODIES),
                 catalogue.catalogue_id)
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
