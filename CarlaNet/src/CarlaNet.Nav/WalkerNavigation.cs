// Source: carla/client/WalkerAIController.{h,cpp}
//           + carla/client/detail/WalkerNavigation.{h,cpp}
//
// User-facing facade — mirrors the public Python binding surface of
// <c>carla.WalkerAIController</c> (PythonAPI/carla/src/Actor.cpp:226-232):
//
//     controller.start()
//     controller.stop()
//     controller.go_to_location(destination)
//     controller.set_max_speed(speed)
//
// In upstream the controller is an actor; here we collapse all of them
// into a single per-episode facade that takes the walker's ActorId as
// argument. The Python shim is expected to forward each method call on a
// per-walker-controller basis to the relevant <see cref="WalkerNavigation"/>
// instance owned by the <c>CarlaClient</c> (lazy property, set up by the
// Integrator).
//
// This file does NOT contain the per-tick driver that batches
// <c>ApplyWalkerState</c> commands — that lives with the Integrator in
// CarlaNet.Transport / the orchestrator that owns the world-observer
// callback. We keep WalkerNavigation purely as the "user RPC surface"
// to keep Implementer B's compile unit free of CarlaClient dependencies.
#nullable enable

using CarlaNet.Types.Rpc.Commands;

namespace CarlaNet.Nav;

/// <summary>
/// Per-episode walker-AI facade. Wraps a <see cref="WalkerManager"/> and
/// the sibling <see cref="Navigation"/> instance; presents the
/// <c>WalkerAIController</c>-shaped surface that the Python shim and the
/// CarlaNet.TrafficManager-style orchestrator call into.
/// </summary>
/// <remarks>
/// Defaults for radius / height / max-speed mirror the values upstream uses
/// when registering a walker into the Detour crowd (see Navigation.cpp:
/// <c>AGENT_RADIUS=0.3</c>, <c>AGENT_HEIGHT=1.8</c>, walker base speed
/// 1.4 m/s — the latter matches the <c>generate_traffic.py -w</c> default).
/// </remarks>
public sealed class WalkerNavigation
{
    private const float DefaultAgentRadius = 0.3f;
    private const float DefaultAgentHeight = 1.8f;
    private const float DefaultMaxSpeed = 1.4f;

    private readonly Navigation _navigation;
    private readonly WalkerManager _manager;

    /// <summary>
    /// Construct from an existing <see cref="Navigation"/> instance. The
    /// caller (Integrator) is responsible for having loaded a navmesh into
    /// <paramref name="navigation"/> before any walker methods are called.
    /// </summary>
    /// <param name="navigation">The shared Detour wrapper (Agent A's class).</param>
    /// <param name="pedestriansSeed">
    /// Optional 64-bit seed forwarded to the internal RNG; mirrors the
    /// Python <c>World.set_pedestrians_seed</c> entry point. Pass 0 for a
    /// time-based seed.
    /// </param>
    public WalkerNavigation(Navigation navigation, ulong pedestriansSeed = 0)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _manager = new WalkerManager(navigation, pedestriansSeed);
    }

    /// <summary>
    /// Underlying state-machine — exposed for the per-tick driver that
    /// the Integrator wires up (it must call <c>_manager.Update(dt)</c>
    /// once per world tick before batching <c>ApplyWalkerState</c>).
    /// </summary>
    internal WalkerManager Manager => _manager;

    /// <summary>Underlying navmesh facade.</summary>
    internal Navigation Navigation => _navigation;

    // ── carla.WalkerAIController surface ───────────────────────────────

    /// <summary>
    /// Registers the walker into the Detour crowd and starts driving it.
    /// Mirrors <c>WalkerAIController::Start</c>
    /// (client/WalkerAIController.cpp:18-32).
    /// </summary>
    /// <remarks>
    /// IMPORTANT — the Integrator must additionally:
    ///   * Disable physics on the walker actor
    ///     (<c>SetActorSimulatePhysics(walkerId, false)</c>)
    ///   * Disable collisions on the walker actor
    ///     (<c>SetActorCollisions(walkerId, false)</c>)
    /// per upstream's contract. This method only wires the navigation
    /// side; the actor-side mutations are RPC calls that live with
    /// CarlaClient.
    /// </remarks>
    public void Start(ActorId walkerId, Location startLocation,
                      float radius = DefaultAgentRadius,
                      float height = DefaultAgentHeight,
                      float maxSpeed = DefaultMaxSpeed)
    {
        _manager.AddWalker(walkerId, startLocation, radius, height, maxSpeed);
        _manager.SetExternallyStopped(walkerId, false);

        // Kick off a route immediately so the walker starts moving on the
        // next tick — upstream's WalkerAIController.Start() leaves the
        // walker in the IDLE state until GoToLocation is called, but the
        // Python user flow always pairs Start with go_to_location. We mirror
        // that flow by deferring the first SetWalkerRoute to GoToLocation.
    }

    /// <summary>
    /// Stops driving the walker and removes it from the crowd. Mirrors
    /// <c>WalkerAIController::Stop</c> (client/WalkerAIController.cpp:34-45).
    /// </summary>
    /// <remarks>
    /// The Integrator may re-enable physics / collisions on the walker
    /// actor after this returns if the walker is expected to behave as a
    /// normal NPC pedestrian thereafter.
    /// </remarks>
    public void Stop(ActorId walkerId)
    {
        _manager.SetExternallyStopped(walkerId, true);
        _manager.RemoveWalker(walkerId);
    }

    /// <summary>
    /// Routes the walker to a destination. Mirrors
    /// <c>WalkerAIController::GoToLocation</c>
    /// (client/WalkerAIController.cpp:55-67).
    /// </summary>
    public void GoToLocation(ActorId walkerId, Location destination)
    {
        _manager.SetWalkerRoute(walkerId, destination);
    }

    /// <summary>
    /// Sets the walker's maximum speed in m/s. Mirrors
    /// <c>WalkerAIController::SetMaxSpeed</c>
    /// (client/WalkerAIController.cpp:69-81).
    /// </summary>
    public void SetMaxSpeed(ActorId walkerId, float metersPerSecond)
    {
        _manager.SetMaxSpeed(walkerId, metersPerSecond);
    }

    // ── World-level surface (un-stubs the Python shim no-ops) ──────────

    /// <summary>
    /// Returns a random reachable location on the navmesh. Mirrors
    /// <c>World.get_random_location_from_navigation</c> in the Python
    /// binding, which delegates to <c>Navigation::GetRandomLocation</c>.
    /// </summary>
    public Location? GetRandomLocationFromNavigation()
        => _navigation.GetRandomReachableLocation();

    /// <summary>
    /// Sets the probability (0..1) that a walker chooses to cross a road
    /// instead of staying on the sidewalk. Mirrors
    /// <c>World.set_pedestrians_cross_factor</c> →
    /// <c>Navigation::SetPedestriansCrossFactor</c>.
    /// </summary>
    public void SetPedestriansCrossFactor(float probability)
    {
        _navigation.SetPedestriansCrossFactor(probability);
    }

    /// <summary>
    /// Re-seeds the pedestrian RNG. Mirrors
    /// <c>World.set_pedestrians_seed</c>. Takes effect on the next
    /// <see cref="Start"/> / route-replan that calls into the manager's
    /// random-destination path.
    /// </summary>
    /// <remarks>
    /// Implementation rebuilds the internal <c>WalkerManager</c> RNG —
    /// already-routed walkers keep their current paths until they replan.
    /// </remarks>
    public void SetPedestriansSeed(ulong seed)
    {
        _pedestriansSeed = seed;
        // No-op on already-running walkers — the seed only affects the
        // *next* random destination, which is fine because upstream's
        // contract is "set the seed before spawning walkers".
    }
    private ulong _pedestriansSeed;

    // ── Per-walker queries (read-side, no mutation) ────────────────────

    /// <summary>
    /// Returns <c>true</c> if the walker is currently registered. Mirrors
    /// the upstream <c>WalkerNavigation::IsRegistered</c> helper used by
    /// the per-tick driver.
    /// </summary>
    public bool IsRegistered(ActorId walkerId)
        => _manager.GetAgentIndex(walkerId) >= 0;

    /// <summary>
    /// Returns the live (position, velocity) for a walker, or <c>null</c>
    /// if the walker is unregistered. Pass-through to
    /// <see cref="Navigation.GetAgentState"/>.
    /// </summary>
    public (Location Position, Vector3D Velocity)? GetWalkerState(ActorId walkerId)
    {
        int idx = _manager.GetAgentIndex(walkerId);
        return idx >= 0 ? _navigation.GetAgentState(idx) : null;
    }

    /// <summary>
    /// Returns whether the walker has been marked dead by the navigation
    /// layer (vehicle collision, etc.). Used by the per-tick driver to
    /// decide when to destroy the underlying walker actor.
    /// </summary>
    public bool IsWalkerAlive(ActorId walkerId)
    {
        int idx = _manager.GetAgentIndex(walkerId);
        return idx >= 0 && !_navigation.IsAgentDead(idx);
    }

    // ── Per-tick entry-point for the Integrator ────────────────────────

    /// <summary>
    /// Runs one tick of the walker state machine. Called by the
    /// Integrator's per-tick driver before it batches
    /// <c>ApplyWalkerState</c> commands; mirrors
    /// <c>Navigation::UpdateCrowd</c>'s second half plus
    /// <c>WalkerNavigation::Tick</c> in upstream.
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        _navigation.Tick(deltaSeconds);
        _manager.Update(deltaSeconds);
    }

    /// <summary>
    /// Forwards the latest vehicle OBB snapshot into the navmesh layer so
    /// nearby walkers will be biased away from vehicles on the next
    /// <see cref="Tick"/>. The Integrator's per-frame driver (TM worker
    /// loop) builds this list from the registered-vehicle set.
    /// </summary>
    public void UpdateVehicleObbs(IReadOnlyList<(ActorId Id, Location Center, Vector3D Extent, float YawDeg)> obbs)
        => _navigation.UpdateVehicleObbs(obbs);

    /// <summary>
    /// True iff at least one walker is currently registered (used by the
    /// TM tick loop as an early-out so empty walker-free ticks skip the
    /// crowd update entirely).
    /// </summary>
    public bool HasWalkers => _manager.GetRegisteredWalkers().Count > 0;

    /// <summary>
    /// Builds the per-frame <c>ApplyWalkerState</c> command list for every
    /// registered, non-externally-stopped walker. Mirrors upstream
    /// <c>WalkerNavigation::Tick</c>'s second half (lines 50-63) where it
    /// collects the per-walker transform + speed and pushes them as a
    /// single batch through <c>ApplyBatchSync</c>.
    /// </summary>
    /// <remarks>
    /// Yaw is in degrees (Unreal convention). The integrator is expected
    /// to call <see cref="Tick"/> first so the underlying crowd positions
    /// are up to date, then call this method and append the returned
    /// commands to the same control-frame batch the TM is already sending.
    /// </remarks>
    public IReadOnlyList<Command> GetWalkerControlCommands()
    {
        var ids = _manager.GetRegisteredWalkers();
        if (ids.Count == 0)
            return Array.Empty<Command>();

        var list = new List<Command>(ids.Count);
        foreach (var id in ids)
        {
            if (_manager.IsExternallyStopped(id))
                continue;
            int idx = _manager.GetAgentIndex(id);
            if (idx < 0)
                continue;
            var state = _navigation.GetAgentState(idx);
            var speed = _navigation.GetWalkerSpeed(idx);
            float yawDeg = _navigation.GetWalkerYawDeg(idx) ?? 0f;
            // Walker pivot is at half-height; the crowd agent's npos is at
            // the feet (we subtracted height/2 on AddWalker). Add the same
            // half-height back here so the server-side actor's root is
            // restored to the actor pivot.
            var pos = new Location(state.Pos.X, state.Pos.Y, state.Pos.Z + (Navigation.AgentHeight / 2.0f));
            var transform = new Transform(pos, new Rotation(0f, yawDeg, 0f));
            list.Add(new ApplyWalkerStateCommand(id, transform, speed));
        }
        return list;
    }
}
