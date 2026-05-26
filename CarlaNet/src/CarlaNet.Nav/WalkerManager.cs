// Source: carla/nav/WalkerManager.{h,cpp}
//
// Per-walker AI state machine that sits on top of the Detour crowd (driven
// here by the sibling <see cref="Navigation"/> facade). For each registered
// walker we hold a <c>WalkerInfo</c> with:
//
//   * A route (list of <c>WalkerRoutePoint</c>: location + event + area type)
//   * A current index into that route
//   * One of four states: Idle, Walking, InEvent, Stop
//
// Once per simulator tick the orchestrator calls <see cref="Update"/>, which
// per-walker transitions:
//
//   Idle      → (no-op until SetWalkerRoute lands)
//   Walking   → distance² to current route point ≤ 1.0 m² → InEvent
//   InEvent   → dispatch the event (switch expression):
//                   Continue → stay in InEvent
//                   End      → SetWalkerNextPoint
//                   TimeOut  → SetWalkerRoute (re-plan)
//   Stop      → → Idle on the next tick
//
// External invocations come from <see cref="WalkerNavigation"/>:
//   * GoToLocation(id, dest) → SetWalkerRoute(id, dest)
//   * Stop(id)               → unregister (RemoveWalker)
//   * SetMaxSpeed            → relayed straight to Navigation
//
// Threading: the per-tick driver and the public mutators (registration,
// SetWalkerRoute) are protected by <see cref="_gate"/>. Mirrors the locking
// pattern used by CarlaNet.TrafficManager.
#nullable enable

namespace CarlaNet.Nav;

/// <summary>
/// Tiny seedable PRNG used to pick random destinations. Mirrors the
/// pattern from <c>CarlaNet.TrafficManager.RandomGenerator</c> but kept
/// local to avoid coupling the Nav project to the TM project (they are
/// parallel siblings — both depend on Transport, neither on each other).
/// </summary>
internal sealed class WalkerRandomGenerator
{
    private readonly Random _random;

    public WalkerRandomGenerator(ulong seed)
    {
        // Fold 64 → 32 bits via XOR; agrees with CarlaNet.TrafficManager.RandomGenerator.
        int seed32 = unchecked((int)((uint)seed ^ (uint)(seed >> 32)));
        _random = new Random(seed32);
    }

    /// <summary>Returns a uniform double in <c>[0, 100)</c>.</summary>
    public double NextPercent() => _random.NextDouble() * 100.0;
}

/// <summary>Mirrors C++ <c>nav::WalkerState</c> (WalkerManager.h:23-28).</summary>
internal enum WalkerState : byte
{
    Idle,
    Walking,
    InEvent,
    Stop,
}

/// <summary>
/// One point on a walker's route. Mutable so we can replace the embedded
/// event in place when its countdown timer ticks (mirrors C++ in-place
/// <c>event.time -= delta</c>). <see cref="AreaType"/> uses the constants
/// in <see cref="NavAreas"/> (sidebar values <c>Block / Sidewalk /
/// Crosswalk / Road / Grass</c>).
/// </summary>
internal struct WalkerRoutePoint
{
    public WalkerEvent Event;
    public Location Location;
    public byte AreaType;

    public WalkerRoutePoint(WalkerEvent ev, Location loc, byte area)
    {
        Event = ev;
        Location = loc;
        AreaType = area;
    }
}

/// <summary>
/// Per-walker bookkeeping. Direct translation of C++ <c>nav::WalkerInfo</c>
/// (WalkerManager.h:37-43). Held by reference in the <c>_walkers</c> dict
/// so the per-tick code can mutate <c>State</c> / <c>CurrentIndex</c> /
/// individual route points without re-inserting.
/// </summary>
internal sealed class WalkerInfo
{
    public Location From;
    public Location To;
    public int CurrentIndex;
    public WalkerState State = WalkerState.Idle;
    public List<WalkerRoutePoint> Route = new();

    /// <summary>
    /// Agent index returned by <see cref="Navigation.AddWalker"/>. Negative
    /// means "not registered in the crowd yet" — kept so the public ActorId
    /// surface can be presented before the underlying crowd insertion.
    /// </summary>
    public int AgentIndex = -1;

    /// <summary>
    /// Cached max speed (m/s) requested via <see cref="WalkerNavigation.SetMaxSpeed"/>.
    /// Persisted so a re-AddWalker after a re-load can replay the setting.
    /// </summary>
    public float MaxSpeed = 1.4f;

    /// <summary>
    /// User-requested "paused" flag set by <see cref="WalkerNavigation.Stop"/>.
    /// Distinct from the in-event pause (which the state machine drives
    /// directly through <see cref="Navigation.PauseWalker"/>).
    /// </summary>
    public bool ExternallyStopped;
}

/// <summary>
/// AI state machine for all walkers in the episode. One instance per
/// <see cref="WalkerNavigation"/>. Mirrors C++ <c>nav::WalkerManager</c>.
/// </summary>
internal sealed class WalkerManager
{
    // Mirrors C++ WalkerManager.cpp:169 — 60-second timeout on the
    // "stop and check for vehicles at a crosswalk" event.
    private const double StopAndCheckTimeoutSeconds = 60.0;

    // Half-vehicle proximity radius used by WalkerEventStopAndCheck when
    // querying HasVehicleNear (Navigation.cpp HasVehicleNear callers pass
    // 6.0 — squared in libcarla, so 2.45 m radius). We mirror upstream's
    // call site by passing the squared value through.
    private const float VehicleNearRadius = 6.0f;

    private readonly Navigation _navigation;
    private readonly WalkerRandomGenerator _rng;
    private readonly object _gate = new();

    // Public-id → per-walker state. Locking on _gate guards all reads/writes.
    private readonly Dictionary<ActorId, WalkerInfo> _walkers = new();

    // Reverse map (agentIndex → ActorId) — kept in sync with WalkerInfo.AgentIndex
    // so we can correlate Navigation's int-keyed callbacks (e.g. "agent N
    // is dead") back to a public ActorId without scanning the whole dict.
    private readonly Dictionary<int, ActorId> _agentToActor = new();

    /// <summary>
    /// Constructs the manager with the sibling <see cref="Navigation"/>
    /// instance plus an RNG seed (defaults to a time-based one if the
    /// caller hasn't called <c>SetPedestriansSeed</c> yet).
    /// </summary>
    public WalkerManager(Navigation navigation, ulong rngSeed = 0)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _rng = new WalkerRandomGenerator(rngSeed == 0 ? unchecked((ulong)Environment.TickCount64) : rngSeed);
    }

    /// <summary>
    /// Returns the navigation facade. Mirrors C++ <c>GetNavigation()</c> —
    /// used by event handlers that need to call back into Detour.
    /// </summary>
    public Navigation Navigation => _navigation;

    // ── Registration ───────────────────────────────────────────────────

    /// <summary>
    /// Registers a new walker. Adds the walker into the Detour crowd via
    /// <see cref="Navigation.AddWalker"/>; subsequent route requests will
    /// target this agent. Mirrors C++ <c>AddWalker(ActorId)</c>, but takes
    /// the spawn location + agent parameters (upstream reads these from the
    /// libcarla actor wrapper). Returns <c>true</c> on success.
    /// </summary>
    public bool AddWalker(ActorId id, Location startLocation, float radius, float height, float maxSpeed)
    {
        lock (_gate)
        {
            if (_walkers.ContainsKey(id))
                return false;

            int agentIndex = _navigation.AddWalker(startLocation, radius, height, maxSpeed);
            if (agentIndex < 0)
                return false;

            var info = new WalkerInfo
            {
                State = WalkerState.Idle,
                From = startLocation,
                AgentIndex = agentIndex,
                MaxSpeed = maxSpeed,
            };
            _walkers[id] = info;
            _agentToActor[agentIndex] = id;
            return true;
        }
    }

    /// <summary>
    /// Unregisters a walker. Mirrors C++ <c>RemoveWalker(ActorId)</c>.
    /// </summary>
    public bool RemoveWalker(ActorId id)
    {
        lock (_gate)
        {
            if (!_walkers.TryGetValue(id, out var info))
                return false;

            if (info.AgentIndex >= 0)
            {
                _navigation.RemoveWalker(info.AgentIndex);
                _agentToActor.Remove(info.AgentIndex);
            }
            _walkers.Remove(id);
            return true;
        }
    }

    /// <summary>
    /// Returns the public ids of every registered walker. Snapshot copy —
    /// caller may iterate without holding the lock.
    /// </summary>
    public IReadOnlyList<ActorId> GetRegisteredWalkers()
    {
        lock (_gate)
        {
            return _walkers.Keys.ToArray();
        }
    }

    // ── Per-tick update ────────────────────────────────────────────────

    /// <summary>
    /// Runs one tick of the per-walker state machine. Mirrors C++
    /// <c>WalkerManager::Update</c> (WalkerManager.cpp:61-111).
    /// </summary>
    public bool Update(double deltaSeconds)
    {
        lock (_gate)
        {
            // Snapshot the key list — the state machine may insert/remove
            // walkers (via SetWalkerRoute → SetWalkerNextPoint → SetWalkerRoute
            // recursion at end-of-route) while iterating.
            ActorId[] keys = ArrayPool();
            int count = 0;
            foreach (var k in _walkers.Keys)
                keys[count++] = k;

            for (int i = 0; i < count; i++)
            {
                ActorId id = keys[i];
                if (!_walkers.TryGetValue(id, out var info))
                    continue;

                if (info.ExternallyStopped)
                    continue;

                switch (info.State)
                {
                    case WalkerState.Idle:
                        // No-op until SetWalkerRoute is called.
                        break;

                    case WalkerState.Walking:
                        {
                            if (info.CurrentIndex < 0 || info.CurrentIndex >= info.Route.Count)
                                break;
                            Location target = info.Route[info.CurrentIndex].Location;
                            var (current, _) = _navigation.GetAgentState(info.AgentIndex);
                            // Upstream computes squared distance in a swapped
                            // basis (x, z, y); the swap is harmless for a
                            // squared-distance threshold so we use the
                            // straightforward Euclidean version here.
                            float dx = target.X - current.X;
                            float dy = target.Y - current.Y;
                            float dz = target.Z - current.Z;
                            float distSq = dx * dx + dy * dy + dz * dz;
                            if (distSq <= 1.0f)
                                info.State = WalkerState.InEvent;
                        }
                        break;

                    case WalkerState.InEvent:
                        switch (ExecuteEvent(id, info, deltaSeconds))
                        {
                            case EventResult.Continue:
                                break;
                            case EventResult.End:
                                SetWalkerNextPointLocked(id);
                                break;
                            case EventResult.TimeOut:
                                SetWalkerRouteLocked(id);
                                break;
                        }
                        break;

                    case WalkerState.Stop:
                        info.State = WalkerState.Idle;
                        break;
                }
            }

            return true;
        }
    }

    // Temporary scratch buffer for Update's key snapshot. Re-allocated when
    // the walker count grows past capacity; reused across ticks otherwise.
    private ActorId[] _updateScratch = Array.Empty<ActorId>();
    private ActorId[] ArrayPool()
    {
        if (_updateScratch.Length < _walkers.Count)
            _updateScratch = new ActorId[Math.Max(16, _walkers.Count * 2)];
        return _updateScratch;
    }

    // ── Route construction ─────────────────────────────────────────────

    /// <summary>
    /// Picks a new random destination and routes the walker there. Mirrors
    /// C++ <c>SetWalkerRoute(ActorId)</c> (WalkerManager.cpp:114-125).
    /// </summary>
    public bool SetWalkerRoute(ActorId id)
    {
        lock (_gate) return SetWalkerRouteLocked(id);
    }

    private bool SetWalkerRouteLocked(ActorId id)
    {
        Location? randomLoc = _navigation.GetRandomReachableLocation();
        if (randomLoc is null)
            return false;
        return SetWalkerRouteLocked(id, randomLoc.Value);
    }

    /// <summary>
    /// Routes the walker to the supplied destination. Mirrors C++
    /// <c>SetWalkerRoute(ActorId, Location)</c> (WalkerManager.cpp:128-181).
    /// </summary>
    public bool SetWalkerRoute(ActorId id, Location destination)
    {
        lock (_gate) return SetWalkerRouteLocked(id, destination);
    }

    private bool SetWalkerRouteLocked(ActorId id, Location destination)
    {
        if (!_walkers.TryGetValue(id, out var info))
            return false;

        // Pull the current position to use as "from" — agrees with upstream
        // (Navigation.cpp GetWalkerPosition vs WalkerManager.cpp:144).
        if (info.AgentIndex >= 0)
        {
            var (current, _) = _navigation.GetAgentState(info.AgentIndex);
            info.From = current;
        }
        info.To = destination;
        info.CurrentIndex = 0;
        info.State = WalkerState.Idle;

        // Ask the navigation facade for the per-agent route (returns the
        // path plus per-waypoint area-type byte). Mirrors C++
        // Navigation::GetAgentRoute.
        IReadOnlyList<Location> path = _navigation.FindAgentRoute(
            info.AgentIndex, info.From, destination, out byte[] areas);

        info.Route.Clear();
        if (path.Count == 0)
        {
            // No path — terminate cleanly and mark for replan on next tick.
            info.State = WalkerState.Stop;
            if (info.AgentIndex >= 0)
                _navigation.PauseWalker(info.AgentIndex, true);
            return false;
        }

        info.Route.Capacity = Math.Max(info.Route.Capacity, path.Count);

        // Mirrors C++ WalkerManager.cpp:155-176 — emit:
        //   * WalkerEventIgnore for sidewalk / grass / unknown
        //   * WalkerEventStopAndCheck on first entry into road/crosswalk
        //     from a safe area (sidewalk/grass)
        //   * (skip the point entirely if we're already in road/crosswalk)
        byte previousArea = (byte)NavAreas.Sidewalk;
        for (int i = 0; i < path.Count; i++)
        {
            byte area = i < areas.Length ? areas[i] : (byte)NavAreas.Sidewalk;
            switch (area)
            {
                case (byte)NavAreas.Sidewalk:
                    info.Route.Add(new WalkerRoutePoint(
                        WalkerEventIgnore.Instance, path[i], area));
                    break;

                case (byte)NavAreas.Road:
                case (byte)NavAreas.Crosswalk:
                    if (previousArea != (byte)NavAreas.Crosswalk
                        && previousArea != (byte)NavAreas.Road)
                    {
                        info.Route.Add(new WalkerRoutePoint(
                            CreateDefaultStopAndCheck(), path[i], area));
                    }
                    // else: skip — already in road/crosswalk, no need for
                    // a second StopAndCheck (matches upstream behaviour).
                    break;

                default:
                    info.Route.Add(new WalkerRoutePoint(
                        WalkerEventIgnore.Instance, path[i], area));
                    break;
            }
            previousArea = area;
        }

        if (info.Route.Count == 0)
        {
            // Whole route was filtered out (all-road-skip). Mark stop and
            // let the next tick try again with a different destination.
            info.State = WalkerState.Stop;
            if (info.AgentIndex >= 0)
                _navigation.PauseWalker(info.AgentIndex, true);
            return false;
        }

        // Mirrors upstream's call at WalkerManager.cpp:179 — immediately
        // advance to route point 1 (skip the start point since we're
        // already there).
        SetWalkerNextPointLocked(id);
        return true;
    }

    /// <summary>
    /// Advances the walker to the next route point. Mirrors C++
    /// <c>SetWalkerNextPoint</c> (WalkerManager.cpp:184-216).
    /// </summary>
    public bool SetWalkerNextPoint(ActorId id)
    {
        lock (_gate) return SetWalkerNextPointLocked(id);
    }

    private bool SetWalkerNextPointLocked(ActorId id)
    {
        if (!_walkers.TryGetValue(id, out var info))
            return false;

        info.CurrentIndex++;

        if (info.CurrentIndex < info.Route.Count)
        {
            info.State = WalkerState.Walking;
            _navigation.PauseWalker(info.AgentIndex, false);
            _navigation.RequestMoveTarget(info.AgentIndex, info.Route[info.CurrentIndex].Location);
            return true;
        }

        // End of route — stop, pause, and request a fresh random route.
        info.State = WalkerState.Stop;
        _navigation.PauseWalker(info.AgentIndex, true);
        SetWalkerRouteLocked(id);
        return true;
    }

    /// <summary>
    /// Returns the next route point, or <c>null</c> if the route is
    /// exhausted. Mirrors C++ <c>GetWalkerNextPoint</c>.
    /// </summary>
    public Location? GetWalkerNextPoint(ActorId id)
    {
        lock (_gate)
        {
            if (!_walkers.TryGetValue(id, out var info))
                return null;
            if (info.CurrentIndex < 0 || info.CurrentIndex >= info.Route.Count)
                return null;
            return info.Route[info.CurrentIndex].Location;
        }
    }

    /// <summary>
    /// Returns the location where the current crosswalk ends, or
    /// <c>null</c> if the walker is not currently traversing a crosswalk.
    /// Mirrors C++ <c>GetWalkerCrosswalkEnd</c> (WalkerManager.cpp:241-265).
    /// </summary>
    public Location? GetWalkerCrosswalkEnd(ActorId id)
    {
        lock (_gate)
        {
            if (!_walkers.TryGetValue(id, out var info))
                return null;

            for (int pos = info.CurrentIndex; pos < info.Route.Count; pos++)
            {
                if (info.Route[pos].AreaType != (byte)NavAreas.Crosswalk)
                    return info.Route[pos].Location;
            }
            return null;
        }
    }

    /// <summary>
    /// Sets the walker's max speed. Delegates to the navigation facade
    /// (the value is also cached on the <see cref="WalkerInfo"/> so we can
    /// replay it after a re-add).
    /// </summary>
    public bool SetMaxSpeed(ActorId id, float metersPerSecond)
    {
        lock (_gate)
        {
            if (!_walkers.TryGetValue(id, out var info))
                return false;
            info.MaxSpeed = metersPerSecond;
            if (info.AgentIndex >= 0)
                return _navigation.SetWalkerMaxSpeed(info.AgentIndex, metersPerSecond);
            return true;
        }
    }

    /// <summary>
    /// Flips the "user has asked this walker to stop" flag. While set, the
    /// per-tick state machine skips the walker (no movement updates) and
    /// downstream <see cref="WalkerNavigation"/> consumers will not emit
    /// position commands for it. Cleared by a subsequent
    /// <see cref="WalkerNavigation.Start"/>.
    /// </summary>
    public void SetExternallyStopped(ActorId id, bool stopped)
    {
        lock (_gate)
        {
            if (_walkers.TryGetValue(id, out var info))
            {
                info.ExternallyStopped = stopped;
                if (info.AgentIndex >= 0)
                    _navigation.PauseWalker(info.AgentIndex, stopped);
            }
        }
    }

    /// <summary>
    /// Returns the crowd-agent index for a walker, or <c>-1</c> if
    /// unregistered. Exposed so <see cref="WalkerNavigation"/> can ask
    /// Navigation for transform/velocity without re-locking this dict.
    /// </summary>
    public int GetAgentIndex(ActorId id)
    {
        lock (_gate)
        {
            return _walkers.TryGetValue(id, out var info) ? info.AgentIndex : -1;
        }
    }

    /// <summary>
    /// Returns whether the walker is currently flagged externally stopped
    /// (i.e. the user called <c>WalkerNavigation.Stop</c>).
    /// </summary>
    public bool IsExternallyStopped(ActorId id)
    {
        lock (_gate)
        {
            return _walkers.TryGetValue(id, out var info) && info.ExternallyStopped;
        }
    }

    /// <summary>
    /// Returns the WalkerInfo snapshot (state + agent index) for
    /// inspection by the per-tick driver. Returns <c>null</c> if the id
    /// is not registered.
    /// </summary>
    public (int AgentIndex, WalkerState State, bool Stopped)? GetWalkerInfo(ActorId id)
    {
        lock (_gate)
        {
            if (!_walkers.TryGetValue(id, out var info))
                return null;
            return (info.AgentIndex, info.State, info.ExternallyStopped);
        }
    }

    // ── Event dispatch ─────────────────────────────────────────────────

    /// <summary>
    /// Dispatches the route point's event. Replaces the C++ visitor object
    /// with a C# switch expression / switch statement. Mirrors C++
    /// <c>ExecuteEvent</c> (WalkerManager.cpp:267-275) and the three
    /// visitor overloads in WalkerEvent.cpp.
    /// </summary>
    private EventResult ExecuteEvent(ActorId id, WalkerInfo info, double delta)
    {
        if (info.CurrentIndex < 0 || info.CurrentIndex >= info.Route.Count)
            return EventResult.End;

        var rp = info.Route[info.CurrentIndex];
        switch (rp.Event)
        {
            case WalkerEventIgnore:
                // WalkerEvent.cpp:15-17 — End immediately.
                return EventResult.End;

            case WalkerEventWait wait:
                {
                    // WalkerEvent.cpp:19-26 — countdown + Continue/End.
                    double rem = wait.TimeRemaining - delta;
                    rp.Event = wait with { TimeRemaining = rem };
                    info.Route[info.CurrentIndex] = rp;
                    return rem <= 0.0 ? EventResult.End : EventResult.Continue;
                }

            case WalkerEventStopAndCheck stop:
                return HandleStopAndCheck(id, info, ref rp, stop, delta);

            default:
                // Should be unreachable — the abstract record is internal
                // and sealed-by-convention. Treat unknown events as
                // "advance" so a future addition does not stall walkers.
                return EventResult.End;
        }
    }

    private EventResult HandleStopAndCheck(
        ActorId id,
        WalkerInfo info,
        ref WalkerRoutePoint rp,
        WalkerEventStopAndCheck ev,
        double delta)
    {
        // WalkerEvent.cpp:28-64 — port of WalkerEventVisitor::operator()
        // on StopAndCheck.

        double rem = ev.TimeRemaining - delta;
        if (rem <= 0.0)
        {
            // Persist the cleared timer back into the slot before bailing
            // so a re-entry sees the same expired event (defensive — the
            // state machine routes to SetWalkerRoute on TimeOut so this
            // event slot is about to be wiped anyway).
            rp.Event = ev with { TimeRemaining = rem };
            info.Route[info.CurrentIndex] = rp;
            return EventResult.TimeOut;
        }

        // First tick of the event: pause the agent and lock in the
        // affecting traffic light (NOT YET IMPLEMENTED — see notes).
        if (info.AgentIndex >= 0)
            _navigation.PauseWalker(info.AgentIndex, true);

        var (currentPos, _) = info.AgentIndex >= 0
            ? _navigation.GetAgentState(info.AgentIndex)
            : (info.From, default(Vector3D));

        ActorId? tlActor = ev.TrafficLightActor;
        bool checkTl = ev.CheckForTrafficLight;
        if (checkTl)
        {
            // Upstream calls WalkerManager::GetTrafficLightAffecting here,
            // which scans every cached traffic-light stop-waypoint and
            // returns the nearest one. CarlaNet's traffic-light state is
            // tracked in TrafficManager / world observer, not in this
            // module. Leaving the lookup to the integrator: until then
            // we treat "no TL nearby" as the result (tlActor remains null)
            // and fall through to the vehicle-near check, which is the
            // safe default behaviour (walker still waits for clear road).
            tlActor = null;
            checkTl = false;
        }

        // Wait while the TL is green/yellow (per upstream: the walker
        // crosses on red, since green/yellow mean cars have right of way).
        // Without TL lookup wired up here, tlActor is always null and we
        // fall through to the vehicle-near check below.

        if (info.AgentIndex >= 0)
            _navigation.PauseWalker(info.AgentIndex, false);

        // Crosswalk-axis vehicle check. Compute the direction to the
        // crosswalk end and ask Navigation whether any tracked vehicle
        // OBB is within the radius along that axis. Mirrors
        // WalkerEvent.cpp:50-62.
        var crosswalkEnd = GetWalkerCrosswalkEndLocked(id) ?? currentPos;
        var direction = new Vector3D(
            crosswalkEnd.X - currentPos.X,
            crosswalkEnd.Y - currentPos.Y,
            crosswalkEnd.Z - currentPos.Z);

        bool vehicleNear = info.AgentIndex >= 0
            && _navigation.HasVehicleNear(info.AgentIndex, VehicleNearRadius, direction);

        // Persist the (possibly cleared) check-tl flag + decremented timer.
        rp.Event = new WalkerEventStopAndCheck(rem, checkTl, tlActor);
        info.Route[info.CurrentIndex] = rp;

        return vehicleNear ? EventResult.Continue : EventResult.End;
    }

    /// <summary>Lock-free helper used inside <see cref="HandleStopAndCheck"/>.</summary>
    private Location? GetWalkerCrosswalkEndLocked(ActorId id)
    {
        if (!_walkers.TryGetValue(id, out var info))
            return null;
        for (int pos = info.CurrentIndex; pos < info.Route.Count; pos++)
        {
            if (info.Route[pos].AreaType != (byte)NavAreas.Crosswalk)
                return info.Route[pos].Location;
        }
        return null;
    }

    /// <summary>
    /// Convenience helper: build the canonical 60-second
    /// <see cref="WalkerEventStopAndCheck"/> event the route-construction
    /// code uses. Kept here so the constant lives in one place.
    /// </summary>
    internal static WalkerEventStopAndCheck CreateDefaultStopAndCheck()
        => new(StopAndCheckTimeoutSeconds);

    /// <summary>
    /// Resets the global "have we cached traffic-light waypoints yet?"
    /// flag (mirrors C++ static bool in GetAllTrafficLightWaypoints). The
    /// integrator can call this on episode reload so the next AddWalker
    /// re-scans the new map's traffic lights once.
    /// </summary>
    public void InvalidateTrafficLightCache()
    {
        // Currently a no-op — the actual traffic-light cache lives outside
        // this module (CarlaNet.TrafficManager owns the TL state). When
        // the integrator wires that in, store the cached actor-id list
        // here and clear it from this method.
    }
}
