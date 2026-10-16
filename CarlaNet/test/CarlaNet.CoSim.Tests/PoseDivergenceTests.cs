using CarlaNet.Types.Geom;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The commanded pose against what the world did with it, per vehicle per tick.
/// </summary>
/// <remarks>
/// Two things have to hold for the measurement to be worth anything, and a world that does as it is
/// told establishes only the first. It has to read zero when the world applies the pose, so that a
/// zero means something; and it has to read the separation when the world does not, so that a
/// non-zero means something. The recording world answers with the pose plus whatever drift it is
/// given, and with the velocity it was given or -- as a server that cannot report one for a
/// physics-disabled vehicle -- with zero, which is how both are exercised without a server.
/// </remarks>
public sealed class PoseDivergenceTests
{
    private readonly ITestOutputHelper _output;

    public PoseDivergenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AnAngleSeparationIsTheShortestArcAndNotTheSubtraction()
    {
        // The engine's quaternion-to-rotator conversion puts a body commanded to 179 degrees on the
        // far side of the wrap. It has turned two degrees, not three hundred and fifty-eight.
        Assert.Equal(2.0, PoseDivergence.Separation(-179.0, 179.0), 9);
        Assert.Equal(2.0, PoseDivergence.Separation(179.0, -179.0), 9);
        Assert.Equal(0.0, PoseDivergence.Separation(360.0, 0.0), 9);
        Assert.Equal(90.0, PoseDivergence.Separation(45.0, -45.0), 9);
    }

    [Fact]
    public void AWorldThatAppliesThePoseDivergesByNothing()
    {
        PoseDivergence divergence = Compare(drift: default, rotationDrift: default);

        Assert.Equal(0.0, divergence.PositionMetres, 6);
        Assert.Equal(0.0, divergence.YawDegrees, 6);
        Assert.Equal(0.0, divergence.PitchDegrees, 6);
        Assert.Equal(0.0, divergence.RollDegrees, 6);
        Assert.Equal(0.0, divergence.VelocityMetresPerSecond, 6);
    }

    [Fact]
    public void ABodyThatReportsNoVelocityIsShortByTheWholeCommandedSpeed()
    {
        // What a bridge that sends no velocity produces, and what a server built before the
        // kinematic-velocity change produces whatever is sent: the gap is the commanded velocity's
        // whole length, climb included, so it cannot be mistaken for rounding.
        PoseDivergence divergence = Compare(default, default, observedVelocity: new Vector3D(0f, 0f, 0f));

        Assert.Equal(Math.Sqrt((12.0 * 12.0) + (5.0 * 5.0) + (0.6 * 0.6)),
                     divergence.CommandedSpeedMetresPerSecond, 6);
        Assert.Equal(divergence.CommandedSpeedMetresPerSecond, divergence.VelocityMetresPerSecond, 6);
    }

    [Fact]
    public void AVelocityAtTheRightSpeedPointingTheWrongWayIsStillMeasured()
    {
        // The same speed with the northing's sign lost, which is the frame conversion's own mistake
        // repeated on the velocity. A comparison of speeds would read zero; the vectors are ten
        // metres per second apart.
        PoseDivergence divergence = Compare(default, default, observedVelocity: new Vector3D(12f, -5f, 0.6f));

        Assert.Equal(10.0, divergence.VelocityMetresPerSecond, 5);
    }

    [Fact]
    public void AWorldThatMovesTheBodyIsMeasuredDoingIt()
    {
        PoseDivergence divergence = Compare(new Location(3f, 4f, 0f), new Rotation(1f, -2f, 0.5f));

        Assert.Equal(5.0, divergence.PositionMetres, 4);
        Assert.Equal(2.0, divergence.YawDegrees, 4);
        Assert.Equal(1.0, divergence.PitchDegrees, 4);
        Assert.Equal(0.5, divergence.RollDegrees, 4);
    }

    [RequiresSumoFact]
    public void ARunAgainstAWorldThatAppliesWhatItIsToldMeasuresZeroOnEveryVehicleTick()
    {
        var carla = new RecordedWorld();
        List<PoseDivergence> divergences = [];

        using (SumoDriveSession session = Run(carla, divergences))
        {
            _output.WriteLine(session.Report.ToString());

            Assert.True(session.Report.DivergenceSamples > 0, "nothing was ever compared");
            Assert.Equal(0, session.Report.VehicleTicksWithNoReadBack);

            // A millimetre, not zero: the pose is computed in double and the wire carries float, so
            // a pose applied exactly still reads back with the rounding of that conversion on it.
            // The tolerance is the conversion's, and anything the conversions themselves get wrong
            // is orders of magnitude above it.
            Assert.True(session.Report.WorstPositionDivergenceMetres < 1e-3,
                        $"worst {session.Report.WorstPositionDivergenceMetres} m");
            Assert.True(session.Report.WorstYawDivergenceDegrees < 1e-3,
                        $"worst yaw {session.Report.WorstYawDivergenceDegrees} deg");
            Assert.True(session.Report.WorstPitchDivergenceDegrees < 1e-3,
                        $"worst pitch {session.Report.WorstPitchDivergenceDegrees} deg");
            Assert.True(session.Report.WorstRollDivergenceDegrees < 1e-3,
                        $"worst roll {session.Report.WorstRollDivergenceDegrees} deg");

            // And every body reports the velocity it was given, to the rounding of the
            // single-precision wire, while it moves at traffic speed.
            Assert.True(session.Report.MeanCommandedSpeedMetresPerSecond > 5.0,
                        $"mean commanded {session.Report.MeanCommandedSpeedMetresPerSecond} m/s");
            Assert.True(session.Report.WorstVelocityDivergenceMetresPerSecond < 1e-4,
                        $"worst velocity {session.Report.WorstVelocityDivergenceMetresPerSecond} m/s");
            Assert.Equal(0, carla.VelocityWritesWhileSimulating);
        }

        // One comparison per vehicle per tick, handed out rather than accumulated.
        Assert.NotEmpty(divergences);
        Assert.All(divergences, divergence => Assert.NotEqual(0u, divergence.Actor));
    }

    [RequiresSumoFact]
    public void ARunAgainstAWorldWhoseBodiesReportNoVelocitySaysTheGapIsTheWholeSpeed()
    {
        // The self-check pointed at the failure it exists for: bodies that report standing still
        // while they are driven. It has to say so on every vehicle-tick, by the commanded speed, and
        // name where the largest gap was -- a check that has never read anything but zero has not
        // been shown to measure anything.
        var carla = new RecordedWorld { ReportsNoKinematicVelocity = true };
        List<PoseDivergence> divergences = [];

        using SumoDriveSession session = Run(carla, divergences);

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.True(session.Report.MeanCommandedSpeedMetresPerSecond > 5.0,
                    $"mean commanded {session.Report.MeanCommandedSpeedMetresPerSecond} m/s");
        Assert.Equal(session.Report.MeanCommandedSpeedMetresPerSecond,
                     session.Report.MeanVelocityDivergenceMetresPerSecond, 6);
        Assert.True(session.Report.WorstVelocityDivergenceMetresPerSecond > 10.0,
                    $"worst velocity {session.Report.WorstVelocityDivergenceMetresPerSecond} m/s");
        Assert.All(divergences, divergence =>
            Assert.Equal(divergence.CommandedSpeedMetresPerSecond, divergence.VelocityMetresPerSecond, 6));
        Assert.NotEmpty(session.Report.WorstVelocityDivergence!.Value.VehicleId);
        Assert.Contains("  velocity         worst ", report);

        // The poses themselves were applied: the gap is in the velocity and nowhere else.
        Assert.True(session.Report.WorstPositionDivergenceMetres < 1e-3,
                    $"worst {session.Report.WorstPositionDivergenceMetres} m");
    }

    [RequiresSumoFact]
    public void ARunAgainstAWorldHalfAMetreOutSaysSoPerVehiclePerTick()
    {
        var carla = new RecordedWorld { TransformDrift = new Location(0.5f, 0f, 0f) };
        List<PoseDivergence> divergences = [];

        using SumoDriveSession session = Run(carla, divergences);

        _output.WriteLine(session.Report.ToString());
        Assert.Equal(0.5, session.Report.WorstPositionDivergenceMetres, 3);
        Assert.Equal(0.5, session.Report.MeanPositionDivergenceMetres, 3);
        Assert.NotNull(session.Report.WorstDivergence);

        // And it names which vehicle and which instant, because that is the next question.
        Assert.NotEmpty(session.Report.WorstDivergence!.Value.VehicleId);
        Assert.All(divergences, divergence => Assert.Equal(0.5, divergence.PositionMetres, 3));
    }

    private static PoseDivergence Compare(Location drift,
                                          Rotation rotationDrift,
                                          Vector3D? observedVelocity = null)
    {
        var commanded = new VehiclePose("v", "vehicle.dodge.charger", 10.0, -20.0, 5.0,
                                        179.0, 2.0, -1.0, 12.0, 5.0, 0.6, true);
        var observed = new Transform(
            new Location((float)commanded.X + drift.X, (float)commanded.Y + drift.Y,
                         (float)commanded.Z + drift.Z),
            new Rotation((float)commanded.PitchDegrees + rotationDrift.Pitch,
                         (float)commanded.YawDegrees + rotationDrift.Yaw,
                         (float)commanded.RollDegrees + rotationDrift.Roll));
        Vector3D reported = observedVelocity
            ?? new Vector3D((float)commanded.VelocityX, (float)commanded.VelocityY,
                            (float)commanded.VelocityZ);
        return PoseDivergence.Between(7, 3.5, "v", 42, commanded, observed, reported);
    }

    private static SumoDriveSession Run(RecordedWorld carla, List<PoseDivergence> divergences)
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        carla.Loaded = world.AsLoaded();

        var options = new SumoDriveSessionOptions(
            CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            OnDivergence = divergences.Add,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

        SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 200 && session.Advance(); step++)
        {
        }

        return session;
    }
}
