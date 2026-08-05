// Source: carla/trafficmanager/MotionPlanStage.{h,cpp}
//
// Per-vehicle actuation planner. For each registered vehicle, the stage:
//
//   1. Reads the localization snapshot (target waypoint, junction info)
//      from sibling <see cref="LocalizationStage"/>.
//   2. Reads collision + traffic-light hazards from
//      <see cref="CollisionStage"/> and <see cref="TrafficLightStage"/>.
//   3. Computes a per-tick *desired speed* — the minimum of:
//        - the parameter-clamped target velocity,
//        - the upcoming-turn speed (3-point-circle curvature),
//        - the upcoming-landmark speed (stop/yield/TL),
//        - the collision-lead follow speed (CollisionHandling).
//      and rate-limits the slowdown to <c>PERC_MAX_SLOWDOWN</c> per tick.
//   4. Computes velocity- and angular-deviation errors, calls
//      <see cref="PIDController.RunStep"/> with the urban/highway PID
//      gain set, and emits a <see cref="ActuationSignal"/>.
//   5. If physics is disabled OR the vehicle is dormant + respawn mode is
//      on, falls back to teleportation: pick a random waypoint in the
//      [lower, upper] band around the hero and apply a transform.
//   6. Wraps the actuation signal (or the teleport transform) in an
//      <see cref="ApplyVehicleControlCommand"/> / <see cref="ApplyTransformCommand"/>
//      and stores it in the per-actor output map.
//
// PID state per vehicle persists across ticks (the derivative term needs
// the previous error). State is keyed by ActorId and cleared via
// <see cref="RemoveActor"/> when ALSM unregisters the vehicle.
//
// Time stamps: upstream pulls <c>cc::World.GetSnapshot().GetTimestamp()</c>
// on every Update call. We don't have a World accessor in this wave;
// MotionPlanStage receives the current timestamp via
// <see cref="UpdateCurrentTimestamp"/> from the TM facade before each tick.
#nullable enable

using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Control;

using static CarlaNet.TrafficManager.LocalizationUtils;

namespace CarlaNet.TrafficManager.Stages;

internal sealed class MotionPlanStage : IStageWithRemoveActor
{
    // ── Dependencies (held by reference, never mutated by this stage) ───
    private readonly SimulationState _simulationState;
    private readonly Parameters _parameters;
    private readonly BufferMap _bufferMap;
    private readonly TrackTraffic _trackTraffic;
    private readonly InMemoryMap _localMap;
    private readonly RandomGenerator _rng;

    // Read-only views into sibling stage outputs.
    private readonly IReadOnlyDictionary<ActorId, LocalizationData> _localizationOutput;
    private readonly IReadOnlyDictionary<ActorId, CollisionHazardData> _collisionHazards;
    private readonly IReadOnlyDictionary<ActorId, TrafficLightFrame> _trafficLightFrames;

    // PID gain triples (Kp, Ki, Kd). Switched per-vehicle based on speed.
    private readonly float[] _urbanLongitudinalParameters;
    private readonly float[] _highwayLongitudinalParameters;
    private readonly float[] _urbanLateralParameters;
    private readonly float[] _highwayLateralParameters;

    // ── Per-actor state carried across ticks ─────────────────────────────
    private readonly Dictionary<ActorId, StateEntry> _pidStateMap = new();
    private readonly Dictionary<ActorId, double> _teleportationInstance = new();

    // ── Output map (cleared and re-populated each tick by the facade) ────
    private readonly Dictionary<ActorId, Command> _output = new();

    // ── Current world timestamp (in seconds). Pushed by the TM facade
    //    before each per-vehicle pass.
    private double _currentTimestamp;

    // ── Constants imported for terseness ─────────────────────────────────
    private const float HIGHWAY_SPEED = Constants.SpeedThreshold.HIGHWAY_SPEED;
    private const float AFTER_JUNCTION_MIN_SPEED = Constants.SpeedThreshold.AFTER_JUNCTION_MIN_SPEED;
    private const float TARGET_WAYPOINT_TIME_HORIZON = Constants.WaypointSelection.TARGET_WAYPOINT_TIME_HORIZON;
    private const float MIN_TARGET_WAYPOINT_DISTANCE = Constants.WaypointSelection.MIN_TARGET_WAYPOINT_DISTANCE;
    private const float MIN_SAFE_INTERVAL_LENGTH = Constants.WaypointSelection.MIN_SAFE_INTERVAL_LENGTH;
    private const float RELATIVE_APPROACH_SPEED = Constants.MotionPlan.RELATIVE_APPROACH_SPEED;
    private const float MIN_FOLLOW_LEAD_DISTANCE = Constants.MotionPlan.MIN_FOLLOW_LEAD_DISTANCE;
    private const float CRITICAL_BRAKING_MARGIN = Constants.MotionPlan.CRITICAL_BRAKING_MARGIN;
    private const float EPSILON_RELATIVE_SPEED = Constants.MotionPlan.EPSILON_RELATIVE_SPEED;
    private const float MAX_JUNCTION_BLOCK_DISTANCE = Constants.MotionPlan.MAX_JUNCTION_BLOCK_DISTANCE;
    private const float LANDMARK_DETECTION_TIME = Constants.MotionPlan.LANDMARK_DETECTION_TIME;
    private const float TL_TARGET_VELOCITY = Constants.MotionPlan.TL_TARGET_VELOCITY;
    private const float STOP_TARGET_VELOCITY = Constants.MotionPlan.STOP_TARGET_VELOCITY;
    private const float YIELD_TARGET_VELOCITY = Constants.MotionPlan.YIELD_TARGET_VELOCITY;
    private const float FRICTION = Constants.MotionPlan.FRICTION;
    private const float GRAVITY = Constants.MotionPlan.GRAVITY;
    private const float PI = Constants.MotionPlan.PI;
    private const float PERC_MAX_SLOWDOWN = Constants.MotionPlan.PERC_MAX_SLOWDOWN;
    private const float FOLLOW_LEAD_FACTOR = Constants.MotionPlan.FOLLOW_LEAD_FACTOR;
    private const float FOLLOW_LEAD_BASELINE = MIN_FOLLOW_LEAD_DISTANCE;
    private const float EPSILON = Constants.Collision.EPSILON;
    private const float HYBRID_MODE_DT = Constants.HybridMode.HYBRID_MODE_DT_FL;
    private const ushort ATTEMPTS_TO_TELEPORT = Constants.MotionPlan.ATTEMPTS_TO_TELEPORT;

    public MotionPlanStage(
        SimulationState simulationState,
        BufferMap bufferMap,
        TrackTraffic trackTraffic,
        Parameters parameters,
        InMemoryMap localMap,
        RandomGenerator rng,
        IReadOnlyDictionary<ActorId, CollisionHazardData> collisionHazards,
        IReadOnlyDictionary<ActorId, TrafficLightFrame> trafficLightFrames,
        IReadOnlyDictionary<ActorId, LocalizationData> localizationOutput,
        float[]? urbanLongitudinalParameters = null,
        float[]? highwayLongitudinalParameters = null,
        float[]? urbanLateralParameters = null,
        float[]? highwayLateralParameters = null)
    {
        _simulationState = simulationState;
        _bufferMap = bufferMap;
        _trackTraffic = trackTraffic;
        _parameters = parameters;
        _localMap = localMap;
        _rng = rng;
        _collisionHazards = collisionHazards;
        _trafficLightFrames = trafficLightFrames;
        _localizationOutput = localizationOutput;
        // Default gains match Constants.PID.* arrays.
        _urbanLongitudinalParameters = urbanLongitudinalParameters ?? Constants.PID.LONGITUDINAL_PARAM;
        _highwayLongitudinalParameters = highwayLongitudinalParameters ?? Constants.PID.LONGITUDINAL_HIGHWAY_PARAM;
        _urbanLateralParameters = urbanLateralParameters ?? Constants.PID.LATERAL_PARAM;
        _highwayLateralParameters = highwayLateralParameters ?? Constants.PID.LATERAL_HIGHWAY_PARAM;
    }

    /// <summary>
    /// Snapshot of the per-tick actuation commands keyed by actor. Cleared
    /// (by the TM facade) and re-populated each tick.
    /// </summary>
    public IReadOnlyDictionary<ActorId, Command> GetOutput() => _output;

    /// <summary>
    /// Push the current world timestamp (seconds since simulator start).
    /// Upstream reads this on every Update via <c>cc::World.GetSnapshot</c>;
    /// since we don't carry a World reference, the TM facade does the work.
    /// </summary>
    public void UpdateCurrentTimestamp(double seconds) => _currentTimestamp = seconds;

    /// <summary>Drop per-actor PID and teleport-timer state.</summary>
    public void RemoveActor(ActorId actorId)
    {
        _pidStateMap.Remove(actorId);
        _teleportationInstance.Remove(actorId);
        _output.Remove(actorId);
    }

    /// <summary>Wipe every per-actor cache. Called on TM shutdown / reset.</summary>
    public void Reset()
    {
        _pidStateMap.Clear();
        _teleportationInstance.Clear();
        _output.Clear();
    }

    /// <summary>
    /// Plan one tick for <paramref name="actorId"/>. Mirrors
    /// <c>MotionPlanStage::Update</c> step-for-step.
    /// </summary>
    public void Update(ActorId actorId)
    {
        Location vehicleLocation = _simulationState.GetLocation(actorId);
        Vector3D vehicleVelocity = _simulationState.GetVelocity(actorId);
        Rotation vehicleRotation = _simulationState.GetRotation(actorId);
        float vehicleSpeed = Length(vehicleVelocity);
        Vector3D vehicleHeading = _simulationState.GetHeading(actorId);
        bool physicsEnabled = _simulationState.IsPhysicsEnabled(actorId);
        if (!_bufferMap.TryGetValue(actorId, out var waypointBuffer) || waypointBuffer.Count == 0)
            return;

        // What speed is this vehicle allowed to do here? Prefer what the road itself declares: the
        // OpenDRIVE <speed> record on the lane the vehicle is on, cached at graph-build time so
        // reading it costs a field access rather than an RPC.
        //
        // The simulator's own per-actor speed limit is only a fallback, because it is derived from
        // speed-limit SIGN actors that the vehicle has driven past. A world generated from OSM has
        // no such signs, so that value never leaves its 30 km/h default and every vehicle on the map
        // crawled at 8.3 m/s, motorways included, while the map itself declared 29 m/s for them.
        float speedLimit = waypointBuffer[0].SpeedLimitKph;
        if (speedLimit <= 0f) speedLimit = _simulationState.GetSpeedLimit(actorId);
        // Neither the road nor the simulator declares one: an urban default, so a vehicle on an
        // unposted road still moves.
        if (speedLimit <= 0f) speedLimit = 30f;

        // Sibling stage outputs — gracefully default if a sibling stage
        // hasn't filled an entry this tick (e.g. before the very first tick
        // has run end-to-end).
        if (!_localizationOutput.TryGetValue(actorId, out var localization))
            localization = default;
        if (!_collisionHazards.TryGetValue(actorId, out var collisionHazard))
            collisionHazard = default;
        bool tlHazard = _trafficLightFrames.TryGetValue(actorId, out var tl) && tl.HazardFlag;

        Transform teleportationTransform = new(vehicleLocation, vehicleRotation);

        Location heroLocation = _trackTraffic.GetHeroLocation();
        bool isHeroAlive = !(heroLocation.X == 0f && heroLocation.Y == 0f && heroLocation.Z == 0f);

        // ─── Dormant + respawn mode → teleport ───────────────────────────
        if (_simulationState.IsDormant(actorId)
            && _parameters.GetRespawnDormantVehicles()
            && isHeroAlive)
        {
            _pidStateMap[actorId] = new StateEntry(_currentTimestamp, 0f, 0f, 0f);

            if (!_teleportationInstance.ContainsKey(actorId))
                _teleportationInstance[actorId] = _currentTimestamp;

            float lower = _parameters.GetLowerBoundaryRespawnDormantVehicles();
            float upper = _parameters.GetUpperBoundaryRespawnDormantVehicles();
            float dilateFactor = (upper - lower) / 100.0f;
            double elapsed = _currentTimestamp - _teleportationInstance[actorId];

            if (_parameters.GetSynchronousMode() || elapsed > HYBRID_MODE_DT)
            {
                float randomSample = (float)(_rng.Next() * dilateFactor) + lower;
                var teleportCandidates = _localMap.GetWaypointsInDelta(
                    heroLocation, ATTEMPTS_TO_TELEPORT, randomSample);
                for (int i = 0; i < teleportCandidates.Count; ++i)
                {
                    var teleportWaypoint = teleportCandidates[i];
                    GeoGridId gridId = teleportWaypoint.GetGeodesicGridId();
                    if (_trackTraffic.IsGeoGridFree(gridId))
                    {
                        Location tloc = teleportWaypoint.Location;
                        tloc = new Location(tloc.X, tloc.Y, tloc.Z + 0.5f);
                        // We only have ForwardVector on SimpleWaypoint, not full
                        // Rotation. The vehicle's own rotation is preserved by
                        // upstream when it doesn't have the destination Rotation
                        // either; the kinematic state below uses the synthesised
                        // transform so the next ALSM tick re-reads the vehicle's
                        // pose anyway.
                        teleportationTransform = new Transform(tloc, vehicleRotation);
                        _trackTraffic.AddTakenGrid(gridId, actorId);
                        break;
                    }
                }
            }
            _output[actorId] = new ApplyTransformCommand(actorId, teleportationTransform);

            var kinematic = new KinematicState
            {
                Location = teleportationTransform.Location,
                Rotation = teleportationTransform.Rotation,
                Velocity = vehicleVelocity,
                SpeedLimit = speedLimit,
                PhysicsEnabled = physicsEnabled,
                IsDormant = _simulationState.IsDormant(actorId),
                HybridEndLocation = teleportationTransform.Location,
            };
            _simulationState.UpdateKinematicState(actorId, kinematic);
            return;
        }

        // ─── Normal flow: PID-driven control or hybrid teleport ──────────

        // Target velocity in m/s.
        float maxTargetVelocity = _parameters.GetVehicleTargetVelocity(actorId, speedLimit) / 3.6f;

        // Reduce around landmarks (TL, stop, yield, speed-limit sign).
        float maxLandmarkTargetVelocity = GetLandmarkTargetVelocity(
            waypointBuffer[0], vehicleLocation, actorId, maxTargetVelocity);

        // Reduce around upcoming turns (curvature-based).
        float maxTurnTargetVelocity = GetTurnTargetVelocity(waypointBuffer, maxTargetVelocity);
        maxTargetVelocity = MathF.Min(MathF.Min(maxTargetVelocity, maxLandmarkTargetVelocity),
                                       maxTurnTargetVelocity);

        // Collision handling.
        var (collisionEmergencyStop, dynamicTargetVelocity) = CollisionHandling(
            collisionHazard, tlHazard, vehicleVelocity, vehicleHeading, maxTargetVelocity);

        // Junction-blocked check.
        bool safeAfterJunction = SafeAfterJunction(localization, tlHazard, collisionEmergencyStop);
        bool emergencyStop = tlHazard || collisionEmergencyStop || !safeAfterJunction;

        if (physicsEnabled && !_simulationState.IsDormant(actorId))
        {
            // ── Pure-pursuit-style target point ──────────────────────────
            float targetPointDistance = MathF.Max(
                vehicleSpeed * TARGET_WAYPOINT_TIME_HORIZON,
                MIN_TARGET_WAYPOINT_DISTANCE);
            var targetWaypoint = GetTargetWaypoint(waypointBuffer, targetPointDistance).Waypoint;
            Location targetLocation = targetWaypoint.Location;

            // Apply per-actor lateral lane offset. Upstream uses
            // target_waypoint->GetTransform().GetRightVector(); we derive
            // the same vector from the cached forward vector since on a
            // flat road the right vector is (forward.Y, -forward.X, 0).
            float offset = _parameters.GetLaneOffset(actorId);
            Vector3D forward = targetWaypoint.ForwardVector;
            // CARLA's GetRightVector @ pitch=roll=0 reduces to (-sin(yaw),
            // cos(yaw), 0). With forward = (cos(yaw), sin(yaw), 0) on the
            // flat road, that's (-forward.Y, forward.X, 0).
            Vector3D rightVector = new(-forward.Y, forward.X, 0f);
            targetLocation = new Location(
                targetLocation.X + offset * rightVector.X,
                targetLocation.Y + offset * rightVector.Y,
                targetLocation.Z);

            float dotProduct = DeviationDotProduct(vehicleLocation, vehicleHeading, targetLocation);
            float crossProduct = DeviationCrossProduct(vehicleLocation, vehicleHeading, targetLocation);
            // acos(dot)/π normalises [0, π] to [0, 1]; signed via cross.
            dotProduct = MathF.Acos(dotProduct) / PI;
            if (crossProduct < 0.0f) dotProduct *= -1.0f;
            float angularDeviation = dotProduct;
            float velocityDeviation = dynamicTargetVelocity == 0f
                ? 0f
                : (dynamicTargetVelocity - vehicleSpeed) / dynamicTargetVelocity;

            if (!_pidStateMap.TryGetValue(actorId, out var prevState))
            {
                prevState = new StateEntry(_currentTimestamp, 0f, 0f, 0f);
                _pidStateMap[actorId] = prevState;
            }

            // Choose PID parameters by current speed regime.
            ReadOnlySpan<float> longitudinal;
            ReadOnlySpan<float> lateral;
            if (vehicleSpeed > HIGHWAY_SPEED)
            {
                longitudinal = _highwayLongitudinalParameters;
                lateral = _highwayLateralParameters;
            }
            else
            {
                longitudinal = _urbanLongitudinalParameters;
                lateral = _urbanLateralParameters;
            }

            var currentState = new StateEntry(_currentTimestamp, angularDeviation, velocityDeviation, 0f);
            ActuationSignal actuation = PIDController.RunStep(
                currentState, prevState, longitudinal, lateral);

            if (emergencyStop)
            {
                actuation = new ActuationSignal(0f, 1f, actuation.Steer);
            }

            // Pack as ApplyVehicleControl. Field order on VehicleControl is
            // (throttle, steer, brake, ...) — not (throttle, brake, steer).
            var vehicleControl = new VehicleControl(
                Throttle: actuation.Throttle,
                Steer: actuation.Steer,
                Brake: actuation.Brake,
                HandBrake: false,
                Reverse: false,
                ManualGearShift: false,
                Gear: 0);
            _output[actorId] = new ApplyVehicleControlCommand(actorId, vehicleControl);

            // Update PID state with the final steer (post-clamp + emergency).
            _pidStateMap[actorId] = currentState with { Steer = actuation.Steer };
        }
        else
        {
            // ── Physics-less / hybrid teleportation path ─────────────────
            _pidStateMap[actorId] = new StateEntry(_currentTimestamp, 0f, 0f, 0f);

            if (!_teleportationInstance.ContainsKey(actorId))
                _teleportationInstance[actorId] = _currentTimestamp;
            double elapsed = _currentTimestamp - _teleportationInstance[actorId];

            if (!emergencyStop
                && (_parameters.GetSynchronousMode() || elapsed > HYBRID_MODE_DT))
            {
                float targetDisplacement = dynamicTargetVelocity * HYBRID_MODE_DT;
                SimpleWaypoint teleportTarget = waypointBuffer[0];
                Location targetBaseLocation = teleportTarget.Location;
                Vector3D targetHeading = teleportTarget.ForwardVector;
                Vector3D correctHeading = MakeSafeUnitVector(
                    new Vector3D(
                        targetBaseLocation.X - vehicleLocation.X,
                        targetBaseLocation.Y - vehicleLocation.Y,
                        targetBaseLocation.Z - vehicleLocation.Z),
                    EPSILON);

                Location teleportLocation;
                if (Distance(vehicleLocation, targetBaseLocation) < targetDisplacement)
                {
                    Vector3D unitHeading = MakeSafeUnitVector(targetHeading, EPSILON);
                    teleportLocation = new Location(
                        vehicleLocation.X + unitHeading.X * targetDisplacement,
                        vehicleLocation.Y + unitHeading.Y * targetDisplacement,
                        vehicleLocation.Z + unitHeading.Z * targetDisplacement);
                }
                else
                {
                    teleportLocation = new Location(
                        vehicleLocation.X + correctHeading.X * targetDisplacement,
                        vehicleLocation.Y + correctHeading.Y * targetDisplacement,
                        vehicleLocation.Z + correctHeading.Z * targetDisplacement);
                }
                // Use the *target* waypoint's rotation? Upstream uses
                // target_base_transform.rotation but we don't carry a rotation
                // on SimpleWaypoint. Fall back to the vehicle's current
                // rotation; ALSM will re-read the pose next tick.
                teleportationTransform = new Transform(teleportLocation, vehicleRotation);
            }
            else
            {
                teleportationTransform = new Transform(vehicleLocation, vehicleRotation);
            }

            _output[actorId] = new ApplyTransformCommand(actorId, teleportationTransform);
            _simulationState.UpdateKinematicHybridEndLocation(actorId, teleportationTransform.Location);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //                          Helpers (private)
    // ═════════════════════════════════════════════════════════════════════

    private bool SafeAfterJunction(LocalizationData localization, bool tlHazard, bool collisionEmergencyStop)
    {
        SimpleWaypoint? junctionEnd = localization.JunctionEndPoint;
        SimpleWaypoint? safePoint = localization.SafePoint;
        bool safeAfter = true;

        if (!tlHazard && !collisionEmergencyStop
            && localization.IsAtJunctionEntrance
            && junctionEnd != null && safePoint != null
            && junctionEnd.DistanceSquared(safePoint) > MIN_SAFE_INTERVAL_LENGTH * MIN_SAFE_INTERVAL_LENGTH)
        {
            var passingSafe = _trackTraffic.GetPassingVehicles(safePoint.GetId());
            var passingJunctionEnd = _trackTraffic.GetPassingVehicles(junctionEnd.GetId());
            Location midPoint = new(
                (junctionEnd.Location.X + safePoint.Location.X) * 0.5f,
                (junctionEnd.Location.Y + safePoint.Location.Y) * 0.5f,
                (junctionEnd.Location.Z + safePoint.Location.Z) * 0.5f);

            // Vehicles in passingSafe but NOT in passingJunctionEnd —
            // i.e. stopped *past* the junction.
            foreach (ActorId blocking in passingSafe)
            {
                if (passingJunctionEnd.Contains(blocking)) continue;
                Location blockingLoc = _simulationState.GetLocation(blocking);
                Vector3D blockingVel = _simulationState.GetVelocity(blocking);
                if (DistanceSquared(blockingLoc, midPoint) < MAX_JUNCTION_BLOCK_DISTANCE * MAX_JUNCTION_BLOCK_DISTANCE
                    && SquaredLength(blockingVel) < AFTER_JUNCTION_MIN_SPEED * AFTER_JUNCTION_MIN_SPEED)
                {
                    safeAfter = false;
                    break;
                }
            }
        }

        return safeAfter;
    }

    private (bool EmergencyStop, float DynamicTargetVelocity) CollisionHandling(
        CollisionHazardData collisionHazard, bool tlHazard,
        Vector3D vehicleVelocity, Vector3D vehicleHeading, float maxTargetVelocity)
    {
        bool emergencyStop = false;
        float dynamicTargetVelocity = maxTargetVelocity;
        float vehicleSpeed = Length(vehicleVelocity);

        if (collisionHazard.Hazard && !tlHazard)
        {
            ActorId otherActorId = collisionHazard.HazardActorId;
            // Defensive: if the hazard partner already vanished from sim state,
            // skip it. Upstream relies on ALSM to keep the maps consistent.
            if (_simulationState.ContainsActor(otherActorId))
            {
                Vector3D otherVelocity = _simulationState.GetVelocity(otherActorId);
                Vector3D relVel = new(
                    vehicleVelocity.X - otherVelocity.X,
                    vehicleVelocity.Y - otherVelocity.Y,
                    vehicleVelocity.Z - otherVelocity.Z);
                float vehicleRelativeSpeed = Length(relVel);
                float availableDistanceMargin = collisionHazard.AvailableDistanceMargin;
                float otherSpeedAlongHeading = Dot(otherVelocity, vehicleHeading);

                if (vehicleRelativeSpeed > EPSILON_RELATIVE_SPEED)
                {
                    float followLeadDistance = FOLLOW_LEAD_FACTOR * vehicleSpeed + FOLLOW_LEAD_BASELINE;
                    if (availableDistanceMargin > followLeadDistance)
                    {
                        dynamicTargetVelocity = otherSpeedAlongHeading;
                    }
                    else if (availableDistanceMargin > CRITICAL_BRAKING_MARGIN)
                    {
                        dynamicTargetVelocity = MathF.Max(otherSpeedAlongHeading, RELATIVE_APPROACH_SPEED);
                    }
                    else
                    {
                        emergencyStop = true;
                    }
                }
                if (availableDistanceMargin < CRITICAL_BRAKING_MARGIN)
                {
                    emergencyStop = true;
                }
            }
        }

        // Rate-limit the slowdown so we never decelerate by more than
        // PERC_MAX_SLOWDOWN of current speed per tick.
        float maxGradualVelocity = PERC_MAX_SLOWDOWN * vehicleSpeed;
        if (dynamicTargetVelocity < vehicleSpeed - maxGradualVelocity)
        {
            dynamicTargetVelocity = vehicleSpeed - maxGradualVelocity;
        }
        dynamicTargetVelocity = MathF.Min(maxTargetVelocity, dynamicTargetVelocity);

        return (emergencyStop, dynamicTargetVelocity);
    }

    /// <summary>
    /// Returns the minimum velocity allowed by upcoming traffic landmarks
    /// (TL, stop, yield, speed-limit sign). Upstream queries the full
    /// landmark list via <c>client::Waypoint.GetAllLandmarksInDistance</c>;
    /// we don't have that client API yet, so this returns
    /// <see cref="float.MaxValue"/> (effectively unlimited). The TM
    /// architecture still uses <see cref="TrafficLightStage"/> separately
    /// to enforce hard stops at red lights — landmarks here are *gradual*
    /// slowdowns, so this conservative default just keeps the v1 build
    /// behaving naturally until Wave 4 wires the OpenDRIVE signal layer.
    /// </summary>
    private float GetLandmarkTargetVelocity(
        SimpleWaypoint waypoint, Location vehicleLocation, ActorId actorId, float maxTargetVelocity)
    {
        // No landmark access surface on SimpleWaypoint yet — see XML doc.
        _ = waypoint; _ = vehicleLocation; _ = actorId; _ = maxTargetVelocity;
        return float.MaxValue;
    }

    /// <summary>
    /// 3-point-circle curvature speed: scan first/mid/last waypoints in
    /// the buffer, compute the inscribed circle's radius, and return the
    /// maximum centripetal-acceleration-bounded speed
    /// <c>√(r × friction × g)</c>. Mirrors upstream verbatim.
    /// </summary>
    private static float GetTurnTargetVelocity(WaypointBuffer waypointBuffer, float maxTargetVelocity)
    {
        if (waypointBuffer.Count < 3) return maxTargetVelocity;

        var first = waypointBuffer[0];
        var last = waypointBuffer[^1];
        var middle = waypointBuffer[waypointBuffer.Count / 2];
        float radius = GetThreePointCircleRadius(first.Location, middle.Location, last.Location);
        return MathF.Sqrt(radius * FRICTION * GRAVITY);
    }

    /// <summary>
    /// Circumscribed circle radius of three 2-D points. Mirrors upstream
    /// arithmetic exactly (line-by-line). Returns
    /// <see cref="float.MaxValue"/> for collinear inputs.
    /// </summary>
    private static float GetThreePointCircleRadius(Location first, Location middle, Location last)
    {
        float x1 = first.X, y1 = first.Y;
        float x2 = middle.X, y2 = middle.Y;
        float x3 = last.X, y3 = last.Y;

        float x12 = x1 - x2;
        float x13 = x1 - x3;
        float y12 = y1 - y2;
        float y13 = y1 - y3;
        float y31 = y3 - y1;
        float y21 = y2 - y1;
        float x31 = x3 - x1;
        float x21 = x2 - x1;

        float sx13 = x1 * x1 - x3 * x3;
        float sy13 = y1 * y1 - y3 * y3;
        float sx21 = x2 * x2 - x1 * x1;
        float sy21 = y2 * y2 - y1 * y1;

        float fDenom = 2f * (y31 * x12 - y21 * x13);
        if (fDenom == 0f) return float.MaxValue;
        float f = (sx13 * x12 + sy13 * x12 + sx21 * x13 + sy21 * x13) / fDenom;

        float gDenom = 2f * (x31 * y12 - x21 * y13);
        if (gDenom == 0f) return float.MaxValue;
        float g = (sx13 * y12 + sy13 * y12 + sx21 * y13 + sy21 * y13) / gDenom;

        float c = -(x1 * x1 + y1 * y1) - 2f * g * x1 - 2f * f * y1;
        float h = -g;
        float k = -f;
        return MathF.Sqrt(h * h + k * k - c);
    }

    // ═════════════════════════════════════════════════════════════════════
    //                          Math primitives
    // ═════════════════════════════════════════════════════════════════════

    private static float Length(Vector3D v)
        => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static float SquaredLength(Vector3D v)
        => v.X * v.X + v.Y * v.Y + v.Z * v.Z;

    private static float Dot(Vector3D a, Vector3D b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static float DistanceSquared(Location a, Location b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float Distance(Location a, Location b)
        => MathF.Sqrt(DistanceSquared(a, b));

    private static Vector3D MakeSafeUnitVector(Vector3D v, float epsilon)
    {
        float length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        float k = (length > MathF.Max(epsilon, 0f)) ? (1f / length) : 1f;
        return new Vector3D(v.X * k, v.Y * k, v.Z * k);
    }
}
