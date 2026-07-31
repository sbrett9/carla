"""SCTMV — Single Client Traffic Manager & Viewer (via the carlanet Python API).

One client, one tick-master. Combines the four standalone demonstrators into a single
process so the Traffic Manager can run the way the documentation intends — in synchronous
mode (the default):

  1. BUILD   — drops an .osm on the running headless server and produces an elevated,
               Cesium-aligned OpenDRIVE world (formerly test_digital_twin.py).
  2. VIEW    — an interactive EO observer window: an unparented RGB camera flown with
               Unreal-editor controls, with layer toggles and a lat/lon/elev picker
               (formerly eo_observer.py).
  3. TRAFFIC — boundary-aware staging traffic that spawns/despawns with an opacity fade at
               the map margin; toggled live with a hotkey (formerly test_generate_fade_traffic.py).
  4. TELEMETRY — Cursor-on-Target vehicle-truth emitted to a TAK endpoint over UDP; toggled
               live with a hotkey, off by default (formerly cot_telemetry.py).

Why a single client: three separate clients cannot share synchronous mode (only one client may be
the tick-master), which is why the standalone scripts were forced to run asynchronously. Collapsing
to one client lets SCTMV own a synchronous world clock for deterministic, real-time EO capture.

Modes:
  * Default (synchronous WORLD): SCTMV drives world.tick() at --fixed-delta, so sensor capture is
    deterministic and paced to real time. The Traffic Manager, however, runs FREE-RUNNING (async):
    the current .NET TM cannot drive vehicles under synchronous ticking (its ALSM reads vehicle state
    from a continuously-streamed world-observer cache that isn't advanced in lockstep with the tick,
    so a fully-synchronous TM produces zero motion). Running the TM async lets it drive the vehicles
    the proven way while SCTMV still owns the world clock. Trade-off: the EO capture is deterministic
    but the traffic is not. True synchronous traffic needs a CarlaNet TM engine fix.
  * --async: the whole world free-runs; the window renders as fast as it can (smoother flying). Same
    free-running Traffic Manager. Use this if you don't need the synchronous world clock.

Synchronous pacing: ticks are paced to wall-clock at --fixed-delta (default 0.05 s -> ~20 fps), so
the EO view runs at real time. Flying is capped to that frame rate.

Controls (hold RIGHT MOUSE to fly, like the Unreal editor):
    RMB + mouse   look around (yaw / pitch)
    Ctrl + LMB    measure lat/lon/elev of a world point (persistent flyout)
    W / S         forward / back (along view)
    A / D         strafe left / right
    E / Q         up / down (world)
    Mouse wheel   change move speed
    Shift         move faster (x3)
    C             toggle the Google photoreal tileset RENDERING on/off
    G             toggle the World Terrain (bare-earth ground) tileset RENDERING on/off
    V             toggle World Terrain (ground) physics COLLISION on/off (default ON)
    R             toggle CARLA road-mesh RENDERING on/off (collision unaffected)
    B             toggle the OSM PERIMETER overlay (red rectangle + corner posts)
    M             toggle the MARGIN/interior-boundary overlay (blue rectangle)
    T             toggle TRAFFIC on/off (staging fade traffic)
    Y             toggle TELEMETRY (CoT over UDP) on/off
    F             toggle RECORDING (periodic PNG of the clean frame + matching CoT-XML sidecar)
    Space         reset to the start pose
    Esc           quit

Prereqs:
  * Headless server running (RunCarlaServer.ps1).
  * SUMO netconvert staged under Build/sumo-install (for the build phase).
  * CESIUM_ION_TOKEN env var (or --ion-token) for the spawned tileset.
  * A draped build (--height-align drape) is required for the staging traffic margin; without
    one the traffic toggle is disabled (viewing still works).
  * A server + carlanet wheel that include set_actor_fade (otherwise the traffic toggle is
    disabled — it would be a silent no-op).

Usage:
    python SCTMV.py [build opts] [view opts] [traffic opts] [telemetry opts]
                    [--async] [--fixed-delta S] [--no-build] [--start-traffic]
"""
import argparse
import math
import os
import queue
import random
import re
import signal
import socket
import sys
import threading
import time
from datetime import datetime, timedelta, timezone
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

FT_PER_M = 3.28084


# ───────────────────────────── argument parsing ─────────────────────────────

def parse_args():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)

    conn = ap.add_argument_group("connection / mode")
    conn.add_argument("--host", default="127.0.0.1", help="CARLA server host (default 127.0.0.1)")
    conn.add_argument("--port", type=int, default=2000, help="CARLA server RPC port (default 2000)")
    conn.add_argument("--tm-port", type=int, default=8000, help="Traffic Manager port (default 8000)")
    conn.add_argument("--async", dest="asynchronous", action="store_true",
                      help="run the server free-running (asynchronous). Default is synchronous, "
                           "which is what the Traffic Manager is designed for.")
    conn.add_argument("--fixed-delta", type=float, default=0.05,
                      help="synchronous mode only: simulation step in seconds; ticks are paced to "
                           "wall-clock at this rate (default 0.05 = ~20 fps / real time)")

    build = ap.add_argument_group("world build (phase 1)")
    build.add_argument("--no-build", dest="build", action="store_false", default=True,
                       help="don't build a world; attach to the one already on the server "
                            "(skip straight to viewing / traffic)")
    build.add_argument("--osm", default=os.path.join(_REPO, "Import", "Lakeview_Carson.osm"))
    build.add_argument("--lat", type=float, default=None, help="origin lat (default: OSM bounds center)")
    build.add_argument("--lon", type=float, default=None, help="origin lon (default: OSM bounds center)")
    build.add_argument("--step", type=float, default=10.0, help="reference-line sample spacing (m)")
    build.add_argument("--origin-height", type=float, default=None,
                       help="vertical datum (m); default = sample the origin")
    build.add_argument("--ion-token", default=os.environ.get("CESIUM_ION_TOKEN", ""))
    build.add_argument("--ion-asset-id", type=int, default=2275207,
                       help="Cesium ion asset for the visual photoreal tileset")
    build.add_argument("--ground-asset-id", type=int, default=1,
                       help="Cesium ion asset for the hidden bare-earth terrain layer whose heights "
                            "set the road elevations (default 1 = Cesium World Terrain; 0 = take "
                            "heights from the photoreal surface instead, legacy)")
    build.add_argument("--height-align", choices=["area", "origin", "none", "drape"], default="none",
                       help="how roads and drivable ground match the photoreal imagery: 'none' "
                            "(default) leaves them on the bare-earth terrain; 'area'/'origin' raise "
                            "everything by one constant height; 'drape' matches the photoreal "
                            "point-by-point (required for the staging traffic margin). Telemetry "
                            "altitude stays true bare-earth in every mode.")
    build.add_argument("--terrain-res", type=float, default=2.0,
                       help="'drape' only: spacing (m) between drivable-surface points (default 2.0)")
    build.add_argument("--terrain-margin", type=float, default=30.48,
                       help="'drape' only: width (m) of the staging ring just inside the map edge "
                            "where boundary-aware traffic enters/exits (default ~100 ft)")
    build.add_argument("--drape-cache-dir", default=None,
                       help="'drape' only: folder to cache terrain-height samples so rebuilds skip "
                            "the slow re-sampling")
    build.add_argument("--no-ground-collision", dest="ground_collision", action="store_false",
                       default=True, help="disable collision on the bare-earth ground (default ON)")
    build.add_argument("--settle", type=float, default=10.0, help="Cesium settle seconds during build")
    build.add_argument("--no-road-filter", action="store_true",
                       help="don't restrict netconvert to car-drivable roads")
    build.add_argument("--no-clip-bounds", action="store_true",
                       help="don't clip the road network to the OSM <bounds>")
    build.add_argument("--save", default=None,
                       help="output elevated .xodr (default: Build/sumo-smoketest/<osm>_elevated.xodr)")
    build.add_argument("--timeout", type=float, default=300.0, help="build RPC timeout (s)")

    view = ap.add_argument_group("EO observer (phase 2)")
    view.add_argument("--z", type=float, default=1000.0, help="start altitude in FEET (default 1000)")
    view.add_argument("--x", type=float, default=0.0, help="camera start x (CARLA metres)")
    view.add_argument("--y", type=float, default=0.0, help="camera start y (CARLA metres; -Y is North)")
    view.add_argument("--fov", type=float, default=90.0)
    view.add_argument("--ev", type=float, default=0.0,
                      help="camera exposure_compensation (EV); >0 brightens")
    view.add_argument("--time", default=None,
                      help="start local solar time as HH:MM or decimal hours (default: 12:00, local "
                           "solar noon). The sun's time zone is derived from the map longitude, so "
                           "noon is high sun wherever the OSM origin is.")
    view.add_argument("--date", default=None,
                      help="scene date as YYYY-MM-DD (default: host system date). Sets the seasonal "
                           "sun angle; not for historical/almanac accuracy.")
    view.add_argument("--time-advance", action="store_true",
                      help="advance the sun over time as the scene runs (toggle at runtime with K). "
                           "It advances with the world tick: WALL-CLOCK time in --async, but "
                           "SIMULATION time under synchronous ticking (so a paused/slow sim slows the "
                           "sun). At rate 1.0 a noon start reaches midnight after ~12 h of runtime.")
    view.add_argument("--time-rate", type=float, default=1.0,
                      help="sun-clock seconds per real/sim second when advancing (1.0 = real time; "
                           ">1 accelerates, e.g. 3600 = one hour of sun per second).")
    view.add_argument("--speed", type=float, default=60.0, help="initial move speed (m/s)")
    view.add_argument("--width", type=int, default=1280)
    view.add_argument("--height", type=int, default=720)

    traf = ap.add_argument_group("staging traffic (phase 3)")
    traf.add_argument("--start-traffic", action="store_true",
                      help="begin with traffic enabled (otherwise toggle it on with T)")
    traf.add_argument("--max", type=int, default=30, help="max vehicles alive at once (default 30)")
    traf.add_argument("--spawn-interval", type=float, default=0.7,
                      help="seconds between spawn attempts while below --max (default 0.7)")
    traf.add_argument("--filter", default="vehicle.*", help="vehicle blueprint filter")
    traf.add_argument("--generation", default="all",
                      help="vehicle blueprint generation to use: 1, 2, 3, or all (default all)")
    traf.add_argument("--seed", type=int, default=None,
                      help="random seed for repeatable spawns/destinations and (in synchronous mode) "
                           "the Traffic Manager (default: nondeterministic)")
    traf.add_argument("--no-fade", dest="fade", action="store_false", default=True,
                      help="don't apply the opacity fade — spawn and despawn vehicles at FULL opacity. "
                           "Diagnostic: makes it obvious whether vehicles are actually driving (rather "
                           "than being hidden by the fade while they sit at the margin).")
    traf.add_argument("--route", action="store_true",
                      help="use the Traffic Manager's custom-path routing to send each vehicle toward "
                           "a far edge. OFF by default because routing can occasionally push a vehicle "
                           "off the road or off a clipped dead-end.")

    tel = ap.add_argument_group("CoT telemetry (phase 4)")
    tel.add_argument("--tak-host", default="239.2.3.1",
                     help="TAK CoT destination (default 239.2.3.1 = TAK SA multicast; set to a WinTAK "
                          "IP for unicast)")
    tel.add_argument("--tak-port", type=int, default=6969, help="TAK CoT UDP port (default 6969)")
    tel.add_argument("--rate", type=float, default=5.0, help="telemetry emit rate Hz (>=5)")
    tel.add_argument("--affiliation", default="n",
                     help="CoT standard-identity: n neutral / u unknown / f friend / h hostile")
    tel.add_argument("--stale", type=float, default=3.0, help="CoT stale seconds")
    tel.add_argument("--ttl", type=int, default=1, help="multicast TTL")
    tel.add_argument("--print", action="store_true", dest="echo", help="also print each CoT event")

    rec = ap.add_argument_group("recording (F hotkey)")
    rec.add_argument("--record-dir", default=os.path.join(_REPO, "Build", "SCTMV_recordings"),
                     help="folder for recordings (default Build/SCTMV_recordings). F toggles recording: "
                          "each capture writes a lossless PNG of the clean streamed imagery (no HUD) plus "
                          "a matching .xml Cursor-on-Target sidecar at that instant — the vehicle tracks "
                          "and the collection platform (the camera itself) as an air track.")
    rec.add_argument("--record-hz", type=float, default=2.0,
                     help="capture rate in Hz (captures per second; may be fractional, e.g. 0.5). "
                          "Default 2.0.")
    rec.add_argument("--platform-type", default="uas-fixed",
                     help="collection-platform airframe class for the recorded sensor's CoT air track: "
                          "uas-fixed (default), uas-rotary, manned-fixed, manned-rotary, or a raw CoT "
                          "type string (e.g. a-f-A-M-F-Q).")
    rec.add_argument("--platform-affiliation", default="f",
                     help="CoT standard identity of the collection platform: f friend (default; it is our "
                          "own asset) / n neutral / u unknown / h hostile.")
    rec.add_argument("--platform-callsign", default="OVERWATCH",
                     help="callsign for the recorded platform track (default OVERWATCH).")
    rec.add_argument("--platform-uid", default=None,
                     help="CoT track uid for the platform (default: CARLA-SENSOR-<camera id>).")

    orbit = ap.add_argument_group("orbit")
    orbit.add_argument("--orbit", action="store_true", help="enable orbit at startup")
    orbit.add_argument("--orbit-x", type=float, default=None, help="orbit center X (CARLA metres)")
    orbit.add_argument("--orbit-y", type=float, default=None, help="orbit center Y (CARLA metres, -Y is North)")
    orbit.add_argument("--orbit-lat", type=float, default=None, help="orbit center latitude (alternative to --orbit-x/--orbit-y)")
    orbit.add_argument("--orbit-lon", type=float, default=None, help="orbit center longitude (alternative to --orbit-x/--orbit-y)")
    orbit.add_argument("--orbit-radius", type=float, default=656.0, help="orbit radius in FEET (default 656 = 200m)")
    orbit.add_argument("--orbit-altitude", type=float, default=1700, help="camera altitude in FEET (default: use spawn altitude)")
    orbit.add_argument("--orbit-speed", type=float, default=240.0, help="orbit speed in seconds (default 240 = 4 min)")

    return ap.parse_args()


# ───────────────────────────── world build (phase 1) ─────────────────────────────

def read_osm_bounds(path):
    """Return (minlat, minlon, maxlat, maxlon) from the OSM <bounds> element, or None."""
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                if "<bounds" in line:
                    def g(k):
                        m = re.search(k + r'="([-0-9.]+)"', line)
                        return float(m.group(1)) if m else None
                    vals = (g("minlat"), g("minlon"), g("maxlat"), g("maxlon"))
                    return vals if None not in vals else None
    except OSError:
        return None
    return None


def make_options(args):
    from CarlaNet.Map import OsmConversionOptions
    opts = OsmConversionOptions()
    opts.NetconvertPath = _NETCONVERT
    opts.ProjDataDirectory = _PROJ
    # netconvert emits the traffic-light signals + guessed phase program; TrafficLightInjector then
    # adds the per-phase controllers and <junction><controller> links netconvert omits, so CARLA
    # groups them correctly (one group per junction, one controller per phase) instead of orphaning
    # every light (the previous ungrouped-TL log spam, issue #1).
    opts.GenerateTrafficLights = True
    opts.OriginLatitude = args.lat
    opts.OriginLongitude = args.lon
    if not args.no_road_filter:
        # Restrict netconvert to car-drivable streets: drop sidewalks/footways/cycleways, all
        # rail/subway/tram, and service/parking-aisle ways; then prune disconnected bits.
        from System.Collections.Generic import List
        extra = List[str]()
        for a in ["--keep-edges.by-vclass", "passenger",
                  "--keep-edges.components", "1",
                  "--remove-edges.isolated", "true"]:
            extra.Add(a)
        opts.ExtraArgs = extra
    return opts


def build_world(client, args) -> bool:
    """Run the OSM -> elevated, Cesium-aligned OpenDRIVE build on the server. Returns True on
    success. Leaves the world built and the Cesium overlay established."""
    print("== Digital-twin build (headless, no editor) ==")
    print(f"  osm        : {args.osm}")
    if not os.path.exists(args.osm):
        print(f"ERROR: OSM not found: {args.osm}", file=sys.stderr); return False
    if not os.path.exists(_NETCONVERT):
        print(f"ERROR: netconvert not staged: {_NETCONVERT}", file=sys.stderr); return False

    if args.lat is None or args.lon is None:
        b = read_osm_bounds(args.osm)
        if b is None:
            print("ERROR: no --lat/--lon given and could not read <bounds> from the OSM file",
                  file=sys.stderr); return False
        args.lat = (b[0] + b[2]) / 2.0
        args.lon = (b[1] + b[3]) / 2.0
        print(f"  origin     : {args.lat:.7f}, {args.lon:.7f}  (derived from OSM bounds center)")
    else:
        print(f"  origin     : {args.lat:.7f}, {args.lon:.7f}  (explicit)")
    print(f"  step       : {args.step} m   road-filter: "
          f"{'OFF' if args.no_road_filter else 'ON (drivable only)'}   height-align: {args.height_align}")
    print(f"  ion asset  : {args.ion_asset_id} (photoreal)  ground: {args.ground_asset_id}  "
          f"token: {'set' if args.ion_token else 'MISSING'}")
    if not args.ion_token:
        print("WARNING: no Ion token; the tileset can't be spawned and sampling will fail.",
              file=sys.stderr)

    # Clip the road network to the selected area BEFORE conversion (an OSM export keeps whole ways
    # that merely touch the box; netconvert can't cut mid-edge). The clipped file keeps the same
    # <bounds>, so the origin and drape sizing are unchanged.
    osm_for_build = args.osm
    if not args.no_clip_bounds:
        bb = read_osm_bounds(args.osm)
        if bb is None:
            print("  clip       : skipped (no <bounds> in the OSM)")
        else:
            import osm_clip
            clipped = os.path.join(_REPO, "Build", "sumo-smoketest",
                                   os.path.splitext(os.path.basename(args.osm))[0] + "_clipped.osm")
            os.makedirs(os.path.dirname(clipped), exist_ok=True)
            nways, nbnd = osm_clip.clip_osm_to_bounds(args.osm, clipped, bb)
            osm_for_build = clipped
            print(f"  clip       : roads cut to <bounds> -> {nways} ways (+{nbnd} edge nodes)")
    else:
        print("  clip       : OFF (--no-clip-bounds)")

    save_path = args.save or os.path.join(
        _REPO, "Build", "sumo-smoketest",
        os.path.splitext(os.path.basename(args.osm))[0] + "_elevated.xodr")

    print("[build] generate_world_from_osm_with_elevation (convert -> sample -> inject -> build)...")
    print("        (blocks while sampling heights and meshing the elevated road network)")
    client.set_timeout(args.timeout)
    t0 = time.time()
    elevated = client.generate_world_from_osm_with_elevation(
        osm_for_build, args.ion_token, args.ion_asset_id,
        ground_ion_asset_id=args.ground_asset_id,
        osm_options=make_options(args),
        sample_step_meters=args.step,
        origin_height=args.origin_height,
        height_align=args.height_align,
        ground_collision=args.ground_collision,
        cesium_settle_seconds=args.settle,
        terrain_res=args.terrain_res,
        terrain_margin=args.terrain_margin,
        drape_cache_dir=args.drape_cache_dir)
    dt = time.time() - t0
    roads = elevated.count("<road ")
    elevs = elevated.count("<elevation ")
    print(f"        done in {dt:.1f}s — {len(elevated):,} chars, {roads} roads, {elevs} elevations")

    os.makedirs(os.path.dirname(save_path), exist_ok=True)
    with open(save_path, "w", encoding="utf-8") as f:
        f.write(elevated)
    print(f"        wrote elevated .xodr -> {save_path}")
    return True


# ───────────────────────────── staging-traffic geometry ─────────────────────────────

def _edge_of(x, y, b):
    """Which map edge a point is nearest to (W/E/S/N)."""
    dists = (("W", x - b["min_x"]), ("E", b["max_x"] - x),
             ("S", y - b["min_y"]), ("N", b["max_y"] - y))
    return min(dists, key=lambda t: t[1])[0]


def _scene_center(b):
    return (0.5 * (b["min_x"] + b["max_x"]), 0.5 * (b["min_y"] + b["max_y"]))


def _in_scene(x, y, b):
    """Inside the interior / region of interest (the sandbox inset by one margin)."""
    return (b["min_x"] + b["margin"] <= x <= b["max_x"] - b["margin"] and
            b["min_y"] + b["margin"] <= y <= b["max_y"] - b["margin"])


def _in_ring(x, y, b):
    """Inside the sandbox but within the staging margin of an edge (the entry/exit ring)."""
    inside = (b["min_x"] <= x <= b["max_x"] and b["min_y"] <= y <= b["max_y"])
    return inside and not _in_scene(x, y, b)


def _inward_min(x, y, b):
    """Signed distance to the nearest interior (blue) edge: +ve inside the interior, -ve in margin."""
    m = b["margin"]
    return min(x - (b["min_x"] + m), (b["max_x"] - m) - x,
               y - (b["min_y"] + m), (b["max_y"] - m) - y)


def _red_clearance(x, y, b):
    """Distance (m) to the nearest red (sandbox) edge — the literal map edge."""
    return min(x - b["min_x"], b["max_x"] - x, y - b["min_y"], b["max_y"] - y)


def _is_inward(tf, b):
    """Spawning here and driving forward heads into the scene rather than off the edge."""
    cx, cy = _scene_center(b)
    yaw = math.radians(tf.rotation.yaw)
    return math.cos(yaw) * (cx - tf.location.x) + math.sin(yaw) * (cy - tf.location.y) > 0.0


def _interior_opacity(cx, cy, yaw_deg, ext_x, ext_y, b):
    """Opacity [0,1] = the fraction of the vehicle's footprint that lies INSIDE the interior (past
    the blue line, one margin in from the red edge). 0 = wholly within the margin (transparent); 1 =
    wholly in the interior (opaque). The change spans the vehicle's own length as it straddles the
    nearest blue boundary."""
    sW = cx - (b["min_x"] + b["margin"])
    sE = (b["max_x"] - b["margin"]) - cx
    sS = cy - (b["min_y"] + b["margin"])
    sN = (b["max_y"] - b["margin"]) - cy
    axis, s = min((("x", sW), ("x", sE), ("y", sS), ("y", sN)), key=lambda e: e[1])
    yaw = math.radians(yaw_deg)
    hx = abs(ext_x * math.cos(yaw)) + abs(ext_y * math.sin(yaw))
    hy = abs(ext_x * math.sin(yaw)) + abs(ext_y * math.cos(yaw))
    h = hx if axis == "x" else hy
    if h <= 1e-3:
        return 1.0 if s >= 0.0 else 0.0
    return max(0.0, min(1.0, (s + h) / (2.0 * h)))


class TrafficController:
    """Boundary-aware staging traffic as a steppable subsystem. enable()/disable() start and stop
    the population; update(now) does the per-frame spawn top-up, reconcile, and opacity fade. Stopping
    despawns every tracked vehicle cleanly."""

    OOB_PAD = 2.0       # metres beyond the red (sandbox) edge before a vehicle is culled as having left
    RED_CLEAR = 3.0     # despawn before an entered vehicle gets this close to the red (map) edge
    CHECK_S = 0.1       # reconcile/fade cadence
    MISS_LIMIT = 5      # consecutive cache-misses before treating a vehicle as gone
    SPAWN_GRACE = 4.0   # a fresh vehicle is exempt from the stuck/out-of-bounds guards this long, so
                        # the Traffic Manager has time to pick it up and drive it inward before any
                        # guard can cull it (a tightly clipped / sloped edge spawn would otherwise
                        # trip the floor or stuck guard before the car has even started moving)
    SUMMARY_S = 5.0     # how often to print the alive/despawn-reason summary while enabled

    _TWO_WHEELED = ("harley", "kawasaki", "yamaha", "vespa", "motorcycle", "omafiets",
                    "crossbike", "bike", "bicycle", "diamondback", "gazelle")

    def __init__(self, world, tm, args, staging, blueprints, ring_sps, spawn_pool, floor_z):
        self.world = world
        self.tm = tm
        self.args = args
        self.b = staging
        self.blueprints = blueprints
        self.ring_sps = ring_sps
        self.spawn_pool = spawn_pool
        self.floor_z = floor_z
        self.available = True
        self.reason = ""
        self.enabled = False
        self.want_enabled = False   # desired state, flipped by the hotkey on the main thread
        self.actors = {}
        self.last_spawn = 0.0
        self.last_check = 0.0
        self.last_summary = 0.0
        self.despawns = {}   # reason -> count, accumulated between summaries (diagnostics)

    def apply_want(self):
        """Reconcile actual on/off with the hotkey's desired state. Called on whichever thread owns
        the RPCs (the background worker in async, the tick loop in sync)."""
        if self.want_enabled and not self.enabled:
            if not self.enable():
                self.want_enabled = False   # unavailable -> drop the desire so we don't retry-spam
        elif not self.want_enabled and self.enabled:
            self.disable()

    # ---- construction / capability probe ----
    @classmethod
    def create(cls, world, client, tm, args):
        """Build the controller, computing the spawn pool and verifying set_actor_fade. On any
        capability gap, returns a disabled controller whose .reason explains why."""
        stub = cls(world, tm, args, None, [], [], [], -1000.0)
        try:
            staging = world.get_staging_bounds()
        except Exception as e:
            stub.available = False; stub.reason = f"get_staging_bounds failed: {e!r}"; return stub
        if not staging:
            stub.available = False
            stub.reason = "no staging bounds (build a draped world: --height-align drape)"
            return stub

        bp_lib = world.get_blueprint_library()
        blueprints = list(bp_lib.filter(args.filter))
        cars = [b for b in blueprints if not cls._is_two_wheeled(b)]
        if cars:
            blueprints = cars
        if args.generation != "all":
            try:
                gen = int(args.generation)
                blueprints = [b for b in blueprints
                              if b.has_attribute('generation')
                              and int(b.get_attribute('generation')) == gen]
            except Exception:
                print(f"warning: bad --generation {args.generation!r}; ignoring", file=sys.stderr)
        if not blueprints:
            stub.available = False
            stub.reason = "no vehicle blueprints matched --filter / --generation"
            return stub

        ring_sps = [sp for sp in world.get_map().get_spawn_points()
                    if _in_ring(sp.location.x, sp.location.y, staging) and _is_inward(sp, staging)]
        if len(ring_sps) < 2:
            stub.available = False
            stub.reason = (f"only {len(ring_sps)} inward edge-ring spawn points; need >=2 "
                           "(select a larger OSM area or a smaller --terrain-margin)")
            return stub
        spawn_pool = [sp for sp in ring_sps
                      if _inward_min(sp.location.x, sp.location.y, staging) <= -2.0
                      and _red_clearance(sp.location.x, sp.location.y, staging) >= 5.0]
        if len(spawn_pool) < 8:
            spawn_pool = ring_sps

        cx, cy = _scene_center(staging)
        try:
            gz = world.ground_z_below(cx, cy, 5000.0, search=10000.0)
            floor_z = (float(gz) - 50.0) if gz is not None else -1000.0
        except Exception:
            floor_z = -1000.0

        ctl = cls(world, tm, args, staging, blueprints, ring_sps, spawn_pool, floor_z)
        # Prove set_actor_fade is wired (else the demo would be silently solid).
        if not ctl._fade_selftest():
            ctl.available = False
            ctl.reason = ("set_actor_fade not available — rebuild server + wheel "
                          "(BuildCarla.ps1 -Vs 2026 -InstallWheel)")
            return ctl
        sw = staging["max_x"] - staging["min_x"]; sh = staging["max_y"] - staging["min_y"]
        m = staging["margin"]
        print(f"traffic: scene {sw:.0f} x {sh:.0f} m, margin {m:.0f} m "
              f"(interior {sw - 2*m:.0f} x {sh - 2*m:.0f} m)")
        rc = [_red_clearance(sp.location.x, sp.location.y, staging) for sp in spawn_pool]
        im = [_inward_min(sp.location.x, sp.location.y, staging) for sp in spawn_pool]
        print(f"traffic: spawn-pool red-clearance {min(rc):.0f}..{max(rc):.0f} m, "
              f"inward {min(im):.0f}..{max(im):.0f} m (negative inward = inside the margin, as intended)")
        print(f"traffic: {len(ring_sps)} inward edge-ring spawn points; "
              f"{len(spawn_pool)} usable in-margin spawn points (set_actor_fade OK)")
        return ctl

    @classmethod
    def _is_two_wheeled(cls, b):
        try:
            return any(k in str(b.id).lower() for k in cls._TWO_WHEELED)
        except Exception:
            return False

    def _spawn_bp(self):
        bp = random.choice(self.blueprints)
        if bp.has_attribute('color'):
            bp.set_attribute('color', random.choice(bp.get_attribute('color').recommended_values))
        bp.set_attribute('role_name', 'autopilot')
        return bp

    def _fade_selftest(self):
        probe = None
        for sp in random.sample(self.ring_sps, min(len(self.ring_sps), 8)):
            try:
                probe = self.world.spawn_actor(self._spawn_bp(), sp); break
            except Exception:
                continue
        if probe is None:
            return False
        ok = True
        try:
            probe.set_fade(0.5)
        except Exception:
            ok = False
        try:
            probe.destroy()
        except Exception:
            pass
        return ok

    def _pick_destination(self, spawn_tf):
        s_edge = _edge_of(spawn_tf.location.x, spawn_tf.location.y, self.b)
        cands = [sp for sp in self.ring_sps
                 if _edge_of(sp.location.x, sp.location.y, self.b) != s_edge] or self.ring_sps
        cands.sort(key=lambda sp: -spawn_tf.location.distance(sp.location))
        return random.choice(cands[:max(1, len(cands) // 2)])

    @staticmethod
    def _safe_fade(v, hide):
        try:
            v.set_fade(hide)
        except Exception:
            pass

    def _clear_shift(self, loc, yaw_deg, ext, pad=0.6):
        """Offset (dx, dy) to move a vehicle FORWARD along its lane just far enough that its whole
        footprint clears the nearest red (sandbox) edge. On a tightly-clipped map the spawn point sits
        right on the edge, so the vehicle's centre is 0-3 m in while its body pokes outside; the spawn
        faces inward, so a small forward nudge along the lane pulls the body fully inside. Returns
        (0, 0) if already clear or if the lane runs ~parallel to the edge (forward wouldn't help)."""
        b = self.b
        edges = (("W", loc.x - b["min_x"], (1.0, 0.0)),
                 ("E", b["max_x"] - loc.x, (-1.0, 0.0)),
                 ("S", loc.y - b["min_y"], (0.0, 1.0)),
                 ("N", b["max_y"] - loc.y, (0.0, -1.0)))
        _, c, n = min(edges, key=lambda e: e[1])     # c = clearance to nearest red edge, n = inward normal
        yaw = math.radians(yaw_deg)
        # AABB half-extent along that edge's axis (same projection as _interior_opacity).
        he = (abs(ext[0] * math.cos(yaw)) + abs(ext[1] * math.sin(yaw))) if n[0] != 0.0 \
            else (abs(ext[0] * math.sin(yaw)) + abs(ext[1] * math.cos(yaw)))
        deficit = (he + pad) - c
        if deficit <= 0:
            return (0.0, 0.0)
        ux, uy = math.cos(yaw), math.sin(yaw)
        dot = ux * n[0] + uy * n[1]                   # how much 'forward' points inward
        if dot < 0.2:                                 # lane ~parallel to the edge: forward won't clear
            return (0.0, 0.0)
        d = min(deficit / dot, b["margin"])           # don't push past one margin
        return (ux * d, uy * d)

    # Conservative bounding radius (half-diagonal, m) assumed for a not-yet-spawned vehicle when
    # testing whether a spawn site is clear — sized to cover long vehicles (e.g. the Carla Cola truck).
    _NEW_VEHICLE_RADIUS = 3.7
    _SPAWN_CLEAR_PAD = 1.0

    def _occupied(self, x, y):
        """True if any tracked vehicle's footprint is close enough to (x, y) that spawning there would
        overlap it. Uses each vehicle's last-known position + its bounding radius, so a spawn site stays
        'occupied' until the previous vehicle has actually driven clear of it."""
        need = self._NEW_VEHICLE_RADIUS + self._SPAWN_CLEAR_PAD
        for rec in self.actors.values():
            p = rec["xy"]
            if p is None:
                continue
            pr = math.hypot(rec["ext"][0], rec["ext"][1])
            if math.hypot(x - p[0], y - p[1]) < (need + pr):
                return True
        return False

    def _spawn_one(self, now):
        pool = list(self.spawn_pool); random.shuffle(pool)
        for sp in pool:
            # Skip a site whose final (forward-nudged) pose is still occupied by a vehicle that hasn't
            # driven off yet — otherwise the new one spawns on top of it and they collide. Estimated
            # with a default extent (real extent isn't known until after spawn); the pad absorbs the
            # small difference. This is what makes the spawn cadence wait for the lane to clear.
            ex, ey = self._clear_shift(sp.location, sp.rotation.yaw, (3.5, 1.1))
            if self._occupied(sp.location.x + ex, sp.location.y + ey):
                continue
            try:
                v = self.world.spawn_actor(self._spawn_bp(), sp)
            except Exception:
                continue
            try:
                bb = v.bounding_box
                ext = (float(bb.extent.x), float(bb.extent.y))
            except Exception:
                ext = (2.4, 1.0)
            # Nudge the vehicle forward along its lane so the whole footprint is inside the red edge
            # (the spawn point itself sits on the edge on a tightly-clipped map). Uses the real extent,
            # so it scales to long vehicles (e.g. the Carla Cola truck).
            sx, sy, syaw = sp.location.x, sp.location.y, sp.rotation.yaw
            dx, dy = self._clear_shift(sp.location, syaw, ext)
            if dx or dy:
                sx += dx; sy += dy
                try:
                    v.set_transform(carla.Transform(
                        carla.Location(x=sx, y=sy, z=sp.location.z),
                        carla.Rotation(pitch=sp.rotation.pitch, yaw=syaw, roll=sp.rotation.roll)))
                except Exception:
                    sx, sy = sp.location.x, sp.location.y   # shift failed; fall back to the raw spawn
            try:
                op = _interior_opacity(sx, sy, syaw, ext[0], ext[1], self.b)
                if self.args.fade:
                    self._safe_fade(v, 1.0 - op)
                v.set_autopilot(True, self.args.tm_port)
                if self.args.route:
                    self.tm.set_path(v, [self._pick_destination(sp).location])
            except Exception as e:
                print(f"  setup failed for {v.id}: {e!r}", file=sys.stderr)
                try: v.destroy()
                except Exception: pass
                continue
            # Seed xy with the actual spawn pose (not None) so the very next spawn's occupancy test
            # already accounts for this vehicle before its first reconcile.
            self.actors[v.id] = {"actor": v, "ext": ext, "entered": False, "born": now,
                                 "xy": (sx, sy), "stuck": 0.0, "misses": 0, "speed": 0.0}
            return True
        return False

    def _despawn(self, vid, actor, reason="other"):
        self.despawns[reason] = self.despawns.get(reason, 0) + 1
        if self.args.fade:
            try: actor.set_fade(1.0)        # force transparent so a lagging destroy is never seen
            except Exception: pass
        try: actor.set_autopilot(False, self.args.tm_port)
        except Exception: pass
        try: actor.destroy()
        except Exception: pass
        self.actors.pop(vid, None)

    def enable(self):
        if not self.available:
            print(f"traffic toggle ignored: {self.reason}", file=sys.stderr)
            return False
        if not self.enabled:
            self.enabled = True
            self.last_spawn = 0.0
            print(f"traffic ON (up to {self.args.max}, "
                  f"{'routed' if self.args.route else 'autopilot'})")
        return True

    def disable(self):
        if self.actors:
            print("traffic OFF; despawning vehicles...")
        for vid in list(self.actors.keys()):
            self._despawn(vid, self.actors[vid]["actor"])
        self.enabled = False

    def update(self, now):
        if not self.enabled:
            return
        b = self.b
        if len(self.actors) < self.args.max and (now - self.last_spawn) >= self.args.spawn_interval:
            self.last_spawn = now
            self._spawn_one(now)
        if now - self.last_check < self.CHECK_S:
            return
        self.last_check = now

        mnx, mny, mxx, mxy = b["min_x"], b["min_y"], b["max_x"], b["max_y"]
        ids = list(self.actors.keys())
        live_ids = set()
        if ids:
            try:
                live_ids = {a.id for a in self.world.get_actors(ids)}
            except Exception:
                live_ids = set(ids)
            if not live_ids:
                live_ids = set(ids)
        for vid in ids:
            rec = self.actors[vid]
            if vid not in live_ids:
                rec["misses"] += 1
                if rec["misses"] >= self.MISS_LIMIT:
                    self.actors.pop(vid, None)
                continue
            rec["misses"] = 0
            a = rec["actor"]
            try:
                tf = a.get_transform()
            except Exception:
                self.actors.pop(vid, None); continue
            loc = tf.location; yaw = tf.rotation.yaw
            armed = (now - rec["born"]) >= self.SPAWN_GRACE   # past the spawn-arming grace?

            # Speed from per-reconcile displacement — sampled for EVERY vehicle BEFORE any despawn, so
            # the summary reflects all cars present, not just the ones that survive the frame.
            xy = rec["xy"]; rec["xy"] = (loc.x, loc.y)
            dist = math.hypot(loc.x - xy[0], loc.y - xy[1]) if xy is not None else 0.0
            rec["speed"] = dist / self.CHECK_S

            # Opacity drives the fade: solid once the footprint is fully past the blue line, invisible
            # once fully back within a margin.
            op = _interior_opacity(loc.x, loc.y, yaw, rec["ext"][0], rec["ext"][1], b)
            if self.args.fade:
                self._safe_fade(a, 1.0 - op)

            # Crossing the red (sandbox) edge horizontally culls IMMEDIATELY — even during the grace —
            # so nothing is seen outside the boundary. The grace covers only the vertical floor check.
            if (loc.x < mnx - self.OOB_PAD or loc.x > mxx + self.OOB_PAD or
                    loc.y < mny - self.OOB_PAD or loc.y > mxy + self.OOB_PAD or
                    (armed and loc.z < self.floor_z)):
                self._despawn(vid, a, "oob"); continue

            # The interior logic (entered / exited / edge / stuck) runs ONLY once armed. During the
            # spawn grace a sync-mode vehicle briefly reports a placeholder pose near the world origin
            # (= scene centre, op~1) before physics places it; running this then would mis-read it as
            # 'entered' and immediately 'exited' — the spurious instant pop-in/pop-out.
            if armed:
                if op >= 0.99:
                    rec["entered"] = True
                if rec["entered"] and op <= 0.02:
                    self._despawn(vid, a, "exited"); continue
                if rec["entered"] and _red_clearance(loc.x, loc.y, b) <= self.RED_CLEAR:
                    self._despawn(vid, a, "red-edge"); continue
                # Stuck (clipped dead-end stub / sunk spawn / Traffic Manager not driving).
                if rec["entered"] or xy is None or dist > 0.05:
                    rec["stuck"] = 0.0
                else:
                    rec["stuck"] += self.CHECK_S
                    if rec["stuck"] >= 6.0:
                        self._despawn(vid, a, "stuck"); continue

        # Periodic diagnostics: how many are alive and why any despawned since the last summary. This
        # is what tells us whether the churn is 'stuck' (Traffic Manager not driving in sync mode),
        # 'oob' (sloped-edge floor), or 'exited' (healthy margin-to-margin turnover).
        if now - self.last_summary >= self.SUMMARY_S:
            self.last_summary = now
            entered = sum(1 for r in self.actors.values() if r["entered"])
            speeds = [r["speed"] for r in self.actors.values()]
            avg = sum(speeds) / len(speeds) if speeds else 0.0
            mx = max(speeds) if speeds else 0.0
            reasons = " ".join(f"{k}={v}" for k, v in sorted(self.despawns.items())) or "none"
            print(f"traffic: {len(self.actors)} alive ({entered} entered, "
                  f"speed avg {avg:.1f} max {mx:.1f} m/s) | despawns/{self.SUMMARY_S:.0f}s: {reasons}")
            self.despawns.clear()

    def count(self):
        return len(self.actors)


# ───────────────────────────── CoT telemetry (phase 4) ─────────────────────────────

def _iso(dt: datetime) -> str:
    """CoT timestamp: ISO-8601 UTC with millisecond precision and a 'Z'."""
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"


def to_cot(rec, affiliation="n", stale_seconds=3.0, source="truth", uid_prefix="CARLA-TRUTH",
           when=None, solar=None, capture=None) -> str:
    """Render one get_vehicle_telemetry() dict as a CoT <event> XML string. `when` (a UTC datetime)
    pins the event time to a specific instant — used so a recorded sidecar matches its PNG exactly.
    `solar` (a get_solar_state() dict) adds a <_solar> element carrying the sun in effect; for recorded
    imagery the PNG tEXt chunk / the sidecar's top-level <_solar> are the authoritative carriers."""
    now = when or datetime.now(timezone.utc)
    stale = now + timedelta(seconds=stale_seconds)
    ev = ET.Element("event", {
        "version": "2.0", "uid": f"{uid_prefix}-{rec['id']}",
        "type": f"a-{affiliation}-G-E-V",
        "how": "m-g" if source == "truth" else "m-f",
        "time": _iso(now), "start": _iso(now), "stale": _iso(stale),
    })
    ET.SubElement(ev, "point", {
        "lat": f"{rec['lat']:.7f}", "lon": f"{rec['lon']:.7f}", "hae": f"{rec['hae']:.2f}",
        "ce": "0.0" if source == "truth" else f"{float(rec.get('ce', 0.0)):.1f}",
        "le": "0.0" if source == "truth" else f"{float(rec.get('le', 0.0)):.1f}",
    })
    detail = ET.SubElement(ev, "detail")
    ET.SubElement(detail, "track",
                  {"course": f"{rec['course_deg']:.1f}", "speed": f"{rec['speed_mps']:.2f}"})
    ET.SubElement(detail, "contact", {"callsign": f"{rec['base_type']}-{rec['id']}"})
    ET.SubElement(detail, "_carla", {
        "source": source, "actor_id": str(rec["id"]), "type_id": rec["type_id"],
        "base_type": rec["base_type"], "special_type": rec["special_type"],
        "length_m": f"{rec['length_m']:.2f}", "width_m": f"{rec['width_m']:.2f}",
        "height_m": f"{rec['height_m']:.2f}", "color": rec["color"], "role_name": rec["role_name"],
        "vx": f"{rec['vx']:.2f}", "vy": f"{rec['vy']:.2f}", "vz": f"{rec['vz']:.2f}",
    })
    if capture is not None:
        # Diagnostic only. This feed drives a live map display and is not a truth source — the recorded
        # sidecar is the sole truth for a frame. The tick rides along so that a disagreement between the
        # live view and a sidecar can be settled by checking whether they describe the same instant.
        ET.SubElement(detail, "_capture", capture.attributes())
    if solar:
        ET.SubElement(detail, "_solar", {
            "solar_time": f"{solar['solar_time']:.4f}",
            "date": f"{solar['year']:04d}-{solar['month']:02d}-{solar['day']:02d}",
            "time_zone": f"{solar['time_zone']:.4f}",
            "sun_elevation_deg": f"{solar['sun_elevation_deg']:.3f}",
            "sun_azimuth_deg": f"{solar['sun_azimuth_deg']:.3f}",
            "advancing": "true" if solar["advancing"] else "false",
            "rate": f"{solar['rate']:g}",
        })
    return ET.tostring(ev, encoding="unicode")


class SimClock:
    """Latest world tick and simulation time, cached from the world-observer stream.

    The recorder reads the tick from each sensor frame's own header. The live telemetry feed has no
    frame to read, so it subscribes here instead.
    """

    def __init__(self, world):
        self.frame = 0
        self.sim_time = 0.0
        try:
            world.on_tick(self._on_tick)
        except Exception as e:
            print(f"tick subscription failed; emitted telemetry will report tick 0: {e!r}",
                  file=sys.stderr)

    def _on_tick(self, ts):
        self.frame = int(ts.frame)
        self.sim_time = float(ts.elapsed_seconds)

    def attributes(self):
        """The tick, as a CoT attribute. Deliberately just the tick: the run identity belongs with the
        recorded artifacts that constitute truth, not on a presentation feed that no consumer reads it
        from."""
        return {"tick": str(self.frame)}


class CotUdpEmitter:
    """One CoT <event> per UDP datagram. Works for unicast or multicast (sets the multicast TTL)."""
    def __init__(self, host, port, ttl=1):
        self._addr = (host, int(port))
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        # Harmless for unicast; lets the TAK default SA multicast group work out of the box.
        self._sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, int(ttl))

    def send(self, cot_xml: str):
        self._sock.sendto(cot_xml.encode("utf-8"), self._addr)

    def close(self):
        self._sock.close()


class TelemetryController:
    """CoT-over-UDP emitter as a steppable subsystem. Needs the georeference origin; if it is
    missing the toggle is disabled. Off by default."""

    def __init__(self, world, origin, args, clock=None):
        self.world = world
        self.origin = origin
        self.args = args
        self.clock = clock
        self.available = origin is not None
        self.reason = "" if self.available else "no georeference origin (get_cesium_origin failed)"
        self.enabled = False
        self.want_enabled = False   # desired state, flipped by the hotkey on the main thread
        self.emit = None
        self.period = 1.0 / max(0.1, args.rate)
        self.last = 0.0
        self.last_count = 0

    def apply_want(self):
        """Reconcile actual on/off with the hotkey's desired state. Called on whichever thread owns
        the RPCs (the background worker in async, the tick loop in sync)."""
        if self.want_enabled and not self.enabled:
            if not self.enable():
                self.want_enabled = False   # unavailable -> drop the desire so we don't retry-spam
        elif not self.want_enabled and self.enabled:
            self.disable()

    def enable(self):
        if not self.available:
            print(f"telemetry toggle ignored: {self.reason}", file=sys.stderr)
            return False
        if self.emit is None:
            self.emit = CotUdpEmitter(self.args.tak_host, self.args.tak_port, ttl=self.args.ttl)
        self.enabled = True
        print(f"telemetry ON -> udp://{self.args.tak_host}:{self.args.tak_port} @ {self.args.rate} Hz")
        return True

    def disable(self):
        self.enabled = False
        print("telemetry OFF")

    def update(self, now):
        if not self.enabled or (now - self.last) < self.period:
            return
        self.last = now
        try:
            recs = self.world.get_vehicle_telemetry(self.origin)
        except Exception as e:
            print(f"get_vehicle_telemetry failed: {e!r}", file=sys.stderr); return
        try:
            solar = self.world.get_solar_state()   # cache read (paired to the latest tick)
        except Exception:
            solar = None
        for r in recs:
            xml = to_cot(r, affiliation=self.args.affiliation, stale_seconds=self.args.stale,
                         solar=solar, capture=self.clock)
            self.emit.send(xml)
            if self.args.echo:
                print(xml)
        self.last_count = len(recs)

    def close(self):
        if self.emit is not None:
            self.emit.close()


class NativeRecorder:
    """Drives the in-engine (C#) FrameRecorder: camera frames are tapped, encoded to PNG, and written
    with their CoT-XML telemetry sidecar (vehicle tracks + the collection platform as a CoT air track)
    entirely on the .NET thread pool — no frame ever crosses to Python and the GIL is never held, so the
    viewer stays smooth while recording. SCTMV only toggles it (want_enabled / apply_want / trigger /
    saved / recording / stop), so the loop, hotkey, and HUD stay decoupled from the recorder."""

    def __init__(self, world, camera, args, run_id=None):
        self.world = world
        self.camera = camera
        self.args = args
        self.run_id = run_id
        self.available = bool(getattr(carla, "_CARLANET_RECORDING_AVAILABLE", False))
        self.recording = False
        self.want_enabled = False
        self._handle = None

    def apply_want(self):
        if self.want_enabled and not self.recording:
            if not self.available:
                print("recording unavailable: CarlaNet.Recording not built (rebuild the DLLs).",
                      file=sys.stderr)
                self.want_enabled = False
                return
            self._handle = self.world.start_recording(
                self.camera, self.args.record_dir, self.args.record_hz,
                self.args.affiliation, self.args.stale, fov=self.args.fov,
                platform_type=self.args.platform_type,
                platform_affiliation=self.args.platform_affiliation,
                platform_callsign=self.args.platform_callsign,
                platform_uid=self.args.platform_uid,
                run_id=self.run_id, seed=self.args.seed)
            if self._handle is None:
                self.want_enabled = False
                return
            self.recording = True
            note = "" if self._handle.HaveTelemetryOrigin else " (PNG only; no georef origin for XML)"
            print(f"recording (native) -> {self.args.record_dir} @ {self.args.record_hz} Hz{note}")
        elif not self.want_enabled and self.recording:
            n = self.saved
            self.world.stop_recording()
            self.recording = False
            self._handle = None
            print(f"recording stopped: {n} capture(s) saved")

    def trigger(self, now, surface):
        pass    # the native recorder taps the camera stream itself; nothing to feed from Python

    @property
    def saved(self):
        try:
            return int(self._handle.Saved) if self._handle is not None else 0
        except Exception:
            return 0

    def stop(self):
        if self.recording:
            try:
                self.world.stop_recording()
            except Exception:
                pass
            self.recording = False
            self._handle = None


# ───────────────────────────── EO observer rendering helpers ─────────────────────────────

def _to_surface(image):
    arr = np.frombuffer(bytes(image.raw_data), dtype=np.uint8)
    arr = np.reshape(arr, (image.height, image.width, 4))
    arr = arr[:, :, :3][:, :, ::-1]
    return pygame.surfarray.make_surface(arr.swapaxes(0, 1))


def _draw_compass(display, font, cx, cy, r, bearing_deg):
    """North-up compass rose at (cx, cy); bearing_deg is the camera heading (0=N, 90=E, cw)."""
    b = math.radians(bearing_deg)
    bg = pygame.Surface((2 * r + 4, 2 * r + 4), pygame.SRCALPHA)
    pygame.draw.circle(bg, (0, 0, 0, 150), (r + 2, r + 2), r)
    pygame.draw.circle(bg, (0, 255, 255), (r + 2, r + 2), r, 1)
    display.blit(bg, (cx - r - 2, cy - r - 2))
    for label, cb, col in (("N", 0, (255, 80, 80)), ("E", 90, (220, 220, 220)),
                           ("S", 180, (220, 220, 220)), ("W", 270, (220, 220, 220))):
        a = math.radians(cb - bearing_deg)
        lx = cx + math.sin(a) * (r - 10)
        ly = cy - math.cos(a) * (r - 10)
        s = font.render(label, True, col)
        display.blit(s, (lx - s.get_width() / 2, ly - s.get_height() / 2))
    pygame.draw.line(display, (255, 80, 80), (cx, cy),
                     (cx - math.sin(b) * (r - 4), cy - math.cos(b) * (r - 4)), 2)
    pygame.draw.line(display, (160, 160, 160), (cx, cy),
                     (cx + math.sin(b) * (r - 4), cy + math.cos(b) * (r - 4)), 2)
    pygame.draw.circle(display, (0, 255, 255), (cx, cy), 2)
    hdg = font.render(f"{bearing_deg:03.0f}", True, (255, 255, 0))
    display.blit(hdg, (cx - hdg.get_width() / 2, cy + r + 2))


def _draw_orbit_viz(display, cx, cy, r, angle):
    """Orbit visualization showing camera position on circular path.
    
    Args:
        display: pygame display surface
        cx, cy: center position for the visualization
        r: radius of the visualization circle
        angle: current orbit angle in radians (0 = East, π/2 = North in CARLA coords)
    """
    # Draw orbit circle
    pygame.draw.circle(display, (100, 100, 100), (cx, cy), r, 1)
    # Draw center point
    pygame.draw.circle(display, (255, 80, 80), (cx, cy), 3)
    # Draw camera position on orbit
    viz_cam_x = cx + int(r * math.cos(angle))
    viz_cam_y = cy + int(r * math.sin(angle))
    pygame.draw.circle(display, (0, 255, 255), (viz_cam_x, viz_cam_y), 4)
    # Draw look-at line from camera to center
    pygame.draw.line(display, (0, 255, 255, 100), (viz_cam_x, viz_cam_y), (cx, cy), 1)


def _draw_flyout(display, font, pick, win_w, win_h, state):
    """Persistent pick marker + a clamped lat/lon/elev panel with a close (x) button. Publishes the
    button rect to state['pick_close'] so a plain LMB inside it dismisses the flyout."""
    u, v = pick["u"], pick["v"]
    pygame.draw.line(display, (0, 255, 255), (u - 8, v), (u + 8, v), 1)
    pygame.draw.line(display, (0, 255, 255), (u, v - 8), (u, v + 8), 1)
    pygame.draw.circle(display, (0, 255, 255), (u, v), 3, 1)
    lat, lon = pick["lat"], pick["lon"]
    lines = [
        f"lat  {lat:11.7f}" if lat is not None else "lat        --",
        f"lon  {lon:11.7f}" if lon is not None else "lon        --",
        f"elev {pick['elev_ft']:6.0f} ft  ({pick['elev_m']:.1f} m)",
    ]
    surfs = [font.render(s, True, (255, 255, 0)) for s in lines]
    pad = 6; btn = 14; header_h = btn + 2
    pw = max(max(s.get_width() for s in surfs), 60) + pad * 2
    ph = pad + header_h + sum(s.get_height() for s in surfs) + pad
    px = max(0, min(u + 12, win_w - pw))
    py = max(0, min(v + 12, win_h - ph))
    panel = pygame.Surface((pw, ph)); panel.set_alpha(200); panel.fill((0, 0, 0))
    display.blit(panel, (px, py))
    pygame.draw.rect(display, (0, 255, 255), (px, py, pw, ph), 1)
    bx = px + pw - pad - btn; by = py + pad
    pygame.draw.rect(display, (0, 255, 255), (bx, by, btn, btn), 1)
    pygame.draw.line(display, (0, 255, 255), (bx + 3, by + 3), (bx + btn - 3, by + btn - 3), 1)
    pygame.draw.line(display, (0, 255, 255), (bx + btn - 3, by + 3), (bx + 3, by + btn - 3), 1)
    state["pick_close"] = (bx, by, btn, btn)
    y = py + pad + header_h
    for s in surfs:
        display.blit(s, (px + pad, y)); y += s.get_height()


def _project_pt(P, cam, fwd, right, up, f, cx, cy):
    """World point -> screen pixel using the camera basis. None if behind/far off-axis."""
    dx = P[0] - cam[0]; dy = P[1] - cam[1]; dz = P[2] - cam[2]
    depth = dx*fwd[0] + dy*fwd[1] + dz*fwd[2]
    if depth <= 0.5:
        return None
    sr = (dx*right[0] + dy*right[1] + dz*right[2]) / depth
    su = (dx*up[0] + dy*up[1] + dz*up[2]) / depth
    if abs(sr) > 6.0 or abs(su) > 6.0:
        return None
    return (int(cx + sr*f), int(cy - su*f))


def _draw_boundary(display, corners, color, cam, yaw, pitch, f, cx, cy, posts=False):
    """Draw a ground rectangle (4 (x,y,z) corners) as a projected polyline over the sensor image."""
    yr = math.radians(yaw); pr = math.radians(pitch)
    fwd = (math.cos(yr)*math.cos(pr), math.sin(yr)*math.cos(pr), math.sin(pr))
    right = (-math.sin(yr), math.cos(yr), 0.0)
    up = (fwd[1]*right[2] - fwd[2]*right[1],
          fwd[2]*right[0] - fwd[0]*right[2],
          fwd[0]*right[1] - fwd[1]*right[0])
    N = 24
    for i in range(4):
        a = corners[i]; b = corners[(i + 1) % 4]
        prev = None
        for t in range(N + 1):
            s = t / N
            P = (a[0] + (b[0]-a[0])*s, a[1] + (b[1]-a[1])*s, a[2] + (b[2]-a[2])*s)
            scr = _project_pt(P, cam, fwd, right, up, f, cx, cy)
            if scr is not None and prev is not None:
                pygame.draw.line(display, color, prev, scr, 3)
            prev = scr
    if posts:
        for c in corners:
            base = _project_pt(c, cam, fwd, right, up, f, cx, cy)
            top = _project_pt((c[0], c[1], c[2] + 25.0), cam, fwd, right, up, f, cx, cy)
            if base is not None:
                pygame.draw.circle(display, color, base, 6, 2)
                if top is not None:
                    pygame.draw.line(display, color, base, top, 3)


class CameraController():
    def __init__(self, controlled_object, world=None, depth_cam=None, spectator=None):
        self.controlled_object = controlled_object
        self.world = world
        self.depth_cam = depth_cam
        self.spectator = spectator
        self.orbit_enabled = False
        self.orbit_paused = False
        
        # Orbit parameters (CARLA coordinates)
        self.center_x = 0.0
        self.center_y = 0.0
        self.center_z = 0.0
        self.radius = 200.0  # meters
        self.cam_altitude = 0.0  # meters above center
        self.orbit_speed = 240.0  # seconds for one complete orbit
        self.angle = 0.0  # current angle in radians
        self.angular_velocity = (2.0 * math.pi) / self.orbit_speed
        self.last_time = None
        
        # Geodetic parameters (optional, for lat/lon support)
        self.center_lat = None
        self.center_lon = None
        self.georeference_origin = None  # (lat0, lon0, height) tuple
        
        # Try to get georeference origin if world is provided
        if self.world is not None:
            try:
                lat0, lon0, origin_h = self.world.get_cesium_origin()
                self.georeference_origin = (lat0, lon0, origin_h)
            except Exception:
                pass
 
 
    def move_object_to_position(self, position):
        self.pose = carla.Transform(carla.Location(x=x, y=y, z=z_ft / FT_PER_M), carla.Rotation(pitch=p, yaw=yaw, roll=r))
        self.controlled_object.set_transform(self.pose)
 
    def set_object_transform(self, tf):
        self.controlled_object.set_transform(tf)
 
 
    def toggle_orbit(self, enabled : bool = None):
        # if no param is passed, switch the state
        if enabled is None:
            self.orbit_enabled = not self.orbit_enabled
        # if a param is passed, set the state to that value
        else:
            self.orbit_enabled = enabled
        
        # Reset time tracking when toggling
        if self.orbit_enabled:
            self.last_time = time.time()
 
 
    def toggle_orbit_pause(self):
        """Pause/resume the orbit motion while keeping orbit mode enabled."""
        self.orbit_paused = not self.orbit_paused
 
 
    def latlon_to_carla(self, lat, lon):
        """Convert lat/lon to CARLA X/Y coordinates using the georeference origin.
        Uses local tangent plane projection (accurate for distances up to ~100km from origin).
        
        Args:
            lat, lon: Target latitude/longitude in decimal degrees
        
        Returns:
            (x, y): CARLA coordinates in meters, or None if no georeference available
        """
        if self.georeference_origin is None:
            return None
        
        lat0, lon0, _ = self.georeference_origin
        # Earth radius in meters
        R = 6378137.0

        # Convert to radians
        lat_rad = math.radians(lat)
        lon_rad = math.radians(lon)
        lat0_rad = math.radians(lat0)
        lon0_rad = math.radians(lon0)
        
        # Local tangent plane projection
        x = R * (lon_rad - lon0_rad) * math.cos(lat0_rad)
        y = -R * (lat_rad - lat0_rad)  # Negative because CARLA -Y is North
        
        return x, y
 
 
    def carla_to_latlon(self, x, y):
        """Convert CARLA X/Y coordinates to lat/lon using the georeference origin.
        
        Args:
            x, y: CARLA coordinates in meters
        
        Returns:
            (lat, lon): Latitude/longitude in decimal degrees, or None if no georeference available
        """
        if self.georeference_origin is None:
            return None
        
        lat0, lon0, _ = self.georeference_origin
        # Earth radius in meters
        R = 6378137.0
        
        lat0_rad = math.radians(lat0)
        lon0_rad = math.radians(lon0)
        
        # Inverse local tangent plane projection
        lon_rad = lon0_rad + (x / (R * math.cos(lat0_rad)))
        lat_rad = lat0_rad - (y / R)  # Negative because CARLA -Y is North
        
        lat = math.degrees(lat_rad)
        lon = math.degrees(lon_rad)
        
        return lat, lon
 
 
    def set_orbit_params(self, center_x=None, center_y=None, center_z=None, 
                        center_lat=None, center_lon=None,
                        radius=None, radius_feet=None, 
                        altitude=None, altitude_feet=None,
                        speed=None, angle=None):
        """Configure orbit parameters. All parameters are optional.
        
        Args:
            center_x, center_y, center_z: Orbit center point in CARLA coordinates (meters)
            center_lat, center_lon: Orbit center in geodetic coordinates (decimal degrees)
            radius: Orbit radius in meters
            radius_feet: Orbit radius in feet (alternative to radius)
            altitude: Camera altitude above center in meters
            altitude_feet: Camera altitude above center in feet (alternative to altitude)
            speed: Orbit speed in seconds for one complete orbit
            angle: Starting angle in radians (0 = East, π/2 = North)
        """
        # Handle lat/lon center (converts to CARLA coordinates)
        if center_lat is not None and center_lon is not None:
            result = self.latlon_to_carla(center_lat, center_lon)
            if result is not None:
                center_x, center_y = result
                self.center_lat = center_lat
                self.center_lon = center_lon
        
        # Handle CARLA coordinates
        if center_x is not None:
            self.center_x = center_x
        if center_y is not None:
            self.center_y = center_y
        if center_z is not None:
            self.center_z = center_z
        
        # Handle radius (feet or meters)
        if radius_feet is not None:
            self.radius = radius_feet / FT_PER_M
        elif radius is not None:
            self.radius = radius
        
        # Handle altitude (feet or meters)
        if altitude_feet is not None:
            self.cam_altitude = altitude_feet / FT_PER_M
        elif altitude is not None:
            self.cam_altitude = altitude
        
        # Handle speed and angle
        if speed is not None:
            self.orbit_speed = speed
            self.angular_velocity = (2.0 * math.pi) / self.orbit_speed
        if angle is not None:
            self.angle = angle
 
 
    def update_orbit(self, dt=None):
        """Update orbit position. Call this each frame when orbit is enabled.
        
        Args:
            dt: Delta time in seconds. If None, calculates from last_time.
        """
        if not self.orbit_enabled or self.orbit_paused:
            return
        
        # Calculate delta time
        if dt is None:
            current_time = time.time()
            if self.last_time is None:
                self.last_time = current_time
                return
            dt = current_time - self.last_time
            self.last_time = current_time
        
        # Update angle
        self.angle += self.angular_velocity * dt
        self.angle = self.angle % (2.0 * math.pi)
        
        # Calculate camera position on orbit
        cam_x = self.center_x + self.radius * math.cos(self.angle)
        cam_y = self.center_y + self.radius * math.sin(self.angle)
        cam_z = self.center_z + self.cam_altitude
        
        # Calculate look-at direction (always pointing to center)
        dx = self.center_x - cam_x
        dy = self.center_y - cam_y
        dz = self.center_z - cam_z
        horizontal_dist = math.sqrt(dx * dx + dy * dy)
        pitch = math.degrees(math.atan2(dz, horizontal_dist))
        yaw = math.degrees(math.atan2(dy, dx))
        
        # Update transform
        tf = carla.Transform(
            carla.Location(x=cam_x, y=cam_y, z=cam_z),
            carla.Rotation(pitch=pitch, yaw=yaw, roll=0.0)
        )
        self.controlled_object.set_transform(tf)
        
        # Update depth camera and spectator for proper tile streaming
        if self.depth_cam is not None:
            try:
                self.depth_cam.set_transform(tf)
            except Exception:
                pass
        if self.spectator is not None:
            try:
                self.spectator.set_transform(tf)
            except Exception:
                pass
 
 
    def get_hud_info(self):
        """Return orbit status information for HUD display."""
        info = {
            "orbit_enabled": self.orbit_enabled,
            "orbit_paused": self.orbit_paused,
        }
        
        if self.orbit_enabled:
            orbit_progress = (self.angle / (2.0 * math.pi)) * 100.0
            
            # Convert lat/lon if available
            latlon = None
            if self.center_lat is not None and self.center_lon is not None:
                latlon = (self.center_lat, self.center_lon)
            elif self.georeference_origin is not None:
                result = self.carla_to_latlon(self.center_x, self.center_y)
                if result is not None:
                    latlon = result
            
            info.update({
                "orbit_center": (self.center_x, self.center_y, self.center_z),
                "center_latlon": latlon,
                "radius": self.radius,
                "radius_feet": self.radius * FT_PER_M,
                "cam_altitude": self.cam_altitude,
                "cam_altitude_feet": self.cam_altitude * FT_PER_M,
                "orbit_speed": self.orbit_speed,
                "angle": self.angle,
                "orbit_progress": orbit_progress,
            })
        
        return info


# ───────────────────────────── main ─────────────────────────────

def main() -> int:
    args = parse_args()
    sync = not args.asynchronous
    if args.seed is not None:
        random.seed(args.seed)

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"server version: {client.get_server_version()}")

    # Phase 1: build the world (with the server still free-running; the build RPC needs that).
    if args.build:
        if not build_world(client, args):
            return 1
    else:
        print("attach mode (--no-build): using the world already on the server")

    client.set_timeout(20.0)
    world = client.get_world()

    # Shared georeference origin (used by both the picker and the telemetry emitter).
    lat0 = lon0 = origin_h = 0.0
    have_origin = False
    cot_origin = None
    try:
        lat0, lon0, origin_h = world.get_cesium_origin()
        cot_origin = world.get_cesium_origin()
        have_origin = True
        print(f"georeference origin: lat {lat0:.7f}  lon {lon0:.7f}  height {origin_h:.1f} m")
    except Exception as e:
        print(f"get_cesium_origin failed (elevation reads AGL-only; telemetry disabled): {e!r}",
              file=sys.stderr)

    # Time of day: CesiumSunSky is the sole sun/lighting authority (CARLA weather is inert here).
    # The bridge already spawns the sun at local solar noon with a longitude-derived time zone; push
    # the user's --time/--date (defaulting to noon and the host date) on top. Re-applied every run
    # because the sun is respawned on each world (re)build.
    try:
        if args.date:
            _y, _mo, _d = (int(v) for v in args.date.split("-"))
        else:
            _now = datetime.now()
            _y, _mo, _d = _now.year, _now.month, _now.day
        if args.time is None:
            _hours = 12.0
        elif ":" in str(args.time):
            _hh, _mm = str(args.time).split(":")
            _hours = int(_hh) + int(_mm) / 60.0
        else:
            _hours = float(args.time)
        world.set_solar_date(_y, _mo, _d)
        if world.set_solar_time(_hours):
            print(f"solar time set: {int(_hours) % 24:02d}:{int(round((_hours % 1) * 60)) % 60:02d} "
                  f"local, date {_y:04d}-{_mo:02d}-{_d:02d}")
        else:
            print("solar time not set (world has no CesiumSunSky)", file=sys.stderr)
        if args.time_advance:
            world.set_time_advance(True, args.time_rate)
            print(f"solar time advancing at {args.time_rate:g}x "
                  "(wall-clock in --async, sim-time under synchronous ticking)")
    except Exception as e:
        print(f"solar time-of-day setup failed: {e!r}", file=sys.stderr)

    # Traffic Manager — created once, shared. Mode set below.
    tm = client.get_trafficmanager(args.tm_port)

    # Phase mode: synchronous world (default) or fully asynchronous.
    #
    # In synchronous mode the WORLD is synchronous (SCTMV owns world.tick(), giving deterministic,
    # real-time EO capture) but the Traffic Manager runs FREE-RUNNING (async). The .NET TM cannot
    # drive vehicles under synchronous ticking: its ALSM reads vehicle state from a continuously-
    # streamed world-observer cache whose clock is not advanced in lockstep with world.tick(), so a
    # fully-synchronous TM produces zero motion. Running the TM async lets it drive vehicles the proven
    # way while SCTMV still owns the world clock. Trade-off: traffic is not deterministic (the EO
    # capture still is). True synchronous traffic needs a CarlaNet TM engine fix (wire its world-state
    # read + per-tick timestamp to the synchronous tick).
    if sync:
        settings = world.get_settings()
        settings.synchronous_mode = True
        settings.fixed_delta_seconds = args.fixed_delta
        world.apply_settings(settings)
        try:
            tm.set_synchronous_mode(False)     # TM free-runs (it can't drive under synchronous ticking)
        except Exception:
            pass
        if args.seed is not None:
            try:
                tm.set_random_device_seed(args.seed)   # seeds the TM RNG (not bit-exact without sync TM)
            except Exception:
                pass
        print(f"mode: SYNCHRONOUS world + free-running Traffic Manager "
              f"(fixed_delta {args.fixed_delta}s -> ~{1.0/args.fixed_delta:.0f} fps, real-time; "
              "traffic is not deterministic)")
    else:
        try:
            tm.set_synchronous_mode(False)
        except Exception:
            pass
        # If a previous synchronous run left the world frozen with no master, restore async.
        try:
            settings = world.get_settings()
            if settings.synchronous_mode:
                settings.synchronous_mode = False
                settings.fixed_delta_seconds = None
                world.apply_settings(settings)
        except Exception:
            pass
        print("mode: ASYNCHRONOUS (server free-running)")

    # Subsystems.
    traffic = TrafficController.create(world, client, tm, args)
    if not traffic.available:
        print(f"traffic unavailable: {traffic.reason}", file=sys.stderr)
    run_id = f"run-{datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')}"
    sim_clock = SimClock(world)
    print(f"run id: {run_id}")
    telemetry = TelemetryController(world, cot_origin, args, clock=sim_clock)
    if not telemetry.available:
        print(f"telemetry unavailable: {telemetry.reason}", file=sys.stderr)
    # recorder is created after the EO camera is spawned (the native backend needs the camera handle).

    # Camera pose and layer toggle state.
    pose = {"x": args.x, "y": args.y, "z": args.z / FT_PER_M, "pitch": -90.0, "yaw": 0.0}
    start = dict(pose)
    speed = args.speed
    photoreal_visible = True
    ground_visible = False
    ground_collision = True
    road_rendered = True
    show_perimeter = False
    show_margin = False
    time_advancing = bool(args.time_advance)
    solar_hud = ""          # "HH:MM" refreshed from get_solar_state at low frequency
    solar_poll_frame = 0

    state = {"surface": None, "frames": 0, "ground_z": None, "agl_pose": None,
             "depth": None, "pick": None, "pick_close": None, "note": None}

    # Boundary overlay corners from the staging bounds (ground Z sampled once per corner).
    perimeter_corners = margin_corners = None
    proj_f = args.width / (2.0 * math.tan(math.radians(args.fov) / 2.0))
    proj_cx, proj_cy = args.width / 2.0, args.height / 2.0
    try:
        _b = world.get_staging_bounds()
    except Exception:
        _b = None
    if _b:
        def _gz(x, y):
            try:
                z = world.ground_z_below(x, y, 5000.0, search=10000.0)
                return float(z) if z is not None else 0.0
            except Exception:
                return 0.0
        _mnx, _mny, _mxx, _mxy, _mg = (_b["min_x"], _b["min_y"], _b["max_x"], _b["max_y"], _b["margin"])
        _pc = [(_mnx, _mny), (_mxx, _mny), (_mxx, _mxy), (_mnx, _mxy)]
        _mc = [(_mnx + _mg, _mny + _mg), (_mxx - _mg, _mny + _mg),
               (_mxx - _mg, _mxy - _mg), (_mnx + _mg, _mxy - _mg)]
        perimeter_corners = [(x, y, _gz(x, y)) for (x, y) in _pc]
        margin_corners = [(x, y, _gz(x, y)) for (x, y) in _mc]
        print(f"boundary overlay ready (B = OSM perimeter, M = margin boundary)")
    else:
        print("no staging bounds; boundary overlay unavailable (build a drape world first).")

    # Sensors: RGB (display) + depth (Ctrl+LMB picking). In sync mode each frame is pulled from a
    # queue after world.tick(); in async mode the listeners write straight to `state`.
    bp = world.get_blueprint_library().find("sensor.camera.rgb")
    bp.set_attribute("image_size_x", str(args.width))
    bp.set_attribute("image_size_y", str(args.height))
    if bp.has_attribute("fov"):
        bp.set_attribute("fov", str(args.fov))
    if args.ev and bp.has_attribute("exposure_compensation"):
        bp.set_attribute("exposure_compensation", str(args.ev))
    dbp = world.get_blueprint_library().find("sensor.camera.depth")
    dbp.set_attribute("image_size_x", str(args.width))
    dbp.set_attribute("image_size_y", str(args.height))
    if dbp.has_attribute("fov"):
        dbp.set_attribute("fov", str(args.fov))

    def make_tf(p):
        return carla.Transform(carla.Location(x=p["x"], y=p["y"], z=p["z"]),
                               carla.Rotation(pitch=p["pitch"], yaw=p["yaw"], roll=0.0))

    camera = world.spawn_actor(bp, make_tf(pose))
    depth_cam = world.spawn_actor(dbp, make_tf(pose))
    spectator = world.get_spectator()
    spectator.set_transform(make_tf(pose))
    print(f"spawned EO camera id={camera.id}, depth camera id={depth_cam.id}")

    camera_controller = CameraController(camera, world, depth_cam, spectator)

    # Background orbit thread control
    orbit_thread_stop = {"stop": False}

    # Enable orbit if requested via command line
    if args.orbit:
        # Determine orbit center
        if args.orbit_lat is not None and args.orbit_lon is not None:
            # Use lat/lon
            camera_controller.set_orbit_params(
                center_lat=args.orbit_lat,
                center_lon=args.orbit_lon,
                center_z=0.0,
                radius_feet=args.orbit_radius,
                altitude_feet=args.orbit_altitude if args.orbit_altitude else pose["z"] * FT_PER_M,
                speed=args.orbit_speed
            )
            print(f"orbit enabled: center lat {args.orbit_lat:.7f}, lon {args.orbit_lon:.7f}, "
                  f"radius {args.orbit_radius:.0f} ft, altitude {args.orbit_altitude or (pose['z'] * FT_PER_M):.0f} ft")
        elif args.orbit_x is not None and args.orbit_y is not None:
            # Use CARLA coordinates
            camera_controller.set_orbit_params(
                center_x=args.orbit_x,
                center_y=args.orbit_y,
                center_z=0.0,
                radius_feet=args.orbit_radius,
                altitude_feet=args.orbit_altitude if args.orbit_altitude else pose["z"] * FT_PER_M,
                speed=args.orbit_speed
            )
            print(f"orbit enabled: center ({args.orbit_x:.1f}, {args.orbit_y:.1f}), "
                  f"radius {args.orbit_radius:.0f} ft, altitude {args.orbit_altitude or (pose['z'] * FT_PER_M):.0f} ft")
        else:
            # Default: use current camera position as center
            camera_controller.set_orbit_params(
                center_x=pose["x"],
                center_y=pose["y"],
                center_z=0.0,
                radius_feet=args.orbit_radius,
                altitude_feet=args.orbit_altitude if args.orbit_altitude else pose["z"] * FT_PER_M,
                speed=args.orbit_speed
            )
            print(f"orbit enabled: center ({pose['x']:.1f}, {pose['y']:.1f}), "
                  f"radius {args.orbit_radius:.0f} ft, altitude {args.orbit_altitude or (pose['z'] * FT_PER_M):.0f} ft")
        
        # Warn about sync mode
        if sync:
            print("NOTE: --orbit works best with --async for optimal tile streaming")
            print("      Consider using: python SCTMV.py --orbit --async")
        
        # Start background orbit thread for smooth updates (decoupled from main loop)
        def orbit_updater():
            """Background thread that updates orbit at 50 Hz, independent of main loop."""
            while not orbit_thread_stop["stop"]:
                if camera_controller.orbit_enabled and not camera_controller.orbit_paused:
                    camera_controller.update_orbit()
                time.sleep(0.02)  # 50 Hz update rate
        
        orbit_thread = threading.Thread(target=orbit_updater, daemon=True)
        orbit_thread.start()
        
        camera_controller.toggle_orbit(True)

    # Recorder: the native (C#) FrameRecorder encodes frames in .NET off the GIL. If the
    # CarlaNet.Recording assembly is absent the recorder reports itself unavailable when toggled (the
    # whole client is CarlaNet, so a missing recording assembly means the build itself is incomplete).
    recorder = NativeRecorder(world, camera, args, run_id=run_id)
    print(f"recording backend: native (C#) -> {args.record_dir}")

    def process_depth(img):
        """Decode a depth frame's capture pose into the picking record (raw bytes + dims + pose)."""
        if hasattr(img, "transform") and img.transform is not None:
            t = img.transform
            cap = {"x": t.location.x, "y": t.location.y, "z": t.location.z,
                   "pitch": t.rotation.pitch, "yaw": t.rotation.yaw}
        else:
            cap = {"x": pose["x"], "y": pose["y"], "z": pose["z"],
                   "pitch": pose["pitch"], "yaw": pose["yaw"]}
        return {"raw": bytes(img.raw_data), "w": img.width, "h": img.height, "pose": cap}

    rgb_q = queue.Queue()
    depth_q = queue.Queue()
    if sync:
        camera.listen(rgb_q.put)
        depth_cam.listen(depth_q.put)
    else:
        camera.listen(lambda img: (state.__setitem__("surface", _to_surface(img)),
                                   state.__setitem__("frames", state["frames"] + 1)))
        depth_cam.listen(lambda img: state.__setitem__("depth", process_depth(img)))

    # Async-only background mover: keeps RPCs off the render thread and raycasts ground Z for AGL.
    move = {"tf": None, "stop": False}

    def _refresh_ground_z(px, py):
        """Ground local-Z under (px, py) for the AGL readout. Prefer the drape terrain — a non-physics
        grid lookup, valid across the whole OSM sandbox at any altitude — and fall back to a downward
        raycast (started just above the camera) outside the sandbox or in non-drape worlds."""
        try:
            ge = world.drape_ground_elevation(px, py)
        except Exception:
            ge = None
        if ge is not None:
            state["ground_z"] = ge - origin_h   # ellipsoidal ground elevation -> local Z (camera frame)
            return
        try:
            start = pose["z"] + 100.0
            state["ground_z"] = world.ground_z_below(px, py, start, search=start + 6000.0)
        except Exception:
            pass

    def _mover():
        last = None; last_agl = 0.0
        while not move["stop"]:
            tf = move["tf"]
            if tf is not None and tf is not last:
                last = tf
                try:
                    camera.set_transform(tf); depth_cam.set_transform(tf); spectator.set_transform(tf)
                except Exception:
                    pass
            now = time.time()
            p = state.get("agl_pose")
            if p is not None and now - last_agl > 0.3:
                last_agl = now
                _refresh_ground_z(p[0], p[1])
            time.sleep(0.04)

    # Async worker: runs the traffic + telemetry RPCs OFF the render thread. With two camera streams
    # saturating the connection each of those RPCs stalls for ~100-200 ms; left on the main loop they
    # collapse it to ~1 fps. Here the render loop never blocks on them, so flying stays smooth while
    # the Traffic Manager (server-side) keeps driving the vehicles. Sync mode runs them inline instead,
    # because there the single world.tick() must own all of it.
    def _worker():
        while not move["stop"]:
            now = time.time()
            try:
                traffic.apply_want(); traffic.update(now)
            except Exception as e:
                print(f"traffic worker: {e!r}", file=sys.stderr)
            try:
                telemetry.apply_want(); telemetry.update(now)
            except Exception as e:
                print(f"telemetry worker: {e!r}", file=sys.stderr)
            try:
                recorder.apply_want(); recorder.trigger(now, state["surface"])
            except Exception as e:
                print(f"recorder worker: {e!r}", file=sys.stderr)
            time.sleep(0.05)

    mover_thread = worker_thread = None
    if not sync:
        mover_thread = threading.Thread(target=_mover, daemon=True)
        mover_thread.start()
        worker_thread = threading.Thread(target=_worker, daemon=True)
        worker_thread.start()

    def _do_pick(u, v):
        """Ctrl+LMB: reconstruct the world point at pixel (u,v) from the latest depth frame and
        convert to geodetic. Pure in-process (no RPC)."""
        def _note(msg):
            state["note"] = (msg, time.time())
        d = state.get("depth")
        if d is None:
            _note("no depth frame yet"); return
        w, h = d["w"], d["h"]
        if not (0 <= u < w and 0 <= v < h):
            return
        arr = np.frombuffer(d["raw"], np.uint8).reshape(h, w, 4)
        B = float(arr[v, u, 0]); G = float(arr[v, u, 1]); R = float(arr[v, u, 2])
        normalized = (R + G * 256.0 + B * 65536.0) / (256.0 ** 3 - 1.0)
        if normalized >= 0.99:
            _note("no surface (sky)"); return
        depth_m = normalized * 1000.0
        cp = d["pose"]
        cam_loc = (cp["x"], cp["y"], cp["z"])
        yr = math.radians(cp["yaw"]); pr = math.radians(cp["pitch"])
        fwd = (math.cos(yr) * math.cos(pr), math.sin(yr) * math.cos(pr), math.sin(pr))
        right = (-math.sin(yr), math.cos(yr), 0.0)

        def _cross(a, b):
            return (a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0])
        up = _cross(fwd, right)
        f = args.width / (2.0 * math.tan(math.radians(args.fov) / 2.0))
        cx, cy = args.width / 2.0, args.height / 2.0
        s_right = (u - cx) / f
        s_up = -(v - cy) / f
        Px = cam_loc[0] + (fwd[0] + right[0]*s_right + up[0]*s_up) * depth_m
        Py = cam_loc[1] + (fwd[1] + right[1]*s_right + up[1]*s_up) * depth_m
        Pz = cam_loc[2] + (fwd[2] + right[2]*s_right + up[2]*s_up) * depth_m
        gz = state.get("ground_z")
        if gz is not None and pose["z"] < gz:
            _note("camera below terrain — pick disabled"); return
        if Pz > cam_loc[2] + 1.0:
            _note("hit above camera — rejected"); return
        lat = lon = elev_m = None
        if have_origin:
            try:
                from CarlaNet.Types.Geom import Geodesy, GeoLocation
                origin = GeoLocation(lat0, lon0, origin_h)
                geo = Geodesy.CarlaLocalToGeodetic(origin, Px, Py, Pz)
                lat, lon, elev_m = geo.Latitude, geo.Longitude, geo.Altitude
            except Exception as e:
                _note(f"geodesy failed: {e!r}"); return
        if elev_m is None:
            elev_m = origin_h + Pz
        state["pick"] = {"u": u, "v": v, "lat": lat, "lon": lon,
                         "elev_ft": elev_m * FT_PER_M, "elev_m": elev_m, "P": (Px, Py, Pz)}
        state["note"] = None

    def drain_latest(q):
        """Get the most recent queued item (sync mode), blocking briefly for the first."""
        try:
            item = q.get(timeout=2.0)
        except queue.Empty:
            return None
        while True:
            try:
                item = q.get_nowait()
            except queue.Empty:
                return item

    # Optional: start with traffic already on (the worker / tick loop applies the desire).
    if args.start_traffic:
        traffic.want_enabled = True

    pygame.init(); pygame.font.init()
    font = pygame.font.SysFont("consolas", 16)
    display = pygame.display.set_mode((args.width, args.height))
    pygame.display.set_caption("SCTMV — Single Client Traffic Manager & Viewer")
    clock = pygame.time.Clock()
    target_fps = (1.0 / args.fixed_delta) if sync else 60.0

    # Clean stop on Ctrl+C even when blocked inside a .NET call (pythonnet can swallow KeyboardInterrupt).
    stop = {"flag": False}
    try:
        signal.signal(signal.SIGINT, lambda *a: stop.__setitem__("flag", True))
    except Exception:
        pass

    looking = False
    sens = 0.15
    running = True
    last_agl_sync = 0.0
    try:
        while running and not stop["flag"]:
            dt = clock.tick(target_fps) / 1000.0
            for ev in pygame.event.get():
                if ev.type == pygame.QUIT:
                    running = False
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_ESCAPE:
                    running = False
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_SPACE:
                    pose.update(start)
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_c:
                    photoreal_visible = not photoreal_visible
                    try: world.set_layer_visible("photoreal", photoreal_visible)
                    except Exception as e: print(f"set_layer_visible(photoreal) failed: {e!r}", file=sys.stderr)
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_g:
                    ground_visible = not ground_visible
                    try: world.set_layer_visible("ground", ground_visible)
                    except Exception as e: print(f"set_layer_visible(ground) failed: {e!r}", file=sys.stderr)
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_v:
                    ground_collision = not ground_collision
                    try: world.set_layer_collision("ground", ground_collision)
                    except Exception as e: print(f"set_layer_collision(ground) failed: {e!r}", file=sys.stderr)
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_r:
                    road_rendered = not road_rendered
                    try: world.set_road_rendered(road_rendered)
                    except Exception as e: print(f"set_road_rendered failed: {e!r}", file=sys.stderr)
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_b:
                    show_perimeter = not show_perimeter
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_m:
                    show_margin = not show_margin
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_k:
                    time_advancing = not time_advancing
                    try: world.set_time_advance(time_advancing, args.time_rate)
                    except Exception as e: print(f"set_time_advance failed: {e!r}", file=sys.stderr)
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_t:
                    traffic.want_enabled = not traffic.want_enabled    # applied off-thread (async) / inline (sync)
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_y:
                    telemetry.want_enabled = not telemetry.want_enabled
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_f:
                    recorder.want_enabled = not recorder.want_enabled    # PNG + CoT XML capture
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_p:
                    if camera_controller.orbit_enabled:
                        camera_controller.toggle_orbit_pause()
                        print(f"orbit {'paused' if camera_controller.orbit_paused else 'resumed'}")
                elif (ev.type == pygame.MOUSEBUTTONDOWN and ev.button == 1
                      and (pygame.key.get_mods() & pygame.KMOD_CTRL)):
                    _do_pick(ev.pos[0], ev.pos[1])
                elif ev.type == pygame.MOUSEBUTTONDOWN and ev.button == 1:
                    r = state.get("pick_close")
                    if (state.get("pick") and r and r[0] <= ev.pos[0] < r[0] + r[2]
                            and r[1] <= ev.pos[1] < r[1] + r[3]):
                        state["pick"] = None; state["pick_close"] = None
                elif ev.type == pygame.MOUSEBUTTONDOWN and ev.button == 3:
                    looking = True; pygame.event.set_grab(True)
                    pygame.mouse.set_visible(False); pygame.mouse.get_rel()
                elif ev.type == pygame.MOUSEBUTTONUP and ev.button == 3:
                    looking = False; pygame.event.set_grab(False); pygame.mouse.set_visible(True)
                elif ev.type == pygame.MOUSEWHEEL:
                    speed = max(2.0, speed * (1.2 ** ev.y))

            moved = False
            if looking:
                dx, dy = pygame.mouse.get_rel()
                if dx or dy:
                    pose["yaw"] += dx * sens
                    pose["pitch"] = max(-89.9, min(89.9, pose["pitch"] - dy * sens))
                    moved = True
                keys = pygame.key.get_pressed()
                step = speed * (3.0 if (keys[pygame.K_LSHIFT] or keys[pygame.K_RSHIFT]) else 1.0) * dt
                yr = math.radians(pose["yaw"]); pr = math.radians(pose["pitch"])
                fwd = (math.cos(yr) * math.cos(pr), math.sin(yr) * math.cos(pr), math.sin(pr))
                right = (-math.sin(yr), math.cos(yr), 0.0)
                if keys[pygame.K_w]: pose["x"] += fwd[0]*step; pose["y"] += fwd[1]*step; pose["z"] += fwd[2]*step; moved = True
                if keys[pygame.K_s]: pose["x"] -= fwd[0]*step; pose["y"] -= fwd[1]*step; pose["z"] -= fwd[2]*step; moved = True
                if keys[pygame.K_d]: pose["x"] += right[0]*step; pose["y"] += right[1]*step; moved = True
                if keys[pygame.K_a]: pose["x"] -= right[0]*step; pose["y"] -= right[1]*step; moved = True
                if keys[pygame.K_e]: pose["z"] += step; moved = True
                if keys[pygame.K_q]: pose["z"] = max(2.0, pose["z"] - step); moved = True

            state["agl_pose"] = (pose["x"], pose["y"], pose["z"])
            now = time.time()

            # Sync pose dict with orbit position for HUD display (orbit update happens in background thread)
            if camera_controller.orbit_enabled:
                cam_tf = camera_controller.controlled_object.get_transform()
                pose["x"] = cam_tf.location.x
                pose["y"] = cam_tf.location.y
                pose["z"] = cam_tf.location.z
                pose["pitch"] = cam_tf.rotation.pitch
                pose["yaw"] = cam_tf.rotation.yaw

            if sync:
                # Set transforms, advance one step, then pull this frame's sensor data.
                # Skip camera updates if orbit is enabled (background thread handles it)
                if not camera_controller.orbit_enabled:
                    tf = make_tf(pose)
                    try:
                        camera.set_transform(tf); depth_cam.set_transform(tf); spectator.set_transform(tf)
                    except Exception:
                        pass
                world.tick()
                # The Traffic Manager is free-running (async) even though the world is synchronous, so
                # there is no synchronous_tick() handshake here — the TM drives the vehicles on its own
                # worker thread while SCTMV owns the world clock via world.tick().
                img = drain_latest(rgb_q)
                if img is not None:
                    state["surface"] = _to_surface(img); state["frames"] += 1
                dimg = drain_latest(depth_q)
                if dimg is not None:
                    state["depth"] = process_depth(dimg)
                if now - last_agl_sync > 0.3:
                    last_agl_sync = now
                    _refresh_ground_z(pose["x"], pose["y"])
                # Sync mode owns everything on the tick thread: step traffic + telemetry inline.
                try: traffic.apply_want(); traffic.update(now)
                except Exception as e: print(f"traffic.update failed: {e!r}", file=sys.stderr)
                try: telemetry.apply_want(); telemetry.update(now)
                except Exception as e: print(f"telemetry.update failed: {e!r}", file=sys.stderr)
                try: recorder.apply_want(); recorder.trigger(now, state["surface"])
                except Exception as e: print(f"recorder failed: {e!r}", file=sys.stderr)
            else:
                # Skip camera updates if orbit is enabled (background thread handles it)
                if moved and not camera_controller.orbit_enabled:
                    move["tf"] = make_tf(pose)   # background mover applies it
                # Async: traffic + telemetry RPCs run on the background worker thread, never here, so
                # the render loop stays smooth regardless of RPC latency.

            # ---- render ----
            if state["surface"] is not None:
                display.blit(state["surface"], (0, 0))
            cam_xyz = (pose["x"], pose["y"], pose["z"])
            if show_perimeter and perimeter_corners:
                _draw_boundary(display, perimeter_corners, (255, 50, 50), cam_xyz,
                               pose["yaw"], pose["pitch"], proj_f, proj_cx, proj_cy, posts=True)
            if show_margin and margin_corners:
                _draw_boundary(display, margin_corners, (40, 160, 255), cam_xyz,
                               pose["yaw"], pose["pitch"], proj_f, proj_cx, proj_cy, posts=False)

            elev_ft = (origin_h + pose["z"]) * FT_PER_M
            gz = state["ground_z"]
            agl_ft = (pose["z"] - gz) * FT_PER_M if gz is not None else None
            agl_str = f"{agl_ft:5.0f}" if agl_ft is not None else "   --"
            traf_str = (f"{traffic.count()}/{args.max}" if traffic.enabled
                        else ("OFF" if traffic.available else "n/a"))
            tel_str = ("ON" if telemetry.enabled
                       else ("OFF" if telemetry.available else "n/a"))
            rec_str = (f"REC {recorder.saved}@{args.record_hz:g}Hz" if recorder.recording else "off")
            # Refresh the solar-clock readout at low frequency (get_solar_state is a blocking RPC).
            solar_poll_frame += 1
            if solar_poll_frame >= 30:
                solar_poll_frame = 0
                try:
                    _ss = world.get_solar_state()
                    if _ss:
                        _h = _ss["solar_time"]
                        solar_hud = f"{int(_h) % 24:02d}:{int((_h % 1) * 60) % 60:02d}"
                except Exception:
                    pass
            time_str = (f"{solar_hud or '--:--'}"
                        + (f" >{args.time_rate:g}x" if time_advancing else ""))
            
            # Get orbit information
            orbit_info = camera_controller.get_hud_info()
            
            hud = [
                f"elev {elev_ft:6.0f} ft   AGL {agl_str} ft   x {pose['x']:7.1f}  N {-pose['y']:7.1f}   "
                f"yaw {pose['yaw']:6.1f} pitch {pose['pitch']:6.1f}   [{'SYNC' if sync else 'ASYNC'}]",
                f"speed {speed:4.0f}   photoreal(C) {'ON' if photoreal_visible else 'OFF'}   "
                f"ground(G) {'ON' if ground_visible else 'OFF'}   gColl(V) {'ON' if ground_collision else 'OFF'}   "
                f"road(R) {'ON' if road_rendered else 'OFF'}   perim(B) {'ON' if show_perimeter else 'OFF'}   "
                f"margin(M) {'ON' if show_margin else 'OFF'}   time(K) {time_str}",
                f"traffic(T) {traf_str}   telemetry(Y) {tel_str}   record(F) {rec_str}   "
                f"fps {clock.get_fps():4.0f}   frames {state['frames']}",
                "RMB look | Ctrl+LMB measure | WASD/EQ fly | wheel speed | Shift fast | C/G/V/R/B/M layers | "
                "K time | T traffic | Y telemetry | F record |",
                "Space reset | P Pause | Esc quit",
            ]
            
            # Add orbit line only if orbit is enabled
            if orbit_info["orbit_enabled"]:
                # Format lat/lon string
                center_lat, center_lon = orbit_info.get("center_latlon", (None, None))
                if center_lat is not None and center_lon is not None:
                    latlon_str = f"lat {center_lat:.7f}, lon {center_lon:.7f}"
                else:
                    latlon_str = "lat/lon unavailable"
                
                # Format orbit status
                orbit_status = "PAUSED" if orbit_info["orbit_paused"] else "ACTIVE"
                
                # Create orbit HUD line
                orbit_line = (
                    f"ORBIT: center ({orbit_info['orbit_center'][0]:7.1f}, {orbit_info['orbit_center'][1]:7.1f})   "
                    f"radius {orbit_info['radius_feet']:6.0f} ft   altitude {orbit_info['cam_altitude_feet']:6.0f} ft   "
                    f"{latlon_str}   progress {orbit_info['orbit_progress']:5.1f}%   "
                    f"speed {orbit_info['orbit_speed']:5.1f} s   {orbit_status}"
                )
                
                # Insert orbit line before the controls line (second to last line)
                hud.insert(-2, orbit_line)
            bar_h = 8 + len(hud) * 18 + 2
            bar = pygame.Surface((args.width, bar_h)); bar.set_alpha(180); bar.fill((0, 0, 0))
            display.blit(bar, (0, 0))
            for i, line in enumerate(hud):
                display.blit(font.render(line, True, (255, 255, 0)), (8, 8 + i * 18))

            yaw_r = math.radians(pose["yaw"])
            bearing = math.degrees(math.atan2(math.cos(yaw_r), -math.sin(yaw_r))) % 360.0
            _draw_compass(display, font, args.width - 52, bar_h + 48, 40, bearing)
            
            # Draw orbit visualization on left side if orbit is enabled
            if orbit_info["orbit_enabled"]:
                _draw_orbit_viz(display, 60, bar_h + 48, 40, orbit_info["angle"])

            pick = state.get("pick")
            if pick is not None:
                _draw_flyout(display, font, pick, args.width, args.height, state)
            note = state.get("note")
            if note and time.time() - note[1] < 3.0:
                display.blit(font.render(note[0], True, (255, 120, 120)), (8, bar_h + 6))
            pygame.display.flip()
    except KeyboardInterrupt:
        pass
    finally:
        print("\nstopping; restoring server state...")
        # Stop the background threads FIRST so nothing races the despawn below.
        move["stop"] = True
        if mover_thread is not None:
            mover_thread.join(timeout=1.0)
        if worker_thread is not None:
            worker_thread.join(timeout=2.0)
        try: traffic.disable()        # despawn any remaining vehicles (now single-threaded)
        except Exception: pass
        try:
            if recorder.recording: recorder.stop()
        except Exception: pass
        try: telemetry.close()
        except Exception: pass
        # Stop orbit background thread if running
        orbit_thread_stop["stop"] = True
        # Restore asynchronous mode so the headless server is never left waiting for a tick.
        try:
            tm.set_synchronous_mode(False)
        except Exception:
            pass
        try:
            settings = world.get_settings()
            settings.synchronous_mode = False
            settings.fixed_delta_seconds = None
            world.apply_settings(settings)
        except Exception:
            pass
        try: camera.stop(); time.sleep(0.2); camera.destroy()
        except Exception: pass
        try: depth_cam.stop(); time.sleep(0.2); depth_cam.destroy()
        except Exception: pass
        pygame.quit()
    return 0


if __name__ == "__main__":
    sys.exit(main())
