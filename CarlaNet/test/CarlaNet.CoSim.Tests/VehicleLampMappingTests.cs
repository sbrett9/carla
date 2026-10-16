using CarlaNet.Sumo;
using CarlaNet.Types.Rpc.Lighting;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// SUMO's signal word reaches CARLA's lamps bit by bit, never by a cast, and never asserts a lamp
/// SUMO has no model for.
/// </summary>
/// <remarks>
/// The words are the eight the sizing scenario was measured producing, and nothing else: 0, brake,
/// left, right, brake with either, brake with both, and both.
/// </remarks>
public sealed class VehicleLampMappingTests
{
    [Theory]
    [InlineData(0, VehicleLightStateFlags.None)]
    [InlineData(1, VehicleLightStateFlags.RightBlinker)]
    [InlineData(2, VehicleLightStateFlags.LeftBlinker)]
    [InlineData(3, VehicleLightStateFlags.LeftBlinker | VehicleLightStateFlags.RightBlinker)]
    [InlineData(8, VehicleLightStateFlags.Brake)]
    [InlineData(9, VehicleLightStateFlags.Brake | VehicleLightStateFlags.RightBlinker)]
    [InlineData(10, VehicleLightStateFlags.Brake | VehicleLightStateFlags.LeftBlinker)]
    [InlineData(11, VehicleLightStateFlags.Brake | VehicleLightStateFlags.LeftBlinker | VehicleLightStateFlags.RightBlinker)]
    [InlineData(2048, VehicleLightStateFlags.Special1)]
    public void EachMeasuredWordLightsTheLampsItNames(int word, VehicleLightStateFlags lamps)
    {
        Assert.Equal(lamps, VehicleLampMapping.FromSumo((SumoVehicleSignals)word));
    }

    [Fact]
    public void ACastWouldLightTheWrongLamps()
    {
        // The trap the mapping exists to avoid: SUMO's right blinker is CARLA's parking light by value,
        // and its left blinker CARLA's dipped beam.
        Assert.Equal(VehicleLightStateFlags.Position, (VehicleLightStateFlags)(uint)SumoVehicleSignals.BlinkerRight);
        Assert.Equal(VehicleLightStateFlags.LowBeam, (VehicleLightStateFlags)(uint)SumoVehicleSignals.BlinkerLeft);
        Assert.Equal(VehicleLightStateFlags.RightBlinker, VehicleLampMapping.FromSumo(SumoVehicleSignals.BlinkerRight));
        Assert.Equal(VehicleLightStateFlags.LeftBlinker, VehicleLampMapping.FromSumo(SumoVehicleSignals.BlinkerLeft));
    }

    [Theory]
    [InlineData(4, VehicleLightStateFlags.LeftBlinker | VehicleLightStateFlags.RightBlinker)]
    [InlineData(128, VehicleLightStateFlags.Reverse)]
    [InlineData(4096, VehicleLightStateFlags.Special1)]
    [InlineData(8192, VehicleLightStateFlags.Special2)]
    [InlineData(16, VehicleLightStateFlags.None)]
    [InlineData(32, VehicleLightStateFlags.None)]
    [InlineData(64, VehicleLightStateFlags.None)]
    [InlineData(256 | 512 | 1024, VehicleLightStateFlags.None)]
    public void TheBitsSumoDeclaresAndNeverWritesMapWhereCarlaHasALampAndNowhereElse(int word,
                                                                                   VehicleLightStateFlags lamps)
    {
        Assert.Equal(lamps, VehicleLampMapping.FromSumo((SumoVehicleSignals)word));
    }

    [Fact]
    public void NoSignalWordEverLightsAHeadlightAHighBeamAFogLampOrTheInterior()
    {
        // Headlights come from the sun, and the rest would be a driver decision the scenario never made.
        const VehicleLightStateFlags Never = VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam
                                             | VehicleLightStateFlags.HighBeam | VehicleLightStateFlags.Fog
                                             | VehicleLightStateFlags.Interior;
        for (int word = 0; word < 16384; word++)
        {
            Assert.Equal(VehicleLightStateFlags.None, VehicleLampMapping.FromSumo((SumoVehicleSignals)word) & Never);
        }
    }
}
