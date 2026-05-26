// Source: carla/trafficmanager/TrafficManagerBase.h
//
// The TM-side relay surface. TrafficManagerServer (the RPC listener) gets
// incoming "register_vehicle", "set_percentage_speed_difference", etc. calls
// from the CARLA simulator and forwards them straight into an implementation
// of this interface. Wave 4 produces the real implementation
// (`TrafficManagerLocal`); for Wave 2.5 the interface is what locks the
// contract.
//
// Method names + signatures match TrafficManagerBase.h 1:1 (C++ ActorPtr
// becomes the strongly-typed CarlaNet `Actor`; ActorId stays uint).
//
// NOTE: Path/Route are upstream typedefs:
//   using Path  = std::vector<cg::Location>;
//   using Route = std::vector<uint8_t>;   // sequence of RoadOption byte codes
//
// NOTE: Action / ActionBuffer return values (GetNextAction, GetActionBuffer)
// use `(RoadOption, client::Waypoint)`. CarlaNet does not yet have a
// public user-facing Waypoint type (Wave 4 fills this in). For Wave 2.5
// the return shape is left as `object` so the wire serialization works
// transparently when Wave 4 plugs in a `[MessagePackObject]` record there.
#nullable enable
using CarlaNet.TrafficManager;

namespace CarlaNet.TrafficManager;

/// <summary>
/// The TM-side callback surface targeted by the RPC server. Maps 1:1 to
/// <c>carla::traffic_manager::TrafficManagerBase</c>.
/// </summary>
/// <remarks>
/// Implementations are free to be thread-safe internally; the
/// <see cref="TrafficManagerServer"/> dispatches calls on thread-pool
/// threads, so a single instance may receive multiple concurrent
/// invocations.
/// </remarks>
public interface ITrafficManagerCallback
{
    // ── Vehicle pool management ───────────────────────────────────────────

    /// <summary>Register a list of vehicles with the traffic manager.</summary>
    void RegisterVehicles(IReadOnlyList<Actor> actors);

    /// <summary>Unregister a list of vehicles from the traffic manager.</summary>
    void UnregisterVehicles(IReadOnlyList<Actor> actors);

    // ── Per-actor knobs ───────────────────────────────────────────────────

    /// <summary>% decrease in velocity vs. speed limit (negative = increase).</summary>
    void SetPercentageSpeedDifference(Actor actor, float percentage);

    /// <summary>Lateral lane offset from center (positive=right, negative=left).</summary>
    void SetLaneOffset(Actor actor, float offset);

    /// <summary>Exact desired velocity (m/s).</summary>
    void SetDesiredSpeed(Actor actor, float value);

    /// <summary>Toggle automatic vehicle-light management for a single actor.</summary>
    void SetUpdateVehicleLights(Actor actor, bool doUpdate);

    /// <summary>Enable/disable collision detection between two actors.</summary>
    void SetCollisionDetection(Actor referenceActor, Actor otherActor, bool detectCollision);

    /// <summary>Force a lane change. <paramref name="direction"/> true = left, false = right.</summary>
    void SetForceLaneChange(Actor actor, bool direction);

    /// <summary>Enable/disable automatic lane change for an actor.</summary>
    void SetAutoLaneChange(Actor actor, bool enable);

    /// <summary>Per-actor distance margin to the leading vehicle (m).</summary>
    void SetDistanceToLeadingVehicle(Actor actor, float distance);

    /// <summary>% chance of ignoring collisions with walkers.</summary>
    void SetPercentageIgnoreWalkers(Actor actor, float percentage);

    /// <summary>% chance of ignoring collisions with vehicles.</summary>
    void SetPercentageIgnoreVehicles(Actor actor, float percentage);

    /// <summary>% chance of running any traffic light.</summary>
    void SetPercentageRunningLight(Actor actor, float percentage);

    /// <summary>% chance of running any traffic sign.</summary>
    void SetPercentageRunningSign(Actor actor, float percentage);

    /// <summary>% chance of staying in the slow (rightmost) lane.</summary>
    void SetKeepSlowLanePercentage(Actor actor, float percentage);

    /// <summary>% chance of randomly performing a left lane change.</summary>
    void SetRandomLeftLaneChangePercentage(Actor actor, float percentage);

    /// <summary>% chance of randomly performing a right lane change.</summary>
    void SetRandomRightLaneChangePercentage(Actor actor, float percentage);

    // ── Global / scalar knobs ─────────────────────────────────────────────

    /// <summary>Global % decrease in velocity vs. speed limit.</summary>
    void SetGlobalPercentageSpeedDifference(float percentage);

    /// <summary>Global lateral lane offset from center.</summary>
    void SetGlobalLaneOffset(float offset);

    /// <summary>Global distance margin to leading vehicle (m).</summary>
    void SetGlobalDistanceToLeadingVehicle(float distance);

    /// <summary>Enable hybrid physics mode (dormant vehicles are teleported).</summary>
    void SetHybridPhysicsMode(bool modeSwitch);

    /// <summary>Radius (m) within which physics stays active in hybrid mode.</summary>
    void SetHybridPhysicsRadius(float radius);

    /// <summary>Enable OpenStreetMap (off-OpenDRIVE) mode.</summary>
    void SetOSMMode(bool modeSwitch);

    // ── Imported paths / routes ───────────────────────────────────────────

    /// <summary>Replace or append a custom path for the actor.</summary>
    void SetCustomPath(Actor actor, IReadOnlyList<Location> path, bool emptyBuffer);

    /// <summary>Remove (or empty) a previously-uploaded custom path.</summary>
    void RemoveUploadPath(ActorId actorId, bool removePath);

    /// <summary>Update an already-uploaded custom path.</summary>
    void UpdateUploadPath(ActorId actorId, IReadOnlyList<Location> path);

    /// <summary>Set a custom route (sequence of RoadOption byte codes).</summary>
    void SetImportedRoute(Actor actor, IReadOnlyList<byte> route, bool emptyBuffer);

    /// <summary>Remove (or empty) a previously-uploaded custom route.</summary>
    void RemoveImportedRoute(ActorId actorId, bool removePath);

    /// <summary>Update an already-uploaded custom route.</summary>
    void UpdateImportedRoute(ActorId actorId, IReadOnlyList<byte> route);

    // ── Respawn ───────────────────────────────────────────────────────────

    /// <summary>Toggle respawn-on-dormant for distant vehicles.</summary>
    void SetRespawnDormantVehicles(bool modeSwitch);

    /// <summary>Min/max respawn distance bounds for dormant-vehicle respawn.</summary>
    void SetBoundariesRespawnDormantVehicles(float lowerBound, float upperBound);

    // ── Action queries ────────────────────────────────────────────────────

    /// <summary>
    /// Get the vehicle's next planned action. Upstream returns
    /// <c>Action = pair&lt;RoadOption, WaypointPtr&gt;</c>; the C++ TM server
    /// binds this to a void-returning lambda (it doesn't actually serialize
    /// the result back over the wire), so we follow the same contract.
    /// </summary>
    void GetNextAction(ActorId actorId);

    /// <summary>
    /// Get the vehicle's full action buffer. Same C++-side void-return
    /// caveat as <see cref="GetNextAction"/>.
    /// </summary>
    void GetActionBuffer(ActorId actorId);

    // ── Lifecycle ────────────────────────────────────────────────────────

    /// <summary>Shut the local TrafficManager down (Release in C++).</summary>
    void ShutDown();

    /// <summary>Toggle synchronous-mode operation.</summary>
    void SetSynchronousMode(bool mode);

    /// <summary>Tick timeout (milliseconds) for synchronous-mode operation.</summary>
    void SetSynchronousModeTimeOutInMiliSecond(double time);

    /// <summary>Seed the RNG used by lane-change probabilities, etc.</summary>
    void SetRandomDeviceSeed(ulong seed);

    /// <summary>
    /// Drive one frame of the TM pipeline synchronously. Returns true if the
    /// tick completed within the timeout, false otherwise.
    /// </summary>
    bool SynchronousTick();
}
