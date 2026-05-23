"""
carlanet_probe.py — Exercise CarlaNet APIs against a live CARLA server via pythonnet.

Analogous to the interrogation/setup phase of generate_traffic.py and the
world-query calls in test_snapshot.py, but driven by CarlaNet instead of libcarla.

Usage: python carlanet_probe.py [host] [port]
"""
import sys, os

# Must remove script dir BEFORE importing clr — otherwise the CarlaNet/ folder
# is cached as a Python namespace package and shadows the CLR namespace importer.
script_dir = os.path.dirname(os.path.abspath(__file__))
if script_dir in sys.path:
    sys.path.remove(script_dir)

HOST = sys.argv[1] if len(sys.argv) > 1 else "localhost"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 2000

PUBLISH_DIR = os.path.join(script_dir, "publish")
DLL = os.path.join(PUBLISH_DIR, "CarlaNet.Transport.dll")

print(f"=== CarlaNet probe ===")
print(f"Target : {HOST}:{PORT}")
print(f"DLL    : {DLL}")
print()

import pythonnet
pythonnet.load("coreclr")

import clr
clr.AddReference(DLL)

from CarlaNet.Transport import CarlaClient
from CarlaNet.Types.Rpc.Environment import EpisodeSettings


def rpc(task):
    """Block on a C# Task and return its result."""
    return task.GetAwaiter().GetResult()


def hr(label):
    print(f"\n-- {label} {'-' * (50 - len(label))}")


def run():
    client = CarlaClient(HOST, PORT)
    try:
        # ── 1. Server / session identity ─────────────────────────────────────────
        hr("Server info")
        version = rpc(client.GetServerVersionAsync())
        ep      = rpc(client.GetEpisodeInfoAsync())
        print(f"  Server version   : {version}")
        print(f"  Episode ID       : {ep.Id}")
        print(f"  Token size       : {len(ep.Token.Data)} bytes")

        # ── 2. Episode settings (current state) ──────────────────────────────────
        hr("Episode settings")
        s = rpc(client.GetEpisodeSettingsAsync())
        print(f"  SynchronousMode  : {s.SynchronousMode}")
        print(f"  NoRenderingMode  : {s.NoRenderingMode}")
        print(f"  FixedDeltaSeconds: {s.FixedDeltaSeconds}")
        print(f"  Substepping      : {s.Substepping}")
        print(f"  MaxSubsteps      : {s.MaxSubsteps}")

        # ── 3. Enable sync mode (analogous to generate_traffic.py lines 127-131) ─
        hr("Enable synchronous mode")
        sync_settings = EpisodeSettings(
            True,                    # SynchronousMode  (was: s.SynchronousMode)
            s.NoRenderingMode,
            0.05,                    # FixedDeltaSeconds  (double? — pass float directly)
            s.Substepping,
            s.MaxSubstepDeltaTime,
            s.MaxSubsteps,
            s.MaxCullingDistance,
            s.DeterministicRagdolls,
            s.TileStreamDistance,
            s.ActorActiveDistance,
            s.SpectatorAsEgo,
        )
        frame_after_set = rpc(client.SetEpisodeSettingsAsync(sync_settings))
        print(f"  SetEpisodeSettings OK  (frame={frame_after_set})")

        # Confirm the server received the change
        s2 = rpc(client.GetEpisodeSettingsAsync())
        print(f"  SynchronousMode now  : {s2.SynchronousMode}")
        print(f"  FixedDeltaSeconds now: {s2.FixedDeltaSeconds}")

        # ── 4. Map info & spawn points (analogous to world.get_map().get_spawn_points()) ─
        hr("Map info")
        map_info     = rpc(client.GetMapInfoAsync())
        spawn_points = map_info.RecommendedSpawnPoints
        sp_count     = spawn_points.Count
        print(f"  Map name         : {map_info.Name}")
        print(f"  Spawn points     : {sp_count}")
        if sp_count > 0:
            sp = spawn_points[0]
            print(f"  [0] loc=({sp.Location.X:.1f}, {sp.Location.Y:.1f}, {sp.Location.Z:.1f})"
                  f"  rot=({sp.Rotation.Pitch:.1f}, {sp.Rotation.Yaw:.1f}, {sp.Rotation.Roll:.1f})")

        # ── 5. Actor definitions (analogous to world.get_blueprint_library().filter()) ─
        hr("Actor definitions  (blueprint library)")
        defs_raw = rpc(client.GetActorDefinitionsAsync())
        defs     = [defs_raw[i] for i in range(defs_raw.Count)]
        vehicles = [d for d in defs if str(d.Id).startswith("vehicle.")]
        walkers  = [d for d in defs if str(d.Id).startswith("walker.")]
        sensors  = [d for d in defs if str(d.Id).startswith("sensor.")]
        print(f"  Total            : {len(defs)}")
        print(f"  vehicle.*        : {len(vehicles)}")
        print(f"  walker.*         : {len(walkers)}")
        print(f"  sensor.*         : {len(sensors)}")
        if vehicles:
            print(f"  First vehicle    : {vehicles[0].Id}")
        if sensors:
            print(f"  First sensor     : {sensors[0].Id}")

        # ── 6. Available maps ─────────────────────────────────────────────────────
        hr("Available maps")
        maps_raw = rpc(client.GetAvailableMapsAsync())
        maps = [maps_raw[i] for i in range(maps_raw.Count)]
        for m in maps:
            print(f"  {m}")

        # ── 7. Tick a few frames (analogous to synchronous_master loop in generate_traffic) ─
        hr("Tick (5 frames)")
        for i in range(5):
            frame = rpc(client.SendTickCueAsync())
            print(f"  frame {i+1}: {frame}")

        # ── 8. Restore async mode ────────────────────────────────────────────────
        hr("Restore asynchronous mode")
        restore = EpisodeSettings(
            False,                   # SynchronousMode back to False
            s.NoRenderingMode,
            None,                    # FixedDeltaSeconds back to None (no fixed step)
            s.Substepping,
            s.MaxSubstepDeltaTime,
            s.MaxSubsteps,
            s.MaxCullingDistance,
            s.DeterministicRagdolls,
            s.TileStreamDistance,
            s.ActorActiveDistance,
            s.SpectatorAsEgo,
        )
        rpc(client.SetEpisodeSettingsAsync(restore))
        print(f"  Restored async mode OK")

        print()
        print("=== All probes passed ===")

    except Exception as ex:
        print(f"\nERROR: {type(ex).__name__}: {ex}")
        import traceback; traceback.print_exc()
    finally:
        rpc(client.DisposeAsync().AsTask())


run()
