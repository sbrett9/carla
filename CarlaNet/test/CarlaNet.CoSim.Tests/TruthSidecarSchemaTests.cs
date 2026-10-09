using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using CarlaNet.CoSim.Schemas;
using CarlaNet.Recording;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Provenance;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Every truth sidecar <see cref="CotWriter"/> writes is valid against the published XSD, in every shape it
/// writes -- the live pull's records, a recorder's with every vehicle place and reason, the supervision in
/// force or unknown, a sidecar from before the format version and the producer record -- and the schema
/// refuses what the writer never writes.
/// </summary>
public sealed class TruthSidecarSchemaTests
{
    private static readonly DateTime Captured = new(2026, 10, 7, 17, 34, 49, 411, DateTimeKind.Utc);

    private static readonly XmlSchemaSet Schema = Compile(TruthSidecarSchema.Text());

    [Fact]
    public void TheSchemaIsAnXsd10SchemaWithNoTargetNamespace()
    {
        XmlSchema compiled = Schema.Schemas().Cast<XmlSchema>().Single();
        Assert.Null(compiled.TargetNamespace);
        Assert.Equal(CotWriter.FormatVersion.ToString(), compiled.Version);
        Assert.Contains(TruthSidecarSchema.Urn, TruthSidecarSchema.Text());
    }

    [Fact]
    public void ALivePullRecordWithNoCaptureNoSensorAndNoProducerIsValid()
    {
        AssertValid(Write([Saloon()]));
    }

    [Fact]
    public void ARecorderSSidecarWithEveryPlaceEveryReasonAndTheSupervisionInForceIsValid()
    {
        var server = ServerBuildIdentity.FromAnswer(new Dictionary<string, string>
        {
            ["release"] = "0.10.0", ["world_interface"] = "1.0", ["build"] = "package",
            ["configuration"] = "Shipping", ["carla_commit"] = "025443a83", ["content_commit"] = "1a2b3c",
            ["engine_commit"] = "4d5e6f", ["commits_from"] = "version_file",
        });
        var producer = new ProducerRecord("carlacontrol.CaptureSession", "0.10.0", "0.10.0+g1a2b3c4d5", server,
                                          "1.27.0", Captured);
        double[] sun = [7.45, 2026, 9, 29, -6, 39.59431, -104.88449, 5.549, 97.827, 0.0, 0.0, 5.694];
        var illumination = new IlluminationDeclaration("freeze_at_window_start", true, true)
        {
            EpochDigest = new string('b', 64),
            EpochCivil = "2026-09-29T07:26:00-06:00",
            UtcOffsetHours = -6,
            DeclaredCivil = "2026-09-29T07:27:00-06:00",
            DeclaredUtc = "2026-09-29T13:27:00Z",
            SunDeclared = "2026-09-29T07:27:00-06:00",
            SunElevationDeclaredDegrees = 5.5491,
            SunCorrectedElevationDeclaredDegrees = 5.694,
            DeclaredElevationKind = "refraction_corrected",
            ResidualClockSeconds = 0.001,
            ResidualDegrees = 0,
            ResidualCorrectedDegrees = 0,
        };
        var exposure = new CameraExposure("Default", CameraExposure.Manual, 100, 320, 4, 0);
        SensorPose sensor = Sensor(exposure);
        RenderedVehicle Lent(uint actor, string id) => new(actor, id, "car.vehicle.audi.tt", 22475) { SumoAngleDegrees = 180.4 };
        var plan = new SupervisionPlanIdentity("Arapahoe_I25_SupervisionCheck", 3, new string('1', 64));
        var observed = new ObservedSupervision(plan,
        [
            KeyValuePair.Create(115u, new SupervisionInForce(SupervisionState.Annotated,
            [
                new AnnotationInForce("Arapahoe_I25_SupervisionCheck/through_transit", ["check:through_transit"],
                                      "transit", "subject"),
                new AnnotationInForce("Arapahoe_I25_SupervisionCheck/unphased", [], "", ""),
            ])),
            KeyValuePair.Create(116u, new SupervisionInForce(SupervisionState.Nominal, [])),
        ]);

        VehicleTelemetry[] records =
        [
            InPicture(Saloon() with { Id = 115, Rendered = Lent(115, "yosemite_north_to_south.0") }, InFrame.Wholly) with
            {
                Occlusion = 0.0, OcclusionLevel = 0, OcclusionSamples = 174,
                Lights = VehicleLightStateFlags.LowBeam | VehicleLightStateFlags.Brake | (VehicleLightStateFlags)(1u << 20),
                PoseSource = PoseSource.Sumo,
            },
            InPicture(Saloon() with { Id = 116, Rendered = Lent(116, "yosemite_north_to_south.2") }, InFrame.Partly) with
            {
                OcclusionUnmeasured = OcclusionUnmeasured.NoSample,
                Lights = VehicleLightStateFlags.None,
                PoseSource = PoseSource.Interpolated,
                DrawDistance = DrawDistanceReach.Partly,
            },
            Saloon() with
            {
                Id = 117, Rendered = Lent(117, "arapahoe_east_to_west.0"), InFrame = InFrame.None,
                ApparentWidthPx = 37, ApparentHeightPx = 59, OcclusionUnmeasured = OcclusionUnmeasured.OutsideFrame,
            },
            Saloon() with
            {
                Id = 118, Rendered = Lent(118, "arapahoe_east_to_west.1"), InFrame = InFrame.BehindCamera,
                OcclusionUnmeasured = OcclusionUnmeasured.BehindCamera,
            },
            Saloon() with
            {
                Id = 119, Rendered = Lent(119, "far.0"), InFrame = InFrame.None, ApparentWidthPx = 2, ApparentHeightPx = 1,
                OcclusionUnmeasured = OcclusionUnmeasured.BeyondDrawDistance, DrawDistance = DrawDistanceReach.Beyond,
                CameraRangeMetres = 812.4,
            },
        ];

        string xml = Write(records, sun, sensor, new CaptureIdentity(23674, 194.179475, "cap-1", "Arapahoe_I25", 42),
                           illumination, SidecarVehicles.Rendered, 500.0,
                           CaptureSupervision.For(23674, 23674, observed, observed), producer);
        Assert.Contains("<_box3d frame=\"geodetic\">", xml);
        Assert.Contains("<annotation ", xml);
        Assert.Contains("lights=\"low_beam brake bit20\"", xml);
        AssertValid(xml);
    }

    [Fact]
    public void ASidecarWhoseSupervisionLightsAndPoseSourcesAreUnknownUnderAHistogramExposureIsValid()
    {
        var producer = new ProducerRecord("run_sumo_drive", null, "0.10.0+g1a2b3c4d5",
                                          ServerBuildIdentity.NotAnswered("built before the call", "0.10.0", "1.0"),
                                          null, Captured);
        double[] sun = [7.45, 2026, 9, 29, -6, 39.59431, -104.88449, 5.549, 97.827, 1.0, 1.0];
        SensorPose sensor = Sensor(new CameraExposure("Default", CameraExposure.Histogram, 100, 320, 4, 0.5));
        VehicleTelemetry lent = InPicture(Saloon() with { Rendered = new RenderedVehicle(7, "a.0", "car", 10) }, InFrame.Wholly);
        string xml = Write([lent, Saloon() with { Id = 8 }], sun, sensor, new CaptureIdentity(11, 0.55), null,
                           SidecarVehicles.Rendered, null, CaptureSupervision.Unknown, producer);
        Assert.Contains("supervision=\"unknown\" lights=\"unknown\" pose_source=\"unknown\"", xml);
        Assert.DoesNotContain("ev100", xml);
        AssertValid(xml);

        AssertValid(Write([], sun, sensor, new CaptureIdentity(12, 0.6), null, SidecarVehicles.Unknown, null,
                          CaptureSupervision.NotInForce, producer));
    }

    [Fact]
    public void ASidecarWrittenBeforeTheFormatVersionAndTheProducerRecordIsVersionOneAndValid()
    {
        AssertValid(LegacySidecar);
    }

    [Theory]
    [InlineData("in_frame=\"none\"", "in_frame=\"maybe\"")]
    [InlineData("<events ", "<events format_version=\"2\" ")]
    [InlineData("occlusion_unmeasured=\"outside_frame\"", "occlusion_unmeasured=\"hidden\"")]
    [InlineData("lights=\"none\"", "lights=\"none brake\"")]
    [InlineData("pose_source=\"sumo\"", "pose_source=\"guessed\"")]
    [InlineData("state=\"unlabelled\"", "state=\"negative\"")]
    [InlineData("role_name=\"sumo\"", "role_name=\"sumo\" corpus=\"1\"")]
    [InlineData("hae=\"1737.34\" />\n      </_box3d>", "hae=\"1737.34\" />\n        <corner lat=\"1\" lon=\"1\" hae=\"1\" />\n      </_box3d>")]
    [InlineData(" vy=\"0.00\"", "")]
    [InlineData("captured=\"2026-10-07T17:34:49.411Z\"", "captured=\"2026-10-07 17:34:49\"")]
    public void TheSchemaRefusesWhatTheWriterNeverWrites(string written, string altered)
    {
        Assert.Contains(written, LegacySidecar);
        Assert.NotEmpty(Errors(LegacySidecar.Replace(written, altered, StringComparison.Ordinal)));
    }

    private static readonly string LegacySidecar = """
        <?xml version="1.0" encoding="utf-8"?>
        <events captured="2026-10-07T17:34:49.411Z" count="2" source="truth" tick="23674" sim_time_s="194.179475" run_id="cap-20261007-173433-41f49b" vehicles="rendered" plan_id="Arapahoe_I25_SupervisionCheck" vocabulary="3" vocabulary_digest="19beb3e43bec2220772e14f501b0657e24c0902246ac6fd0e9ce28a579e0f197">
          <_solar solar_time="7.45" date="2026-09-29" time_zone="-6" lat="39.5943100" lon="-104.8844900" sun_elevation_deg="5.549" sun_corrected_elevation_deg="5.694" sun_azimuth_deg="97.827" advancing="false" rate="0" illumination_band="golden" illumination_band_elevation="refraction_corrected" />
          <event version="2.0" uid="CARLA-SENSOR-107" type="a-f-A-M-F-Q" how="m-g" time="2026-10-07T17:34:49.411Z" start="2026-10-07T17:34:49.411Z" stale="2026-10-07T17:34:52.411Z">
            <point lat="39.5971345" lon="-104.8891363" hae="1818.06" ce="0.0" le="0.0" />
            <detail>
              <contact callsign="Check_Overhead_1" />
              <track course="90.0" speed="0.00" />
              <sensor azimuth="90" elevation="-70.346" roll="0" fov="50" vfov="29.395" range="0" type="EO" model="sensor.camera.rgb" />
              <_carla_intrinsics width="1920" height="1080" fx="2058.73" fy="2058.73" cx="960" cy="540" hfov_deg="50" vfov_deg="29.395" model="pinhole" distortion="none" align_offset_m="-0.63" />
              <_carla_exposure post_process_profile="Default" method="manual" iso="100" shutter_s="0.003125" fstop="4" compensation_ev="0" ev100="12.322" />
            </detail>
          </event>
          <event version="2.0" uid="CARLA-TRUTH-SUMO-arapahoe_east_to_west.0" type="a-n-G-E-V" how="m-g" time="2026-10-07T17:34:49.411Z" start="2026-10-07T17:34:49.411Z" stale="2026-10-07T17:34:52.411Z">
            <point lat="39.5951938" lon="-104.8806125" hae="1748.58" ce="0.0" le="0.0" />
            <detail>
              <track course="262.7" speed="0.00" />
              <contact callsign="van-arapahoe_east_to_west.0" />
              <_carla source="truth" actor_id="109" type_id="vehicle.sprinter.mercedes" base_type="van" special_type="" length_m="5.92" width_m="1.99" height_m="2.73" color="233,234,236" role_name="sumo" vx="0.00" vy="0.00" vz="0.00" heading_deg="262.7" in_frame="none" apparent_width_px="37" apparent_height_px="59" occlusion_unmeasured="outside_frame" sumo_id="arapahoe_east_to_west.0" vtype_id="van.vehicle.sprinter.mercedes" admitted_tick="22475" sumo_angle_deg="320.3" />
              <_supervision state="unlabelled" vocabulary="3" vocabulary_digest="19beb3e43bec2220772e14f501b0657e24c0902246ac6fd0e9ce28a579e0f197" />
            </detail>
          </event>
          <event version="2.0" uid="CARLA-TRUTH-SUMO-yosemite_north_to_south.0" type="a-n-G-E-V" how="m-g" time="2026-10-07T17:34:49.411Z" start="2026-10-07T17:34:49.411Z" stale="2026-10-07T17:34:52.411Z">
            <point lat="39.5973969" lon="-104.8889602" hae="1735.95" ce="0.0" le="0.0" />
            <detail>
              <track course="180.4" speed="16.43" />
              <contact callsign="car-yosemite_north_to_south.0" />
              <_carla source="truth" actor_id="115" type_id="vehicle.ue4.audi.tt" base_type="car" special_type="" length_m="4.18" width_m="1.99" height_m="1.39" color="0,0,0" role_name="sumo" vx="-0.11" vy="16.43" vz="0.07" heading_deg="180.4" pitch_deg="0.24" roll_deg="0.39" in_frame="wholly" occlusion="0.000" occlusion_level="0" occlusion_samples="174" apparent_width_px="123" apparent_height_px="57" box_px="165.41 842.97 288.37 899.83" box_oriented_px="165.74 842.91 288.41 843.72 288.04 899.87 165.37 899.06" truncation="0.000" lights="none" pose_source="sumo" camera_range_m="87.9" sumo_id="yosemite_north_to_south.0" vtype_id="car.vehicle.ue4.audi.tt" admitted_tick="22475" sumo_angle_deg="180.4" />
              <_box3d frame="geodetic">
                <corner lat="39.5973780" lon="-104.8889488" hae="1735.97" />
                <corner lat="39.5973782" lon="-104.8889720" hae="1735.96" />
                <corner lat="39.5974158" lon="-104.8889717" hae="1735.94" />
                <corner lat="39.5974157" lon="-104.8889485" hae="1735.95" />
                <corner lat="39.5973781" lon="-104.8889489" hae="1737.35" />
                <corner lat="39.5973782" lon="-104.8889721" hae="1737.34" />
                <corner lat="39.5974159" lon="-104.8889718" hae="1737.32" />
                <corner lat="39.5974157" lon="-104.8889486" hae="1737.34" />
              </_box3d>
              <_supervision state="unlabelled" vocabulary="3" vocabulary_digest="19beb3e43bec2220772e14f501b0657e24c0902246ac6fd0e9ce28a579e0f197" />
            </detail>
          </event>
        </events>
        """.ReplaceLineEndings("\n");

    private static VehicleTelemetry Saloon() => new(
        7, "vehicle.audi.tt", "car", "", "0,0,0", "sumo",
        39.5973969, -104.8889602, 1735.95, 1735.9,
        16.43, 180.4, -0.11, 16.43, 0.07,
        4.18, 1.99, 1.39) { HeadingDeg = 180.4 };

    private static VehicleTelemetry InPicture(VehicleTelemetry record, InFrame where) => record with
    {
        InFrame = where,
        ApparentWidthPx = 123,
        ApparentHeightPx = 57,
        CameraRangeMetres = 87.9,
        Box = new CaptureBox(
            165.41, 842.97, where == InFrame.Wholly ? 288.37 : 1950.0, 899.83,
            [new PixelPoint(165.74, 842.91), new PixelPoint(288.41, 843.72), new PixelPoint(288.04, 899.87),
             new PixelPoint(165.37, 899.06)],
            where == InFrame.Wholly ? 0.0 : 0.991, 0.24, -0.39,
            [.. Enumerable.Range(0, 8).Select(corner => new GeodeticCorner(39.597378 + corner * 1e-6, -104.888949, 1735.97 + (corner >= 4 ? 1.38 : 0)))]),
    };

    private static SensorPose Sensor(CameraExposure exposure) => new(
        "a-f-A-M-F-Q", "Check_Overhead_1", "CARLA-SENSOR-107", 39.5971345, -104.8891363, 1818.06, -0.63,
        90, -70.346, 0, 90, 0, 1920, 1080, 2058.73, 2058.73, 960, 540, 50, 29.395,
        "sensor.camera.rgb", "pinhole", "none", exposure);

    private static string Write(IReadOnlyList<VehicleTelemetry> records, IReadOnlyList<double>? solar = null,
                                SensorPose? sensor = null, CaptureIdentity? capture = null,
                                IlluminationDeclaration? illumination = null,
                                SidecarVehicles vehicles = SidecarVehicles.World, double? drawDistance = null,
                                CaptureSupervision? supervision = null, ProducerRecord? producer = null)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, Captured, records, solar: solar, sensor: sensor, capture: capture,
                                  illumination: illumination, vehicles: vehicles, drawDistanceMetres: drawDistance,
                                  supervision: supervision, producer: producer);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static XmlSchemaSet Compile(string xsd)
    {
        var set = new XmlSchemaSet();
        using var reader = XmlReader.Create(new StringReader(xsd));
        set.Add(null, reader);
        set.Compile();
        return set;
    }

    /// <summary>Every validation error <paramref name="xml"/> raises against the published schema.</summary>
    internal static List<string> Errors(string xml)
    {
        List<string> errors = [];
        XDocument document = XDocument.Parse(xml);
        document.Validate(Schema, (_, failed) => errors.Add(failed.Message));
        return errors;
    }

    private static void AssertValid(string xml)
    {
        List<string> errors = Errors(xml);
        Assert.True(errors.Count == 0, string.Join("\n", errors) + "\n" + xml);
    }
}
