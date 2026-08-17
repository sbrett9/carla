// Exercises the depth-based occlusion measurement against synthetic depth captures: a vehicle with
// nothing in front of it, one behind a wall, and the sweep from one to the other. See
// Docs/CAT_Research/Findings/17_Photoreal_Occlusion_Metric.md.
using CarlaNet.Recording;
using CarlaNet.Types.Geom;

namespace CarlaNet.Tests.Recording;

public class OcclusionEstimatorTests
{
    private const int Size = 200;
    private const double HFovDeg = 90.0;      // focal length = Size / 2 = 100 px
    private const double SkyRange = 900.0;

    // Looking along +X from the origin, level and upright.
    private static readonly Transform Boresight =
        new(new Location(0f, 0f, 0f), new Rotation(0f, 0f, 0f));

    private static DepthFrame Capture(Transform pose, Func<int, int, double> rangeAt)
    {
        var bgra = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                DepthFrame.WriteRange(bgra, y * Size + x, rangeAt(x, y));
        return new DepthFrame(1, 0.0, pose, Size, Size, HFovDeg, bgra);
    }

    // A 4 m x 2 m x 1.5 m vehicle — a saloon's bounding box.
    private static VehicleBox Vehicle(uint id, double x, double y, double z, float yawDeg = 0f) =>
        new(id,
            new Transform(new Location((float)x, (float)y, (float)z), new Rotation(0f, yawDeg, 0f)),
            new BoundingBox(new Location(0f, 0f, 0f), new Vector3D(2f, 1f, 0.75f), new Rotation()));

    private static VehicleOcclusion Measure(DepthFrame depth, VehicleBox vehicle,
                                            OcclusionOptions? options = null)
    {
        var measured = OcclusionEstimator.Estimate(depth, [vehicle], options ?? OcclusionOptions.Default);
        return Assert.Contains(vehicle.ActorId, (IDictionary<uint, VehicleOcclusion>)measured);
    }

    [Fact]
    public void Clear_Line_Of_Sight_Is_Not_Occluded()
    {
        var occlusion = Measure(Capture(Boresight, (_, _) => SkyRange), Vehicle(1, 10, 0, 0));
        Assert.Equal(0.0, occlusion.Fraction, 6);
        Assert.Equal(0, occlusion.Level);
        Assert.True(occlusion.Samples > 20, $"expected a well-sampled silhouette, got {occlusion.Samples}");
    }

    [Fact]
    public void A_Wall_In_Front_Hides_The_Whole_Vehicle()
    {
        var occlusion = Measure(Capture(Boresight, (_, _) => 5.0), Vehicle(1, 10, 0, 0));
        Assert.Equal(1.0, occlusion.Fraction, 6);
        Assert.Equal(4, occlusion.Level);
    }

    [Fact]
    public void Surfaces_Beyond_The_Vehicle_Do_Not_Occlude_It()
    {
        // The ground behind and below a vehicle is farther away, so it is not in the way.
        var occlusion = Measure(Capture(Boresight, (_, _) => 20.0), Vehicle(1, 10, 0, 0));
        Assert.Equal(0.0, occlusion.Fraction, 6);
    }

    [Fact]
    public void A_Wall_Across_Half_The_View_Hides_About_Half_The_Vehicle()
    {
        // The vehicle is centred on the optical axis, so a wall covering the left half of the frame
        // covers half its silhouette.
        var depth = Capture(Boresight, (x, _) => x < Size / 2 ? 5.0 : SkyRange);
        var occlusion = Measure(depth, Vehicle(1, 10, 0, 0));
        Assert.InRange(occlusion.Fraction, 0.4, 0.6);
        Assert.Equal(2, occlusion.Level);
    }

    [Fact]
    public void Occlusion_Rises_Monotonically_As_A_Wall_Sweeps_Across()
    {
        // The verification the design doc asks for, in miniature: as an obstruction slides across the
        // line of sight the reported fraction must climb from wholly visible to wholly hidden without
        // ever going backwards.
        var vehicle = Vehicle(1, 10, 0, 0);
        double previous = -1.0;
        var fractions = new List<double>();
        for (int edge = 80; edge <= 120; edge += 4)
        {
            int wallEdge = edge;
            var depth = Capture(Boresight, (x, _) => x < wallEdge ? 5.0 : SkyRange);
            double fraction = Measure(depth, vehicle).Fraction;
            Assert.True(fraction >= previous,
                        $"fraction fell from {previous} to {fraction} with the wall edge at {wallEdge}");
            previous = fraction;
            fractions.Add(fraction);
        }
        Assert.Equal(0.0, fractions[0], 6);
        Assert.Equal(1.0, fractions[^1], 6);
    }

    [Fact]
    public void An_Obstruction_Inside_The_Margin_Is_Not_Counted()
    {
        // The vehicle's leading face is at 8 m. With a 1 m margin, something at 7.5 m is treated as
        // the vehicle's own bodywork standing proud of its box, not as an occluder.
        var vehicle = Vehicle(1, 10, 0, 0);
        Assert.Equal(0.0, Measure(Capture(Boresight, (_, _) => 7.5), vehicle).Fraction, 6);
        Assert.Equal(1.0, Measure(Capture(Boresight, (_, _) => 6.5), vehicle).Fraction, 6);
    }

    [Fact]
    public void A_Vehicle_Outside_The_Frame_Is_Not_Reported()
    {
        var depth = Capture(Boresight, (_, _) => SkyRange);
        var measured = OcclusionEstimator.Estimate(depth, [Vehicle(1, 10, 100, 0)], OcclusionOptions.Default);
        Assert.Empty(measured);
    }

    [Fact]
    public void A_Vehicle_Behind_The_Camera_Is_Not_Reported()
    {
        var depth = Capture(Boresight, (_, _) => SkyRange);
        var measured = OcclusionEstimator.Estimate(depth, [Vehicle(1, -10, 0, 0)], OcclusionOptions.Default);
        Assert.Empty(measured);
    }

    [Fact]
    public void A_Vehicle_Past_The_Depth_Far_Plane_Is_Not_Reported()
    {
        // Out there every reading saturates, so nothing can be told apart; reporting the vehicle as
        // hidden would quietly drop a legitimate distant target from the training set.
        var depth = Capture(Boresight, (_, _) => SkyRange);
        var vehicle = Vehicle(1, DepthFrame.FarPlaneMetres + 50.0, 0, 0);
        Assert.Empty(OcclusionEstimator.Estimate(depth, [vehicle], OcclusionOptions.Default));
    }

    [Fact]
    public void Works_From_An_Airborne_Oblique_View()
    {
        // 20 m up, looking down at 45 degrees along a 45 degree heading — the geometry an airborne
        // electro-optical camera actually collects from, with pitch, yaw and a turned vehicle all in
        // play. The vehicle sits on the boresight at a range of 20*sqrt(2) m.
        var camera = new Transform(new Location(0f, 0f, 20f), new Rotation(-45f, 45f, 0f));
        var vehicle = Vehicle(1, 10.0, 10.0, 0.0, yawDeg: 30f);

        Assert.Equal(0.0, Measure(Capture(camera, (_, _) => SkyRange), vehicle).Fraction, 6);
        Assert.Equal(1.0, Measure(Capture(camera, (_, _) => 10.0), vehicle).Fraction, 6);
    }

    [Fact]
    public void Sample_Count_Is_Bounded_By_The_Requested_Density()
    {
        // A vehicle filling the frame must not cost a sample per pixel.
        var options = OcclusionOptions.Default with { SamplesAcross = 16 };
        var occlusion = Measure(Capture(Boresight, (_, _) => SkyRange), Vehicle(1, 4, 0, 0), options);
        Assert.InRange(occlusion.Samples, 1, 16 * 16);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.01, 1)]
    [InlineData(0.29, 1)]
    [InlineData(0.30, 2)]
    [InlineData(0.59, 2)]
    [InlineData(0.60, 3)]
    [InlineData(0.89, 3)]
    [InlineData(0.90, 4)]
    [InlineData(1.0, 4)]
    public void Levels_Follow_The_Published_Occlusion_Bands(double fraction, int expected)
        => Assert.Equal(expected, OcclusionEstimator.LevelFor(fraction));

    [Fact]
    public void Range_Survives_The_Colour_Encoding()
    {
        var bgra = new byte[4 * 4];
        double[] ranges = [0.0, 7.5, 123.456, DepthFrame.FarPlaneMetres];
        for (int i = 0; i < ranges.Length; i++) DepthFrame.WriteRange(bgra, i, ranges[i]);
        var depth = new DepthFrame(1, 0.0, Boresight, 4, 1, HFovDeg, bgra);
        for (int i = 0; i < ranges.Length; i++)
            Assert.Equal(ranges[i], depth.RangeAt(i, 0), 3);
    }
}
