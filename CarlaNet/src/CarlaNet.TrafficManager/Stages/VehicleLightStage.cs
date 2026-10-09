// Source: carla/trafficmanager/VehicleLightStage.{h,cpp}
//
// Computes the desired vehicle-light bitmask for each registered vehicle
// based on:
//   - Hard braking (brake > 0.5 from this tick's MotionPlanStage command)
//   - Junction turn intention (look-ahead RoadOption on the waypoint buffer)
//   - Weather (precipitation, fog density, sun altitude)
//
// The output is collected by the orchestrator and shipped to the simulator
// as a batch of <c>SetVehicleLightStateCommand</c>. We do NOT call any RPC
// inside Update; the only RPC interaction is the per-tick refresh in
// <see cref="UpdateWorldInfo"/>.
//
// Threading: single-threaded by contract. The orchestrator's worker thread
// is the only caller.
#nullable enable

using CarlaNet.Transport;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;
using CarlaNet.Types.Rpc.Lighting;

namespace CarlaNet.TrafficManager.Stages;

internal sealed class VehicleLightStage : IStageWithRemoveActor
{
    // ── Dependencies (held by reference) ────────────────────────────────
    private readonly List<ActorId> _vehicleIdList;
    private readonly BufferMap _bufferMap;
    private readonly Parameters _parameters;
    private readonly CarlaClient _client;

    /// <summary>
    /// Per-tick control frame (one entry per registered vehicle) populated
    /// by MotionPlanStage. We scan it to read each vehicle's brake value.
    /// Owned by the orchestrator (Wave 4); we hold the reference.
    /// </summary>
    private readonly List<Command> _controlFrame;

    // ── World snapshot (refreshed by UpdateWorldInfo) ───────────────────
    private IReadOnlyList<(ActorId Id, VehicleLightStateFlags Flags)> _allLightStates;
    private WeatherParameters _weather;
    private bool _isWeatherEnabled;
    private long _lightStatesReadAtTicks = long.MinValue;
    private long _weatherReadAtTicks = long.MinValue;

    // How stale each of these may get. Upstream re-reads both every tick, which costs three
    // blocking round trips before a single vehicle has been looked at; the traffic manager needs
    // several in a row before it can command anything, and in asynchronous mode the simulator
    // serves them one per rendered frame, so each one is a frame on the control loop's period.
    //
    // Neither reading justifies that. Weather is a scene setting that changes on an operator's
    // command, if at all, and is consulted only for thresholds on sun altitude, precipitation and
    // fog -- whether it is dark enough for headlights, wet enough for fog lights. The light states
    // are this stage's own feedback, used to avoid re-sending a value a vehicle already has; a
    // quarter-second-old answer at worst re-sends one command.
    private const long WeatherRefreshIntervalMs = 1000;
    private const long LightStateRefreshIntervalMs = 250;

    // ── Output collection (drained by orchestrator) ─────────────────────
    /// <summary>
    /// Map of actor → desired light state, populated by <see cref="Update"/>
    /// when the desired state differs from the current one. The orchestrator
    /// drains this via <see cref="GetLightStateUpdates"/> at the end of the
    /// tick and emits <see cref="SetVehicleLightStateCommand"/> per entry.
    /// </summary>
    private readonly Dictionary<ActorId, VehicleLightStateFlags> _pendingUpdates = new();

    public VehicleLightStage(
        List<ActorId> vehicleIdList,
        BufferMap bufferMap,
        Parameters parameters,
        CarlaClient client,
        List<Command> controlFrame)
    {
        _vehicleIdList = vehicleIdList;
        _bufferMap = bufferMap;
        _parameters = parameters;
        _client = client;
        _controlFrame = controlFrame;
        _allLightStates = Array.Empty<(ActorId, VehicleLightStateFlags)>();
        _weather = default;
        _isWeatherEnabled = false;
    }

    // ── Per-tick world refresh (orchestrator calls once per tick) ───────

    /// <summary>
    /// Refreshes the per-tick view of the world: the full vehicle-light
    /// state list and the current weather. Should be called exactly once
    /// per tick before any <see cref="Update"/> invocation.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>VehicleLightStage::UpdateWorldInfo</c> issues
    /// <c>get_vehicle_light_states</c>, <c>is_weather_enabled</c> and
    /// <c>get_weather_parameters</c> on every tick. Here each is on its own
    /// interval instead — see the constants above for why neither reading
    /// needs to be tick-fresh, and what the round trips cost.
    /// </remarks>
    public void UpdateWorldInfo()
    {
        long now = Environment.TickCount64;

        if (now - _lightStatesReadAtTicks >= LightStateRefreshIntervalMs)
        {
            _lightStatesReadAtTicks = now;
            try
            {
                _allLightStates = _client.GetVehiclesLightStatesAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _allLightStates = Array.Empty<(ActorId, VehicleLightStateFlags)>();
                // Reported once rather than discarded. An empty light-state view is survivable -
                // the stage re-sends a value a vehicle already has - so this failure produced no
                // symptom anyone could see, and a wrong RPC name survived in it undetected.
                TMDiagnostics.WarnOnce("vehicle-light-states",
                    "[TM] reading every vehicle's light state failed; the stage will re-send light "
                    + "commands vehicles already have until it succeeds. "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (now - _weatherReadAtTicks < WeatherRefreshIntervalMs) return;
        _weatherReadAtTicks = now;
        try
        {
            _isWeatherEnabled = _client.IsWeatherEnabledAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            _isWeatherEnabled = false;
        }
        if (_isWeatherEnabled)
        {
            try
            {
                _weather = _client.GetWeatherParametersAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                _isWeatherEnabled = false;
            }
        }
    }

    /// <summary>
    /// Convenience overload: accept a weather snapshot fed in by the
    /// orchestrator (when it queries weather once per tick alongside other
    /// world data) so this stage doesn't issue its own RPC.
    /// </summary>
    public void SetWorldInfo(
        IReadOnlyList<(ActorId Id, VehicleLightStateFlags Flags)> allLightStates,
        WeatherParameters weather,
        bool isWeatherEnabled)
    {
        _allLightStates = allLightStates;
        _weather = weather;
        _isWeatherEnabled = isWeatherEnabled;
        // Supplied from outside counts as read: a caller feeding this stage should not then have
        // it go behind their back and fetch the same thing again.
        long now = Environment.TickCount64;
        _lightStatesReadAtTicks = now;
        _weatherReadAtTicks = now;
    }

    // ── Per-vehicle update (orchestrator calls in a tight loop) ─────────

    /// <summary>
    /// Compute the desired light state for one registered vehicle. Mirrors
    /// upstream's <c>VehicleLightStage::Update(index)</c> by accepting the
    /// vehicle id directly (the Wave 4 orchestrator can iterate either by
    /// index into <c>vehicleIdList</c> or by direct id — both supported).
    /// </summary>
    public void Update(ActorId actorId)
    {
        if (!_parameters.GetUpdateVehicleLights(actorId))
            return; // automatic lights are off for this vehicle.

        // ── Find current light state (search by id; the list is tiny so
        //    a linear scan beats a Dictionary allocation each call). ─────
        VehicleLightStateFlags lightStates = (VehicleLightStateFlags)uint.MaxValue;
        for (int i = 0; i < _allLightStates.Count; i++)
        {
            if (_allLightStates[i].Id == actorId)
            {
                lightStates = _allLightStates[i].Flags;
                break;
            }
        }

        bool brakeLights = false;
        bool leftTurnIndicator = false;
        bool rightTurnIndicator = false;
        bool position = false;
        bool lowBeam = false;
        bool highBeam = false;
        bool fogLights = false;

        // ── Scan the planning horizon for an upcoming turn ──────────────
        if (_bufferMap.TryGetValue(actorId, out var waypointBuffer) && waypointBuffer.Count > 0)
        {
            Location frontLocation = waypointBuffer[0].GetLocation();
            for (int i = 0; i < waypointBuffer.Count; i++)
            {
                SimpleWaypoint waypoint = waypointBuffer[i];
                if (waypoint.CheckJunction())
                {
                    RoadOption targetRo = waypoint.GetRoadOption();
                    if (targetRo == RoadOption.Left) leftTurnIndicator = true;
                    else if (targetRo == RoadOption.Right) rightTurnIndicator = true;
                    break;
                }
                if (DistanceSquared(frontLocation, waypoint.GetLocation())
                    > Constants.VehicleLight.MAX_DISTANCE_LIGHT_CHECK)
                {
                    break;
                }
            }
        }

        // ── Brake light: derived from this tick's MotionPlan command ────
        for (int cc = 0; cc < _controlFrame.Count; cc++)
        {
            if (_controlFrame[cc] is ApplyVehicleControlCommand ctrl && ctrl.Actor == actorId)
            {
                // Match upstream's >0.5 threshold so light throttle-brake
                // pulses don't flicker the brake lamp.
                brakeLights = ctrl.Control.Brake > 0.5f;
                break;
            }
        }

        // ── Weather-driven position / beams / fog ───────────────────────
        if (_isWeatherEnabled)
        {
            // Beams + positions from sunset to dawn.
            if (_weather.SunAltitudeAngle < Constants.VehicleLight.SUN_ALTITUDE_DEGREES_BEFORE_DAWN
                || _weather.SunAltitudeAngle > Constants.VehicleLight.SUN_ALTITUDE_DEGREES_AFTER_SUNSET)
            {
                position = true;
                lowBeam = true;
            }
            else if (_weather.SunAltitudeAngle < Constants.VehicleLight.SUN_ALTITUDE_DEGREES_JUST_AFTER_DAWN
                  || _weather.SunAltitudeAngle > Constants.VehicleLight.SUN_ALTITUDE_DEGREES_JUST_BEFORE_SUNSET)
            {
                position = true;
            }

            if (_weather.Precipitation > Constants.VehicleLight.HEAVY_PRECIPITATION_THRESHOLD)
            {
                position = true;
                lowBeam = true;
            }

            if (_weather.FogDensity > Constants.VehicleLight.FOG_DENSITY_THRESHOLD)
            {
                position = true;
                lowBeam = true;
                fogLights = true;
            }
        }

        // ── Compose new bitmask ─────────────────────────────────────────
        VehicleLightStateFlags newLightStates = lightStates;

        newLightStates = brakeLights
            ? newLightStates | VehicleLightStateFlags.Brake
            : newLightStates & ~VehicleLightStateFlags.Brake;

        newLightStates = leftTurnIndicator
            ? newLightStates | VehicleLightStateFlags.LeftBlinker
            : newLightStates & ~VehicleLightStateFlags.LeftBlinker;

        newLightStates = rightTurnIndicator
            ? newLightStates | VehicleLightStateFlags.RightBlinker
            : newLightStates & ~VehicleLightStateFlags.RightBlinker;

        newLightStates = position
            ? newLightStates | VehicleLightStateFlags.Position
            : newLightStates & ~VehicleLightStateFlags.Position;

        newLightStates = lowBeam
            ? newLightStates | VehicleLightStateFlags.LowBeam
            : newLightStates & ~VehicleLightStateFlags.LowBeam;

        newLightStates = highBeam
            ? newLightStates | VehicleLightStateFlags.HighBeam
            : newLightStates & ~VehicleLightStateFlags.HighBeam;

        newLightStates = fogLights
            ? newLightStates | VehicleLightStateFlags.Fog
            : newLightStates & ~VehicleLightStateFlags.Fog;

        // ── Emit a pending update only on change ────────────────────────
        if (newLightStates != lightStates)
        {
            _pendingUpdates[actorId] = newLightStates;
            // Append to the control frame so a single ApplyBatchSync at
            // the end of the tick flushes the lights alongside the
            // MotionPlan commands — matches upstream behaviour.
            _controlFrame.Add(new SetVehicleLightStateCommand(actorId, newLightStates));
        }
    }

    /// <summary>
    /// Snapshot of the pending updates produced this tick. Drained
    /// (cleared) at the end of every tick by the orchestrator. The
    /// orchestrator can use this as either the source for a separate
    /// apply-batch OR as a verification mirror of what was already
    /// appended to <c>controlFrame</c>.
    /// </summary>
    public IReadOnlyDictionary<ActorId, VehicleLightStateFlags> GetLightStateUpdates()
        => _pendingUpdates;

    /// <summary>Clears the per-tick pending updates. Call after orchestrator drain.</summary>
    public void ClearPendingUpdates() => _pendingUpdates.Clear();

    public void RemoveActor(ActorId actorId)
    {
        // Upstream's VehicleLightStage::RemoveActor is a no-op (the stage
        // holds no per-actor cached state). We mirror that — but also drop
        // any in-flight pending update so a destroyed actor doesn't show
        // up in the orchestrator's drain.
        _pendingUpdates.Remove(actorId);
    }

    public void Reset()
    {
        _pendingUpdates.Clear();
        _allLightStates = Array.Empty<(ActorId, VehicleLightStateFlags)>();
        _isWeatherEnabled = false;
        // A new episode is a new world: re-read both on the next tick rather than waiting out an
        // interval against readings that belong to the previous one.
        _lightStatesReadAtTicks = long.MinValue;
        _weatherReadAtTicks = long.MinValue;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static float DistanceSquared(Location a, Location b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
