// Guards the CoT sidecar's _carla block against the schema in
// Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md.
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class CotWriterTests
{
    private static VehicleTelemetry Saloon() => new(
        7, "vehicle.audi.tt", "car", "", "0,0,0", "autopilot",
        37.7841234, -122.4567890, 61.2, 58.0,
        11.3, 182.4, 11.2, -1.4, 0.0,
        4.5, 2.0, 1.4);

    private static string Write(params VehicleTelemetry[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), records);
            return File.ReadAllText(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Measured_Occlusion_Rides_In_The_Truth_Extras()
    {
        string xml = Write(Saloon() with { Occlusion = 0.42, OcclusionLevel = 2 });
        Assert.Contains("occlusion=\"0.420\"", xml);
        Assert.Contains("occlusion_level=\"2\"", xml);
    }

    [Fact]
    public void Unmeasured_Occlusion_Is_Absent_Rather_Than_Zero()
    {
        // An absent attribute means "not known", which is a different claim from "nothing in the way".
        string xml = Write(Saloon());
        Assert.DoesNotContain("occlusion", xml);
    }

    [Fact]
    public void The_Vehicle_Track_Still_Carries_Its_Contracted_Fields()
    {
        string xml = Write(Saloon() with { Occlusion = 0.0, OcclusionLevel = 0 });
        Assert.Contains("uid=\"CARLA-TRUTH-7\"", xml);
        Assert.Contains("type=\"a-n-G-E-V\"", xml);
        Assert.Contains("hae=\"61.20\"", xml);
        Assert.Contains("callsign=\"car-7\"", xml);
        Assert.Contains("occlusion=\"0.000\"", xml);
    }
}
