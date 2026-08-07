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
    // Vehicles currently braking for a signal they can see but are not yet standing at, so the
    // hold and the release are each reported once rather than every tick.
    private readonly HashSet<ActorId> _heldOnApproach = new();

    // Deceleration assumed available for a planned stop at a signal, in m/s². Below what the tyres
    // could deliver, because the point is to stop short of the line rather than as late as possible.
    private const float ComfortableDecelerationMetresPerSecondSquared = 3.5f;
    // Applied to the computed stopping distance, so braking begins before the stop becomes marginal.
    private const float ApproachBrakingMargin = 1.6f;
    // Vehicles close to a red hold for it whatever their speed, so one that has crept forward or is
    // already stopped at the line stays there instead of easing over it.
    private const float MinimumSignalApproachMetres = 8.0f;
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
        CarlaClient? client = null)
    {
        _simulationState = simulationState;
        _bufferMap = bufferMap;
        _parameters = parameters;
        _random = random;
        _client = client;
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
            if (!_lastReportedLight.TryGetValue(egoActorId, out var previouslySeen) || previouslySeen != seen)
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
            bool heldByApproachingSignal = false;
            if (!committedToJunction
                && egoBuffer is { Count: > 0 }
                && egoBuffer[0].GoverningSignalId is { } approachingSignalId
                && _signalStates.TryGetValue(approachingSignalId, out TLS approachingState)
                && approachingState != TLS.Green
                && approachingState != TLS.Off
                && _parameters.GetPercentageRunningLight(egoActorId) <= _random.Next())
            {
                Vector3D velocity = _simulationState.GetVelocity(egoActorId);
                float speed = MathF.Sqrt(
                    velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z);
                // Room to stop, plus a margin so the decision is made before it is marginal. Braking
                // is the emergency stop the motion planner already applies for a light, so this is
                // the distance at which that stop still lands short of the line.
                float roomToStop =
                    speed * speed / (2f * ComfortableDecelerationMetresPerSecondSquared);
                float startBrakingAt =
                    MathF.Max(roomToStop * ApproachBrakingMargin, MinimumSignalApproachMetres);
                heldByApproachingSignal = egoBuffer[0].DistanceToGoverningSignal <= startBrakingAt;
            }

            // Report the transition either way. Being held for a signal the vehicle cannot yet be
            // standing at is the behaviour this is here to produce, and being released is what
            // distinguishes a working signal from one that has stopped traffic permanently.
            if (_heldOnApproach.Contains(egoActorId) != heldByApproachingSignal)
            {
                if (heldByApproachingSignal)
                {
                    _heldOnApproach.Add(egoActorId);
                    TrafficReport.Writer.WriteLine(
                        $"{DateTime.Now:HH:mm:ss.fff} [traffic] vehicle {egoActorId} braking for "
                        + $"signal {egoBuffer![0].GoverningSignalId} "
                        + $"{egoBuffer[0].DistanceToGoverningSignal:F1} m ahead.");
                }
                else
                {
                    _heldOnApproach.Remove(egoActorId);
                    TrafficReport.Writer.WriteLine(
                        $"{DateTime.Now:HH:mm:ss.fff} [traffic] vehicle {egoActorId} released by its "
                        + "signal.");
                }
            }

            // Case 1: at a signalised junction with a red/yellow light.
            if (isAtTrafficLight
                && trafficLightState != TLS.Green
                && trafficLightState != TLS.Off
                && _parameters.GetPercentageRunningLight(egoActorId) <= _random.Next())
            {
                if (currentJunctionId != -1)
                    RemoveActor(egoActorId);
                trafficLightHazard = !committedToJunction;
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
            if (!headingIntoJunction)
                _committedToJunction.Remove(egoActorId);
            else if (!trafficLightHazard)
            {
                // Report the moment a vehicle commits, and how far ahead the junction waypoint that
                // granted it actually was. Commitment is what suppresses the red-light stop, so a
                // vehicle that commits while the junction is still tens of metres away will drive
                // through the light — and the buffer head can be well ahead of the vehicle, because
                // a lane change replaces the whole buffer with a change-over point walked up to
                // MAX_WPT_DISTANCE down the new lane.
                if (_committedToJunction.Add(egoActorId) && egoBuffer is { Count: > 0 })
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
