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
/// given, which is how both are exercised without a server.
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
        }

        // One comparison per vehicle per tick, handed out rather than accumulated.
        Assert.NotEmpty(divergences);
        Assert.All(divergences, divergence => Assert.NotEqual(0u, divergence.Actor));
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

    private static PoseDivergence Compare(Location drift, Rotation rotationDrift)
    {
        var commanded = new VehiclePose("v", "vehicle.dodge.charger", 10.0, -20.0, 5.0,
                                        179.0, 2.0, -1.0, 0.0, 0.0, true);
        var observed = new Transform(
            new Location((float)commanded.X + drift.X, (float)commanded.Y + drift.Y,
                         (float)commanded.Z + drift.Z),
            new Rotation((float)commanded.PitchDegrees + rotationDrift.Pitch,
                         (float)commanded.YawDegrees + rotationDrift.Yaw,
                         (float)commanded.RollDegrees + rotationDrift.Roll));
        return PoseDivergence.Between(7, 3.5, "v", 42, commanded, observed);
    }

    private static SumoDriveSession Run(RecordedWorld carla, List<PoseDivergence> divergences)
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var options = new SumoDriveSessionOptions(
            CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 8))
        {
            World = carla,
            OnDivergence = divergences.Add,
        };

        SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 200 && session.Advance(); step++)
        {
        }

        return session;
    }
}
