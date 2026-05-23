"""
carlanet_manual_control.py — Drive a CARLA vehicle from Python via CarlaNet.

  W / UP     : throttle
  S / DOWN   : brake
  A / LEFT   : steer left
  D / RIGHT  : steer right
  Q          : toggle reverse
  SPACE      : handbrake
  P          : toggle autopilot
  ESC        : quit

Usage:
  py -3.11 carlanet_manual_control.py [host] [port] [--sync] [--blueprint vehicle.lincoln.mkz]
"""
import sys, os, threading, argparse, time

# Remove script dir before importing clr to prevent namespace shadowing
script_dir = os.path.dirname(os.path.abspath(__file__))
if script_dir in sys.path:
    sys.path.remove(script_dir)

parser = argparse.ArgumentParser()
parser.add_argument("host",      nargs="?", default="localhost")
parser.add_argument("port",      nargs="?", type=int, default=2000)
parser.add_argument("--sync",    action="store_true")
parser.add_argument("--blueprint", default="vehicle.lincoln.mkz")
parser.add_argument("--spawn",   type=int, default=5)
parser.add_argument("--width",   type=int, default=1280)
parser.add_argument("--height",  type=int, default=720)
args = parser.parse_args()

PUBLISH_DIR = os.path.join(script_dir, "publish")
DLL = os.path.join(PUBLISH_DIR, "CarlaNet.Transport.dll")

import pythonnet
pythonnet.load("coreclr")
import clr
clr.AddReference(DLL)

from CarlaNet.Transport import CarlaClient
from CarlaNet.Types.Rpc.Control import VehicleControl
from CarlaNet.Types.Rpc.Environment import EpisodeSettings

import pygame
import numpy as np

WIDTH, HEIGHT = args.width, args.height


def rpc(task):
    return task.GetAwaiter().GetResult()


def make_control(throttle=0.0, brake=0.0, steer=0.0,
                 hand_brake=False, reverse=False):
    return VehicleControl(
        float(throttle), float(steer), float(brake),
        hand_brake, reverse, False, 0)


class FrameBuffer:
    """Thread-safe holder for the latest camera frame."""
    def __init__(self):
        self._lock = threading.Lock()
        self._frame = None

    def put(self, frame_bytes):
        with self._lock:
            self._frame = frame_bytes

    def get(self):
        with self._lock:
            return self._frame


def main():
    pygame.init()
    pygame.font.init()

    display = pygame.display.set_mode(
        (WIDTH, HEIGHT), pygame.HWSURFACE | pygame.DOUBLEBUF)
    pygame.display.set_caption("CarlaNet Manual Control")
    display.fill((0, 0, 0))
    pygame.display.flip()

    font = pygame.font.SysFont("courier", 14)

    client = CarlaClient(args.host, args.port)
    vehicle_id = None
    camera = None
    sub = None
    original_settings = None

    try:
        ver = rpc(client.GetServerVersionAsync())
        print(f"Server: {args.host}:{args.port}  version={ver}")

        # -- Sync mode ---------------------------------------------------------
        if args.sync:
            s = rpc(client.GetEpisodeSettingsAsync())
            original_settings = s
            sync_s = EpisodeSettings(
                True, s.NoRenderingMode, 0.05,
                s.Substepping, s.MaxSubstepDeltaTime, s.MaxSubsteps,
                s.MaxCullingDistance, s.DeterministicRagdolls,
                s.TileStreamDistance, s.ActorActiveDistance, s.SpectatorAsEgo)
            rpc(client.SetEpisodeSettingsAsync(sync_s))
            print("Synchronous mode ON (0.05s fixed step)")

        # -- Spawn vehicle -----------------------------------------------------
        print(f"Spawning {args.blueprint} at spawn point {args.spawn}...")
        vehicle = rpc(client.SpawnVehicleAsync(args.blueprint, args.spawn))
        vehicle_id = vehicle.Id
        print(f"Vehicle spawned  id={vehicle_id}")

        # -- Spawn camera ------------------------------------------------------
        print(f"Spawning RGB camera {WIDTH}x{HEIGHT}...")
        camera = rpc(client.SpawnCameraAsync(vehicle_id, WIDTH, HEIGHT))
        print(f"Camera spawned   id={camera.Id}")

        # -- Subscribe to camera stream ----------------------------------------
        frame_buf = FrameBuffer()

        def on_frame(sf):
            raw = bytes(sf.PayloadBytes)
            frame_buf.put(raw)

        from System import Action
        from CarlaNet.Transport.Streaming import SensorFrame
        cs_action = Action[SensorFrame](on_frame)
        # camera.StreamToken is the raw 24-byte binary from Actor.stream_token
        sub = client.SubscribeToStream(camera.StreamToken, cs_action)
        print("Camera stream subscribed. Drive with WASD / arrow keys.")

        # -- Wait for first frame ----------------------------------------------
        deadline = time.time() + 5.0
        while frame_buf.get() is None and time.time() < deadline:
            if args.sync:
                rpc(client.SendTickCueAsync())
            time.sleep(0.05)

        if frame_buf.get() is None:
            print("WARNING: No camera frames received yet — server may need a tick.")

        # -- Main loop ---------------------------------------------------------
        clock = pygame.time.Clock()
        steer_cache = 0.0
        reverse = False
        autopilot = False
        throttle = 0.0
        brake = 0.0
        frame_count = 0

        while True:
            # -- Tick ----------------------------------------------------------
            if args.sync:
                rpc(client.SendTickCueAsync())

            clock.tick_busy_loop(60)

            # -- Events --------------------------------------------------------
            quit_requested = False
            for event in pygame.event.get():
                if event.type == pygame.QUIT:
                    quit_requested = True
                elif event.type == pygame.KEYUP:
                    k = event.key
                    if k == pygame.K_ESCAPE or (k == pygame.K_q and
                            event.mod & pygame.KMOD_CTRL):
                        quit_requested = True
                    elif k == pygame.K_q:
                        reverse = not reverse
                        print(f"Reverse: {reverse}")
                    elif k == pygame.K_p:
                        autopilot = not autopilot
                        rpc(client.SetActorAutopilotAsync(vehicle_id, autopilot))
                        print(f"Autopilot: {autopilot}")

            if quit_requested:
                break

            # -- Controls (held keys) ------------------------------------------
            if not autopilot:
                keys = pygame.key.get_pressed()

                if keys[pygame.K_UP] or keys[pygame.K_w]:
                    throttle = min(throttle + 0.05, 1.0)
                else:
                    throttle = 0.0

                if keys[pygame.K_DOWN] or keys[pygame.K_s]:
                    brake = min(brake + 0.2, 1.0)
                else:
                    brake = 0.0

                inc = 5e-4 * clock.get_time()
                if keys[pygame.K_LEFT] or keys[pygame.K_a]:
                    steer_cache = max(steer_cache - inc, -0.7) if steer_cache >= 0 else steer_cache - inc
                elif keys[pygame.K_RIGHT] or keys[pygame.K_d]:
                    steer_cache = min(steer_cache + inc, 0.7) if steer_cache <= 0 else steer_cache + inc
                else:
                    steer_cache = 0.0
                steer_cache = max(-0.7, min(0.7, steer_cache))

                hand_brake = bool(keys[pygame.K_SPACE])
                ctrl = make_control(throttle, brake, round(steer_cache, 2),
                                    hand_brake, reverse)
                rpc(client.ApplyControlToVehicleAsync(vehicle_id, ctrl))

            # -- Render camera frame -------------------------------------------
            raw = frame_buf.get()
            if raw is not None:
                # BGRA → RGB, shape (H, W, 4) → (H, W, 3)
                arr = np.frombuffer(raw, dtype=np.uint8).reshape((HEIGHT, WIDTH, 4))
                rgb = arr[:, :, [2, 1, 0]]   # BGR → RGB
                surf = pygame.surfarray.make_surface(rgb.swapaxes(0, 1))
                display.blit(surf, (0, 0))

            # -- HUD overlay ---------------------------------------------------
            frame_count += 1
            fps = clock.get_fps()
            lines = [
                f"CarlaNet  {args.host}:{args.port}",
                f"FPS: {fps:.0f}  frame: {frame_count}",
                f"Throttle: {throttle:.2f}  Brake: {brake:.2f}",
                f"Steer: {steer_cache:.2f}  Reverse: {reverse}",
                f"Autopilot: {autopilot}",
                "",
                "WASD/arrows=drive  Q=reverse  P=autopilot  ESC=quit",
            ]
            y = 8
            for line in lines:
                surf = font.render(line, True, (255, 255, 0))
                display.blit(surf, (8, y))
                y += 18

            pygame.display.flip()

    finally:
        print("\nCleaning up...")
        if sub is not None:
            try:
                sub.Dispose()
            except Exception:
                pass
        if camera is not None:
            try:
                rpc(client.DestroyActorAsync(camera.Id))
            except Exception:
                pass
        if vehicle_id is not None:
            try:
                rpc(client.DestroyActorAsync(vehicle_id))
            except Exception:
                pass
        if original_settings is not None:
            try:
                rpc(client.SetEpisodeSettingsAsync(original_settings))
            except Exception:
                pass
        rpc(client.DisposeAsync().AsTask())
        pygame.quit()
        print("Done.")


if __name__ == "__main__":
    main()
