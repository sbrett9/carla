using CarlaNet.Recording;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The draw distance is an optional performance control, off by default: unset, nothing is sent and
/// every body is drawn at any range, as before; set, every body is given it once, before the tick it
/// is first drawn in, every vehicle is still posed and in every frame's render set, and each frame
/// says the distance it was drawn under. A server without the call refuses it once, and the run goes
/// on drawing every body at any range.
/// </summary>
public sealed class SumoDriveSessionDrawDistanceTests
{
    private const double Distance = 250.0;

    private readonly ITestOutputHelper _output;

    public SumoDriveSessionDrawDistanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void UnsetNothingIsSentAndNoFrameCarriesADistance()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };

        using (SumoDriveSession session = SumoDriveSession.Start(Options(world, carla)))
        {
            Run(session, 200);

            Assert.Null(session.DrawDistanceMetres);
            Assert.Null(session.Report.DrawDistanceMetres);
            Assert.Equal(0, session.Report.DrawDistanceWrites);
            Assert.All(HeldFrames(session), frame => Assert.Null(frame.DrawDistanceMetres));
            Assert.Contains("draw distance      none: every body drawn at any range", session.Report.ToString());
        }

        Assert.Empty(carla.DrawDistanceWrites);
        Assert.NotEmpty(carla.Spawned);
    }

    [RequiresSumoFact]
    public void EveryBodyIsGivenTheDistanceOnceBeforeTheTickItIsFirstDrawnIn()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, carla);
        options.DrawDistanceMetres = Distance;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Run(session, 400);
        _output.WriteLine(session.Report.ToString());

        // Every body the pool spawned was set once, to the distance, in a write made on the tick of
        // the batch that first posed it -- so before that tick's cue, and no frame drew it unset.
        Assert.True(carla.Spawned.Count >= 2, "the run spawned too few bodies to show bodies spawned apart");
        IEnumerable<uint> written = carla.DrawDistanceWrites.SelectMany(write => write.Bodies);
        Assert.Equal(Enumerable.Range(1, carla.Spawned.Count).Select(actor => (uint)actor), written.Order());
        Assert.All(carla.DrawDistanceWrites, write => Assert.Equal(Distance, write.Metres));
        foreach ((IReadOnlyList<uint> bodies, _, long atTick) in carla.DrawDistanceWrites)
        {
            foreach (uint body in bodies)
            {
                long firstPosed = carla.DrivenBatches
                    .First(entry => entry.Batch.Any(command => command is Types.Rpc.Commands.ApplyTransformCommand t
                                                                && t.Actor == body)).AtTick;
                Assert.Equal(firstPosed, atTick);
                Assert.Equal(Distance, carla.DrawDistanceOf(body));
            }
        }

        // One write per tick that spawned a body, and none on any other.
        Assert.Equal(carla.DrawDistanceWrites.Count, session.Report.DrawDistanceWrites);
        Assert.True(session.Report.DrawDistanceWrites <= carla.Spawned.Count);
        Assert.Equal(0, session.Report.DrawDistanceBodiesNotFound);
        Assert.Null(session.Report.DrawDistanceRefused);
        Assert.Equal(Distance, session.DrawDistanceMetres);
        Assert.Contains("draw distance      250 m, rendering only", session.Report.ToString());

        // And each frame says the distance it was drawn under.
        Assert.All(HeldFrames(session).Where(frame => frame.Count > 0),
                   frame => Assert.Equal(Distance, frame.DrawDistanceMetres));
    }

    [RequiresSumoFact]
    public void TheDistanceChangesNoBodyNoPoseAndNoRenderSet()
    {
        // Rendering only: the same scenario run with and without a draw distance spawns the same
        // bodies, writes the same poses and draws the same vehicles on every frame.
        using SyntheticWorld world = Fixture();
        (List<CoSimPoseRecord> plainPoses, List<(ulong, string[])> plainSets) = RunRecording(world, null);
        (List<CoSimPoseRecord> limitedPoses, List<(ulong, string[])> limitedSets) = RunRecording(world, 40.0);

        Assert.NotEmpty(plainPoses);
        Assert.Equal(plainPoses.Select(Describe), limitedPoses.Select(Describe));
        Assert.Equal(plainSets.Count, limitedSets.Count);
        for (int index = 0; index < plainSets.Count; index++)
        {
            Assert.Equal(plainSets[index].Item1, limitedSets[index].Item1);
            Assert.Equal(plainSets[index].Item2, limitedSets[index].Item2);
        }
    }

    [RequiresSumoFact]
    public void ADistanceSetDuringTheRunReachesEveryBodyInOneWriteAndHoldsFromTheNextFrame()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };

        using SumoDriveSession session = SumoDriveSession.Start(Options(world, carla));
        Run(session, 120);
        int bodies = carla.Spawned.Count;
        Assert.True(bodies > 0);
        ulong before = session.RenderSet.NewestFrame!.Value;
        Assert.Empty(carla.DrawDistanceWrites);

        session.SetDrawDistance(Distance);

        var set = Assert.Single(carla.DrawDistanceWrites);
        Assert.Equal(Enumerable.Range(1, bodies).Select(actor => (uint)actor), set.Bodies.Order());
        Assert.Equal(Distance, set.Metres);
        Assert.Equal(Distance, session.Report.DrawDistanceMetres);
        Assert.True(session.RenderSet.TryGetRenderSet(before, out RenderSet earlier));
        Assert.Null(earlier.DrawDistanceMetres);

        Run(session, 20);
        Assert.True(session.RenderSet.TryGetRenderSet(before + 1, out RenderSet next));
        Assert.Equal(Distance, next.DrawDistanceMetres);

        // Cleared again: every body is sent zero, drawn at any range, and the frames carry none.
        session.SetDrawDistance(null);
        (IReadOnlyList<uint> cleared, double zero, _) = carla.DrawDistanceWrites[^1];
        Assert.Equal(0.0, zero);
        Assert.Equal(carla.Spawned.Count, cleared.Count);
        Assert.All(cleared, body => Assert.Null(carla.DrawDistanceOf(body)));
        ulong clearedAfter = session.RenderSet.NewestFrame!.Value;
        Run(session, 5);
        Assert.True(session.RenderSet.TryGetRenderSet(clearedAfter + 1, out RenderSet unlimited));
        Assert.Null(unlimited.DrawDistanceMetres);
        Assert.Null(session.DrawDistanceMetres);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetDrawDistance(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetDrawDistance(double.NaN));
    }

    [RequiresSumoFact]
    public void AServerThatRefusesTheDistanceIsAskedOnceAndEveryBodyIsDrawnAtAnyRange()
    {
        using SyntheticWorld world = Fixture();
        const string refusal = "unknown method 'set_actors_max_draw_distance'";
        var carla = new RecordedWorld { Loaded = world.AsLoaded(), RefusesDrawDistance = refusal };
        SumoDriveSessionOptions options = Options(world, carla);
        options.DrawDistanceMetres = Distance;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Run(session, 400);

        Assert.Single(carla.DrawDistanceWrites);
        Assert.Equal(refusal, session.Report.DrawDistanceRefused);
        Assert.Equal(0, session.Report.DrawDistanceWrites);
        Assert.Null(session.Report.Stopped);
        Assert.Null(session.DrawDistanceMetres);
        Assert.All(HeldFrames(session), frame => Assert.Null(frame.DrawDistanceMetres));
        Assert.Contains("250 m asked for and refused, so every body was drawn at any range", session.Report.ToString());

        // Nothing more is sent, a change during the run included.
        session.SetDrawDistance(100.0);
        Assert.Single(carla.DrawDistanceWrites);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ADistanceThatIsNotAPositiveNumberIsRefusedBeforeAnythingIsStarted(double metres)
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, carla);
        options.DrawDistanceMetres = metres;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Contains("draw distance", refused.Message);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.Spawned);
    }

    /// <summary>One run of the fixture, keeping every pose and every frame's set, under a distance or none.</summary>
    private static (List<CoSimPoseRecord> Poses, List<(ulong Frame, string[] Vehicles)> Sets) RunRecording(
        SyntheticWorld world, double? metres)
    {
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Options(world, carla);
        options.OnPose = poses.Add;
        options.DrawDistanceMetres = metres;
        List<(ulong, string[])> sets = [];
        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 300 && session.Advance(); step++)
        {
            if (session.RenderSet.NewestFrame is { } frame
                && session.RenderSet.TryGetRenderSet(frame, out RenderSet set))
            {
                sets.Add((frame, set.ByActor.Values.Select(vehicle => $"{vehicle.ActorId}:{vehicle.SumoId}")
                                    .Order(StringComparer.Ordinal).ToArray()));
            }
        }

        return (poses, sets);
    }

    private static string Describe(CoSimPoseRecord record) =>
        FormattableString.Invariant(
            $"{record.TickIndex} {record.Actor} {record.Pose.VehicleId} {record.Pose.X:0.000000} {record.Pose.Y:0.000000} {record.Pose.Z:0.000000} {record.Pose.YawDegrees:0.000000}");

    private static void Run(SumoDriveSession session, int steps)
    {
        for (int step = 0; step < steps && session.Advance(); step++)
        {
        }
    }

    /// <summary>Every frame's render set the session still holds.</summary>
    private static List<RenderSet> HeldFrames(SumoDriveSession session)
    {
        List<RenderSet> frames = [];
        if (session.RenderSet.NewestFrame is not { } newest)
        {
            return frames;
        }

        for (ulong frame = newest; frame >= 1 && session.RenderSet.TryGetRenderSet(frame, out RenderSet set); frame--)
        {
            frames.Add(set);
        }

        return frames;
    }

    private static SyntheticWorld Fixture() =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

    private static SumoDriveSessionOptions Options(SyntheticWorld world, RecordedWorld carla) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
