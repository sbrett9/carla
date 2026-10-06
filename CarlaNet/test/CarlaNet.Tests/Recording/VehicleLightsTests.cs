// The words a truth record's `lights` and `pose_source` attributes hold, as the owner ruled on 2026-10-06:
// the lights commanded on for a vehicle in the picture on the capture's frame, and where its drawn pose
// came from. See Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/08_Collection_And_EPoL.md §5.1.
using CarlaNet.Recording;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Recording;

public class VehicleLightsTests
{
    [Fact]
    public void The_Lights_On_Are_Written_As_Words_In_The_Order_Of_CARLA_s_Flags()
    {
        // A car at dusk braking and indicating left: its headlights from the sun, the rest from SUMO.
        VehicleLightStateFlags lights = VehicleLightStateFlags.LeftBlinker | VehicleLightStateFlags.Brake
                                        | VehicleLightStateFlags.LowBeam | VehicleLightStateFlags.Position;

        Assert.Equal("position low_beam brake left_blinker", VehicleLights.SidecarValue(lights));
    }

    [Fact]
    public void A_Vehicle_With_No_Light_On_Says_So_In_A_Word()
    {
        Assert.Equal("none", VehicleLights.SidecarValue(VehicleLightStateFlags.None));
    }

    [Fact]
    public void Every_Light_CARLA_Names_Has_Its_Own_Word()
    {
        const VehicleLightStateFlags named = (VehicleLightStateFlags)0x7FF;

        Assert.Equal("position low_beam high_beam brake right_blinker left_blinker reverse fog interior "
                     + "special1 special2", VehicleLights.SidecarValue(named));
    }

    [Fact]
    public void A_Light_No_Word_Names_Is_Written_By_Its_Bit_Rather_Than_Dropped()
    {
        Assert.Equal("brake bit11", VehicleLights.SidecarValue(VehicleLightStateFlags.Brake | (VehicleLightStateFlags)0x800));
    }

    [Theory]
    [InlineData(PoseSource.Sumo, "sumo")]
    [InlineData(PoseSource.Interpolated, "interpolated")]
    [InlineData(PoseSource.Jump, "jump")]
    [InlineData(PoseSource.Stale, "stale")]
    public void Each_Pose_Source_Has_Its_Word(PoseSource source, string word)
    {
        Assert.Equal(word, PoseSources.SidecarValue(source));
    }

    [Fact]
    public void The_Four_Words_Are_The_Owner_s_And_Every_Pose_Source_Has_One()
    {
        // The owner's words of 2026-10-06, one per member, so a member added without its word fails here.
        Assert.Equal(["sumo", "interpolated", "jump", "stale"],
                     Enum.GetValues<PoseSource>().Select(PoseSources.SidecarValue));
    }
}
