// Every vehicle record of a capture says where the vehicle's box fell against the picture -- wholly in
// it, partly, outside it, or with a corner at or behind the lens -- and how large it appears, from the
// box's eight corners projected through the camera's pinhole and nothing else; and where its occlusion
// was not measured, why. The owner's ruling of 2026-10-05: a truth field carries what was declared, what
// happened, or a measurement from the frame's geometry by a fixed published method with no pass mark.
// See Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md §5.1.
using System.Xml.Linq;
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class BoxProjectorTests
{
    private const int Width = 200, Height = 100;
    private const double HFovDeg = 90.0;      // focal length = Width / 2 = 100 px

    // Looking along +X from the origin, level and upright: +Y is right in the picture, +Z is up.
    private static readonly PinholeCamera Boresight =
        new(new Transform(new Location(0f, 0f, 0f), new Rotation(0f, 0f, 0f)), Width, Height, HFovDeg);

    // A 4 m x 2 m x 1.5 m vehicle -- a saloon's bounding box -- at a point, facing +X.
    private static readonly BoundingBox Saloon =
        new(new Location(0f, 0f, 0f), new Vector3D(2f, 1f, 0.75f), new Rotation());

    private static Transform At(double x, double y, double z, float yawDeg = 0f) =>
        new(new Location((float)x, (float)y, (float)z), new Rotation(0f, yawDeg, 0f));

    private static VehicleTelemetry Vehicle(uint id, double x, double y, double z) => new VehicleTelemetry(
        id, "vehicle.audi.tt", "car", "", "0,0,0", "sumo",
        38.9051631, -119.7526194, 1421.6, 1420.9,
        12.5, 90.0, 12.5, 0.0, 0.0,
        4.0, 2.0, 1.5)
    {
        ActorTransform = At(x, y, z),
        BoundingBox = Saloon,
    };

    [Fact]
    public void A_Vehicle_On_The_Boresight_Is_Wholly_In_The_Picture()
    {
        BoxProjection projection = BoxProjector.Project(Boresight, At(10, 0, 0), Saloon);

        Assert.Equal(InFrame.Wholly, projection.InFrame);
        Assert.True(projection.IsInPicture);
        Assert.True(projection.HasFootprint);
        // 2 m wide at 8 to 12 m: the near face spans 100 * 1 / 8 = 12.5 px either side of the centre.
        Assert.InRange(projection.MinU, 87.0, 88.0);
        Assert.InRange(projection.MaxU, 112.0, 113.0);
        Assert.Equal(25, projection.ApparentWidthPx);
        Assert.Equal(19, projection.ApparentHeightPx);
    }

    [Fact]
    public void A_Vehicle_Far_To_The_Side_Is_Outside_The_Picture_And_Still_Has_An_Apparent_Size()
    {
        BoxProjection projection = BoxProjector.Project(Boresight, At(10, 100, 0), Saloon);

        Assert.Equal(InFrame.None, projection.InFrame);
        Assert.False(projection.IsInPicture);
        Assert.True(projection.HasFootprint);
        Assert.True(projection.MinU > Width);
        Assert.True(projection.ApparentWidthPx > 0);
    }

    [Fact]
    public void A_Vehicle_Crossing_The_Right_Edge_Is_Partly_In_The_Picture()
    {
        // The box spans y = 9 to 11 at x = 10 (its near face at x = 8): its right edge projects past
        // the picture's right edge at u = 200 and its left edge inside it.
        BoxProjection projection = BoxProjector.Project(Boresight, At(10, 10, 0), Saloon);

        Assert.Equal(InFrame.Partly, projection.InFrame);
        Assert.True(projection.IsInPicture);
        Assert.True(projection.MinU < Width && projection.MaxU > Width);
    }

    [Fact]
    public void A_Rectangle_That_Only_Touches_An_Edge_From_Outside_Shows_No_Pixel_And_Is_Outside()
    {
        // A flat box 10 m ahead whose left edge, at y = 10, projects exactly on the picture's right
        // edge at u = 200.
        BoxProjection touching = BoxProjector.Project(
            Boresight, At(10, 0, 0), new BoundingBox(new Location(0f, 11f, 0f), new Vector3D(0f, 1f, 0.75f), new Rotation()));

        Assert.Equal(200.0, touching.MinU, 6);
        Assert.Equal(InFrame.None, touching.InFrame);
    }

    [Fact]
    public void A_Corner_At_Or_Behind_The_Lens_Has_No_Projection()
    {
        // The box's rear face is 2 m behind the camera; its front face 2 m ahead of it.
        BoxProjection straddling = BoxProjector.Project(Boresight, At(0, 0, 0), Saloon);
        BoxProjection behind = BoxProjector.Project(Boresight, At(-10, 0, 0), Saloon);

        Assert.Equal(InFrame.BehindCamera, straddling.InFrame);
        Assert.Equal(InFrame.BehindCamera, behind.InFrame);
        Assert.False(straddling.HasFootprint);
        Assert.False(straddling.IsInPicture);
        Assert.Equal(0, straddling.ApparentWidthPx);
        Assert.Equal(0, straddling.ApparentHeightPx);
        Assert.True(double.IsNaN(straddling.MinU));
    }

    [Fact]
    public void The_Near_Plane_Is_The_Only_Distance_The_Projection_Rests_On()
    {
        // A box whose nearest corner stands just past the near plane projects; one at the plane does not.
        var sliver = new BoundingBox(new Location(0f, 0f, 0f), new Vector3D(0.01f, 0.01f, 0.01f), new Rotation());
        double justPast = BoxProjector.NearPlaneMetres + 0.01 + 1e-6;
        double atThePlane = BoxProjector.NearPlaneMetres + 0.01;

        Assert.NotEqual(InFrame.BehindCamera, BoxProjector.Project(Boresight, At(justPast, 0, 0), sliver).InFrame);
        Assert.Equal(InFrame.BehindCamera, BoxProjector.Project(Boresight, At(atThePlane, 0, 0), sliver).InFrame);
    }

    [Fact]
    public void Works_From_An_Airborne_Nadir_View()
    {
        // 100 m up, looking straight down: the 90 degree picture covers 200 m across and 100 m down
        // the ground, with +Y to the right and +X up the picture.
        var nadir = new PinholeCamera(new Transform(new Location(0f, 0f, 100f), new Rotation(-90f, 0f, 0f)),
                                      Width, Height, HFovDeg);

        Assert.Equal(InFrame.Wholly, BoxProjector.Project(nadir, At(0, 0, 0), Saloon).InFrame);
        Assert.Equal(InFrame.Wholly, BoxProjector.Project(nadir, At(40, 90, 0), Saloon).InFrame);
        Assert.Equal(InFrame.Partly, BoxProjector.Project(nadir, At(0, 100, 0), Saloon).InFrame);
        Assert.Equal(InFrame.None, BoxProjector.Project(nadir, At(0, 150, 0), Saloon).InFrame);
        Assert.Equal(InFrame.None, BoxProjector.Project(nadir, At(60, 0, 0), Saloon).InFrame);
        Assert.Equal(InFrame.BehindCamera, BoxProjector.Project(nadir, At(0, 0, 150), Saloon).InFrame);
    }

    [Fact]
    public void Apparent_Size_Shrinks_With_Range_And_Never_Needs_A_Depth_Capture()
    {
        BoxProjection near = BoxProjector.Project(Boresight, At(20, 0, 0), Saloon);
        BoxProjection far = BoxProjector.Project(Boresight, At(400, 0, 0), Saloon);

        Assert.True(near.ApparentWidthPx > far.ApparentWidthPx);
        Assert.True(far.ApparentWidthPx > 0);
        Assert.InRange((double)near.ApparentWidthPx / far.ApparentWidthPx, 10.0, 30.0);
    }

    [Theory]
    [InlineData(InFrame.Wholly, "wholly")]
    [InlineData(InFrame.Partly, "partly")]
    [InlineData(InFrame.None, "none")]
    [InlineData(InFrame.BehindCamera, "behind_camera")]
    public void Each_Place_A_Box_Falls_Has_Its_Sidecar_Word(InFrame inFrame, string word)
        => Assert.Equal(word, BoxProjector.SidecarValue(inFrame));

    [Fact]
    public void A_Place_Outside_The_Four_Is_Refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BoxProjector.SidecarValue((InFrame)9));

    [Theory]
    [InlineData(0, 100, 90.0)]
    [InlineData(200, 0, 90.0)]
    [InlineData(200, 100, 0.0)]
    [InlineData(200, 100, 180.0)]
    [InlineData(200, 100, -45.0)]
    [InlineData(200, 100, double.NaN)]
    public void A_Camera_Without_A_Usable_Picture_Or_Field_Of_View_Is_Refused(int width, int height, double hFovDeg)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PinholeCamera(default, width, height, hFovDeg));

    [Fact]
    public void Every_Record_Is_Marked_With_Where_It_Fell_And_Its_Apparent_Size()
    {
        IReadOnlyList<VehicleTelemetry> records =
            [Vehicle(5, 10, 0, 0), Vehicle(6, 10, 100, 0), Vehicle(7, -10, 0, 0), Vehicle(8, 10, 10, 0)];

        IReadOnlyList<VehicleTelemetry> marked = BoxProjector.Mark(records, Boresight);

        Assert.Equal([5u, 6u, 7u, 8u], marked.Select(record => record.Id));
        Assert.Equal([InFrame.Wholly, InFrame.None, InFrame.BehindCamera, InFrame.Partly],
                     marked.Select(record => record.InFrame));
        Assert.Equal(25, marked[0].ApparentWidthPx);
        Assert.True(marked[1].ApparentWidthPx > 0);
        Assert.Equal(0, marked[2].ApparentWidthPx);
        // The projection says nothing of occlusion: that is the depth sampling's to say.
        Assert.All(marked, record => Assert.True(double.IsNaN(record.Occlusion)));
        Assert.All(marked, record => Assert.Null(record.OcclusionUnmeasured));
        // The records as they came carry no place: nothing has projected them.
        Assert.All(records, record => Assert.Null(record.InFrame));
    }

    [Fact]
    public void A_Record_That_Was_Never_Projected_Cannot_Be_Given_A_Reason()
        => Assert.Throws<InvalidOperationException>(() => UnmeasuredOcclusion.FromGeometry(Vehicle(5, 10, 0, 0)));

    [Fact]
    public void The_Reason_Nearest_The_Vehicle_Is_Written_Where_Several_Hold()
    {
        // Four vehicles with no depth camera: the one in the picture says so, the one outside says it is
        // outside, the one behind the camera says that, and the one beyond the draw distance says that
        // -- the picture's edges and the lens first, the draw distance next, the depth camera last.
        IReadOnlyList<VehicleTelemetry> marked = BoxProjector.Mark(
            [Vehicle(5, 10, 0, 0), Vehicle(6, 10, 100, 0), Vehicle(7, -10, 0, 0),
             Vehicle(8, 10, 0, 0) with { DrawDistance = DrawDistanceReach.Beyond, CameraRangeMetres = 10.0 }],
            Boresight);

        IReadOnlyList<VehicleTelemetry> reasoned = UnmeasuredOcclusion.Mark(marked, OcclusionUnmeasured.NoDepthCamera);

        Assert.Equal([OcclusionUnmeasured.NoDepthCamera, OcclusionUnmeasured.OutsideFrame,
                      OcclusionUnmeasured.BehindCamera, OcclusionUnmeasured.BeyondDrawDistance],
                     reasoned.Select(record => record.OcclusionUnmeasured));
        Assert.All(reasoned, record => Assert.True(double.IsNaN(record.Occlusion)));
        // The place and the apparent size are untouched by the reason.
        Assert.Equal(marked.Select(record => record.InFrame), reasoned.Select(record => record.InFrame));
        Assert.Equal(marked.Select(record => record.ApparentWidthPx), reasoned.Select(record => record.ApparentWidthPx));
    }

    [Fact]
    public void A_Vehicle_Partly_Beyond_The_Draw_Distance_Gives_No_Reason_Of_Its_Own()
    {
        VehicleTelemetry partly = BoxProjector.Mark(
            [Vehicle(5, 10, 0, 0) with { DrawDistance = DrawDistanceReach.Partly, CameraRangeMetres = 10.0 }],
            Boresight)[0];

        Assert.Null(UnmeasuredOcclusion.FromGeometry(partly));
    }

    [Theory]
    [InlineData(OcclusionUnmeasured.BehindCamera, "behind_camera")]
    [InlineData(OcclusionUnmeasured.OutsideFrame, "outside_frame")]
    [InlineData(OcclusionUnmeasured.BeyondDrawDistance, "beyond_draw_distance")]
    [InlineData(OcclusionUnmeasured.NoDepthCamera, "no_depth_camera")]
    [InlineData(OcclusionUnmeasured.NoDepthCapture, "no_depth_capture")]
    [InlineData(OcclusionUnmeasured.DepthOutOfStep, "depth_out_of_step")]
    [InlineData(OcclusionUnmeasured.DepthPoseMismatch, "depth_pose_mismatch")]
    [InlineData(OcclusionUnmeasured.BeyondDepthRange, "beyond_depth_range")]
    [InlineData(OcclusionUnmeasured.NoSample, "no_sample")]
    public void Each_Reason_Has_Its_Sidecar_Word(OcclusionUnmeasured reason, string word)
        => Assert.Equal(word, UnmeasuredOcclusion.SidecarValue(reason));

    [Fact]
    public void A_Reason_Outside_The_Nine_Is_Refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => UnmeasuredOcclusion.SidecarValue((OcclusionUnmeasured)42));

    [Fact]
    public void The_Sidecar_Says_Where_Every_Vehicle_Fell_And_Why_Its_Occlusion_Is_Absent()
    {
        IReadOnlyList<VehicleTelemetry> marked = UnmeasuredOcclusion.Mark(
            BoxProjector.Mark([Vehicle(5, 10, 0, 0), Vehicle(6, 10, 100, 0), Vehicle(7, -10, 0, 0), Vehicle(8, 10, 10, 0)],
                              Boresight),
            OcclusionUnmeasured.NoDepthCamera);

        Dictionary<string, XElement> extras = Sidecar(marked);

        Assert.Equal("wholly", (string?)extras["CARLA-TRUTH-5"].Attribute("in_frame"));
        Assert.Equal("25", (string?)extras["CARLA-TRUTH-5"].Attribute("apparent_width_px"));
        Assert.Equal("19", (string?)extras["CARLA-TRUTH-5"].Attribute("apparent_height_px"));
        Assert.Equal("no_depth_camera", (string?)extras["CARLA-TRUTH-5"].Attribute("occlusion_unmeasured"));

        Assert.Equal("none", (string?)extras["CARLA-TRUTH-6"].Attribute("in_frame"));
        Assert.NotNull(extras["CARLA-TRUTH-6"].Attribute("apparent_width_px"));
        Assert.Equal("outside_frame", (string?)extras["CARLA-TRUTH-6"].Attribute("occlusion_unmeasured"));

        // No footprint behind the camera, so no apparent size either.
        Assert.Equal("behind_camera", (string?)extras["CARLA-TRUTH-7"].Attribute("in_frame"));
        Assert.Null(extras["CARLA-TRUTH-7"].Attribute("apparent_width_px"));
        Assert.Null(extras["CARLA-TRUTH-7"].Attribute("apparent_height_px"));
        Assert.Equal("behind_camera", (string?)extras["CARLA-TRUTH-7"].Attribute("occlusion_unmeasured"));

        Assert.Equal("partly", (string?)extras["CARLA-TRUTH-8"].Attribute("in_frame"));

        // Nothing measured, so none of the three measurement attributes anywhere.
        Assert.All(extras.Values, carla =>
        {
            Assert.Null(carla.Attribute("occlusion"));
            Assert.Null(carla.Attribute("occlusion_level"));
            Assert.Null(carla.Attribute("occlusion_samples"));
        });
    }

    [Fact]
    public void A_Measured_Vehicle_Carries_The_Five_Fields_And_No_Reason()
    {
        VehicleTelemetry measured = BoxProjector.Mark([Vehicle(5, 10, 0, 0)], Boresight)[0] with
        {
            Occlusion = 0.25, OcclusionLevel = 1, OcclusionSamples = 64,
        };

        XElement carla = Sidecar([measured])["CARLA-TRUTH-5"];

        Assert.Equal("wholly", (string?)carla.Attribute("in_frame"));
        Assert.Equal("0.250", (string?)carla.Attribute("occlusion"));
        Assert.Equal("1", (string?)carla.Attribute("occlusion_level"));
        Assert.Equal("64", (string?)carla.Attribute("occlusion_samples"));
        Assert.Equal("25", (string?)carla.Attribute("apparent_width_px"));
        Assert.Equal("19", (string?)carla.Attribute("apparent_height_px"));
        Assert.Null(carla.Attribute("occlusion_unmeasured"));
    }

    [Fact]
    public void A_Reason_Is_Never_Written_Beside_A_Measurement()
    {
        // A record that somehow carries both: the measurement wins and the reason is left out, so the
        // two can never contradict each other in a file.
        VehicleTelemetry both = BoxProjector.Mark([Vehicle(5, 10, 0, 0)], Boresight)[0] with
        {
            Occlusion = 0.0, OcclusionLevel = 0, OcclusionSamples = 64,
            OcclusionUnmeasured = OcclusionUnmeasured.NoSample,
        };

        XElement carla = Sidecar([both])["CARLA-TRUTH-5"];

        Assert.Equal("0.000", (string?)carla.Attribute("occlusion"));
        Assert.Null(carla.Attribute("occlusion_unmeasured"));
    }

    [Fact]
    public void A_Record_No_Camera_Projected_Carries_None_Of_It()
    {
        // The live pull's shape: no camera, so no place, no apparent size and no reason.
        XElement carla = Sidecar([Vehicle(5, 10, 0, 0)])["CARLA-TRUTH-5"];

        Assert.Null(carla.Attribute("in_frame"));
        Assert.Null(carla.Attribute("apparent_width_px"));
        Assert.Null(carla.Attribute("apparent_height_px"));
        Assert.Null(carla.Attribute("occlusion_unmeasured"));
    }

    private static Dictionary<string, XElement> Sidecar(IReadOnlyList<VehicleTelemetry> records)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc), records,
                                  capture: new CaptureIdentity(100, 5.0, "run-1"));
            return XDocument.Load(path).Root!.Elements("event")
                .ToDictionary(e => (string)e.Attribute("uid")!, e => e.Element("detail")!.Element("_carla")!);
        }
        finally { File.Delete(path); }
    }
}
