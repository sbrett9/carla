// Source: carla/trafficmanager/TrafficManagerServer.h
//
// The msgpack-RPC server that the CARLA simulator (port 2000) calls *into*
// when a script does `SetAutopilot(FutureActor, True, tm.get_port())`.
// The simulator opens a reverse RPC channel back to the TM on the TM's port
// (default 8000) and pushes per-frame events; the TM responds.
//
// This class wires every binding listed in TrafficManagerServer.h to a method
// on a user-supplied ITrafficManagerCallback. The callback IS the local TM
// instance — Wave 4 produces it (TrafficManagerLocal).
//
// Method names below match TrafficManagerServer.h exactly. Do not rename;
// they're the literal wire protocol.
#nullable enable
using CarlaNet.Transport.MsgPackRpc.Server;
using Microsoft.Extensions.Logging;

namespace CarlaNet.TrafficManager;

/// <summary>
/// RPC server that exposes a <see cref="ITrafficManagerCallback"/> over
/// msgpack-RPC on a TCP port. Matches the C++
/// <c>traffic_manager::TrafficManagerServer</c> in
/// <c>TrafficManagerServer.h</c>.
/// </summary>
/// <remarks>
/// Construction wires the handlers. Call <see cref="StartAsync"/> to begin
/// accepting connections (mirrors the C++ ctor's call to
/// <c>server.async_run()</c>). Each RPC call is dispatched on a thread-pool
/// thread by the underlying <see cref="MsgPackRpcServer"/>.
/// </remarks>
public sealed class TrafficManagerServer : IAsyncDisposable
{
    private readonly MsgPackRpcServer _server;
    private readonly MsgPackRpcHandlerRegistry _registry;
    private readonly ITrafficManagerCallback _tm;

    /// <summary>The TCP port the server is listening on.</summary>
    public int Port => _server.Port;

    /// <summary>
    /// All RPC method names registered (in upstream definition order).
    /// Useful for diagnostics and tests.
    /// </summary>
    public IReadOnlyList<string> RegisteredMethods => _registry.RegisteredMethods;

    public TrafficManagerServer(ITrafficManagerCallback tm, int port = 8000, ILogger? logger = null)
    {
        _tm = tm;
        _server = new MsgPackRpcServer(port, logger);
        _registry = new MsgPackRpcHandlerRegistry(_server);
        BindAll();
    }

    /// <summary>Begin accepting RPC calls (mirrors C++ <c>async_run()</c>).</summary>
    public Task StartAsync(CancellationToken ct = default) => _server.StartAsync(ct);

    /// <summary>Stop accepting calls and drain in-flight handlers.</summary>
    public Task StopAsync() => _server.StopAsync();

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    // Bind names below match TrafficManagerServer.h exactly. Order is
    // preserved against the C++ file as a sanity aid when reviewing.
    private void BindAll()
    {
        // register_vehicle / unregister_vehicle — note the SINGULAR form,
        // matching upstream TrafficManagerServer.h:73 and :83. The C++ lambda
        // takes `std::vector<carla::rpc::Actor>` which CarlaNet's MessagePack
        // formatter deserializes straight into an Actor[]. We expose
        // IReadOnlyList<Actor> to the callback for ergonomics.
        _registry.Bind<Actor[]>("register_vehicle", actors => _tm.RegisterVehicles(actors));
        _registry.Bind<Actor[]>("unregister_vehicle", actors => _tm.UnregisterVehicles(actors));

        // Per-actor knobs
        _registry.Bind<Actor, float>("set_percentage_speed_difference",
            (a, p) => _tm.SetPercentageSpeedDifference(a, p));
        _registry.Bind<Actor, float>("set_lane_offset",
            (a, o) => _tm.SetLaneOffset(a, o));
        _registry.Bind<Actor, float>("set_desired_speed",
            (a, v) => _tm.SetDesiredSpeed(a, v));
        _registry.Bind<Actor, bool>("update_vehicle_lights",
            (a, u) => _tm.SetUpdateVehicleLights(a, u));

        // Global knobs
        _registry.Bind<float>("set_global_percentage_speed_difference",
            p => _tm.SetGlobalPercentageSpeedDifference(p));
        _registry.Bind<float>("set_global_lane_offset",
            o => _tm.SetGlobalLaneOffset(o));

        // Collision detection
        _registry.Bind<Actor, Actor, bool>("set_collision_detection",
            (refA, otherA, detect) => _tm.SetCollisionDetection(refA, otherA, detect));

        // Lane change
        _registry.Bind<Actor, bool>("set_force_lane_change",
            (a, dir) => _tm.SetForceLaneChange(a, dir));
        _registry.Bind<Actor, bool>("set_auto_lane_change",
            (a, enable) => _tm.SetAutoLaneChange(a, enable));

        // Distance to leading vehicle
        _registry.Bind<Actor, float>("set_distance_to_leading_vehicle",
            (a, d) => _tm.SetDistanceToLeadingVehicle(a, d));
        _registry.Bind<float>("set_global_distance_to_leading_vehicle",
            d => _tm.SetGlobalDistanceToLeadingVehicle(d));

        // Ignore rules
        _registry.Bind<Actor, float>("set_percentage_running_light",
            (a, p) => _tm.SetPercentageRunningLight(a, p));
        _registry.Bind<Actor, float>("set_percentage_running_sign",
            (a, p) => _tm.SetPercentageRunningSign(a, p));
        _registry.Bind<Actor, float>("set_percentage_ignore_walkers",
            (a, p) => _tm.SetPercentageIgnoreWalkers(a, p));
        _registry.Bind<Actor, float>("set_percentage_ignore_vehicles",
            (a, p) => _tm.SetPercentageIgnoreVehicles(a, p));

        // Keep-slow-lane / random-lane-change
        _registry.Bind<Actor, float>("set_percentage_keep_slow_lane_rule",
            (a, p) => _tm.SetKeepSlowLanePercentage(a, p));
        _registry.Bind<Actor, float>("set_percentage_random_left_lanechange",
            (a, p) => _tm.SetRandomLeftLaneChangePercentage(a, p));
        _registry.Bind<Actor, float>("set_percentage_random_right_lanechange",
            (a, p) => _tm.SetRandomRightLaneChangePercentage(a, p));

        // Hybrid / OSM modes
        _registry.Bind<bool>("set_hybrid_physics_mode",
            m => _tm.SetHybridPhysicsMode(m));
        _registry.Bind<float>("set_hybrid_physics_radius",
            r => _tm.SetHybridPhysicsRadius(r));
        _registry.Bind<bool>("set_osm_mode",
            m => _tm.SetOSMMode(m));

        // Imported paths.
        // Upstream uses `Path = std::vector<cg::Location>` → we land it as
        // `Location[]` and the callback adapts to IReadOnlyList<Location>.
        _registry.Bind<Actor, Location[], bool>("set_path",
            (a, path, empty) => _tm.SetCustomPath(a, path, empty));
        _registry.Bind<ActorId, bool>("remove_custom_path",
            (id, removePath) => _tm.RemoveUploadPath(id, removePath));
        _registry.Bind<ActorId, Location[]>("update_custom_path",
            (id, path) => _tm.UpdateUploadPath(id, path));

        // Imported routes (Route = std::vector<uint8_t>).
        _registry.Bind<Actor, byte[], bool>("set_imported_route",
            (a, route, empty) => _tm.SetImportedRoute(a, route, empty));
        _registry.Bind<ActorId, bool>("remove_imported_route",
            (id, removePath) => _tm.RemoveImportedRoute(id, removePath));
        _registry.Bind<ActorId, byte[]>("update_imported_route",
            (id, route) => _tm.UpdateImportedRoute(id, route));

        // Respawn dormant
        _registry.Bind<bool>("set_respawn_dormant_vehicles",
            m => _tm.SetRespawnDormantVehicles(m));
        _registry.Bind<float, float>("set_boundaries_respawn_dormant_vehicles",
            (lower, upper) => _tm.SetBoundariesRespawnDormantVehicles(lower, upper));

        // Action queries — note these are bound to VOID lambdas in C++ even
        // though the underlying `tm->GetNextAction` returns a value. We
        // preserve the void wire shape (upstream simply discards the result).
        _registry.Bind<ActorId>("get_next_action",
            id => _tm.GetNextAction(id));
        _registry.Bind<ActorId>("get_all_actions",
            id => _tm.GetActionBuffer(id));

        // Lifecycle / sync mode
        _registry.Bind("shut_down", () => _tm.ShutDown());
        _registry.Bind<bool>("set_synchronous_mode",
            m => _tm.SetSynchronousMode(m));
        _registry.Bind<double>("set_synchronous_mode_timeout_in_milisecond",
            t => _tm.SetSynchronousModeTimeOutInMiliSecond(t));
        _registry.Bind<ulong>("set_random_device_seed",
            s => _tm.SetRandomDeviceSeed(s));
        _registry.BindFunc<bool>("synchronous_tick",
            () => _tm.SynchronousTick());

        // Health check — empty body in C++. Just acknowledge.
        _registry.Bind("health_check_remote_TM", () => { /* no-op, matches C++ */ });
    }
}
