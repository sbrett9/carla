using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using CarlaNet.Recording;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Streaming;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Every live event <see cref="CotWriter.StreamEvent"/> writes is valid against the published stream schema,
/// <c>CarlaControl/schemas/cot_telemetry.xsd</c>, which takes its shared parts from the truth sidecar's schema
/// beside it; and that schema refuses a sidecar's event, which carries what the feed withholds.
/// </summary>
public sealed class CotStreamSchemaTests
{
    private static readonly DateTime Stamped = new(2026, 10, 2, 6, 49, 29, 950, DateTimeKind.Utc);

    private static readonly XmlSchemaSet Schema = Compile();

    [Theory]
    [InlineData("n", "CARLA-TRUTH")]
    [InlineData("f", "CARLA-TRUTH")]
    [InlineData("h", "RUN7")]
    public void ALiveEventOfEveryKindOfRecordIsValid(string affiliation, string uidPrefix)
    {
        foreach (VehicleTelemetry record in Records())
        {
            string xml = CotWriter.StreamEvent(record, affiliation, uidPrefix, Stamped, 3.0);
            List<string> errors = Errors(xml);
            Assert.True(errors.Count == 0, string.Join("\n", errors) + "\n" + xml);
        }
    }

    [Fact]
    public void TheStreamSchemaRefusesASidecarSEventForTheSameRecord()
    {
        VehicleTelemetry inPicture = Records()[0];
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, Stamped, [inPicture], vehicles: SidecarVehicles.Rendered);
            XElement sidecarEvent = XDocument.Load(path).Root!.Element("event")!;

            List<string> errors = Errors(sidecarEvent.ToString(SaveOptions.DisableFormatting));
            Assert.Contains(errors, error => error.Contains("'in_frame'", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("'_box3d'", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A body in the picture with its box, lights and pose source; one outside it beyond a draw distance;
    /// and an actor no render set named, with no heading and a velocity of negative zero.
    /// </summary>
    private static VehicleTelemetry[] Records()
    {
        VehicleTelemetry lent = new VehicleTelemetry(
            115, "vehicle.ue4.audi.tt", "car", "", "0,0,0", "sumo",
            39.5973969, -104.8889602, 1735.95, 1735.9,
            16.43, 180.4, -0.11, 16.43, 0.07,
            4.18, 1.99, 1.39) { HeadingDeg = 180.4 };
        return
        [
            lent with
            {
                Rendered = new RenderedVehicle(115, "yosemite_north_to_south.0", "car.vehicle.ue4.audi.tt", 22475)
                {
                    SumoAngleDegrees = 180.4,
                },
                InFrame = InFrame.Wholly, ApparentWidthPx = 123, ApparentHeightPx = 57, CameraRangeMetres = 87.9,
                Occlusion = 0.0, OcclusionLevel = 0, OcclusionSamples = 174,
                Lights = VehicleLightStateFlags.LowBeam, PoseSource = PoseSource.Sumo,
                Box = new CaptureBox(
                    165.41, 842.97, 288.37, 899.83,
                    [new PixelPoint(165.74, 842.91), new PixelPoint(288.41, 843.72), new PixelPoint(288.04, 899.87),
                     new PixelPoint(165.37, 899.06)],
                    0.0, 0.24, -0.39,
                    [.. Enumerable.Range(0, 8).Select(corner => new GeodeticCorner(39.597378, -104.888949, 1735.97))]),
            },
            lent with
            {
                Id = 116,
                Rendered = new RenderedVehicle(116, "far.0", "car.vehicle.ue4.audi.tt", 22480),
                InFrame = InFrame.None, ApparentWidthPx = 2, ApparentHeightPx = 1,
                OcclusionUnmeasured = OcclusionUnmeasured.BeyondDrawDistance, DrawDistance = DrawDistanceReach.Beyond,
                CameraRangeMetres = 812.4,
            },
            new VehicleTelemetry(
                8, "vehicle.fuso.mitsubishi", "truck", "", "10,20,30", "autopilot",
                39.5901234, -104.8807890, 1700.8, 1700.0,
                0.0, 90.0, -0.0, -0.0, -0.0,
                7.0, 2.5, 3.2),
        ];
    }

    private static XmlSchemaSet Compile()
    {
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.Add(null, Path.Combine(CaptureSchemaPublicationTests.SchemaDirectory, "cot_telemetry.xsd"));
        set.Compile();
        return set;
    }

    private static List<string> Errors(string xml)
    {
        List<string> errors = [];
        XDocument.Parse(xml).Validate(Schema, (_, failed) => errors.Add(failed.Message));
        return errors;
    }
}
