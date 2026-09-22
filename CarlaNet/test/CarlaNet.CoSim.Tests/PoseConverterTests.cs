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
