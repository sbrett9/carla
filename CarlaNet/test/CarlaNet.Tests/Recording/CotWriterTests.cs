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

    private static string WriteWith(CaptureIdentity capture, params VehicleTelemetry[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), records,
                                  capture: capture);
            return File.ReadAllText(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_Sun_Block_States_The_Elevation_The_Frame_Was_Lit_At_Where_It_Is_Known()
    {
        double[] geometricOnly = [7.0, 2026, 3, 21, 3.5, 27.15012, 56.18065, 4.981, 119.56, 0.0, 0.0];
        double[] withCorrected = [.. geometricOnly, 5.141];

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [],
                                  solar: withCorrected);
            string xml = File.ReadAllText(path);
            Assert.Contains("sun_elevation_deg=\"4.981\"", xml);
            Assert.Contains("sun_corrected_elevation_deg=\"5.141\"", xml);

            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [],
                                  solar: geometricOnly);
            Assert.DoesNotContain("sun_corrected_elevation_deg", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    private static IlluminationDeclaration PortWindow() =>
        new("freeze_at_window_start", EpochHonoured: true, Audited: true)
        {
            EpochDigest = "979f424f6f030bacbdd659afd1adec90c00a91be4ea3df2f39ec0fd7964ab40e",
            EpochCivil = "2026-12-21T00:00:00+03:30",
            UtcOffsetHours = 3.5,
            DeclaredCivil = "2026-12-21T17:00:00.25+03:30",
            DeclaredUtc = "2026-12-21T13:30:00.25Z",
            SunDeclared = "2026-12-21T17:00:00+03:30",
            SunElevationDeclaredDegrees = -1.58296,
            SunCorrectedElevationDeclaredDegrees = -1.37418,
            DeclaredElevationKind = "refraction_corrected",
            ResidualClockSeconds = 0.001,
            ResidualDegrees = 0.0000123,
            ResidualCorrectedDegrees = -0.0000045,
        };

    [Fact]
    public void The_Illumination_Declaration_Is_Written_Beside_The_Sun_It_Was_Checked_Against()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            double[] sun = [17.0, 2026, 12, 21, 3.5, 27.15012, 56.18065, -1.58296, 244.3416, 0.0, 0.0, -1.37418];
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [],
                                  solar: sun, illumination: PortWindow());
            string xml = File.ReadAllText(path);

            Assert.Contains("<_illumination policy=\"freeze_at_window_start\" epoch_honoured=\"true\" audited=\"true\"", xml);
            Assert.Contains("declared_civil=\"2026-12-21T17:00:00.25+03:30\"", xml);
            Assert.Contains("sun_declared=\"2026-12-21T17:00:00+03:30\"", xml);
            Assert.Contains("sun_elevation_declared_deg=\"-1.583\"", xml);
            Assert.Contains("sun_corrected_elevation_declared_deg=\"-1.3742\"", xml);
            Assert.Contains("declared_elevation=\"refraction_corrected\"", xml);
            Assert.Contains("residual_deg=\"0.000012\"", xml);
            Assert.True(xml.IndexOf("<_solar", StringComparison.Ordinal)
                        < xml.IndexOf("<_illumination", StringComparison.Ordinal));

            // A run that declared nothing writes nothing, rather than an empty declaration.
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [], solar: sun);
            Assert.DoesNotContain("_illumination", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_Illumination_Chunk_Names_The_Epoch_And_Omits_What_Was_Not_Declared()
    {
        (string keyword, string json) = Assert.Single(PortWindow().PngTextChunks());
        Assert.Equal("carla:illumination", keyword);
        Assert.StartsWith("{\"policy\":\"freeze_at_window_start\",\"epoch_honoured\":true,\"audited\":true", json);
        Assert.Contains("\"epoch_digest\":\"979f424f6f03", json);
        Assert.Contains("\"utc_offset_hours\":3.5", json);
        Assert.Contains("\"residual_clock_s\":0.001", json);
        Assert.DoesNotContain("\"rate\"", json);

        string ignored = new IlluminationDeclaration("ignore", false, false).ToJson();
        Assert.Equal("{\"policy\":\"ignore\",\"epoch_honoured\":false,\"audited\":false}", ignored);
    }

    [Fact]
    public void The_Frame_The_Truth_Came_From_Is_Named_Beside_The_Frame_Of_The_Pixels()
    {
        // The image's own frame and the frame its vehicle records describe are normally the same; when
        // the client no longer held the image's frame the sidecar must say which frame it got instead.
        string exact = WriteWith(new CaptureIdentity(260042, 310.21974, "run-1", null, 103, 260042), Saloon());
        Assert.Contains("tick=\"260042\"", exact);
        Assert.Contains("telemetry_tick=\"260042\"", exact);

        string offset = WriteWith(new CaptureIdentity(260042, 310.21974, "run-1", null, 103, 260039), Saloon());
        Assert.Contains("telemetry_tick=\"260039\"", offset);
    }

    [Fact]
    public void A_Capture_Without_Truth_Names_No_Telemetry_Frame()
    {
        string xml = WriteWith(new CaptureIdentity(260042, 310.21974), Saloon());
        Assert.Contains("tick=\"260042\"", xml);
        Assert.DoesNotContain("telemetry_tick", xml);
        Assert.DoesNotContain("telemetry_tick", new CaptureIdentity(1, 0.0).ToJson());
        Assert.Contains("\"telemetry_tick\":5", new CaptureIdentity(7, 0.0, TelemetryTick: 5).ToJson());
    }

    [Fact]
    public void Measured_Occlusion_Rides_In_The_Truth_Extras()
    {
        string xml = Write(Saloon() with
        {
            Occlusion = 0.42, OcclusionLevel = 2,
            OcclusionSamples = 96, ApparentWidthPx = 48, ApparentHeightPx = 21,
        });
        Assert.Contains("occlusion=\"0.420\"", xml);
        Assert.Contains("occlusion_level=\"2\"", xml);
        Assert.Contains("occlusion_samples=\"96\"", xml);
        Assert.Contains("apparent_width_px=\"48\"", xml);
        Assert.Contains("apparent_height_px=\"21\"", xml);
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
