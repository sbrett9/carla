// Source: carla/trafficmanager/TrafficLightStage.{h,cpp}
//
// Per-tick traffic-control / stop-sign / non-signalised-junction arbiter.
// Decides for each registered vehicle whether it should brake for any of:
//
//   1. A traffic light it is currently at (Red / Yellow).
//   2. A stop sign or "ghost-yield" non-signalised junction it is
//      approaching — only one vehicle enters such a junction at a time,
//      FIFO by arrival.
//
// Both decisions can be stochastically overridden by
// Parameters.GetPercentageRunningLight / GetPercentageRunningSign — that's
// how chaotic-traffic scenarios are configured.
//
// The traffic-light state itself is NOT queried from the simulator inside
// this stage: ALSM populates SimulationState.GetTLS() once per tick by
// reading actor.GetTrafficLightState() (which is part of the actor
// snapshot — no separate RPC). The cc::World reference in the C++ class
// is used ONLY to fetch the simulator timestamp for the stop-sign
// MINIMUM_STOP_TIME accumulator. We mirror that — see _currentTimestamp.
//
// The signal-id → server-actor-id mapping that the prompt asks about
// is handled by the simulator end: actor.GetTrafficLightState() returns
// the live TLS for whichever traffic light governs the vehicle's lane,
// without TM ever knowing the signal's OpenDRIVE id. ALSM uses that to
// fill SimulationState.GetTLS(). No bridge work is needed here.
//
// NOTE: the constructor accepts a CarlaClient so we can fetch the
// current simulator timestamp via the world-observer cache (see
// ALSM / OnTick wiring in CarlaNet.Transport.CarlaClient). Wave 4 will
// route the timestamp directly from the orchestrator's tick loop and
// this dependency can be removed.

#nullable enable

using CarlaNet.Transport;

namespace CarlaNet.TrafficManager.Stages;

// TrafficLightFrame: per-vehicle hazard flag, mirroring upstream's
// `TLFrame = std::vector<bool>`. We expose a dictionary keyed by ActorId
// so callers can read it the same way as CollisionFrame and friends.
internal readonly record struct TrafficLightFrame(bool HazardFlag);

internal sealed class TrafficLightStage : IStageWithRemoveActor
{
    private readonly SimulationState _simulationState;
    private readonly BufferMap _bufferMap;
    private readonly Parameters _parameters;
    private readonly RandomGenerator _random;
    private readonly CarlaClient? _client;
    private readonly InMemoryMap? _localMap;

    // Per-junction FIFO of vehicles approaching a non-signalised junction.
    // Mirrors `std::deque<ActorId>` upstream — LinkedList gives us O(1)
    // remove-from-middle for the RemoveActor case.
    private readonly Dictionary<JuncId, LinkedList<ActorId>> _enteringVehiclesMap = new();
    // Reverse lookup: which junction is each vehicle currently bound to?
    private readonly Dictionary<ActorId, JuncId> _vehicleLastJunction = new();
    // Timestamp at which the vehicle first stopped at the stop sign.
    private readonly Dictionary<ActorId, double> _vehicleStopTime = new();
    // Last (atTrafficLight, state) pair reported for each vehicle, so the signal it is being shown
    // is reported on change rather than every tick.
    private readonly Dictionary<ActorId, (bool AtLight, TLS State)> _lastReportedLight = new();

    // Live state of every generated traffic light, keyed by the OpenDRIVE signal id the waypoints
    // refer to. Refreshed once per tick; empty when the simulator connection is absent, in which
    // case approach control does nothing and the box-based decision is the only one in play.
    private IReadOnlyDictionary<string, TLS> _signalStates = new Dictionary<string, TLS>();
    // Which signal each vehicle is currently braking for, or absent if it is not braking for one.
    // Held rather than recomputed so a vehicle that has begun stopping for a signal keeps stopping
    // until that signal permits it, instead of re-deciding on a threshold its own braking moves.
    private readonly Dictionary<ActorId, string> _heldOnApproach = new();

    // Deceleration assumed available when stopping for a signal, in m/s². The motion planner answers
    // a signal hazard with full brake rather than a controlled deceleration, so this is set near what
    // that delivers: assuming much less means braking from far enough back to stop well short of the
    // line, which with a hold that lasts until the signal permits is where the vehicle then waits.
    private const float AssumedBrakingDecelerationMetresPerSecondSquared = 6.0f;
    // Applied to the computed stopping distance, so braking begins before the stop becomes marginal.
    private const float ApproachBrakingMargin = 1.25f;
    // Vehicles close to a red hold for it whatever their speed, so one that has crept forward or is
    // already stopped at the line stays there instead of easing over it.
    private const float MinimumSignalApproachMetres = 8.0f;
    // At or below this speed a vehicle counts as stopped, in m/s. A vehicle stopped short of its
    // signal releases the brake so ordinary control can close the gap to whatever is in front.
    private const float CreepSpeedMetresPerSecond = 0.5f;
    // Vehicles that entered a signalised junction while permitted to proceed, and so must clear it
    // rather than stop part-way across if the light changes behind them. See Update().
    private readonly HashSet<ActorId> _committedToJunction = new();

    // Per-tick output.
    private readonly Dictionary<ActorId, TrafficLightFrame> _output = new();

    // Simulator-time accumulator (seconds since episode start).
    private double _currentTimestamp;

    public TrafficLightStage(
        SimulationState simulationState,
        BufferMap bufferMap,
        Parameters parameters,
        RandomGenerator random,
        CarlaClient? client = null,
        InMemoryMap? localMap = null)
    {
        _simulationState = simulationState;
        _bufferMap = bufferMap;
        _parameters = parameters;
        _random = random;
        _client = client;
        _localMap = localMap;
    }

    public IReadOnlyDictionary<ActorId, TrafficLightFrame> GetOutput() => _output;

    /// <summary>
    /// Update the per-vehicle simulator timestamp tracker. The orchestrator
    /// is expected to invoke this once per tick (mirrors upstream's
    /// `current_timestamp = world.GetSnapshot().GetTimestamp()` inside
    /// the per-vehicle update — we hoist it for efficiency).
    /// </summary>
    public void SetCurrentTimestamp(double elapsedSeconds)
    {
        _currentTimestamp = elapsedSeconds;
    }

    /// <summary>
    /// Re-read every traffic light's state for this tick. Reads the world-observer snapshot the
    /// simulator already sends, so it costs one pass over the lights and no round trip. Called once
    /// per tick by the orchestrator, alongside <see cref="SetCurrentTimestamp"/>.
    /// </summary>
    public void RefreshSignalStates()
    {
        if (_client is null) return;
        var observed = _client.GetTrafficLightStatesBySignId();
        if (observed.Count == 0)
        {
            EnsureSignalRegistryDiscovery();
            return;
        }
        var states = new Dictionary<string, TLS>(observed.Count);
        foreach (var (signalId, state) in observed)
            states[signalId] = (TLS)(byte)state;
        _signalStates = states;
    }

    /// <summary>
    /// Establish which actors are traffic lights, off the tick.
    /// </summary>
    /// <remarks>
    /// Learning this costs a round trip, and the tick holds the registration lock, so doing it here
    /// would stall whichever thread owns the world tick. It also cannot be done once at construction:
    /// the lights are found through the world-observer snapshot, which is not populated until the
    /// observer has delivered a frame. So it is attempted in the background and retried on an
    /// interval — a map with no traffic lights simply never succeeds, at the cost of one idle request
    /// every few seconds rather than one per tick.
    /// </remarks>
    private void EnsureSignalRegistryDiscovery()
    {
        if (_signalRegistryDiscovery is { IsCompleted: false }) return;
        long now = Environment.TickCount64;
        if (now - _lastSignalRegistryAttempt < SignalRegistryRetryIntervalMs) return;
        _lastSignalRegistryAttempt = now;
        CarlaClient client = _client!;
        _signalRegistryDiscovery = Task.Run(async () =>
        {
            try
            {
                int found = await client.RefreshTrafficLightRegistryAsync().ConfigureAwait(false);
                if (found > 0)
                    TrafficReport.Writer.WriteLine(
                        $"{DateTime.Now:HH:mm:ss.fff} [traffic] tracking {found} traffic lights by "
                        + "signal id; approaching vehicles are told their state from a distance.");
            }
            catch (Exception ex)
            {
                TrafficReport.Writer.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} [traffic] could not enumerate traffic lights "
                    + $"({ex.Message}); vehicles will only see a signal while standing at it.");
            }
        });
    }

    private Task? _signalRegistryDiscovery;
    private long _lastSignalRegistryAttempt;
    private const long SignalRegistryRetryIntervalMs = 5000;

    /// <summary>Number of traffic lights whose state is currently being tracked.</summary>
    public int TrackedSignalCount => _signalStates.Count;

    /// <summary>
    /// Supply signal states directly, for tests that have no simulator connection.
    /// </summary>
    internal void SetSignalStatesForTesting(IReadOnlyDictionary<string, TLS> states)
        => _signalStates = states;

    /// <summary>
    /// Decide whether the supplied vehicle should brake for a TL / stop /
    /// non-signalised-junction this tick. Result is written to the output
    /// map keyed by <paramref name="egoActorId"/>.
    /// </summary>
    public void Update(ActorId egoActorId)
    {
        bool trafficLightHazard = false;

        if (!_simulationState.ContainsActor(egoActorId))
        {
            _output[egoActorId] = new TrafficLightFrame(false);
            return;
        }

        if (!_simulationState.IsDormant(egoActorId))
        {
            JuncId currentJunctionId = -1;
            if (_vehicleLastJunction.TryGetValue(egoActorId, out var lastJunction))
                currentJunctionId = lastJunction;

            JuncId affectedJunctionId = GetAffectedJunctionId(egoActorId);

            TrafficLightStateData tlState = _simulationState.GetTLS(egoActorId);
            TLS trafficLightState = tlState.TlState;
            bool isAtTrafficLight = tlState.AtTrafficLight;

            // Report every change in what a vehicle is being told about its light, with where it was
            // standing at the time. A vehicle that drives through a red does so either because it was
            // never associated with the signal — the simulator answers "not at a light, green" for a
            // vehicle outside every stop-line trigger box, which is indistinguishable downstream from
            // a real green — or because it was told green while the signal was red. Those two have
            // different causes and different fixes, and nothing else emitted here separates them:
            // a red suppresses commitment, so it can never appear on a commitment line.
            var seen = (isAtTrafficLight, trafficLightState);
            if (TrafficReport.DiagnosticsEnabled
                && (!_lastReportedLight.TryGetValue(egoActorId, out var previouslySeen) || previouslySeen != seen))
            {
                _lastReportedLight[egoActorId] = seen;
                Location where = _simulationState.GetLocation(egoActorId);
                TrafficReport.Writer.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} [traffic] vehicle {egoActorId} sees light "
                    + $"{trafficLightState} (atLight={isAtTrafficLight}) at "
                    + $"({where.X:F1}, {where.Y:F1})");
            }

            // Is the vehicle's path about to enter, or already on, a junction road?
            bool headingIntoJunction =
                _bufferMap.TryGetValue(egoActorId, out var egoBuffer)
                && egoBuffer.Count > 0
                && egoBuffer[0].CheckJunction();

            // A vehicle that entered the junction while permitted to proceed has committed to its
            // manoeuvre and must clear the intersection, even if the light changes behind it. This is
            // needed because a vehicle keeps reporting "at traffic light" for as long as it overlaps
            // the signal's stop-line trigger box, and that box (~3 m) is far shorter than a long
            // vehicle, so the flag stays set well after the nose is into the junction. Without it, a
            // light changing mid-manoeuvre halts the vehicle across the intersection — worst for a
            // permissive left turn, which waits inside the junction for a gap and clears on
            // yellow/red, and would otherwise block cross traffic until its own light cycles green.
            //
            // Commitment is tracked rather than inferred from position: the waypoint buffer looks
            // AHEAD of the vehicle, so a vehicle still stopped at the stop line already has a junction
            // waypoint in front of it and cannot be distinguished geometrically from one that is
            // genuinely across the line. Entering on a permitting light is the distinction that
            // matters, and a vehicle arriving against a red never commits, so it still stops.
            bool committedToJunction = _committedToJunction.Contains(egoActorId);

            // Whether the vehicle itself is inside a junction, as opposed to its horizon buffer
            // pointing into one. These come apart exactly where it matters: the buffer looks ahead,
            // so while a vehicle is still crossing an intersection its front waypoint has already
            // moved onto the road beyond, taking the next signal's identity with it. A vehicle
            // part-way through a left turn would then brake for a signal on the far side and stop
            // dead across the junction. Measured before this test existed: 325 vehicles committed to
            // a junction and then began braking within twelve seconds, many inside a fifth of a
            // second, several for a signal 4.5 m past the junction they were still in.
            Vector3D approachVelocity = _simulationState.GetVelocity(egoActorId);
            float approachSpeed = MathF.Sqrt(
                approachVelocity.X * approachVelocity.X
                + approachVelocity.Y * approachVelocity.Y
                + approachVelocity.Z * approachVelocity.Z);

            bool insideJunction =
                _localMap is not null
                && _localMap.GetWaypoint(_simulationState.GetLocation(egoActorId)).CheckJunction();

            // A vehicle is only handed a light's state by the simulator while it physically overlaps
            // that light's stop-line trigger box. On a road posted well above walking pace it crosses
            // that box in a fraction of a second — measured at 0.2 to 0.8 s over 3.7 to 8.0 m — which
            // is far less than it needs to stop, and the moment it leaves the box the answer reverts
            // to "not at a light, green". Waiting to be told therefore means being told too late, and
            // then told nothing at all.
            //
            // So read the signal governing this lane directly, from any distance, and start braking
            // while there is still room to stop. The waypoint knows which signal it is approaching and
            // how far ahead the stop line is; the state comes from the light itself rather than from
            // whichever box the vehicle happens to be standing in. A vehicle already committed to its
            // manoeuvre is exempt, as it is for the box-based decision below.
            string? governingSignalId =
                egoBuffer is { Count: > 0 } ? egoBuffer[0].GoverningSignalId : null;
            bool signalAheadIsStopping =
                governingSignalId is not null
                && _signalStates.TryGetValue(governingSignalId, out TLS approachingState)
                && approachingState != TLS.Green
                && approachingState != TLS.Off;

            _heldOnApproach.TryGetValue(egoActorId, out string? alreadyHeldFor);

            // Whether the vehicle is stopping for the signal ahead. Distinct from whether it brakes
            // this tick: a vehicle stopped short of the line is still stopping for the signal while
            // it edges up to it.
            bool stoppingForSignal = false;
            if (committedToJunction || insideJunction || !signalAheadIsStopping)
            {
                // Nothing ahead to stop for: either the signal has changed to permit this vehicle, it
                // is no longer the signal governing the lane, or the vehicle is already crossing.
                stoppingForSignal = false;
            }
            else if (alreadyHeldFor == governingSignalId)
            {
                // Already stopping for this signal, so keep stopping until it permits. This must not
                // be re-decided from distance and speed: braking reduces the speed, which reduces the
                // room the vehicle needs, which drops the threshold below the distance and releases
                // it — then it accelerates, the threshold grows, and it brakes again, all the way to
                // the line. Measured before this held: 940 braking events across 129 approaches, one
                // vehicle re-braking for the same signal 37 times.
                stoppingForSignal = true;
            }
            else if (_parameters.GetPercentageRunningLight(egoActorId) <= _random.Next())
            {
                // Decide once, on the way in. A vehicle configured to run lights rolls for it here
                // rather than every tick, so it does not change its mind mid-approach.
                float roomToStop =
                    approachSpeed * approachSpeed / (2f * AssumedBrakingDecelerationMetresPerSecondSquared);
                float startBrakingAt =
                    MathF.Max(roomToStop * ApproachBrakingMargin, MinimumSignalApproachMetres);
                stoppingForSignal = egoBuffer![0].DistanceToGoverningSignal <= startBrakingAt;
            }

            // Braking is all-or-nothing — the motion planner answers a signal hazard by cutting the
            // throttle and applying full brake — so holding it continuously would park each vehicle
            // wherever its own stopping distance happened to run out and give it no way to close up.
            // Since every vehicle brakes on its distance to the signal, and nothing in that decision
            // knows about the vehicle in front, the gaps compound down a queue and widen with speed.
            //
            // A vehicle that has come to rest still short of the line therefore stops braking, so
            // ordinary control resumes and edges it forward — that control already keeps its distance
            // from the vehicle ahead, which is the constraint that should govern a queue. It stays
            // marked as stopping for the signal throughout, and the minimum-approach distance brings
            // the brake back as it reaches the line, so it creeps up and holds rather than easing over.
            bool restingShortOfTheLine =
                stoppingForSignal
                && approachSpeed < CreepSpeedMetresPerSecond
                && egoBuffer![0].DistanceToGoverningSignal > MinimumSignalApproachMetres;
            bool heldByApproachingSignal = stoppingForSignal && !restingShortOfTheLine;

            // Report on the underlying decision rather than on the brake, so that creeping up to the
            // line reads as one approach instead of a burst of stops and starts. Being held for a
            // signal the vehicle cannot yet be standing at is the behaviour this is here to produce,
            // and being released is what distinguishes a working signal from one that has stopped
            // traffic permanently.
            if (stoppingForSignal && alreadyHeldFor != governingSignalId)
            {
                _heldOnApproach[egoActorId] = governingSignalId!;
                if (TrafficReport.DiagnosticsEnabled)
                    TrafficReport.Writer.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} [traffic] vehicle {egoActorId} braking for "
                    + $"signal {governingSignalId} "
                    + $"{egoBuffer![0].DistanceToGoverningSignal:F1} m ahead.");
            }
            else if (!stoppingForSignal && alreadyHeldFor is not null)
            {
                _heldOnApproach.Remove(egoActorId);
                if (TrafficReport.DiagnosticsEnabled)
                    TrafficReport.Writer.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} [traffic] vehicle {egoActorId} released by signal "
                    + $"{alreadyHeldFor}.");
            }

            // Case 1: at a signalised junction with a red/yellow light.
            if (isAtTrafficLight
                && trafficLightState != TLS.Green
                && trafficLightState != TLS.Off
                && _parameters.GetPercentageRunningLight(egoActorId) <= _random.Next())
            {
                if (currentJunctionId != -1)
                    RemoveActor(egoActorId);
                // A vehicle physically inside the intersection is never held by a signal, whether or
                // not it was granted commitment on the way in. Commitment alone is not enough: it is
                // only granted while nothing is holding the vehicle back, so one whose light was
                // already red as it entered never receives it, and would then be stopped by the same
                // red it is standing past — blocking every movement that crosses it until its own
                // light cycles green. A vehicle that has entered has to leave.
                trafficLightHazard = !committedToJunction && !insideJunction;
            }
            // Case 2: currently arbitrating a non-signalised junction.
            else if (currentJunctionId != -1)
            {
                if (affectedJunctionId == -1 || affectedJunctionId != currentJunctionId)
                {
                    RemoveActor(egoActorId);
                }
                else
                {
                    trafficLightHazard = HandleNonSignalisedJunction(
                        egoActorId, affectedJunctionId, _currentTimestamp);
                }
            }
            // Case 3: approaching a non-signalised junction we have not
            // entered yet (and we are not currently bound to a TL).
            else if (affectedJunctionId != -1
                && !isAtTrafficLight
                && trafficLightState != TLS.Green
                && _parameters.GetPercentageRunningSign(egoActorId) <= _random.Next())
            {
                AddActorToNonSignalisedJunction(egoActorId, affectedJunctionId);
                trafficLightHazard = true;
            }

            // Hold for a signal seen on approach as well as for one the vehicle is standing at. This
            // has to be applied before commitment is decided below, so that a vehicle still short of
            // a red does not commit to crossing on the strength of nothing having stopped it yet.
            trafficLightHazard |= heldByApproachingSignal;

            // Update the commitment: a vehicle proceeding into the junction (nothing holding it back
            // this tick) has committed to crossing; one that has left the junction behind releases it.
            // Re-asserted every tick, so a committed vehicle stays committed for as long as it is
            // still crossing.
            if (!headingIntoJunction && !insideJunction)
                _committedToJunction.Remove(egoActorId);
            else if (!trafficLightHazard)
            {
                // Report the moment a vehicle commits, and how far ahead the junction waypoint that
                // granted it actually was. Commitment is what suppresses the red-light stop, so a
                // vehicle that commits while the junction is still tens of metres away will drive
                // through the light — and the buffer head can be well ahead of the vehicle, because
                // a lane change replaces the whole buffer with a change-over point walked up to
                // MAX_WPT_DISTANCE down the new lane.
                if (_committedToJunction.Add(egoActorId) && egoBuffer is { Count: > 0 }
                    && TrafficReport.DiagnosticsEnabled)
                {
                    float ahead = MathF.Sqrt(
                        egoBuffer[0].DistanceSquared(_simulationState.GetLocation(egoActorId)));
                    TrafficReport.Writer.WriteLine(
                        $"{DateTime.Now:HH:mm:ss.fff} [traffic] vehicle {egoActorId} committed to a "
                        + $"junction {ahead:F1} m ahead (light {trafficLightState}, "
                        + $"atLight={isAtTrafficLight}); it will not stop for that light.");
                }
            }
        }

        _output[egoActorId] = new TrafficLightFrame(trafficLightHazard);
    }

    /// <summary>
    /// Clean up all per-actor state. Called by the orchestrator when a
    /// vehicle is destroyed AND internally when a vehicle changes
    /// junctions.
    /// </summary>
    public void RemoveActor(ActorId actorId)
    {
        if (_vehicleLastJunction.TryGetValue(actorId, out var junctionId))
        {
            if (_enteringVehiclesMap.TryGetValue(junctionId, out var deque))
            {
                // LinkedList<T>.Remove returns true if found.
                var node = deque.First;
                while (node is not null)
                {
                    if (node.Value == actorId)
                    {
                        deque.Remove(node);
                        break;
                    }
                    node = node.Next;
                }
            }
            _vehicleStopTime.Remove(actorId);
            _vehicleLastJunction.Remove(actorId);
        }
        _committedToJunction.Remove(actorId);
        _output.Remove(actorId);
    }

    public void Reset()
    {
        _enteringVehiclesMap.Clear();
        _vehicleLastJunction.Clear();
        _vehicleStopTime.Clear();
        _committedToJunction.Clear();
        _lastReportedLight.Clear();
        _heldOnApproach.Clear();
        _output.Clear();
    }

    // ═════════════════════════════════════════════════════════════════
    //                Non-signalised junction arbitration
    // ═════════════════════════════════════════════════════════════════

    private void AddActorToNonSignalisedJunction(ActorId egoActorId, JuncId junctionId)
    {
        if (!_enteringVehiclesMap.TryGetValue(junctionId, out var enteringVehicles))
        {
            enteringVehicles = new LinkedList<ActorId>();
            _enteringVehiclesMap[junctionId] = enteringVehicles;
        }

        // Skip if already in the FIFO for this junction.
        var node = enteringVehicles.First;
        while (node is not null)
        {
            if (node.Value == egoActorId) return;
            node = node.Next;
        }

        enteringVehicles.AddLast(egoActorId);

        // If the actor was queued under a different junction, drop that
        // entry first (mirrors upstream: a vehicle can only be in one
        // junction queue at a time).
        if (_vehicleLastJunction.ContainsKey(egoActorId))
            RemoveActor(egoActorId);

        _vehicleLastJunction[egoActorId] = junctionId;
    }

    private bool HandleNonSignalisedJunction(
        ActorId egoActorId, JuncId junctionId, double timestamp)
    {
        bool trafficLightHazard = false;

        if (!_enteringVehiclesMap.TryGetValue(junctionId, out var enteringVehicles))
            return false;

        // Phase 1: ensure the vehicle has actually come to a stop before
        // we even start counting the stop-sign timer.
        if (!_vehicleStopTime.ContainsKey(egoActorId))
        {
            Vector3D vel = _simulationState.GetVelocity(egoActorId);
            float speed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z);
            if (speed < Constants.MotionPlan.EPSILON_RELATIVE_SPEED)
                _vehicleStopTime[egoActorId] = timestamp;
            trafficLightHazard = true;
        }
        // Phase 2: we are at the head of the FIFO — wait MINIMUM_STOP_TIME
        // seconds, then we are clear to enter.
        else if (enteringVehicles.First!.Value == egoActorId)
        {
            double entryElapsed = _vehicleStopTime[egoActorId];
            if (timestamp - entryElapsed < Constants.TrafficLight.MINIMUM_STOP_TIME)
                trafficLightHazard = true;
        }
        // Phase 3: we are not at the head of the FIFO — keep braking.
        else
        {
            trafficLightHazard = true;
        }
        return trafficLightHazard;
    }

    /// <summary>
    /// Resolve which junction is currently affecting the ego vehicle. Mirrors
    /// upstream's <c>GetAffectedJunctionId</c> control flow: look-ahead point
    /// wins unless we are already mid-arbitration for a different junction.
    /// </summary>
    private JuncId GetAffectedJunctionId(ActorId egoActorId)
    {
        if (!_bufferMap.TryGetValue(egoActorId, out var waypointBuffer)
            || waypointBuffer.Count == 0)
            return -1;

        SimpleWaypoint lookAheadPoint = LocalizationUtils
            .GetTargetWaypoint(waypointBuffer, Constants.WaypointSelection.JUNCTION_LOOK_AHEAD).Waypoint;
        SimpleWaypoint frontPoint = waypointBuffer[0];

        JuncId lookAheadJunctionId = lookAheadPoint.GetJunctionId();
        JuncId frontJunctionId = frontPoint.GetJunctionId();

        JuncId currentJunctionId = -1;
        if (_vehicleLastJunction.TryGetValue(egoActorId, out var v))
            currentJunctionId = v;

        if (currentJunctionId != -1)
        {
            if (currentJunctionId == lookAheadJunctionId)
                return lookAheadJunctionId;
            if (lookAheadJunctionId != -1)
                return lookAheadJunctionId;
            if (currentJunctionId == frontJunctionId)
                return frontJunctionId;
            return -1;
        }
        return lookAheadJunctionId;
    }
}
