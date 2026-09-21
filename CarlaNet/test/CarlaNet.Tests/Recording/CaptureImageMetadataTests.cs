// What a written capture carries in its own image file, read back out of the PNG rather than out of
// the formatter that produced it. Two leaks lived here undetected because a PNG looks opaque to
// anything walking a file tree: carla:solar embedded the sun policy (whether this run was set to
// advance its sun, and how fast) and carla:capture embedded the run configuration (which scenario,
// which seed). Neither is observable by a fielded system with the same sensor, navigation solution,
// clock and public reference data, and a seed or a scenario name is a handle on a whole set of
// scenes, so a corpus carrying them can be learned by run rather than by scene.
//
// The split is made at the writer, so this asserts both halves of it: the imagery loses those fields
// and the Cursor-on-Target sidecar keeps them. See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/08_Collection_And_EPoL.md §9.7.
using System.Buffers.Binary;
using System.Text;
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class CaptureImageMetadataTests
{
    // The eleven doubles the world observer streams: solar state, then the two policy values.
    private static readonly double[] Solar =
        { 6.001808, 2026, 9, 16, -6.992299, 39.59431, -104.88449, 2.661484, 88.800522, 1.0, 1.0 };

    private static CaptureIdentity Capture() =>
        new(64798, 15.538677, "run-20260916-195810", "Shahid_Bahonar_Port_PatternOfLife", 103);

    private static SensorPose Platform() =>
        new("a-f-A-M-F-Q", "OVERWATCH", "CARLA-SENSOR-102",
            39.5940521, -104.8838477, 1907.41, 1.23,
            297.433, -68.912, 0.0, 297.4, 0.0,
            2888, 2160, 2181.65, 2181.65, 1444, 1080, 67.0, 52.674,
            "sensor.camera.rgb", "pinhole", "none");

    /// A real capture as the recorder writes one: a PNG carrying the three metadata chunks.
    private static Dictionary<string, string> WriteAndReadBackChunks()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        try
        {
            var chunks = SolarMetadata.PngTextChunks(Solar)
                .Concat(SensorMetadata.PngTextChunks(Platform()))
                .Concat(Capture().PngTextChunks());
            PngEncoder.WriteBgraToFile(new byte[4 * 4 * 4], 4, 4, path, chunks);
            return ReadTextChunks(path);
        }
        finally { File.Delete(path); }
    }

    /// Every tEXt chunk in a PNG, by keyword — the file as a consumer sees it, not as it was built.
    private static Dictionary<string, string> ReadTextChunks(string path)
    {
        var found = new Dictionary<string, string>();
        byte[] bytes = File.ReadAllBytes(path);
        int offset = 8;   // the PNG signature
        while (offset + 8 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            string kind = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            if (kind == "tEXt")
            {
                int separator = data.IndexOf((byte)0);
                found[Encoding.Latin1.GetString(data[..separator])] =
                    Encoding.Latin1.GetString(data[(separator + 1)..]);
            }
            offset += 12 + length;   // length + type + data + CRC
            if (kind == "IEND") break;
        }
        return found;
    }

    [Fact]
    public void The_Image_Carries_The_Sun_State_And_Not_The_Sun_Policy()
    {
        string solar = WriteAndReadBackChunks()["carla:solar"];

        foreach (string field in new[] { "solar_time", "date", "time_zone", "lat", "lon",
                                         "sun_elevation_deg", "sun_azimuth_deg" })
            Assert.Contains($"\"{field}\":", solar);

        Assert.DoesNotContain("advancing", solar);
        Assert.DoesNotContain("rate", solar);
    }

    [Fact]
    public void The_Image_Pairs_Itself_To_Its_Sidecar_And_Names_No_Run_Configuration()
    {
        string capture = WriteAndReadBackChunks()["carla:capture"];

        Assert.Contains("\"tick\":64798", capture);
        Assert.Contains("\"sim_time_s\":15.538677", capture);
        Assert.Contains("\"run_id\":\"run-20260916-195810\"", capture);

        Assert.DoesNotContain("scenario_id", capture);
        Assert.DoesNotContain("seed", capture);
    }

    [Fact]
    public void The_Image_Still_Describes_The_Platform_And_Its_Optics()
    {
        // The allow-list names carla:sensor rather than tolerating it: the platform's position is the
        // navigation solution and the intrinsics are the sensor, both of which a fielded observer has.
        var chunks = WriteAndReadBackChunks();

        Assert.Equal(new[] { "carla:solar", "carla:sensor", "carla:capture" }.Order(),
                     chunks.Keys.Order());
        Assert.Contains("\"uid\":\"CARLA-SENSOR-102\"", chunks["carla:sensor"]);
        Assert.Contains("\"hfov_deg\":67", chunks["carla:sensor"]);
    }

    [Fact]
    public void The_Sidecar_Keeps_Everything_The_Image_Gave_Up()
    {
        // Nothing is discarded, only moved: the split is between two artifacts, not a deletion.
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        string xml;
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc),
                                  Array.Empty<VehicleTelemetry>(), "n", 3.0,
                                  Solar, Platform(), Capture());
            xml = File.ReadAllText(path);
        }
        finally { File.Delete(path); }

        Assert.Contains("advancing=\"true\"", xml);
        Assert.Contains("rate=\"1\"", xml);
        Assert.Contains("scenario_id=\"Shahid_Bahonar_Port_PatternOfLife\"", xml);
        Assert.Contains("seed=\"103\"", xml);
    }

    [Fact]
    public void A_Chunk_Written_Before_The_Split_Would_Have_Failed_These_Checks()
    {
        // The shape of the 54 captures already on disk, so that what these tests reject is stated
        // rather than assumed. Measured from SCTMV_2026.09.16_12.58.29.046.png.
        const string shipped =
            "{\"solar_time\":6.001808,\"date\":\"2026-09-16\",\"time_zone\":-6.992299," +
            "\"lat\":39.59431,\"lon\":-104.88449,\"sun_elevation_deg\":2.661484," +
            "\"sun_azimuth_deg\":88.800522,\"advancing\":true,\"rate\":1}";

        Assert.Contains("advancing", shipped);
        Assert.NotEqual(shipped, SolarMetadata.ToJson(Solar));
    }
}
