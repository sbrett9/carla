// Keeps routed vehicles on the routes they were planned, and says so out loud when one is not.
//
// A route is computed once, before the vehicle spawns (see RoutePlanner), and handed to the vehicle
// as a dense list of breadcrumbs through Parameters.SetCustomPath. Things then happen to the vehicle
// that the plan did not anticipate: an automatic lane change to pass an obstacle (on by default, and
// it empties the horizon buffer outright), a shove from a collision, a junction the horizon walk
// took differently from the plan. This class watches for that and replans.
//
// Where the plan lives. NOT in the horizon buffer. A lane change pops every waypoint in the buffer
// and re-seeds it from the change-over point, so anything kept there is destroyed by an entirely
// routine manoeuvre. The breadcrumbs live in Parameters, which LocalizationStage re-reads every tick
// and whose unconsumed remainder it writes back, and the route itself lives here — both survive a
// lane change.
//
// Detection is O(1) per vehicle per tick: the buffer head is the vehicle's current position on the
// road graph, and the route knows whether it covers that waypoint (or its lane-change neighbour, so
// that overtaking within the same road is not mistaken for a departure).
//
// Replanning is NOT done on the tick. A search can touch every reachable waypoint on the map, and
// the traffic-manager tick holds the registration lock for its whole duration — a slow tick blocks
// whichever thread owns world.tick(), which is how a viewer freezes with no output at all. The tick
// only ever enqueues; one background thread does the searching and installs the result through the
// same thread-safe parameter store the tick reads from. Until it lands the vehicle carries on under
// the existing greedy steering, which is exactly what it would have done with no planner at all.
//
// Every departure, replan, failure and fallback prints one line. This is deliberate and not behind a
// diagnostic flag: a vehicle silently not going where it was sent is the failure mode this whole
// subsystem exists to remove, and it must be visible from the console without re-running anything.
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// Per-vehicle route bookkeeping, route-departure detection, and off-tick replanning.
/// </summary>
internal sealed class RouteSupervisor : IDisposable
{
    /// <summary>
    /// Longest a vehicle whose replans keep failing will wait between attempts. Failures repeat when
    /// a vehicle ends up somewhere with no route to its destination at all; without a growing delay
    /// it would re-run a full-map search every tick for the rest of its life.
    /// </summary>
    private const int MaxReplanBackoffMs = 30_000;

    /// <summary>Growth of the delay between consecutive failed replans for one vehicle.</summary>
    private const int ReplanBackoffStepMs = 2_000;

    /// <summary>
    /// Upper bound on vehicles waiting to be replanned. One entry per vehicle at most (a vehicle
    /// with a search already running is not re-queued), so this only binds if a whole large fleet
    /// leaves its routes at once; past it, requests are dropped and retried on a later tick rather
    /// than allowed to accumulate.
    /// </summary>
    private const int MaxPendingReplans = 512;

    private readonly RoutePlanner _planner;
    private readonly Parameters _parameters;
    private readonly System.IO.TextWriter _report;

    private readonly ConcurrentDictionary<ActorId, RoutedVehicle> _routed = new();
    private readonly System.Collections.Concurrent.BlockingCollection<ActorId> _pending =
        new(MaxPendingReplans);

    private readonly object _threadGate = new();
    private Thread? _replanThread;
    private bool _disposed;

    /// <param name="report">
    /// Where route events are written. Defaults to standard error, which is where the traffic
    /// manager's other output goes and where the embedded Python host picks it up.
    /// </param>
    public RouteSupervisor(RoutePlanner planner, Parameters parameters, System.IO.TextWriter? report = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _report = report ?? Console.Error;
    }

    /// <summary>
    /// State for one vehicle that has been given a route. Written from both the traffic-manager
    /// worker (via <see cref="Observe"/>) and the replan thread, so every read and write of it is
    /// taken under a lock on the instance. The search itself runs outside that lock — holding it
    /// across a search is the thing this class exists to avoid.
    /// </summary>
    private sealed class RoutedVehicle
    {
        public required PlannedRoute Route;
        public required Location Destination;
        public Location LastSeenLocation;
        public int FailedReplans;
        public long NextReplanAllowedAtMs;
        public bool HasLeftRoute;
        public bool ReplanRunning;
        public bool SteeringGreedily;
        public bool Retired;
    }

    // ─────────────────────────────────────────────────────────────────────
    //                      Called from the client's thread
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Put <paramref name="actorId"/> on <paramref name="route"/>: install the breadcrumbs and start
    /// watching whether the vehicle stays on them. Replaces any route the vehicle already had.
    /// </summary>
    public void Assign(ActorId actorId, PlannedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        _routed[actorId] = new RoutedVehicle
        {
            Route = route,
            Destination = route.Destination,
            LastSeenLocation = route.Path.Count > 0 ? route.Path[0] : route.Destination,
        };
        _parameters.SetCustomPath(actorId, route.Path, emptyBuffer: true);
        EnsureReplanThread();
    }

    /// <summary>Number of vehicles currently following a planned route.</summary>
    public int RoutedVehicleCount => _routed.Count;

    // ─────────────────────────────────────────────────────────────────────
    //                  Called from the traffic-manager worker
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One tick's observation of a vehicle. <paramref name="graphPosition"/> is the head of its
    /// horizon buffer — the waypoint it is currently on. Does nothing at all for a vehicle that was
    /// never given a route, so traffic running without routes pays a dictionary miss and no more.
    /// </summary>
    public void Observe(ActorId actorId, Location vehicleLocation, SimpleWaypoint? graphPosition)
    {
        if (_routed.IsEmpty) return;
        if (!_routed.TryGetValue(actorId, out RoutedVehicle? vehicle)) return;

        // LocalizationStage.ImportPath drops the custom path once the last breadcrumb has been
        // consumed, so an empty path means the vehicle has driven the whole route.
        bool routeConsumed = _parameters.GetCustomPath(actorId).Count == 0;

        bool requestReplan = false;
        lock (vehicle)
        {
            if (vehicle.Retired) return;
            vehicle.LastSeenLocation = vehicleLocation;

            if (routeConsumed)
            {
                vehicle.Retired = true;
                _routed.TryRemove(actorId, out _);
                if (!vehicle.SteeringGreedily)
                    Report($"vehicle {actorId} reached the end of its planned route.");
                return;
            }

            // Handed back to greedy steering: there is no plan left to be off.
            if (vehicle.SteeringGreedily) return;
            if (graphPosition is null) return;

            if (vehicle.Route.Covers(graphPosition))
            {
                if (vehicle.HasLeftRoute)
                {
                    vehicle.HasLeftRoute = false;
                    vehicle.FailedReplans = 0;
                    vehicle.NextReplanAllowedAtMs = 0;
                    Report($"vehicle {actorId} is back on its planned route at {Format(vehicleLocation)}.");
                }
                return;
            }

            if (!vehicle.HasLeftRoute)
            {
                vehicle.HasLeftRoute = true;
                Report($"vehicle {actorId} left its planned route at {Format(vehicleLocation)}; "
                       + $"replanning to {Format(vehicle.Destination)}.");
            }

            long now = Environment.TickCount64;
            if (!vehicle.ReplanRunning && now >= vehicle.NextReplanAllowedAtMs)
            {
                vehicle.ReplanRunning = true;
                requestReplan = true;
            }
        }

        if (!requestReplan) return;

        bool queued;
        // TryAdd throws once the queue has been closed, which happens only while shutting down.
        try { queued = _pending.TryAdd(actorId); }
        catch (InvalidOperationException) { queued = false; }

        if (!queued)
        {
            // The queue is saturated (or closing). Release the in-flight flag so a later tick can
            // try again rather than leaving the vehicle marked as replanning forever.
            lock (vehicle) { vehicle.ReplanRunning = false; }
        }
    }

    /// <summary>
    /// Forget a vehicle. Called when the vehicle is destroyed: a despawned vehicle never comes back,
    /// so there is no route to resume and nothing to carry over.
    /// </summary>
    public void RemoveActor(ActorId actorId)
    {
        if (!_routed.TryRemove(actorId, out RoutedVehicle? vehicle)) return;
        lock (vehicle) { vehicle.Retired = true; }
        DiscardPath(actorId);
    }

    /// <summary>Drop every routed vehicle. Called when the traffic manager is stopped or reset.</summary>
    public void Reset()
    {
        foreach (var entry in _routed)
        {
            lock (entry.Value) { entry.Value.Retired = true; }
            DiscardPath(entry.Key);
        }
        _routed.Clear();
    }

    /// <summary>
    /// Drop the breadcrumbs installed for a vehicle that no longer exists.
    /// </summary>
    /// <remarks>
    /// The parameter store keeps a vehicle's path until something takes it away, and destroying an
    /// actor does not. That barely mattered while a route was a single destination point, but a
    /// planned route is hundreds of waypoints, and a scenario that spawns and despawns traffic for
    /// hours would accumulate one abandoned route per vehicle for as long as it ran.
    /// </remarks>
    private void DiscardPath(ActorId actorId)
    {
        _parameters.RemoveUploadPath(actorId, removePath: true);    // the waypoints
        _parameters.RemoveUploadPath(actorId, removePath: false);   // the flush-the-buffer flag
    }

    // ─────────────────────────────────────────────────────────────────────
    //                            Replan thread
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start the replan thread on first use. Traffic that never gets a route never starts it.
    /// </summary>
    private void EnsureReplanThread()
    {
        if (_replanThread is not null) return;
        lock (_threadGate)
        {
            if (_replanThread is not null || _disposed) return;
            _replanThread = new Thread(ReplanLoop)
            {
                IsBackground = true,
                Name = "CarlaNet.TM.RoutePlanner",
            };
            _replanThread.Start();
        }
    }

    private void ReplanLoop()
    {
        foreach (ActorId actorId in _pending.GetConsumingEnumerable())
        {
            try { Replan(actorId); }
            catch (Exception ex)
            {
                // A failed search must not take the thread down with it, or every subsequent
                // departure would go unnoticed and unreported.
                Report($"vehicle {actorId} replan raised {ex.GetType().Name}: {ex.Message}");
                if (_routed.TryGetValue(actorId, out RoutedVehicle? vehicle))
                {
                    lock (vehicle) { vehicle.ReplanRunning = false; }
                }
            }
        }
    }

    private void Replan(ActorId actorId)
    {
        if (!_routed.TryGetValue(actorId, out RoutedVehicle? vehicle)) return;

        Location origin;
        Location destination;
        lock (vehicle)
        {
            if (vehicle.Retired) return;
            origin = vehicle.LastSeenLocation;
            destination = vehicle.Destination;
        }

        // The search — the whole reason this runs off the tick.
        PlannedRoute? replanned = _planner.Plan(origin, destination);

        lock (vehicle)
        {
            vehicle.ReplanRunning = false;
            if (vehicle.Retired) return;

            if (replanned is not null && replanned.Path.Count > 0)
            {
                vehicle.Route = replanned;
                vehicle.HasLeftRoute = false;
                vehicle.FailedReplans = 0;
                vehicle.NextReplanAllowedAtMs = 0;
                // The worker may write back the unconsumed remainder of the OLD path in the same
                // instant and overwrite this. That costs one wasted search: the vehicle is still off
                // its route, so the next tick's observation queues another replan straight away.
                // Taking a lock wide enough to close that window would put this search back on the
                // tick's critical path, which is the one thing it must never be on.
                _parameters.SetCustomPath(actorId, replanned.Path, emptyBuffer: true);
                Report($"vehicle {actorId} replanned to {Format(destination)}: "
                       + $"{replanned.Path.Count} waypoints, {replanned.LengthMetres:F0} m.");
                return;
            }

            vehicle.FailedReplans++;
            int attemptLimit = _parameters.GetRouteReplanAttemptLimit();
            bool fallbackAllowed = _parameters.GetRouteGreedyFallbackEnabled();

            if (fallbackAllowed && attemptLimit > 0 && vehicle.FailedReplans >= attemptLimit)
            {
                vehicle.SteeringGreedily = true;
                // Hand the vehicle the bare destination, which is what an unplanned routed vehicle
                // has always been given: the horizon walk steers toward it junction by junction.
                _parameters.SetCustomPath(actorId, new List<Location> { destination }, emptyBuffer: true);
                Report($"vehicle {actorId} could not be replanned to {Format(destination)} in "
                       + $"{vehicle.FailedReplans} attempts; steering greedily toward it instead.");
                return;
            }

            int backoffMs = Math.Min(vehicle.FailedReplans * ReplanBackoffStepMs, MaxReplanBackoffMs);
            vehicle.NextReplanAllowedAtMs = Environment.TickCount64 + backoffMs;
            string ceiling = attemptLimit > 0 && fallbackAllowed ? $" of {attemptLimit}" : string.Empty;
            Report($"vehicle {actorId} has no route from {Format(origin)} to {Format(destination)} "
                   + $"(attempt {vehicle.FailedReplans}{ceiling}); retrying in {backoffMs / 1000.0:F0} s.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Route events go to the console unconditionally. They are edge-triggered — a departure is
    /// announced once, not once per tick — and repeated failures are spaced out by the backoff, so
    /// the output stays readable with a large fleet.
    /// </summary>
    private void Report(string message) => _report.WriteLine("[route] " + message);

    private static string Format(Location location)
        => $"({location.X:F1}, {location.Y:F1})";

    public void Dispose()
    {
        lock (_threadGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _pending.CompleteAdding(); } catch (ObjectDisposedException) { }
        try { _replanThread?.Join(TimeSpan.FromSeconds(2)); }
        catch (ThreadStateException) { }
        _replanThread = null;
        try { _pending.Dispose(); } catch (InvalidOperationException) { }
        Reset();
    }
}
