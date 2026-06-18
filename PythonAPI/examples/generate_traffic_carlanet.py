#!/usr/bin/env python

# Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma de
# Barcelona (UAB).
#
# This work is licensed under the terms of the MIT license.
# For a copy, see <https://opensource.org/licenses/MIT>.

"""Example script to generate traffic in the simulation"""

import time
import math

import carlanet as carla

import argparse
import logging
from numpy import random

# ── Boundary-aware "staging" traffic ─────────────────────────────────────────
# When the world was built with a draped sandbox (height-align=drape), get_staging_bounds()
# returns the sandbox extent + an inward "staging ring" reserved at the OSM edge. Traffic enters
# from that ring driving inward, and despawns once it has been into the scene and returned to the
# ring — so vehicles/walkers are never seen popping into existence or vanishing mid-scene.

def _scene_center(b):
    return (0.5 * (b["min_x"] + b["max_x"]), 0.5 * (b["min_y"] + b["max_y"]))

def _in_scene(x, y, b):
    """Inside the scene / region of interest (the sandbox inset by the staging margin)."""
    return (b["min_x"] + b["margin"] <= x <= b["max_x"] - b["margin"] and
            b["min_y"] + b["margin"] <= y <= b["max_y"] - b["margin"])

def _in_ring(x, y, b):
    """Inside the sandbox but within the staging margin of an edge (the entry/exit ring)."""
    inside = (b["min_x"] <= x <= b["max_x"] and b["min_y"] <= y <= b["max_y"])
    return inside and not _in_scene(x, y, b)

def _is_inward(tf, b):
    """True if the transform's forward (yaw) points toward the scene center — i.e. spawning here and
    driving forward enters the scene rather than leaving it."""
    cx, cy = _scene_center(b)
    yaw = math.radians(tf.rotation.yaw)
    return math.cos(yaw) * (cx - tf.location.x) + math.sin(yaw) * (cy - tf.location.y) > 0.0

def _random_scene_navpoint(world, b, tries=40):
    """A random navigation point inside the scene (so a walker heads inward), or any nav point if
    none found / no staging."""
    last = None
    for _ in range(tries):
        loc = world.get_random_location_from_navigation()
        if loc is None:
            continue
        last = loc
        if b is None or _in_scene(loc.x, loc.y, b):
            return loc
    return last

def get_actor_blueprints(world, filter, generation):
    bps = world.get_blueprint_library().filter(filter)

    if generation.lower() == "all":
        return bps

    # If the filter returns only one bp, we assume that this one needed
    # and therefore, we ignore the generation
    if len(bps) == 1:
        return bps

    try:
        int_generation = int(generation)
        # Check if generation is in available generations
        if int_generation in [1, 2, 3]:
            bps = [x for x in bps if int(x.get_attribute('generation')) == int_generation]
            return bps
        else:
            print("   Warning! Actor Generation is not valid. No actor will be spawned.")
            return []
    except:
        print("   Warning! Actor Generation is not valid. No actor will be spawned.")
        return []

def main():
    argparser = argparse.ArgumentParser(description=__doc__)
    argparser.add_argument(
        '--host', metavar='H', default='127.0.0.1',
        help='IP of the host server (default: 127.0.0.1)')
    argparser.add_argument(
        '-p', '--port', metavar='P', default=2000, type=int,
        help='TCP port to listen to (default: 2000)')
    argparser.add_argument(
        '-n', '--number-of-vehicles', metavar='N', default=30, type=int,
        help='Number of vehicles (default: 30)')
    argparser.add_argument(
        '-w', '--number-of-walkers', metavar='W', default=10, type=int,
        help='Number of walkers (default: 10)')
    argparser.add_argument(
        '--safe', action='store_true',
        help='Avoid spawning vehicles prone to accidents')
    argparser.add_argument(
        '--filterv', metavar='PATTERN', default='vehicle.*',
        help='Filter vehicle model (default: "vehicle.*")')
    argparser.add_argument(
        '--generationv', metavar='G', default='All',
        help='restrict to certain vehicle generation (values: "2","3","All" - default: "All")')
    argparser.add_argument(
        '--filterw', metavar='PATTERN', default='walker.pedestrian.*',
        help='Filter pedestrian type (default: "walker.pedestrian.*")')
    argparser.add_argument(
        '--generationw', metavar='G', default='All',
        help='restrict to certain pedestrian generation (values: "2","3","All" - default: "All")')
    argparser.add_argument(
        '--tm-port', metavar='P', default=8000, type=int,
        help='Port to communicate with TM (default: 8000)')
    argparser.add_argument(
        '--asynch', action='store_true',
        help='Activate asynchronous mode execution')
    argparser.add_argument(
        '--hybrid', action='store_true',
        help='Activate hybrid mode for Traffic Manager')
    argparser.add_argument(
        '-s', '--seed', metavar='S', type=int,
        help='Set random device seed and deterministic mode for Traffic Manager')
    argparser.add_argument(
        '--seedw', metavar='S', default=0, type=int,
        help='Set the seed for pedestrians module')
    argparser.add_argument(
        '--car-lights-on', action='store_true', default=False,
        help='Enable automatic car light management')
    argparser.add_argument(
        '--hero', action='store_true', default=False,
        help='Set one of the vehicles as hero')
    argparser.add_argument(
        '--respawn', action='store_true', default=False,
        help='Automatically respawn dormant vehicles (only in large maps)')
    argparser.add_argument(
        '--no-rendering', action='store_true', default=False,
        help='Activate no rendering mode')
    argparser.add_argument(
        '--no-staging', action='store_true', default=False,
        help='Disable boundary-aware staging (spawn anywhere on the map). By default, when the world '
             'has a draped sandbox (height-align=drape), traffic enters from the edge staging ring '
             'and despawns when it returns there, so nothing pops in/out mid-scene.')

    args = argparser.parse_args()

    logging.basicConfig(format='%(levelname)s: %(message)s', level=logging.INFO)

    vehicles_list = []
    walkers_list = []
    all_id = []
    client = carla.Client(args.host, args.port)
    client.set_timeout(10.0)
    synchronous_master = False
    random.seed(args.seed if args.seed is not None else int(time.time()))

    try:
        world = client.get_world()

        traffic_manager = client.get_trafficmanager(args.tm_port)
        traffic_manager.set_global_distance_to_leading_vehicle(2.5)
        if args.respawn:
            traffic_manager.set_respawn_dormant_vehicles(True)
        if args.hybrid:
            traffic_manager.set_hybrid_physics_mode(True)
            traffic_manager.set_hybrid_physics_radius(70.0)
        if args.seed is not None:
            traffic_manager.set_random_device_seed(args.seed)

        settings = world.get_settings()
        if not args.asynch:
            traffic_manager.set_synchronous_mode(True)
            if not settings.synchronous_mode:
                synchronous_master = True
                settings.synchronous_mode = True
                settings.fixed_delta_seconds = 0.05
            else:
                synchronous_master = False
        else:
            print("You are currently in asynchronous mode, and traffic might experience some issues")

        if args.no_rendering:
            settings.no_rendering_mode = True
        world.apply_settings(settings)

        blueprints = get_actor_blueprints(world, args.filterv, args.generationv)
        if not blueprints:
            raise ValueError("Couldn't find any vehicles with the specified filters")
        blueprintsWalkers = get_actor_blueprints(world, args.filterw, args.generationw)
        if not blueprintsWalkers:
            raise ValueError("Couldn't find any walkers with the specified filters")

        if args.safe:
            blueprints = [x for x in blueprints if x.get_attribute('base_type') == 'car']

        blueprints = sorted(blueprints, key=lambda bp: bp.id)

        # Boundary-aware staging: if the world has a draped sandbox, restrict vehicle entry to the
        # edge ring, facing inward. staging stays None (upstream behaviour) when there's no sandbox
        # or --no-staging is set.
        staging = None
        if not args.no_staging:
            try:
                staging = world.get_staging_bounds()
            except Exception:
                staging = None

        spawn_points = world.get_map().get_spawn_points()
        if staging:
            ring_sps = [sp for sp in spawn_points
                        if _in_ring(sp.location.x, sp.location.y, staging) and _is_inward(sp, staging)]
            print('[staging] %d inward edge-ring spawn points (of %d); margin %.0f m'
                  % (len(ring_sps), len(spawn_points), staging["margin"]))
            if ring_sps:
                spawn_points = ring_sps
            else:
                print('[staging] no inward ring spawn points found; using all spawn points')
                staging = None
        number_of_spawn_points = len(spawn_points)
        # Keep the inward ring spawn points for respawning exited vehicles in the main loop.
        veh_ring_sps = list(spawn_points) if staging else None

        if args.number_of_vehicles < number_of_spawn_points:
            random.shuffle(spawn_points)
        elif args.number_of_vehicles > number_of_spawn_points:
            msg = 'requested %d vehicles, but could only find %d spawn points'
            logging.warning(msg, args.number_of_vehicles, number_of_spawn_points)
            args.number_of_vehicles = number_of_spawn_points

        # @todo cannot import these directly.
        SpawnActor = carla.command.SpawnActor
        SetAutopilot = carla.command.SetAutopilot
        FutureActor = carla.command.FutureActor

        # --------------
        # Spawn vehicles
        # --------------
        batch = []
        hero = args.hero
        for n, transform in enumerate(spawn_points):
            if n >= args.number_of_vehicles:
                break
            blueprint = random.choice(blueprints)
            if blueprint.has_attribute('color'):
                color = random.choice(blueprint.get_attribute('color').recommended_values)
                blueprint.set_attribute('color', color)
            if blueprint.has_attribute('driver_id'):
                driver_id = random.choice(blueprint.get_attribute('driver_id').recommended_values)
                blueprint.set_attribute('driver_id', driver_id)
            if hero:
                blueprint.set_attribute('role_name', 'hero')
                hero = False
            else:
                blueprint.set_attribute('role_name', 'autopilot')

            # spawn the cars and set their autopilot and light state all together
            batch.append(SpawnActor(blueprint, transform)
                .then(SetAutopilot(FutureActor, True, traffic_manager.get_port())))

        for response in client.apply_batch_sync(batch, synchronous_master):
            if response.error:
                logging.error(response.error)
            else:
                vehicles_list.append(response.actor_id)

        # Set automatic vehicle lights update if specified
        if args.car_lights_on:
            all_vehicle_actors = world.get_actors(vehicles_list)
            for actor in all_vehicle_actors:
                traffic_manager.update_vehicle_lights(actor, True)

        # -------------
        # Spawn Walkers
        # -------------
        # some settings
        percentagePedestriansRunning = 0.0      # how many pedestrians will run
        percentagePedestriansCrossing = 0.0     # how many pedestrians will walk through the road
        if args.seedw:
            world.set_pedestrians_seed(args.seedw)
            random.seed(args.seedw)
        # 1. take all the random locations to spawn. Under staging, keep only nav points that fall in
        #    the edge ring (so walkers, like vehicles, enter from the boundary). The ring is a thin
        #    band, so allow many more random draws before giving up.
        # CarlaNet edit: C# Transform/Location are init-only record structs, so build the Transform
        # immutably with the lifted Z baked in rather than mutating .location / .location.z.
        spawn_points = []
        _walker_attempts = 0
        _walker_attempt_cap = args.number_of_walkers * (40 if staging else 1)
        while len(spawn_points) < args.number_of_walkers and _walker_attempts < _walker_attempt_cap:
            _walker_attempts += 1
            loc = world.get_random_location_from_navigation()
            if loc is None:
                continue
            if staging and not _in_ring(loc.x, loc.y, staging):
                continue
            spawn_points.append(carla.Transform(carla.Location(loc.x, loc.y, loc.z + 2)))
        if staging:
            print('[staging] %d edge-ring walker spawn points (target %d)'
                  % (len(spawn_points), args.number_of_walkers))
        # 2. we spawn the walker object
        batch = []
        walker_speed = []
        for spawn_point in spawn_points:
            walker_bp = random.choice(blueprintsWalkers)
            # set as not invincible
            if walker_bp.has_attribute('is_invincible'):
                walker_bp.set_attribute('is_invincible', 'false')
            # set the max speed
            if walker_bp.has_attribute('speed'):
                if (random.random() > percentagePedestriansRunning):
                    # walking
                    walker_speed.append(walker_bp.get_attribute('speed').recommended_values[1])
                else:
                    # running
                    walker_speed.append(walker_bp.get_attribute('speed').recommended_values[2])
            else:
                print("Walker has no speed")
                walker_speed.append(0.0)
            batch.append(SpawnActor(walker_bp, spawn_point))
        results = client.apply_batch_sync(batch, True)
        walker_speed2 = []
        for i in range(len(results)):
            if results[i].error:
                logging.error(results[i].error)
            else:
                walkers_list.append({"id": results[i].actor_id})
                walker_speed2.append(walker_speed[i])
        walker_speed = walker_speed2
        # 3. we spawn the walker controller
        batch = []
        walker_controller_bp = world.get_blueprint_library().find('controller.ai.walker')
        for i in range(len(walkers_list)):
            batch.append(SpawnActor(walker_controller_bp, carla.Transform(), walkers_list[i]["id"]))
        results = client.apply_batch_sync(batch, True)
        for i in range(len(results)):
            if results[i].error:
                logging.error(results[i].error)
            else:
                walkers_list[i]["con"] = results[i].actor_id
        # 4. we put together the walkers and controllers id to get the objects from their id
        for i in range(len(walkers_list)):
            all_id.append(walkers_list[i]["con"])
            all_id.append(walkers_list[i]["id"])
        all_actors = world.get_actors(all_id)

        # wait for a tick to ensure client receives the last transform of the walkers we have just created
        if args.asynch or not synchronous_master:
            world.wait_for_tick()
        else:
            world.tick()

        # 5. initialize each controller and set target to walk to (list is [controler, actor, controller, actor ...])
        # set how many pedestrians can cross the road
        world.set_pedestrians_cross_factor(percentagePedestriansCrossing)
        for i in range(0, len(all_id), 2):
            # start walker
            all_actors[i].start()
            # head inward (into the scene) under staging, else a random point
            all_actors[i].go_to_location(_random_scene_navpoint(world, staging))
            # max speed
            all_actors[i].set_max_speed(float(walker_speed[int(i/2)]))

        print('spawned %d vehicles and %d walkers, press Ctrl+C to exit.' % (len(vehicles_list), len(walkers_list)))

        # Example of how to use Traffic Manager parameters
        traffic_manager.global_percentage_speed_difference(30.0)

        # Boundary-aware respawn: once an actor has been into the scene and returns to the edge ring,
        # a vehicle is despawned and a fresh one enters at an inward ring point (constant count),
        # and a walker is re-aimed back inward — so traffic continuously enters and leaves at the
        # boundary, never popping in/out mid-scene. Checked about once a second in the existing loop.
        veh_entered = {vid: False for vid in vehicles_list}
        walk_entered = {}
        veh_target = len(vehicles_list)      # maintain this many vehicles in play
        last_staging_check = 0.0
        # Check often (a fast car can cross the ~30 m ring in a second; a tighter cadence catches it
        # in the ring and despawns it before it can reach the bound and fall off the edge).
        STAGING_CHECK_S = 0.25

        def _spawn_entrant():
            """Spawn one autopilot vehicle at an inward edge-ring point. The ring is thin, so its few
            spawn points are often momentarily occupied — try several before giving up. Returns True
            on success."""
            if not veh_ring_sps:
                return False
            pts = list(veh_ring_sps)
            random.shuffle(pts)
            for sp in pts[:10]:
                bp = random.choice(blueprints)
                if bp.has_attribute('color'):
                    bp.set_attribute('color', random.choice(bp.get_attribute('color').recommended_values))
                bp.set_attribute('role_name', 'autopilot')
                try:
                    nv = world.spawn_actor(bp, sp)
                    nv.set_autopilot(True, traffic_manager.get_port())
                    vehicles_list.append(nv.id); veh_entered[nv.id] = False
                    return True
                except Exception:
                    continue
            return False

        while True:
            if not args.asynch and synchronous_master:
                world.tick()
            else:
                world.wait_for_tick()

            if not staging or (time.time() - last_staging_check) < STAGING_CHECK_S:
                continue
            last_staging_check = time.time()

            # vehicles: mark "entered" on first time inside the scene; on return to the ring, despawn + respawn
            try:
                veh_actors = {a.id: a for a in world.get_actors(vehicles_list)} if vehicles_list else {}
            except Exception:
                veh_actors = {}
            for vid in list(vehicles_list):
                a = veh_actors.get(vid)
                if a is None:
                    continue
                loc = a.get_location()
                if _in_scene(loc.x, loc.y, staging):
                    veh_entered[vid] = True
                elif veh_entered.get(vid):
                    # entered the scene and has now left it (into the ring or already past the bound,
                    # caught either way so a fast car can't slip out between checks): despawn + respawn
                    try: a.destroy()
                    except Exception: pass
                    vehicles_list.remove(vid); veh_entered.pop(vid, None)
                    _spawn_entrant()

            # Top up toward the target (covers despawns + respawns that failed on a spawn collision).
            _topup_tries = 0
            while len(vehicles_list) < veh_target and _topup_tries < 6:
                _topup_tries += 1
                if not _spawn_entrant():
                    break

            # walkers: re-aim back into the scene when they reach the ring after entering (v1: kept in
            # play rather than despawned — controller teardown/rebuild is a heavier follow-up)
            for i in range(0, len(all_id), 2):
                try:
                    wloc = all_actors[i + 1].get_location()
                except Exception:
                    continue
                wid = all_id[i + 1]
                if _in_scene(wloc.x, wloc.y, staging):
                    walk_entered[wid] = True
                elif walk_entered.get(wid):
                    try:
                        all_actors[i].go_to_location(_random_scene_navpoint(world, staging))
                    except Exception:
                        pass
                    walk_entered[wid] = False

    finally:

        if not args.asynch and synchronous_master:
            settings = world.get_settings()
            settings.synchronous_mode = False
            settings.no_rendering_mode = False
            settings.fixed_delta_seconds = None
            world.apply_settings(settings)

        print('\ndestroying %d vehicles' % len(vehicles_list))
        client.apply_batch([carla.command.DestroyActor(x) for x in vehicles_list])

        # stop walker controllers (list is [controller, actor, controller, actor ...])
        for i in range(0, len(all_id), 2):
            all_actors[i].stop()

        print('\ndestroying %d walkers' % len(walkers_list))
        client.apply_batch([carla.command.DestroyActor(x) for x in all_id])

        time.sleep(0.5)

if __name__ == '__main__':

    try:
        main()
    except KeyboardInterrupt:
        pass
    finally:
        print('\ndone.')
