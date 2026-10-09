// A vehicle whose box fell in a capture's picture carries its box on its record in the truth sidecar --
// the axis-aligned and the minimum-area rectangles in pixels, the share outside the picture, the range
// from the camera, the body's tilt, and the box's eight corners in latitude, longitude and bare-earth
// height converted as the record's own point is -- and no other vehicle does: the owner's rulings of
// 2026-10-06. Every number below is worked by hand from the camera's pinhole and the box's corners.
// See Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md §5.1.
using System.Xml.Linq;
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class CaptureBoxTests
{
    private const int Width = 200, Height = 100;
    private const double HFovDeg = 90.0;      // focal length = Width / 2 = 100 px

    // Bahonar's georeference, and a surface shift of the kind height-align takes off a vehicle's point.
    private static readonly GeoLocation Origin = new(27.15012, 56.18065, 12.0);
    private const double AlignOffset = 3.25;

    // A 4 m x 2 m x 1.5 m box standing on the actor's origin, as a vehicle's box stands on its wheels.
    private static readonly BoundingBox Standing =
        new(new Location(0f, 0f, 0.75f), new Vector3D(2f, 1f, 0.75f), new Rotation());

    // The same box centered on the actor's origin, as BoxProjectorTests places it.
    private static readonly BoundingBox Centered =
        new(new Location(0f, 0f, 0f), new Vector3D(2f, 1f, 0.75f), new Rotation());

    // 21.5 m above a point, looking straight down: the box's roof, 1.5 m up, is 20 m from the lens, so a
    // meter on the roof is 100 / 20 = 5 px. +Y is right in the picture and +X up it.
    private static PinholeCamera StraightDownAbove(double x, double y) =>
        new(new Transform(new Location((float)x, (float)y, 21.5f), new Rotation(-90f, 0f, 0f)), Width, Height, HFovDeg);

    // Level at the origin, looking along +X: +Y is right in the picture, +Z up.
    private static readonly PinholeCamera Boresight =
        new(new Transform(new Location(0f, 0f, 0f), new Rotation(0f, 0f, 0f)), Width, Height, HFovDeg);

    private static Transform At(double x, double y, double z, float yawDeg = 0f, float pitchDeg = 0f, float rollDeg = 0f) =>
        new(new Location((float)x, (float)y, (float)z), new Rotation(pitchDeg, yawDeg, rollDeg));

    /// A record as VehicleTelemetryService builds one: its point converted from the georeference origin,
    /// less the height-align offset.
    private static VehicleTelemetry Vehicle(uint id, Transform pose, BoundingBox box)
    {
        GeoLocation geo = Geodesy.CarlaLocalToGeodetic(Origin, pose.Location.X, pose.Location.Y, pose.Location.Z);
        return new VehicleTelemetry(
            id, "vehicle.audi.tt", "car", "", "0,0,0", "sumo",
            geo.Latitude, geo.Longitude, geo.Altitude - AlignOffset, geo.Altitude - AlignOffset,
            12.5, 90.0, 12.5, 0.0, 0.0,
            4.0, 2.0, 1.5)
        {
            // The direction the body points, as the service writes it from the yaw.
            HeadingDeg = (Math.Atan2(Math.Cos(pose.Rotation.Yaw * Math.PI / 180.0),
                                     -Math.Sin(pose.Rotation.Yaw * Math.PI / 180.0)) * 180.0 / Math.PI + 360.0) % 360.0,
            ActorTransform = pose,
            BoundingBox = box,
            HeightAlignOffset = AlignOffset,
        };
    }

    private static VehicleTelemetry Marked(VehicleTelemetry record, PinholeCamera camera) =>
        BoxProjector.Mark([record], camera, Origin)[0];

    private static void Near(double expected, double actual, double tolerance = 1e-4) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static void Near(PixelPoint expected, PixelPoint actual)
    {
        Near(expected.U, actual.U);
        Near(expected.V, actual.V);
    }

    [Fact]
    public void Straight_Down_On_A_Vehicle_Square_To_The_Picture_Both_Rectangles_Are_Its_Roof()
    {
        // The roof spans x = -2 to 2 and y = -1 to 1 at 5 px a meter about the picture's center (100, 50);
        // the floor, 21.5 m away, is smaller and inside it.
        VehicleTelemetry record = Marked(Vehicle(5, At(0, 0, 0), Standing), StraightDownAbove(0, 0));
        CaptureBox box = record.Box!;

        Assert.Equal(InFrame.Wholly, record.InFrame);
        Near(95.0, box.MinU);
        Near(40.0, box.MinV);
        Near(105.0, box.MaxU);
        Near(60.0, box.MaxV);
        Assert.Equal(4, box.Oriented.Count);
        Near(new PixelPoint(95, 40), box.Oriented[0]);
        Near(new PixelPoint(105, 40), box.Oriented[1]);
        Near(new PixelPoint(105, 60), box.Oriented[2]);
        Near(new PixelPoint(95, 60), box.Oriented[3]);
        Assert.Equal(0.0, box.Truncation);
        Assert.Equal(0.0, box.PitchDeg);
        Assert.Equal(0.0, box.RollDeg);
        // The box's center, 0.75 m up, is 20.75 m below the lens.
        Near(20.75, record.CameraRangeMetres);
    }

    [Fact]
    public void Straight_Down_On_A_Vehicle_Turned_Thirty_Degrees_The_Oriented_Rectangle_Follows_The_Body()
    {
        // At yaw 30 the roof's corners stand at x = 2 cos 30 + sin 30 = 2.232051, y = 2 sin 30 - cos 30 =
        // 0.133975 (front left), (1.232051, 1.866025) (front right), and their opposites behind; at 5 px a
        // meter, u = 100 + 5y and v = 50 - 5x.
        VehicleTelemetry record = Marked(Vehicle(5, At(0, 0, 0, yawDeg: 30f), Standing), StraightDownAbove(0, 0));
        CaptureBox box = record.Box!;

        Near(90.669873, box.MinU);
        Near(38.839746, box.MinV);
        Near(109.330127, box.MaxU);
        Near(61.160254, box.MaxV);
        // Clockwise in the picture from the top-most corner: front left, front right, back right, back left.
        Near(new PixelPoint(100.669873, 38.839746), box.Oriented[0]);
        Near(new PixelPoint(109.330127, 43.839746), box.Oriented[1]);
        Near(new PixelPoint(99.330127, 61.160254), box.Oriented[2]);
        Near(new PixelPoint(90.669873, 56.160254), box.Oriented[3]);
        // The roof's own 4 m x 2 m, 20 px x 10 px, where the axis-aligned box is 18.66 px x 22.32 px.
        Near(200.0, Area(box.Oriented), 1e-3);
        Near(18.660254 * 22.320508, (box.MaxU - box.MinU) * (box.MaxV - box.MinV), 1e-3);
        Assert.Equal(0.0, box.Truncation);
    }

    [Fact]
    public void A_Box_Straddling_The_Right_Edge_Is_Truncated_By_The_Share_Outside()
    {
        // The centered box spans x = 8 to 12 and y = 9 to 11: u = 100 + 100 y / x runs from 175 (y = 9 at
        // x = 12) to 237.5 (y = 11 at x = 8), and the picture ends at 200, so 25 of 62.5 px are inside.
        VehicleTelemetry record = Marked(Vehicle(8, At(10, 10, 0), Centered), Boresight);
        CaptureBox box = record.Box!;

        Assert.Equal(InFrame.Partly, record.InFrame);
        Near(175.0, box.MinU);
        Near(237.5, box.MaxU);
        Near(40.625, box.MinV);
        Near(59.375, box.MaxV);
        Near(0.6, box.Truncation, 1e-9);
        // The rectangle is not clipped: it runs past the picture's edge as far as the box does.
        Assert.True(box.MaxU > Width);
        Assert.All(box.Oriented, corner => Assert.InRange(corner.U, 175.0 - 1e-6, 237.5 + 1e-6));
    }

    [Fact]
    public void A_Box_Straddling_A_Corner_Is_Truncated_By_The_Share_Outside_Both_Edges()
    {
        // Lifted 5 m, the box's v = 50 - 100 z / x runs from -21.875 (z = 5.75 at x = 8) to 14.583
        // (z = 4.25 at x = 12): 0.4 of it below the top edge, as 0.4 of it is left of the right edge.
        // Inside: 0.4 x 0.4 = 0.16 of the area.
        CaptureBox box = Marked(Vehicle(8, At(10, 10, 5), Centered), Boresight).Box!;

        Near(-21.875, box.MinV);
        Near(175.0 / 12.0, box.MaxV);
        Near(0.84, box.Truncation, 1e-9);
    }

    [Fact]
    public void The_Corners_Are_Converted_As_The_Records_Own_Point_And_Agree_With_It()
    {
        // Turned 30 degrees, at (10, 20, 0). CARLA's +X is east and +Y south, so the front left corner
        // stands 2.232051 m east and 0.133975 m south of the point, on the ground, and so on round.
        VehicleTelemetry record = Marked(Vehicle(5, At(10, 20, 0, yawDeg: 30f), Standing), StraightDownAbove(10, 20));
        IReadOnlyList<GeodeticCorner> corners = record.Box!.Corners;

        Assert.Equal(8, corners.Count);
        GeoLocation point = new(record.Lat, record.Lon, record.Hae);
        (double East, double North, double Up)[] expected =
        [
            (2.232051, -0.133975, 0.0), (1.232051, -1.866025, 0.0),      // front left, front right
            (-2.232051, 0.133975, 0.0), (-1.232051, 1.866025, 0.0),      // back right, back left
            (2.232051, -0.133975, 1.5), (1.232051, -1.866025, 1.5),      // the roof, the same way round
            (-2.232051, 0.133975, 1.5), (-1.232051, 1.866025, 1.5),
        ];
        for (int index = 0; index < 8; index++)
        {
            (double east, double north, double up) = Geodesy.GeodeticToEnu(
                point, new GeoLocation(corners[index].Lat, corners[index].Lon, corners[index].Hae));
            Near(expected[index].East, east, 1e-3);
            Near(expected[index].North, north, 1e-3);
            Near(expected[index].Up, up, 1e-3);
        }

        // The floor's center is the point: its corners average to the record's own lat, lon and hae, in
        // the bare-earth convention, with the same offset taken off.
        Near(record.Lat, corners.Take(4).Average(corner => corner.Lat), 1e-9);
        Near(record.Lon, corners.Take(4).Average(corner => corner.Lon), 1e-9);
        Near(record.Hae, corners.Take(4).Average(corner => corner.Hae), 1e-4);
        Assert.Equal(["front_left_bottom", "front_right_bottom", "back_right_bottom", "back_left_bottom",
                      "front_left_top", "front_right_top", "back_right_top", "back_left_top"],
                     CaptureBoxes.CornerNames);
    }

    [Fact]
    public void Every_Corner_Takes_The_Offset_The_Records_Point_Took()
    {
        // Two records of one pose, one with no surface shift: every corner differs by the shift, as the
        // point does, so the box is the body's box shifted whole.
        VehicleTelemetry shifted = Vehicle(5, At(10, 20, 0, yawDeg: 30f), Standing);
        VehicleTelemetry unshifted = shifted with { Hae = shifted.Hae + AlignOffset, HeightAlignOffset = 0.0 };

        IReadOnlyList<GeodeticCorner> a = Marked(shifted, StraightDownAbove(10, 20)).Box!.Corners;
        IReadOnlyList<GeodeticCorner> b = Marked(unshifted, StraightDownAbove(10, 20)).Box!.Corners;

        for (int index = 0; index < 8; index++)
        {
            Assert.Equal(b[index].Lat, a[index].Lat);
            Assert.Equal(b[index].Lon, a[index].Lon);
            Near(AlignOffset, b[index].Hae - a[index].Hae, 1e-9);
        }
    }

    [Fact]
    public void The_Tilt_Is_The_Bodys_Pitch_And_Roll_From_Its_Transform()
    {
        CaptureBox box = Marked(Vehicle(5, At(0, 0, 0, yawDeg: 30f, pitchDeg: 4.5f, rollDeg: -1.25f), Standing),
                                StraightDownAbove(0, 0)).Box!;

        Near(4.5, box.PitchDeg, 1e-6);
        Near(-1.25, box.RollDeg, 1e-6);
    }

    [Fact]
    public void Only_A_Vehicle_In_The_Picture_Gets_A_Box()
    {
        IReadOnlyList<VehicleTelemetry> marked = BoxProjector.Mark(
            [Vehicle(5, At(10, 0, 0), Centered), Vehicle(6, At(10, 100, 0), Centered),
             Vehicle(7, At(-10, 0, 0), Centered), Vehicle(8, At(10, 10, 0), Centered)],
            Boresight, Origin);

        Assert.Equal([InFrame.Wholly, InFrame.None, InFrame.BehindCamera, InFrame.Partly],
                     marked.Select(record => record.InFrame));
        Assert.NotNull(marked[0].Box);
        Assert.Null(marked[1].Box);
        Assert.Null(marked[2].Box);
        Assert.NotNull(marked[3].Box);
        Assert.True(double.IsNaN(marked[1].CameraRangeMetres));
        Assert.True(double.IsNaN(marked[2].CameraRangeMetres));
        Near(10.0, marked[0].CameraRangeMetres, 1e-6);
    }

    [Fact]
    public void A_Projection_Given_No_Origin_Gives_No_Box()
    {
        // The projection that marks only where each box fell, as it always has.
        VehicleTelemetry record = BoxProjector.Mark([Vehicle(5, At(10, 0, 0), Centered)], Boresight)[0];

        Assert.Equal(InFrame.Wholly, record.InFrame);
        Assert.Null(record.Box);
        Assert.True(double.IsNaN(record.CameraRangeMetres));
    }

    [Fact]
    public void The_Sidecar_Writes_The_Box_Fields_On_A_Vehicle_In_The_Picture_And_None_On_Any_Other()
    {
        IReadOnlyList<VehicleTelemetry> marked = UnmeasuredOcclusion.Mark(
            BoxProjector.Mark([Vehicle(5, At(10, 0, 0), Centered), Vehicle(6, At(10, 100, 0), Centered),
                               Vehicle(7, At(-10, 0, 0), Centered), Vehicle(8, At(10, 10, 0), Centered)],
                              Boresight, Origin),
            OcclusionUnmeasured.NoDepthCamera);

        Dictionary<string, XElement> details = Details(Sidecar(marked));

        foreach (string uid in new[] { "CARLA-TRUTH-5", "CARLA-TRUTH-8" })
        {
            XElement carla = details[uid].Element("_carla")!;
            foreach (string field in BoxFields)
                Assert.NotNull(carla.Attribute(field));
            XElement box3d = details[uid].Element("_box3d")!;
            Assert.Equal("geodetic", (string?)box3d.Attribute("frame"));
            Assert.Equal(8, box3d.Elements("corner").Count());
            Assert.All(box3d.Elements("corner"), corner =>
                Assert.Equal(["lat", "lon", "hae"], corner.Attributes().Select(a => a.Name.LocalName)));
        }

        double[] rectangle = Numbers(details["CARLA-TRUTH-8"].Element("_carla")!.Attribute("box_px"));
        Assert.Equal(4, rectangle.Length);
        Near(175.0, rectangle[0], 0.006);
        Near(40.625, rectangle[1], 0.006);
        Near(237.5, rectangle[2], 0.006);
        Near(59.375, rectangle[3], 0.006);
        Assert.Equal(8, Numbers(details["CARLA-TRUTH-8"].Element("_carla")!.Attribute("box_oriented_px")).Length);
        Assert.Equal("0.600", (string?)details["CARLA-TRUTH-8"].Element("_carla")!.Attribute("truncation"));
        Assert.Equal("10.0", (string?)details["CARLA-TRUTH-5"].Element("_carla")!.Attribute("camera_range_m"));

        foreach (string uid in new[] { "CARLA-TRUTH-6", "CARLA-TRUTH-7" })
        {
            XElement carla = details[uid].Element("_carla")!;
            foreach (string field in BoxFields)
                Assert.Null(carla.Attribute(field));
            Assert.Null(details[uid].Element("_box3d"));
        }
    }

    [Fact]
    public void A_Vehicle_Square_To_A_Camera_Straight_Above_Is_Written_In_Full()
    {
        VehicleTelemetry record = UnmeasuredOcclusion.Mark(
            [Marked(Vehicle(5, At(0, 0, 0, yawDeg: 30f), Standing), StraightDownAbove(0, 0))],
            OcclusionUnmeasured.NoDepthCamera)[0];

        XElement detail = Details(Sidecar([record]))["CARLA-TRUTH-5"];
        XElement carla = detail.Element("_carla")!;

        Assert.Equal("0.00", (string?)carla.Attribute("pitch_deg"));
        Assert.Equal("0.00", (string?)carla.Attribute("roll_deg"));
        Assert.Equal("90.67 38.84 109.33 61.16", (string?)carla.Attribute("box_px"));
        Assert.Equal("100.67 38.84 109.33 43.84 99.33 61.16 90.67 56.16", (string?)carla.Attribute("box_oriented_px"));
        Assert.Equal("0.000", (string?)carla.Attribute("truncation"));
        // The attributes in the order written: the tilt beside the heading, the rectangles beside the
        // apparent size, the range after the reason.
        Assert.Equal(["heading_deg", "pitch_deg", "roll_deg", "in_frame", "apparent_width_px", "apparent_height_px",
                      "box_px", "box_oriented_px", "truncation", "occlusion_unmeasured", "camera_range_m"],
                     carla.Attributes().Select(a => a.Name.LocalName).SkipWhile(name => name != "heading_deg"));
        // Each corner exactly as the record's own point is written, to the same places.
        GeodeticCorner first = record.Box!.Corners[0];
        XElement corner = detail.Element("_box3d")!.Elements("corner").First();
        Assert.Equal(first.Lat.ToString("0.0000000", System.Globalization.CultureInfo.InvariantCulture),
                     (string?)corner.Attribute("lat"));
        Assert.Equal(first.Hae.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                     (string?)corner.Attribute("hae"));
        // The roof is written 1.5 m above the floor.
        double[] heights = detail.Element("_box3d")!.Elements("corner").Select(c => (double)c.Attribute("hae")!).ToArray();
        Assert.All(heights.Take(4), h => Near(heights[0], h, 0.011));
        Assert.All(heights.Skip(4), h => Near(heights[0] + 1.5, h, 0.011));
    }

    [Fact]
    public void Under_A_Draw_Distance_A_Vehicle_In_The_Picture_Carries_One_Range()
    {
        // Marked against the draw distance and in the picture: both rest on one range, written once.
        IReadOnlyList<VehicleTelemetry> drawn = DrawDistanceCheck.Mark(
            [Vehicle(5, At(10, 0, 0), Centered)], Boresight.Pose.Location, 10.5, out _, out int partly);
        VehicleTelemetry record = BoxProjector.Mark(drawn, Boresight, Origin)[0];

        Assert.Equal(1, partly);
        Assert.Equal(drawn[0].CameraRangeMetres, record.CameraRangeMetres);
        XElement carla = Details(Sidecar([record]))["CARLA-TRUTH-5"].Element("_carla")!;
        Assert.Equal("partly", (string?)carla.Attribute("beyond_draw_distance"));
        Assert.Equal("10.0", (string?)carla.Attribute("camera_range_m"));
        Assert.NotNull(carla.Attribute("box_px"));
    }

    [Fact]
    public void A_Record_No_Camera_Projected_Carries_No_Box()
    {
        // The live pull's shape, and a record that somehow holds a box without a place: neither is written.
        VehicleTelemetry inPicture = Marked(Vehicle(5, At(10, 0, 0), Centered), Boresight);
        VehicleTelemetry placeless = inPicture with { InFrame = null };

        Dictionary<string, XElement> details = Details(Sidecar([Vehicle(6, At(10, 0, 0), Centered), placeless]));

        foreach (XElement detail in details.Values)
        {
            foreach (string field in BoxFields)
                Assert.Null(detail.Element("_carla")!.Attribute(field));
            Assert.Null(detail.Element("_box3d"));
        }
    }

    [Fact]
    public void The_Oriented_Rectangle_Encloses_Every_Corner_And_Is_Never_Larger_Than_The_Axis_Aligned_One()
    {
        // Vehicles at any pose seen from cameras at any pose: wherever the box fell in the picture, its
        // oriented rectangle holds all eight projected corners, runs clockwise from the top-most, and has
        // no more area than the axis-aligned one; a box wholly in the picture is not truncated.
        var random = new Random(20261006);
        int inPicture = 0;
        for (int trial = 0; trial < 2000; trial++)
        {
            var camera = new PinholeCamera(
                At(random.NextDouble() * 40 - 20, random.NextDouble() * 40 - 20, 5 + random.NextDouble() * 200,
                   yawDeg: (float)(random.NextDouble() * 360), pitchDeg: (float)(-90 + random.NextDouble() * 80),
                   rollDeg: (float)(random.NextDouble() * 20 - 10)),
                Width, Height, 20 + random.NextDouble() * 100);
            Transform pose = At(random.NextDouble() * 200 - 100, random.NextDouble() * 200 - 100, random.NextDouble() * 4,
                                yawDeg: (float)(random.NextDouble() * 360), pitchDeg: (float)(random.NextDouble() * 10 - 5),
                                rollDeg: (float)(random.NextDouble() * 10 - 5));
            VehicleTelemetry record = Marked(Vehicle(5, pose, Standing), camera);
            if (record.Box is not { } box)
                continue;
            inPicture++;

            PixelPoint[] corners = BoxProjector.ProjectCorners(camera, pose, Standing)!;
            // How far from the picture's corner the corners lie, which is what the arithmetic's rounding
            // grows with.
            double scale = Math.Max(1.0, corners.Max(c => Math.Max(Math.Abs(c.U), Math.Abs(c.V))));
            for (int side = 0; side < 4; side++)
            {
                PixelPoint a = box.Oriented[side], b = box.Oriented[(side + 1) % 4];
                // Clockwise in the picture with V down: every corner on the right of each side, or on it.
                Assert.All(corners, c =>
                    Assert.True(((b.U - a.U) * (c.V - a.V)) - ((b.V - a.V) * (c.U - a.U)) >= -1e-9 * scale * scale));
            }

            Assert.True(Area(box.Oriented) <= ((box.MaxU - box.MinU) * (box.MaxV - box.MinV)) * (1 + 1e-9) + 1e-9);
            Assert.All(box.Oriented, c => Assert.True(c.V >= box.Oriented[0].V - 1e-3));
            Assert.InRange(box.Truncation, 0.0, 1.0);
            if (record.InFrame == InFrame.Wholly)
                Assert.Equal(0.0, box.Truncation);
            else
                Assert.True(box.Truncation > 0.0);
        }

        Assert.True(inPicture > 200, $"only {inPicture} of the trials put the box in the picture");
    }

    [Fact]
    public void A_Box_With_No_Extent_Or_Seen_Edge_On_Has_A_Rectangle_Of_No_Area()
    {
        PixelPoint[] point = CaptureBoxes.MinimumAreaRectangle(Enumerable.Repeat(new PixelPoint(3, 4), 8).ToArray());
        PixelPoint[] line = CaptureBoxes.MinimumAreaRectangle(
            [new PixelPoint(0, 0), new PixelPoint(10, 10), new PixelPoint(5, 5), new PixelPoint(10, 10)]);

        Assert.All(point, corner => Assert.Equal(new PixelPoint(3, 4), corner));
        Assert.Equal(0.0, Area(line));
        Assert.Contains(new PixelPoint(0, 0), line);
        Assert.Contains(new PixelPoint(10, 10), line);
        Assert.Equal(new PixelPoint(0, 0), line[0]);
        Assert.Throws<ArgumentException>(() => CaptureBoxes.MinimumAreaRectangle([]));
    }

    [Fact]
    public void Truncation_Has_No_Meaning_Without_A_Rectangle()
        => Assert.Throws<ArgumentException>(() => CaptureBoxes.Truncation(
            new BoxProjection(InFrame.BehindCamera, double.NaN, double.NaN, double.NaN, double.NaN), Width, Height));

    // The box fields of the _carla block: none on a vehicle outside the picture or with no camera.
    private static readonly string[] BoxFields =
        ["box_px", "box_oriented_px", "truncation", "pitch_deg", "roll_deg", "camera_range_m"];

    private static double Area(IReadOnlyList<PixelPoint> rectangle)
    {
        double signed = 0.0;
        for (int index = 0; index < rectangle.Count; index++)
        {
            PixelPoint a = rectangle[index], b = rectangle[(index + 1) % rectangle.Count];
            signed += (a.U * b.V) - (b.U * a.V);
        }

        return Math.Abs(signed) / 2.0;
    }

    private static double[] Numbers(XAttribute? attribute) =>
        ((string)attribute!).Split(' ').Select(n => double.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static XElement Sidecar(IReadOnlyList<VehicleTelemetry> records)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc), records,
                                  capture: new CaptureIdentity(100, 5.0, "run-1"));
            return XDocument.Load(path).Root!;
        }
        finally { File.Delete(path); }
    }

    private static Dictionary<string, XElement> Details(XElement events) =>
        events.Elements("event").ToDictionary(e => (string)e.Attribute("uid")!, e => e.Element("detail")!);
}
