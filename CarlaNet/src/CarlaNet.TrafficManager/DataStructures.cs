// Source: carla/trafficmanager/DataStructures.h
//
// All small POD types that the TM stages pass between themselves: per-tick
// frame entries (LocalizationData / CollisionHazardData), the PID controller
// snapshot (StateEntry / ActuationSignal), and the various "frame" type
// aliases.
//
// Performance contract: at 50 vehicles × 30 Hz the stages allocate one entry
// per type per vehicle every tick. Every per-vehicle structure here is a
// `readonly record struct` so the per-tick frame arrays are flat blocks of
// memory with no boxing and no GC pressure.
#nullable enable

namespace CarlaNet.TrafficManager;

// ───────────────────────────────────────────────────────────────────────
// LocalizationStage outputs (DataStructures.h:37–42)
// ───────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-vehicle output of <c>LocalizationStage</c>. Carries the next junction
/// boundary, the "safe point" past it, and whether we're crossing the
/// junction's threshold this tick. Consumed by <c>MotionPlanStage</c> and
/// <c>TrafficLightStage</c>.
/// </summary>
/// <remarks>
/// `record struct`, not `readonly record struct`: in upstream the two
/// waypoint pointers are mutated after the fact (the
/// `LocalizationFrame[i].junction_end_point = ...` pattern in
/// LocalizationStage.cpp). Keeping the struct mutable preserves that
/// access pattern; consumers still treat it as a value type.
/// </remarks>
#pragma warning disable CS0649 // fields populated by Wave 3 LocalizationStage
internal record struct LocalizationData
{
    public SimpleWaypoint? JunctionEndPoint;
    public SimpleWaypoint? SafePoint;
    public bool IsAtJunctionEntrance;
}
#pragma warning restore CS0649

// ───────────────────────────────────────────────────────────────────────
// CollisionStage outputs (DataStructures.h:44–49)
// ───────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-vehicle output of <c>CollisionStage</c>. <see cref="Hazard"/> is true
/// when a collision is imminent within the lookahead horizon; in that case
/// <see cref="HazardActorId"/> is the leader/intruder and
/// <see cref="AvailableDistanceMargin"/> is the slack to the hazard.
/// </summary>
internal readonly record struct CollisionHazardData(
    float AvailableDistanceMargin,
    ActorId HazardActorId,
    bool Hazard);

// ───────────────────────────────────────────────────────────────────────
// PID actuation + previous-step state (DataStructures.h:55–68)
// ───────────────────────────────────────────────────────────────────────

/// <summary>
/// PID controller output: throttle, brake, steer ∈ [-1, 1] (steer is signed).
/// Mirrors <c>traffic_manager::ActuationSignal</c>.
/// </summary>
internal readonly record struct ActuationSignal(
    float Throttle,
    float Brake,
    float Steer);

/// <summary>
/// Previous PID step bookkeeping. Required because the PID step needs the
/// previous error for the derivative term and the previous time stamp for
/// the integral term. Mirrors <c>traffic_manager::StateEntry</c>.
/// </summary>
/// <remarks>
/// Upstream's <c>cc::Timestamp</c> wraps the simulator's elapsed seconds.
/// We use a plain <see cref="double"/> for the time instance to avoid
/// pulling a Timestamp record in for Wave 1; the PID arithmetic only ever
/// touches the elapsed-seconds field.
/// </remarks>
internal readonly record struct StateEntry(
    double TimeInstance,
    float AngularDeviation,
    float VelocityDeviation,
    float Steer);

// ───────────────────────────────────────────────────────────────────────
// Parameters helper struct (Parameters.h:32–35)
// ───────────────────────────────────────────────────────────────────────

/// <summary>
/// Pending lane-change request from <c>SetForceLaneChange</c>. The flag is
/// consumed on the next <c>LocalizationStage</c> tick (the map entry is
/// removed inside <c>GetForceLaneChange</c>, mirroring upstream).
/// </summary>
internal readonly record struct ChangeLaneInfo(
    bool ChangeLane,
    bool Direction);

// ───────────────────────────────────────────────────────────────────────
// SimulationState payload structs (SimulationState.h:17–40)
// ───────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-vehicle kinematic snapshot pushed by ALSM each tick. The hybrid-end
/// location field is mutated mid-frame by MotionPlanStage when teleporting
/// dormant vehicles, so this remains a mutable struct (not readonly).
/// </summary>
internal record struct KinematicState
{
    public Location Location;
    public Rotation Rotation;
    public Vector3D Velocity;
    public float SpeedLimit;
    public bool PhysicsEnabled;
    public bool IsDormant;
    public Location HybridEndLocation;
}

/// <summary>
/// Static, per-actor attributes captured at registration time. Used by
/// CollisionStage to size the geodesic boundary polygons.
/// </summary>
internal readonly record struct StaticAttributes(
    ActorType ActorType,
    float HalfLength,
    float HalfWidth,
    float HalfHeight);

/// <summary>
/// Per-vehicle traffic-light snapshot. Decoupled from <see cref="KinematicState"/>
/// because TL state is sampled less often (only the <c>AtTrafficLight</c>
/// transition triggers ALSM to push the underlying RPC value).
/// </summary>
#pragma warning disable CS0649 // fields populated by Wave 3 ALSM
internal record struct TrafficLightStateData
{
    public TLS TlState;
    public bool AtTrafficLight;
}
#pragma warning restore CS0649

// ───────────────────────────────────────────────────────────────────────
// ActorType enum (SimulationState.h:11–15)
// ───────────────────────────────────────────────────────────────────────

/// <summary>
/// Coarse classification of every actor the TM knows about. <c>Any</c> is
/// used as a wildcard in CollisionStage's broad-phase filter.
/// </summary>
internal enum ActorType
{
    Vehicle,
    Pedestrian,
    Any,
}

// ───────────────────────────────────────────────────────────────────────
// Frame aliases (DataStructures.h:42, 49, 51, 53)
//
// Upstream uses `using LocalizationFrame = std::vector<LocalizationData>;`
// — a transparent typedef. We don't expose dedicated wrapper types; the
// stages declare `List<LocalizationData>` directly. The aliases below are
// documentation only (handy for `using` import noise reduction if Wave 3
// agents want them).
// ───────────────────────────────────────────────────────────────────────
