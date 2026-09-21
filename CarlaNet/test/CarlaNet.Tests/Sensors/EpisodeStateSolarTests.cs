// The episode-state header's solar block and the flag that says whether it means anything.
// Mirrors: LibCarla/source/carla/sensor/s11n/EpisodeStateSerializer.h (SolarStateValid) and
// Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/WorldObserver.cpp.
//
// A world with no CesiumSunSky leaves the header's solar fields at their defaults, and those
// defaults are a well-formed reading: midnight of year 0 at latitude 0, longitude 0, rate 1.0.
// Nothing in the values distinguishes that non-reading from a real sun, so the reader must go by
// the SolarStateValid flag.
using System.Buffers.Binary;
using CarlaNet.Recording;
using CarlaNet.Sensors;

namespace CarlaNet.Tests.Sensors;

public class EpisodeStateSolarTests
{
    private const int StateHeaderSize = 124;
    private const int SolarOffset = 36;

    /// A 124-byte episode-state header with no actors. <paramref name="solar"/> is the 11-double
    /// block at offset 36; null leaves it at the all-default bytes a sunless world produces.
    private static byte[] MakeHeader(SimulationState state, double[]? solar)
    {
        var bytes = new byte[StateHeaderSize];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(span, 7UL);            // episode_id
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], BitConverter.DoubleToInt64Bits(1.5));
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], BitConverter.SingleToInt32Bits(0.05f));
        bytes[32] = (byte)state;
        if (solar is not null)
        {
            for (int k = 0; k < solar.Length; k++)
                BinaryPrimitives.WriteInt64LittleEndian(
                    span[(SolarOffset + k * 8)..], BitConverter.DoubleToInt64Bits(solar[k]));
        }
        return bytes;
    }

    private static readonly double[] MeasuredSun =
    {
        7.0,          // solar_time
        2026, 3, 21,  // year, month, day
        3.5,          // time_zone (+03:30 is a real civil offset)
        27.15012, 56.18065,
        18.38,        // sun_elevation_deg
        95.4,         // sun_azimuth_deg
        0.0, 1.0      // advancing, rate
    };

    [Fact]
    public void Solar_Block_Is_Read_When_The_Header_Says_A_Sun_Was_Measured()
    {
        var payload = MakeHeader(SimulationState.SolarStateValid, MeasuredSun);

        var data = EpisodeStateSensorData.Deserialize(payload);

        Assert.Equal(11, data.Header.Solar.Count);
        Assert.Equal(7.0, data.Header.Solar[0]);
        Assert.Equal(2026, data.Header.Solar[1]);
        Assert.Equal(3.5, data.Header.Solar[4]);
        Assert.Equal(18.38, data.Header.Solar[7]);
        Assert.True(SolarMetadata.HasData(data.Header.Solar));
    }

    [Fact]
    public void A_Sunless_World_Reports_No_Sun_Rather_Than_Midnight_At_The_Origin()
    {
        // Exactly what a world with no CesiumSunSky produces: the flag clear and the solar fields
        // at their defaults. Before the flag existed this parsed as eleven valid numbers and was
        // written into the truth sidecar and the PNG chunk as a real sun.
        var payload = MakeHeader(SimulationState.None, solar: null);

        var data = EpisodeStateSensorData.Deserialize(payload);

        Assert.Empty(data.Header.Solar);
        Assert.False(SolarMetadata.HasData(data.Header.Solar));
        Assert.Empty(SolarMetadata.PngTextChunks(data.Header.Solar));
        Assert.Equal("{}", SolarMetadata.ToJson(data.Header.Solar));
    }

    [Fact]
    public void The_Flag_Decides_Not_The_Values()
    {
        // Values that look like a real sun still do not make one: only the server sets the flag,
        // and a reader that trusted the values would have no way to tell a stale block from a live
        // one either.
        var payload = MakeHeader(SimulationState.None, MeasuredSun);

        var data = EpisodeStateSensorData.Deserialize(payload);

        Assert.Empty(data.Header.Solar);
    }

    [Fact]
    public void SolarStateValid_Does_Not_Disturb_The_Other_Simulation_State_Flags()
    {
        var payload = MakeHeader(
            SimulationState.MapChange | SimulationState.PendingLightUpdate
                | SimulationState.SolarStateValid,
            MeasuredSun);

        var data = EpisodeStateSensorData.Deserialize(payload);

        Assert.True(data.Header.SimulationState.HasFlag(SimulationState.MapChange));
        Assert.True(data.Header.SimulationState.HasFlag(SimulationState.PendingLightUpdate));
        Assert.Equal(11, data.Header.Solar.Count);
    }
}
