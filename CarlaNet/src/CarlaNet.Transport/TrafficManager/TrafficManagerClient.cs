// §11 — Traffic Manager RPC client.
// Protocol: msgpack-RPC over TCP, default port 8000, timeout 2000ms (from Constants.h).
// RPC method names are the server binding names from TrafficManagerServer.h.
// Note: set_percentage_keep_slow_lane_rule is the server name (§13.4 — TM client has a bug).
using CarlaNet.Transport.MsgPackRpc;

namespace CarlaNet.Transport.TrafficManager;

public sealed class TrafficManagerClient : IAsyncDisposable
{
    private readonly MsgPackRpcClient _rpc;

    public TrafficManagerClient(string host, int port = 8000)
        => _rpc = new MsgPackRpcClient(host, port, TimeSpan.FromMilliseconds(2000));

    public Task RegisterVehicleAsync(IReadOnlyList<Actor> actors)
        => _rpc.CallVoidAsync("register_vehicle", actors);

    public Task UnregisterVehicleAsync(IReadOnlyList<Actor> actors)
        => _rpc.CallVoidAsync("unregister_vehicle", actors);

    public Task SetPercentageSpeedDifferenceAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_speed_difference", actor, percentage);

    public Task SetDesiredSpeedAsync(Actor actor, float speed)
        => _rpc.CallVoidAsync("set_desired_speed", actor, speed);

    public Task SetGlobalPercentageSpeedDifferenceAsync(float percentage)
        => _rpc.CallVoidAsync("set_global_percentage_speed_difference", percentage);

    public Task SetLaneOffsetAsync(Actor actor, float offset)
        => _rpc.CallVoidAsync("set_lane_offset", actor, offset);

    public Task SetGlobalLaneOffsetAsync(float offset)
        => _rpc.CallVoidAsync("set_global_lane_offset", offset);

    public Task SetForceLaneChangeAsync(Actor actor, bool toLeft)
        => _rpc.CallVoidAsync("set_force_lane_change", actor, toLeft);

    public Task SetAutoLaneChangeAsync(Actor actor, bool enable)
        => _rpc.CallVoidAsync("set_auto_lane_change", actor, enable);

    public Task SetDistanceToLeadingVehicleAsync(Actor actor, float distance)
        => _rpc.CallVoidAsync("set_distance_to_leading_vehicle", actor, distance);

    public Task SetGlobalDistanceToLeadingVehicleAsync(float distance)
        => _rpc.CallVoidAsync("set_global_distance_to_leading_vehicle", distance);

    public Task SetCollisionDetectionAsync(Actor actor, Actor otherActor, bool enable)
        => _rpc.CallVoidAsync("set_collision_detection", actor, otherActor, enable);

    public Task SetPercentageIgnoreWalkersAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_ignore_walkers", actor, percentage);

    public Task SetPercentageIgnoreVehiclesAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_ignore_vehicles", actor, percentage);

    public Task SetPercentageRunningLightAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_running_light", actor, percentage);

    public Task SetPercentageRunningSignAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_running_sign", actor, percentage);

    // Source-verified server binding name (§13.4)
    public Task SetPercentageKeepSlowLaneRuleAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_keep_slow_lane_rule", actor, percentage);

    public Task SetPercentageRandomLeftLaneChangeAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_random_left_lanechange", actor, percentage);

    public Task SetPercentageRandomRightLaneChangeAsync(Actor actor, float percentage)
        => _rpc.CallVoidAsync("set_percentage_random_right_lanechange", actor, percentage);

    public Task UpdateVehicleLightsAsync(Actor actor, bool enable)
        => _rpc.CallVoidAsync("update_vehicle_lights", actor, enable);

    public Task SetHybridPhysicsModeAsync(bool enable)
        => _rpc.CallVoidAsync("set_hybrid_physics_mode", enable);

    public Task SetHybridPhysicsRadiusAsync(float radius)
        => _rpc.CallVoidAsync("set_hybrid_physics_radius", radius);

    public Task SetOsmModeAsync(bool enable)
        => _rpc.CallVoidAsync("set_osm_mode", enable);

    public Task SetRandomDeviceSeedAsync(ulong seed)
        => _rpc.CallVoidAsync("set_random_device_seed", seed);

    public Task SetPathAsync(Actor actor, IReadOnlyList<Location> path, bool empty)
        => _rpc.CallVoidAsync("set_path", actor, path, empty);

    public Task RemoveCustomPathAsync(ActorId actorId, bool empty)
        => _rpc.CallVoidAsync("remove_custom_path", actorId, empty);

    public Task UpdateCustomPathAsync(ActorId actorId, IReadOnlyList<Location> path)
        => _rpc.CallVoidAsync("update_custom_path", actorId, path);

    public Task SetImportedRouteAsync(Actor actor, IReadOnlyList<byte> route, bool empty)
        => _rpc.CallVoidAsync("set_imported_route", actor, route, empty);

    public Task RemoveImportedRouteAsync(ActorId actorId, bool empty)
        => _rpc.CallVoidAsync("remove_imported_route", actorId, empty);

    public Task UpdateImportedRouteAsync(ActorId actorId, IReadOnlyList<byte> route)
        => _rpc.CallVoidAsync("update_imported_route", actorId, route);

    public Task SetRespawnDormantVehiclesAsync(bool enable)
        => _rpc.CallVoidAsync("set_respawn_dormant_vehicles", enable);

    public Task SetBoundariesRespawnDormantVehiclesAsync(float lowerBound, float upperBound)
        => _rpc.CallVoidAsync("set_boundaries_respawn_dormant_vehicles", lowerBound, upperBound);

    public Task SetSynchronousModeAsync(bool enable)
        => _rpc.CallVoidAsync("set_synchronous_mode", enable);

    public Task SetSynchronousModeTimeoutAsync(double milliseconds)
        => _rpc.CallVoidAsync("set_synchronous_mode_timeout_in_milisecond", milliseconds);

    public Task<bool> SynchronousTickAsync()
        => _rpc.CallAsync<bool>("synchronous_tick");

    public Task ShutDownAsync()
        => _rpc.CallVoidAsync("shut_down");

    public Task HealthCheckAsync()
        => _rpc.CallVoidAsync("health_check_remote_TM");

    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}
