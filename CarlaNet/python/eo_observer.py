"""Interactive EO observer (via the carlanet Python API) — NO server window.

Spawns an unparented RGB camera over the world origin, streams its frames into a local
pygame window, and flies the viewpoint with Unreal-editor-style controls. The CARLA
server stays headless (-RenderOffScreen); pixels come back over the sensor stream.

KEY TRICK: a CARLA camera *sensor* frustum does not drive Cesium tile streaming, but the
SPECTATOR (player) camera does. So we move the spectator to the camera pose every frame —
it pulls the photoreal tiles in, and the co-located sensor captures them.

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
    R             toggle CARLA road-mesh RENDERING on/off (collision unaffected — cars still drive)
    Space         reset to the start pose
    Esc           quit

Run order (separate terminals):
    1. RunCarlaServer.ps1
    2. test_digital_twin.py                                 # build the elevated world
    3. generate_traffic_carlanet.py --asynch -n 40 -w 0     # moving traffic
    4. python eo_observer.py

Usage:
    python eo_observer.py [--z FEET] [--x M --y M] [--fov DEG] [--ev EV]
        [--speed MPS] [--width PX --height PX] [--host H --port P]
"""
import argparse
import math
import sys
import threading
import time

import numpy as np
import pygame

import carlanet as carla

FT_PER_M = 3.28084

ap = argparse.ArgumentParser()
ap.add_argument("--z", type=float, default=1000.0, help="start altitude in FEET (default 1000)")
ap.add_argument("--x", type=float, default=0.0)
ap.add_argument("--y", type=float, default=0.0, help="CARLA metres; -Y is North")
ap.add_argument("--fov", type=float, default=90.0)
ap.add_argument("--ev", type=float, default=0.0, help="camera exposure_compensation (EV); >0 brightens")
ap.add_argument("--speed", type=float, default=60.0, help="initial move speed (m/s)")
ap.add_argument("--width", type=int, default=1280)
ap.add_argument("--height", type=int, default=720)
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)
args = ap.parse_args()

_state = {"surface": None, "frames": 0, "ground_z": None, "agl_pose": None,
          "depth": None, "pick": None, "pick_close": None, "note": None}


def _to_surface(image):
    arr = np.frombuffer(bytes(image.raw_data), dtype=np.uint8)
    arr = np.reshape(arr, (image.height, image.width, 4))
    arr = arr[:, :, :3][:, :, ::-1]
    return pygame.surfarray.make_surface(arr.swapaxes(0, 1))


def _draw_compass(display, font, cx, cy, r, bearing_deg):
    """Draw a north-up compass rose at (cx, cy). bearing_deg = the camera's heading (0=N, 90=E,
    clockwise). The N marker rotates to where true north is relative to the current view."""
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


def _draw_flyout(display, font, pick, win_w, win_h):
    """Draw the persistent pick marker + a clamped-on-screen lat/lon/elev panel with a close
    (x) button. The button's rect is published to _state['pick_close'] so a plain LMB inside
    it dismisses the flyout (see the event loop)."""
    u, v = pick["u"], pick["v"]
    # crosshair marker at the picked pixel
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
    pad = 6
    btn = 14                                    # close-button square
    header_h = btn + 2                          # top row reserved for the x button
    pw = max(max(s.get_width() for s in surfs), 60) + pad * 2
    ph = pad + header_h + sum(s.get_height() for s in surfs) + pad
    # place near the click, then clamp the panel rect fully on-screen (guard 6)
    px = max(0, min(u + 12, win_w - pw))
    py = max(0, min(v + 12, win_h - ph))
    panel = pygame.Surface((pw, ph))
    panel.set_alpha(200)
    panel.fill((0, 0, 0))
    display.blit(panel, (px, py))
    pygame.draw.rect(display, (0, 255, 255), (px, py, pw, ph), 1)

    # close (x) button, top-right corner; publish its rect for the LMB hit-test
    bx = px + pw - pad - btn
    by = py + pad
    pygame.draw.rect(display, (0, 255, 255), (bx, by, btn, btn), 1)
    pygame.draw.line(display, (0, 255, 255), (bx + 3, by + 3), (bx + btn - 3, by + btn - 3), 1)
    pygame.draw.line(display, (0, 255, 255), (bx + btn - 3, by + 3), (bx + 3, by + btn - 3), 1)
    _state["pick_close"] = (bx, by, btn, btn)

    y = py + pad + header_h
    for s in surfs:
        display.blit(s, (px + pad, y))
        y += s.get_height()


def main() -> int:
    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"server version: {client.get_server_version()}")
    world = client.get_world()

    pose = {"x": args.x, "y": args.y, "z": args.z / FT_PER_M, "pitch": -90.0, "yaw": 0.0}
    start = dict(pose)
    speed = args.speed
    # Layer toggle states (08_Layer_Architecture). After test_digital_twin builds the world:
    # photoreal visible, ground hidden + collidable, road visible + collidable.
    photoreal_visible = True    # C : Google photoreal tileset rendering
    ground_visible = False      # G : World Terrain (bare-earth) tileset rendering (hidden by default)
    ground_collision = True     # V : World Terrain physics — ON by default (road=ground coincide under
                                #     height-align 'none', so vehicles never float; gives off-road collision)
    road_rendered = True        # R : OpenDRIVE road-mesh rendering

    bp = world.get_blueprint_library().find("sensor.camera.rgb")
    bp.set_attribute("image_size_x", str(args.width))
    bp.set_attribute("image_size_y", str(args.height))
    if bp.has_attribute("fov"):
        bp.set_attribute("fov", str(args.fov))
    if args.ev and bp.has_attribute("exposure_compensation"):
        bp.set_attribute("exposure_compensation", str(args.ev))

    def make_tf(p):
        return carla.Transform(carla.Location(x=p["x"], y=p["y"], z=p["z"]),
                               carla.Rotation(pitch=p["pitch"], yaw=p["yaw"], roll=0.0))

    camera = world.spawn_actor(bp, make_tf(pose))
    print(f"spawned EO camera id={camera.id}")
    spectator = world.get_spectator()
    spectator.set_transform(make_tf(pose))

    camera.listen(lambda img: (_state.__setitem__("surface", _to_surface(img)),
                               _state.__setitem__("frames", _state["frames"] + 1)))

    # Co-located depth camera (same res/fov) for Ctrl+LMB world picking. Its listener stores
    # ONLY the latest frame: raw bytes + dims, plus the camera pose AT CAPTURE. Prefer the
    # sensor's own world transform (image.transform) when exposed; else snapshot the live pose.
    dbp = world.get_blueprint_library().find("sensor.camera.depth")
    dbp.set_attribute("image_size_x", str(args.width))
    dbp.set_attribute("image_size_y", str(args.height))
    if dbp.has_attribute("fov"):
        dbp.set_attribute("fov", str(args.fov))
    depth_cam = world.spawn_actor(dbp, make_tf(pose))
    print(f"spawned depth camera id={depth_cam.id}")

    def _on_depth(img):
        if hasattr(img, "transform") and img.transform is not None:
            t = img.transform
            cap = {"x": t.location.x, "y": t.location.y, "z": t.location.z,
                   "pitch": t.rotation.pitch, "yaw": t.rotation.yaw}
        else:
            cap = {"x": pose["x"], "y": pose["y"], "z": pose["z"],
                   "pitch": pose["pitch"], "yaw": pose["yaw"]}
        _state["depth"] = {"raw": bytes(img.raw_data), "w": img.width, "h": img.height, "pose": cap}

    depth_cam.listen(_on_depth)

    # Georeference origin (lat/lon/height in metres): true elevation = origin_h + local Z.
    # lat0/lon0/origin_h are also the GeoLocation origin for Ctrl+LMB local->geodetic picks.
    lat0 = lon0 = 0.0
    origin_h = 0.0
    have_origin = False
    try:
        lat0, lon0, origin_h = world.get_cesium_origin()  # (lat, lon, height_m)
        have_origin = True
        print(f"georeference origin: lat {lat0:.7f}  lon {lon0:.7f}  height {origin_h:.1f} m")
    except Exception as e:
        print(f"get_cesium_origin failed (elevation will read as AGL-only): {e!r}", file=sys.stderr)

    def _do_pick(u, v):
        """Ctrl+LMB: reconstruct the world point at pixel (u,v) from the latest depth frame
        and convert to geodetic. Pure in-process (no RPC). On success stores _state['pick'];
        on a guarded miss sets a transient _state['note'] and leaves any existing pick intact."""
        def _note(msg):
            _state["note"] = (msg, time.time())        # transient: expires after ~3 s in render
        d = _state.get("depth")
        if d is None:                                  # guard 1: no depth frame yet
            _note("no depth frame yet")
            return
        w, h = d["w"], d["h"]
        if not (0 <= u < w and 0 <= v < h):
            return
        # decode just the clicked pixel (CARLA depth = BGRA, R+G*256+B*65536 normalized).
        arr = np.frombuffer(d["raw"], np.uint8).reshape(h, w, 4)
        B = float(arr[v, u, 0]); G = float(arr[v, u, 1]); R = float(arr[v, u, 2])
        normalized = (R + G * 256.0 + B * 65536.0) / (256.0 ** 3 - 1.0)   # [0,1]
        if normalized >= 0.99:                         # guard 2: sky / horizon / into space
            _note("no surface (sky)")
            return
        depth_m = normalized * 1000.0                  # CARLA 'far' = 1000 m (planar component)

        cp = d["pose"]
        cam_loc = (cp["x"], cp["y"], cp["z"])
        yr = math.radians(cp["yaw"]); pr = math.radians(cp["pitch"])   # roll = 0
        fwd = (math.cos(yr) * math.cos(pr), math.sin(yr) * math.cos(pr), math.sin(pr))
        right = (-math.sin(yr), math.cos(yr), 0.0)     # matches the file's strafe-right basis

        def _cross(a, b):
            return (a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0])
        up = _cross(fwd, right)                         # = +Z at identity; tilts with pitch

        f = args.width / (2.0 * math.tan(math.radians(args.fov) / 2.0))   # h-fov, square px
        cx, cy = args.width / 2.0, args.height / 2.0
        s_right = (u - cx) / f
        s_up = -(v - cy) / f                            # pixel v grows downward => -up
        # NADIR self-check: pitch=-90, center pixel => fwd=(0,0,-1), right=(0,1,0)*~, up=(...),
        # s_right=s_up=0 => P = cam + fwd*depth_m = (cam_x, cam_y, cam_z - depth_m).  OK.
        Px = cam_loc[0] + (fwd[0] + right[0]*s_right + up[0]*s_up) * depth_m
        Py = cam_loc[1] + (fwd[1] + right[1]*s_right + up[1]*s_up) * depth_m
        Pz = cam_loc[2] + (fwd[2] + right[2]*s_right + up[2]*s_up) * depth_m

        # guard 3: camera below terrain at its column => underside of the world; disable picks.
        gz = _state.get("ground_z")
        if gz is not None and pose["z"] < gz:
            _note("camera below terrain — pick disabled")
            return
        # guard 4: a hit ABOVE the camera is a likely underside/oblique artifact (EO looks down).
        if Pz > cam_loc[2] + 1.0:
            _note("hit above camera — rejected")
            return

        lat = lon = elev_m = None
        if have_origin:
            try:
                from CarlaNet.Types.Geom import Geodesy, GeoLocation
                origin = GeoLocation(lat0, lon0, origin_h)        # (lat, lon, alt)
                geo = Geodesy.CarlaLocalToGeodetic(origin, Px, Py, Pz)
                lat, lon, elev_m = geo.Latitude, geo.Longitude, geo.Altitude
            except Exception as e:
                _note(f"geodesy failed: {e!r}")
                return
        if elev_m is None:
            elev_m = origin_h + Pz                                # fallback: origin height + local Z
        _state["pick"] = {"u": u, "v": v, "lat": lat, "lon": lon,
                          "elev_ft": elev_m * FT_PER_M, "elev_m": elev_m, "P": (Px, Py, Pz)}
        _state["note"] = None

    # Push camera+spectator pose from a BACKGROUND thread so the render/input loop never
    # blocks on an RPC (e.g. while traffic spawns and the server is busy). The same thread
    # raycasts the ground below (throttled) for the AGL readout — all RPCs stay on one thread.
    _move = {"tf": None, "stop": False}

    def _mover():
        last = None
        last_agl = 0.0
        while not _move["stop"]:
            tf = _move["tf"]
            if tf is not None and tf is not last:
                last = tf
                try:
                    camera.set_transform(tf)
                    depth_cam.set_transform(tf)   # keep depth co-located with RGB
                    spectator.set_transform(tf)
                except Exception:
                    pass
            now = time.time()
            p = _state.get("agl_pose")
            if p is not None and now - last_agl > 0.3:
                last_agl = now
                try:
                    # Cast from high ABOVE the column (not from the camera) so we read the
                    # surface elevation at (x,y) regardless of the camera's height. AGL is then
                    # signed: positive above the terrain, NEGATIVE when the camera has clipped
                    # below it. 5000 m start is well above any terrain; 10000 m reach covers it.
                    _state["ground_z"] = world.ground_z_below(p[0], p[1], 5000.0, search=10000.0)
                except Exception:
                    pass
            time.sleep(0.04)

    mover_thread = threading.Thread(target=_mover, daemon=True)
    mover_thread.start()

    pygame.init()
    pygame.font.init()
    font = pygame.font.SysFont("consolas", 16)
    display = pygame.display.set_mode((args.width, args.height))
    pygame.display.set_caption("CARLA x Cesium — EO observer")
    clock = pygame.time.Clock()

    looking = False
    sens = 0.15
    running = True
    while running:
        dt = clock.tick(60) / 1000.0
        for ev in pygame.event.get():
            if ev.type == pygame.QUIT:
                running = False
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_ESCAPE:
                running = False
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_SPACE:
                pose.update(start)
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_c:
                photoreal_visible = not photoreal_visible
                try:
                    world.set_layer_visible("photoreal", photoreal_visible)
                except Exception as e:
                    print(f"set_layer_visible(photoreal) failed: {e!r}", file=sys.stderr)
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_g:
                ground_visible = not ground_visible
                try:
                    world.set_layer_visible("ground", ground_visible)
                except Exception as e:
                    print(f"set_layer_visible(ground) failed: {e!r}", file=sys.stderr)
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_v:
                ground_collision = not ground_collision
                try:
                    world.set_layer_collision("ground", ground_collision)
                except Exception as e:
                    print(f"set_layer_collision(ground) failed: {e!r}", file=sys.stderr)
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_r:
                # Hide/show the CARLA road MESH rendering only (z-fights with the photoreal
                # streets). Collision is untouched — vehicles still drive on the roads.
                road_rendered = not road_rendered
                try:
                    world.set_road_rendered(road_rendered)
                except Exception as e:
                    print(f"set_road_rendered failed: {e!r}", file=sys.stderr)
            elif (ev.type == pygame.MOUSEBUTTONDOWN and ev.button == 1
                  and (pygame.key.get_mods() & pygame.KMOD_CTRL)):  # Ctrl+LMB world pick
                _do_pick(ev.pos[0], ev.pos[1])
            elif ev.type == pygame.MOUSEBUTTONDOWN and ev.button == 1:  # plain LMB: close flyout if x hit
                r = _state.get("pick_close")
                if (_state.get("pick") and r and r[0] <= ev.pos[0] < r[0] + r[2]
                        and r[1] <= ev.pos[1] < r[1] + r[3]):
                    _state["pick"] = None
                    _state["pick_close"] = None
            elif ev.type == pygame.MOUSEBUTTONDOWN and ev.button == 3:  # RMB
                looking = True
                pygame.event.set_grab(True)
                pygame.mouse.set_visible(False)
                pygame.mouse.get_rel()
            elif ev.type == pygame.MOUSEBUTTONUP and ev.button == 3:
                looking = False
                pygame.event.set_grab(False)
                pygame.mouse.set_visible(True)
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
            yr = math.radians(pose["yaw"])
            pr = math.radians(pose["pitch"])
            fwd = (math.cos(yr) * math.cos(pr), math.sin(yr) * math.cos(pr), math.sin(pr))
            right = (-math.sin(yr), math.cos(yr), 0.0)  # horizontal strafe (A=left, D=right)
            if keys[pygame.K_w]: pose["x"] += fwd[0]*step; pose["y"] += fwd[1]*step; pose["z"] += fwd[2]*step; moved = True
            if keys[pygame.K_s]: pose["x"] -= fwd[0]*step; pose["y"] -= fwd[1]*step; pose["z"] -= fwd[2]*step; moved = True
            if keys[pygame.K_d]: pose["x"] += right[0]*step; pose["y"] += right[1]*step; moved = True
            if keys[pygame.K_a]: pose["x"] -= right[0]*step; pose["y"] -= right[1]*step; moved = True
            if keys[pygame.K_e]: pose["z"] += step; moved = True
            if keys[pygame.K_q]: pose["z"] = max(2.0, pose["z"] - step); moved = True

        if moved:
            _move["tf"] = make_tf(pose)   # hand off to the background mover (non-blocking)
        _state["agl_pose"] = (pose["x"], pose["y"], pose["z"])  # for the mover-thread raycast

        if _state["surface"] is not None:
            display.blit(_state["surface"], (0, 0))
        elev_ft = (origin_h + pose["z"]) * FT_PER_M
        gz = _state["ground_z"]
        agl_ft = (pose["z"] - gz) * FT_PER_M if gz is not None else None
        agl_str = f"{agl_ft:5.0f}" if agl_ft is not None else "   --"
        hud = [
            f"elev {elev_ft:6.0f} ft   AGL {agl_str} ft   x {pose['x']:7.1f}  N {-pose['y']:7.1f}   "
            f"yaw {pose['yaw']:6.1f} pitch {pose['pitch']:6.1f}",
            f"speed {speed:4.0f}   photoreal(C) {'ON' if photoreal_visible else 'OFF'}   "
            f"ground(G) {'ON' if ground_visible else 'OFF'}   "
            f"gColl(V) {'ON' if ground_collision else 'OFF'}   "
            f"road(R) {'ON' if road_rendered else 'OFF'}   "
            f"fps {clock.get_fps():4.0f}  frames {_state['frames']}",
            "RMB look | Ctrl+LMB measure | WASD/EQ fly | wheel speed | Shift fast | C photoreal | G ground | V gColl | R road | Space reset | Esc quit",
        ]
        # Feature 1: black (semi-transparent) bar behind the HUD so yellow text stays readable
        # over bright photogrammetry.
        bar_h = 8 + len(hud) * 18 + 2
        bar = pygame.Surface((args.width, bar_h))
        bar.set_alpha(180)
        bar.fill((0, 0, 0))
        display.blit(bar, (0, 0))
        for i, line in enumerate(hud):
            display.blit(font.render(line, True, (255, 255, 0)), (8, 8 + i * 18))

        # Compass rose (top-right, below the HUD bar). Camera heading: 0=N, 90=E (CARLA +X=East,
        # -Y=North) — same convention as the CoT course-over-ground, so it doubles as a heading check.
        yaw_r = math.radians(pose["yaw"])
        bearing = math.degrees(math.atan2(math.cos(yaw_r), -math.sin(yaw_r))) % 360.0
        _draw_compass(display, font, args.width - 52, bar_h + 48, 40, bearing)

        # Feature 2: persistent pick flyout (lat/lon/elev), plus a transient note for misses.
        pick = _state.get("pick")
        if pick is not None:
            _draw_flyout(display, font, pick, args.width, args.height)
        note = _state.get("note")
        if note and time.time() - note[1] < 3.0:        # transient miss message (~3 s)
            display.blit(font.render(note[0], True, (255, 120, 120)), (8, bar_h + 6))
        pygame.display.flip()

    print("stopping camera ...")
    _move["stop"] = True
    try:
        camera.stop(); time.sleep(0.3); camera.destroy()
    except Exception:
        pass
    try:
        depth_cam.stop(); time.sleep(0.3); depth_cam.destroy()
    except Exception:
        pass
    pygame.quit()
    return 0


if __name__ == "__main__":
    sys.exit(main())
