using System.Text;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Types.Provenance;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;

namespace CarlaNet.Tests.Recording;

/// <summary>
/// The writer's two field sets. A sidecar's vehicle events come out byte for byte as the writer wrote
/// them before the live feed's set existed (<c>Recording/Fixtures</c>), and the live feed's event for a
/// record is the sidecar's event for it less exactly the fields the feed withholds.
/// </summary>
/// <remarks>
/// The records cover every place a box falls and every reason a vehicle carries: wholly and partly in
/// the picture with their boxes, outside it, behind the lens, partly and wholly beyond a draw distance,
/// under supervision, with lights and pose sources and without them, and an actor no render set named.
/// The files are stored with LF line endings, and the writer ends its lines as the platform does, so
/// each is compared with its line endings made the platform's.
/// </remarks>
public sealed class CotWriterFieldSetTests
{
    /// <summary>The <c>_carla</c> attributes the live feed withholds.</summary>
    private static readonly HashSet<string> Withheld =
    [
        "pitch_deg", "roll_deg", "in_frame", "occlusion", "occlusion_level", "occlusion_samples",
        "occlusion_unmeasured", "apparent_width_px", "apparent_height_px", "box_px", "box_oriented_px",
        "truncation", "lights", "pose_source", "beyond_draw_distance", "camera_range_m", "vtype_id",
        "sumo_angle_deg",
    ];

    private static readonly DateTime Captured = new(2026, 10, 2, 6, 49, 29, 950, DateTimeKind.Utc);

    private static readonly SupervisionPlanIdentity Plan = new(
        "Shahid_Bahonar_Port_PatternOfLife", 3, "e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad");

    public static TheoryData<string> Sidecars() => ["capture.xml", "unknown.xml", "live_pull.xml"];

    [Theory]
    [MemberData(nameof(Sidecars))]
    public void ASidecarOfTheFixedRecordsIsWrittenByteForByteAsRecorded(string name)
    {
        byte[] expected = Encoding.UTF8.GetBytes(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recording", "Fixtures", name))
                .ReplaceLineEndings());

        Assert.Equal(expected, WriteSidecar(name));
    }

    [Fact]
    public void ALiveEventIsTheSidecarSEventForTheSameRecordLessTheFieldsTheFeedWithholds()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        List<XElement> written;
        try
        {
            CotWriter.WriteToFile(path, Captured, CaptureRecords(), sensor: Sensor(), vehicles: SidecarVehicles.Rendered,
                                  drawDistanceMetres: 500.0, supervision: InForce());
            written = [.. XDocument.Load(path).Root!.Elements("event")
                          .Where(e => ((string?)e.Attribute("uid"))!.StartsWith(CotWriter.DefaultUidPrefix + "-",
                                                                                 StringComparison.Ordinal))];
        }
        finally
        {
            File.Delete(path);
        }

        VehicleTelemetry[] records = CaptureRecords();
        Assert.Equal(records.Length, written.Count);
        for (int index = 0; index < records.Length; index++)
        {
            XElement sidecar = written[index];
            XElement live = XElement.Parse(CotWriter.StreamEvent(records[index], "n", CotWriter.DefaultUidPrefix,
                                                                 Captured, 3.0));

            Assert.Equal(Attributes(sidecar), Attributes(live));
            Assert.Equal(Attributes(sidecar.Element("point")!), Attributes(live.Element("point")!));
            XElement sidecarDetail = sidecar.Element("detail")!;
            XElement liveDetail = live.Element("detail")!;
            Assert.Equal(["track", "contact", "_carla"], liveDetail.Elements().Select(e => e.Name.LocalName));
            Assert.Equal(Attributes(sidecarDetail.Element("track")!), Attributes(liveDetail.Element("track")!));
            Assert.Equal(Attributes(sidecarDetail.Element("contact")!), Attributes(liveDetail.Element("contact")!));
            Assert.Equal(Attributes(sidecarDetail.Element("_carla")!).Where(a => !Withheld.Contains(a.Name)),
                         Attributes(liveDetail.Element("_carla")!));
        }

        // The fixed records carry every withheld field somewhere, so the comparison above removed each.
        HashSet<string> carried = [.. written.SelectMany(e => e.Element("detail")!.Element("_carla")!.Attributes())
                                         .Select(a => a.Name.LocalName)];
        Assert.Subset(carried, Withheld);
        Assert.Contains(written, e => e.Element("detail")!.Element("_box3d") is not null);
        Assert.Contains(written, e => e.Element("detail")!.Element("_supervision") is not null);
    }

    [Fact]
    public void ALiveEventCarriesExactlyTheStreamSFieldsOnOneLineWithNoDeclaration()
    {
        VehicleTelemetry inPicture = CaptureRecords()[0];
        string xml = CotWriter.StreamEvent(inPicture, "f", CotWriter.DefaultUidPrefix, Captured, 3.0);

        Assert.StartsWith("<event ", xml, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', xml);
        XElement live = XElement.Parse(xml);
        Assert.Equal(["version", "uid", "type", "how", "time", "start", "stale"], Names(live));
        Assert.Equal(["point", "detail"], live.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["lat", "lon", "hae", "ce", "le"], Names(live.Element("point")!));
        XElement detail = live.Element("detail")!;
        Assert.Equal(["course", "speed"], Names(detail.Element("track")!));
        Assert.Equal(["callsign"], Names(detail.Element("contact")!));
        Assert.Equal(["source", "actor_id", "type_id", "base_type", "special_type", "length_m", "width_m",
                      "height_m", "color", "role_name", "vx", "vy", "vz", "heading_deg", "sumo_id", "admitted_tick"],
                     Names(detail.Element("_carla")!));

        Assert.Equal("a-f-G-E-V", (string?)live.Attribute("type"));
        Assert.Equal("2026-10-02T06:49:29.950Z", (string?)live.Attribute("time"));
        Assert.Equal("2026-10-02T06:49:29.950Z", (string?)live.Attribute("start"));
        Assert.Equal("2026-10-02T06:49:32.950Z", (string?)live.Attribute("stale"));
        Assert.Equal("2026-10-02T06:49:37.450Z",
                     (string?)XElement.Parse(CotWriter.StreamEvent(inPicture, "n", "CARLA-TRUTH", Captured, 7.5))
                         .Attribute("stale"));
    }

    [Fact]
    public void ALiveEventSUidTakesThePrefixItIsGiven()
    {
        VehicleTelemetry lent = LivePullRecords()[0];
        VehicleTelemetry unnamed = LivePullRecords()[2];

        Assert.Equal("CARLA-TRUTH-SUMO-escort_1", Uid(CotWriter.StreamEvent(lent, "n", "CARLA-TRUTH", Captured, 3.0)));
        Assert.Equal("RUN7-SUMO-escort_1", Uid(CotWriter.StreamEvent(lent, "n", "RUN7", Captured, 3.0)));
        Assert.Equal("RUN7-9", Uid(CotWriter.StreamEvent(unnamed, "n", "RUN7", Captured, 3.0)));
        // The callsign is the vehicle's, whatever the prefix.
        Assert.Equal("car-escort_1",
                     (string?)XElement.Parse(CotWriter.StreamEvent(lent, "n", "RUN7", Captured, 3.0))
                         .Element("detail")!.Element("contact")!.Attribute("callsign"));
    }

    private static string? Uid(string xml) => (string?)XElement.Parse(xml).Attribute("uid");

    private static List<string> Names(XElement element) =>
        [.. element.Attributes().Select(attribute => attribute.Name.LocalName)];

    private static List<(string Name, string Value)> Attributes(XElement element) =>
        [.. element.Attributes().Select(attribute => (attribute.Name.LocalName, attribute.Value))];

    /// <summary>The bytes the writer writes for one of the fixed sidecars.</summary>
    internal static byte[] WriteSidecar(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            switch (name)
            {
                case "capture.xml":
                    CotWriter.WriteToFile(path, Captured, CaptureRecords(), solar: Sun, sensor: Sensor(),
                                          capture: new CaptureIdentity(5423399, 271169.95, "run-1", "Bahonar", 42),
                                          illumination: Declared(), vehicles: SidecarVehicles.Rendered,
                                          drawDistanceMetres: 500.0, supervision: InForce(), producer: Producer());
                    break;
                case "unknown.xml":
                    CotWriter.WriteToFile(path, Captured, UnknownRecords(), solar: Sun, sensor: Sensor(),
                                          capture: new CaptureIdentity(5423419, 271170.95),
                                          vehicles: SidecarVehicles.Rendered, supervision: CaptureSupervision.Unknown,
                                          producer: new ProducerRecord("run_sumo_drive", null, "0.10.0",
                                                                       ServerBuildIdentity.NotAnswered(
                                                                           "built before the call", "0.10.0", "1.0"),
                                                                       null, Captured));
                    break;
                default:
                    CotWriter.WriteToFile(path, Captured, LivePullRecords(), affiliation: "f", staleSeconds: 5.0);
                    break;
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A body a SUMO drive lent, with its truth as the world reported it.</summary>
    internal static VehicleTelemetry Lent(uint actor, string sumoId, string typeId) => new(
        actor, "vehicle.jeep.wrangler_rubicon", "car", "", "17,213,91", "sumo",
        27.1501234, 56.1806789, 12.34, 11.9,
        8.25, 271.3, -8.24, -0.19, 0.0,
        3.86, 1.93, 1.88)
    {
        HeadingDeg = 270.9,
        Rendered = new RenderedVehicle(actor, sumoId, typeId, 5423380) { SumoAngleDegrees = 271.4 },
    };

    /// <summary>A box in the picture, wholly or with a share outside it.</summary>
    internal static CaptureBox Box(double truncation) => new(
        165.41, 842.97, 288.37, 899.83,
        [new PixelPoint(165.74, 842.91), new PixelPoint(288.41, 843.72), new PixelPoint(288.04, 899.87),
         new PixelPoint(165.37, 899.06)],
        truncation, 0.24, -0.39,
        [.. Enumerable.Range(0, 8).Select(corner => new GeodeticCorner(27.150118 + (corner * 1e-6), 56.180671,
                                                                       12.36 + (corner >= 4 ? 1.88 : 0.0)))]);

    /// <summary>
    /// A capture's records: wholly in the picture and measured; partly in it and partly beyond the draw
    /// distance; outside it; behind the lens; wholly beyond the draw distance; and an actor no render set
    /// named, with no heading and a velocity of negative zero.
    /// </summary>
    internal static VehicleTelemetry[] CaptureRecords() =>
    [
        Lent(21, "escort_0", "mil_truck.vehicle.carlamotors.european_hgv") with
        {
            InFrame = InFrame.Wholly, Box = Box(0.0), ApparentWidthPx = 123, ApparentHeightPx = 57,
            Occlusion = 0.125, OcclusionLevel = 1, OcclusionSamples = 96, CameraRangeMetres = 212.44,
            Lights = VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam,
            PoseSource = PoseSource.Sumo,
        },
        Lent(22, "guard_d4_h15_t3", "guard.vehicle.jeep.wrangler_rubicon") with
        {
            InFrame = InFrame.Partly, Box = Box(0.4), ApparentWidthPx = 88, ApparentHeightPx = 40,
            OcclusionUnmeasured = OcclusionUnmeasured.NoSample, DrawDistance = DrawDistanceReach.Partly,
            CameraRangeMetres = 498.2, Lights = VehicleLightStateFlags.None, PoseSource = PoseSource.Interpolated,
        },
        Lent(23, "corridor_d0_p0_h6.12", "car.vehicle.audi.tt") with
        {
            InFrame = InFrame.None, ApparentWidthPx = 6, ApparentHeightPx = 3,
            OcclusionUnmeasured = OcclusionUnmeasured.OutsideFrame,
        },
        Lent(24, "probe_d2", "car.vehicle.audi.tt") with
        {
            InFrame = InFrame.BehindCamera, OcclusionUnmeasured = OcclusionUnmeasured.BehindCamera,
        },
        Lent(25, "staybehind", "car.vehicle.audi.tt") with
        {
            InFrame = InFrame.None, ApparentWidthPx = 2, ApparentHeightPx = 1,
            OcclusionUnmeasured = OcclusionUnmeasured.BeyondDrawDistance, DrawDistance = DrawDistanceReach.Beyond,
            CameraRangeMetres = 812.4,
        },
        new VehicleTelemetry(
            8, "vehicle.fuso.mitsubishi", "truck", "", "10,20,30", "autopilot",
            27.1490001, 56.1790002, -238.8, 58.0,
            0.0, 90.0, -0.0, -0.0, -0.0,
            7.0, 2.5, 3.2)
        {
            InFrame = InFrame.None, ApparentWidthPx = 1, ApparentHeightPx = 1,
            OcclusionUnmeasured = OcclusionUnmeasured.OutsideFrame,
        },
    ];

    /// <summary>
    /// A capture whose snapshot carried no lights and no pose source: one vehicle in the picture without
    /// them, one outside it.
    /// </summary>
    internal static VehicleTelemetry[] UnknownRecords() =>
    [
        Lent(31, "shadow", "car.vehicle.audi.tt") with
        {
            InFrame = InFrame.Wholly, Box = Box(0.0), ApparentWidthPx = 123, ApparentHeightPx = 57,
            CameraRangeMetres = 87.9, OcclusionUnmeasured = OcclusionUnmeasured.NoDepthCamera,
        },
        Lent(32, "escort_4", "mil_jeep.vehicle.jeep.wrangler_rubicon") with
        {
            InFrame = InFrame.None, ApparentWidthPx = 9, ApparentHeightPx = 4,
            OcclusionUnmeasured = OcclusionUnmeasured.NoDepthCamera,
        },
    ];

    /// <summary>The live pull's records: no camera projected them.</summary>
    internal static VehicleTelemetry[] LivePullRecords() =>
    [
        Lent(41, "escort_1", "mil_truck.vehicle.carlamotors.european_hgv"),
        Lent(42, "42", "car.vehicle.audi.tt") with
        {
            HeadingDeg = double.NaN, Rendered = new RenderedVehicle(42, "42", "car.vehicle.audi.tt", 7),
        },
        new VehicleTelemetry(
            9, "vehicle.audi.tt", "car", "", "0,0,0", "autopilot",
            27.1481234, 56.1786789, 7.5, 7.0,
            11.3, 182.4, 11.2, -1.4, 0.0,
            4.5, 2.0, 1.4),
    ];

    private static readonly double[] Sun = [7.45, 2026, 10, 2, 3.5, 27.15012, 56.18065, 5.549, 97.827, 1.0, 1.0, 5.694];

    private static SensorPose Sensor() => new(
        "a-f-A-M-F-Q", "Gate_Orbit_1200ft", "CARLA-SENSOR-107", 27.1521345, 56.1781363, 380.06, -0.63,
        90, -70.346, 0, 90, 12.5, 1280, 720, 640, 640, 640, 360, 90, 58.716,
        "sensor.camera.rgb", "pinhole", "none", new CameraExposure("Default", CameraExposure.Manual, 100, 320, 4, 0));

    private static IlluminationDeclaration Declared() =>
        new("advance", EpochHonoured: true, Audited: true)
        {
            Rate = 1.0,
            EpochDigest = new string('b', 64),
            EpochCivil = "2026-10-02T00:00:00+03:30",
            UtcOffsetHours = 3.5,
            DeclaredCivil = "2026-10-02T10:19:29.95+03:30",
            DeclaredUtc = "2026-10-02T06:49:29.95Z",
            SimulatedSeconds = 271169.95,
            SunDeclared = "2026-10-02T10:19:29.95+03:30",
            SunElevationDeclaredDegrees = 5.5491,
            SunCorrectedElevationDeclaredDegrees = 5.694,
            DeclaredElevationKind = "refraction_corrected",
            ResidualClockSeconds = 0.001,
            ResidualDegrees = 0.0000123,
            ResidualCorrectedDegrees = -0.0000045,
        };

    private static CaptureSupervision InForce()
    {
        var observed = new ObservedSupervision(Plan,
        [
            KeyValuePair.Create(21u, new SupervisionInForce(SupervisionState.Annotated,
            [
                new AnnotationInForce("Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3",
                                      ["bahonar:coordinated_group_transit"], "transit", "bahonar:lead"),
            ])),
            KeyValuePair.Create(22u, new SupervisionInForce(SupervisionState.Nominal,
            [
                new AnnotationInForce("Shahid_Bahonar_Port_PatternOfLife/pi_tower_posting_d4_h15_t3",
                                      ["bahonar:tower_posting"], "dwell", "bahonar:guard"),
            ])),
        ]);
        return CaptureSupervision.For(5423399, 5423399, observed, observed);
    }

    private static ProducerRecord Producer() => new(
        "run_sumo_drive", "0.10.0+g1a2b3c4d5", "0.10.0+g1a2b3c4d5",
        ServerBuildIdentity.FromAnswer(new Dictionary<string, string>
        {
            ["release"] = "0.10.0", ["world_interface"] = "1.0", ["build"] = "package",
            ["configuration"] = "Shipping", ["carla_commit"] = "025443a83", ["content_commit"] = "1a2b3c",
            ["engine_commit"] = "4d5e6f", ["commits_from"] = "version_file",
        }),
        "1.27.0", new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc));
}
