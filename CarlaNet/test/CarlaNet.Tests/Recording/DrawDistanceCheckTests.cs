// A vehicle farther from a camera than the draw distance a SUMO drive was run under is in the world
// and in the truth, and not in that camera's image: its record in that camera's sidecar says so, and
// it is never measured for occlusion against a depth capture that cannot show it either. See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md §8.2 and
// Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md.
using System.Xml.Linq;
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class DrawDistanceCheckTests
{
    private static readonly Location Camera = new(0f, 0f, 100f);

    // A box 3 m by 4 m by 12 m around its centre: a bounding sphere of radius 6.5 m.
    private static readonly BoundingBox Box = new(default, new Vector3D(1.5f, 2f, 6f), default);

    private static VehicleTelemetry Vehicle(uint id, float x, float y, float z, BoundingBox? box = null,
                                            Rotation rotation = default) =>
        new VehicleTelemetry(
            id, "vehicle.fuso.mitsubishi", "truck", "", "10,20,30", "sumo",
            38.9051631, -119.7526194, 1421.6, 1420.9,
            12.5, 90.0, 12.5, 0.0, 0.0,
            7.0, 2.5, 3.2)
        {
            ActorTransform = new Transform(new Location(x, y, z), rotation),
            BoundingBox = box ?? Box,
        };

    [Theory]
    // The centre 100 m away along x: the sphere spans 93.5 m to 106.5 m from the camera.
    [InlineData(110.0, DrawDistanceReach.Inside)]
    [InlineData(106.6, DrawDistanceReach.Inside)]
    [InlineData(106.4, DrawDistanceReach.Partly)]
    [InlineData(100.0, DrawDistanceReach.Partly)]
    [InlineData(93.6, DrawDistanceReach.Partly)]
    [InlineData(93.4, DrawDistanceReach.Beyond)]
    [InlineData(10.0, DrawDistanceReach.Beyond)]
    public void A_Vehicle_Is_Inside_Partly_Or_Wholly_Beyond_By_Its_Bounding_Sphere(double distance,
                                                                                 DrawDistanceReach expected)
    {
        (DrawDistanceReach reach, double range) = DrawDistanceCheck.Of(Vehicle(5, 100f, 0f, 100f), Camera, distance);

        Assert.Equal(expected, reach);
        Assert.Equal(100.0, range, 4);
    }

    [Fact]
    public void The_Range_Is_Taken_To_The_Centre_Of_The_Box_Where_It_Sits_Off_The_Actor_s_Origin()
    {
        // The box's centre is 2 m ahead of the actor's origin along its own x, and the actor faces
        // +y, so the centre is 2 m along world y from the origin.
        var offset = new BoundingBox(new Location(2f, 0f, 0f), new Vector3D(1.5f, 2f, 6f), default);
        VehicleTelemetry record = Vehicle(5, 0f, 98f, 100f, offset, new Rotation(0f, 90f, 0f));

        (_, double range) = DrawDistanceCheck.Of(record, Camera, 500.0);

        Assert.Equal(100.0, range, 4);
    }

    [Fact]
    public void With_No_Draw_Distance_The_Records_Are_Left_As_They_Were()
    {
        IReadOnlyList<VehicleTelemetry> records = [Vehicle(5, 100f, 0f, 100f), Vehicle(6, 900f, 0f, 100f)];

        IReadOnlyList<VehicleTelemetry> marked = DrawDistanceCheck.Mark(records, Camera, null, out int beyond,
                                                                        out int partly);

        Assert.Same(records, marked);
        Assert.Equal(0, beyond);
        Assert.Equal(0, partly);
        Assert.All(marked, record => Assert.Equal(DrawDistanceReach.Inside, record.DrawDistance));
        Assert.All(marked, record => Assert.True(double.IsNaN(record.CameraRangeMetres)));
    }

    [Fact]
    public void Every_Record_Is_Marked_And_The_Ones_Beyond_Are_Counted()
    {
        IReadOnlyList<VehicleTelemetry> records =
            [Vehicle(5, 30f, 0f, 100f), Vehicle(6, 200f, 0f, 100f), Vehicle(7, 900f, 0f, 100f)];

        IReadOnlyList<VehicleTelemetry> marked = DrawDistanceCheck.Mark(records, Camera, 200.0, out int beyond,
                                                                        out int partly);

        Assert.Equal([DrawDistanceReach.Inside, DrawDistanceReach.Partly, DrawDistanceReach.Beyond],
                     marked.Select(record => record.DrawDistance));
        Assert.Equal([5u, 6u, 7u], marked.Select(record => record.Id));
        Assert.Equal(1, beyond);
        Assert.Equal(1, partly);
        Assert.Equal(900.0, marked[2].CameraRangeMetres, 3);
    }

    [Fact]
    public void A_Vehicle_Wholly_Beyond_Is_Never_Measured_For_Occlusion()
    {
        IReadOnlyList<VehicleTelemetry> marked = DrawDistanceCheck.Mark(
            [Vehicle(5, 30f, 0f, 100f), Vehicle(6, 200f, 0f, 100f), Vehicle(7, 900f, 0f, 100f)], Camera, 200.0,
            out _, out _);

        Assert.Equal([true, true, false], marked.Select(DrawDistanceCheck.MayBeMeasuredForOcclusion));
    }

    [Fact]
    public void The_Sidecar_States_The_Distance_And_Marks_Every_Vehicle_It_Kept_Out_Of_The_Image()
    {
        IReadOnlyList<VehicleTelemetry> marked = DrawDistanceCheck.Mark(
            [Vehicle(5, 30f, 0f, 100f), Vehicle(6, 200f, 0f, 100f), Vehicle(7, 900f, 0f, 100f)], Camera, 200.0,
            out _, out _);

        XElement events = Sidecar(marked, 200.0);

        Assert.Equal("200", (string?)events.Attribute("draw_distance_m"));
        Dictionary<string, XElement> extras = events.Elements("event")
            .ToDictionary(e => (string)e.Attribute("uid")!, e => e.Element("detail")!.Element("_carla")!);
        Assert.Null(extras["CARLA-TRUTH-5"].Attribute("beyond_draw_distance"));
        Assert.Null(extras["CARLA-TRUTH-5"].Attribute("camera_range_m"));
        Assert.Equal("partly", (string?)extras["CARLA-TRUTH-6"].Attribute("beyond_draw_distance"));
        Assert.Equal("200.0", (string?)extras["CARLA-TRUTH-6"].Attribute("camera_range_m"));
        Assert.Equal("wholly", (string?)extras["CARLA-TRUTH-7"].Attribute("beyond_draw_distance"));
        Assert.Equal("900.0", (string?)extras["CARLA-TRUTH-7"].Attribute("camera_range_m"));

        // The vehicle is still listed: it is in the world, with its truth.
        Assert.Equal("3", (string?)events.Attribute("count"));
    }

    [Fact]
    public void A_Sidecar_With_No_Draw_Distance_Carries_No_Trace_Of_One()
    {
        XElement events = Sidecar([Vehicle(5, 30f, 0f, 100f), Vehicle(7, 900f, 0f, 100f)], null);

        Assert.Null(events.Attribute("draw_distance_m"));
        Assert.All(events.Descendants("_carla"), extras =>
        {
            Assert.Null(extras.Attribute("beyond_draw_distance"));
            Assert.Null(extras.Attribute("camera_range_m"));
        });
    }

    [Fact]
    public void A_Paired_Capture_Takes_The_Distance_Its_Own_Frame_Was_Drawn_Under()
    {
        var source = new OneFrame(new RenderSet([new RenderedVehicle(5, "flow_0.1", "passenger", 90)])
        {
            DrawDistanceMetres = 300.0,
        });
        var pairing = new RenderSetPairing(source, TimeSpan.FromMilliseconds(10));

        PairedTruth paired = pairing.Pair([Vehicle(5, 30f, 0f, 100f)], 100);
        PairedTruth unpaired = pairing.Pair([Vehicle(5, 30f, 0f, 100f)], 99);

        Assert.Equal(300.0, paired.DrawDistanceMetres);
        Assert.Null(unpaired.DrawDistanceMetres);
        Assert.Equal(SidecarVehicles.Unknown, unpaired.Vehicles);
    }

    private static XElement Sidecar(IReadOnlyList<VehicleTelemetry> records, double? drawDistance)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc), records,
                                  capture: new CaptureIdentity(100, 5.0, "run-1", TelemetryTick: 100),
                                  drawDistanceMetres: drawDistance);
            return XDocument.Load(path).Root!;
        }
        finally { File.Delete(path); }
    }

    /// <summary>A source holding one frame, frame 100.</summary>
    private sealed class OneFrame(RenderSet renderSet) : IRenderSetSource
    {
        public ulong? NewestFrame => 100;

        public bool TryGetRenderSet(ulong frame, out RenderSet held)
        {
            held = renderSet;
            return frame == 100;
        }
    }
}
