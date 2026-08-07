// Source: carla/trafficmanager/LocalizationStage.{h,cpp}
//
// Per-vehicle horizon planner. For each registered vehicle, this stage:
//
//   1. Identifies the closest waypoint to the vehicle's current location
//      (via <see cref="InMemoryMap.GetWaypoint"/>) when its buffer is empty
//      or it has drifted off the head of the buffer.
//   2. Maintains a deque-like waypoint horizon (front = vehicle, back =
//      lookahead) of roughly <c>speed × HORIZON_RATE</c> metres ahead.
//   3. Pops consumed waypoints (those behind the vehicle by dot-product).
//   4. Pops excess waypoints from the back when the horizon shrank (e.g.
//      after a speed drop) — unless we're rolling through a junction.
//   5. Considers a lane change (auto / forced / random / keep-right) and
//      rebuilds the buffer from the lane-change point if one is chosen.
//   6. Imports a user-supplied Path or Route if one is queued; otherwise
//      extends the buffer along the road graph using the random selector.
//   7. Computes "safe space after a junction" (junction end-point + safe
//      point) and writes it back into <see cref="LocalizationData"/>.
//   8. Updates the <see cref="TrackTraffic"/> geodesic-grid index for the
//      actor's new buffer.
//
// PERF: Buffer is a plain <c>List&lt;SimpleWaypoint&gt;</c>. Upstream uses
// std::deque but at the buffer sizes TM works with, the O(n) RemoveAt(0)
// cost is negligible (and saves the LinkedList allocation overhead). That
// holds only because every walk that fills the buffer is bounded — see
// MaxHorizonWalkSteps. Per-tick allocations are limited to a handful of
// HashSet snapshots in lane-change and cycle detection — none on the
// steady-state hot path.
//
// Hot path: Update(actorId) is called once per registered vehicle per tick
// (≈ 50× per tick at the design target). Output dictionary is cleared and
// re-populated each tick — no incremental delta tracking.
#nullable enable

using static CarlaNet.TrafficManager.LocalizationUtils;

namespace CarlaNet.TrafficManager.Stages;

// ─────────────────────────────────────────────────────────────────────────
// The per-vehicle waypoint horizon buffer types (<c>WaypointBuffer</c> +
// <c>BufferMap</c>) and the <see cref="IStageWithRemoveActor"/> contract
// live in ALSM.cs (sibling Wave-3 agent's territory). We just consume them.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-vehicle horizon planner. Mirrors
/// <c>traffic_manager::LocalizationStage</c>.
/// </summary>
internal sealed class LocalizationStage : IStageWithRemoveActor
{
    // ── Constructor-injected dependencies ────────────────────────────────
    private readonly SimulationState _simulationState;
    private readonly BufferMap _bufferMap;
    private readonly TrackTraffic _trackTraffic;
    private readonly Parameters _parameters;
    private readonly InMemoryMap _localMap;
    private readonly RandomGenerator _rng;
    // Optional sink for vehicles that walked off the end of the road graph
    // and need to be unregistered by the TM facade. Upstream's analog is
    // the `marked_for_removal` vector reference passed by the TM.
    private readonly List<ActorId>? _markedForRemoval;
    // Watches whether vehicles given a precomputed route are still on it. Optional so a test can
    // drive this stage without one; the orchestrator always supplies it. A vehicle that was never
    // given a route costs one dictionary miss per tick and nothing else.
    private readonly RouteSupervisor? _routeSupervisor;

    // ── Per-actor state carried across ticks ─────────────────────────────
    private readonly Dictionary<ActorId, SimpleWaypoint> _lastLaneChangeSwpt = new();
    private readonly Dictionary<ActorId, (SimpleWaypoint? End, SimpleWaypoint? Safe)>
        _vehiclesAtJunctionEntrance = new();
    private readonly HashSet<ActorId> _vehiclesAtJunction = new();

    // ── Output map (cleared and re-populated each tick by the facade) ────
    private readonly Dictionary<ActorId, LocalizationData> _output = new();

    // ── Constants imported for terseness ─────────────────────────────────
    private const float HORIZON_RATE = Constants.PathBufferUpdate.HORIZON_RATE;
    private const float HIGH_SPEED_HORIZON_RATE = Constants.PathBufferUpdate.HIGH_SPEED_HORIZON_RATE;
    private const float MINIMUM_HORIZON_LENGTH = Constants.PathBufferUpdate.MINIMUM_HORIZON_LENGTH;
    private const float MAX_START_DISTANCE = Constants.PathBufferUpdate.MAX_START_DISTANCE;
    private const float HIGHWAY_SPEED = Constants.SpeedThreshold.HIGHWAY_SPEED;
    private const float JUNCTION_LOOK_AHEAD = Constants.WaypointSelection.JUNCTION_LOOK_AHEAD;
    private const float SAFE_DISTANCE_AFTER_JUNCTION = Constants.WaypointSelection.SAFE_DISTANCE_AFTER_JUNCTION;
    private const float MIN_JUNCTION_LENGTH = Constants.WaypointSelection.MIN_JUNCTION_LENGTH;
    private const float MIN_LANE_CHANGE_SPEED = Constants.LaneChange.MIN_LANE_CHANGE_SPEED;
    private const float FIFTYPERC = Constants.LaneChange.FIFTYPERC;
    private const float INTER_LANE_CHANGE_DISTANCE = Constants.LaneChange.INTER_LANE_CHANGE_DISTANCE;
    private const float MINIMUM_LANE_CHANGE_DISTANCE = Constants.LaneChange.MINIMUM_LANE_CHANGE_DISTANCE;
    private const float MAXIMUM_LANE_OBSTACLE_DISTANCE = Constants.LaneChange.MAXIMUM_LANE_OBSTACLE_DISTANCE;
    private const float MAXIMUM_LANE_OBSTACLE_CURVATURE = Constants.LaneChange.MAXIMUM_LANE_OBSTACLE_CURVATURE;
    private const float MIN_WPT_DISTANCE = Constants.LaneChange.MIN_WPT_DISTANCE;
    private const float MAX_WPT_DISTANCE = Constants.LaneChange.MAX_WPT_DISTANCE;

    // Same right-hand-traffic flag default carla::client::Waypoint::IsRHT()
    // returns for the standard CARLA maps. The upstream IsRHT() RPC call is
    // bound to the client::Waypoint which we don't replicate here — Wave 3G
    // can wire a real lookup through SimpleWaypoint if needed.
    private const bool ASSUME_RHT = true;

    // Upper bound on how many waypoints a single horizon walk may visit.
    //
    // Every walk in this stage follows the road graph forward until it is "far enough" — either the
    // buffer spans the horizon, or a probe reaches a junction or a distance threshold. Those tests
    // all measure STRAIGHT-LINE distance, so a cyclic road graph satisfies none of them: a loop ramp
    // or roundabout returns the walk to where it began without it ever getting far from the start.
    // Unbounded, the horizon buffer then grows until the process runs out of memory.
    //
    // The horizon is at most a few hundred metres and waypoints are MAP_RESOLUTION apart, so a
    // legitimate walk uses well under a hundred steps even on a curved road, where path length far
    // exceeds endpoint separation. This bound is an order of magnitude above that: it never truncates
    // a real horizon, and it caps the pathological case at a harmless buffer instead of the heap.
    private const int MaxHorizonWalkSteps = 512;

    // How little road may remain ahead of a vehicle before it counts as having run out of it.
    //
    // The horizon runs speed x HORIZON_RATE ahead of the vehicle, so a walk that finds a waypoint
    // with no successors has discovered where the road graph ENDS, not where the vehicle is. Acting
    // on that directly destroys a vehicle that is still a hundred metres short of the end and
    // driving perfectly well, and the faster the vehicle the further ahead it looks, so raising
    // traffic to motorway speeds made it destroy nearly everything it spawned. Only a vehicle whose
    // remaining buffer is this short has actually arrived at the end of the drivable network.
    private const int MinBufferAtRoadEnd = 3;

    public LocalizationStage(
        SimulationState simulationState,
        BufferMap bufferMap,
        TrackTraffic trackTraffic,
        Parameters parameters,
        InMemoryMap localMap,
        RandomGenerator rng,
        List<ActorId>? markedForRemoval = null,
        RouteSupervisor? routeSupervisor = null)
    {
        _simulationState = simulationState;
        _bufferMap = bufferMap;
        _trackTraffic = trackTraffic;
        _parameters = parameters;
        _localMap = localMap;
        _rng = rng;
        _markedForRemoval = markedForRemoval;
        _routeSupervisor = routeSupervisor;
    }

    /// <summary>Snapshot of the per-tick localization output indexed by actor.</summary>
    public IReadOnlyDictionary<ActorId, LocalizationData> GetOutput() => _output;

    /// <summary>
    /// Flag a vehicle for removal only if it has genuinely reached the end of the road network,
    /// rather than merely looked far enough ahead to see one. See <see cref="MinBufferAtRoadEnd"/>.
    /// </summary>
    private void MarkIfOutOfRoad(ActorId actorId, WaypointBuffer waypointBuffer)
    {
        if (waypointBuffer.Count > MinBufferAtRoadEnd) return;
        _markedForRemoval?.Add(actorId);
    }

    /// <summary>Drop per-actor state. Called by the TM facade on vehicle destroy.</summary>
    public void RemoveActor(ActorId actorId)
    {
        _lastLaneChangeSwpt.Remove(actorId);
        _vehiclesAtJunction.Remove(actorId);
        _vehiclesAtJunctionEntrance.Remove(actorId);
        _output.Remove(actorId);
        // A destroyed vehicle is gone for good — there is no route left to follow or resume.
        _routeSupervisor?.RemoveActor(actorId);
    }

    /// <summary>Wipe every per-actor cache. Called on TM shutdown / reset.</summary>
    public void Reset()
    {
        _lastLaneChangeSwpt.Clear();
        _vehiclesAtJunction.Clear();
        _vehiclesAtJunctionEntrance.Clear();
        _output.Clear();
        _routeSupervisor?.Reset();
    }

    /// <summary>
    /// Plan one tick for <paramref name="actorId"/>. Mirrors
    /// <c>LocalizationStage::Update</c> step-for-step.
    /// </summary>
    public void Update(ActorId actorId)
    {
        // ─── Vehicle state snapshot ──────────────────────────────────────
        Location vehicleLocation = _simulationState.GetLocation(actorId);
        Vector3D headingVector = _simulationState.GetHeading(actorId);
        Vector3D velocityVector = _simulationState.GetVelocity(actorId);
        float vehicleSpeed = Length(velocityVector);

        // ─── Speed-dependent horizon length ──────────────────────────────
        float horizonLength = MathF.Max(vehicleSpeed * HORIZON_RATE, MINIMUM_HORIZON_LENGTH);
        if (vehicleSpeed > HIGHWAY_SPEED)
        {
            horizonLength = MathF.Max(vehicleSpeed * HIGH_SPEED_HORIZON_RATE, MINIMUM_HORIZON_LENGTH);
        }
        float horizonSquare = horizonLength * horizonLength;

        // ─── Lazily create the actor's buffer ────────────────────────────
        if (!_bufferMap.TryGetValue(actorId, out var waypointBuffer))
        {
            waypointBuffer = new WaypointBuffer();
            _bufferMap[actorId] = waypointBuffer;
        }

        // ─── Recovery: if drifted too far, blow away the whole buffer ────
        if (waypointBuffer.Count > 0 &&
            DistanceSquared(waypointBuffer[0].Location, vehicleLocation) > MAX_START_DISTANCE * MAX_START_DISTANCE)
        {
            int numberOfPops = waypointBuffer.Count;
            for (int j = 0; j < numberOfPops; ++j)
            {
                PopWaypoint(actorId, _trackTraffic, waypointBuffer);
            }
        }

        // ─── Trim consumed waypoints + detect junction entrance ──────────
        bool isAtJunctionEntrance = false;
        if (waypointBuffer.Count > 0)
        {
            // Pop everything behind the vehicle (dot product < 0 == behind).
            float dot = DeviationDotProduct(vehicleLocation, headingVector, waypointBuffer[0].Location);
            while (dot <= 0.0f && waypointBuffer.Count > 0)
            {
                PopWaypoint(actorId, _trackTraffic, waypointBuffer);
                if (waypointBuffer.Count > 0)
                {
                    dot = DeviationDotProduct(vehicleLocation, headingVector, waypointBuffer[0].Location);
                }
            }

            if (waypointBuffer.Count > 0)
            {
                // We're at the entrance of a junction if the front waypoint is NOT
                // in a junction but the lookahead point IS, OR the front IS in a
                // junction but the previous waypoint was NOT.
                SimpleWaypoint lookAhead = GetTargetWaypoint(waypointBuffer, JUNCTION_LOOK_AHEAD).Waypoint;
                SimpleWaypoint frontWaypoint = waypointBuffer[0];
                bool frontIsJunction = frontWaypoint.CheckJunction();
                isAtJunctionEntrance = !frontIsJunction && lookAhead.CheckJunction();

                if (!isAtJunctionEntrance)
                {
                    var prev = frontWaypoint.GetPreviousWaypoint();
                    if (prev.Count == 1)
                    {
                        isAtJunctionEntrance = !prev[0].CheckJunction() && frontIsJunction;
                    }
                }

                // Town03-roundabout fudge (LocalizationStage.cpp:90–95).
                if (isAtJunctionEntrance
                    && _localMap.GetMapName() == "Carla/Maps/Town03"
                    && SquaredLength(vehicleLocation) < 30f * 30f)
                {
                    isAtJunctionEntrance = false;
                }
            }

            // Trim the back of the buffer if the horizon shrank — but not
            // while we're inside or crossing a junction (we need the full
            // path through it).
            while (!isAtJunctionEntrance
                   && waypointBuffer.Count > 0
                   && waypointBuffer[^1].DistanceSquared(waypointBuffer[0]) > horizonSquare + horizonSquare
                   && !waypointBuffer[^1].CheckJunction())
            {
                PopWaypoint(actorId, _trackTraffic, waypointBuffer, frontOrBack: false);
            }
        }

        // ─── Re-seed an empty buffer from the closest waypoint ───────────
        if (waypointBuffer.Count == 0)
        {
            SimpleWaypoint closest = _localMap.GetWaypoint(vehicleLocation);
            PushWaypoint(actorId, _trackTraffic, waypointBuffer, closest);
        }

        // ─── Lane-change decision ────────────────────────────────────────
        ChangeLaneInfo laneChangeInfo = _parameters.GetForceLaneChange(actorId);
        bool forceLaneChange = laneChangeInfo.ChangeLane;
        bool laneChangeDirection = laneChangeInfo.Direction;

        if (!forceLaneChange && vehicleSpeed > MIN_LANE_CHANGE_SPEED)
        {
            float percKeepSlow = _parameters.GetKeepSlowLanePercentage(actorId);
            float percRandomLeft = _parameters.GetRandomLeftLaneChangePercentage(actorId);
            float percRandomRight = _parameters.GetRandomRightLaneChangePercentage(actorId);
            // Upstream pulls IsRHT() off the underlying client Waypoint;
            // we don't have that surface yet, so assume right-hand traffic
            // (true for every shipped CARLA map). Wave 3G can revisit.
            const bool isRht = ASSUME_RHT;

            bool isKeepSlow = percKeepSlow > _rng.Next();
            bool isRandomRight = percRandomRight >= _rng.Next();
            bool isRandomLeft = percRandomLeft >= _rng.Next();

            bool isLeftLaneChange = isRht ? isRandomLeft : (isKeepSlow || isRandomLeft);
            bool isRightLaneChange = isRht ? (isKeepSlow || isRandomRight) : isRandomRight;

            if (isLeftLaneChange && isRightLaneChange)
            {
                forceLaneChange = true;
                laneChangeDirection = FIFTYPERC > _rng.Next();
            }
            else if (isRightLaneChange)
            {
                forceLaneChange = true;
                laneChangeDirection = true;
            }
            else if (isLeftLaneChange)
            {
                forceLaneChange = true;
                laneChangeDirection = false;
            }
        }

        SimpleWaypoint frontWp = waypointBuffer[0];
        float laneChangeDistanceSquared = Square(MathF.Max(10.0f * vehicleSpeed, INTER_LANE_CHANGE_DISTANCE));

        bool recentlyNotExecutedLaneChange = !_lastLaneChangeSwpt.ContainsKey(actorId);
        bool doneWithPreviousLaneChange = true;
        if (!recentlyNotExecutedLaneChange)
        {
            float distFromPrev = DistanceSquared(
                _lastLaneChangeSwpt[actorId].Location, vehicleLocation);
            doneWithPreviousLaneChange = distFromPrev > laneChangeDistanceSquared;
            if (doneWithPreviousLaneChange)
                _lastLaneChangeSwpt.Remove(actorId);
        }
        bool autoOrForce = _parameters.GetAutoLaneChange(actorId) || forceLaneChange;
        bool frontNotJunction = !frontWp.CheckJunction();

        if (autoOrForce
            && frontNotJunction
            && (recentlyNotExecutedLaneChange || doneWithPreviousLaneChange))
        {
            SimpleWaypoint? changeOver = AssignLaneChange(
                actorId, vehicleLocation, vehicleSpeed, forceLaneChange, laneChangeDirection);

            if (changeOver != null)
            {
                _lastLaneChangeSwpt[actorId] = changeOver;
                int numberOfPops = waypointBuffer.Count;
                for (int j = 0; j < numberOfPops; ++j)
                {
                    PopWaypoint(actorId, _trackTraffic, waypointBuffer);
                }
                PushWaypoint(actorId, _trackTraffic, waypointBuffer, changeOver);
            }
        }

        // ─── Path / Route import OR random forward extension ─────────────
        IReadOnlyList<Location> importedPath = _parameters.GetCustomPath(actorId);
        IReadOnlyList<byte> importedActions = _parameters.GetImportedRoute(actorId);

        if (importedPath.Count > 0)
        {
            ImportPath(importedPath, waypointBuffer, actorId, horizonSquare);
        }
        else if (importedActions.Count > 0)
        {
            ImportRoute(importedActions, waypointBuffer, actorId, horizonSquare);
        }
        else
        {
            // Random forward extension along the road graph. Bounded for the same reason as the
            // imported-path walks: the horizon test measures straight-line distance, which a cycle
            // never satisfies. The id check below catches only cycles that pass through the front
            // waypoint, so it is not sufficient on its own.
            int walkSteps = 0;
            while (waypointBuffer[^1].DistanceSquared(waypointBuffer[0]) <= horizonSquare
                   && walkSteps++ < MaxHorizonWalkSteps)
            {
                SimpleWaypoint furthest = waypointBuffer[^1];
                IReadOnlyList<SimpleWaypoint> nexts = furthest.GetNextWaypoint();
                int selection = 0;
                if (nexts.Count > 1)
                {
                    // Upstream's selection: r ~ U(0, 100), idx = floor(r * n * 0.01).
                    // RandomGenerator.Next returns [0, 100). With n possible
                    // successors the index falls cleanly in [0, n-1].
                    double rSample = _rng.Next();
                    selection = (int)(rSample * nexts.Count * 0.01);
                    if (selection >= nexts.Count) selection = nexts.Count - 1;
                }
                else if (nexts.Count == 0)
                {
                    MarkIfOutOfRoad(actorId, waypointBuffer);
                    break;
                }
                SimpleWaypoint nextSel = nexts[selection];
                PushWaypoint(actorId, _trackTraffic, waypointBuffer, nextSel);
                // Loop detection: if we wrapped back to the front, stop. We
                // compare ids (not zero-distance) because two distinct waypoints
                // can share a location on adjacent lanes.
                if (nextSel.GetId() == waypointBuffer[0].GetId())
                {
                    break;
                }
            }
        }

        ExtendAndFindSafeSpace(actorId, isAtJunctionEntrance, waypointBuffer);

        // ─── Write the per-tick LocalizationData ─────────────────────────
        var localizationData = new LocalizationData
        {
            IsAtJunctionEntrance = isAtJunctionEntrance,
        };
        if (isAtJunctionEntrance
            && _vehiclesAtJunctionEntrance.TryGetValue(actorId, out var endpoints))
        {
            localizationData.JunctionEndPoint = endpoints.End;
            localizationData.SafePoint = endpoints.Safe;
        }
        _output[actorId] = localizationData;

        // ─── Refresh the geodesic-grid index for this actor ──────────────
        _trackTraffic.UpdateGridPosition(actorId, waypointBuffer);

        // ─── Is a routed vehicle still on its route? ─────────────────────
        // The buffer head is where the vehicle sits on the road graph, after any lane change this
        // tick made (a change-over replaces the whole buffer with its own start point). The check
        // is a set lookup; anything that follows from it happens on another thread.
        _routeSupervisor?.Observe(
            actorId, vehicleLocation, waypointBuffer.Count > 0 ? waypointBuffer[0] : null);
    }

    // ═════════════════════════════════════════════════════════════════════
    //                  Helpers (private; mirror upstream)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Walk the buffer searching for the end of the upcoming junction and
    /// the first "safe" point past it; extend the buffer further down the
    /// road graph if the existing horizon doesn't reach the safe point.
    /// </summary>
    private void ExtendAndFindSafeSpace(ActorId actorId, bool isAtJunctionEntrance, WaypointBuffer waypointBuffer)
    {
        SimpleWaypoint? junctionEndPoint = null;
        SimpleWaypoint? safePointAfterJunction = null;

        if (isAtJunctionEntrance
            && !_vehiclesAtJunctionEntrance.ContainsKey(actorId))
        {
            bool enteredJunction = false;
            bool pastJunction = false;
            bool safePointFound = false;
            SimpleWaypoint? currentWaypoint = null;
            SimpleWaypoint? junctionBeginPoint = null;
            float safeDistanceSquared = Square(SAFE_DISTANCE_AFTER_JUNCTION);

            // 1. Scan existing buffer points.
            for (int i = 0; i < waypointBuffer.Count && !safePointFound; ++i)
            {
                currentWaypoint = waypointBuffer[i];
                if (!enteredJunction && currentWaypoint.CheckJunction())
                {
                    enteredJunction = true;
                    junctionBeginPoint = currentWaypoint;
                }
                if (enteredJunction && !pastJunction && !currentWaypoint.CheckJunction())
                {
                    pastJunction = true;
                    junctionEndPoint = currentWaypoint;
                }
                if (pastJunction && junctionEndPoint != null
                    && junctionEndPoint.DistanceSquared(currentWaypoint) > safeDistanceSquared)
                {
                    safePointFound = true;
                    safePointAfterJunction = currentWaypoint;
                }
            }

            // 2. Extend buffer if not enough room to reach a safe point yet.
            if (!safePointFound)
            {
                bool abort = false;

                // 2a. Extend until we exit the junction.
                while (!pastJunction && !abort)
                {
                    var nextWaypoints = currentWaypoint!.GetNextWaypoint();
                    if (nextWaypoints.Count > 0)
                    {
                        currentWaypoint = nextWaypoints[0];
                        PushWaypoint(actorId, _trackTraffic, waypointBuffer, currentWaypoint);
                        if (!currentWaypoint.CheckJunction())
                        {
                            pastJunction = true;
                            junctionEndPoint = currentWaypoint;
                        }
                    }
                    else
                    {
                        abort = true;
                    }
                }

                // 2b. Then extend until we hit a safe distance past it.
                while (!safePointFound && !abort)
                {
                    var nextWaypoints = currentWaypoint!.GetNextWaypoint();
                    if (junctionEndPoint != null
                        && (junctionEndPoint.DistanceSquared(currentWaypoint) > safeDistanceSquared
                            || nextWaypoints.Count > 1
                            || currentWaypoint.CheckJunction()))
                    {
                        safePointFound = true;
                        safePointAfterJunction = currentWaypoint;
                    }
                    else
                    {
                        if (nextWaypoints.Count > 0)
                        {
                            currentWaypoint = nextWaypoints[0];
                            PushWaypoint(actorId, _trackTraffic, waypointBuffer, currentWaypoint);
                        }
                        else
                        {
                            abort = true;
                        }
                    }
                }
            }

            // 3. Discard sub-MIN_JUNCTION_LENGTH junctions — they're false
            //    positives (short stub roads tagged as junctions in OpenDRIVE).
            if (junctionEndPoint != null
                && safePointAfterJunction != null
                && junctionBeginPoint != null
                && junctionBeginPoint.DistanceSquared(junctionEndPoint) < Square(MIN_JUNCTION_LENGTH))
            {
                junctionEndPoint = null;
                safePointAfterJunction = null;
            }

            _vehiclesAtJunctionEntrance[actorId] = (junctionEndPoint, safePointAfterJunction);
        }
        else if (!isAtJunctionEntrance && _vehiclesAtJunctionEntrance.ContainsKey(actorId))
        {
            _vehiclesAtJunctionEntrance.Remove(actorId);
        }
    }

    /// <summary>
    /// Pick the waypoint that should become the new buffer head if the
    /// vehicle is to change lanes — accounting for in-lane obstacles and
    /// the availability of the target lane. Returns null if no safe
    /// change-over point exists.
    /// </summary>
    private SimpleWaypoint? AssignLaneChange(
        ActorId actorId, Location vehicleLocation, float vehicleSpeed, bool force, bool direction)
    {
        SimpleWaypoint? changeOverPoint = null;

        if (!_bufferMap.TryGetValue(actorId, out var waypointBuffer) || waypointBuffer.Count == 0)
            return changeOverPoint;

        SimpleWaypoint currentWaypoint = waypointBuffer[0];
        SimpleWaypoint? leftWaypoint = currentWaypoint.GetLeftWaypoint();
        SimpleWaypoint? rightWaypoint = currentWaypoint.GetRightWaypoint();

        // Broad-phase: every actor whose path crosses our grids.
        var blockingVehicles = _trackTraffic.GetOverlappingVehicles(actorId);

        bool obstacleTooClose = false;
        float minSquaredDistance = float.PositiveInfinity;
        ActorId obstacleActorId = 0u;

        if (!force)
        {
            foreach (ActorId other in blockingVehicles)
            {
                if (obstacleTooClose) break;
                if (!_bufferMap.TryGetValue(other, out var otherBuffer) || otherBuffer.Count == 0)
                    continue;

                SimpleWaypoint otherCurrent = otherBuffer[0];
                Location otherLoc = otherCurrent.Location;

                Vector3D refHeading = currentWaypoint.ForwardVector;
                Vector3D refToOther = new(
                    otherLoc.X - currentWaypoint.Location.X,
                    otherLoc.Y - currentWaypoint.Location.Y,
                    otherLoc.Z - currentWaypoint.Location.Z);
                Vector3D otherHeading = otherCurrent.ForwardVector;

                var curRaw = currentWaypoint.Waypoint;
                var otherRaw = otherCurrent.Waypoint;

                // Both vehicles in same lane (road + lane id), in front, same heading.
                if (!currentWaypoint.CheckJunction()
                    && !otherCurrent.CheckJunction()
                    && otherRaw.RoadId == curRaw.RoadId
                    && otherRaw.LaneId == curRaw.LaneId
                    && Dot(refHeading, refToOther) > 0.0f
                    && Dot(refHeading, otherHeading) > MAXIMUM_LANE_OBSTACLE_CURVATURE)
                {
                    float sqd = DistanceSquared(vehicleLocation, otherLoc);
                    if (sqd > Square(MINIMUM_LANE_CHANGE_DISTANCE))
                    {
                        if (sqd < minSquaredDistance && sqd < Square(MAXIMUM_LANE_OBSTACLE_DISTANCE))
                        {
                            minSquaredDistance = sqd;
                            obstacleActorId = other;
                        }
                    }
                    else
                    {
                        obstacleTooClose = true;
                    }
                }
            }
        }

        if (!obstacleTooClose && obstacleActorId != 0u && !force)
        {
            // Pick a free adjacent lane near the obstacle.
            var otherBuffer = _bufferMap[obstacleActorId];
            SimpleWaypoint otherCurrent = otherBuffer[0];

            bool distantLeftFree = false;
            bool distantRightFree = false;
            // Iteration order matches upstream's brace-init {left, right}.
            SimpleWaypoint? otherLeft = otherCurrent.GetLeftWaypoint();
            SimpleWaypoint? otherRight = otherCurrent.GetRightWaypoint();
            if (otherLeft != null && _trackTraffic.GetPassingVehicles(otherLeft.GetId()).Count == 0)
                distantLeftFree = true;
            if (otherRight != null && _trackTraffic.GetPassingVehicles(otherRight.GetId()).Count == 0)
                distantRightFree = true;

            if (distantRightFree && rightWaypoint != null
                && _trackTraffic.GetPassingVehicles(rightWaypoint.GetId()).Count == 0)
            {
                changeOverPoint = rightWaypoint;
            }
            else if (distantLeftFree && leftWaypoint != null
                     && _trackTraffic.GetPassingVehicles(leftWaypoint.GetId()).Count == 0)
            {
                changeOverPoint = leftWaypoint;
            }
        }
        else if (force)
        {
            if (direction && rightWaypoint != null)
                changeOverPoint = rightWaypoint;
            else if (!direction && leftWaypoint != null)
                changeOverPoint = leftWaypoint;
        }

        // Walk down the new lane until we hit min lane-change distance or a junction.
        if (changeOverPoint != null)
        {
            float changeOverDistance = Math.Clamp(1.5f * vehicleSpeed, MIN_WPT_DISTANCE, MAX_WPT_DISTANCE);
            SimpleWaypoint startingPoint = changeOverPoint;
            while (changeOverPoint.DistanceSquared(startingPoint) < Square(changeOverDistance)
                   && !changeOverPoint.CheckJunction())
            {
                var nexts = changeOverPoint.GetNextWaypoint();
                if (nexts.Count == 0) break;
                changeOverPoint = nexts[0];
            }
        }

        return changeOverPoint;
    }

    // Exposed to the test assembly (not private) so the cycle-termination regression test can
    // drive the walk directly against a cyclic road graph without standing up a server.
    internal void ImportPath(IReadOnlyList<Location> importedPath, WaypointBuffer waypointBuffer,
                             ActorId actorId, float horizonSquare)
    {
        // Snapshot to a mutable list so we can pop off the front.
        var workingPath = new List<Location>(importedPath);

        if (_parameters.GetUploadPath(actorId))
        {
            int numberOfPops = waypointBuffer.Count;
            for (int j = 0; j < numberOfPops - 1; ++j)
            {
                PopWaypoint(actorId, _trackTraffic, waypointBuffer, frontOrBack: false);
            }
            _parameters.RemoveUploadPath(actorId, removePath: false);
        }

        Location latestImported = workingPath[0];
        SimpleWaypoint imported = _localMap.GetWaypoint(latestImported);

        // The destination steers the walk but never terminates it: this is a greedy step-by-step
        // descent toward a bearing, not a solved route. On a cyclic road graph the walk can circle
        // indefinitely — never reaching the destination, never leaving the horizon — so track which
        // waypoints this call has already appended and stop when it revisits one. `visited` starts
        // empty rather than seeded from the buffer, so a cycle entered on an earlier tick costs at
        // most one extra lap before it is caught, and the common case allocates nothing.
        var visited = new HashSet<ulong>();
        int walkSteps = 0;

        while (workingPath.Count > 0
               && waypointBuffer[^1].DistanceSquared(waypointBuffer[0]) <= horizonSquare
               && walkSteps++ < MaxHorizonWalkSteps)
        {
            SimpleWaypoint latestWp = waypointBuffer[^1];
            IReadOnlyList<SimpleWaypoint> nexts = latestWp.GetNextWaypoint();
            int selection = 0;

            if (nexts.Count > 1)
            {
                uint importedRoadId = imported.Waypoint.RoadId;
                float minDistance = float.PositiveInfinity;
                for (int k = 0; k < nexts.Count; ++k)
                {
                    SimpleWaypoint junctionEnd = nexts[k];
                    // Each of these three probes follows a single successor chain and stops on a
                    // predicate that a cycle satisfies forever, so each is bounded (see
                    // MaxHorizonWalkSteps). Walking past the bound only degrades the junction-choice
                    // heuristic for this candidate; it cannot hang the tick.
                    int steps = 0;
                    // Walk to the next non-junction segment.
                    while (!junctionEnd.CheckJunction() && steps++ < MaxHorizonWalkSteps)
                    {
                        var nx = junctionEnd.GetNextWaypoint();
                        if (nx.Count == 0) break;
                        junctionEnd = nx[0];
                    }
                    steps = 0;
                    while (junctionEnd.CheckJunction() && steps++ < MaxHorizonWalkSteps)
                    {
                        var nx = junctionEnd.GetNextWaypoint();
                        if (nx.Count == 0) break;
                        junctionEnd = nx[0];
                    }
                    steps = 0;
                    while (nexts[k].DistanceSquared(junctionEnd) < 50.0f && steps++ < MaxHorizonWalkSteps)
                    {
                        var nx = junctionEnd.GetNextWaypoint();
                        if (nx.Count == 0) break;
                        junctionEnd = nx[0];
                    }
                    uint jepRoadId = junctionEnd.Waypoint.RoadId;
                    if (jepRoadId == importedRoadId)
                    {
                        selection = k;
                        break;
                    }
                    float distance = junctionEnd.DistanceSquared(imported);
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        selection = k;
                    }
                }
            }
            else if (nexts.Count == 0)
            {
                MarkIfOutOfRoad(actorId, waypointBuffer);
                break;
            }

            SimpleWaypoint nextSel = nexts[selection];

            // Close enough to the imported target → consume one path entry.
            if (nextSel.DistanceSquared(imported) < 30.0f)
            {
                workingPath.RemoveAt(0);
                var possibles = nextSel.GetNextWaypoint();
                bool importedIsSuccessor = false;
                for (int p = 0; p < possibles.Count; ++p)
                {
                    if (ReferenceEquals(possibles[p], imported)) { importedIsSuccessor = true; break; }
                }
                if (importedIsSuccessor)
                {
                    PushWaypoint(actorId, _trackTraffic, waypointBuffer, nextSel);
                }
                PushWaypoint(actorId, _trackTraffic, waypointBuffer, imported);
                if (workingPath.Count > 0)
                {
                    latestImported = workingPath[0];
                    imported = _localMap.GetWaypoint(latestImported);
                }
            }
            else
            {
                // Revisiting a waypoint means the road graph looped back; extending further would
                // retrace the same cycle forever.
                if (!visited.Add(nextSel.GetId())) break;
                PushWaypoint(actorId, _trackTraffic, waypointBuffer, nextSel);
            }
        }

        if (workingPath.Count == 0)
        {
            _parameters.RemoveUploadPath(actorId, removePath: true);
        }
        else
        {
            _parameters.UpdateUploadPath(actorId, workingPath);
        }
    }

    // Exposed to the test assembly (not private) so the cycle-termination regression test can
    // drive the walk directly against a cyclic road graph without standing up a server.
    internal void ImportRoute(IReadOnlyList<byte> importedActions, WaypointBuffer waypointBuffer,
                              ActorId actorId, float horizonSquare)
    {
        var workingActions = new List<byte>(importedActions);

        if (_parameters.GetUploadRoute(actorId))
        {
            int numberOfPops = waypointBuffer.Count;
            for (int j = 0; j < numberOfPops - 1; ++j)
            {
                PopWaypoint(actorId, _trackTraffic, waypointBuffer, frontOrBack: false);
            }
            _parameters.RemoveImportedRoute(actorId, removePath: false);
        }

        RoadOption nextRoadOption = (RoadOption)workingActions[0];

        // Same unbounded-walk hazard as ImportPath: the loop ends only when the buffer spans the
        // horizon in a straight line, which circling a loop ramp never achieves.
        var visited = new HashSet<ulong>();
        int walkSteps = 0;

        while (workingActions.Count > 0
               && waypointBuffer[^1].DistanceSquared(waypointBuffer[0]) <= horizonSquare
               && walkSteps++ < MaxHorizonWalkSteps)
        {
            SimpleWaypoint latestWp = waypointBuffer[^1];
            RoadOption latestRoadOption = latestWp.GetRoadOption();
            IReadOnlyList<SimpleWaypoint> nexts = latestWp.GetNextWaypoint();
            int selection = 0;

            if (nexts.Count > 1)
            {
                for (int i = 0; i < nexts.Count; ++i)
                {
                    if (nexts[i].GetRoadOption() == nextRoadOption)
                    {
                        selection = i;
                        break;
                    }
                }
            }
            else if (nexts.Count == 0)
            {
                MarkIfOutOfRoad(actorId, waypointBuffer);
                break;
            }

            SimpleWaypoint nextSel = nexts[selection];
            // Revisiting a waypoint means the road graph looped back on itself.
            if (!visited.Add(nextSel.GetId())) break;
            PushWaypoint(actorId, _trackTraffic, waypointBuffer, nextSel);

            if (latestRoadOption != nextSel.GetRoadOption()
                && nextRoadOption == nextSel.GetRoadOption())
            {
                workingActions.RemoveAt(0);
                if (workingActions.Count > 0)
                {
                    nextRoadOption = (RoadOption)workingActions[0];
                }
            }
        }

        if (workingActions.Count == 0)
        {
            _parameters.RemoveImportedRoute(actorId, removePath: true);
        }
        else
        {
            _parameters.UpdateImportedRoute(actorId, workingActions);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //                  Public query helpers (parity with upstream)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the next meaningful action — lane-change or intersection
    /// turn — for the actor's current buffer. Mirrors upstream's
    /// <c>ComputeNextAction</c> in LocalizationStage.cpp:597.
    /// </summary>
    public (RoadOption Option, SimpleWaypoint? Waypoint) ComputeNextAction(ActorId actorId)
    {
        if (!_bufferMap.TryGetValue(actorId, out var waypointBuffer) || waypointBuffer.Count == 0)
            return (RoadOption.Void, null);

        var nextAction = (Option: RoadOption.LaneFollow, Waypoint: (SimpleWaypoint?)waypointBuffer[^1]);
        bool isLaneChange = false;
        if (_lastLaneChangeSwpt.TryGetValue(actorId, out var lcWp))
        {
            isLaneChange = true;
            Vector3D heading = _simulationState.GetHeading(actorId);
            Location loc = _simulationState.GetLocation(actorId);
            Vector3D rel = new(loc.X - lcWp.Location.X, loc.Y - lcWp.Location.Y, loc.Z - lcWp.Location.Z);
            bool leftHeading = (heading.X * rel.Y - heading.Y * rel.X) > 0.0f;
            nextAction = (leftHeading ? RoadOption.ChangeLaneLeft : RoadOption.ChangeLaneRight, lcWp);
        }

        for (int i = 0; i < waypointBuffer.Count; ++i)
        {
            var swpt = waypointBuffer[i];
            RoadOption opt = swpt.GetRoadOption();
            if (opt != RoadOption.LaneFollow)
            {
                if (!isLaneChange)
                {
                    return (opt, swpt);
                }
                else
                {
                    Location laneChangeLoc = _lastLaneChangeSwpt[actorId].Location;
                    Location actualLoc = _simulationState.GetLocation(actorId);
                    float dlc = DistanceSquared(actualLoc, laneChangeLoc);
                    float dother = DistanceSquared(actualLoc, swpt.Location);
                    return dlc < dother ? nextAction : (opt, swpt);
                }
            }
        }
        return nextAction;
    }

    /// <summary>
    /// Returns the full chain of upcoming actions in the buffer. Mirrors
    /// upstream's <c>ComputeActionBuffer</c> in LocalizationStage.cpp:633.
    /// </summary>
    public List<(RoadOption Option, SimpleWaypoint Waypoint)> ComputeActionBuffer(ActorId actorId)
    {
        var actionBuffer = new List<(RoadOption, SimpleWaypoint)>();
        if (!_bufferMap.TryGetValue(actorId, out var waypointBuffer) || waypointBuffer.Count == 0)
            return actionBuffer;

        (RoadOption Option, SimpleWaypoint Waypoint)? laneChange = null;
        SimpleWaypoint bufFront = waypointBuffer[0];
        RoadOption lastOpt = bufFront.GetRoadOption();
        actionBuffer.Add((lastOpt, bufFront));

        if (_lastLaneChangeSwpt.TryGetValue(actorId, out var lcWp))
        {
            Vector3D heading = _simulationState.GetHeading(actorId);
            Location loc = _simulationState.GetLocation(actorId);
            Vector3D rel = new(loc.X - lcWp.Location.X, loc.Y - lcWp.Location.Y, loc.Z - lcWp.Location.Z);
            bool leftHeading = (heading.X * rel.Y - heading.Y * rel.X) > 0.0f;
            laneChange = (leftHeading ? RoadOption.ChangeLaneLeft : RoadOption.ChangeLaneRight, lcWp);
        }

        for (int i = 0; i < waypointBuffer.Count; ++i)
        {
            var wpt = waypointBuffer[i];
            RoadOption curOpt = wpt.GetRoadOption();
            if (curOpt != lastOpt)
            {
                actionBuffer.Add((curOpt, wpt));
                lastOpt = curOpt;
            }
        }

        if (laneChange is { } lc)
        {
            float dLC = DistanceSquared(bufFront.Location, lc.Waypoint.Location);
            for (int i = 0; i < actionBuffer.Count; ++i)
            {
                float dAct = DistanceSquared(bufFront.Location, actionBuffer[i].Item2.Location);
                if (i == actionBuffer.Count - 1)
                {
                    actionBuffer.Add(lc);
                    break;
                }
                else if (dAct > dLC)
                {
                    actionBuffer.Insert(i, lc);
                    break;
                }
            }
        }
        return actionBuffer;
    }

    // ═════════════════════════════════════════════════════════════════════
    //                          Math primitives
    // ═════════════════════════════════════════════════════════════════════

    private static float Square(float a) => a * a;

    private static float Length(Vector3D v)
        => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static float SquaredLength(Location v)
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
}

