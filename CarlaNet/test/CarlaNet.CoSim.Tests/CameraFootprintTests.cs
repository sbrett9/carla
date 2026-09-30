using CarlaNet.Types.Geom;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A camera's ground footprint, against cases whose answer is a line of trigonometry: straight down,
/// oblique, a view that takes in the horizon, one of the sky, and one over a drop in the ground.
/// </summary>
/// <remarks>
/// Every expected edge is worked from the camera's height and the angle of the ray that makes it, not
/// from the code under test, and the CARLA frame is the engine's: x east, y south, yaw 0 facing east,
/// and a camera looking straight down with yaw 0 has the picture's right to the south and its top to
/// the east.
/// </remarks>
public sealed class CameraFootprintTests
{
    private const double Far = 1e7;

    [Fact]
    public void StraightDownTheFootprintIsThePictureWithItsWidthAlongTheCameraSRight()
    {
        // 90 degrees across 1600 px from 100 m: 100 m either side along the picture's right, which is
        // south, and 56.25 m either side along its top, which is east.
        var optics = new CameraOptics(1600, 900, 90.0);
        GroundPolygon footprint = CameraFootprint.Project(Pose(0, 0, 100, -90, 0), optics, 0.0, Far);

        Assert.Equal(200.0 * 112.5, footprint.Area, 1.0);
        Assert.Equal(0.0, footprint.DistanceTo(50.0, 95.0));
        Assert.Equal(0.0, footprint.DistanceTo(-55.0, -99.0));
        Assert.Equal(38.75, footprint.DistanceTo(95.0, 50.0), 3);
        Assert.Equal(10.0, footprint.DistanceTo(0.0, 110.0), 3);
    }

    [Fact]
    public void AnObliqueViewIsTheTrapezoidItsEdgeRaysMeetTheGroundAt()
    {
        // 45 degrees down from 100 m with 60 degrees each way: the bottom edge meets the ground 75 degrees
        // down, at 100 / tan 75 = 26.795 m, and the top edge 15 degrees down, at 100 / tan 15 = 373.205 m.
        var optics = new CameraOptics(1000, 1000, 60.0);
        GroundPolygon footprint = CameraFootprint.Project(Pose(0, 0, 100, -45, 0), optics, 0.0, Far);
        double near = 100.0 / Math.Tan(75.0 * Math.PI / 180.0);
        double far = 100.0 / Math.Tan(15.0 * Math.PI / 180.0);

        Assert.Equal(1.0, footprint.DistanceTo(near - 1.0, 0.0), 3);
        Assert.Equal(0.0, footprint.DistanceTo(near + 1.0, 0.0));
        Assert.Equal(0.0, footprint.DistanceTo(far - 1.0, 0.0));
        Assert.Equal(1.0, footprint.DistanceTo(far + 1.0, 0.0), 3);

        // Where the axis meets the ground, 141.42 m down the axis, the picture is tan 30 of that either
        // side: 81.65 m.
        double halfWidth = Math.Sqrt(2.0) * 100.0 * Math.Tan(30.0 * Math.PI / 180.0);
        Assert.Equal(0.0, footprint.DistanceTo(100.0, halfWidth - 0.5));
        Assert.InRange(footprint.DistanceTo(100.0, halfWidth + 1.0), 0.5, 1.0);

        // And turned to face south, it lies to the south.
        GroundPolygon south = CameraFootprint.Project(Pose(0, 0, 100, -45, 90), optics, 0.0, Far);
        Assert.Equal(0.0, south.DistanceTo(0.0, 100.0));
        Assert.True(south.DistanceTo(100.0, 0.0) > 40.0);
    }

    [Fact]
    public void AViewThatTakesInTheHorizonIsBoundedByTheRangeCapAndByNothingElse()
    {
        // Level at 100 m: the lower half of the picture sees ground out to the horizon.
        var optics = new CameraOptics(1280, 720, 90.0);
        Transform level = Pose(0, 0, 100, 0, 0);

        // Uncapped, the footprint runs as far as the disc it is cut from.
        Assert.Equal(0.0, CameraFootprint.Project(level, optics, 0.0, Far).DistanceTo(1e6, 0.0));

        double cap = optics.RangeAtWhichABodyCovers(10.0, 2.0);
        double groundRadius = Math.Sqrt((cap * cap) - (100.0 * 100.0));
        GroundPolygon capped = CameraFootprint.OnTheGround(level, optics, cap, (_, _) => 0.0, out _);

        // The cap's circle is drawn as a polygon whose edges touch it midway, so along the middle of an
        // edge the cap is the circle itself.
        double midEdge = Math.PI / 64.0;
        Assert.Equal(0.0, capped.DistanceTo((groundRadius - 10.0) * Math.Cos(midEdge),
                                            (groundRadius - 10.0) * Math.Sin(midEdge)));
        Assert.Equal(10.0, capped.DistanceTo((groundRadius + 10.0) * Math.Cos(midEdge),
                                             (groundRadius + 10.0) * Math.Sin(midEdge)), 3);
        (double minX, double minY, double maxX, double maxY) = capped.Bounds;
        Assert.True(maxX <= groundRadius * 1.002 && Math.Max(-minY, maxY) <= groundRadius * 1.002 && minX >= 0.0,
                    $"the capped footprint spans ({minX}, {minY}) to ({maxX}, {maxY})");
    }

    [Fact]
    public void TheRangeCapIsTheReferenceBodyBroadsideAtTheCornersScale()
    {
        // Worked independently of the optics' own formula: the pixels one radian covers at the corner
        // of the picture, taken as the slope of f tan θ there.
        var optics = new CameraOptics(1920, 1080, 60.0);
        double tanX = Math.Tan(30.0 * Math.PI / 180.0);
        double focal = 960.0 / tanX;
        double corner = Math.Atan(Math.Sqrt((tanX * tanX) + Math.Pow(tanX * 1080.0 / 1920.0, 2)));
        const double step = 1e-7;
        double pixelsPerRadian = focal * (Math.Tan(corner + step) - Math.Tan(corner - step)) / (2.0 * step);

        Assert.Equal(10.1743 * pixelsPerRadian / 2.0, optics.RangeAtWhichABodyCovers(10.1743, 2.0), 2);
        Assert.Equal(12170.0, optics.RangeAtWhichABodyCovers(10.1743, 2.0), 5.0);
        Assert.Equal(focal, optics.FocalLengthPixels, 9);
    }

    [Fact]
    public void AViewOfTheSkyOrFromUnderTheGroundHasNoFootprint()
    {
        var optics = new CameraOptics(1000, 1000, 60.0);

        // Pitched 40 degrees up with 30 degrees above and below the axis: every ray climbs.
        Assert.True(CameraFootprint.Project(Pose(0, 0, 100, 40, 0), optics, 0.0, Far).IsEmpty);
        Assert.True(CameraFootprint.Project(Pose(0, 0, -5, -90, 0), optics, 0.0, Far).IsEmpty);
        Assert.Equal(double.PositiveInfinity, GroundPolygon.Empty.DistanceTo(0.0, 0.0));
    }

    [Fact]
    public void GroundThatFallsAwayUnderAnObliqueViewIsCoveredWhereOnePlaneWouldStopShort()
    {
        // Flat to x = 200 and 50 m lower beyond it. Looking 40 degrees down from 100 m with 30 degrees
        // each way, the top edge is 10 degrees down: a plane at the height the axis meets reaches
        // 100 / tan 10 = 567 m, and the ground beyond the drop is reached at 150 / tan 10 = 851 m.
        var optics = new CameraOptics(1000, 1000, 60.0);
        Transform pose = Pose(0, 0, 100, -40, 0);
        double? Ground(double x, double y) => x < 200.0 ? 0.0 : -50.0;
        double onePlane = 100.0 / Math.Tan(10.0 * Math.PI / 180.0);
        double beyondTheDrop = 150.0 / Math.Tan(10.0 * Math.PI / 180.0);

        GroundPolygon single = CameraFootprint.Project(pose, optics, 0.0, Far);
        GroundPolygon bracketed = CameraFootprint.OnTheGround(pose, optics, Far, Ground,
                                                              out (double Low, double High) bracket);

        Assert.Equal((-50.0, 0.0), bracket);
        Assert.True(single.DistanceTo(onePlane + 50.0, 0.0) > 40.0);
        Assert.Equal(0.0, bracketed.DistanceTo(beyondTheDrop - 5.0, 0.0));
    }

    [Fact]
    public void TheHullOfAFootprintAndItsMovedSelfIsTheGroundItSweeps()
    {
        GroundPolygon here = GroundPolygon.AroundDisc(0.0, 0.0, 10.0, 4);
        GroundPolygon there = GroundPolygon.AroundDisc(100.0, 0.0, 10.0, 4);
        GroundPolygon swept = GroundPolygon.Hull(here, there);

        Assert.Equal(0.0, swept.DistanceTo(50.0, 0.0));
        Assert.True(here.DistanceTo(50.0, 0.0) > 30.0 && there.DistanceTo(50.0, 0.0) > 30.0);
        Assert.Equal(swept.Area, GroundPolygon.Hull(there, here).Area, 9);
    }

    private static Transform Pose(double x, double y, double z, double pitch, double yaw, double roll = 0.0) =>
        new(new Location((float)x, (float)y, (float)z), new Rotation((float)pitch, (float)yaw, (float)roll));
}
