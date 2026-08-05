// Source: carla/trafficmanager/TrafficManager.{h,cpp}
//
// Thin public facade the Python shim instantiates. Owns:
//  - the OpenDRIVE Map parsed from the server's get_map_data RPC,
//  - the InMemoryMap (SetUp'd once at construction — heavy ~500 ms on Town03),
//  - the TrafficManagerLocal (which in turn owns the RPC server + worker).
//
// API matches TrafficManager.h's 30-method Python-facing surface. Method
// names are PascalCase here; the Python shim re-exports them under
// snake_case to keep `carla.TrafficManager` API parity.
//
// The Map is cached in a process-wide dictionary keyed by map name so a
// second `client.get_trafficmanager()` reuses the same parsed graph.
#nullable enable

using CarlaNet.Map.OpenDrive;
using CarlaNet.Transport;
using Microsoft.Extensions.Logging;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.TrafficManager;

/// <summary>
/// Public facade that the Python shim and external C# callers use. One
/// instance corresponds to a single TM RPC port (default 8000).
/// </summary>
public sealed class TrafficManager : IAsyncDisposable
{
    private static readonly Dictionary<string, (RoadMap Map, InMemoryMap LocalMap)> _mapCache = new();
    private static readonly object _mapCacheGate = new();

    private readonly TrafficManagerLocal _local;
    private readonly CarlaClient _client;
    private readonly ILogger? _logger;

    /// <summary>The port the RPC server is bound to (may differ from the
    /// requested port if 8000..8009 were all busy).</summary>
    public int Port => _local.Port;

    /// <summary>True if the RPC server is listening and the worker is running.</summary>
    public bool IsRunning => _local.IsRunning;

    /// <summary>
    /// Construct a TM bound to <paramref name="client"/>. Fetches the
    /// OpenDRIVE XML synchronously, parses it, builds the InMemoryMap,
    /// and starts the RPC server + worker thread. Blocks for ~300–500 ms
    /// on the first call against a given map.
    /// </summary>
    /// <param name="client">Connected CarlaClient (the world observer
    /// should already be running for ALSM to function fully).</param>
    /// <param name="port">Requested TM RPC port. The actual bound port may
    /// walk to <paramref name="port"/>+1..+9 if the requested port is taken.</param>
    /// <param name="logger">Optional logger.</param>
    public TrafficManager(CarlaClient client, ushort port = 8000, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;

        // ── Fetch + parse + cache the map ─────────────────────────────
        var (map, localMap, mapName) = FetchOrBuildMap(client, logger);
        _logger?.LogDebug("TM bound to map '{Map}'", mapName);

        // ── Construct the orchestrator (does NOT start it yet) ────────
        _local = new TrafficManagerLocal(client, localMap, port, 0.0f, logger);
    }

    /// <summary>
    /// Fetches the OpenDRIVE XML via <c>get_map_data</c> RPC, parses it
    /// into a <see cref="Map"/>, then builds + warms up an
    /// <see cref="InMemoryMap"/>. Cached per map-name across calls.
    /// </summary>
    private static (RoadMap Map, InMemoryMap LocalMap, string Name) FetchOrBuildMap(
        CarlaClient client, ILogger? logger)
    {
        // We use the map info name as the cache key (it's the canonical
        // identifier the server returns from get_map_info).
        var info = client.GetMapInfoAsync().GetAwaiter().GetResult();
        string mapName = info.Name ?? string.Empty;

        lock (_mapCacheGate)
        {
            if (_mapCache.TryGetValue(mapName, out var cached))
            {
                logger?.LogDebug("Reusing cached InMemoryMap for '{Name}'", mapName);
                return (cached.Map, cached.LocalMap, mapName);
            }
        }

        // ── Cache miss: do the heavy lift ─────────────────────────────
        string xml = client.GetMapDataAsync().GetAwaiter().GetResult();
        var parsedMap = OpenDriveParser.Load(xml)
            ?? throw new InvalidOperationException(
                $"Failed to parse OpenDRIVE map '{mapName}' (length={xml?.Length ?? 0})");

        var localMap = new InMemoryMap(parsedMap);
        localMap.SetUp(); // expensive (~500 ms on Town03)

        lock (_mapCacheGate)
        {
            _mapCache[mapName] = (parsedMap, localMap);
        }
        return (parsedMap, localMap, mapName);
    }

    /// <summary>Spin up the RPC server + worker thread. Idempotent.</summary>
    public void Start() => _local.Start();

    /// <summary>Stop the worker and RPC server (matches upstream
    /// <c>TrafficManager::ShutDown</c>). Idempotent.</summary>
    public void ShutDown() => _local.ShutDown();

    public async ValueTask DisposeAsync()
    {
        await _local.DisposeAsync().ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────
    //           Public TM API — pass-throughs to TrafficManagerLocal
    // ─────────────────────────────────────────────────────────────────

    public void RegisterVehicles(IReadOnlyList<Actor> actors) => _local.RegisterVehicles(actors);
    public void UnregisterVehicles(IReadOnlyList<Actor> actors) => _local.UnregisterVehicles(actors);

    // ID-based overloads: fetch the Actor records from the server and forward.
    // Used by Python's apply_batch path where only the spawned ActorId is known.
    public void RegisterVehicleIds(IReadOnlyList<uint> ids)
    {
        if (ids is null || ids.Count == 0) return;
        var actors = _client.GetActorsByIdAsync(ids).GetAwaiter().GetResult();
        if (actors is { Count: > 0 }) _local.RegisterVehicles(actors);
    }

    public void UnregisterVehicleIds(IReadOnlyList<uint> ids)
    {
        if (ids is null || ids.Count == 0) return;
        var actors = _client.GetActorsByIdAsync(ids).GetAwaiter().GetResult();
        if (actors is { Count: > 0 }) _local.UnregisterVehicles(actors);
    }

    public void SetPercentageSpeedDifference(Actor actor, float percentage)
        => _local.SetPercentageSpeedDifference(actor, percentage);

    public void SetLaneOffset(Actor actor, float offset) => _local.SetLaneOffset(actor, offset);

    public void SetDesiredSpeed(Actor actor, float value) => _local.SetDesiredSpeed(actor, value);

    public void SetGlobalPercentageSpeedDifference(float percentage)
        => _local.SetGlobalPercentageSpeedDifference(percentage);

    public void SetGlobalLaneOffset(float offset) => _local.SetGlobalLaneOffset(offset);

    public void SetUpdateVehicleLights(Actor actor, bool doUpdate)
        => _local.SetUpdateVehicleLights(actor, doUpdate);

    public void SetCollisionDetection(Actor reference, Actor other, bool detect)
        => _local.SetCollisionDetection(reference, other, detect);

    public void SetForceLaneChange(Actor actor, bool direction)
        => _local.SetForceLaneChange(actor, direction);

    public void SetAutoLaneChange(Actor actor, bool enable)
        => _local.SetAutoLaneChange(actor, enable);

    public void SetDistanceToLeadingVehicle(Actor actor, float distance)
        => _local.SetDistanceToLeadingVehicle(actor, distance);

    public void SetPercentageIgnoreWalkers(Actor actor, float percentage)
        => _local.SetPercentageIgnoreWalkers(actor, percentage);

    public void SetPercentageIgnoreVehicles(Actor actor, float percentage)
        => _local.SetPercentageIgnoreVehicles(actor, percentage);

    public void SetPercentageRunningLight(Actor actor, float percentage)
        => _local.SetPercentageRunningLight(actor, percentage);

    public void SetPercentageRunningSign(Actor actor, float percentage)
        => _local.SetPercentageRunningSign(actor, percentage);

    public void SetGlobalDistanceToLeadingVehicle(float distance)
        => _local.SetGlobalDistanceToLeadingVehicle(distance);

    public void SetKeepSlowLanePercentage(Actor actor, float percentage)
        => _local.SetKeepSlowLanePercentage(actor, percentage);

    public void SetRandomLeftLaneChangePercentage(Actor actor, float percentage)
        => _local.SetRandomLeftLaneChangePercentage(actor, percentage);

    public void SetRandomRightLaneChangePercentage(Actor actor, float percentage)
        => _local.SetRandomRightLaneChangePercentage(actor, percentage);

    public void SetSynchronousMode(bool mode) => _local.SetSynchronousMode(mode);

    public void SetSynchronousModeTimeOutInMiliSecond(double timeMs)
        => _local.SetSynchronousModeTimeOutInMiliSecond(timeMs);

    public void SetHybridPhysicsMode(bool modeSwitch) => _local.SetHybridPhysicsMode(modeSwitch);

    public void SetHybridPhysicsRadius(float radius) => _local.SetHybridPhysicsRadius(radius);

    public void SetRandomDeviceSeed(ulong seed) => _local.SetRandomDeviceSeed(seed);

    public void SetOsmMode(bool modeSwitch) => _local.SetOSMMode(modeSwitch);

    public void SetCustomPath(Actor actor, IReadOnlyList<Location> path, bool emptyBuffer = true)
        => _local.SetCustomPath(actor, path, emptyBuffer);

    public void RemoveUploadPath(ActorId actorId, bool removePath)
        => _local.RemoveUploadPath(actorId, removePath);

    public void UpdateUploadPath(ActorId actorId, IReadOnlyList<Location> path)
        => _local.UpdateUploadPath(actorId, path);

    public void SetImportedRoute(Actor actor, IReadOnlyList<byte> route, bool emptyBuffer = true)
        => _local.SetImportedRoute(actor, route, emptyBuffer);

    public void RemoveImportedRoute(ActorId actorId, bool removePath)
        => _local.RemoveImportedRoute(actorId, removePath);

    public void UpdateImportedRoute(ActorId actorId, IReadOnlyList<byte> route)
        => _local.UpdateImportedRoute(actorId, route);

    // ─────────────────────────────────────────────────────────────────
    //                        Planned routes
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Search the road graph for a route from <paramref name="origin"/> to
    /// <paramref name="destination"/>. Returns null when no sequence of lanes connects them.
    /// </summary>
    /// <remarks>
    /// Runs the search on the calling thread, not on the traffic-manager tick — call it before
    /// spawning the vehicle, both to keep the tick free and so a spawn point with no route to the
    /// destination can be rejected before a vehicle exists at it.
    ///
    /// The result depends only on the two endpoints and the map, so the same scenario replayed with
    /// the same seed produces the same routes. Speed, collision avoidance and traffic-signal
    /// response remain emergent; only the route is decided in advance.
    /// </remarks>
    public PlannedRoute? PlanRoute(Location origin, Location destination)
        => _local.RoutePlanner.Plan(origin, destination);

    /// <summary>
    /// Put a vehicle on a route returned by <see cref="PlanRoute"/>. The route's waypoints are
    /// installed as the vehicle's path, and the vehicle is watched from then on: if it leaves the
    /// route it is replanned from wherever it now is to the same destination.
    /// </summary>
    public void ApplyRoute(Actor actor, PlannedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _local.RouteSupervisor.Assign(actor.Id, route);
    }

    /// <summary>Stop supervising a vehicle's route. Its current path is left in place.</summary>
    public void ClearRoute(Actor actor) => _local.RouteSupervisor.RemoveActor(actor.Id);

    /// <summary>Number of vehicles currently following a planned route.</summary>
    public int RoutedVehicleCount => _local.RouteSupervisor.RoutedVehicleCount;

    /// <summary>
    /// How many consecutive failed replans a vehicle may accumulate before
    /// <see cref="SetRouteGreedyFallbackEnabled">the greedy fallback</see> takes over. Zero means
    /// the fallback is never reached however often replanning fails. Default 3.
    /// </summary>
    public void SetRouteReplanAttemptLimit(int limit) => _local.SetRouteReplanAttemptLimit(limit);

    /// <summary>
    /// Whether a vehicle that cannot be replanned is eventually handed back to greedy steering
    /// toward its destination, rather than going on trying to find a real route. Off by default.
    /// </summary>
    public void SetRouteGreedyFallbackEnabled(bool enabled)
        => _local.SetRouteGreedyFallbackEnabled(enabled);

    public void SetRespawnDormantVehicles(bool modeSwitch) => _local.SetRespawnDormantVehicles(modeSwitch);

    public void SetBoundariesRespawnDormantVehicles(float lowerBound, float upperBound)
        => _local.SetBoundariesRespawnDormantVehicles(lowerBound, upperBound);

    public bool SynchronousTick() => _local.SynchronousTick();
}
