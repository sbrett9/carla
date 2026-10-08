"""SCTMV — Single Client Traffic Manager & Viewer.

Unified CARLA client combining world building, interactive EO viewing, traffic simulation,
and telemetry output in a single process with synchronous tick control.

Components:
  1. BUILD     — OSM-to-OpenDRIVE world generation with Cesium terrain alignment
  2. VIEW      — Interactive RGB camera with Unreal-style flight controls
  3. TRAFFIC   — Boundary-aware traffic entering and leaving through the staging margin
                 (--fade additionally dissolves vehicles in and out across it)
  4. TELEMETRY — Cursor-on-Target vehicle truth over UDP to TAK endpoints
  5. RECORDING — Frame capture with CoT-XML sidecar files

Modes:
  * Synchronous (default): world.tick() paced at --fixed-delta for deterministic capture
  * --async: free-running world for smoother interactive flying

Controls (hold RIGHT MOUSE to fly):
    RMB + mouse   look around
    Ctrl + LMB    measure lat/lon/elev of world point
    W/S A/D E/Q   forward/back, strafe, up/down
    Mouse wheel   adjust move speed
    Shift         move faster (x3)
    C             toggle Google photoreal tileset rendering
    G             toggle World Terrain rendering
    V             toggle World Terrain collision (default ON)
    R             toggle CARLA road-mesh rendering
    L             toggle rendering of the traffic lights and signs generated from OpenDRIVE
                  (rendering only: stop-line triggers stay live, so vehicles keep obeying
                  a signal you have hidden)
    B             toggle OSM perimeter overlay
    M             toggle margin/boundary overlay
    T             toggle traffic
    ]             toggle the Traffic Manager's per-vehicle diagnostics (what signal each
                  vehicle is shown, when it brakes for one, when it is left standing in a
                  junction). Off unless --traffic-diagnostics was given; vehicle removals
                  and route failures are always reported either way
    Y             toggle telemetry
    F             toggle recording
    Space         reset to start pose
    Esc           quit

Prerequisites:
  * Headless CARLA server running
  * SUMO netconvert (for world build): from a checkout, the one under Build/sumo-install;
    installed, the one CARLA_NETCONVERT or SUMO_HOME names, which a CARLA distribution's
    environment step sets
  * CESIUM_ION_TOKEN environment variable

Usage:
    carla-sctmv [options]

(From a checkout, `python CarlaControl/scripts/run_SCTMV.py` is the same tool.)
"""
import logging
import os
import signal
import threading
import time
from datetime import UTC, datetime

import carlanet as carla

from carlacontrol import (
    CarlaControlArgumentParser,
    NativeRecorder,
    OrbitSensorController,
    PygameInterface,
    PyGameSensorController,
    ScenarioController,
    SensorRig,
    SimClock,
    TelemetryController,
    TrafficController,
    WorldBuilder,
)
from carlacontrol.ProducerRecord import ProducerRecord
from carlacontrol.ToolLayout import ToolLayout

# The tool every file this command writes names, the CarlaNet writers' included: this command.
TOOL = "carla-sctmv"


def configure_logging(log_path: str | None) -> None:
    """Log to the console and, where `--log` names a file, to that file as well."""
    handlers: list[logging.Handler] = [logging.StreamHandler()]
    if log_path:
        os.makedirs(os.path.dirname(os.path.abspath(log_path)), exist_ok=True)
        handlers.append(logging.FileHandler(log_path, mode="w", encoding="utf-8"))
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s.%(msecs)03d %(levelname)s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
        handlers=handlers,
        force=True,
    )


def main(argv: list[str] | None = None) -> int:
    # First, so the world package a build writes and the stills a recording writes name this command
    # and carlacontrol's release.
    ProducerRecord.declare_tool(TOOL)

    # Build-tool paths go on the environment next: netconvert, PROJ and SUMO for everything this
    # process starts. Prefer what the environment already names (a packaged distribution's
    # environment step names its bundled toolchain); from a checkout, fall back to the staged build.
    # The SUMO scenario tooling and traci both resolve their installation through SUMO_HOME, which
    # from a checkout defaults to the same staged install the converter comes from, so a machine with
    # no SUMO installed still authors against the build that converted the world. Defaults, not
    # assignments: an operator who sets one deliberately keeps that choice, which is what makes the
    # version check carlacontrol.SumoInstallation performs meaningful rather than circular.
    layout = ToolLayout.current()
    netconvert, proj = layout.apply_sumo_environment()

    # Parse Args
    args = CarlaControlArgumentParser(layout, description=__doc__).parse_args(argv)
    configure_logging(args.log)
    logger = logging.getLogger(__name__)
    sync = not args.asynchronous

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    world = client.get_world()

    logger.info("server version: %s", client.get_server_version())

    # Build the world (with the server still free-running; the build RPC needs that).
    if args.build:
        if netconvert is None:
            logger.error("no netconvert to build the world with: set CARLA_NETCONVERT, or SUMO_HOME "
                         "to a SUMO installation (a CARLA distribution's environment step sets both)")
            return 1
        world_builder = WorldBuilder(layout, str(netconvert), str(proj) if proj else None)
        if not world_builder.build_world(client, args):
            return 1
    else:
        logger.info("attach mode (--no-build): using the world already on the server")

    client.set_timeout(20.0)
    world = client.get_world()

    # Place the sun, but only as far as the operator asked. A built world respawns its sun and so
    # needs whatever was asked for re-applied; an attached world was lit by whoever built it and is
    # left that way. Whichever branch ran, setup_solar_time applies --date, --time and
    # --time-advance only where they were given, and reports the sun it found where they were not.
    WorldBuilder.setup_solar_time(world, args)

    # Configure world synchronous/asynchronous mode.
    WorldBuilder.configure_sync_mode(world, sync, args.fixed_delta)

    # Traffic controller — configure for sync/async mode.
    tm = client.get_trafficmanager(args.tm_port)
    TrafficController.configure_traffic_manager(tm, sync, args.fixed_delta, args.seed)

    traffic = TrafficController.create(world, client, tm, args)
    if not traffic.available:
        logger.warning("traffic unavailable: %s", traffic.reason)
    # A SUMO drive holding the world's lease refuses this client's traffic at the server: said here,
    # at start, as well as when T is pressed, so the operator is not left wondering why nothing moves.
    holder = traffic.drive_lease_holder()
    if holder is not None:
        logger.warning("traffic locked out: %s holds this world's drive lease; the server refuses every "
                       "other traffic system's autopilot and vehicle-control writes until it is released",
                       holder)

    # Telemetry Controller
    run_id = f"run-{datetime.now(UTC).strftime('%Y%m%d-%H%M%S')}"
    try:
        origin = world.get_cesium_origin()
    except Exception as e:
        logger.warning(
            "get_cesium_origin failed (telemetry disabled): %r",
            e,
        )
        origin = None
    sim_clock = SimClock(world)
    logger.info("run id: %s", run_id)
    telemetry = TelemetryController(world, origin, args, clock=sim_clock)
    if not telemetry.available:
        logger.warning("telemetry unavailable: %s", telemetry.reason)

    # Sensors: RGB (display) + depth (Ctrl+LMB picking). Listeners are configured automatically
    # based on args.asynchronous: sync mode uses queues, async mode stores frames directly.
    sensors = SensorRig(world=world, args=args, client=client)

    # PyGameSensorController to move the sensor rig around the world
    pygame_controller = PyGameSensorController(
        sensors, world, sensors.get_initial_pose(), speed=args.speed
    )
    orbit_sensor_controller = OrbitSensorController(
        sensors=sensors,
        world=world,
        args=args,
        flight_controller=pygame_controller,
        logger=logger,
        sync=sync,
    )

    # Recorder: the native (C#) FrameRecorder encodes frames in .NET off the GIL. If the
    # CarlaNet.Recording assembly is absent the recorder reports itself unavailable when toggled (the
    # whole client is CarlaNet, so a missing recording assembly means the build itself is incomplete).
    # The depth camera rides the RGB camera's pose, which is what lets each capture record how much
    # of every vehicle that camera can actually see.
    recorder = NativeRecorder(
        world, sensors.camera, args, run_id=run_id, depth_camera=sensors.depth_cam
    )
    scenario = ScenarioController(world, args.scenario, tm)
    if args.scenario:
        logger.info("scenario armed: %s (X to run)", os.path.basename(args.scenario))

    # lastly link everything into the PyGameInterface
    pg = PygameInterface(
        args=args,
        world=world,
        window_title="SCTMV — Single Client Traffic Manager & Viewer",
        sync=sync,
        sensors=sensors,
        controller=pygame_controller,
        traffic=traffic,
        telemetry=telemetry,
        recorder=recorder,
        scenario=scenario,
        orbit_sensor_controller=orbit_sensor_controller,
    )




    # Async worker: runs the traffic + telemetry RPCs OFF the render thread. With two camera streams
    # saturating the connection each of those RPCs stalls for ~100-200 ms; left on the main loop they
    # collapse it to ~1 fps. Here the render loop never blocks on them, so flying stays smooth while
    # the Traffic Manager (server-side) keeps driving the vehicles. Sync mode runs them inline instead,
    # because there the single world.tick() must own all of it.

    stop_flag = {"value": False}
    worker_systems = [traffic, telemetry, scenario, recorder]

    def _worker():
        while not stop_flag["value"]:
            now = time.time()
            for system in worker_systems:
                try:
                    system.apply_want()
                    system.update(now)
                except Exception as e:
                    logger.exception("%s worker update failed: %r", system, e)
            time.sleep(0.05)


    worker_thread = None
    if not sync:
        worker_thread = threading.Thread(target=_worker, daemon=True)
        worker_thread.start()
        pygame_controller.start_async()

    # main thread setup
    # Clean stop on Ctrl+C even when blocked inside a .NET call (pythonnet can swallow KeyboardInterrupt).
    stop = {"flag": False}
    try:
        signal.signal(signal.SIGINT, lambda *a: stop.__setitem__("flag", True))
    except Exception as e:
        logger.warn("Failed to set signal handler: %r", e)

    try:
        while pg.running and not stop["flag"]:
            pg_quit = pg.process_events()
            if pg_quit:
                break

            if sync:
                # this thread controls world tick
                world.tick()

                dimg = sensors.get_latest_depth()
                if dimg is not None:
                    sensors.store_depth(dimg, pygame_controller.pose)

                # Apply camera transform (sync mode applies immediately)
                if not orbit_sensor_controller.orbit_enabled:
                    pygame_controller.apply_transform_sync()

                # Sync mode owns everything on the tick thread: step traffic + telemetry inline.
                #
                # On the world's clock, not the wall's. Everything these subsystems schedule
                # against -- the spawn interval, the reconcile cadence, the grace period a fresh
                # vehicle gets, the telemetry and recording rates -- is a duration in seconds, and
                # under a synchronous world a wall-clock second is however long the host took.
                # Two runs of the same seed then spawn on different frames and diverge from there:
                # measured across a pair of them, the nth vehicle appeared up to 558 frames apart
                # by the end, while the traffic manager itself drove the early vehicles
                # bit-identically for over a thousand frames.
                now = world.get_sim_time()
                for system in worker_systems:
                    try:
                        system.apply_want()
                        system.update(now)
                    except Exception as e:
                        logger.exception("%s.update failed: %r", system, e)
            else:
                if not orbit_sensor_controller.orbit_enabled:
                    pygame_controller.apply_transform_async()   # background controller applies it
                # Async: traffic + telemetry RPCs run on the background worker thread, never here, so
                # the render loop stays smooth regardless of RPC latency.
            pg.render()
    except KeyboardInterrupt:
        pass

    finally:
        logger.info("stopping; restoring server state...")
        # Stop the background threads FIRST so nothing races the despawn below.

        stop_flag["value"] = True

        if not sync:
            pygame_controller.stop_async()

        if worker_thread is not None:
            worker_thread.join(timeout=2.0)

        # The server's orbit is turned off before the cameras go.
        try:
            orbit_sensor_controller.set_enabled(False)
        except Exception as e:
            logger.warn("Failed to stop the orbit during shutdown: %r", e)

        try:
            scenario.toggle_want(False)
            scenario.apply_want()
        except Exception:
            pass

        try:
            traffic.disable()        # despawn any remaining vehicles (now single-threaded)
        except Exception as e:
            logger.warn("Failed to disable traffic during shutdown: %r", e)

        try:
            if recorder.recording:
                recorder.stop()
        except Exception as e:
            logger.warn("Failed to stop recorder during shutdown: %r", e)
        try:
            telemetry.close()
        except Exception as e:
            logger.warn("Failed to close telemetry during shutdown: %r", e)

        # Restore asynchronous mode so the headless server is never left waiting for a tick.
        try:
            tm.set_synchronous_mode(False)
        except Exception as e:
            logger.warn("Failed to restore asynchronous mode during shutdown: %r", e)

        WorldBuilder.configure_sync_mode(world, sync=False)

        sensors.cleanup()
        pg.quit()
    return 0
