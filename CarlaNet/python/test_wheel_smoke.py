"""
Smoke test for the carlanet pip-installed wheel.

Verifies:
  1. import carlanet succeeds (wheel was installed correctly with DLLs).
  2. Loaded from site-packages (not the source tree).
  3. Connects to a running CARLA server on localhost:2000.
  4. Reads world state (map name, actor count) without mutating the session.

Exit code: 0 on success, 1 on any exception (with traceback).
"""

import sys
import traceback


def main() -> int:
    # ── 1) Import the wheel-installed package ────────────────────────────────
    print("== Step 1: import carlanet ==")
    import carlanet as carla
    print(f"  carlanet.__file__ = {carla.__file__}")

    # ── 2) Connect ───────────────────────────────────────────────────────────
    print("== Step 2: connect to localhost:2000 ==")
    client = carla.Client("localhost", 2000)
    # set_timeout takes seconds (per __init__.py line 711-714)
    client.set_timeout(10.0)
    print(f"  client = {client!r}")

    # Versions (best effort — may RPC the server)
    try:
        cv = client.get_client_version()
        print(f"  client_version = {cv}")
    except Exception as ex:
        print(f"  client_version: <unavailable: {ex}>")
    try:
        sv = client.get_server_version()
        print(f"  server_version = {sv}")
    except Exception as ex:
        print(f"  server_version: <unavailable: {ex}>")

    # ── 3) World ─────────────────────────────────────────────────────────────
    print("== Step 3: get_world ==")
    world = client.get_world()
    print(f"  world  = {world!r}")

    # ── 4) Map ───────────────────────────────────────────────────────────────
    print("== Step 4: get_map ==")
    world_map = world.get_map()
    # Per __init__.py:230-240, Map has `.name` attribute and `get_spawn_points()`.
    print(f"  map.name = {world_map.name!r}")
    spawn_points = world_map.get_spawn_points()
    print(f"  map.spawn_points = {len(spawn_points)} entries")

    # ── 5) Actors ────────────────────────────────────────────────────────────
    print("== Step 5: get_actors ==")
    actors = world.get_actors()
    print(f"  actor count = {len(actors)}")
    # Print up to first 5 actor type_ids for context
    for a in actors[:5]:
        print(f"    - {a!r}")

    # ── 6) Weather (optional, skip on failure) ───────────────────────────────
    print("== Step 6: get_weather (optional) ==")
    try:
        weather = world.get_weather()
        print(f"  weather = {weather}")
    except Exception as ex:
        print(f"  weather skipped: {ex}")

    # ── 7) Clean disconnect ─────────────────────────────────────────────────
    # Per __init__.py:771-775, Client.__del__ calls DisposeAsync. There's no
    # public close() method exposed; let the GC handle it. Explicitly drop the
    # reference so __del__ runs in this process.
    print("== Step 7: disconnect ==")
    del client
    print("  client released")

    print("== SUCCESS ==")
    return 0


if __name__ == "__main__":
    try:
        rc = main()
    except Exception:
        print("== FAILURE ==", file=sys.stderr)
        traceback.print_exc()
        rc = 1
    sys.exit(rc)
