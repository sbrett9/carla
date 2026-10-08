// Guards the CoT sidecar's _carla block against the schema in
// Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md.
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Types.Illumination;
using CarlaNet.Types.Provenance;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;

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
    public void A_Sidecar_Names_Its_Format_And_Says_What_Made_It_Before_Anything_Else()
    {
        var producer = new ProducerRecord("carlacontrol.CaptureSession", "0.10.0+g1a2b3c4d5", "0.10.0+g1a2b3c4d5",
                                          ServerBuildIdentity.NotAnswered("built before the call", "0.10.0", "1.0"),
                                          "1.27.0", new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
        double[] sun = [7.0, 2026, 3, 21, 3.5, 27.15012, 56.18065, 4.981, 119.56, 0.0, 0.0];
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [Saloon()],
                                  solar: sun, capture: new CaptureIdentity(100, 5.0, "run-1"), producer: producer);
            XElement events = XDocument.Load(path).Root!;

            Assert.Equal(CotWriter.FormatVersion.ToString(), (string?)events.Attribute("format_version"));
            Assert.Equal("format_version", events.Attributes().First().Name.LocalName);
            XElement first = events.Elements().First();
            Assert.Equal("_producer", first.Name.LocalName);
            Assert.Equal("carlacontrol.CaptureSession", (string?)first.Attribute("tool"));
            Assert.Equal("0.10.0+g1a2b3c4d5", (string?)first.Attribute("carlanet"));
            Assert.Equal("1.27.0", (string?)first.Attribute("sumo"));
            Assert.Equal("2026-10-07T12:00:00.000Z", (string?)first.Attribute("written_utc"));
            Assert.Equal("false", (string?)Assert.Single(first.Elements("_server")).Attribute("available"));
            Assert.Equal("_solar", first.ElementsAfterSelf().First().Name.LocalName);

            // A writer given no record writes none, and still names its format.
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [Saloon()]);
            XElement bare = XDocument.Load(path).Root!;
            Assert.Equal("1", (string?)bare.Attribute("format_version"));
            Assert.Empty(bare.Elements("_producer"));
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

    [Fact]
    public void The_Sun_Block_Names_Its_Band_Cut_From_The_Elevation_The_Frame_Was_Lit_At()
    {
        // A sun just below the horizon whose light refraction lifts just above it: the band is the
        // corrected elevation's (golden), never the geometric one's (civil twilight), wherever the
        // block carries it; a server that carries only the geometric elevation is banded by that, and
        // the sidecar says which.
        double[] geometricOnly = [6.0, 2026, 3, 21, 3.5, 27.15012, 56.18065, -0.25, 95.4, 0.0, 0.0];
        double[] withCorrected = [.. geometricOnly, 0.31];

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [],
                                  solar: withCorrected);
            XElement corrected = XDocument.Load(path).Root!.Element("_solar")!;
            Assert.Equal("golden", (string?)corrected.Attribute("illumination_band"));
            Assert.Equal("refraction_corrected", (string?)corrected.Attribute("illumination_band_elevation"));
            // Written after every attribute the element already carried.
            Assert.Equal("illumination_band_elevation", corrected.Attributes().Last().Name.LocalName);

            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [],
                                  solar: geometricOnly);
            XElement geometric = XDocument.Load(path).Root!.Element("_solar")!;
            Assert.Equal("civil_twilight", (string?)geometric.Attribute("illumination_band"));
            Assert.Equal("geometric", (string?)geometric.Attribute("illumination_band_elevation"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_Sun_The_Engine_Could_Not_Compute_Is_Recorded_Without_A_Band()
    {
        // -180 degrees is the engine's sentinel for an impossible date (doc 11 F4), not a night.
        double[] sentinel = [12.0, 2026, 2, 31, 3.5, 27.15012, 56.18065, -180.0, 0.0, 0.0, 0.0, -180.0];
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), [],
                                  solar: sentinel);
            XElement solar = XDocument.Load(path).Root!.Element("_solar")!;
            Assert.Equal("-180", (string?)solar.Attribute("sun_elevation_deg"));
            Assert.Null(solar.Attribute("illumination_band"));
            Assert.Null(solar.Attribute("illumination_band_elevation"));
            Assert.DoesNotContain("illumination_band", SolarMetadata.ToJson(sentinel));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_Png_Sun_Chunk_Carries_The_Same_Band_As_The_Sidecar()
    {
        double[] port = [17.0, 2026, 12, 21, 3.5, 27.15012, 56.18065, -1.58296, 244.3416, 0.0, 0.0, -1.37418];
        (string keyword, string json) = Assert.Single(SolarMetadata.PngTextChunks(port));
        Assert.Equal("carla:solar", keyword);
        Assert.EndsWith(",\"sun_corrected_elevation_deg\":-1.37418,\"illumination_band\":\"civil_twilight\","
                        + "\"illumination_band_elevation\":\"refraction_corrected\"}", json);
        var band = SolarMetadata.Band(port);
        Assert.NotNull(band);
        Assert.Equal(IlluminationBand.CivilTwilight, band.Value.Band);
        Assert.Equal(SolarElevationKind.RefractionCorrected, band.Value.AssignedFrom);

        // No sun, no band, and no chunk to carry one.
        Assert.Null(SolarMetadata.Band([]));
        Assert.Empty(SolarMetadata.PngTextChunks([]));
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
        Assert.StartsWith("{\"format_version\":1,\"policy\":\"freeze_at_window_start\",\"epoch_honoured\":true,"
                          + "\"audited\":true", json);
        Assert.Contains("\"epoch_digest\":\"979f424f6f03", json);
        Assert.Contains("\"utc_offset_hours\":3.5", json);
        Assert.Contains("\"residual_clock_s\":0.001", json);
        Assert.DoesNotContain("\"rate\"", json);

        string ignored = new IlluminationDeclaration("ignore", false, false).ToJson();
        Assert.Equal("{\"format_version\":1,\"policy\":\"ignore\",\"epoch_honoured\":false,\"audited\":false}", ignored);
    }

    [Fact]
    public void The_Capture_Names_One_Frame_For_The_Pixels_And_The_Truth_Alike()
    {
        // The vehicle records beside a still are the truth of the still's own frame and no other, so
        // the container names one frame, tick, and no second frame the truth might have come from.
        string xml = WriteWith(new CaptureIdentity(260042, 310.21974, "run-1", null, 103), Saloon());
        Assert.Contains("tick=\"260042\"", xml);
        Assert.DoesNotContain("telemetry_tick", xml);
        Assert.DoesNotContain("telemetry_tick", new CaptureIdentity(260042, 310.21974, "run-1", null, 103).ToJson());
        Assert.Equal("{\"format_version\":1,\"tick\":7,\"sim_time_s\":0}", new CaptureIdentity(7, 0.0).ToJson());
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
    public void Without_A_Render_Set_The_Sidecar_Is_Written_Exactly_As_Before()
    {
        // What a traffic-manager run writes: no render-set source, so every vehicle actor is listed,
        // keyed by its actor id, with nothing on the container saying which vehicles they are. Taken
        // byte for byte from the writer as it stood before the render set was introduced; its sun has
        // since gained the band it falls in, after the attributes it always carried, and its container
        // the sidecar's format version, before them.
        var parked = new VehicleTelemetry(
            8, "vehicle.fuso.mitsubishi", "truck", "", "10,20,30", "autopilot",
            37.7801234, -122.4507890, -238.8, 58.0,
            0.0, 90.0, 0.0, 0.0, 0.0,
            7.0, 2.5, 3.2);
        double[] sun = [7.0, 2026, 3, 21, 3.5, 27.15012, 56.18065, 4.981, 119.56, 0.0, 0.0];
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc),
                                  [Saloon() with
                                  {
                                      Occlusion = 0.42, OcclusionLevel = 2,
                                      OcclusionSamples = 96, ApparentWidthPx = 48, ApparentHeightPx = 21,
                                  }, parked],
                                  solar: sun, capture: new CaptureIdentity(260042, 310.21974, "run-1", null, 103));
            Assert.Equal(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <events format_version="1" captured="2026-07-10T18:00:00.000Z" count="2" source="truth" tick="260042" sim_time_s="310.21974" run_id="run-1" seed="103">
                  <_solar solar_time="7" date="2026-03-21" time_zone="3.5" lat="27.1501200" lon="56.1806500" sun_elevation_deg="4.981" sun_azimuth_deg="119.56" advancing="false" rate="0" illumination_band="golden" illumination_band_elevation="geometric" />
                  <event version="2.0" uid="CARLA-TRUTH-7" type="a-n-G-E-V" how="m-g" time="2026-07-10T18:00:00.000Z" start="2026-07-10T18:00:00.000Z" stale="2026-07-10T18:00:03.000Z">
                    <point lat="37.7841234" lon="-122.4567890" hae="61.20" ce="0.0" le="0.0" />
                    <detail>
                      <track course="182.4" speed="11.30" />
                      <contact callsign="car-7" />
                      <_carla source="truth" actor_id="7" type_id="vehicle.audi.tt" base_type="car" special_type="" length_m="4.50" width_m="2.00" height_m="1.40" color="0,0,0" role_name="autopilot" vx="11.20" vy="-1.40" vz="0.00" occlusion="0.420" occlusion_level="2" occlusion_samples="96" apparent_width_px="48" apparent_height_px="21" />
                    </detail>
                  </event>
                  <event version="2.0" uid="CARLA-TRUTH-8" type="a-n-G-E-V" how="m-g" time="2026-07-10T18:00:00.000Z" start="2026-07-10T18:00:00.000Z" stale="2026-07-10T18:00:03.000Z">
                    <point lat="37.7801234" lon="-122.4507890" hae="-238.80" ce="0.0" le="0.0" />
                    <detail>
                      <track course="90.0" speed="0.00" />
                      <contact callsign="truck-8" />
                      <_carla source="truth" actor_id="8" type_id="vehicle.fuso.mitsubishi" base_type="truck" special_type="" length_m="7.00" width_m="2.50" height_m="3.20" color="10,20,30" role_name="autopilot" vx="0.00" vy="0.00" vz="0.00" />
                    </detail>
                  </event>
                </events>
                """.ReplaceLineEndings(),
                File.ReadAllText(path).ReplaceLineEndings());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_Vehicle_Named_By_A_Render_Set_Is_A_Track_Of_That_Vehicle_And_Keeps_Its_Body_Beside_It()
    {
        // A SUMO id that is a number must not read as an actor id, so the uid says whose it is.
        string xml = Write(Saloon() with { Rendered = new RenderedVehicle(7, "12", "passenger", 260001) });
        Assert.Contains("uid=\"CARLA-TRUTH-SUMO-12\"", xml);
        Assert.Contains("callsign=\"car-12\"", xml);
        Assert.Contains("actor_id=\"7\"", xml);
        Assert.Contains("sumo_id=\"12\" vtype_id=\"passenger\" admitted_tick=\"260001\"", xml);
        Assert.DoesNotContain("uid=\"CARLA-TRUTH-7\"", xml);
        // Written with no vehicles marker unless the writer is told which vehicles these are.
        Assert.DoesNotContain("vehicles=", xml);
    }

    [Fact]
    public void The_Truth_Carries_The_Bodys_Heading_Beside_Its_Course_And_Sumos_Angle_For_Audit()
    {
        // During a SUMO drive the body's heading comes from the path it took, its course from its
        // velocity, and SUMO's own reported angle rides beside them where the session supplied it.
        string xml = Write(Saloon() with
        {
            HeadingDeg = 176.25,
            Rendered = new RenderedVehicle(7, "12", "passenger", 260001) { SumoAngleDegrees = 179.94 },
        });
        Assert.Contains("course=\"182.4\"", xml);
        Assert.Contains("heading_deg=\"176.3\"", xml);
        Assert.Contains("sumo_angle_deg=\"179.9\"", xml);

        // A render set from the server knows no SUMO angle, and a record with no transform no heading.
        string fromTheServer = Write(Saloon() with { Rendered = new RenderedVehicle(7, "12", "passenger", 260001) });
        Assert.DoesNotContain("sumo_angle_deg", fromTheServer);
        Assert.DoesNotContain("heading_deg", fromTheServer);
    }

    // The supervision a Bahonar frame might carry: the escort's lead annotated, a guard on its posting
    // nominal with the ordinary behaviour named, and the tower it should have relieved unmanned.
    private static readonly SupervisionPlanIdentity BahonarPlan = new(
        "Shahid_Bahonar_Port_PatternOfLife", 3, "e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad");

    private static ObservedSupervision Bahonar() => new(
        BahonarPlan,
        [
            KeyValuePair.Create(21u, new SupervisionInForce(SupervisionState.Annotated,
            [
                new AnnotationInForce("Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3",
                                      ["bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"],
                                      "transit", "bahonar:lead"),
            ])),
            KeyValuePair.Create(22u, new SupervisionInForce(SupervisionState.Nominal,
            [
                new AnnotationInForce("Shahid_Bahonar_Port_PatternOfLife/pi_tower_posting_d4_h15_t3",
                                      ["bahonar:tower_posting"], "dwell", "bahonar:guard"),
            ])),
        ]);

    private static string WriteSupervised(CaptureSupervision supervision, params VehicleTelemetry[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc), records,
                                  capture: new CaptureIdentity(1044000, 370800.0, "cap-1", null, 42),
                                  vehicles: SidecarVehicles.Rendered, supervision: supervision);
            return File.ReadAllText(path).ReplaceLineEndings();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_Supervised_Capture_Carries_The_Plan_And_Every_Drawn_Vehicle_s_State_And_Nothing_For_The_World()
    {
        // The escort's lead, a guard on its posting, an ambient flow vehicle the author asserts nothing
        // of, and a vehicle actor no session named.
        VehicleTelemetry escort = Saloon() with { Id = 21, Rendered = new RenderedVehicle(21, "escort_0", "military_truck", 1043990) };
        VehicleTelemetry guard = Saloon() with { Id = 22, Rendered = new RenderedVehicle(22, "guard_d4_h15_t3", "guard", 1015259) };
        VehicleTelemetry ambient = Saloon() with { Id = 23, Rendered = new RenderedVehicle(23, "corridor_d0_p0_h6.12", "car", 1043000) };
        VehicleTelemetry unnamed = Saloon() with { Id = 24 };
        ObservedSupervision observed = Bahonar();

        string xml = WriteSupervised(CaptureSupervision.For(1044000, 1044000, observed, observed),
                                     escort, guard, ambient, unnamed);

        Assert.Contains(
            """
            <events format_version="1" captured="2026-07-10T18:00:00.000Z" count="4" source="truth" tick="1044000" sim_time_s="370800" run_id="cap-1" seed="42" vehicles="rendered" plan_id="Shahid_Bahonar_Port_PatternOfLife" vocabulary="3" vocabulary_digest="e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad">
            """.ReplaceLineEndings(), xml);
        Assert.Contains(
            """
                  <_carla source="truth" actor_id="21" type_id="vehicle.audi.tt" base_type="car" special_type="" length_m="4.50" width_m="2.00" height_m="1.40" color="0,0,0" role_name="autopilot" vx="11.20" vy="-1.40" vz="0.00" sumo_id="escort_0" vtype_id="military_truck" admitted_tick="1043990" />
                  <_supervision state="annotated" vocabulary="3" vocabulary_digest="e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad">
                    <annotation instance="Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3" labels="bahonar:coordinated_group_transit bahonar:destination_off_pattern" phase="transit" role="bahonar:lead" />
                  </_supervision>
            """.ReplaceLineEndings(), xml);
        // A nominal vehicle names which authored ordinary behaviour it is (06 D6.31).
        Assert.Contains(
            """
                  <_supervision state="nominal" vocabulary="3" vocabulary_digest="e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad">
                    <annotation instance="Shahid_Bahonar_Port_PatternOfLife/pi_tower_posting_d4_h15_t3" labels="bahonar:tower_posting" phase="dwell" role="bahonar:guard" />
                  </_supervision>
            """.ReplaceLineEndings(), xml);
        // The state is always written: unlabelled, bare, is a state and not an omission.
        Assert.Contains(
            """
                  <_carla source="truth" actor_id="23" type_id="vehicle.audi.tt" base_type="car" special_type="" length_m="4.50" width_m="2.00" height_m="1.40" color="0,0,0" role_name="autopilot" vx="11.20" vy="-1.40" vz="0.00" sumo_id="corridor_d0_p0_h6.12" vtype_id="car" admitted_tick="1043000" />
                  <_supervision state="unlabelled" vocabulary="3" vocabulary_digest="e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad" />
            """.ReplaceLineEndings(), xml);

        XElement root = XDocument.Parse(xml).Root!;
        // A vehicle no session named is no subject of the plan.
        XElement unnamedEvent = root.Elements("event").Single(e => (string?)e.Attribute("uid") == "CARLA-TRUTH-24");
        Assert.Null(unnamedEvent.Element("detail")!.Element("_supervision"));
        Assert.Equal(4, root.Elements("event").Count());
        // Nothing is written for the world apart from the plan on the container: every label follows a
        // vehicle, so every _supervision element is inside a vehicle's event (06 §3.5).
        Assert.Empty(root.Elements("_supervision"));
        Assert.Equal(3, root.Descendants("_supervision").Count());
        Assert.All(root.Descendants("_supervision"), element => Assert.Equal("detail", element.Parent!.Name.LocalName));
    }

    [Fact]
    public void A_Capture_Whose_Supervision_Is_Unknown_Says_So_And_Writes_None()
    {
        VehicleTelemetry escort = Saloon() with { Id = 21, Rendered = new RenderedVehicle(21, "escort_0", "military_truck", 1043990) };

        string xml = WriteSupervised(CaptureSupervision.Unknown, escort);

        Assert.Contains("vehicles=\"rendered\" supervision=\"unknown\">", xml);
        Assert.DoesNotContain("_supervision", xml);
        Assert.DoesNotContain("plan_id", xml);
        Assert.DoesNotContain("vocabulary", xml);
    }

    [Fact]
    public void A_Capture_Of_A_Frame_No_Plan_Was_In_Force_On_Carries_No_Supervision()
    {
        VehicleTelemetry escort = Saloon() with { Id = 21, Rendered = new RenderedVehicle(21, "escort_0", "military_truck", 1043990) };

        string xml = WriteSupervised(CaptureSupervision.For(1044000, 1044000, ObservedSupervision.None,
                                                            ObservedSupervision.None), escort);

        Assert.DoesNotContain("supervision", xml);
        Assert.DoesNotContain("plan_id", xml);
        Assert.DoesNotContain("vocabulary", xml);
    }

    [Fact]
    public void Supervision_Is_The_Capture_s_Own_Frame_s_Or_Unknown_Never_A_Neighbour_s()
    {
        ObservedSupervision observed = Bahonar();

        Assert.Equal(SidecarSupervision.InForce, CaptureSupervision.For(100, 100, observed, observed).State);
        // The snapshot read is not the image's frame's, and it carried a plan: unknown, not the neighbour's.
        Assert.Same(CaptureSupervision.Unknown, CaptureSupervision.For(100, 99, observed, observed));
        // Its block could not be read: a plan was in force, and what it asserted is unknown.
        Assert.Same(CaptureSupervision.Unknown,
                    CaptureSupervision.For(100, 100, ObservedSupervision.Unreadable, ObservedSupervision.Unreadable));
        // No snapshot held at all, while the newest carries a plan.
        Assert.Same(CaptureSupervision.Unknown, CaptureSupervision.For(100, null, ObservedSupervision.None, observed));
        // No plan anywhere near: nothing to know.
        Assert.Same(CaptureSupervision.NotInForce,
                    CaptureSupervision.For(100, 99, ObservedSupervision.None, ObservedSupervision.None));
        Assert.Same(CaptureSupervision.NotInForce,
                    CaptureSupervision.For(100, null, ObservedSupervision.None, ObservedSupervision.None));
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
