namespace CarlaNet.Sumo;

/// <summary>
/// SUMO's per-vehicle signal word, as delivered by the <c>VAR_SIGNALS</c> subscription.
/// </summary>
/// <remarks>
/// SUMO declares fourteen signal bits but its microsimulation only ever writes the ones named here:
/// the two blinkers and the brake light on the ordinary path, and the blue light for a vehicle whose
/// <c>vClass</c> is <c>emergency</c>. The rest -- front light, fog light, high beam, back-drive,
/// wiper, the two door bits, and the red and yellow emergency lights -- appear in SUMO's enumeration
/// and nowhere else in its source, so a consumer that waited for them would wait forever. Hazard
/// lights are not a bit either: SUMO expresses them as both blinkers at once.
///
/// <para><b>These values are SUMO's and do not line up with CARLA's vehicle light flags.</b> SUMO
/// uses 1 for the right blinker where CARLA uses 0x10, and 2 for the left where CARLA uses 0x20, so
/// casting one to the other turns a vehicle indicating right into a vehicle with its parking lights
/// and dipped beam on. Whatever maps these to a renderer maps them bit by bit.</para>
/// </remarks>
[Flags]
public enum SumoVehicleSignals
{
    /// <summary>No signal is lit.</summary>
    None = 0,

    /// <summary>
    /// Right indicator. SUMO lights it for a lane change and also ahead of a junction turn, from
    /// roughly seven seconds of travel out, so it carries intent before the manoeuvre.
    /// </summary>
    BlinkerRight = 1,

    /// <summary>Left indicator, on the same rule as <see cref="BlinkerRight"/>.</summary>
    BlinkerLeft = 2,

    /// <summary>
    /// Brake light. SUMO switches it off for a vehicle halted at a scheduled stop, so a parked
    /// vehicle shows no brake light even though it is stationary.
    /// </summary>
    BrakeLight = 8,

    /// <summary>
    /// Emergency blue light, toggled once a second. Only ever set for a vehicle whose type declares
    /// <c>vClass="emergency"</c>; no other vehicle class can produce it.
    /// </summary>
    EmergencyBlue = 2048,
}
