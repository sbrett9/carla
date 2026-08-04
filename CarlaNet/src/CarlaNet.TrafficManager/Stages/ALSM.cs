// Source: carla/trafficmanager/ALSM.{h,cpp}
//
// Agent Lifecycle and State Management. Runs once per tick (called by the
// orchestrator in Wave 4) and is responsible for:
//   1. Diffing the world's actor list against our registered set,
//   2. Cascading destruction notifications to every dependent stage,
//   3. Snapshotting every actor's kinematic + TL state into SimulationState,
//   4. Toggling hybrid-mode physics on/off based on hero proximity,
//   5. Updating idle-time bookkeeping and culling stuck vehicles,
//   6. Pushing per-actor grid-occupancy data through TrackTraffic.
//
// Hot-path RPC count: ONE per tick — `GetActorsByIdAsync` against the cached
// actor-id list. All transform/velocity/acceleration data is pulled lock-free
// from CarlaClient's world-observer cache (zero additional round trips). This
// matches upstream's design (one `world.GetActors()` call per tick) and keeps
// per-tick overhead at ~1-3 ms regardless of registered vehicle count.
//
// Threading: single-threaded by contract. The orchestrator's worker thread is
// the only caller. We use `.GetAwaiter().GetResult()` to drive the async
// CarlaClient RPC because the TM main loop is synchronous (matches upstream's
// blocking world.GetActors() call).
#nullable enable

using CarlaNet.Transport;

namespace CarlaNet.TrafficManager.Stages;

// ─────────────────────────────────────────────────────────────────────────
// Local helper: BufferMap alias and IStageWithRemoveActor interface.
// Defined here (not DataStructures.cs — Wave 1C's territory) because no
// other stage owns these concepts.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-actor planning horizon buffer. Mirrors upstream's
/// <c>std::deque&lt;SimpleWaypointPtr&gt;</c>. We use <see cref="List{T}"/> because the buffer holds
/// tens of entries, where the front-pop cost (O(n)) is trivial next to a LinkedList's per-node
/// allocations.
/// <para>
/// That trade depends on the buffer staying small, which is not automatic: the walks that fill it
/// stop on straight-line distance tests that a cyclic road graph never satisfies. They are bounded
/// for exactly this reason (see <c>LocalizationStage.MaxHorizonWalkSteps</c>). Removing that bound
/// makes the front-pop quadratic as well as unbounded.
/// </para>
/// </summary>
internal sealed class WaypointBuffer : List<SimpleWaypoint>
{
    public WaypointBuffer() : base(capacity: 64) { }
}

/// <summary>
/// <c>BufferMap</c> typedef from DataStructures.h:33 — keyed map from each
/// registered actor to its horizon buffer. Owned by the orchestrator and
/// passed by reference into the stages that mutate (LocalizationStage,
/// ALSM) or read (CollisionStage, MotionPlanStage, VehicleLightStage) it.
/// </summary>
internal sealed class BufferMap : Dictionary<ActorId, WaypointBuffer>
{
}

/// <summary>
/// Minimal contract every stage must satisfy so ALSM can broadcast
/// destruction notifications without holding hard references to each
/// concrete stage type (which would couple this file to every sibling
/// agent's Wave 3 work). Sibling stages implement this interface.
/// </summary>
internal interface IStageWithRemoveActor
{
    void RemoveActor(ActorId actorId);
}

// ─────────────────────────────────────────────────────────────────────────
// ALSM
// ─────────────────────────────────────────────────────────────────────────

internal sealed class ALSM
{
    // ── Refs / dependencies (held by reference, mutated externally) ─────
    private readonly AtomicActorSet _registeredVehicles;
    private readonly BufferMap _bufferMap;
    private readonly TrackTraffic _trackTraffic;
    private readonly List<ActorId> _markedForRemoval;
    private readonly Parameters _parameters;
    private readonly CarlaClient _client;
    private readonly InMemoryMap _localMap;
    private readonly SimulationState _simulationState;

    // Downstream stages — only needed for the RemoveActor cascade.
    private readonly IStageWithRemoveActor _localizationStage;
    private readonly IStageWithRemoveActor _collisionStage;
    private readonly IStageWithRemoveActor _trafficLightStage;
    private readonly IStageWithRemoveActor _motionPlanStage;
    private readonly IStageWithRemoveActor _vehicleLightStage;

    // ── Internal state (per-tick mutated) ───────────────────────────────
    private readonly Dictionary<ActorId, Actor> _unregisteredActors = new();
    private readonly Dictionary<ActorId, double> _idleTime = new();
    private readonly Dictionary<ActorId, Actor> _heroActors = new();
    private readonly Dictionary<ActorId, bool> _hasPhysicsEnabled = new();

    private double _elapsedLastActorDestruction;
    private double _currentTimestamp;

    public ALSM(
        AtomicActorSet registeredVehicles,
        BufferMap bufferMap,
        TrackTraffic trackTraffic,
        List<ActorId> markedForRemoval,
        Parameters parameters,
        CarlaClient client,
        InMemoryMap localMap,
        SimulationState simulationState,
        IStageWithRemoveActor localizationStage,
        IStageWithRemoveActor collisionStage,
        IStageWithRemoveActor trafficLightStage,
        IStageWithRemoveActor motionPlanStage,
        IStageWithRemoveActor vehicleLightStage)
    {
        _registeredVehicles = registeredVehicles;
        _bufferMap = bufferMap;
        _trackTraffic = trackTraffic;
        _markedForRemoval = markedForRemoval;
        _parameters = parameters;
        _client = client;
        _localMap = localMap;
        _simulationState = simulationState;
        _localizationStage = localizationStage;
        _collisionStage = collisionStage;
        _trafficLightStage = trafficLightStage;
        _motionPlanStage = motionPlanStage;
        _vehicleLightStage = vehicleLightStage;
    }

    // ── Public entrypoint (one call per tick) ───────────────────────────

    /// <summary>
    /// Runs the ALSM pipeline. Idempotent w.r.t. SimulationState — calling
    /// this twice in a row is a no-op for an unchanged world.
    /// </summary>
    public void Update()
    {
        bool hybridPhysicsMode = _parameters.GetHybridPhysicsMode();

        // ── 1. Pull world state (single RPC) ────────────────────────────
        // Upstream calls world.GetSnapshot() + world.GetActors(). The .NET
        // world observer already streams snapshots continuously into
        // CarlaClient's actor cache, so we can read elapsed time and the
        // actor id list for free. We still need the full Actor records
        // (attributes + bounding box) for newly-discovered actors so we
        // issue ONE RPC: GetActorsByIdAsync against the cached id list.
        IReadOnlyList<ActorId> worldActorIds = _client.GetCachedActorIds();
        // Elapsed seconds: derived from snapshot count. The observer fires
        // OnTick with the latest timestamp; the orchestrator (Wave 4) will
        // pass us a TickTimestamp eventually but for now we approximate via
        // walltime — fine for the idle-time threshold use case (it only
        // compares deltas).
        _currentTimestamp = GetCurrentElapsedSeconds();

        IReadOnlyList<Actor> worldActors = _client
            .GetActorsByIdAsync(worldActorIds)
            .GetAwaiter().GetResult();

        // ── 2. Find destroyed actors and propagate ──────────────────────
        var (destroyedRegistered, destroyedUnregistered) = IdentifyDestroyedActors(worldActors);

        foreach (var deletionId in destroyedRegistered)
            RemoveActor(deletionId, registeredActor: true);

        foreach (var deletionId in destroyedUnregistered)
            RemoveActor(deletionId, registeredActor: false);

        // ── 3. Invalidate hero actors that died ─────────────────────────
        if (_heroActors.Count != 0)
        {
            var heroActorsToDelete = new List<ActorId>();
            foreach (var kv in _heroActors)
            {
                if (destroyedUnregistered.Contains(kv.Key) || destroyedRegistered.Contains(kv.Key))
                    heroActorsToDelete.Add(kv.Key);
            }
            for (int i = 0; i < heroActorsToDelete.Count; i++)
                _heroActors.Remove(heroActorsToDelete[i]);
        }

        // ── 4. Scan for newly-spawned actors ────────────────────────────
        IdentifyNewActors(worldActors);

        // ── 5. Update dynamic state for registered vehicles ─────────────
        var maxIdleTime = new IdleInfo(0u, _currentTimestamp);
        UpdateRegisteredActorsData(hybridPhysicsMode, ref maxIdleTime);

        // ── 6. Cull stuck registered vehicles ───────────────────────────
        if (IsVehicleStuck(maxIdleTime.ActorId)
            && (_currentTimestamp - _elapsedLastActorDestruction) > Constants.VehicleRemoval.DELTA_TIME_BETWEEN_DESTRUCTIONS
            && !_heroActors.ContainsKey(maxIdleTime.ActorId))
        {
            DestroyActorViaRpc(maxIdleTime.ActorId);
            _registeredVehicles.Destroy(maxIdleTime.ActorId);
            RemoveActor(maxIdleTime.ActorId, registeredActor: true);
            _elapsedLastActorDestruction = _currentTimestamp;
        }

        // ── 7. Process stage-flagged removals (OSM mode only) ───────────
        if (_parameters.GetOSMMode())
        {
            for (int i = 0; i < _markedForRemoval.Count; i++)
            {
                ActorId actorId = _markedForRemoval[i];
                DestroyActorViaRpc(actorId);
                _registeredVehicles.Destroy(actorId);
                RemoveActor(actorId, registeredActor: true);
            }
            _markedForRemoval.Clear();
        }

        // ── 8. Update unregistered actors (walkers + non-TM vehicles) ──
        UpdateUnregisteredActorsData();
    }

    // ─────────────────────────────────────────────────────────────────
    //                       Private helpers
    // ─────────────────────────────────────────────────────────────────

    private double GetCurrentElapsedSeconds()
    {
        // Use Environment.TickCount64-derived monotonic clock. This is
        // close enough for ALSM's idle-time comparisons; the orchestrator
        // (Wave 4) will inject the real cc::Timestamp via a setter once
        // it threads timestamps through the world-observer callback.
        return Environment.TickCount64 / 1000.0;
    }

    private void IdentifyNewActors(IReadOnlyList<Actor> worldActors)
    {
        for (int i = 0; i < worldActors.Count; i++)
        {
            Actor actor = worldActors[i];
            ActorId actorId = actor.Id;
            string typeId = actor.Description.Id;
            // Identify hero vehicles by scanning role_name attribute.
            if (typeId.Length > 0 && typeId[0] == 'v')
            {
                if (_heroActors.Count == 0 || !_heroActors.ContainsKey(actorId))
                {
                    var attributes = actor.Description.Attributes;
                    for (int j = 0; j < attributes.Count; j++)
                    {
                        var attr = attributes[j];
                        if (attr.Id == "role_name" && attr.Value == "hero")
                        {
                            _heroActors[actorId] = actor;
                            break;
                        }
                    }
                }
            }

            if (!_registeredVehicles.Contains(actorId)
                && !_unregisteredActors.ContainsKey(actorId))
            {
                _unregisteredActors[actorId] = actor;
            }
        }
    }

    private (HashSet<ActorId> Registered, HashSet<ActorId> Unregistered) IdentifyDestroyedActors(
        IReadOnlyList<Actor> worldActors)
    {
        var deletedRegistered = new HashSet<ActorId>();
        var deletedUnregistered = new HashSet<ActorId>();

        // Snapshot current actor set.
        var currentActors = new HashSet<ActorId>(worldActors.Count);
        for (int i = 0; i < worldActors.Count; i++)
            currentActors.Add(worldActors[i].Id);

        // Registered vehicles no longer in the world.
        IReadOnlyList<ActorId> registeredIds = _registeredVehicles.GetIDList();
        for (int i = 0; i < registeredIds.Count; i++)
        {
            ActorId actorId = registeredIds[i];
            if (!currentActors.Contains(actorId))
                deletedRegistered.Add(actorId);
        }

        // Unregistered actors that are gone OR have since been registered.
        foreach (var kv in _unregisteredActors)
        {
            ActorId actorId = kv.Key;
            if (!currentActors.Contains(actorId) || _registeredVehicles.Contains(actorId))
                deletedUnregistered.Add(actorId);
        }

        return (deletedRegistered, deletedUnregistered);
    }

    private readonly record struct IdleInfo(ActorId ActorId, double Time);

    private void UpdateRegisteredActorsData(bool hybridPhysicsMode, ref IdleInfo maxIdleTime)
    {
        IReadOnlyList<Actor> vehicleList = _registeredVehicles.GetList();
        bool heroActorPresent = _heroActors.Count != 0;
        float physicsRadius = _parameters.GetHybridPhysicsRadius();
        float physicsRadiusSquare = physicsRadius * physicsRadius;
        bool isRespawnVehicles = _parameters.GetRespawnDormantVehicles();

        if (isRespawnVehicles && !heroActorPresent)
            _trackTraffic.SetHeroLocation(new Location(0f, 0f, 0f));

        // Hero vehicles first so the broadcast hero-location is set before
        // the others read it.
        foreach (var kv in _heroActors)
        {
            if (isRespawnVehicles)
            {
                Transform t = _client.GetActorTransform(kv.Key);
                _trackTraffic.SetHeroLocation(t.Location);
            }
            UpdateData(hybridPhysicsMode, kv.Value, heroActorPresent, physicsRadiusSquare);
        }

        // All non-hero registered vehicles.
        for (int i = 0; i < vehicleList.Count; i++)
        {
            Actor vehicle = vehicleList[i];
            ActorId actorId = vehicle.Id;
            if (!_heroActors.ContainsKey(actorId))
            {
                UpdateData(hybridPhysicsMode, vehicle, heroActorPresent, physicsRadiusSquare);
                UpdateIdleTime(ref maxIdleTime, actorId);
            }
        }
    }

    private void UpdateData(bool hybridPhysicsMode, Actor vehicle,
                            bool heroActorPresent, float physicsRadiusSquare)
    {
        ActorId actorId = vehicle.Id;

        // Pull live kinematic state from the world-observer cache (free).
        Transform vehicleTransform = _client.GetActorTransform(actorId);
        Location vehicleLocation = vehicleTransform.Location;
        Rotation vehicleRotation = vehicleTransform.Rotation;
        Vector3D vehicleVelocity = _client.GetActorVelocity(actorId);
        bool stateEntryPresent = _simulationState.ContainsActor(actorId);

        // Seed idle time on first sighting.
        if (!_idleTime.ContainsKey(actorId) && _currentTimestamp != 0.0)
            _idleTime[actorId] = _currentTimestamp;

        // Hybrid mode: enable physics only when within radius of any hero.
        bool inRangeOfHeroActor = false;
        if (heroActorPresent && hybridPhysicsMode)
        {
            foreach (var kv in _heroActors)
            {
                ActorId heroActorId = kv.Key;
                if (_simulationState.ContainsActor(heroActorId))
                {
                    Location heroLocation = _simulationState.GetLocation(heroActorId);
                    if (DistanceSquared(vehicleLocation, heroLocation) < physicsRadiusSquare)
                    {
                        inRangeOfHeroActor = true;
                        break;
                    }
                }
            }
        }

        bool enablePhysics = hybridPhysicsMode ? inRangeOfHeroActor : true;
        if (!_hasPhysicsEnabled.TryGetValue(actorId, out var prevPhys) || prevPhys != enablePhysics)
        {
            if (!_heroActors.ContainsKey(actorId))
            {
                // Upstream: vehicle->SetSimulatePhysics(enable_physics). RPC.
                try
                {
                    _client.SetActorSimulatePhysicsAsync(actorId, enablePhysics).GetAwaiter().GetResult();
                }
                catch (Exception) { /* swallow RPC errors — match upstream's loose handling */ }
                _hasPhysicsEnabled[actorId] = enablePhysics;
                if (enablePhysics && stateEntryPresent)
                {
                    try
                    {
                        _client.SetActorTargetVelocityAsync(actorId, _simulationState.GetVelocity(actorId))
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception) { /* swallow */ }
                }
            }
        }

        // When physics is disabled, recompute velocity from displacement.
        if (stateEntryPresent && !_simulationState.IsPhysicsEnabled(actorId))
        {
            Location previousLocation = _simulationState.GetLocation(actorId);
            Location previousEndLocation = _simulationState.GetHybridEndLocation(actorId);
            float invHybridDt = (float)Constants.HybridMode.INV_HYBRID_DT;
            vehicleVelocity = new Vector3D(
                (previousEndLocation.X - previousLocation.X) * invHybridDt,
                (previousEndLocation.Y - previousLocation.Y) * invHybridDt,
                (previousEndLocation.Z - previousLocation.Z) * invHybridDt);
        }

        // Build the kinematic snapshot.
        // Speed limit + traffic-light state come from the world-observer
        // snapshot's per-vehicle VehicleData payload, which the server fills
        // each tick from the vehicle's AI controller (WorldObserver.cpp).
        // Reading them here is what lets the TM see a red light / a real speed
        // limit; previously these were hardcoded to Green / 0, so
        // set_percentage_running_light and any speed-limit-relative target were
        // inert. speed_limit is km/h, matching upstream SimulationState.
        VehicleObservedState observed = _client.GetActorVehicleState(actorId);
        float speedLimit = observed.SpeedLimit;

        // Dormant flag — comes from upstream's actor->IsDormant(). The
        // .NET observer doesn't expose this directly (the type-dependent
        // state union has no dormant bit on the wire as of UE5.7). For
        // hybrid-mode parity it suffices that dormant == !physics_enabled
        // for non-hero registered vehicles.
        bool isDormant = !enablePhysics && !_heroActors.ContainsKey(actorId);

        var kinematicState = new KinematicState
        {
            Location = vehicleLocation,
            Rotation = vehicleRotation,
            Velocity = vehicleVelocity,
            SpeedLimit = speedLimit,
            PhysicsEnabled = enablePhysics,
            IsDormant = isDormant,
            HybridEndLocation = new Location(0f, 0f, 0f),
        };

        var tlStateData = new TrafficLightStateData
        {
            TlState = observed.TrafficLightState,
            AtTrafficLight = observed.AtTrafficLight,
        };

        if (stateEntryPresent)
        {
            _simulationState.UpdateKinematicState(actorId, kinematicState);
            _simulationState.UpdateTrafficLightState(actorId, tlStateData);
        }
        else
        {
            // First sighting — populate static attributes from the Actor's
            // bounding box (which is captured at registration time).
            Vector3D dimensions = vehicle.BoundingBox.Extent;
            var attributes = new StaticAttributes(
                ActorType.Vehicle, dimensions.X, dimensions.Y, dimensions.Z);

            _simulationState.AddActor(actorId, kinematicState, attributes, tlStateData);
        }
    }

    private void UpdateUnregisteredActorsData()
    {
        // Snapshot list copy — UpdateUnregisteredGridPosition mutates state
        // we're iterating in TrackTraffic so we need to detach from any
        // implicit aliasing.
        foreach (var actorInfo in _unregisteredActors)
        {
            ActorId actorId = actorInfo.Key;
            Actor actorPtr = actorInfo.Value;
            string typeId = actorPtr.Description.Id;

            Transform actorTransform = _client.GetActorTransform(actorId);
            Location actorLocation = actorTransform.Location;
            Rotation actorRotation = actorTransform.Rotation;
            Vector3D actorVelocity = _client.GetActorVelocity(actorId);

            var kinematicState = new KinematicState
            {
                Location = actorLocation,
                Rotation = actorRotation,
                Velocity = actorVelocity,
                SpeedLimit = -1.0f,
                PhysicsEnabled = true,
                IsDormant = false,
                HybridEndLocation = new Location(0f, 0f, 0f),
            };

            var tlStateData = new TrafficLightStateData { TlState = TLS.Green, AtTrafficLight = false };
            ActorType actorType = ActorType.Any;
            var nearestWaypoints = new List<SimpleWaypoint>(3);

            bool stateEntryNotPresent = !_simulationState.ContainsActor(actorId);

            if (typeId.Length > 0 && typeId[0] == 'v')
            {
                // Unregistered vehicles (non-TM traffic) still carry real
                // VehicleData in the snapshot, so read their traffic-light state
                // and speed limit too — collision/motion stages consult these.
                VehicleObservedState observed = _client.GetActorVehicleState(actorId);
                kinematicState.SpeedLimit = observed.SpeedLimit;
                tlStateData.TlState = observed.TrafficLightState;
                tlStateData.AtTrafficLight = observed.AtTrafficLight;

                if (stateEntryNotPresent)
                {
                    Vector3D dimensions = actorPtr.BoundingBox.Extent;
                    actorType = ActorType.Vehicle;
                    var attributes = new StaticAttributes(
                        actorType, dimensions.X, dimensions.Y, dimensions.Z);
                    _simulationState.AddActor(actorId, kinematicState, attributes, tlStateData);
                }
                else
                {
                    _simulationState.UpdateKinematicState(actorId, kinematicState);
                    _simulationState.UpdateTrafficLightState(actorId, tlStateData);
                }

                // Identify occupied waypoints (3-point sample along heading).
                Vector3D extent = actorPtr.BoundingBox.Extent;
                Vector3D heading = GetForwardVector(actorRotation);
                var pFront = new Location(
                    actorLocation.X + extent.X * heading.X,
                    actorLocation.Y + extent.X * heading.Y,
                    actorLocation.Z + extent.X * heading.Z);
                var pBack = new Location(
                    actorLocation.X - extent.X * heading.X,
                    actorLocation.Y - extent.X * heading.Y,
                    actorLocation.Z - extent.X * heading.Z);
                nearestWaypoints.Add(_localMap.GetWaypoint(pFront));
                nearestWaypoints.Add(_localMap.GetWaypoint(actorLocation));
                nearestWaypoints.Add(_localMap.GetWaypoint(pBack));
            }
            else if (typeId.Length > 0 && typeId[0] == 'w')
            {
                if (stateEntryNotPresent)
                {
                    Vector3D dimensions = actorPtr.BoundingBox.Extent;
                    actorType = ActorType.Pedestrian;
                    var attributes = new StaticAttributes(
                        actorType, dimensions.X, dimensions.Y, dimensions.Z);
                    _simulationState.AddActor(actorId, kinematicState, attributes, tlStateData);
                }
                else
                {
                    _simulationState.UpdateKinematicState(actorId, kinematicState);
                }
                nearestWaypoints.Add(_localMap.GetWaypoint(actorLocation));
            }

            _trackTraffic.UpdateUnregisteredGridPosition(actorId, nearestWaypoints);
        }
    }

    private void UpdateIdleTime(ref IdleInfo maxIdleTime, ActorId actorId)
    {
        if (!_idleTime.TryGetValue(actorId, out var idleDuration))
            return;

        Vector3D vel = _simulationState.GetVelocity(actorId);
        float vSq = vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z;
        float thresholdSq = Constants.VehicleRemoval.STOPPED_VELOCITY_THRESHOLD
                          * Constants.VehicleRemoval.STOPPED_VELOCITY_THRESHOLD;
        if (vSq > thresholdSq)
        {
            idleDuration = _currentTimestamp;
            _idleTime[actorId] = idleDuration;
        }

        // Track the "most idle" vehicle (smallest last-moved timestamp).
        if (maxIdleTime.ActorId == 0u || maxIdleTime.Time > idleDuration)
            maxIdleTime = new IdleInfo(actorId, idleDuration);
    }

    private bool IsVehicleStuck(ActorId actorId)
    {
        if (!_idleTime.TryGetValue(actorId, out var since))
            return false;

        double deltaIdleTime = _currentTimestamp - since;
        TrafficLightStateData tlState = _simulationState.GetTLS(actorId);

        if (deltaIdleTime >= Constants.VehicleRemoval.RED_TL_BLOCKED_TIME_THRESHOLD
            || (deltaIdleTime >= Constants.VehicleRemoval.BLOCKED_TIME_THRESHOLD
                && tlState.TlState != TLS.Red))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Cascades a destruction through every downstream stage's
    /// <see cref="IStageWithRemoveActor.RemoveActor"/>, then clears
    /// per-actor state in SimulationState / BufferMap / TrackTraffic.
    /// </summary>
    public void RemoveActor(ActorId actorId, bool registeredActor)
    {
        if (registeredActor)
        {
            _registeredVehicles.Remove(new[] { actorId });
            _bufferMap.Remove(actorId);
            _idleTime.Remove(actorId);
            _localizationStage.RemoveActor(actorId);
            _collisionStage.RemoveActor(actorId);
            _trafficLightStage.RemoveActor(actorId);
            _motionPlanStage.RemoveActor(actorId);
            _vehicleLightStage.RemoveActor(actorId);
        }
        else
        {
            _unregisteredActors.Remove(actorId);
            _heroActors.Remove(actorId);
        }

        _trackTraffic.DeleteActor(actorId);
        _simulationState.RemoveActor(actorId);
    }

    public void Reset()
    {
        _unregisteredActors.Clear();
        _idleTime.Clear();
        _heroActors.Clear();
        _elapsedLastActorDestruction = 0.0;
        _currentTimestamp = GetCurrentElapsedSeconds();
    }

    // ── Tiny static helpers (kept inline to avoid Vector3D allocations) ─

    private static float DistanceSquared(Location a, Location b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static Vector3D GetForwardVector(Rotation r)
    {
        const float deg2rad = MathF.PI / 180.0f;
        float cy = MathF.Cos(r.Yaw * deg2rad);
        float sy = MathF.Sin(r.Yaw * deg2rad);
        float cp = MathF.Cos(r.Pitch * deg2rad);
        float sp = MathF.Sin(r.Pitch * deg2rad);
        return new Vector3D(cp * cy, cp * sy, sp);
    }

    private void DestroyActorViaRpc(ActorId actorId)
    {
        try
        {
            _client.DestroyActorAsync(actorId).GetAwaiter().GetResult();
        }
        catch (Exception) { /* swallow — matches upstream's loose handling */ }
    }
}
