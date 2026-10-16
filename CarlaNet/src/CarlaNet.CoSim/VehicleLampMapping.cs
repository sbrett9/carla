using CarlaNet.Sumo;
using CarlaNet.Types.Rpc.Lighting;

namespace CarlaNet.CoSim;

/// <summary>
/// SUMO's signal word as CARLA's vehicle light flags, bit by bit.
/// </summary>
/// <remarks>
/// <para><b>Mapped, never cast.</b> The two bitfields share no layout: SUMO's right blinker is 1 and its
/// left 2, where CARLA's <c>Position</c> is 0x1 and <c>LowBeam</c> 0x2, so casting one to the other turns a
/// vehicle indicating right into a vehicle with its parking lights and dipped beam on, and a braking
/// vehicle (SUMO's 8) into one showing CARLA's brake light only by coincidence. Every bit here is
/// translated by name.</para>
///
/// <para><b>The table is the time-and-illumination plan's</b> (D11.8): a pure, total function of the
/// signal word and nothing else. SUMO writes only the blinkers, the brake light and, for a vehicle of
/// <c>vClass="emergency"</c>, the blue beacon (<c>MSVehicle.h:1108-1139</c>, measured over the sizing
/// scenario); the other bits it declares are mapped where CARLA has a lamp for them, for completeness,
/// and never occur. SUMO has no headlight model, so <c>Position</c> and <c>LowBeam</c> never come from
/// here; <see cref="HeadlightRule"/> supplies them from the sun. <c>HighBeam</c>, <c>Fog</c> and
/// <c>Interior</c> are never set: asserting them would be a driver decision the scenario never made.</para>
///
/// <para><b>Steady, not blinking.</b> Both words are steady intent: a blinker asserted is "the
/// indicator was on", and whether a blueprint animates it is the content's.</para>
/// </remarks>
public static class VehicleLampMapping
{
    // SUMO's declared signal bits that the microsimulation never writes, by SUMO's own names
    // (MSVehicle::Signalling). Mapped because the plan's table maps them.
    private const int BlinkerEmergency = 4;
    private const int BackDrive = 128;
    private const int EmergencyRed = 4096;
    private const int EmergencyYellow = 8192;

    /// <summary>The CARLA lamps a SUMO signal word asserts.</summary>
    public static VehicleLightStateFlags FromSumo(SumoVehicleSignals signals)
    {
        int bits = (int)signals;
        VehicleLightStateFlags lamps = VehicleLightStateFlags.None;
        if ((bits & (int)SumoVehicleSignals.BlinkerRight) != 0)
        {
            lamps |= VehicleLightStateFlags.RightBlinker;
        }

        if ((bits & (int)SumoVehicleSignals.BlinkerLeft) != 0)
        {
            lamps |= VehicleLightStateFlags.LeftBlinker;
        }

        if ((bits & BlinkerEmergency) != 0)
        {
            lamps |= VehicleLightStateFlags.LeftBlinker | VehicleLightStateFlags.RightBlinker;
        }

        if ((bits & (int)SumoVehicleSignals.BrakeLight) != 0)
        {
            lamps |= VehicleLightStateFlags.Brake;
        }

        if ((bits & BackDrive) != 0)
        {
            lamps |= VehicleLightStateFlags.Reverse;
        }

        if ((bits & ((int)SumoVehicleSignals.EmergencyBlue | EmergencyRed)) != 0)
        {
            lamps |= VehicleLightStateFlags.Special1;
        }

        if ((bits & EmergencyYellow) != 0)
        {
            lamps |= VehicleLightStateFlags.Special2;
        }

        return lamps;
    }
}
