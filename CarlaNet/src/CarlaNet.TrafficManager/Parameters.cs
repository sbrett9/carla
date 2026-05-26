// Source: carla/trafficmanager/Parameters.h / Parameters.cpp
//
// The tunable parameter store. Every setter from the Python API
// (carla::traffic_manager::TrafficManager::SetXxx) ultimately lands here.
// Stages read the values once per vehicle per tick, so we keep everything
// in thread-safe maps that allow lock-free reads:
//
//   - per-actor knobs → AtomicMap<ActorId, T> (a thin ConcurrentDictionary)
//   - global / scalar knobs → Interlocked-mediated `int`/`float` fields
//     stored as int32 bit patterns for atomic read/write
//
// Public surface mirrors Parameters.h 1:1 — the Wave-4 facade simply
// forwards every method onto an instance of this class.
//
// Path type uses CarlaNet's <see cref="Location"/>. Route type matches
// upstream's `std::vector<uint8_t>` (a sequence of RoadOption byte codes).
#nullable enable

namespace CarlaNet.TrafficManager;

using Path = IReadOnlyList<Location>;
using Route = IReadOnlyList<byte>;

/// <summary>
/// Thread-safe parameter store backing the user-facing TrafficManager facade.
/// </summary>
/// <remarks>
/// Every per-actor map is an <see cref="AtomicMap{TKey, TValue}"/>; every
/// scalar global value is atomically read/written. The whole class is
/// designed to be hit concurrently from (a) the worker thread running the
/// stage pipeline, (b) the RPC-server thread receiving `register_vehicle`
/// commands from the simulator, and (c) the user's Python thread calling
/// `tm.set_xxx`.
/// </remarks>
internal sealed class Parameters
{
    // ── Per-actor maps (Parameters.h:41–101) ───────────────────────────
    private readonly AtomicMap<ActorId, float> _percentageDifferenceFromSpeedLimit = new();
    private readonly AtomicMap<ActorId, float> _laneOffset = new();
    private readonly AtomicMap<ActorId, float> _exactDesiredSpeed = new();
    private readonly AtomicMap<ActorId, AtomicActorSet> _ignoreCollision = new();
    private readonly AtomicMap<ActorId, float> _distanceToLeadingVehicle = new();
    private readonly AtomicMap<ActorId, ChangeLaneInfo> _forceLaneChange = new();
    private readonly AtomicMap<ActorId, bool> _autoLaneChange = new();
    private readonly AtomicMap<ActorId, float> _percRunTrafficLight = new();
    private readonly AtomicMap<ActorId, float> _percRunTrafficSign = new();
    private readonly AtomicMap<ActorId, float> _percIgnoreWalkers = new();
    private readonly AtomicMap<ActorId, float> _percIgnoreVehicles = new();
    private readonly AtomicMap<ActorId, float> _percKeepSlowLane = new();
    private readonly AtomicMap<ActorId, float> _percRandomLeft = new();
    private readonly AtomicMap<ActorId, float> _percRandomRight = new();
    private readonly AtomicMap<ActorId, bool> _autoUpdateVehicleLights = new();
    private readonly AtomicMap<ActorId, bool> _uploadPath = new();
    private readonly AtomicMap<ActorId, IReadOnlyList<Location>> _customPath = new();
    private readonly AtomicMap<ActorId, bool> _uploadRoute = new();
    private readonly AtomicMap<ActorId, IReadOnlyList<byte>> _customRoute = new();

    // ── Global scalars ─────────────────────────────────────────────────
    // C++ uses std::atomic<float>/<bool>. C# has no Interlocked.Read(float),
    // so we store floats reinterpret-cast to int via Single-bit conversions
    // and use Interlocked.Exchange/CompareExchange. For bools we use int 0/1.
    // The cost of one extra Int↔Single bit-cast per read is dwarfed by the
    // surrounding stage work — and it gives us a defect-free release-store
    // / acquire-load semantics on all CLRs.
    private int _globalPercentageDifferenceBits = SingleToInt32Bits(0f);
    private int _globalLaneOffsetBits = SingleToInt32Bits(0f);
    private int _synchronousMode;      // 0 = false, 1 = true
    private int _distanceMarginBits = SingleToInt32Bits(2.0f);
    private int _hybridPhysicsMode;
    private int _respawnDormantVehicles;
    private int _respawnLowerBoundBits = SingleToInt32Bits(100.0f);
    private int _respawnUpperBoundBits = SingleToInt32Bits(1000.0f);
    // Configurable bounds set by SetMaxBoundaries — NOT atomic in C++ either.
    private float _minLowerBound;
    private float _maxUpperBound;
    private int _hybridPhysicsRadiusBits = SingleToInt32Bits(70.0f);
    private int _osmMode = 1;  // C++ default: true

    // Synchronous-mode timeout (milliseconds). Stored as bits of double for
    // Interlocked.Exchange64 semantics; default 10 ms.
    private long _synchronousTimeOutMsBits = BitConverter.DoubleToInt64Bits(10.0);

    public Parameters()
    {
        // Matches the C++ constructor (Parameters.cpp:13–17): default sync
        // timeout 10 ms. All other defaults are set inline above.
    }

    // ═════════════════════════════════════════════════════════════════
    //                            SETTERS
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Set a vehicle's % decrease in velocity w.r.t. the speed limit.
    /// Negative values mean a % increase. Clamped to ≤100. Resetting this
    /// clears any prior SetDesiredSpeed.
    /// </summary>
    public void SetPercentageSpeedDifference(ActorId actorId, float percentage)
    {
        float newPercentage = Math.Min(100.0f, percentage);
        _percentageDifferenceFromSpeedLimit.AddEntry(actorId, newPercentage);
        if (_exactDesiredSpeed.Contains(actorId))
            _exactDesiredSpeed.RemoveEntry(actorId);
    }

    /// <summary>Right-positive lane offset displacement from centerline.</summary>
    public void SetLaneOffset(ActorId actorId, float offset)
        => _laneOffset.AddEntry(actorId, offset);

    /// <summary>Set the vehicle's exact target velocity (m/s). Negative values
    /// are clamped to 0. Clears any prior SetPercentageSpeedDifference.</summary>
    public void SetDesiredSpeed(ActorId actorId, float value)
    {
        float newValue = Math.Max(0.0f, value);
        _exactDesiredSpeed.AddEntry(actorId, newValue);
        if (_percentageDifferenceFromSpeedLimit.Contains(actorId))
            _percentageDifferenceFromSpeedLimit.RemoveEntry(actorId);
    }

    /// <summary>Set the global % decrease in velocity vs speed limit. ≤100.</summary>
    public void SetGlobalPercentageSpeedDifference(float percentage)
    {
        float newPercentage = Math.Min(100.0f, percentage);
        Interlocked.Exchange(ref _globalPercentageDifferenceBits, SingleToInt32Bits(newPercentage));
    }

    public void SetGlobalLaneOffset(float offset)
        => Interlocked.Exchange(ref _globalLaneOffsetBits, SingleToInt32Bits(offset));

    /// <summary>
    /// Toggle collision detection between a specific pair of vehicles.
    /// Mirrors Parameters.cpp:75–99 — when <paramref name="detectCollision"/>
    /// is false, <paramref name="otherActor"/> is added to the reference's
    /// ignore set; when true, it's removed.
    /// </summary>
    public void SetCollisionDetection(ActorId referenceActorId, ActorId otherActorId, Actor otherActor, bool detectCollision)
    {
        if (detectCollision)
        {
            if (_ignoreCollision.TryGetValue(referenceActorId, out var actorSet))
            {
                if (actorSet.Contains(otherActorId))
                    actorSet.Remove(new[] { otherActorId });
            }
        }
        else
        {
            if (_ignoreCollision.TryGetValue(referenceActorId, out var actorSet))
            {
                if (!actorSet.Contains(otherActorId))
                    actorSet.Insert(new[] { otherActor });
            }
            else
            {
                var newSet = new AtomicActorSet();
                newSet.Insert(new[] { otherActor });
                _ignoreCollision.AddEntry(referenceActorId, newSet);
            }
        }
    }

    /// <summary>Force lane change. <paramref name="direction"/>: true = left, false = right.</summary>
    public void SetForceLaneChange(ActorId actorId, bool direction)
        => _forceLaneChange.AddEntry(actorId, new ChangeLaneInfo(ChangeLane: true, Direction: direction));

    public void SetKeepSlowLanePercentage(ActorId actorId, float percentage)
        => _percKeepSlowLane.AddEntry(actorId, percentage);

    public void SetRandomLeftLaneChangePercentage(ActorId actorId, float percentage)
        => _percRandomLeft.AddEntry(actorId, percentage);

    public void SetRandomRightLaneChangePercentage(ActorId actorId, float percentage)
        => _percRandomRight.AddEntry(actorId, percentage);

    public void SetUpdateVehicleLights(ActorId actorId, bool doUpdate)
        => _autoUpdateVehicleLights.AddEntry(actorId, doUpdate);

    public void SetAutoLaneChange(ActorId actorId, bool enable)
        => _autoLaneChange.AddEntry(actorId, enable);

    public void SetDistanceToLeadingVehicle(ActorId actorId, float distance)
    {
        float newDistance = Math.Max(0.0f, distance);
        _distanceToLeadingVehicle.AddEntry(actorId, newDistance);
    }

    public void SetSynchronousMode(bool modeSwitch = true)
        => Interlocked.Exchange(ref _synchronousMode, modeSwitch ? 1 : 0);

    public void SetSynchronousModeTimeOutInMiliSecond(double time)
        => Interlocked.Exchange(ref _synchronousTimeOutMsBits, BitConverter.DoubleToInt64Bits(time));

    public void SetGlobalDistanceToLeadingVehicle(float dist)
        => Interlocked.Exchange(ref _distanceMarginBits, SingleToInt32Bits(dist));

    public void SetPercentageRunningLight(ActorId actorId, float perc)
        => _percRunTrafficLight.AddEntry(actorId, Math.Clamp(perc, 0.0f, 100.0f));

    public void SetPercentageRunningSign(ActorId actorId, float perc)
        => _percRunTrafficSign.AddEntry(actorId, Math.Clamp(perc, 0.0f, 100.0f));

    public void SetPercentageIgnoreVehicles(ActorId actorId, float perc)
        => _percIgnoreVehicles.AddEntry(actorId, Math.Clamp(perc, 0.0f, 100.0f));

    public void SetPercentageIgnoreWalkers(ActorId actorId, float perc)
        => _percIgnoreWalkers.AddEntry(actorId, Math.Clamp(perc, 0.0f, 100.0f));

    public void SetHybridPhysicsMode(bool modeSwitch)
        => Interlocked.Exchange(ref _hybridPhysicsMode, modeSwitch ? 1 : 0);

    public void SetHybridPhysicsRadius(float radius)
    {
        float newRadius = Math.Max(radius, 0.0f);
        Interlocked.Exchange(ref _hybridPhysicsRadiusBits, SingleToInt32Bits(newRadius));
    }

    public void SetOSMMode(bool modeSwitch)
        => Interlocked.Exchange(ref _osmMode, modeSwitch ? 1 : 0);

    public void SetRespawnDormantVehicles(bool modeSwitch)
        => Interlocked.Exchange(ref _respawnDormantVehicles, modeSwitch ? 1 : 0);

    /// <summary>
    /// Set the [lower, upper] respawn distance band for dormant vehicles,
    /// clamped to whatever bounds <see cref="SetMaxBoundaries"/> previously
    /// configured.
    /// </summary>
    public void SetBoundariesRespawnDormantVehicles(float lowerBound, float upperBound)
    {
        float lower = _minLowerBound > lowerBound ? _minLowerBound : lowerBound;
        float upper = _maxUpperBound < upperBound ? _maxUpperBound : upperBound;
        Interlocked.Exchange(ref _respawnLowerBoundBits, SingleToInt32Bits(lower));
        Interlocked.Exchange(ref _respawnUpperBoundBits, SingleToInt32Bits(upper));
    }

    /// <summary>Configure the absolute boundaries that
    /// <see cref="SetBoundariesRespawnDormantVehicles"/> clamps against.</summary>
    public void SetMaxBoundaries(float lower, float upper)
    {
        _minLowerBound = lower;
        _maxUpperBound = upper;
    }

    /// <summary>
    /// Install a user-supplied path (list of locations) on a vehicle.
    /// <paramref name="emptyBuffer"/> controls whether the LocalizationStage
    /// flushes the existing horizon buffer first.
    /// </summary>
    public void SetCustomPath(ActorId actorId, IReadOnlyList<Location> path, bool emptyBuffer)
    {
        _customPath.AddEntry(actorId, path);
        _uploadPath.AddEntry(actorId, emptyBuffer);
    }

    /// <summary>
    /// If <paramref name="removePath"/> is true, drops the path entirely; if
    /// false, only the upload flag is cleared (the path stays cached for
    /// re-installation). Mirrors Parameters.cpp:204–210.
    /// </summary>
    public void RemoveUploadPath(ActorId actorId, bool removePath)
    {
        if (!removePath)
            _uploadPath.RemoveEntry(actorId);
        else
            _customPath.RemoveEntry(actorId);
    }

    /// <summary>Replace the cached path without touching the upload flag.</summary>
    public void UpdateUploadPath(ActorId actorId, IReadOnlyList<Location> path)
    {
        _customPath.RemoveEntry(actorId);
        _customPath.AddEntry(actorId, path);
    }

    /// <summary>
    /// Install a user-supplied route (sequence of <see cref="RoadOption"/>
    /// byte codes) on a vehicle.
    /// </summary>
    public void SetImportedRoute(ActorId actorId, IReadOnlyList<byte> route, bool emptyBuffer)
    {
        _customRoute.AddEntry(actorId, route);
        _uploadRoute.AddEntry(actorId, emptyBuffer);
    }

    public void RemoveImportedRoute(ActorId actorId, bool removePath)
    {
        if (!removePath)
            _uploadRoute.RemoveEntry(actorId);
        else
            _customRoute.RemoveEntry(actorId);
    }

    public void UpdateImportedRoute(ActorId actorId, IReadOnlyList<byte> route)
    {
        _customRoute.RemoveEntry(actorId);
        _customRoute.AddEntry(actorId, route);
    }

    // ═════════════════════════════════════════════════════════════════
    //                            GETTERS
    // ═════════════════════════════════════════════════════════════════

    public float GetHybridPhysicsRadius()
        => Int32BitsToSingle(Volatile.Read(ref _hybridPhysicsRadiusBits));

    public bool GetSynchronousMode() => Volatile.Read(ref _synchronousMode) != 0;

    public double GetSynchronousModeTimeOutInMiliSecond()
        => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _synchronousTimeOutMsBits));

    /// <summary>
    /// Resolve effective target velocity for a vehicle given the road's
    /// posted speed limit. Per-actor overrides take precedence over the
    /// global % difference; an explicit SetDesiredSpeed wins outright.
    /// </summary>
    public float GetVehicleTargetVelocity(ActorId actorId, float speedLimit)
    {
        float percentageDifference = Int32BitsToSingle(Volatile.Read(ref _globalPercentageDifferenceBits));

        if (_percentageDifferenceFromSpeedLimit.TryGetValue(actorId, out var perVehiclePerc))
        {
            percentageDifference = perVehiclePerc;
        }
        else if (_exactDesiredSpeed.TryGetValue(actorId, out var exactSpeed))
        {
            return exactSpeed;
        }

        return speedLimit * (1.0f - percentageDifference / 100.0f);
    }

    public float GetLaneOffset(ActorId actorId)
    {
        if (_laneOffset.TryGetValue(actorId, out var v))
            return v;
        return Int32BitsToSingle(Volatile.Read(ref _globalLaneOffsetBits));
    }

    /// <summary>
    /// Returns true if collision detection is enabled between this pair of
    /// actors. False only if the reference has explicitly added the other
    /// to its ignore set.
    /// </summary>
    public bool GetCollisionDetection(ActorId referenceActorId, ActorId otherActorId)
    {
        if (_ignoreCollision.TryGetValue(referenceActorId, out var set)
            && set.Contains(otherActorId))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the pending lane-change request and clears it (the upstream
    /// implementation does the same: see Parameters.cpp:297). Subsequent
    /// calls before a re-set return <c>{false, false}</c>.
    /// </summary>
    public ChangeLaneInfo GetForceLaneChange(ActorId actorId)
    {
        var changeLaneInfo = new ChangeLaneInfo(false, false);
        if (_forceLaneChange.TryGetValue(actorId, out var v))
            changeLaneInfo = v;
        _forceLaneChange.RemoveEntry(actorId);
        return changeLaneInfo;
    }

    /// <summary>Returns -1 if no per-actor override is set.</summary>
    public float GetKeepSlowLanePercentage(ActorId actorId)
        => _percKeepSlowLane.TryGetValue(actorId, out var v) ? v : -1.0f;

    /// <summary>Returns -1 if no per-actor override is set.</summary>
    public float GetRandomLeftLaneChangePercentage(ActorId actorId)
        => _percRandomLeft.TryGetValue(actorId, out var v) ? v : -1.0f;

    /// <summary>Returns -1 if no per-actor override is set.</summary>
    public float GetRandomRightLaneChangePercentage(ActorId actorId)
        => _percRandomRight.TryGetValue(actorId, out var v) ? v : -1.0f;

    /// <summary>Default policy is auto-lane-change enabled.</summary>
    public bool GetAutoLaneChange(ActorId actorId)
        => _autoLaneChange.TryGetValue(actorId, out var v) ? v : true;

    /// <summary>
    /// Returns the per-actor leading-vehicle distance margin if set;
    /// otherwise the global margin.
    /// </summary>
    public float GetDistanceToLeadingVehicle(ActorId actorId)
    {
        if (_distanceToLeadingVehicle.TryGetValue(actorId, out var v))
            return v;
        return Int32BitsToSingle(Volatile.Read(ref _distanceMarginBits));
    }

    public float GetPercentageRunningLight(ActorId actorId)
        => _percRunTrafficLight.TryGetValue(actorId, out var v) ? v : 0.0f;

    public float GetPercentageRunningSign(ActorId actorId)
        => _percRunTrafficSign.TryGetValue(actorId, out var v) ? v : 0.0f;

    public float GetPercentageIgnoreWalkers(ActorId actorId)
        => _percIgnoreWalkers.TryGetValue(actorId, out var v) ? v : 0.0f;

    public float GetPercentageIgnoreVehicles(ActorId actorId)
        => _percIgnoreVehicles.TryGetValue(actorId, out var v) ? v : 0.0f;

    public bool GetUpdateVehicleLights(ActorId actorId)
        => _autoUpdateVehicleLights.TryGetValue(actorId, out var v) ? v : false;

    public bool GetHybridPhysicsMode() => Volatile.Read(ref _hybridPhysicsMode) != 0;

    public bool GetRespawnDormantVehicles() => Volatile.Read(ref _respawnDormantVehicles) != 0;

    public float GetLowerBoundaryRespawnDormantVehicles()
        => Int32BitsToSingle(Volatile.Read(ref _respawnLowerBoundBits));

    public float GetUpperBoundaryRespawnDormantVehicles()
        => Int32BitsToSingle(Volatile.Read(ref _respawnUpperBoundBits));

    public bool GetOSMMode() => Volatile.Read(ref _osmMode) != 0;

    public bool GetUploadPath(ActorId actorId)
        => _uploadPath.TryGetValue(actorId, out var v) ? v : false;

    /// <summary>Returns an empty list if no path is currently uploaded.</summary>
    public IReadOnlyList<Location> GetCustomPath(ActorId actorId)
        => _customPath.TryGetValue(actorId, out var v) ? v : Array.Empty<Location>();

    public bool GetUploadRoute(ActorId actorId)
        => _uploadRoute.TryGetValue(actorId, out var v) ? v : false;

    /// <summary>Returns an empty list if no route is currently uploaded.</summary>
    public IReadOnlyList<byte> GetImportedRoute(ActorId actorId)
        => _customRoute.TryGetValue(actorId, out var v) ? v : Array.Empty<byte>();

    // ═════════════════════════════════════════════════════════════════
    //                  Float ↔ Int32 bitcast helpers
    // ═════════════════════════════════════════════════════════════════
    // BitConverter.SingleToInt32Bits exists from .NET 6+, but we wrap it so
    // the call sites read like the C++ atomic load/store pattern.
    private static int SingleToInt32Bits(float value) => BitConverter.SingleToInt32Bits(value);
    private static float Int32BitsToSingle(int value) => BitConverter.Int32BitsToSingle(value);
}
