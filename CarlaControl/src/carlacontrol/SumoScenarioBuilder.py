"""Build a SUMO road network and traffic scenario for a world generated from OpenStreetMap.

The world-generation pipeline already hands OSM to netconvert to get OpenDRIVE, and the same
netconvert run produces a SUMO network that `OsmConverter` reads for traffic-light phase programs
and then deletes. This builder re-runs netconvert with the same flag set and *keeps* the network, so
the result is the road graph the CARLA map was built from, in the same coordinate frame: the origin
is pinned to the world's latitude/longitude and offset normalization is disabled, which makes SUMO
(x, y) equal to CARLA (x, -y) with no offset arithmetic.

On top of a network it writes a scenario in which one marked vehicle drives in along a chosen
approach, repeats a loop a fixed number of times, and leaves along a chosen exit, while ambient
traffic runs across the rest of the map.

Speed along the marked vehicle's route is shaped with SUMO waypoints -- `<stop>` elements carrying a
`speed`, which the vehicle passes rather than stops at. A waypoint constrains speed only between its
own `startPos` and `endPos` on its own edge, so holding a whole phase to one speed means one
waypoint spanning every edge in that phase. The phases the marked vehicle drives are therefore:

  * approach: a waypoint per edge, set to that edge's own speed limit, so the higher `speedFactor`
    on its vehicle type does not show yet,
  * loop: a waypoint per edge per lap, set to the residential cruise speed,
  * exit: no waypoints at all, so the vehicle finally runs at its `speedFactor` -- above the posted
    limit of whatever road it leaves on.
"""
from __future__ import annotations

import logging
import os
import subprocess
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class NetconvertSettings:
    """The netconvert flag set a generated world was built with.

    Defaults mirror `OsmConverter.BuildArguments` and the road filter that world builds pass as
    extra arguments. `origin_lat` / `origin_lon` come from the world package and are what pin the
    network to the CARLA map's coordinate frame.
    """

    origin_lat: float
    origin_lon: float
    lane_width: float = 3.35
    sidewalk_width: float = 2.80
    traffic_lights: bool = True
    drivable_edges_only: bool = True

    def to_arguments(self, osm_path: Path, out_path: Path) -> list[str]:
        """The full netconvert command line, minus the executable."""
        args = [
            "--osm-files", str(osm_path),
            "--output-file", str(out_path),
            # Origin pinning: this latitude/longitude becomes (0,0), and normalization is off so
            # nothing shifts it afterwards. This is what keeps SUMO and CARLA coordinates in step.
            "--proj", (f"+proj=tmerc +lat_0={self.origin_lat} +lon_0={self.origin_lon} "
                       "+k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs"),
            "--offset.disable-normalization",
            "--default.lanewidth", str(self.lane_width),
            "--default.sidewalk-width", str(self.sidewalk_width),
            "--tls.guess", "true" if self.traffic_lights else "false",
            "--geometry.remove",
            "--roundabouts.guess",
            "--osm.turn-lanes",
            # Names cost nothing geometrically and make the network readable against a scenario.
            "--output.street-names", "true",
            "--output.original-names", "true",
        ]
        args += ["--junctions.join"] if self.traffic_lights else ["--tls.discard-loaded"]
        if self.drivable_edges_only:
            args += [
                "--keep-edges.by-vclass", "passenger",
                "--keep-edges.components", "1",
                "--remove-edges.isolated", "true",
            ]
        return args


@dataclass(frozen=True)
class RoadNetwork:
    """The part of a SUMO network this builder needs: lane geometry and edge-to-edge connections."""

    lane_length: dict[str, float]
    lane_speed: dict[str, float]
    street_name: dict[str, str]
    successors: dict[str, set[str]]

    @classmethod
    def from_file(cls, path: str | Path) -> RoadNetwork:
        """Read a .net.xml, skipping the internal edges SUMO generates inside junctions."""
        lane_length: dict[str, float] = {}
        lane_speed: dict[str, float] = {}
        street_name: dict[str, str] = {}
        successors: dict[str, set[str]] = {}
        for element in ET.parse(str(path)).getroot():
            if element.tag == "edge" and element.get("function") != "internal":
                edge_id = element.get("id")
                street_name[edge_id] = element.get("name", "")
                for lane in element.findall("lane"):
                    lane_length[lane.get("id")] = float(lane.get("length"))
                    lane_speed[lane.get("id")] = float(lane.get("speed"))
            elif element.tag == "connection":
                source, target = element.get("from"), element.get("to")
                if not source.startswith(":") and not target.startswith(":"):
                    successors.setdefault(source, set()).add(target)
        return cls(lane_length, lane_speed, street_name, successors)

    @property
    def edge_ids(self) -> set[str]:
        return set(self.street_name)

    def first_lane(self, edge_id: str) -> str:
        return f"{edge_id}_0"

    def length_of(self, edge_id: str) -> float:
        return self.lane_length[self.first_lane(edge_id)]

    def speed_of(self, edge_id: str) -> float:
        return self.lane_speed[self.first_lane(edge_id)]

    def total_length(self, edge_ids: tuple[str, ...]) -> float:
        return sum(self.length_of(edge_id) for edge_id in edge_ids)

    def check_drivable(self, edge_ids: tuple[str, ...]) -> None:
        """Raise if the network cannot carry this sequence of edges end to end."""
        unknown = [edge_id for edge_id in edge_ids if edge_id not in self.street_name]
        if unknown:
            raise ValueError(f"edges are not in this network: {', '.join(sorted(set(unknown)))}")
        broken = [(a, b) for a, b in zip(edge_ids, edge_ids[1:], strict=False)
                  if b not in self.successors.get(a, set())]
        if broken:
            raise ValueError("no connection between: "
                             + ", ".join(f"{a} -> {b}" for a, b in dict.fromkeys(broken)))


@dataclass(frozen=True)
class AmbientFlow:
    """One stream of background traffic, inserted at a steady rate between two edges."""

    flow_id: str
    from_edge: str
    to_edge: str
    vehicles_per_hour: int
    # Edges the route must pass through. Without them the router takes the fastest path, which on a
    # map with one through road is always that road.
    via: tuple[str, ...] = ()
    # Which vehicle-type distribution to draw from. A map with a freeway, an arterial and
    # residential streets wants a different mix on each.
    vehicle_type: str = "ambient_mix"


@dataclass(frozen=True)
class OrbitRoute:
    """The marked vehicle's route in three phases: in, round and round, out."""

    approach: tuple[str, ...]
    loop: tuple[str, ...]
    exit: tuple[str, ...]

    def edges(self, laps: int) -> tuple[str, ...]:
        return self.approach + self.loop * laps + self.exit


@dataclass(frozen=True)
class OrbitSettings:
    """How the marked vehicle drives its route."""

    laps: int = 20
    # Cap while circling, in m/s. Residential streets netconvert has given a 50 km/h default are
    # signed far lower than that in a US neighbourhood.
    loop_speed: float = 11.0
    # Multiple of the posted limit once the last waypoint is behind it, i.e. on the way out only.
    exit_speed_factor: float = 1.25
    depart_time: int = 60


@dataclass(frozen=True)
class DwellTrip:
    """A marked vehicle that drives to one place, waits there a long time, and drives on.

    Where an orbit is described by the edges it repeats, this is described by its endpoints: SUMO
    routes it, so only the waypoints that pin the route down have to be named. The dwell itself is
    a real <stop> with a duration, not the speed-carrying <stop> an orbit uses to shape pace -- the
    two look alike in XML and behave nothing alike.
    """

    from_edge: str
    to_edge: str
    dwell_lane: str
    dwell_seconds: float
    # Edges the route must pass through, in order. Without them SUMO takes its own shortest path,
    # which will not be the one the scenario is about.
    via: tuple[str, ...] = ()
    depart_time: int = 60
    # Where along the dwell lane to stop. None puts it at the lane's end.
    dwell_position: float | None = None
    # Parking takes the vehicle off the running lane. On a single-lane road a vehicle stopped on the
    # carriageway blocks it for the whole dwell, so this matters wherever traffic shares the edge.
    # It also changes whether the vehicle is still reported while stopped, which is the whole point
    # of a dwell in a telemetry scenario, so verify it against a run before changing it.
    parking: bool = False
    speed_factor: float = 1.0

    @property
    def dwell_edge(self) -> str:
        return self.dwell_lane.rsplit("_", 1)[0]


@dataclass(frozen=True)
class LaneClosure:
    """Lanes taken out of service for a window, the way an incident would take them.

    SUMO reroutes around a closure only if a vehicle learns of it before committing, so the trigger
    sits on an edge upstream of the lanes being closed. Traffic arriving faster than the surviving
    lanes can carry queues back from the closure and drains once it lifts.
    """

    trigger_edge: str
    closed_lanes: tuple[str, ...]
    begin: float
    end: float

    def to_xml(self) -> str:
        closed = "\n".join(
            f'            <closingLaneReroute id="{lane}" allow="authority"/>'
            for lane in self.closed_lanes)
        return (f'    <rerouter id="incident" edges="{self.trigger_edge}">\n'
                f'        <interval begin="{self.begin:.2f}" end="{self.end:.2f}">\n'
                f'{closed}\n'
                f'        </interval>\n'
                f'    </rerouter>')


@dataclass
class ScenarioPaths:
    """Where a built scenario landed."""

    network: Path
    routes: Path
    config: Path
    end_time: int = 0
    lap_length: float = 0.0
    route_length: float = 0.0


AMBIENT_VEHICLE_TYPES = """\
    <!-- Ambient mix. Rural Nevada, so pickups and light trucks carry more of it than a town would. -->
    <vType id="car" vClass="passenger" length="4.6" maxSpeed="55" color="0.80,0.80,0.85"
           speedFactor="normc(1.00,0.10,0.80,1.20)"/>
    <vType id="pickup" vClass="passenger" length="5.6" width="2.00" maxSpeed="50" color="0.50,0.55,0.60"
           speedFactor="normc(1.02,0.10,0.80,1.22)"/>
    <vType id="suv" vClass="passenger" length="5.0" width="1.95" maxSpeed="52" color="0.35,0.40,0.45"
           speedFactor="normc(1.00,0.10,0.80,1.20)"/>
    <vType id="van" vClass="delivery" length="5.9" maxSpeed="45" color="0.90,0.90,0.90"
           speedFactor="normc(0.95,0.08,0.75,1.10)"/>
    <vType id="truck" vClass="truck" length="9.5" maxSpeed="35" color="0.60,0.45,0.30"
           speedFactor="normc(0.90,0.06,0.75,1.05)"/>
    <vType id="motorcycle" vClass="motorcycle" length="2.2" width="0.90" maxSpeed="55" color="0.20,0.20,0.20"
           speedFactor="normc(1.05,0.12,0.85,1.30)"/>

    <vTypeDistribution id="ambient_mix"
                       vTypes="car pickup suv van truck motorcycle"
                       probabilities="0.42 0.25 0.18 0.07 0.05 0.03"/>
"""

GENERATED_BY = "carlacontrol.SumoScenarioBuilder"


class SumoScenarioBuilder:
    """Builds the .net.xml, .rou.xml and .sumocfg for one generated world."""

    def __init__(self, netconvert_path: str | Path, proj_data_path: str | Path | None = None):
        self.netconvert_path = Path(netconvert_path)
        self.proj_data_path = Path(proj_data_path) if proj_data_path else None
        self.logger = logging.getLogger(__name__)

    def build_network(self, osm_path: str | Path, out_path: str | Path,
                      settings: NetconvertSettings) -> Path:
        """Run netconvert over an OSM extract and keep the SUMO network it produces."""
        osm_path, out_path = Path(osm_path), Path(out_path)
        if not self.netconvert_path.exists():
            raise FileNotFoundError(f"netconvert not staged: {self.netconvert_path}")
        if not osm_path.exists():
            raise FileNotFoundError(f"OSM file not found: {osm_path}")

        environment = None
        if self.proj_data_path:
            # libproj needs proj.db to reproject; without it netconvert fails at run time.
            environment = dict(os.environ,
                               PROJ_LIB=str(self.proj_data_path),
                               PROJ_DATA=str(self.proj_data_path))

        out_path.parent.mkdir(parents=True, exist_ok=True)
        command = [str(self.netconvert_path), *settings.to_arguments(osm_path, out_path)]
        self.logger.info("netconvert %s -> %s", osm_path.name, out_path.name)
        result = subprocess.run(command, env=environment, capture_output=True, text=True,
                                check=False)
        if result.returncode != 0:
            raise RuntimeError(f"netconvert exited {result.returncode}:\n{result.stderr}")
        return out_path

    def estimate_end_time(self, network: RoadNetwork, route: OrbitRoute,
                          settings: OrbitSettings) -> int:
        """A simulation end time with room for the whole orbit, rounded up to the next minute.

        Junctions and ambient traffic both cost more than the straight-line estimate, hence the
        headroom.
        """
        approach_speed = network.speed_of(route.approach[0])
        exit_speed = settings.exit_speed_factor * network.speed_of(route.exit[-1])
        seconds = (network.total_length(route.approach) / approach_speed
                   + settings.laps * network.total_length(route.loop) / settings.loop_speed
                   + network.total_length(route.exit) / exit_speed)
        return int(round((settings.depart_time + 1.25 * seconds) / 60.0 + 1) * 60)

    def write_routes(self, out_path: str | Path, network: RoadNetwork, route: OrbitRoute,
                     settings: OrbitSettings, flows: list[AmbientFlow], end_time: int,
                     title: str = "") -> Path:
        """Write the .rou.xml: vehicle types, the marked vehicle's route, and the ambient flows."""
        out_path = Path(out_path)
        network.check_drivable(route.edges(min(settings.laps, 2)))

        posted = network.speed_of(route.approach[0])
        exit_speed = settings.exit_speed_factor * network.speed_of(route.exit[-1])
        lap_length = network.total_length(route.loop)

        # SUMO ignores any vehicle or flow that departs earlier than one already read, so the flows
        # (which all begin at 0) have to come before the marked vehicle.
        flow_xml = "\n".join(self._flow_xml(flow, end_time) for flow in flows)
        stop_xml = "\n".join(self._waypoint_xml(network, route, settings))
        loop_names = self._street_names(network, route.loop)

        marked_xml = f"""    <!-- The marked vehicle. speedFactor is what it runs at wherever no waypoint constrains it,
         which is only on the way out: {settings.exit_speed_factor:.2f} x the posted
         {network.speed_of(route.exit[-1]):.1f} m/s = {exit_speed:.1f} m/s. speedDev is zeroed so
         that multiple is exact rather than drawn from a distribution around it. -->
    <vType id="orbiter" vClass="passenger" length="4.8" maxSpeed="55" color="1.00,0.55,0.00"
           speedFactor="{settings.exit_speed_factor:.2f}" speedDev="0" sigma="0.20" tau="1.20"/>

    <!-- {len(route.approach)} edges in, {settings.laps} x {len(route.loop)} edges round the loop
         ({lap_length:.0f} m), {len(route.exit)} edges out. -->
    <route id="orbit" edges="{" ".join(route.edges(settings.laps))}"/>

    <vehicle id="orbiter" type="orbiter" route="orbit" depart="{settings.depart_time}"
             departLane="free" departSpeed="{posted:.2f}" arrivalSpeed="current">
        <!-- One waypoint per edge. Each holds the vehicle to the given speed for that edge's whole
             length; past the last one nothing constrains it but its own speedFactor. -->
{stop_xml}
    </vehicle>"""
        headline = (f'{title or "Orbit scenario"}: ambient traffic plus one marked vehicle that '
                    f'drives in, circles\n     {loop_names} {settings.laps} times, and leaves '
                    f'faster than it arrived.')
        return self._write_routes_document(out_path, headline, flow_xml, marked_xml)

    def write_dwell_routes(self, out_path: str | Path, network: RoadNetwork, trip: DwellTrip,
                           flows: list[AmbientFlow], end_time: int, title: str = "",
                           vehicle_types: str = AMBIENT_VEHICLE_TYPES) -> Path:
        """Write the .rou.xml for a marked vehicle that drives somewhere, waits, and drives on."""
        for edge_id in (trip.from_edge, trip.to_edge, trip.dwell_edge, *trip.via):
            if edge_id not in network.street_name:
                raise ValueError(f"edge {edge_id} is not in this network")
        if trip.dwell_lane not in network.lane_length:
            raise ValueError(f"lane {trip.dwell_lane} is not in this network")

        lane_length = network.lane_length[trip.dwell_lane]
        position = lane_length if trip.dwell_position is None else trip.dwell_position
        position = max(1.0, min(position, lane_length))
        where = network.street_name.get(trip.dwell_edge) or trip.dwell_edge
        via = f' via="{" ".join(trip.via)}"' if trip.via else ""
        minutes = trip.dwell_seconds / 60.0

        flow_xml = "\n".join(self._flow_xml(flow, end_time) for flow in flows)
        marked_xml = f"""    <!-- The marked vehicle. It drives at the posted limit throughout: what makes it an outlier
         is where it stops, not how fast it goes. -->
    <vType id="marked" vClass="passenger" length="4.80" maxSpeed="55" color="1.00,0.55,0.00"
           speedFactor="{trip.speed_factor:.2f}" speedDev="0" sigma="0.20" tau="1.20"/>

    <!-- SUMO routes this itself; the via edges hold it to the intended path, and the stop below is
         a real halt of {minutes:.0f} minutes on {where}. -->
    <trip id="marked" type="marked" depart="{trip.depart_time}"
          from="{trip.from_edge}" to="{trip.to_edge}"{via}
          departLane="best" departSpeed="max" arrivalSpeed="current">
        <stop lane="{trip.dwell_lane}" endPos="{position:.2f}" duration="{trip.dwell_seconds:.2f}"
              parking="{"true" if trip.parking else "false"}"/>
    </trip>"""
        headline = (f'{title or "Dwell scenario"}: ambient traffic plus one marked vehicle that '
                    f'drives in,\n     waits {minutes:.0f} minutes on {where}, and drives out.')
        return self._write_routes_document(out_path, headline, flow_xml, marked_xml,
                                          vehicle_types)

    def _write_routes_document(self, out_path: str | Path, headline: str, flow_xml: str,
                               marked_xml: str,
                               vehicle_types: str = AMBIENT_VEHICLE_TYPES) -> Path:
        """The shared .rou.xml skeleton: vehicle types, the ambient flows, then the marked vehicle.

        SUMO ignores any vehicle or flow that departs earlier than one already read, so the flows --
        which all begin at 0 -- have to be written before the marked vehicle.
        """
        out_path = Path(out_path)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(f"""<?xml version="1.0" encoding="UTF-8"?>
<!-- {headline}
     Generated by {GENERATED_BY}; edit that, not this. -->
<routes xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
        xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/routes_file.xsd">

{vehicle_types}
{flow_xml}

{marked_xml}

</routes>
""", encoding="utf-8")
        return out_path

    def write_config(self, out_path: str | Path, network_name: str, routes_name: str,
                     end_time: int, step_length: float = 0.05, seed: int = 42,
                     additional_names: tuple[str, ...] = ()) -> Path:
        """Write the .sumocfg that ties the network and routes together."""
        out_path = Path(out_path)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(f"""<?xml version="1.0" encoding="UTF-8"?>
<!-- Generated by {GENERATED_BY}; edit that, not this. -->
<configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
               xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/sumoConfiguration.xsd">
    <input>
        <net-file value="{network_name}"/>
        <route-files value="{routes_name}"/>{self._additional_input(additional_names)}
    </input>
    <time>
        <begin value="0"/>
        <end value="{end_time}"/>
        <!-- Matches the CARLA fixed delta the world is ticked at, so the two step together. -->
        <step-length value="{step_length}"/>
    </time>
    <processing>
        <!-- Fixed so the scenario replays identically. It also decides where the gaps in the
             ambient stream fall, and so whether the marked vehicle gets a clear run on its way
             out; this value was measured to give it one. -->
        <seed value="{seed}"/>
        <!-- A teleport cannot be mirrored by a CARLA actor, so let a jam stay a jam instead. -->
        <time-to-teleport value="-1"/>
        <max-depart-delay value="900"/>
        <collision.action value="warn"/>
    </processing>
    <report>
        <no-step-log value="true"/>
        <duration-log.statistics value="true"/>
    </report>
</configuration>
""", encoding="utf-8")
        return out_path

    def write_additional(self, out_path: str | Path, closure: LaneClosure) -> Path:
        """Write the additional file carrying a scenario's lane closure."""
        out_path = Path(out_path)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(f"""<?xml version="1.0" encoding="UTF-8"?>
<!-- Generated by {GENERATED_BY}; edit that, not this. -->
<additional xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
            xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/additional_file.xsd">
{closure.to_xml()}
</additional>
""", encoding="utf-8")
        return out_path

    def build(self, osm_path: str | Path, out_dir: str | Path, map_name: str, scenario_name: str,
              netconvert_settings: NetconvertSettings, route: OrbitRoute,
              orbit_settings: OrbitSettings, flows: list[AmbientFlow],
              end_time: int = 0, step_length: float = 0.05, seed: int = 42,
              reuse_network: bool = False) -> ScenarioPaths:
        """Build network, routes and config together, and report what was written."""
        out_dir = Path(out_dir)
        network_name = f"{map_name}.net.xml"
        routes_name = f"{scenario_name}.rou.xml"
        network_path = out_dir / network_name

        if reuse_network:
            self.logger.info("reusing network %s", network_path)
        else:
            self.build_network(osm_path, network_path, netconvert_settings)

        network = RoadNetwork.from_file(network_path)
        end_time = end_time or self.estimate_end_time(network, route, orbit_settings)

        routes_path = self.write_routes(out_dir / routes_name, network, route, orbit_settings,
                                        flows, end_time, title=map_name)
        config_path = self.write_config(out_dir / f"{scenario_name}.sumocfg", network_name,
                                        routes_name, end_time, step_length, seed)

        paths = ScenarioPaths(
            network=network_path, routes=routes_path, config=config_path, end_time=end_time,
            lap_length=network.total_length(route.loop),
            route_length=network.total_length(route.edges(orbit_settings.laps)))
        self._log_summary(network, route, orbit_settings, flows, paths)
        return paths

    # -- XML fragments -------------------------------------------------------------------------

    @staticmethod
    def _additional_input(names: tuple[str, ...]) -> str:
        """Reference any additional files -- detectors, rerouters -- from the configuration."""
        if not names:
            return ""
        joined = ",".join(names)
        return f'\n        <additional-files value="{joined}"/>'

    def _flow_xml(self, flow: AmbientFlow, end_time: int) -> str:
        via = f' via="{" ".join(flow.via)}"' if flow.via else ""
        return (f'    <flow id="{flow.flow_id}" type="{flow.vehicle_type}" begin="0" end="{end_time}"\n'
                f'          vehsPerHour="{flow.vehicles_per_hour}" '
                f'from="{flow.from_edge}" to="{flow.to_edge}"{via}\n'
                f'          departLane="free" departSpeed="max"/>')

    def _waypoint_xml(self, network: RoadNetwork, route: OrbitRoute,
                      settings: OrbitSettings) -> list[str]:
        """A waypoint spanning every edge before the exit: posted speed in, cruise round the loop.

        The exit edges deliberately get none, which is what lets the vehicle's speedFactor show
        there and nowhere else.
        """
        lines = ["        <!-- Approach: hold to the posted limit despite the higher speedFactor. -->"]
        for edge_id in route.approach:
            lines.append(self._stop_xml(network, edge_id, network.speed_of(edge_id)))
        for lap in range(settings.laps):
            lines.append(f"        <!-- Lap {lap + 1} of {settings.laps}. -->")
            for edge_id in route.loop:
                lines.append(self._stop_xml(network, edge_id,
                                            min(settings.loop_speed, network.speed_of(edge_id))))
        lines.append("        <!-- No waypoints past here, so the exit runs on speedFactor. -->")
        return lines

    def _stop_xml(self, network: RoadNetwork, edge_id: str, speed: float) -> str:
        lane = network.first_lane(edge_id)
        return (f'        <stop lane="{lane}" startPos="0" '
                f'endPos="{network.lane_length[lane]:.2f}" speed="{speed:.2f}"/>')

    def _street_names(self, network: RoadNetwork, edge_ids: tuple[str, ...]) -> str:
        names = dict.fromkeys(network.street_name[e] for e in edge_ids if network.street_name[e])
        return ", ".join(names) if names else "the loop"

    def _log_summary(self, network: RoadNetwork, route: OrbitRoute, settings: OrbitSettings,
                     flows: list[AmbientFlow], paths: ScenarioPaths) -> None:
        self.logger.info("network %s", paths.network)
        self.logger.info("  %d edges, %.1f km of road", len(network.edge_ids),
                         sum(network.length_of(e) for e in network.edge_ids) / 1000.0)
        self.logger.info("routes  %s", paths.routes)
        self.logger.info("  orbit %.0f m in + %d x %.0f m + %.0f m out = %.2f km",
                         network.total_length(route.approach), settings.laps, paths.lap_length,
                         network.total_length(route.exit), paths.route_length / 1000.0)
        self.logger.info("  loop capped at %.1f m/s, exit at %.2f x %.1f = %.1f m/s",
                         settings.loop_speed, settings.exit_speed_factor,
                         network.speed_of(route.exit[-1]),
                         settings.exit_speed_factor * network.speed_of(route.exit[-1]))
        self.logger.info("  ambient %d flows, %d vehicles/hour",
                         len(flows), sum(f.vehicles_per_hour for f in flows))
        self.logger.info("config  %s  (ends at %d s)", paths.config, paths.end_time)
