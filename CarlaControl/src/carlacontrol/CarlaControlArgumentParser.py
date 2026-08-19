"""Argument parser for CarlaControl applications.

Provides command-line argument parsing for full SCTMV and other CarlaControl subsystems:
connection, world building, EO observer, traffic, telemetry, and recording.
"""

import argparse
import os
import random


class CarlaControlArgumentParser:
    """Parse CarlaControl command-line arguments into a configuration dict.

    Organizes arguments into logical groups for connection, world building,
    viewing, traffic management, telemetry, and recording subsystems.
    """

    def __init__(self, repo_root: str, description: str | None = None):
        """Initialize the argument parser.

        Args:
            repo_root: Path to repository root for default paths
            description: Program description (uses default if None)
        """
        self.repo_root = repo_root
        self.description = description or "SCTMV — Single Client Traffic Manager & Viewer"
        self._parser = self._build_parser()

    def _build_parser(self) -> argparse.ArgumentParser:
        """Build the argument parser with all argument groups."""
        ap = argparse.ArgumentParser(
            description=self.description,
            formatter_class=argparse.RawDescriptionHelpFormatter,
        )

        self._add_connection_args(ap)
        self._add_build_args(ap)
        self._add_view_args(ap)
        self._add_traffic_args(ap)
        self._add_scenario_args(ap)
        self._add_telemetry_args(ap)
        self._add_runtime_logging_args(ap)
        self._add_recording_args(ap)
        self._add_orbit_args(ap)

        return ap

    def _add_connection_args(self, ap: argparse.ArgumentParser) -> None:
        """Add connection and mode arguments."""
        conn = ap.add_argument_group("connection / mode")
        conn.add_argument(
            "--host", default="127.0.0.1", help="CARLA server host (default 127.0.0.1)"
        )
        conn.add_argument(
            "--port", type=int, default=2000, help="CARLA server RPC port (default 2000)"
        )
        conn.add_argument(
            "--tm-port", type=int, default=8000, help="Traffic Manager port (default 8000)"
        )
        conn.add_argument(
            "--async",
            dest="asynchronous",
            action="store_true",
            help="run the server free-running (asynchronous). Default is synchronous, "
            "which is what the Traffic Manager is designed for.",
        )
        conn.add_argument(
            "--fixed-delta",
            type=float,
            default=0.05,
            help="synchronous mode only: simulation step in seconds; ticks are paced to "
            "wall-clock at this rate (default 0.05 = ~20 fps / real time)",
        )

    def _add_build_args(self, ap: argparse.ArgumentParser) -> None:
        """Add world build arguments."""
        build = ap.add_argument_group("world build (phase 1)")
        build.add_argument(
            "--no-build",
            dest="build",
            action="store_false",
            default=True,
            help="don't build a world; attach to the one already on the server "
            "(skip straight to viewing / traffic)",
        )
        build.add_argument(
            "--osm", default=os.path.join(self.repo_root, "Import", "Lakeview_Carson.osm")
        )
        build.add_argument(
            "--lat", type=float, default=None, help="origin lat (default: OSM bounds center)"
        )
        build.add_argument(
            "--lon", type=float, default=None, help="origin lon (default: OSM bounds center)"
        )
        build.add_argument(
            "--step", type=float, default=10.0, help="reference-line sample spacing (m)"
        )
        build.add_argument(
            "--additional-width",
            type=float,
            default=0.6,
            help="extra width added to each side of a driving lane inside a junction (m), "
            "making connectors overlap rather than leave gaps on curves. Applies only "
            "with --no-smooth-junctions: the resolved drivable surface covers the gaps "
            "directly and meshes connectors at their true lane width",
        )
        build.add_argument(
            "--no-smooth-junctions",
            dest="smooth_junctions",
            action="store_false",
            help="build the road mesh as one strip per lane, the way it was before the "
            "drivable surface was resolved into one continuous sheet per height layer. "
            "Kept as an escape hatch for comparison",
        )
        build.set_defaults(smooth_junctions=True)
        build.add_argument(
            "--origin-height",
            type=float,
            default=None,
            help="vertical datum (m); default = sample the origin",
        )
        build.add_argument("--ion-token", default=os.environ.get("CESIUM_ION_TOKEN", ""))
        build.add_argument(
            "--ion-asset-id",
            type=int,
            default=2275207,
            help="Cesium ion asset for the visual photoreal tileset",
        )
        build.add_argument(
            "--ground-asset-id",
            type=int,
            default=1,
            help="Cesium ion asset for the hidden bare-earth terrain layer whose heights "
            "set the road elevations (default 1 = Cesium World Terrain; 0 = take "
            "heights from the photoreal surface instead, legacy)",
        )
        build.add_argument(
            "--height-align",
            choices=["area", "origin", "none", "drape"],
            default="none",
            help="how roads and drivable ground match the photoreal imagery: 'none' "
            "(default) leaves them on the bare-earth terrain; 'area'/'origin' raise "
            "everything by one constant height; 'drape' matches the photoreal "
            "point-by-point (required for the staging traffic margin). Telemetry "
            "altitude stays true bare-earth in every mode.",
        )
        build.add_argument(
            "--terrain-res",
            type=float,
            default=2.0,
            help="'drape' only: spacing (m) between drivable-surface points (default 2.0)",
        )
        build.add_argument(
            "--terrain-margin",
            type=float,
            default=30.48,
            help="width (m) of the staging ring just inside the map edge "
            "where boundary-aware traffic enters/exits (default ~100 ft)",
        )
        build.add_argument(
            "--drape-cache-dir",
            default=None,
            help="'drape' only: folder to cache terrain-height samples so rebuilds skip "
            "the slow re-sampling",
        )
        build.add_argument(
            "--no-ground-collision",
            dest="ground_collision",
            action="store_false",
            default=True,
            help="disable collision on the bare-earth ground (default ON)",
        )
        build.add_argument(
            "--settle",
            type=float,
            default=10.0,
            help="Cesium settle seconds during build",
        )
        build.add_argument(
            "--no-road-filter",
            action="store_true",
            help="don't restrict netconvert to car-drivable roads",
        )
        build.add_argument(
            "--no-clip-bounds",
            action="store_true",
            help="don't clip the road network to the OSM <bounds>",
        )
        build.add_argument(
            "--save",
            default=None,
            help="output elevated .xodr (default: Build/sumo-smoketest/<osm>_elevated.xodr)",
        )
        build.add_argument("--timeout", type=float, default=300.0, help="build RPC timeout (s)")

    def _add_view_args(self, ap: argparse.ArgumentParser) -> None:
        """Add EO observer viewing arguments."""
        view = ap.add_argument_group("EO observer (phase 2)")
        view.add_argument(
            "--z", type=float, default=1000.0, help="start altitude in FEET (default 1000)"
        )
        view.add_argument("--x", type=float, default=0.0, help="camera start x (CARLA metres)")
        view.add_argument(
            "--y",
            type=float,
            default=0.0,
            help="camera start y (CARLA metres; -Y is North)",
        )
        view.add_argument("--fov", type=float, default=90.0)
        view.add_argument(
            "--ev",
            type=float,
            default=0.0,
            help="camera exposure_compensation (EV); >0 brightens",
        )
        view.add_argument(
            "--time",
            default=None,
            help="start local solar time as HH:MM or decimal hours (default: 12:00, local "
            "solar noon). The sun's time zone is derived from the map longitude, so "
            "noon is high sun wherever the OSM origin is.",
        )
        view.add_argument(
            "--date",
            default=None,
            help="scene date as YYYY-MM-DD (default: host system date). Sets the seasonal "
            "sun angle; not for historical/almanac accuracy.",
        )
        view.add_argument(
            "--time-advance",
            action="store_true",
            help="advance the sun over time as the scene runs (toggle at runtime with K). "
            "It advances with the world tick: WALL-CLOCK time in --async, but "
            "SIMULATION time under synchronous ticking (so a paused/slow sim slows the "
            "sun). At rate 1.0 a noon start reaches midnight after ~12 h of runtime.",
        )
        view.add_argument(
            "--time-rate",
            type=float,
            default=1.0,
            help="sun-clock seconds per real/sim second when advancing (1.0 = real time; "
            ">1 accelerates, e.g. 3600 = one hour of sun per second).",
        )
        view.add_argument("--speed", type=float, default=60.0, help="initial move speed (m/s)")
        view.add_argument("--width", type=int, default=1280)
        view.add_argument("--height", type=int, default=720)
        view.add_argument(
            "--depth-max-range",
            type=float,
            default=20000.0,
            help="how far the depth camera can measure, in metres (default 20000). Depth is what "
            "Ctrl+LMB measures a point with and what tells the recorder whether a vehicle is "
            "hidden behind something; anything further away than this reads the same as empty "
            "sky. CARLA's own default of 1000 runs out at about 3250 ft looking straight down, "
            "and sooner when the camera is tilted. Raising it costs no accuracy worth "
            "measuring, but accuracy does fall off with distance either way: a reading is short "
            "by roughly 0.1%% of the distance for every kilometre of distance.",
        )

    def _add_traffic_args(self, ap: argparse.ArgumentParser) -> None:
        """Add staging traffic arguments."""
        traf = ap.add_argument_group("staging traffic (phase 3)")
        traf.add_argument(
            "--start-traffic",
            action="store_true",
            help="begin with traffic enabled (otherwise toggle it on with T)",
        )
        traf.add_argument(
            "--max", type=int, default=30, help="max vehicles alive at once (default 30)"
        )
        traf.add_argument(
            "--spawn-interval",
            type=float,
            default=0.7,
            help="seconds between spawn attempts while below --max (default 0.7)",
        )
        traf.add_argument("--filter", default="vehicle.*", help="vehicle blueprint filter")
        traf.add_argument(
            "--generation",
            default="all",
            help="vehicle blueprint generation to use: 1, 2, 3, or all (default all)",
        )
        traf.add_argument(
            "--seed",
            type=int,
            default=None,
            help="random seed for repeatable spawns/destinations and (in synchronous mode) "
            "the Traffic Manager (default: nondeterministic)",
        )
        traf.add_argument(
            "--no-fade",
            dest="fade",
            action="store_false",
            default=True,
            help="don't apply the opacity fade — spawn and despawn vehicles at FULL opacity. "
            "Diagnostic: makes it obvious whether vehicles are actually driving (rather "
            "than being hidden by the fade while they sit at the margin).",
        )
        traf.add_argument(
            "--route",
            action="store_true",
            help="give each vehicle a far-edge destination and a route to it, searched "
            "over the road network before the vehicle is spawned. The same seed and "
            "the same spawn points yield the same routes; speed, braking and "
            "traffic-signal response stay emergent. A spawn point with no route to "
            "any destination is skipped, so spawning is slower. OFF by default.",
        )
        traf.add_argument(
            "--stall-timeout",
            type=float,
            default=45.0,
            metavar="SECONDS",
            help="despawn a vehicle that has driven into the scene and then stopped dead "
            "for this long (default 45). Set well above any traffic-light phase, so "
            "waiting at a light is not mistaken for a stall. 0 keeps them forever, "
            "which leaves a stalled vehicle blocking its lane for the rest of the run.",
        )
        traf.add_argument(
            "--spawn-at-speed",
            action="store_true",
            help="give each vehicle its road speed the instant it is created, instead of "
            "letting it accelerate from rest. OFF by default: this sets the body's "
            "velocity while its wheels are still stationary, so the tyre model sees "
            "full slip and the vehicle briefly has no grip — which can carry it off "
            "the road before the traffic manager has any say.",
        )
        traf.add_argument(
            "--speed-scale",
            type=float,
            default=100.0,
            metavar="PCT",
            help="drive this percentage of each road's posted speed limit (default 100). "
            "Lower it to run the whole fleet slower without flattening the "
            "differences between roads: 40 gives 40%% of the limit everywhere, so a "
            "65 mph freeway becomes 26 mph and a 25 mph street becomes 10. Useful "
            "for telling apart behaviour that degrades with speed from behaviour "
            "that is wrong at any speed.",
        )
        traf.add_argument(
            "--speed-spread",
            type=float,
            default=20.0,
            metavar="PCT",
            help="how much drivers differ from the posted speed limit, as a percentage "
            "either side of it (default 20, so each vehicle drives between 80%% and "
            "120%% of the limit on whatever road it is on). The limit itself comes "
            "from the map: OpenDRIVE carries one per lane, derived from the OSM "
            "maxspeed tags. 0 makes every vehicle drive exactly the limit.",
        )
        traf.add_argument(
            "--route-replan-limit",
            type=int,
            default=3,
            metavar="N",
            help="a vehicle knocked off its route is replanned from where it now is. "
            "After N consecutive failures the greedy fallback takes over, if it is "
            "enabled. 0 means the fallback is never reached however often "
            "replanning fails (default 3).",
        )
        traf.add_argument(
            "--route-greedy-fallback",
            action="store_true",
            help="after --route-replan-limit failures, hand the vehicle back to greedy "
            "steering toward its destination instead of going on trying to plan a "
            "route. OFF by default: a routed vehicle either follows a route that "
            "was actually searched for, or says on the console that it cannot find "
            "one.",
        )
        traf.add_argument(
            "--traffic-diagnostics",
            action="store_true",
            help="start with the Traffic Manager's per-vehicle diagnostics on: what signal "
            "each vehicle is shown, when it brakes for one and is released, when it "
            "commits to a junction, and when it is left standing inside one. Off by "
            "default because they describe every vehicle rather than reporting "
            "something unusual, so at fleet scale they bury the lines worth reading. "
            "Toggle live with ']'. Vehicles being removed and routes failing are "
            "always reported either way.",
        )

    def _add_scenario_args(self, ap: argparse.ArgumentParser) -> None:
        scen = ap.add_argument_group("scenario")
        scen.add_argument(
            "--scenario",
            default=None,
            help="an ASAM OpenSCENARIO storyboard (.xosc) to run against the built world; "
            "loaded at startup so problems surface immediately, and started with X. "
            "Positions are resolved against the road network the server has loaded, so "
            "the storyboard must have been authored against this same world.",
        )

    def _add_telemetry_args(self, ap: argparse.ArgumentParser) -> None:
        """Add CoT telemetry arguments."""
        tel = ap.add_argument_group("CoT telemetry (phase 4)")
        tel.add_argument(
            "--tak-host",
            default="239.2.3.1",
            help="TAK CoT destination (default 239.2.3.1 = TAK SA multicast; set to a WinTAK "
            "IP for unicast)",
        )
        tel.add_argument(
            "--tak-port",
            type=int,
            default=6969,
            help="TAK CoT UDP port (default 6969)",
        )
        tel.add_argument("--rate", type=float, default=5.0, help="telemetry emit rate Hz (>=5)")
        tel.add_argument(
            "--affiliation",
            default="n",
            help="CoT standard-identity: n neutral / u unknown / f friend / h hostile",
        )
        tel.add_argument("--stale", type=float, default=3.0, help="CoT stale seconds")
        tel.add_argument("--ttl", type=int, default=1, help="multicast TTL")
        tel.add_argument(
            "--print",
            action="store_true",
            dest="echo",
            help="also print each CoT event",
        )

    def _add_runtime_logging_args(self, ap: argparse.ArgumentParser) -> None:
        ap.add_argument(
            "--log",
            default=None,
            metavar="FILE",
            help="also write this run's console output to FILE, with timestamps. "
            "Flushed line by line, so a run that ends badly still leaves its log.",
        )

    def _add_recording_args(self, ap: argparse.ArgumentParser) -> None:
        """Add recording arguments."""
        rec = ap.add_argument_group("recording (F hotkey)")
        rec.add_argument(
            "--record-dir",
            default=os.path.join(self.repo_root, "Build", "SCTMV_recordings"),
            help="folder for recordings (default Build/SCTMV_recordings). F toggles recording: "
            "each capture writes a lossless PNG of the clean streamed imagery (no HUD) plus "
            "a matching .xml Cursor-on-Target sidecar at that instant — the vehicle tracks "
            "and the collection platform (the camera itself) as an air track.",
        )
        rec.add_argument(
            "--record-hz",
            type=float,
            default=2.0,
            help="capture rate in Hz (captures per second; may be fractional, e.g. 0.5). "
            "Default 2.0.",
        )
        rec.add_argument(
            "--platform-type",
            default="uas-fixed",
            help="collection-platform airframe class for the recorded sensor's CoT air track: "
            "uas-fixed (default), uas-rotary, manned-fixed, manned-rotary, or a raw CoT "
            "type string (e.g. a-f-A-M-F-Q).",
        )
        rec.add_argument(
            "--platform-affiliation",
            default="f",
            help="CoT standard identity of the collection platform: f friend (default; it is our "
            "own asset) / n neutral / u unknown / h hostile.",
        )
        rec.add_argument(
            "--platform-callsign",
            default="OVERWATCH",
            help="callsign for the recorded platform track (default OVERWATCH).",
        )
        rec.add_argument(
            "--platform-uid",
            default=None,
            help="CoT track uid for the platform (default: CARLA-SENSOR-<camera id>).",
        )
        rec.add_argument(
            "--no-occlusion",
            dest="occlusion",
            action="store_false",
            help="do not record how much of each vehicle the camera can actually see. By default "
            "every capture measures, per vehicle, the fraction of it hidden behind buildings, "
            "trees, terrain or other vehicles, and writes it into the sidecar as occlusion "
            "(0 = fully visible, 1 = fully hidden) plus a coarse occlusion_level band, so a "
            "process drawing training boxes can drop the ones it cannot see and label the rest. "
            "The measurement reads the depth camera, adding a second subscription to its frames.",
        )
        rec.add_argument(
            "--occlusion-margin",
            type=float,
            default=1.0,
            help="metres nearer than a vehicle's own surface that something must be before it "
            "counts as hiding it (default 1.0). Absorbs the gap between the vehicle's bounding "
            "box and its real bodywork; raise it if vehicles report occlusion with a clear view, "
            "lower it if an obstruction pressed right against a vehicle is being missed.",
        )
        rec.add_argument(
            "--occlusion-samples",
            type=int,
            default=24,
            help="how finely each vehicle's outline is sampled when measuring occlusion, as the "
            "number of samples across its longer side (default 24). Higher is smoother and "
            "costs more per capture.",
        )

    def _add_orbit_args(self, ap: argparse.ArgumentParser) -> None:
        orbit = ap.add_argument_group("orbit")
        orbit.add_argument(
            "--orbit",
            action="store_true",
            help="start with the orbit camera running instead of free flight; O toggles "
            "between the two at any time either way",
        )
        orbit.add_argument("--orbit-x", type=float, default=None, help="orbit center X (CARLA metres)")
        orbit.add_argument(
            "--orbit-y",
            type=float,
            default=None,
            help="orbit center Y (CARLA metres; -Y is North)",
        )
        orbit.add_argument(
            "--orbit-lat",
            type=float,
            default=None,
            help="orbit center latitude (alternative to --orbit-x/--orbit-y)",
        )
        orbit.add_argument(
            "--orbit-lon",
            type=float,
            default=None,
            help="orbit center longitude (alternative to --orbit-x/--orbit-y)",
        )
        orbit.add_argument(
            "--orbit-radius",
            type=float,
            default=656.0,
            help="orbit radius in FEET (default 656 = 200m)",
        )
        orbit.add_argument(
            "--orbit-altitude",
            type=float,
            default=1700,
            help="camera altitude above the orbit centre, in FEET (default 1700).",
        )
        orbit.add_argument(
            "--orbit-speed",
            type=float,
            default=240.0,
            help="orbit speed in seconds (default 240 = 4 min)",
        )

    def parse(self, args: list[str] | None = None) -> dict:
        """Parse arguments and return as a dictionary.

        Args:
            args: Command-line arguments (uses sys.argv if None)

        Returns:
            Dictionary of parsed arguments suitable for use as **kwargs
        """
        namespace = self._parser.parse_args(args)
        return vars(namespace)

    def parse_args(self, args: list[str] | None = None):
        """Parse arguments and return as Namespace (for backward compatibility).

        Args:
            args: Command-line arguments (uses sys.argv if None)

        Returns:
            argparse.Namespace object
        """
        namespace = self._parser.parse_args(args)

        if namespace.seed is not None:
            random.seed(namespace.seed)

        return namespace
