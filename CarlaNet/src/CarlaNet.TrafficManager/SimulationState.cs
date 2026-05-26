// Source: carla/trafficmanager/SimulationState.h / SimulationState.cpp
//
// Per-tick snapshot of every actor in the world. ALSM populates this from
// the result of `world.GetActors()` once per tick; every downstream stage
// reads from it instead of doing its own RPC. Single-threaded access from
// the TM worker thread → plain Dictionary is sufficient (no concurrency
// wrapper required).
//
// All the kinematic/static/TL structs live in DataStructures.cs to keep the
// per-frame allocations colocated.
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// In-memory mirror of the simulator's actor state. Holds three keyed maps
/// (kinematic, static attributes, traffic-light) plus an actor-id set.
/// </summary>
/// <remarks>
/// Single-threaded by contract — the worker thread mutates it during
/// <c>ALSM.Update</c> and then reads it from every subsequent stage in the
/// same tick. The TM never exposes a concurrent setter.
/// </remarks>
internal sealed class SimulationState
{
    private readonly HashSet<ActorId> _actorSet = new();
    private readonly Dictionary<ActorId, KinematicState> _kinematicStateMap = new();
    private readonly Dictionary<ActorId, StaticAttributes> _staticAttributeMap = new();
    private readonly Dictionary<ActorId, TrafficLightStateData> _tlStateMap = new();

    public SimulationState() { }

    public void AddActor(ActorId actorId,
                          KinematicState kinematicState,
                          StaticAttributes attributes,
                          TrafficLightStateData tlState)
    {
        _actorSet.Add(actorId);
        _kinematicStateMap[actorId] = kinematicState;
        _staticAttributeMap[actorId] = attributes;
        _tlStateMap[actorId] = tlState;
    }

    public bool ContainsActor(ActorId actorId) => _actorSet.Contains(actorId);

    public void RemoveActor(ActorId actorId)
    {
        _actorSet.Remove(actorId);
        _kinematicStateMap.Remove(actorId);
        _staticAttributeMap.Remove(actorId);
        _tlStateMap.Remove(actorId);
    }

    public void Reset()
    {
        _actorSet.Clear();
        _kinematicStateMap.Clear();
        _staticAttributeMap.Clear();
        _tlStateMap.Clear();
    }

    public void UpdateKinematicState(ActorId actorId, KinematicState state)
        => _kinematicStateMap[actorId] = state;

    public void UpdateKinematicHybridEndLocation(ActorId actorId, Location location)
    {
        // Dictionary lookup via ref-of-value: requires CollectionsMarshal.
        // Equivalent to upstream's `kinematic_state_map.at(actor_id).hybrid_end_location = location`.
        ref var state = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrNullRef(_kinematicStateMap, actorId);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref state))
            state.HybridEndLocation = location;
    }

    /// <summary>
    /// Update the per-actor TL snapshot. Matches the upstream
    /// "stickiness" rule (SimulationState.cpp:46–55): once a vehicle
    /// has crossed into the trigger volume on Green, hold Green even
    /// across a Green→Yellow transition.
    /// </summary>
    public void UpdateTrafficLightState(ActorId actorId, TrafficLightStateData state)
    {
        var previousTlState = GetTLS(actorId);
        if (previousTlState.AtTrafficLight && previousTlState.TlState == TLS.Green)
        {
            state.TlState = TLS.Green;
        }
        _tlStateMap[actorId] = state;
    }

    // Plain getters — Dictionary indexer throws KeyNotFoundException, same
    // behavior as `std::unordered_map::at`. Callers in upstream never check
    // first because ALSM guarantees the entry exists before the read.

    public Location GetLocation(ActorId actorId) => _kinematicStateMap[actorId].Location;

    public Location GetHybridEndLocation(ActorId actorId) => _kinematicStateMap[actorId].HybridEndLocation;

    public Rotation GetRotation(ActorId actorId) => _kinematicStateMap[actorId].Rotation;

    /// <summary>
    /// Forward vector derived from the Rotation. C++ calls
    /// <c>rotation.GetForwardVector()</c>; we inline the same yaw/pitch math
    /// here so this file has no cross-project dependency. Carla uses left-
    /// handed UE coords with yaw rotating around +Z.
    /// </summary>
    public Vector3D GetHeading(ActorId actorId)
    {
        var r = _kinematicStateMap[actorId].Rotation;
        // Match cg::Math::GetForwardVector (degrees → radians + standard
        // UE-style yaw/pitch composition).
        const float deg2rad = MathF.PI / 180.0f;
        float cy = MathF.Cos(r.Yaw * deg2rad);
        float sy = MathF.Sin(r.Yaw * deg2rad);
        float cp = MathF.Cos(r.Pitch * deg2rad);
        float sp = MathF.Sin(r.Pitch * deg2rad);
        return new Vector3D(cp * cy, cp * sy, sp);
    }

    public Vector3D GetVelocity(ActorId actorId) => _kinematicStateMap[actorId].Velocity;

    public float GetSpeedLimit(ActorId actorId) => _kinematicStateMap[actorId].SpeedLimit;

    public bool IsPhysicsEnabled(ActorId actorId) => _kinematicStateMap[actorId].PhysicsEnabled;

    public bool IsDormant(ActorId actorId) => _kinematicStateMap[actorId].IsDormant;

    public TrafficLightStateData GetTLS(ActorId actorId) => _tlStateMap[actorId];

    public ActorType GetType(ActorId actorId) => _staticAttributeMap[actorId].ActorType;

    /// <summary>Half-extents <c>(L/2, W/2, H/2)</c> packed in a Vector3D.</summary>
    public Vector3D GetDimensions(ActorId actorId)
    {
        var a = _staticAttributeMap[actorId];
        return new Vector3D(a.HalfLength, a.HalfWidth, a.HalfHeight);
    }
}
