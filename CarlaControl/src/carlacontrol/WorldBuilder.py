from __future__ import annotations

import logging
import os
import shlex
import time
from pathlib import Path

from CarlaNet.Map import OsmConversionOptions
from System.Collections.Generic import List

from carlacontrol.AreaOfInterestSource import AreaOfInterestError, AreaOfInterestSource
from carlacontrol.AuthoringReferenceSet import AuthoringReferenceSet
from carlacontrol.NetconvertTypeMap import (
    TYPE_FILES_OPTION,
    NetconvertTypeMap,
    NetconvertTypeMapError,
)
from carlacontrol.OsmClipper import OsmClipper
from carlacontrol.SumoInstallation import SumoInstallation


class WorldBuilder:
    def __init__(self, repo_root: str, netconvert_path: str, proj_data_path: str):
        self.repo_root = repo_root
        self.netconvert_path = netconvert_path
        self.proj_data_path = proj_data_path
        # The world's own edge types, when it declares any (`load_type_map`); passed to netconvert
        # with SUMO's map first by `make_osm_conversion_options`.
        self.type_map: NetconvertTypeMap | None = None
        self.logger = logging.getLogger(__name__)

        self.logger.info(f"world builder initialized: netconvert={netconvert_path}")


    def make_osm_conversion_options(self, args):
        opts = OsmConversionOptions()
        opts.NetconvertPath = self.netconvert_path
        opts.ProjDataDirectory = self.proj_data_path
        # netconvert emits the traffic-light signals + guessed phase program; TrafficLightInjector then
        # adds the per-phase controllers and <junction><controller> links netconvert omits, so CARLA
        # groups them correctly (one group per junction, one controller per phase) instead of orphaning
        # every light (the previous ungrouped-TL log spam, issue #1).
        opts.GenerateTrafficLights = True
        opts.OriginLatitude = args.lat
        opts.OriginLongitude = args.lon
        extra = List[str]()
        if not args.no_road_filter:
            for a in [
                "--keep-edges.by-vclass",
                "passenger",
                "--keep-edges.components",
                "1",
                "--remove-edges.isolated",
                "true",
            ]:
                extra.Add(a)
        # Slide the road network sideways to sit on the roadway in the photoreal imagery, for a map
        # whose road data is drawn beside it. netconvert shifts the projected coordinates and leaves
        # the .xodr geoReference alone, so the Cesium georeference -- and every latitude/longitude
        # the telemetry derives from it -- stays pinned; only the drivable surface moves. Elevation
        # is then sampled at the shifted position, so the roads seat on the imagery they now cover.
        # netconvert's y axis is northing, which the OpenDRIVE reader flips into CARLA's -Y = north.
        if args.road_offset_east or args.road_offset_north:
            for a in [
                "--offset.x",
                f"{args.road_offset_east:.6f}",
                "--offset.y",
                f"{args.road_offset_north:.6f}",
            ]:
                extra.Add(a)
        # Anything else the caller needs this world built with. A SUMO scenario validates its own
        # netconvert flag set against the one the world package records, so a scenario that needs a
        # flag the build does not offer -- dropping pedestrian ways by type, say -- is served by
        # passing it here rather than by letting the two sides diverge.
        # Each occurrence is split as a shell would, so one netconvert option and its value are
        # quoted together: --netconvert-arg "--remove-edges.by-type highway.footway". Keeping the
        # pair in one token is what carries a value through the parser, which reads a lone
        # dash-prefixed token as an option of its own. One token per occurrence also works, via
        # --netconvert-arg=<token>, and the two forms compose.
        for a in getattr(args, "netconvert_arg", None) or []:
            for token in shlex.split(str(a)):
                extra.Add(token)
        # The world's own edge types, after SUMO's: which vehicle classes each kind of road admits
        # is decided in the one netconvert run that writes the world's network and its OpenDRIVE, so
        # the network every scenario on this world runs already carries it (NetconvertTypeMap).
        if self.type_map is not None:
            installation_home = Path(self.netconvert_path).resolve().parent.parent
            for a in self.type_map.netconvert_arguments(installation_home):
                extra.Add(a)
        opts.ExtraArgs = extra
        return opts

    def build_world(self, client, args) -> bool:
        self.logger.info("== Digital-twin build (headless, no editor) ==")
        self.logger.info(f"  osm        : {args.osm}")
        if not os.path.exists(args.osm):
            self.logger.error(f"OSM not found: {args.osm}")
            return False
        if not os.path.exists(self.netconvert_path):
            self.logger.error(f"netconvert not staged: {self.netconvert_path}")
            return False

        if args.lat is None or args.lon is None:
            b = OsmClipper.read_bounds(args.osm)
            if b is None:
                self.logger.error("no --lat/--lon given and could not read <bounds> from the OSM file")
                return False
            args.lat = (b.min_lat + b.max_lat) / 2.0
            args.lon = (b.min_lon + b.max_lon) / 2.0
            self.logger.info(f"  origin     : {args.lat:.7f}, {args.lon:.7f}  (derived from OSM bounds center)")
        else:
            self.logger.info(f"  origin     : {args.lat:.7f}, {args.lon:.7f}  (explicit)")
        self.logger.info(
            f"  step       : {args.step} m   road-filter: "
            f"{'OFF' if args.no_road_filter else 'ON (drivable only)'}   height-align: {args.height_align}"
        )
        if args.road_offset_east or args.road_offset_north:
            self.logger.info(
                f"  road offset: {args.road_offset_east:+.2f} m east, "
                f"{args.road_offset_north:+.2f} m north "
                "(moves the drivable surface only; imagery and telemetry stay pinned)"
            )
        ok, areas = self.load_areas_of_interest(args)
        if not ok:
            return False
        if not self.load_type_map(args):
            return False
        self.logger.info(
            f"  ion asset  : {args.ion_asset_id} (photoreal)  ground: {args.ground_asset_id}  "
            f"token: {'set' if args.ion_token else 'MISSING'}"
        )
        if not args.ion_token:
            self.logger.warning("no Ion token; the tileset can't be spawned and sampling will fail.")

        osm_for_build = args.osm
        if not args.no_clip_bounds:
            bb = OsmClipper.read_bounds(args.osm)
            if bb is None:
                self.logger.info("  clip       : skipped (no <bounds> in the OSM)")
            else:
                clipped = os.path.join(
                    self.repo_root,
                    "Build",
                    "sumo-smoketest",
                    os.path.splitext(os.path.basename(args.osm))[0] + "_clipped.osm",
                )
                os.makedirs(os.path.dirname(clipped), exist_ok=True)
                clip = OsmClipper.clip_osm_to_bounds(args.osm, clipped, bb)
                osm_for_build = clipped
                self.logger.info(
                    f"  clip       : roads cut to <bounds> -> {clip.ways} ways "
                    f"(+{clip.boundary_nodes} edge nodes, {clip.renumbered_runs} split runs "
                    f"renumbered), carrying {clip.relations} relations, "
                    f"{clip.standalone_nodes} mapped features and "
                    f"{clip.relation_nodes} nodes named only by a relation")
        else:
            self.logger.info("  clip       : OFF (--no-clip-bounds)")

        save_path = args.save or os.path.join(
            self.repo_root,
            "Build",
            "sumo-smoketest",
            os.path.splitext(os.path.basename(args.osm))[0] + "_elevated.xodr",
        )

        self.logger.info("[build] generate_world_from_osm_with_elevation (convert -> sample -> inject -> build)...")
        self.logger.info("        (blocks while sampling heights and meshing the elevated road network)")
        client.set_timeout(args.timeout)
        t0 = time.time()
        elevated = client.generate_world_from_osm_with_elevation(
            osm_for_build,
            args.ion_token,
            args.ion_asset_id,
            ground_ion_asset_id=args.ground_asset_id,
            osm_options=self.make_osm_conversion_options(args),
            sample_step_meters=args.step,
            origin_height=args.origin_height,
            height_align=args.height_align,
            ground_collision=args.ground_collision,
            terrain_res=args.terrain_res,
            terrain_margin=args.terrain_margin,
            drape_cache_dir=args.drape_cache_dir,
        )
        dt = time.time() - t0
        roads = elevated.count("<road ")
        elevs = elevated.count("<elevation ")
        self.logger.info(f"        done in {dt:.1f}s — {len(elevated):,} chars, {roads} roads, {elevs} elevations")

        os.makedirs(os.path.dirname(save_path), exist_ok=True)
        with open(save_path, "w", encoding="utf-8") as f:
            f.write(elevated)
        self.logger.info(f"        wrote elevated .xodr -> {save_path}")

        if args.emit_world_package:
            self._write_world_package(client, args, osm_for_build, elevated, areas)
        return True

    def load_areas_of_interest(self, args) -> tuple[bool, AreaOfInterestSource | None]:
        """Find and validate the world's areas of interest before anything is built.

        `--aoi` names the file; without it, `<extract>.aoi.geojson` beside `--osm` is used when it
        exists. A file that fails validation refuses the build -- that costs one malformed file
        rather than minutes of building a world whose areas cannot be published. Returns whether
        to proceed, and the areas (None when none are declared).
        """
        explicit = getattr(args, "aoi", None)
        path = Path(explicit) if explicit else AreaOfInterestSource.discover(args.osm)
        if path is None:
            self.logger.info("  areas      : none declared (no --aoi, and no %s beside the extract)",
                             AreaOfInterestSource.beside(args.osm).name)
            return True, None
        if not path.is_file():
            self.logger.error(f"areas of interest not found: {path}")
            return False, None
        bounds = OsmClipper.read_bounds(args.osm)
        try:
            areas = AreaOfInterestSource.load(path, bounds)
        except AreaOfInterestError as refusal:
            self.logger.error(f"areas of interest refused, so the world is not built: {path}")
            for problem in refusal.problems:
                self.logger.error(f"    {problem}")
            return False, None
        self.logger.info(
            f"  areas      : {len(areas.areas)} from {path}"
            + ("" if args.emit_world_package else " (validated; published only with "
                                                  "--emit-world-package)"))
        return True, areas

    def load_type_map(self, args) -> bool:
        """Find and validate the world's own edge types before anything is built.

        `--type-map` names the file; without it, `<extract>.typ.xml` beside `--osm` is used when it
        exists. A malformed file, a map with SUMO's own map missing from the installation beside
        netconvert, or a `--type-files` also given through `--netconvert-arg` refuses the build:
        netconvert takes that option once, and a second list would replace the first. Returns
        whether to proceed; the map is kept on `self.type_map`.
        """
        self.type_map = None
        explicit = getattr(args, "type_map", None)
        path = Path(explicit) if explicit else NetconvertTypeMap.discover(args.osm)
        if path is None:
            self.logger.info("  road types : SUMO's own (no --type-map, and no %s beside the extract)",
                             NetconvertTypeMap.beside(args.osm).name)
            return True
        if not path.is_file():
            self.logger.error(f"type map not found: {path}")
            return False
        passed = [token for a in getattr(args, "netconvert_arg", None) or []
                  for token in shlex.split(str(a))]
        if any(token == TYPE_FILES_OPTION or token.startswith(TYPE_FILES_OPTION + "=")
               for token in passed):
            self.logger.error(f"a type map ({path}) and {TYPE_FILES_OPTION} through "
                              "--netconvert-arg were both given; netconvert reads the option once, "
                              "so name the world's types with --type-map alone")
            return False
        try:
            type_map = NetconvertTypeMap.load(path)
            type_map.netconvert_arguments(Path(self.netconvert_path).resolve().parent.parent)
        except (NetconvertTypeMapError, FileNotFoundError) as refusal:
            self.logger.error(f"type map refused, so the world is not built: {refusal}")
            return False
        self.type_map = type_map
        self.logger.info(f"  road types : SUMO's own, then {len(type_map.types)} from {path} "
                         f"(sha256 {type_map.sha256[:12]})")
        for line in type_map.describe():
            self.logger.info(f"               {line}")
        return True

    def _write_world_package(self, client, args, osm_for_build: str, elevated: str,
                             areas: AreaOfInterestSource | None = None) -> None:
        """Record the built world on disk: road network, the grids that recover true ground height
        from driven height, and a manifest of the origin, imagery layers and build settings.

        The build itself has already succeeded by this point, so a failure to write the record is
        reported and swallowed rather than failing the run -- losing the record is a lesser harm
        than discarding a world that took minutes to build."""
        map_name = os.path.splitext(os.path.basename(args.osm))[0]
        try:
            manifest_path = client.write_world_package(
                args.emit_world_package,
                map_name,
                elevated,
                args.height_align,
                osm_for_build,
                args.ion_asset_id,
                args.ground_asset_id,
                args.step,
                args.terrain_res,
                args.terrain_margin,
                self.make_osm_conversion_options(args).ExtraArgs,
            )
        except Exception as ex:
            self.logger.warning(f"world package not written: {ex}")
            return
        self.logger.info(f"        wrote world package -> {manifest_path}")
        self._publish_reference_set(manifest_path, areas)

    def _publish_reference_set(self, package_path: str,
                               areas: AreaOfInterestSource | None) -> None:
        """Publish the areas, the place index and the solar frame into the package just written.

        Reported and swallowed on failure, for the same reason as the package itself: the world is
        built, and losing part of its record is a lesser harm than discarding it. Areas that fail
        resolution are refused by name in the log and left out; the rest is still published.
        """
        installation = None
        try:
            # The installation that converted the world, so the projection that places the areas is
            # the one that placed the lanes.
            installation = SumoInstallation.locate(explicit=Path(self.netconvert_path).parent.parent)
        except FileNotFoundError as ex:
            self.logger.warning(f"no SUMO installation beside {self.netconvert_path}: {ex}")
        try:
            report = AuthoringReferenceSet(package_path, installation, areas).publish()
        except Exception as ex:
            self.logger.warning(f"authoring reference set not published: {ex!r}")
            return
        if report.refusals:
            self.logger.error(f"        areas of interest refused ({len(report.refusals)} "
                              "problem(s) above); the rest of the reference set is published")
        self.logger.info(f"        published reference set -> {', '.join(report.entries)}")

    @staticmethod
    def configure_sync_mode(world, sync: bool, fixed_delta: float = 0.05) -> None:
        """Configure world synchronous or asynchronous mode.

        Args:
            world: CARLA world object
            sync: True for synchronous mode, False for asynchronous
            fixed_delta: Simulation step in seconds (synchronous mode only)
        """
        logger = logging.getLogger(__name__)
        if sync:
            settings = world.get_settings()
            settings.synchronous_mode = True
            settings.fixed_delta_seconds = fixed_delta
            world.apply_settings(settings)
            logger.info(f"world synchronous mode enabled: fixed_delta={fixed_delta}s")
        else:
            try:
                settings = world.get_settings()
                if settings.synchronous_mode:
                    settings.synchronous_mode = False
                    settings.fixed_delta_seconds = None
                    world.apply_settings(settings)
                    logger.info("world asynchronous mode enabled")
            except Exception as e:
                logger.debug(f"failed to disable synchronous mode: {e}")

    @staticmethod
    def solar_time_requested(args) -> bool:
        """Whether the operator asked for the sun to be placed at all.

        `--date`, `--time` and `--time-advance` are the three ways of asking. When none of them was
        given there is nothing to apply, and the world keeps the sun it already has.
        """
        return bool(args.date) or args.time is not None or bool(args.time_advance)

    @staticmethod
    def parse_solar_hours(value) -> float:
        """`--time` as decimal hours, written either as HH:MM or as a decimal figure."""
        text = str(value)
        if ":" in text:
            hours, minutes = text.split(":")
            return int(hours) + int(minutes) / 60.0
        return float(text)

    @staticmethod
    def setup_solar_time(world, args) -> bool:
        """Apply the solar date, clock and advancement that were asked for, and only those.

        Each of `--date`, `--time` and `--time-advance` is applied when it was given and left alone
        when it was not. Nothing is invented for the ones that were not: a stand-in date is the
        host's calendar and a stand-in hour is noon, so inventing them makes the illumination of
        every capture a by-product of when the run happened rather than a setting the run declared.
        Illumination is a controlled variable or it is nothing.

        With none of the three given, the world is not touched at all and the sun it already has is
        reported instead -- including in attach mode, where nothing was respawned and relighting
        somebody's world would be a silent change to what their captures show.

        Args:
            world: CARLA world object
            args: Parsed arguments with time, date, time_advance, time_rate

        Returns:
            True if successful (logs warnings on failure)
        """
        logger = logging.getLogger(__name__)
        if not WorldBuilder.solar_time_requested(args):
            WorldBuilder._report_solar_state_left_alone(world, logger)
            return True
        try:
            if args.date:
                year, month, day = (int(v) for v in args.date.split("-"))
                if world.set_solar_date(year, month, day):
                    logger.info(f"solar date set: {year:04d}-{month:02d}-{day:02d}")
                else:
                    logger.warning("solar date not set (world has no CesiumSunSky)")
            if args.time is not None:
                hours = WorldBuilder.parse_solar_hours(args.time)
                if world.set_solar_time(hours):
                    logger.info(
                        "solar time set: "
                        f"{int(hours) % 24:02d}:{int(round((hours % 1) * 60)) % 60:02d} local"
                    )
                else:
                    logger.warning("solar time not set (world has no CesiumSunSky)")
            if args.time_advance:
                world.set_time_advance(True, args.time_rate)
                logger.info(
                    f"solar time advancing at {args.time_rate:g}x "
                    "(wall-clock in --async, sim-time under synchronous ticking)"
                )
            return True
        except Exception as e:
            logger.error(f"solar time-of-day setup failed: {e!r}")
            return False

    @staticmethod
    def _report_solar_state_left_alone(world, logger: logging.Logger) -> None:
        """Say which sun the run is using when the run did not choose one.

        Reading it back rather than announcing an intention: the sun a world carries comes from
        whoever last set it, and a line in the log is the only record of what lit the captures.
        """
        state = None
        try:
            state = world.get_solar_state()
        except Exception as e:
            logger.debug(f"could not read the world's solar state: {e!r}")
        if not state:
            logger.info("sun left as the world has it (no --time, --date or --time-advance given); "
                        "the world reports no sun to read")
            return
        hours = state["solar_time"]
        logger.info(
            "sun left as the world has it (no --time, --date or --time-advance given): "
            f"{int(hours) % 24:02d}:{int(round((hours % 1) * 60)) % 60:02d} local on "
            f"{state['year']:04d}-{state['month']:02d}-{state['day']:02d}, "
            f"elevation {state['sun_elevation_deg']:.2f} deg"
            + (f", advancing at {state['rate']:g}x" if state["advancing"] else "")
        )
