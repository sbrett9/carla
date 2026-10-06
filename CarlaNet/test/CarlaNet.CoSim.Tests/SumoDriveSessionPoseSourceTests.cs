using CarlaNet.Types.Streaming;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Where each lent body's drawn pose came from is put on the server -- the SUMO step declared once, a body
/// named only as it stops or starts following it -- and every snapshot carries it, so any reader of a
/// frame computes the frame's pose source from its number, as the owner ruled on 2026-10-06, and nothing
/// is sent per tick.
/// </summary>
/// <remarks>
/// Each frame's published pose source is held against what the session itself did on the tick that drew
/// it: the interpolation fraction is zero on the first tick of each SUMO step, a discontinuous step places
/// a body at SUMO's later step, and a body the session did not pose stands where its last pose put it.
/// </remarks>
public sealed class SumoDriveSessionPoseSourceTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionPoseSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void AtOneTickPerStepTheStepIsDeclaredOnceAndEveryFrameShowsSumosOwnStep()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, []);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            long frames = session.Report.Ticks;

            // Declared once, before the first tick's cue, so a step falls on the first frame.
            (PoseSourceChange declared, long declaredAt) = carla.PoseSourceWrites[0];
            Assert.True(declared.DeclareStep);
            Assert.Equal(1u, declared.TicksPerStep);
            Assert.Equal(0, declaredAt);
            Assert.Equal(1, session.Report.PoseSourceStepDeclarations);
            Assert.Equal(0, session.Report.PoseSourceFramesOutOfStep);
            Assert.Null(session.Report.PoseSourceRefused);

            int lentBodyFrames = 0;
            for (ulong frame = 1; frame <= (ulong)frames; frame++)
            {
                ObservedPoseSource published = carla.PublishedPoseSourceOf(frame)
                    ?? throw new Xunit.Sdk.XunitException($"frame {frame} carried no pose source");
                Assert.Equal(1u, published.TicksPerStep);
                Assert.Equal(1ul, published.StepFrame);
                foreach (uint body in carla.PublishedRenderSetOf(frame)?.Lent.Keys ?? Enumerable.Empty<uint>())
                {
                    // Every frame is one a SUMO step falls on, so a body that follows the step stands at it.
                    if (!published.Named.ContainsKey(body))
                    {
                        Assert.Equal(PoseSource.Simulated, published.Of(body, frame));
                    }

                    lentBodyFrames++;
                }
            }

            Assert.True(lentBodyFrames > 0, "no frame drew a body");
            // Nothing per tick: the declaration and a handful of named bodies at most.
            Assert.True(carla.PoseSourceWrites.Count < frames / 10,
                        $"{carla.PoseSourceWrites.Count} changes put over {frames} ticks");
            Assert.Equal(carla.PoseSourceWrites.Count, session.Report.PoseSourceUpdates);
        }

        // Withdrawn as the session ends: the world carries no step no session poses from.
        Assert.True(carla.PoseSourceWrites[^1].Change.IsWithdrawal);
        Assert.Equal(0u, carla.StepDeclared);
        Assert.Equal(0, carla.PoseNamedBodies);
    }

    [RequiresSumoFact]
    public void AtTwentyTicksPerStepOneFrameInTwentyShowsSumosStepAndEveryOtherAnInterpolatedPose()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, computed);
        options.World = carla;
        // A 0.05 s SUMO step over twenty 2.5 ms world ticks.
        options.WorldDeltaSeconds = 0.0025;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 150 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        Assert.Equal(20, session.Clock.WorldTicksPerSumoStep);
        long frames = session.Report.Ticks;
        Assert.Equal(20u, Assert.Single(carla.PoseSourceWrites, write => write.Change.DeclareStep).Change.TicksPerStep);

        // One frame in twenty falls on a step: the first of each twenty, from the first frame on.
        ObservedPoseSource last = carla.PublishedPoseSourceOf((ulong)frames)!;
        int stepFrames = Enumerable.Range(1, (int)frames).Count(frame => last.StepFallsOn((ulong)frame) == true);
        Assert.Equal((int)((frames + 19) / 20), stepFrames);

        // And each body's published pose source is what the session did on the tick that drew it: SUMO's own
        // step where the fraction was zero or the step discontinuous, interpolated everywhere else.
        int simulated = 0, interpolated = 0;
        foreach (CoSimPoseRecord record in computed.Where(record => record.Actor != 0))
        {
            ulong frame = (ulong)record.TickIndex + 1;
            ObservedPoseSource published = carla.PublishedPoseSourceOf(frame)!;
            PoseSource expected = record.Case == LaneInterpolationCase.Discontinuous || record.TickIndex % 20 == 0
                ? PoseSource.Simulated
                : PoseSource.Interpolated;
            Assert.Equal(expected, published.Of(record.Actor, frame));
            simulated += expected == PoseSource.Simulated ? 1 : 0;
            interpolated += expected == PoseSource.Interpolated ? 1 : 0;
        }

        _output.WriteLine($"{simulated} body-frames at SUMO's step, {interpolated} interpolated");
        Assert.True(simulated > 0 && interpolated > simulated * 15, "the drive drew too few bodies to tell");
    }

    [RequiresSumoFact]
    public void ABodyTheSessionCouldNotPlaceIsHeldOnEveryFrameItStoodAndOnNoOther()
    {
        // A grid that stops short of the network's arms, so a vehicle driving off it keeps its body and the
        // session cannot place it (SumoDriveSessionTests.ABodyWhoseVehicleDrivesOffTheGroundIsLeftWhereItWasAndToldItIsStill).
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!", cellSize: 26.0, min: -76.0, cells: 7);
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        SumoDriveSessionOptions options = Options(CoSimFixtures.RightAngleTurnScenario, world, computed);
        options.World = carla;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        Assert.True(session.Report.PosesRefusedForMissingGround > 0, "no vehicle ever left the ground");
        HashSet<(ulong Frame, uint Actor)> posed = [.. computed
            .Where(record => record.Actor != 0)
            .Select(record => ((ulong)record.TickIndex + 1, record.Actor))];

        int held = 0;
        for (ulong frame = 1; frame <= (ulong)session.Report.Ticks; frame++)
        {
            ObservedPoseSource published = carla.PublishedPoseSourceOf(frame)!;
            foreach (uint body in carla.PublishedRenderSetOf(frame)?.Lent.Keys ?? Enumerable.Empty<uint>())
            {
                PoseSource? source = published.Of(body, frame);
                if (posed.Contains((frame, body)))
                {
                    Assert.NotEqual(PoseSource.Held, source);
                }
                else
                {
                    Assert.Equal(PoseSource.Held, source);
                    held++;
                }
            }
        }

        _output.WriteLine($"{held} body-frames held, {carla.PoseSourceWrites.Count} changes put");
        Assert.True(held > 0, "no body was held");
        // Named as the hold began and ended, not on every frame it lasted.
        Assert.True(carla.PoseSourceWrites.Count < held, "a held body was named on every frame");
        Assert.Equal(0, session.Report.PoseSourceBodiesNotApplied);
    }

    [RequiresSumoFact]
    public void AFrameThatComesBackOutOfStepIsCountedAndTheStepIsDeclaredAgainAtTheNextStep()
    {
        // Another client ticks the world once after frame 30, the tenth tick of the second step: every later
        // frame is one on from where the first declaration puts it.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded(), AnotherClientTicksAfterFrame = 30 };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, []);
        options.World = carla;
        options.WorldDeltaSeconds = 0.0025;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 10 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        Assert.Equal(1, session.Report.PoseSourceFramesOutOfStep);
        Assert.Equal(2, session.Report.PoseSourceStepDeclarations);
        // The second declaration is put before the cue of the first tick of the next step, the session's 41st:
        // the world's count of ticks, which the stray one put one ahead of the session's, is then 41.
        Assert.Equal([0L, 41L], carla.PoseSourceWrites.Where(write => write.Change.DeclareStep).Select(write => write.AtTick));

        // Before the stray tick and from the second declaration on, a frame falls on a step exactly where the
        // session's fraction was zero; the ten frames between carry the first declaration's step.
        for (long tick = 0; tick < session.Report.Ticks; tick++)
        {
            ulong frame = (ulong)tick + (tick < 30 ? 1UL : 2UL);
            bool? falls = carla.PublishedPoseSourceOf(frame)!.StepFallsOn(frame);
            if (tick < 30 || tick >= 40)
            {
                Assert.Equal(tick % 20 == 0, falls);
            }
        }
    }

    [RequiresSumoFact]
    public void AServerThatRefusesThePoseSourceIsAskedOnceAndTheRunGoesOn()
    {
        // A server built before it carried a pose source has no such call. The session says what the
        // server said and goes on, and no frame says where a pose came from.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        const string refusal = "unknown method 'update_pose_source'";
        var carla = new RecordedWorld { Loaded = world.AsLoaded(), RefusesPoseSource = refusal };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, []);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            Assert.Single(carla.PoseSourceWrites);
            Assert.Equal(refusal, session.Report.PoseSourceRefused);
            Assert.Equal(0, session.Report.PoseSourceUpdates);
            Assert.Null(session.Report.Stopped);
            Assert.Contains(refusal, session.Report.ToString());
            Assert.True(session.Report.RenderSetUpdates > 0);
            Assert.Null(carla.PublishedPoseSourceOf((ulong)session.Report.Ticks));
        }

        // Refused, so nothing is withdrawn either.
        Assert.Single(carla.PoseSourceWrites);
    }

    private static SumoDriveSessionOptions Options(string scenario, SyntheticWorld world, List<CoSimPoseRecord> computed) =>
        new(scenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            OnPose = computed.Add,
        };
}
