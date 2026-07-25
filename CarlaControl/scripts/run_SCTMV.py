#!/usr/bin/env python3
"""SCTMV — Single Client Traffic Manager & Viewer.

Unified CARLA client combining world building, interactive EO viewing, traffic simulation,
and telemetry output in a single process with synchronous tick control.

Components:
  1. BUILD     — OSM-to-OpenDRIVE world generation with Cesium terrain alignment
  2. VIEW      — Interactive RGB camera with Unreal-style flight controls
  3. TRAFFIC   — Boundary-aware traffic with margin-based fade spawning/despawning
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
    B             toggle OSM perimeter overlay
    M             toggle margin/boundary overlay
    T             toggle traffic
    Y             toggle telemetry
    F             toggle recording
    Space         reset to start pose
    Esc           quit

Prerequisites:
  * Headless CARLA server running
  * SUMO netconvert under Build/sumo-install (for world build)
  * CESIUM_ION_TOKEN environment variable

Usage:
    python run_SCTMV.py [options]
"""
import os
import signal
import sys
import threading
import time
import xml.etree.ElementTree as ET

import numpy as np
import pygame

# Build-tool paths must be on the environment BEFORE carlanet is imported (netconvert / PROJ).
_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
_INSTALL = os.path.join(_REPO, "Build", "sumo-install")
# Prefer an explicit path from the environment (set by a packaged distribution that bundles its own
# netconvert/PROJ outside the source tree); fall back to the in-repo build location otherwise.
_NETCONVERT = os.environ.get("CARLA_NETCONVERT") or os.path.join(
    _INSTALL, "bin", "netconvert.exe" if os.name == "nt" else "netconvert")
_PROJ = os.environ.get("PROJ_LIB") or os.environ.get("PROJ_DATA") or os.path.join(_INSTALL, "share", "proj")
os.environ.setdefault("CARLA_NETCONVERT", _NETCONVERT)
os.environ.setdefault("PROJ_LIB", _PROJ)
os.environ.setdefault("PROJ_DATA", _PROJ)


import carlanet as carla


from carlacontrol import (
    CarlaControlArgumentParser,
    NativeRecorder,
    Pose,
    PygameInterface,
    PyGameSensorController,
    SensorRig,
    TelemetryController,
    TrafficController,
    WorldBuilder,
)

def main() -> int:
    # Parse Args
    args = CarlaControlArgumentParser(_REPO, description=__doc__).parse_args()
    sync = not args.asynchronous

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    world = client.get_world()
    
    print(f"server version: {client.get_server_version()}")
    


    # Build the world (with the server still free-running; the build RPC needs that).
    if args.build:
        world_builder = WorldBuilder(_REPO, _NETCONVERT, _PROJ)
        if not world_builder.build_world(client, args):
            return 1
    else:
        print("attach mode (--no-build): using the world already on the server")
    # Configure solar time/date (re-applied every run because the sun is respawned on each world build).
    WorldBuilder.setup_solar_time(world, args)

    # Configure world synchronous/asynchronous mode.
    WorldBuilder.configure_sync_mode(world, sync, args.fixed_delta)
    
    client.set_timeout(20.0)
    world = client.get_world()




    # Traffic controller — configure for sync/async mode.
    tm = client.get_trafficmanager(args.tm_port)
    TrafficController.configure_traffic_manager(tm, sync, args.fixed_delta, args.seed)

    traffic = TrafficController.create(world, client, tm, args)
    if not traffic.available:
        print(f"traffic unavailable: {traffic_controller.reason}", file=sys.stderr)
    
    # Telemetry Controller
    telemetry = TelemetryController(world, args)
    if not telemetry.available:
        print(f"telemetry unavailable: {telemetry.reason}", file=sys.stderr)


    # Sensors: RGB (display) + depth (Ctrl+LMB picking). Listeners are configured automatically
    # based on args.asynchronous: sync mode uses queues, async mode stores frames directly.
    sensors = SensorRig(world=world, args=args)

    # Recorder: the native (C#) FrameRecorder encodes frames in .NET off the GIL. If the
    # CarlaNet.Recording assembly is absent the recorder reports itself unavailable when toggled (the
    # whole client is CarlaNet, so a missing recording assembly means the build itself is incomplete).
    recorder= NativeRecorder(world, sensors.camera, args)


    # PyGameSensorController to move the sensor rig around the world
    controller = PyGameSensorController(sensors, world, sensors.get_initial_pose())

    # lastly link everything into the PyGameInterface
    pg = PygameInterface(
        args=args,
        world=world,
        window_title="SCTMV — Single Client Traffic Manager & Viewer",
        sync=sync,
        sensors=sensors,
        controller=controller,
        traffic=traffic,
        telemetry=telemetry,
        recorder=recorder,
    )




    # Async worker: runs the traffic + telemetry RPCs OFF the render thread. With two camera streams
    # saturating the connection each of those RPCs stalls for ~100-200 ms; left on the main loop they
    # collapse it to ~1 fps. Here the render loop never blocks on them, so flying stays smooth while
    # the Traffic Manager (server-side) keeps driving the vehicles. Sync mode runs them inline instead,
    # because there the single world.tick() must own all of it.

    stop_flag = {"value": False}
    
    def _worker():
        while not stop_flag["value"]:
            now = time.time()
            for system in [traffic, telemetry, recorder]:
                try:
                    system.apply_want()
                    system.update(now)
                except Exception as e:
                    print(f"{system} worker: {e!r}", file=sys.stderr)
            time.sleep(0.05)


    worker_thread = None
    if not sync:
        worker_thread = threading.Thread(target=_worker, daemon=True)
        worker_thread.start()
        controller.start_async()



    # main thread setup
    # Clean stop on Ctrl+C even when blocked inside a .NET call (pythonnet can swallow KeyboardInterrupt).
    stop = {"flag": False}
    try:
        signal.signal(signal.SIGINT, lambda *a: stop.__setitem__("flag", True))
    except Exception:
        pass

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
                    sensors.store_depth(dimg, controller.pose)

                # Apply camera transform (sync mode applies immediately)
                controller.apply_transform_sync()

                # Sync mode owns everything on the tick thread: step traffic + telemetry inline.
                now = time.time()
                for system in [traffic, telemetry, recorder]:
                    try:
                        system.apply_want()
                        system.update(now)
                    except Exception as e: 
                        print(f"{system}.update failed: {e!r}", file=sys.stderr)
                    
            else:
                controller.apply_transform_async()   # background controller applies it
                # Async: traffic + telemetry RPCs run on the background worker thread, never here, so
                # the render loop stays smooth regardless of RPC latency.

            
            pg.render()



    except KeyboardInterrupt:
        pass

    finally:
        print("\nstopping; restoring server state...")
        # Stop the background threads FIRST so nothing races the despawn below.

        stop_flag["value"] = True

        if not sync:
            controller.stop_async()

        if worker_thread is not None:
            worker_thread.join(timeout=2.0)

        try: 
            traffic.disable()        # despawn any remaining vehicles (now single-threaded)
        except Exception: 
            pass
        
        try:
            if recorder.recording: 
                recorder.stop()
        except Exception: 
            pass
        try: 
            telemetry.close()
        except Exception: 
            pass
        
        
        # Restore asynchronous mode so the headless server is never left waiting for a tick.
        try:
            tm.set_synchronous_mode(False)
        except Exception:
            pass
            
        WorldBuilder.configure_sync_mode(world, sync=False)

        sensors.cleanup()
        pg.quit()
    return 0


if __name__ == "__main__":
    sys.exit(main())
