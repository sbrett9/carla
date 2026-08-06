// Source: carla/trafficmanager/TrafficManagerLocal.{h,cpp}
//
// The orchestrator. Owns the 7 stage instances and a single worker thread
// that drives them sequentially each tick:
//   ALSM.Update()                          (once)
//   for each veh: LocalizationStage.Update
//   for each veh: CollisionStage.Update
//   CollisionStage.ClearCycleCache()
//   VehicleLightStage.UpdateWorldInfo()
//   for each veh: TrafficLightStage.Update
//                 MotionPlanStage.Update
//                 VehicleLightStage.Update
//   ApplyBatchSync(commands)
//
// Implements ITrafficManagerCallback — every setter delegates to Parameters,
// the registration methods mutate AtomicActorSet, and the lifecycle methods
// control the worker thread.
//
// Threading: SINGLE dedicated background thread runs the pipeline.
// Parameters / AtomicActorSet are thread-safe so RPC-server threads can
// concurrently set knobs. A `_registrationGate` lock prevents the worker
// from racing with vehicle (un)registration during the tick.
#nullable enable

using CarlaNet.Map.Road;
using CarlaNet.Nav;
using CarlaNet.TrafficManager.Stages;
using CarlaNet.Transport;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using Microsoft.Extensions.Logging;

namespace CarlaNet.TrafficManager;

/// <summary>
/// The in-process traffic-manager orchestrator. Mirrors
/// <c>carla::traffic_manager::TrafficManagerLocal</c>. Implementations of
/// the <see cref="ITrafficManagerCallback"/> interface delegate to either
/// <see cref="Parameters"/>, the <see cref="AtomicActorSet"/>, or the
/// worker-thread control variables.
///
/// Internal: the user-facing entry point is <see cref="TrafficManager"/>.
/// </summary>
internal sealed class TrafficManagerLocal : ITrafficManagerCallback, IAsyncDisposable
{
    private readonly CarlaClient _client;
    private readonly InMemoryMap _localMap;
    private readonly ILogger? _logger;

    // ── Shared per-pipeline state (held by all stages) ──────────────────
    private readonly Parameters _parameters = new();
    private readonly AtomicActorSet _registeredVehicles = new();
    private readonly SimulationState _simulationState = new();
    private readonly TrackTraffic _trackTraffic = new();
    private readonly BufferMap _bufferMap = new();
    private readonly List<ActorId> _markedForRemoval = new();
    private readonly List<Command> _controlFrame = new(capacity: 128);
    private readonly RoutePlanner _routePlanner;
    private readonly RouteSupervisor _routeSupervisor;

    // ── Stages (constructed in the ctor) ─────────────────────────────────
    private readonly LocalizationStage _localizationStage;
    private readonly CollisionStage _collisionStage;
    private readonly TrafficLightStage _trafficLightStage;
    private readonly MotionPlanStage _motionPlanStage;
    private readonly VehicleLightStage _vehicleLightStage;
    private readonly ALSM _alsm;

    // ── Worker thread + sync-mode triggers ───────────────────────────────
    private Thread? _workerThread;
    private readonly ManualResetEventSlim _stepBegin = new(initialState: false);
    private readonly ManualResetEventSlim _stepEnd = new(initialState: false);
    private readonly object _registrationGate = new();
    private volatile bool _running;
    private ulong _seed;
    private RandomGenerator _randomDevice;
    private int _registeredVehiclesState = -1;
    private long _previousUpdateInstanceTicks;

    /// <summary>The TM's RPC server, started by <see cref="Start"/>.</summary>
    private TrafficManagerServer? _server;
    private int _port;

    /// <summary>The port the underlying RPC server is bound to (only valid after Start()).</summary>
    public int Port => _server?.Port ?? _port;

    /// <summary>Direct accessor for the underlying parameter store (for the facade's getters).</summary>
    internal Parameters Parameters => _parameters;

    /// <summary>Shortest-path search over the road graph, for the facade's route-planning surface.</summary>
    internal RoutePlanner RoutePlanner => _routePlanner;

    /// <summary>The dense road graph, for facade queries about a place on the map.</summary>
    internal InMemoryMap LocalMap => _localMap;

    /// <summary>Route bookkeeping and recovery, for the facade's route-assignment surface.</summary>
    internal RouteSupervisor RouteSupervisor => _routeSupervisor;

    /// <summary>True after <see cref="Start"/> succeeded and the worker is running.</summary>
    public bool IsRunning => _running;

    public TrafficManagerLocal(
        CarlaClient client,
        InMemoryMap localMap,
        int port = 8000,
        float globalPercentageSpeedDifference = 0.0f,
        ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _localMap = localMap ?? throw new ArgumentNullException(nameof(localMap));
        _logger = logger;
        _port = port;
        _seed = unchecked((ulong)Environment.TickCount64);
        _randomDevice = new RandomGenerator(_seed);

        _routePlanner = new RoutePlanner(_localMap);
        _routeSupervisor = new RouteSupervisor(_routePlanner, _parameters);

        // Construct stages. Order matters only for the cross-references
        // (MotionPlanStage reads the other stages' output dictionaries).
        _localizationStage = new LocalizationStage(
            simulationState: _simulationState,
            bufferMap: _bufferMap,
            trackTraffic: _trackTraffic,
            parameters: _parameters,
            localMap: _localMap,
            rng: _randomDevice,
            markedForRemoval: _markedForRemoval,
            routeSupervisor: _routeSupervisor);

        _collisionStage = new CollisionStage(
            simulationState: _simulationState,
            bufferMap: _bufferMap,
            trackTraffic: _trackTraffic,
            parameters: _parameters,
            random: _randomDevice);

        _trafficLightStage = new TrafficLightStage(
            simulationState: _simulationState,
            bufferMap: _bufferMap,
            parameters: _parameters,
            random: _randomDevice,
            client: _client);

        _motionPlanStage = new MotionPlanStage(
            simulationState: _simulationState,
            bufferMap: _bufferMap,
            trackTraffic: _trackTraffic,
            parameters: _parameters,
            localMap: _localMap,
            rng: _randomDevice,
            collisionHazards: _collisionStage.GetOutput(),
            trafficLightFrames: _trafficLightStage.GetOutput(),
            localizationOutput: _localizationStage.GetOutput());

        _vehicleLightStage = new VehicleLightStage(
            vehicleIdList: new List<ActorId>(),
            bufferMap: _bufferMap,
            parameters: _parameters,
            client: _client,
            controlFrame: _controlFrame);

        _alsm = new ALSM(
            registeredVehicles: _registeredVehicles,
            bufferMap: _bufferMap,
            trackTraffic: _trackTraffic,
            markedForRemoval: _markedForRemoval,
            parameters: _parameters,
            client: _client,
            localMap: _localMap,
            simulationState: _simulationState,
            localizationStage: _localizationStage,
            collisionStage: _collisionStage,
            trafficLightStage: _trafficLightStage,
            motionPlanStage: _motionPlanStage,
            vehicleLightStage: _vehicleLightStage);

        _parameters.SetGlobalPercentageSpeedDifference(globalPercentageSpeedDifference);
    }

    // ─────────────────────────────────────────────────────────────────
    //                            Lifecycle
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the RPC server (with port-walking 8000..8010) and spins the
    /// background worker thread. Returns once both are up.
    /// </summary>
    public void Start()
    {
        if (_running) return;

        // Walk ports 8000..port+10 looking for the first free one — upstream
        // does the same in TrafficManager.cpp's CreateTrafficManagerServer.
        const int maxAttempts = 10;
        Exception? lastError = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int candidatePort = _port + attempt;
            try
            {
                var server = new TrafficManagerServer(this, candidatePort, _logger);
                // Block until listening so subsequent Port reads are valid.
                server.StartAsync().GetAwaiter().GetResult();
                _server = server;
                _port = server.Port;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger?.LogDebug(ex, "Port {Port} unavailable for TM RPC server, walking", candidatePort);
            }
        }
        if (_server is null)
        {
            throw new InvalidOperationException(
                $"Could not bind TrafficManagerServer on ports {_port}..{_port + maxAttempts - 1}",
                lastError);
        }

        _running = true;
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "CarlaNet.TM.Worker",
        };
        _workerThread.Start();
        TMDiagnostics.Log($"[TM] Start: worker thread started, RPC server on port {_port}");
    }

    /// <summary>
    /// Stops the worker thread, releases per-tick caches, then stops the RPC
    /// server. Safe to call repeatedly.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        // Unblock the worker if it's in the sync-mode wait.
        _stepBegin.Set();

        try { _workerThread?.Join(timeout: TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Worker join failed"); }
        _workerThread = null;

        lock (_registrationGate)
        {
            _registeredVehicles.Clear();
            _registeredVehiclesState = -1;
            _trackTraffic.Clear();
            _simulationState.Reset();
            _localizationStage.Reset();
            _collisionStage.Reset();
            _trafficLightStage.Reset();
            _motionPlanStage.Reset();
            _bufferMap.Clear();
            _controlFrame.Clear();
        }

        if (_server is not null)
        {
            try { _server.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Server stop failed"); }
        }

        _stepBegin.Reset();
        _stepEnd.Reset();
        // After a stop we go back to a fresh runnable state.
        _running = false;
    }

    /// <summary>Stops the worker AND disposes the RPC server.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        _routeSupervisor.Dispose();
        if (_server is not null)
        {
            try { await _server.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Server dispose failed"); }
            _server = null;
        }
        _stepBegin.Dispose();
        _stepEnd.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────
    //                          Worker loop
    // ─────────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        _previousUpdateInstanceTicks = Environment.TickCount64;

        while (_running)
        {
            try
            {
                bool synchronousMode = _parameters.GetSynchronousMode();
                bool hybridPhysicsMode = _parameters.GetHybridPhysicsMode();

                // Sync-mode: wait for SynchronousTick to release us.
                if (synchronousMode)
                {
                    _stepBegin.Wait();
                    _stepBegin.Reset();
                    if (!_running) break;
                }

                // Async-mode hybrid: throttle to ~20 Hz (HYBRID_MODE_DT = 50 ms).
                if (!synchronousMode && hybridPhysicsMode)
                {
                    long nowTicks = Environment.TickCount64;
                    long elapsedMs = nowTicks - _previousUpdateInstanceTicks;
                    int targetMs = (int)(Constants.HybridMode.HYBRID_MODE_DT_FL * 1000f);
                    int sleepMs = targetMs - (int)elapsedMs;
                    if (sleepMs > 0)
                        Thread.Sleep(sleepMs);
                    _previousUpdateInstanceTicks = Environment.TickCount64;
                }
                else if (!synchronousMode)
                {
                    // Async non-hybrid: still throttle to ~33 ms to keep CPU sane.
                    Thread.Sleep(33);
                }

                RunOneTick();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Worker tick failed (continuing)");
            }
            finally
            {
                if (_parameters.GetSynchronousMode())
                {
                    _stepEnd.Set();
                }
            }
        }
    }

    /// <summary>
    /// Single tick of the pipeline. Wraps everything in the registration
    /// gate so RegisterVehicles / UnregisterVehicles can't race the frame.
    /// </summary>
    private long _tickCounter;
    private void RunOneTick()
    {
        lock (_registrationGate)
        {
            _tickCounter++;

            // ── 1. Update actor lifecycle + per-vehicle world state ──
            try { _alsm.Update(); }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "ALSM.Update failed");
                TMDiagnostics.LogFirstFailure("ALSM.Update", ex, _tickCounter);
            }

            // ── 2. Track changes in registered-vehicle set ────────────
            IReadOnlyList<ActorId> vehicleIds = _registeredVehicles.GetIDList();
            int currentState = _registeredVehicles.GetState();
            if (_registeredVehiclesState != currentState)
            {
                _registeredVehiclesState = currentState;
            }
            int numVehicles = vehicleIds.Count;
            _controlFrame.Clear();

            // Heartbeat: even with no vehicles, prove the worker thread is alive.
            if (TMDiagnostics.Enabled && numVehicles == 0 && (_tickCounter == 1 || _tickCounter % 60 == 0))
                TMDiagnostics.Log($"[TM tick {_tickCounter}] idle (registered=0)");

            // Early-out if no registered vehicles AND no registered walkers —
            // still send a no-op batch in sync mode so the simulator doesn't
            // wait on us. The walker subsystem is lazy: NavigationFactory.TryGet
            // returns false (without forcing a navmesh fetch) until the Python
            // shim calls WalkerAIController.start() for the first time.
            var walkerNav = TryGetWalkerNavigation();
            bool hasWalkers = walkerNav is { HasWalkers: true };
            if (numVehicles == 0 && !hasWalkers)
            {
                if (_parameters.GetSynchronousMode())
                {
                    try { _client.ApplyBatchSyncAsync(_controlFrame, doTickCue: false).GetAwaiter().GetResult(); }
                    catch (Exception ex) { _logger?.LogDebug(ex, "Empty batch failed (likely no actors)"); }
                }
                return;
            }

            // Walker-only fast path: skip the vehicle pipeline entirely when
            // no vehicles are registered but walkers are. The walker step
            // below is the only contributor to _controlFrame this tick.
            if (numVehicles == 0)
            {
                RunWalkerTick(walkerNav!, vehicleIds: Array.Empty<ActorId>());
                if (_controlFrame.Count > 0 || _parameters.GetSynchronousMode())
                {
                    try { _client.ApplyBatchSyncAsync(_controlFrame, doTickCue: false).GetAwaiter().GetResult(); }
                    catch (Exception ex) { _logger?.LogDebug(ex, "ApplyBatchSync (walker-only) failed"); }
                }
                return;
            }

            // Update simulator time on the time-sensitive stages.
            double elapsedSeconds = Environment.TickCount64 / 1000.0;
            _trafficLightStage.SetCurrentTimestamp(elapsedSeconds);
            _motionPlanStage.UpdateCurrentTimestamp(elapsedSeconds);

            // ── 3. LocalizationStage per vehicle ─────────────────────
            for (int i = 0; i < numVehicles; i++)
            {
                try { _localizationStage.Update(vehicleIds[i]); }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Localization failed");
                    TMDiagnostics.LogFirstFailure("Localization", ex, _tickCounter);
                }
            }

            // ── 4. CollisionStage per vehicle ────────────────────────
            for (int i = 0; i < numVehicles; i++)
            {
                try { _collisionStage.Update(vehicleIds[i]); }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Collision failed");
                    TMDiagnostics.LogFirstFailure("Collision", ex, _tickCounter);
                }
            }
            _collisionStage.ClearCycleCache();

            // ── 5. VehicleLightStage world refresh (once) ────────────
            try { _vehicleLightStage.UpdateWorldInfo(); }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "VehicleLightStage UpdateWorldInfo failed");
                TMDiagnostics.LogFirstFailure("VehicleLight.UpdateWorldInfo", ex, _tickCounter);
            }

            // ── 6. TrafficLight + MotionPlan + VehicleLight per veh ──
            for (int i = 0; i < numVehicles; i++)
            {
                ActorId id = vehicleIds[i];
                try { _trafficLightStage.Update(id); }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "TrafficLight failed");
                    TMDiagnostics.LogFirstFailure("TrafficLight", ex, _tickCounter);
                }
                try { _motionPlanStage.Update(id); }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "MotionPlan failed");
                    TMDiagnostics.LogFirstFailure("MotionPlan", ex, _tickCounter);
                }
                var mpOutputs = _motionPlanStage.GetOutput();
                if (mpOutputs.TryGetValue(id, out var cmd))
                    _controlFrame.Add(cmd);
                _vehicleLightStage.Update(id);
            }

            if (TMDiagnostics.Enabled && (_tickCounter == 1 || _tickCounter % 30 == 0))
            {
                string sample = "";
                if (_controlFrame.Count > 0 &&
                    _controlFrame[0] is CarlaNet.Types.Rpc.Commands.ApplyVehicleControlCommand vc)
                {
                    var ctl = vc.Control;
                    sample = $" sample[0]: actor={vc.Actor} throttle={ctl.Throttle:F2} steer={ctl.Steer:F2} brake={ctl.Brake:F2} reverse={ctl.Reverse} hand_brake={ctl.HandBrake}";
                }
                TMDiagnostics.Log($"[TM tick {_tickCounter}] registered={numVehicles} controlFrame={_controlFrame.Count}{sample}");
            }

            // ── 6b. Walker AI tick (Wave 5 — only if any walker is registered) ─
            // Pushes vehicle OBBs into the crowd, advances the per-walker
            // state machine, and appends ApplyWalkerState commands to the
            // same control frame so the simulator gets one batch per tick.
            if (hasWalkers)
            {
                RunWalkerTick(walkerNav!, vehicleIds);
            }

            // ── 7. Send the per-frame batch to the simulator ─────────
            if (_controlFrame.Count > 0 || _parameters.GetSynchronousMode())
            {
                try { _client.ApplyBatchSyncAsync(_controlFrame, doTickCue: false).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "ApplyBatchSync failed");
                    TMDiagnostics.LogFirstFailure("ApplyBatchSync", ex, _tickCounter);
                }
            }

            _vehicleLightStage.ClearPendingUpdates();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //                  Walker AI plumbing  (Wave 5)
    //
    //  The TM never proactively builds a WalkerNavigation. The first
    //  Python-side WalkerAIController.start() call routes through
    //  NavigationFactory.GetOrCreate(client) (fetches the navmesh, parses
    //  it, registers the walker). From that point on the TM's per-tick
    //  loop picks the factory's cached instance up via TryGet and drives
    //  it. TMs that never see a walker pay zero navmesh cost.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached walker-navigation instance if the Python shim has
    /// already constructed one for this client; never triggers the navmesh
    /// RPC itself.
    /// </summary>
    private WalkerNavigation? TryGetWalkerNavigation()
    {
        return NavigationFactory.TryGet(_client, out var wn) ? wn : null;
    }

    /// <summary>
    /// One walker-subsystem tick: push the latest vehicle OBBs in, advance
    /// the crowd, append the per-walker ApplyWalkerState commands to the
    /// outgoing control frame. Called from <see cref="RunOneTick"/>.
    /// </summary>
    private void RunWalkerTick(WalkerNavigation wn, IReadOnlyList<ActorId> vehicleIds)
    {
        try
        {
            // 1. Refresh OBBs from the current registered-vehicle set.
            wn.UpdateVehicleObbs(BuildObbList(vehicleIds));

            // 2. Estimate dt. The TM doesn't carry a synchronised clock
            //    (the time-sensitive stages above poll TickCount64 too),
            //    so derive dt the same way: difference between successive
            //    RunOneTick invocations, clamped to a sane range.
            float dt = ComputeDeltaSeconds();

            // 3. Advance crowd + walker state machine.
            wn.Tick(dt);

            // 4. Batch ApplyWalkerState commands.
            var walkerCommands = wn.GetWalkerControlCommands();
            for (int i = 0; i < walkerCommands.Count; i++)
                _controlFrame.Add(walkerCommands[i]);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Walker tick failed");
            TMDiagnostics.LogFirstFailure("WalkerTick", ex, _tickCounter);
        }
    }

    private long _lastWalkerTickTicks;
    private float ComputeDeltaSeconds()
    {
        long now = Environment.TickCount64;
        long delta = _lastWalkerTickTicks == 0 ? 50 : (now - _lastWalkerTickTicks);
        _lastWalkerTickTicks = now;
        // Clamp 1 ms .. 200 ms to avoid feeding the crowd nonsense numbers
        // on the first tick or after a long stall.
        if (delta < 1) delta = 1;
        if (delta > 200) delta = 200;
        return delta / 1000.0f;
    }

    /// <summary>
    /// Builds an OBB snapshot for the currently-registered vehicles, pulled
    /// from the world-observer cache. Vehicles not yet in the cache (e.g.
    /// spawned this tick) are skipped — they'll show up next tick.
    /// </summary>
    private IReadOnlyList<(ActorId Id, Location Center, Vector3D Extent, float YawDeg)>
        BuildObbList(IReadOnlyList<ActorId> vehicleIds)
    {
        if (vehicleIds.Count == 0)
            return Array.Empty<(ActorId, Location, Vector3D, float)>();

        var list = new List<(ActorId, Location, Vector3D, float)>(vehicleIds.Count);
        for (int i = 0; i < vehicleIds.Count; i++)
        {
            var id = vehicleIds[i];
            var snap = _client.GetActorSnapshot(id);
            if (snap is null) continue;

            // Extent is in the vehicle's local frame; we treat it as world
            // half-size and let the OBB tracker rotate by yaw to do the
            // axis-aligned containment test in vehicle-local space.
            var center = snap.Transform.Location;
            // SimulationState in TM keeps a per-actor BoundingBox snapshot,
            // but pulling that here would tie this code to ALSM internals.
            // The world-observer payload doesn't carry bbox extents (they
            // live on the Actor record returned by spawn / get_actors_by_id);
            // a 2 m × 1 m × 1.5 m default matches a passenger car closely
            // enough for the walker-repulsion bias. Vehicles outside this
            // approximation can be tracked exactly if needed; v1 ships the
            // default.
            var extent = new Vector3D(2.5f, 1.0f, 1.5f);
            list.Add((id, center, extent, snap.Transform.Rotation.Yaw));
        }
        return list;
    }

    // ─────────────────────────────────────────────────────────────────
    //                  ITrafficManagerCallback impl
    // ─────────────────────────────────────────────────────────────────

    // RunOneTick holds _registrationGate for a whole tick, so registering a vehicle waits for the
    // current tick to finish. Callers reach this from the thread that owns world.tick(), where an
    // unbounded wait stops the simulation and — under a client that pumps a UI on that thread —
    // freezes the window with no output at all. A slow tick is a defect somewhere else, but it must
    // not be silent: warn once the wait becomes implausible, then keep waiting so behaviour is
    // unchanged.
    private static readonly TimeSpan RegistrationWaitWarning = TimeSpan.FromSeconds(5);

    private void EnterRegistrationGate(string operation)
    {
        if (Monitor.TryEnter(_registrationGate, RegistrationWaitWarning)) return;

        _logger?.LogWarning(
            "{Operation} has waited {Seconds:F0}s for the traffic-manager tick to release the "
            + "registration lock; the tick is running long and the calling thread is blocked.",
            operation, RegistrationWaitWarning.TotalSeconds);
        Monitor.Enter(_registrationGate);
    }

    public void RegisterVehicles(IReadOnlyList<Actor> actors)
    {
        EnterRegistrationGate(nameof(RegisterVehicles));
        try
        {
            _registeredVehicles.Insert(actors);
        }
        finally { Monitor.Exit(_registrationGate); }
    }

    public void UnregisterVehicles(IReadOnlyList<Actor> actors)
    {
        EnterRegistrationGate(nameof(UnregisterVehicles));
        try
        {
            for (int i = 0; i < actors.Count; i++)
            {
                ActorId id = actors[i].Id;
                _alsm.RemoveActor(id, registeredActor: true);
            }
        }
        finally { Monitor.Exit(_registrationGate); }
    }

    public void SetPercentageSpeedDifference(Actor actor, float percentage)
        => _parameters.SetPercentageSpeedDifference(actor.Id, percentage);

    public void SetLaneOffset(Actor actor, float offset)
        => _parameters.SetLaneOffset(actor.Id, offset);

    public void SetDesiredSpeed(Actor actor, float value)
        => _parameters.SetDesiredSpeed(actor.Id, value);

    public void SetUpdateVehicleLights(Actor actor, bool doUpdate)
        => _parameters.SetUpdateVehicleLights(actor.Id, doUpdate);

    public void SetCollisionDetection(Actor referenceActor, Actor otherActor, bool detectCollision)
        => _parameters.SetCollisionDetection(referenceActor.Id, otherActor.Id, otherActor, detectCollision);

    public void SetForceLaneChange(Actor actor, bool direction)
        => _parameters.SetForceLaneChange(actor.Id, direction);

    public void SetAutoLaneChange(Actor actor, bool enable)
        => _parameters.SetAutoLaneChange(actor.Id, enable);

    public void SetDistanceToLeadingVehicle(Actor actor, float distance)
        => _parameters.SetDistanceToLeadingVehicle(actor.Id, distance);

    public void SetPercentageIgnoreWalkers(Actor actor, float percentage)
        => _parameters.SetPercentageIgnoreWalkers(actor.Id, percentage);

    public void SetPercentageIgnoreVehicles(Actor actor, float percentage)
        => _parameters.SetPercentageIgnoreVehicles(actor.Id, percentage);

    public void SetPercentageRunningLight(Actor actor, float percentage)
        => _parameters.SetPercentageRunningLight(actor.Id, percentage);

    public void SetPercentageRunningSign(Actor actor, float percentage)
        => _parameters.SetPercentageRunningSign(actor.Id, percentage);

    public void SetKeepSlowLanePercentage(Actor actor, float percentage)
        => _parameters.SetKeepSlowLanePercentage(actor.Id, percentage);

    public void SetRandomLeftLaneChangePercentage(Actor actor, float percentage)
        => _parameters.SetRandomLeftLaneChangePercentage(actor.Id, percentage);

    public void SetRandomRightLaneChangePercentage(Actor actor, float percentage)
        => _parameters.SetRandomRightLaneChangePercentage(actor.Id, percentage);

    public void SetGlobalPercentageSpeedDifference(float percentage)
        => _parameters.SetGlobalPercentageSpeedDifference(percentage);

    public void SetGlobalLaneOffset(float offset)
        => _parameters.SetGlobalLaneOffset(offset);

    public void SetGlobalDistanceToLeadingVehicle(float distance)
        => _parameters.SetGlobalDistanceToLeadingVehicle(distance);

    public void SetHybridPhysicsMode(bool modeSwitch)
        => _parameters.SetHybridPhysicsMode(modeSwitch);

    public void SetHybridPhysicsRadius(float radius)
        => _parameters.SetHybridPhysicsRadius(radius);

    public void SetOSMMode(bool modeSwitch)
        => _parameters.SetOSMMode(modeSwitch);

    public void SetRouteReplanAttemptLimit(int limit)
        => _parameters.SetRouteReplanAttemptLimit(limit);

    public void SetRouteGreedyFallbackEnabled(bool enabled)
        => _parameters.SetRouteGreedyFallbackEnabled(enabled);

    public void SetCustomPath(Actor actor, IReadOnlyList<Location> path, bool emptyBuffer)
        => _parameters.SetCustomPath(actor.Id, path, emptyBuffer);

    public void RemoveUploadPath(ActorId actorId, bool removePath)
        => _parameters.RemoveUploadPath(actorId, removePath);

    public void UpdateUploadPath(ActorId actorId, IReadOnlyList<Location> path)
        => _parameters.UpdateUploadPath(actorId, path);

    public void SetImportedRoute(Actor actor, IReadOnlyList<byte> route, bool emptyBuffer)
        => _parameters.SetImportedRoute(actor.Id, route, emptyBuffer);

    public void RemoveImportedRoute(ActorId actorId, bool removePath)
        => _parameters.RemoveImportedRoute(actorId, removePath);

    public void UpdateImportedRoute(ActorId actorId, IReadOnlyList<byte> route)
        => _parameters.UpdateImportedRoute(actorId, route);

    public void SetRespawnDormantVehicles(bool modeSwitch)
        => _parameters.SetRespawnDormantVehicles(modeSwitch);

    public void SetBoundariesRespawnDormantVehicles(float lowerBound, float upperBound)
        => _parameters.SetBoundariesRespawnDormantVehicles(lowerBound, upperBound);

    public void GetNextAction(ActorId actorId)
    {
        // Upstream's RPC server lambda binds this to a void-returning call,
        // so we just compute and discard. The facade exposes the typed result.
        try { _localizationStage.ComputeNextAction(actorId); }
        catch (Exception ex) { _logger?.LogDebug(ex, "ComputeNextAction failed"); }
    }

    public void GetActionBuffer(ActorId actorId)
    {
        try { _localizationStage.ComputeActionBuffer(actorId); }
        catch (Exception ex) { _logger?.LogDebug(ex, "ComputeActionBuffer failed"); }
    }

    /// <summary>Facade helper: get the (RoadOption, SimpleWaypoint?) tuple.</summary>
    internal (RoadOption Option, SimpleWaypoint? Waypoint) ComputeNextAction(ActorId actorId)
        => _localizationStage.ComputeNextAction(actorId);

    /// <summary>Facade helper: get the full action buffer.</summary>
    internal List<(RoadOption Option, SimpleWaypoint Waypoint)> ComputeActionBuffer(ActorId actorId)
        => _localizationStage.ComputeActionBuffer(actorId);

    public void ShutDown() => Stop();

    public void SetSynchronousMode(bool mode)
    {
        bool previous = _parameters.GetSynchronousMode();
        _parameters.SetSynchronousMode(mode);
        if (previous && !mode)
        {
            // Releasing the worker so it stops waiting forever.
            _stepBegin.Set();
        }
    }

    public void SetSynchronousModeTimeOutInMiliSecond(double time)
        => _parameters.SetSynchronousModeTimeOutInMiliSecond(time);

    public void SetRandomDeviceSeed(ulong seed)
    {
        _seed = seed;
        _randomDevice = new RandomGenerator(seed);
        try { _client.ResetAllTrafficLightsAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "ResetAllTrafficLights failed"); }
    }

    public bool SynchronousTick()
    {
        if (!_parameters.GetSynchronousMode())
            return true;
        if (!_running)
            return false;

        _stepEnd.Reset();
        _stepBegin.Set();

        double timeoutMs = _parameters.GetSynchronousModeTimeOutInMiliSecond();
        // Treat 0 / non-positive as "no timeout" — matches upstream's
        // condition_variable::wait_for behaviour (millisecond=0 means
        // poll-once; we use Wait() with a large fallback).
        int waitMs = timeoutMs > 0
            ? (int)Math.Min(int.MaxValue, Math.Max(1, timeoutMs))
            : -1;
        bool ok = _stepEnd.Wait(waitMs);
        _stepEnd.Reset();
        return ok;
    }
}
