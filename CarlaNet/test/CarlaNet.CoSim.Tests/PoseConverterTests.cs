using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The conversion from a SUMO state to the CARLA pose that would be applied, and the refusals that
/// stop a pose being produced from a number nobody measured.
/// </summary>
public sealed class PoseConverterTests
{
    // The Mitsubishi Fuso as the shipped catalogue measures it: the blueprint whose box centre is
    // furthest from its actor origin, and therefore the one a bumper shift is most visible on.
    private static readonly VehicleExtent Fuso =
        new("vehicle.fuso.mitsubishi", 10.1743, 3.9277, 4.241, (-0.4935, -0.0925, 2.1318));

    [Fact]
    public void TheYawIsSumoAngleLessNinety()
    {
        Assert.Equal(-90.0, PoseConverter.YawFromSumoAngle(0.0), 9);     // north
        Assert.Equal(0.0, PoseConverter.YawFromSumoAngle(90.0), 9);      // east
        Assert.Equal(90.0, PoseConverter.YawFromSumoAngle(180.0), 9);    // south
        Assert.Equal(180.0, PoseConverter.YawFromSumoAngle(270.0), 9);   // west
        Assert.Equal(-90.0, PoseConverter.YawFromSumoAngle(360.0), 9);
    }

    [Fact]
    public void TheYawPutsTheForwardVectorWhereTheTruthPathWouldReadTheSumoCourseBack()
    {
        // The shipped truth path computes course = atan2(cos yaw, -sin yaw). Requiring that to
        // reproduce SUMO's own angle is the whole derivation, so it is the whole test.
        foreach (double angle in new[] { 0.0, 37.0, 90.0, 143.5, 180.0, 271.25, 359.0 })
        {
            double yaw = PoseConverter.YawFromSumoAngle(angle) * (Math.PI / 180.0);
            double course = Math.Atan2(Math.Cos(yaw), -Math.Sin(yaw)) * (180.0 / Math.PI);
            Assert.Equal(angle, (course + 360.0) % 360.0, 9);
        }
    }

    [Fact]
    public void TheBumperShiftIsTheMeasuredDistanceAndNotHalfTheDeclaredLength()
    {
        // 10.1743 / 2 less the 0.4935 m the box centre sits behind the origin.
        Assert.Equal(4.59365, Fuso.BumperToOriginMetres, 6);
        Assert.Equal(-0.0925, Fuso.LateralOffsetMetres, 6);
        Assert.NotEqual(Fuso.LengthMetres / 2.0, Fuso.BumperToOriginMetres, 3);
    }

    [Fact]
    public void ThePoseIsShiftedBackAlongTheVehicleSOwnHeading()
    {
        GroundSurface ground = FlatSurface();
        var converter = new PoseConverter(ground);

        // Heading east: the origin sits the bumper distance west of the bumper, and the lateral
        // offset moves it to the north side, which in the CARLA frame is negative y.
        VehiclePose pose = converter.Convert("v", Fuso, 100.0, 0.0, 90.0, 0.0)!.Value;
        Assert.Equal(100.0 - Fuso.BumperToOriginMetres, pose.X, 6);
        Assert.Equal(-Fuso.LateralOffsetMetres, pose.Y, 6);
        Assert.Equal(0.0, pose.YawDegrees, 9);

        // Heading north: the shift is along negative CARLA y, because CARLA's y is negated north.
        VehiclePose north = converter.Convert("v", Fuso, 0.0, 100.0, 0.0, 0.0)!.Value;
        Assert.Equal(-100.0 + Fuso.BumperToOriginMetres, north.Y, 6);
        Assert.Equal(-90.0, north.YawDegrees, 9);
    }

    [Fact]
    public void ARenderedFrontBumperLandsExactlyOnSumoSReferencePoint()
    {
        // The round trip the whole conversion exists for: take the pose back to the point SUMO
        // reported, through the same rotation, and it has to return the input.
        GroundSurface ground = FlatSurface();
        var converter = new PoseConverter(ground);

        foreach (double angle in new[] { 0.0, 33.0, 90.0, 174.0, 250.0, 300.5 })
        {
            VehiclePose pose = converter.Convert("v", Fuso, 60.0, -75.0, angle, 12.0)!.Value;
            double radians = pose.YawDegrees * (Math.PI / 180.0);
            double forwardX = Math.Cos(radians);
            double forwardY = Math.Sin(radians);
            double bumperX = pose.X + (Fuso.BumperToOriginMetres * forwardX)
                             - (Fuso.LateralOffsetMetres * forwardY);
            double bumperY = pose.Y + (Fuso.BumperToOriginMetres * forwardY)
                             + (Fuso.LateralOffsetMetres * forwardX);
            Assert.Equal(60.0, bumperX, 9);
            Assert.Equal(-75.0, -bumperY, 9);
        }
    }

    [Fact]
    public void TheBodySCentreSitsHalfALengthBehindTheBumperWithTheNorthingNegatedAndTheYawTheSumoAngleLessNinety()
    {
        // The pose convention checked against numbers worked out by hand, none of them from the
        // converter: the figures below are the arithmetic written out, and the test fails if the
        // converter's result departs from them, so a wrong reference point, a wrong frame or a wrong
        // yaw cannot pass by agreeing with itself (06 §4.3). The bumper round-trip test above cannot
        // catch any of the three, because it undoes the shift with the converter's own quantities.
        //
        // The body: five metres long, its box centre half a metre behind the actor origin and 0.8 m
        // above it, no sideways offset. SUMO puts its front bumper at (40 E, 60 N) heading 30 degrees
        // clockwise from north.
        var body = new VehicleExtent("vehicle.test.sedan", 5.0, 2.0, 1.6, (-0.5, 0.0, 0.8));
        const double BumperEast = 40.0;
        const double BumperNorth = 60.0;
        const double SumoAngle = 30.0;

        // By hand. A heading of 30 degrees clockwise from north is the unit vector (sin 30, cos 30)
        // = (0.5, 0.8660254037844386) in SUMO's east-north frame. The body's centre is half the
        // length, 2.5 m, back along it from the bumper:
        //   east  40 - 2.5 * 0.5                = 38.75
        //   north 60 - 2.5 * 0.8660254037844386 = 57.8349364905389
        // CARLA's y is negated north, so the centre is (38.75, -57.8349364905389). The actor origin is
        // 0.5 m ahead of the centre along the heading, 2.0 m behind the bumper:
        //   east  40 - 2.0 * 0.5                = 39.0
        //   north 60 - 2.0 * 0.8660254037844386 = 58.267949192431125   ->  y = -58.267949192431125
        // and the yaw is the SUMO angle less ninety, -60 degrees, whose cosine and sine are 0.5 and
        // -0.8660254037844386: the same heading, east and negated north, as CARLA's forward vector.
        const double CentreX = 38.75;
        const double CentreY = -57.8349364905389;
        const double OriginX = 39.0;
        const double OriginY = -58.267949192431125;
        const double Yaw = -60.0;
        const double CosYaw = 0.5;
        const double SinYaw = -0.8660254037844386;

        var converter = new PoseConverter(FlatSurface());
        VehiclePose pose = converter.Convert("v", body, BumperEast, BumperNorth, SumoAngle, 0.0)!.Value;

        Assert.Equal(Yaw, pose.YawDegrees, 9);
        Assert.Equal(OriginX, pose.X, 9);
        Assert.Equal(OriginY, pose.Y, 9);

        // The body's centre is the origin carried by the box centre's offset, turned by the hand-worked
        // yaw: half a length behind the bumper, along the heading.
        double centreX = pose.X + (body.BoxCentreMetres.X * CosYaw) - (body.BoxCentreMetres.Y * SinYaw);
        double centreY = pose.Y + (body.BoxCentreMetres.X * SinYaw) + (body.BoxCentreMetres.Y * CosYaw);
        Assert.Equal(CentreX, centreX, 9);
        Assert.Equal(CentreY, centreY, 9);

        // From the centre, the bumper is half a length away and all of it is ahead along the heading.
        double toBumperX = BumperEast - centreX;
        double toBumperY = -BumperNorth - centreY;
        Assert.Equal(body.LengthMetres / 2.0, Math.Sqrt((toBumperX * toBumperX) + (toBumperY * toBumperY)), 9);
        Assert.Equal(body.LengthMetres / 2.0, (toBumperX * CosYaw) + (toBumperY * SinYaw), 9);

        // Each wrong convention puts the centre somewhere these numbers are not, by the residual 06 §4.3
        // names for it, so the assertions above can tell them apart. The bumper taken as the centre: half
        // a length, 2.5 m. The northing not negated: the centre mirrored to y = +57.8349364905389, which
        // is 115.6698729810778 m away. A yaw of the SUMO angle itself: the centre swung a quarter turn
        // about the bumper, 2.5 * sqrt 2 = 3.5355339059327378 m away.
        Assert.Equal(2.5, Math.Sqrt(Math.Pow(BumperEast - CentreX, 2) + Math.Pow(-BumperNorth - CentreY, 2)), 9);
        const double MirroredCentreY = 57.8349364905389;
        Assert.Equal(115.6698729810778, Math.Abs(MirroredCentreY - centreY), 9);
        double wrongRadians = SumoAngle * (Math.PI / 180.0);
        double wrongCentreX = BumperEast - (2.5 * Math.Cos(wrongRadians));
        double wrongCentreY = -BumperNorth - (2.5 * Math.Sin(wrongRadians));
        Assert.Equal(3.5355339059327378,
                     Math.Sqrt(Math.Pow(wrongCentreX - centreX, 2) + Math.Pow(wrongCentreY - centreY, 2)), 9);
    }

    [Fact]
    public void AVehicleOutsideTheWorldSGroundSurfaceGetsNoPose()
    {
        var converter = new PoseConverter(FlatSurface());
        Assert.Null(converter.Convert("v", Fuso, 100_000.0, 0.0, 90.0, 0.0));
    }

    [Fact]
    public void ASlopeTiltsTheBodyAlongAndAcrossItsHeading()
    {
        // A surface climbing one metre per ten eastwards: a vehicle heading east is nose-up by the
        // slope angle and level across, and one heading north is level along and leaning away from
        // the uphill side.
        //
        // The tolerance is a hundredth of a degree rather than an exact match because the package
        // stores absolute ellipsoidal heights as float32. Around a thousand metres that is about
        // 6e-5 m of quantisation per cell, which over the four-metre central difference is a
        // thousandth of a degree of slope -- a property of the shipped grid format, not of this
        // arithmetic, and the same on any real world.
        GroundSurface ground = RampSurface(gradientEastwards: 0.1);
        var converter = new PoseConverter(ground);
        double expected = Math.Atan(0.1) * (180.0 / Math.PI);

        // Nose up, because the ground ahead is higher. CARLA's forward vector carries sin(pitch) as
        // its height, so a nose-up body has a positive pitch.
        VehiclePose east = converter.Convert("v", Fuso, 0.0, 0.0, 90.0, 0.0)!.Value;
        Assert.True(Math.Abs(east.PitchDegrees - expected) < 0.01, $"pitch {east.PitchDegrees}");
        Assert.Equal(0.0, east.RollDegrees, 6);

        // Heading north the slope is entirely across the body, and the uphill side is the right
        // one. CARLA's right vector carries -sin(roll) as its height, so a body whose right side is
        // higher has a negative roll.
        VehiclePose north = converter.Convert("v", Fuso, 0.0, 0.0, 0.0, 0.0)!.Value;
        Assert.Equal(0.0, north.PitchDegrees, 6);
        Assert.True(Math.Abs(north.RollDegrees + expected) < 0.01, $"roll {north.RollDegrees}");
    }

    [Fact]
    public void TheTiltedBodySAxesLieInTheSurfaceRatherThanCuttingThroughIt()
    {
        // The test that settles the two signs, and it is written against CARLA's own geometry
        // rather than against a chosen convention: ForwardVector and RightVector below are
        // LibCarla/source/carla/geom/Math.cpp lines 117-136, and a carla::geom::Rotation reaches
        // the engine as FRotator{pitch, yaw, roll} with no sign change (Rotation.h:221), so what
        // holds here is what the engine draws.
        //
        // On a plane of height a*x + b*y, a body seated in the surface has both of its horizontal
        // axes lying in that plane, which for a unit axis (u, v, w) is w == a*u + b*v. Anything
        // else is a body that cuts into the ground on one side and lifts off it on the other.
        const double eastward = 0.1;
        const double northward = -0.06;
        GroundSurface ground = SyntheticWorld.Build(
            cell => (cell.X * eastward) + (cell.Y * northward));
        var converter = new PoseConverter(ground);

        foreach (double sumoAngle in new[] { 0.0, 45.0, 90.0, 137.0, 180.0, 270.0, 312.5 })
        {
            VehiclePose pose = converter.Convert("v", Fuso, 0.0, 0.0, sumoAngle, 0.0)!.Value;
            (double fx, double fy, double fz) = ForwardVector(pose);
            (double rx, double ry, double rz) = RightVector(pose);

            Assert.True(Math.Abs(fz - ((eastward * fx) + (northward * fy))) < 1e-4,
                        $"at {sumoAngle} deg the forward axis leaves the surface: {fz} against "
                        + $"{(eastward * fx) + (northward * fy)}");
            Assert.True(Math.Abs(rz - ((eastward * rx) + (northward * ry))) < 1e-4,
                        $"at {sumoAngle} deg the right axis leaves the surface: {rz} against "
                        + $"{(eastward * rx) + (northward * ry)}");
        }
    }

    /// <summary>Port of <c>carla::geom::Math::GetForwardVector</c>.</summary>
    private static (double X, double Y, double Z) ForwardVector(in VehiclePose pose)
    {
        (double sp, double cp) = Math.SinCos(pose.PitchDegrees * (Math.PI / 180.0));
        (double sy, double cy) = Math.SinCos(pose.YawDegrees * (Math.PI / 180.0));
        return (cy * cp, sy * cp, sp);
    }

    /// <summary>Port of <c>carla::geom::Math::GetRightVector</c>.</summary>
    private static (double X, double Y, double Z) RightVector(in VehiclePose pose)
    {
        (double sp, double cp) = Math.SinCos(pose.PitchDegrees * (Math.PI / 180.0));
        (double sy, double cy) = Math.SinCos(pose.YawDegrees * (Math.PI / 180.0));
        (double sr, double cr) = Math.SinCos(pose.RollDegrees * (Math.PI / 180.0));
        return ((cy * sp * sr) - (sy * cr), (sy * sp * sr) + (cy * cr), -cp * sr);
    }

    [Fact]
    public void ASeatHeightTakenFromTheBoxSaysSoAndAMeasuredOneDoesNot()
    {
        GroundSurface ground = FlatSurface();

        VehiclePose approximated = new PoseConverter(ground)
            .Convert("v", Fuso, 0.0, 0.0, 90.0, 0.0)!.Value;
        Assert.True(approximated.SeatHeightWasApproximated);

        var measured = new Dictionary<string, double> { ["vehicle.fuso.mitsubishi"] = 0.25 };
        VehiclePose settled = new PoseConverter(ground, measured)
            .Convert("v", Fuso, 0.0, 0.0, 90.0, 0.0)!.Value;
        Assert.False(settled.SeatHeightWasApproximated);
        Assert.Equal(approximated.Z - Fuso.ApproximateSeatHeightMetres + 0.25, settled.Z, 9);
    }

    [Fact]
    public void TheVelocityPointsWhereTheBodyPoints()
    {
        var converter = new PoseConverter(FlatSurface());
        VehiclePose east = converter.Convert("v", Fuso, 0.0, 0.0, 90.0, 13.0)!.Value;
        Assert.Equal(13.0, east.VelocityX, 9);
        Assert.Equal(0.0, east.VelocityY, 9);

        VehiclePose north = converter.Convert("v", Fuso, 0.0, 0.0, 0.0, 13.0)!.Value;
        Assert.Equal(0.0, north.VelocityX, 9);
        Assert.Equal(-13.0, north.VelocityY, 9);

        // Level ground: nothing climbs.
        Assert.Equal(0.0, east.VelocityZ, 9);
        Assert.Equal(0.0, north.VelocityZ, 9);
    }

    [Fact]
    public void TheVelocityClimbsWithTheGroundTheBodyIsSeatedOn()
    {
        // One in ten eastwards. Heading east at 13 m/s the body climbs 1.3 m every second, heading
        // west it descends as fast, and heading north it neither climbs nor descends. The horizontal
        // speed is SUMO's own in every case, because SUMO's network is flat and its speed is the
        // horizontal speed; a truth record that takes the horizontal speed of this velocity reads
        // SUMO's.
        //
        // The same float32 quantisation of the grid as the tilt test allows for: a thousandth of a
        // degree of slope is about 2e-5 of gradient, a quarter of a millimetre per second here.
        GroundSurface ground = RampSurface(gradientEastwards: 0.1);
        var converter = new PoseConverter(ground);

        VehiclePose east = converter.Convert("v", Fuso, 0.0, 0.0, 90.0, 13.0)!.Value;
        VehiclePose west = converter.Convert("v", Fuso, 0.0, 0.0, 270.0, 13.0)!.Value;
        VehiclePose north = converter.Convert("v", Fuso, 0.0, 0.0, 0.0, 13.0)!.Value;

        Assert.True(Math.Abs(east.VelocityZ - 1.3) < 1e-3, $"east climbs at {east.VelocityZ}");
        Assert.True(Math.Abs(west.VelocityZ + 1.3) < 1e-3, $"west climbs at {west.VelocityZ}");
        Assert.Equal(0.0, north.VelocityZ, 6);
        foreach (VehiclePose pose in new[] { east, west, north })
        {
            Assert.Equal(13.0, Math.Sqrt((pose.VelocityX * pose.VelocityX)
                                         + (pose.VelocityY * pose.VelocityY)), 9);
        }

        // And the velocity is tangent to the body's own forward axis: the climb over the
        // horizontal speed is the tangent of the pitch, whichever way the body faces.
        foreach (double sumoAngle in new[] { 0.0, 37.0, 90.0, 143.5, 200.0, 270.0, 333.0 })
        {
            VehiclePose pose = converter.Convert("v", Fuso, 0.0, 0.0, sumoAngle, 13.0)!.Value;
            double pitchTangent = Math.Tan(pose.PitchDegrees * (Math.PI / 180.0));
            Assert.True(Math.Abs((pose.VelocityZ / 13.0) - pitchTangent) < 1e-9,
                        $"at {sumoAngle} deg the velocity climbs at {pose.VelocityZ / 13.0} and the "
                        + $"body is pitched at a tangent of {pitchTangent}");
        }
    }

    [Fact]
    public void TheCatalogueRefusesATypeThatNamesNoBodyAndOneThatNamesAnUnmeasuredBody()
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);

        UnrenderableVehicleTypeException none = Assert.Throws<UnrenderableVehicleTypeException>(
            () => catalogue.Resolve("civ_car", string.Empty));
        Assert.Equal(UnrenderableReason.NoBlueprint, none.Reason);

        UnrenderableVehicleTypeException unknown = Assert.Throws<UnrenderableVehicleTypeException>(
            () => catalogue.Resolve("civ_car", "vehicle.nobody.measured"));
        Assert.Equal(UnrenderableReason.UnknownExtent, unknown.Reason);
        Assert.Contains("is not in catalogue", unknown.Message);
    }

    [Fact]
    public void TheShippedCatalogueMeasuresTheBodyThisTestSuiteAssertsAgainst()
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);
        VehicleExtent fuso = catalogue.Resolve("measured_truck", "vehicle.fuso.mitsubishi");
        Assert.Equal(Fuso, fuso);
    }

    [RequiresSumoFact]
    public void AVehicleTypeIsBoundOrRefusedOnceFromWhatTheScenarioWroteOnIt()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var binder = new VehicleTypeBinder(sumo.TraCI,
                                           VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue));

        Assert.True(binder.TryBind("measured_truck", out VehicleExtent extent));
        Assert.Equal("vehicle.fuso.mitsubishi", extent.BlueprintId);

        Assert.False(binder.TryBind("unmeasured", out _));
        Assert.Equal(UnrenderableReason.NoBlueprint, binder.RefusedTypes["unmeasured"]);

        // Asked again, both answers come from the cache rather than from SUMO.
        Assert.True(binder.TryBind("measured_truck", out _));
        Assert.False(binder.TryBind("unmeasured", out _));
        Assert.Single(binder.BoundTypes);
        Assert.Single(binder.RefusedTypes);
    }

    /// <summary>A world package holding a level surface at a known height, built in memory.</summary>
    private static GroundSurface FlatSurface() => SyntheticWorld.Build(_ => 0.0);

    /// <summary>A world package whose surface climbs eastwards at a constant gradient.</summary>
    private static GroundSurface RampSurface(double gradientEastwards) =>
        SyntheticWorld.Build(cell => cell.X * gradientEastwards);
}
