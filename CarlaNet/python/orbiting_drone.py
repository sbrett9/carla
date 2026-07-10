"""Persistent orbiting drone camera using CarlaNet API.

Spawns an unparented RGB camera that orbits around a configurable center coordinate
while maintaining continuous focus on that center point. The camera acts as a ghost-like 
entity with no physics, smoothly circling while always keeping the center in view.

Controls:
    P             pause/resume orbit
    F             toggle recording (PNG + CoT XML)
    Esc           quit

Run order (separate terminals):
    1. RunCarlaServer.ps1
    2. test_digital_twin.py                                 # build the elevated world
    3. generate_traffic_carlanet.py --asynch -n 40 -w 0     # optional: moving traffic
    4. python orbiting_drone.py

Usage:
    # Using CARLA coordinates:
    python orbiting_drone.py [--x M --y M --z FEET] [--radius M] [--altitude FEET] [--orbit-speed SEC]
        [--fov DEG] [--ev EV] [--width PX --height PX] [--host H --port P]
        [--record-dir DIR] [--record-hz HZ]
    
    # Using lat/lon (requires georeferenced world):
    python orbiting_drone.py [--lat LAT --lon LON --z FEET] [--radius M] [--orbit-speed SEC]
        [--fov DEG] [--ev EV] [--width PX --height PX] [--host H --port P]
        [--record-dir DIR] [--record-hz HZ]
"""
import argparse
import math
import os
import queue
import sys
import threading
import time
from datetime import datetime, timezone

import numpy as np
import pygame

import carlanet as carla

FT_PER_M = 3.28084
_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))

ap = argparse.ArgumentParser()
ap.add_argument("--x", type=float, default=None, help="orbit center X (CARLA metres)")
ap.add_argument("--y", type=float, default=None, help="orbit center Y (CARLA metres, -Y is North)")
ap.add_argument("--z", type=float, default=1700.0, help="camera altitude in FEET (default 1700)")
ap.add_argument("--lat", type=float, default=None, help="orbit center latitude (alternative to --x/--y)")
ap.add_argument("--lon", type=float, default=None, help="orbit center longitude (alternative to --x/--y)")
ap.add_argument("--radius", type=float, default=656.0, help="orbit radius in FEET (default 656 = 200m)")
ap.add_argument("--orbit-speed", type=float, default=240.0, help="orbit speed in seconds (default 240 = 4 min)")
ap.add_argument("--fov", type=float, default=90.0)
ap.add_argument("--ev", type=float, default=0.0, help="camera exposure_compensation (EV); >0 brightens")
ap.add_argument("--width", type=int, default=1280)
ap.add_argument("--height", type=int, default=720)
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)

rec = ap.add_argument_group("recording (F hotkey)")
rec.add_argument("--record-dir", default=os.path.join(_REPO, "Build", "drone_recordings"),
                 help="folder for recordings (default Build/drone_recordings). F toggles recording: "
                      "each capture writes a lossless PNG of the clean streamed imagery (no HUD) "
                      "and a matching CoT XML sidecar with vehicle telemetry.")
rec.add_argument("--record-hz", type=float, default=2.0,
                 help="capture rate in Hz (captures per second; may be fractional, e.g. 0.5). "
                      "Default 2.0.")

args = ap.parse_args()

_state = {"surface": None, "frames": 0, "paused": False}


def latlon_to_carla(lat, lon, lat0, lon0):
    """Convert lat/lon to CARLA X/Y coordinates using the georeference origin.
    Uses local tangent plane projection (accurate for distances up to ~100km from origin).
    
    Args:
        lat, lon: Target latitude/longitude in decimal degrees
        lat0, lon0: Georeference origin latitude/longitude in decimal degrees
    
    Returns:
        (x, y): CARLA coordinates in meters
    """
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


def carla_to_latlon(x, y, lat0, lon0):
    """Convert CARLA X/Y coordinates to lat/lon using the georeference origin.
    Inverse of latlon_to_carla.
    
    Args:
        x, y: CARLA coordinates in meters
        lat0, lon0: Georeference origin latitude/longitude in decimal degrees
    
    Returns:
        (lat, lon): Latitude/longitude in decimal degrees
    """
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


class NativeRecorder:
    """Drives the in-engine (C#) FrameRecorder: camera frames are tapped, encoded to PNG, and written
    entirely on the .NET thread pool — no frame ever crosses to Python and the GIL is never held, so
    the viewer stays smooth while recording. Exposes the same interface as the Python Recorder
    (want_enabled / apply_want / trigger / saved / recording / stop) so the loop, hotkey, and HUD are
    backend-agnostic."""

    def __init__(self, world, camera, args):
        self.world = world
        self.camera = camera
        self.args = args
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
                self.camera, self.args.record_dir, self.args.record_hz)
            if self._handle is None:
                self.want_enabled = False
                return
            self.recording = True
            print(f"recording (native) -> {self.args.record_dir} @ {self.args.record_hz} Hz (PNG only)")
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


def _to_surface(image):
    arr = np.frombuffer(bytes(image.raw_data), dtype=np.uint8)
    arr = np.reshape(arr, (image.height, image.width, 4))
    arr = arr[:, :, :3][:, :, ::-1]
    return pygame.surfarray.make_surface(arr.swapaxes(0, 1))


def _draw_compass(display, font, cx, cy, r, bearing_deg):
    """Draw a north-up compass rose at (cx, cy). bearing_deg = the camera's heading (0=N, 90=E,
    clockwise)."""
    b = math.radians(bearing_deg)
    bg = pygame.Surface((2 * r + 4, 2 * r + 4), pygame.SRCALPHA)
    pygame.draw.circle(bg, (0, 0, 0, 150), (r + 2, r + 2), r)
    pygame.draw.circle(bg, (0, 255, 255), (r + 2, r + 2), r, 1)
    display.blit(bg, (cx - r - 2, cy - r - 2))
    # cardinal letters at screen-angle (cardinal_bearing - camera_bearing); 0 => straight up
    for label, cb, col in (("N", 0, (255, 80, 80)), ("E", 90, (220, 220, 220)),
                           ("S", 180, (220, 220, 220)), ("W", 270, (220, 220, 220))):
        a = math.radians(cb - bearing_deg)
        lx = cx + math.sin(a) * (r - 10)
        ly = cy - math.cos(a) * (r - 10)
        s = font.render(label, True, col)
        display.blit(s, (lx - s.get_width() / 2, ly - s.get_height() / 2))
    # north needle (red) + south tail (grey)
    pygame.draw.line(display, (255, 80, 80), (cx, cy),
                     (cx - math.sin(b) * (r - 4), cy - math.cos(b) * (r - 4)), 2)
    pygame.draw.line(display, (160, 160, 160), (cx, cy),
                     (cx + math.sin(b) * (r - 4), cy + math.cos(b) * (r - 4)), 2)
    pygame.draw.circle(display, (0, 255, 255), (cx, cy), 2)
    hdg = font.render(f"{bearing_deg:03.0f}", True, (255, 255, 0))
    display.blit(hdg, (cx - hdg.get_width() / 2, cy + r + 2))


def main() -> int:
    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"server version: {client.get_server_version()}")
    world = client.get_world()
    
    # Get georeference origin early if we need it for lat/lon conversion
    lat0 = lon0 = origin_h = 0.0
    have_origin = False
    if args.lat is not None and args.lon is not None:
        try:
            lat0, lon0, origin_h = world.get_cesium_origin()
            have_origin = True
            print(f"georeference origin: lat {lat0:.7f}  lon {lon0:.7f}  height {origin_h:.1f} m")
        except Exception as e:
            print(f"ERROR: --lat/--lon requires georeference origin, but get_cesium_origin failed: {e!r}",
                  file=sys.stderr)
            return 1

    # Orbit parameters (x, y define horizontal center, z is camera altitude)
    # Determine orbit center from either lat/lon or CARLA coordinates
    if args.lat is not None and args.lon is not None:
        # User provided lat/lon
        center_x, center_y = latlon_to_carla(args.lat, args.lon, lat0, lon0)
        print(f"orbit center: lat {args.lat:.7f}, lon {args.lon:.7f} "
              f"→ CARLA ({center_x:.1f}, {center_y:.1f})")
    elif args.x is not None and args.y is not None:
        # User provided CARLA coordinates
        center_x = args.x
        center_y = args.y
    else:
        # Default to origin if nothing specified
        center_x = 0.0
        center_y = 0.0
    
    start_center_x = center_x
    start_center_y = center_y
    cam_altitude = args.z / FT_PER_M  # Convert feet to meters
    start_altitude = cam_altitude
    radius = args.radius / FT_PER_M  # Convert feet to meters
    start_radius = radius
    orbit_speed = args.orbit_speed  # seconds for one complete orbit
    start_orbit_speed = orbit_speed
    angular_velocity = (2.0 * math.pi) / orbit_speed  # radians per second
    
    # Start at angle 0 (East of center in CARLA coords: +X direction)
    angle = 0.0
    
    # Set road rendering off
    world.set_road_rendered(False)

    # Camera setup
    bp = world.get_blueprint_library().find("sensor.camera.rgb")
    bp.set_attribute("image_size_x", str(args.width))
    bp.set_attribute("image_size_y", str(args.height))
    if bp.has_attribute("fov"):
        bp.set_attribute("fov", str(args.fov))
    if args.ev and bp.has_attribute("exposure_compensation"):
        bp.set_attribute("exposure_compensation", str(args.ev))

    def make_tf(x, y, z, pitch, yaw):
        return carla.Transform(carla.Location(x=x, y=y, z=z),
                               carla.Rotation(pitch=pitch, yaw=yaw, roll=0.0))

    # Calculate initial position
    cam_x = center_x + radius * math.cos(angle)
    cam_y = center_y + radius * math.sin(angle)
    cam_z = cam_altitude
    
    # Calculate initial look-at (pitch and yaw to center)
    dx = center_x - cam_x
    dy = center_y - cam_y
    dz = 0.0 - cam_z  # Center is at ground level (z=0)
    horizontal_dist = math.sqrt(dx * dx + dy * dy)
    pitch = math.degrees(math.atan2(dz, horizontal_dist))
    yaw = math.degrees(math.atan2(dy, dx))

    camera = world.spawn_actor(bp, make_tf(cam_x, cam_y, cam_z, pitch, yaw))
    print(f"spawned orbital drone camera id={camera.id}")
    print(f"orbit center: ({center_x:.1f}, {center_y:.1f}) at altitude {cam_altitude * FT_PER_M:.0f} ft")
    print(f"orbit radius: {radius * FT_PER_M:.0f} ft ({radius:.1f} m), orbit_speed: {orbit_speed:.1f} s")
    
    # Recorder: native (C#) backend only
    recorder = NativeRecorder(world, camera, args)
    if not recorder.available:
        print("ERROR: recording unavailable - CarlaNet.Recording not built. Rebuild the DLLs.",
              file=sys.stderr)
        return 1
    print(f"recording backend: native (C#) -> {args.record_dir}")
    
    spectator = world.get_spectator()
    spectator.set_transform(make_tf(cam_x, cam_y, cam_z, pitch, yaw))

    camera.listen(lambda img: (_state.__setitem__("surface", _to_surface(img)),
                               _state.__setitem__("frames", _state["frames"] + 1)))

    # Background thread for smooth camera movement (non-blocking RPCs)
    _move = {"tf": None, "stop": False}

    def _mover():
        last = None
        while not _move["stop"]:
            tf = _move["tf"]
            if tf is not None and tf is not last:
                last = tf
                try:
                    camera.set_transform(tf)
                    spectator.set_transform(tf)
                except Exception:
                    pass
            time.sleep(0.02)  # 50 Hz update rate

    mover_thread = threading.Thread(target=_mover, daemon=True)
    mover_thread.start()

    # Georeference origin for display (fetch if not already fetched for lat/lon conversion)
    if not have_origin:
        try:
            lat0, lon0, origin_h = world.get_cesium_origin()
            have_origin = True
            print(f"georeference origin: lat {lat0:.7f}  lon {lon0:.7f}  height {origin_h:.1f} m")
        except Exception as e:
            print(f"get_cesium_origin failed: {e!r}", file=sys.stderr)
            lat0 = lon0 = origin_h = 0.0

    pygame.init()
    pygame.font.init()
    font = pygame.font.SysFont("consolas", 16)
    display = pygame.display.set_mode((args.width, args.height))
    pygame.display.set_caption("CARLA x Cesium — Orbital Drone")
    clock = pygame.time.Clock()

    running = True
    last_time = time.time()
    
    try:
        while running:
            current_time = time.time()
            dt = current_time - last_time
            last_time = current_time
            
            for ev in pygame.event.get():
                if ev.type == pygame.QUIT:
                    running = False
                elif ev.type == pygame.KEYDOWN:
                    if ev.key == pygame.K_ESCAPE:
                        running = False
                    elif ev.key == pygame.K_p:
                        _state["paused"] = not _state["paused"]
                        print(f"orbit {'paused' if _state['paused'] else 'resumed'}")
                    elif ev.key == pygame.K_f:
                        recorder.want_enabled = not recorder.want_enabled

            # Update orbit angle if not paused
            if not _state["paused"]:
                angle += angular_velocity * dt
                angle = angle % (2.0 * math.pi)  # Keep in [0, 2π)

            # Calculate camera position on orbit
            cam_x = center_x + radius * math.cos(angle)
            cam_y = center_y + radius * math.sin(angle)
            cam_z = cam_altitude

            # Calculate look-at direction (always pointing to center at ground level)
            dx = center_x - cam_x
            dy = center_y - cam_y
            dz = 0.0 - cam_z  # Center is at ground level (z=0)
            horizontal_dist = math.sqrt(dx * dx + dy * dy)
            pitch = math.degrees(math.atan2(dz, horizontal_dist))
            yaw = math.degrees(math.atan2(dy, dx))

            # Update camera transform
            _move["tf"] = make_tf(cam_x, cam_y, cam_z, pitch, yaw)
            
            # Recording
            now = time.time()
            try:
                recorder.apply_want()
                recorder.trigger(now, _state["surface"])
            except Exception as e:
                print(f"recorder failed: {e!r}", file=sys.stderr)

            # Render
            if _state["surface"] is not None:
                display.blit(_state["surface"], (0, 0))

            # Calculate progress through orbit
            orbit_progress = (angle / (2.0 * math.pi)) * 100.0
            elev_ft = (origin_h + cam_z) * FT_PER_M
            rec_str = (f"REC {recorder.saved}@{args.record_hz:g}Hz" if recorder.recording else "off")
            
            # HUD
            if have_origin:
                display_lat, display_lon = carla_to_latlon(center_x, center_y, lat0, lon0)
                latlon_str = f"lat/lon ({display_lat:.6f}, {display_lon:.6f})"
            else:
                latlon_str = "lat/lon unavailable (no georeference)"
            
            hud = [
                f"CARLA center ({center_x:7.1f}, {center_y:7.1f})   "
                f"radius {radius * FT_PER_M:6.0f} ft   altitude {cam_altitude * FT_PER_M:6.0f} ft   elev {elev_ft:6.0f} ft",
                f"{latlon_str}   "
                f"orbit {orbit_progress:5.1f}%   speed {orbit_speed:5.1f} s   "
                f"{'PAUSED' if _state['paused'] else 'ACTIVE'}   "
                f"record(F) {rec_str}   "
                f"fps {clock.get_fps():4.0f}   frames {_state['frames']}",
                "P pause | F record | Esc quit",
            ]
            
            bar_h = 8 + len(hud) * 18 + 2
            bar = pygame.Surface((args.width, bar_h))
            bar.set_alpha(180)
            bar.fill((0, 0, 0))
            display.blit(bar, (0, 0))
            for i, line in enumerate(hud):
                display.blit(font.render(line, True, (255, 255, 0)), (8, 8 + i * 18))

            # Compass rose (camera heading)
            yaw_r = math.radians(yaw)
            bearing = math.degrees(math.atan2(math.cos(yaw_r), -math.sin(yaw_r))) % 360.0
            _draw_compass(display, font, args.width - 52, bar_h + 48, 40, bearing)

            # Orbit visualization indicator (top-left, below HUD)
            orbit_viz_x = 60
            orbit_viz_y = bar_h + 60
            orbit_viz_r = 40
            
            # Draw orbit circle
            pygame.draw.circle(display, (100, 100, 100), (orbit_viz_x, orbit_viz_y), orbit_viz_r, 1)
            # Draw center point
            pygame.draw.circle(display, (255, 80, 80), (orbit_viz_x, orbit_viz_y), 3)
            # Draw camera position on orbit
            viz_cam_x = orbit_viz_x + int(orbit_viz_r * math.cos(angle))
            viz_cam_y = orbit_viz_y + int(orbit_viz_r * math.sin(angle))
            pygame.draw.circle(display, (0, 255, 255), (viz_cam_x, viz_cam_y), 4)
            # Draw look-at line
            pygame.draw.line(display, (0, 255, 255, 100), (viz_cam_x, viz_cam_y), 
                            (orbit_viz_x, orbit_viz_y), 1)

            pygame.display.flip()
            clock.tick(60)
    except KeyboardInterrupt:
        pass
    finally:
        print("\nstopping camera ...")
        _move["stop"] = True
        mover_thread.join(timeout=1.0)
        try:
            if recorder.recording:
                recorder.stop()
        except Exception:
            pass
        try:
            camera.stop()
            time.sleep(0.3)
            camera.destroy()
        except Exception:
            pass
        pygame.quit()
    return 0


if __name__ == "__main__":
    sys.exit(main())
