using System.Globalization;
using CarlaNet.Types.Rpc.Lighting;

namespace CarlaNet.Recording;

/// <summary>
/// A vehicle's commanded lights as a truth record writes them: one word per light on, in the order of
/// CARLA's flags, or <c>none</c>.
/// </summary>
/// <remarks>
/// <para>The flags are CARLA's <c>rpc::VehicleLightState</c>, which the world observer carries for every
/// vehicle on every frame (<c>VehicleData::light_state</c>). They are what was commanded -- a SUMO drive's
/// session writes them from SUMO's signals and the sun (<c>VehicleLampMapping</c>,
/// <c>HeadlightRule</c>), the traffic manager from its own rules -- and whether a blueprint draws a lamp
/// for each, or animates a blinker, is the content's.</para>
///
/// <para>A light CARLA declares and this table does not name is written as <c>bit</c> and its bit number,
/// so no light a server carried is dropped from the record.</para>
/// </remarks>
public static class VehicleLights
{
    /// <summary>The word written where no light is on.</summary>
    public const string NoneOn = "none";

    // In flag order, so a record's words come in one order whatever set them.
    private static readonly (VehicleLightStateFlags Flag, string Word)[] Words =
    [
        (VehicleLightStateFlags.Position, "position"),
        (VehicleLightStateFlags.LowBeam, "low_beam"),
        (VehicleLightStateFlags.HighBeam, "high_beam"),
        (VehicleLightStateFlags.Brake, "brake"),
        (VehicleLightStateFlags.RightBlinker, "right_blinker"),
        (VehicleLightStateFlags.LeftBlinker, "left_blinker"),
        (VehicleLightStateFlags.Reverse, "reverse"),
        (VehicleLightStateFlags.Fog, "fog"),
        (VehicleLightStateFlags.Interior, "interior"),
        (VehicleLightStateFlags.Special1, "special1"),
        (VehicleLightStateFlags.Special2, "special2"),
    ];

    /// <summary>
    /// The lights on, as the words a truth record's <c>lights</c> attribute holds, separated by single
    /// spaces: for example <c>position low_beam brake left_blinker</c>; <see cref="NoneOn"/> where none is.
    /// </summary>
    public static string SidecarValue(VehicleLightStateFlags lights)
    {
        uint bits = (uint)lights;
        if (bits == 0)
        {
            return NoneOn;
        }

        var words = new List<string>();
        uint named = 0;
        foreach ((VehicleLightStateFlags flag, string word) in Words)
        {
            named |= (uint)flag;
            if ((bits & (uint)flag) != 0)
            {
                words.Add(word);
            }
        }

        for (int bit = 0; bit < 32; bit++)
        {
            uint mask = 1u << bit;
            if ((bits & mask) != 0 && (named & mask) == 0)
            {
                words.Add("bit" + bit.ToString(CultureInfo.InvariantCulture));
            }
        }

        return string.Join(' ', words);
    }
}
