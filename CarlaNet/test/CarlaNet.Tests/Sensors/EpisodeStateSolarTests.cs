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
using CarlaNet.Types.Streaming;

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

    /// A header as a server that carries the refraction-corrected elevation writes it -- twelve
    /// solar doubles, the layout flag set whatever the sun -- followed by one actor record.
    private static byte[] MakeWideHeaderWithOneActor(bool sun, uint actorId)
    {
        const int WideHeaderSize = 132;
        const int ActorSize = 119;
        var bytes = new byte[WideHeaderSize + ActorSize];
        var span = bytes.AsSpan();
        bytes[32] = (byte)(SimulationState.SolarCorrectedElevationCarried
                           | (sun ? SimulationState.SolarStateValid : SimulationState.None));
        double[] block = [.. MeasuredSun, 18.42];
        for (int k = 0; k < block.Length; k++)
            BinaryPrimitives.WriteInt64LittleEndian(
                span[(SolarOffset + k * 8)..], BitConverter.DoubleToInt64Bits(block[k]));
        BinaryPrimitives.WriteUInt32LittleEndian(span[WideHeaderSize..], actorId);
        return bytes;
    }

    [Fact]
    public void The_Corrected_Elevation_Is_Read_Where_The_Header_Carries_It()
    {
        var data = EpisodeStateSensorData.Deserialize(MakeWideHeaderWithOneActor(sun: true, 4242));

        Assert.Equal(12, data.Header.Solar.Count);
        Assert.Equal(18.38, data.Header.Solar[7]);   // geometric
        Assert.Equal(18.42, data.Header.Solar[11]);  // what the frame was lit at
        Assert.Contains("\"sun_corrected_elevation_deg\":18.42", SolarMetadata.ToJson(data.Header.Solar));

        // The actors start after the wider header, not eight bytes into it.
        Assert.Equal(4242u, Assert.Single(data.Actors).Id);
    }

    [Fact]
    public void The_Layout_Flag_Places_The_Actors_Even_When_There_Is_No_Sun()
    {
        var data = EpisodeStateSensorData.Deserialize(MakeWideHeaderWithOneActor(sun: false, 77));

        Assert.Empty(data.Header.Solar);
        Assert.Equal(77u, Assert.Single(data.Actors).Id);
    }

    [Fact]
    public void A_Server_That_Carries_Only_The_Geometric_Elevation_Is_Still_Read()
    {
        // A header from a server built before the corrected elevation was carried: eleven doubles,
        // no layout flag, actors at offset 124. A reader that assumed the wide layout would read
        // every actor eight bytes late.
        var narrow = new byte[StateHeaderSize + 119];
        MakeHeader(SimulationState.SolarStateValid, MeasuredSun).CopyTo(narrow, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(narrow.AsSpan(StateHeaderSize), 9001);

        var data = EpisodeStateSensorData.Deserialize(narrow);

        Assert.Equal(11, data.Header.Solar.Count);
        Assert.Equal(9001u, Assert.Single(data.Actors).Id);
        Assert.DoesNotContain("sun_corrected_elevation_deg", SolarMetadata.ToJson(data.Header.Solar));
        Assert.Equal(124, EpisodeStateLayout.HeaderSize(narrow));
        Assert.Equal(132, EpisodeStateLayout.HeaderSize(MakeWideHeaderWithOneActor(true, 1)));
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
