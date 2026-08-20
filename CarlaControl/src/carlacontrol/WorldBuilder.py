from __future__ import annotations

import logging
import os
import time
from datetime import datetime

from CarlaNet.Map import OsmConversionOptions
from CarlaNet.Types.Rpc.Environment import OpendriveGenerationParameters
from System.Collections.Generic import List

from carlacontrol.OsmClipper import OsmClipper


class WorldBuilder:
    def __init__(self, repo_root: str, netconvert_path: str, proj_data_path: str):
        self.repo_root = repo_root
        self.netconvert_path = netconvert_path
        self.proj_data_path = proj_data_path
        self.logger = logging.getLogger(__name__)
        
        self.logger.info(f"world builder initialized: netconvert={netconvert_path}")


    def make_opendrive_parameters(self, args):
        """Road-mesh parameters for the generated world.

        Matches the OSM defaults carlanet would otherwise apply — opposing lanes arrive as
        separate roads, so walls are disabled to stop them colliding, and a larger maximum
        road length keeps the mesh less fragmented — while exposing the two that govern
        how junction surfaces are built.
        """
        return OpendriveGenerationParameters(
            2.0,                      # vertex distance
            500.0,                    # maximum road length before a road is split
            0.0,                      # wall height
            0.0,                      # extra width per side on junction driving lanes
            True,                     # resolve the drivable network into one surface
            True,                     # mesh visibility
            True,                     # pedestrian navigation
        )

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
                nways, nbnd = OsmClipper.clip_osm_to_bounds(args.osm, clipped, bb)
                osm_for_build = clipped
                self.logger.info(f"  clip       : roads cut to <bounds> -> {nways} ways (+{nbnd} edge nodes)")
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
            parameters=self.make_opendrive_parameters(args),
            sample_step_meters=args.step,
            origin_height=args.origin_height,
            height_align=args.height_align,
            ground_collision=args.ground_collision,
            cesium_settle_seconds=args.settle,
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
        return True

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
    def setup_solar_time(world, args) -> bool:
        """Configure solar time/date and time advancement after world build.

        Args:
            world: CARLA world object
            args: Parsed arguments with time, date, time_advance, time_rate

        Returns:
            True if successful (logs warnings on failure)
        """
        logger = logging.getLogger(__name__)
        try:
            if args.date:
                y, mo, d = (int(v) for v in args.date.split("-"))
            else:
                now = datetime.now()
                y, mo, d = now.year, now.month, now.day
            if args.time is None:
                hours = 12.0
            elif ":" in str(args.time):
                hh, mm = str(args.time).split(":")
                hours = int(hh) + int(mm) / 60.0
            else:
                hours = float(args.time)
            world.set_solar_date(y, mo, d)
            if world.set_solar_time(hours):
                logger.info(
                    f"solar time set: {int(hours) % 24:02d}:{int(round((hours % 1) * 60)) % 60:02d} "
                    f"local, date {y:04d}-{mo:02d}-{d:02d}"
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
