"""Interactive EO observer (via the carlanet Python API) — NO server window.

Spawns an unparented RGB camera over the world origin, streams its frames into a local
pygame window, and flies the viewpoint with Unreal-editor-style controls. The CARLA
server stays headless (-RenderOffScreen); pixels come back over the sensor stream.

KEY TRICK: a CARLA camera *sensor* frustum does not drive Cesium tile streaming, but the
SPECTATOR (player) camera does. So we move the spectator to the camera pose every frame —
it pulls the photoreal tiles in, and the co-located sensor captures them.

Controls (hold RIGHT MOUSE to fly, like the Unreal editor):
    RMB + mouse   look around (yaw / pitch)
    W / S         forward / back (along view)
    A / D         strafe left / right
    E / Q         up / down (world)
    Mouse wheel   change move speed
    Shift         move faster (x3)
    C             toggle the Cesium photogrammetry overlay on/off
    V             toggle Cesium physics collision on/off (default ON)
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

_state = {"surface": None, "frames": 0, "ground_z": None, "agl_pose": None}


def _to_surface(image):
    arr = np.frombuffer(bytes(image.raw_data), dtype=np.uint8)
    arr = np.reshape(arr, (image.height, image.width, 4))
    arr = arr[:, :, :3][:, :, ::-1]
    return pygame.surfarray.make_surface(arr.swapaxes(0, 1))


def main() -> int:
    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"server version: {client.get_server_version()}")
    world = client.get_world()

    pose = {"x": args.x, "y": args.y, "z": args.z / FT_PER_M, "pitch": -90.0, "yaw": 0.0}
    start = dict(pose)
    speed = args.speed
    cesium_visible = True
    cesium_collision = True
    road_rendered = True

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

    # Georeference origin height (metres): true elevation = origin_h + local Z.
    origin_h = 0.0
    try:
        _, _, origin_h = world.get_cesium_origin()
        print(f"georeference origin height: {origin_h:.1f} m")
    except Exception as e:
        print(f"get_cesium_origin failed (elevation will read as AGL-only): {e!r}", file=sys.stderr)

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
                cesium_visible = not cesium_visible
                try:
                    world.set_cesium_visible(cesium_visible)
                except Exception as e:
                    print(f"set_cesium_visible failed: {e!r}", file=sys.stderr)
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_v:
                cesium_collision = not cesium_collision
                try:
                    world.set_cesium_collision(cesium_collision)
                except Exception as e:
                    print(f"set_cesium_collision failed: {e!r}", file=sys.stderr)
            elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_r:
                # Hide/show the CARLA road MESH rendering only (z-fights with the photoreal
                # streets). Collision is untouched — vehicles still drive on the roads.
                road_rendered = not road_rendered
                try:
                    world.set_road_rendered(road_rendered)
                except Exception as e:
                    print(f"set_road_rendered failed: {e!r}", file=sys.stderr)
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
            f"speed {speed:5.0f} m/s   cesium(C) {'ON' if cesium_visible else 'OFF'}   "
            f"collision(V) {'ON' if cesium_collision else 'OFF'}   "
            f"road(R) {'ON' if road_rendered else 'OFF'}   "
            f"fps {clock.get_fps():4.0f}  frames {_state['frames']}",
            "RMB+mouse look | W/S/A/D fly | E/Q up/down | wheel speed | Shift fast | C cesium | V collision | R road | Space reset | Esc quit",
        ]
        for i, line in enumerate(hud):
            display.blit(font.render(line, True, (255, 255, 0)), (8, 8 + i * 18))
        pygame.display.flip()

    print("stopping camera ...")
    _move["stop"] = True
    try:
        camera.stop(); time.sleep(0.3); camera.destroy()
    except Exception:
        pass
    pygame.quit()
    return 0


if __name__ == "__main__":
    sys.exit(main())
