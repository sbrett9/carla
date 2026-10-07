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
/// it, in the owner's four words: sumo where the interpolation fraction is zero, on the first tick of each
/// SUMO step; interpolated on every other tick; jump where SUMO's step was too far from the last to drive
/// in one step and the body is shown at SUMO's later position; and stale where the session did not pose a
/// body and it stands where it was last drawn.
/// </remarks>
public sealed class SumoDriveSessionPoseSourceTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionPoseSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void AtOneTickPerStepTheStepIsDeclaredOnceAndEveryFrameIsSumo()
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
                        Assert.Equal(PoseSource.Sumo, published.Of(body, frame));
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
    public void AtTwentyTicksPerStepOneFrameInTwentyIsSumoAndEveryOtherInterpolated()
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

        // And each body's published pose source is what the session did on the tick that drew it: sumo where
        // the fraction was zero, interpolated everywhere else.
        int sumo = 0, interpolated = 0;
        foreach (CoSimPoseRecord record in computed.Where(record => record.Actor != 0))
        {
            ulong frame = (ulong)record.TickIndex + 1;
            ObservedPoseSource published = carla.PublishedPoseSourceOf(frame)!;
            Assert.NotEqual(LaneInterpolationCase.Discontinuous, record.Case);
            PoseSource expected = record.TickIndex % 20 == 0 ? PoseSource.Sumo : PoseSource.Interpolated;
            Assert.Equal(expected, published.Of(record.Actor, frame));
            sumo += expected == PoseSource.Sumo ? 1 : 0;
            interpolated += expected == PoseSource.Interpolated ? 1 : 0;
        }

        _output.WriteLine($"{sumo} body-frames sumo, {interpolated} interpolated");
        Assert.True(sumo > 0 && interpolated > sumo * 15, "the drive drew too few bodies to tell");
    }

    [RequiresSumoFact]
    public void ADiscontinuousStepIsAJumpOnEveryFrameOfItAndOnNoOther()
    {
        // The stop's jump moves the vehicle to the start of the exit: SUMO's two frames either side are
        // further apart than it drives in a step, and at twenty ticks per step all twenty frames between them
        // show the body at SUMO's later position, the step frame among them.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        SumoDriveSessionOptions options = Options(CoSimFixtures.JumpScenario, world, computed);
        options.World = carla;
        options.WorldDeltaSeconds = 0.0025;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 200 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            Assert.Equal(20, session.Clock.WorldTicksPerSumoStep);
            int jumped = 0;
            foreach (CoSimPoseRecord record in computed.Where(record => record.Actor != 0))
            {
                ulong frame = (ulong)record.TickIndex + 1;
                PoseSource? published = carla.PublishedPoseSourceOf(frame)!.Of(record.Actor, frame);
                if (record.Case == LaneInterpolationCase.Discontinuous)
                {
                    Assert.Equal(PoseSource.Jump, published);
                    jumped++;
                }
                else
                {
                    Assert.Equal(record.TickIndex % 20 == 0 ? PoseSource.Sumo : PoseSource.Interpolated, published);
                }
            }

            Assert.Equal(20, jumped);
            Assert.Null(session.Report.PoseSourceWithoutJump);
            Assert.Equal(0, session.Report.PoseSourceJumpsNamedSumo);
            Assert.Equal(0, session.Report.PoseSourceBodiesNotApplied);
        }

        // Named as the jump began, on the step frame, and cleared as the next step began; never named sumo.
        (PoseSourceChange named, long namedAt) = Assert.Single(carla.PoseSourceWrites, write => write.Change.Jump.Count > 0);
        (PoseSourceChange cleared, long clearedAt) = Assert.Single(carla.PoseSourceWrites, write => write.Change.Cleared.Count > 0);
        Assert.Equal(named.Jump, cleared.Cleared);
        Assert.Equal(0, namedAt % 20);
        Assert.Equal(namedAt + 20, clearedAt);
        Assert.All(carla.PoseSourceWrites, write => Assert.Empty(write.Change.Sumo));
        Assert.All(carla.PoseSourceWrites, write => Assert.False(write.Change.WithoutJump));
    }

    [RequiresSumoFact]
    public void ABodyWithNoGroundUnderItIsStaleOnEveryFrameItStoodAndOnNoOther()
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

        int stale = 0;
        for (ulong frame = 1; frame <= (ulong)session.Report.Ticks; frame++)
        {
            ObservedPoseSource published = carla.PublishedPoseSourceOf(frame)!;
            foreach (uint body in carla.PublishedRenderSetOf(frame)?.Lent.Keys ?? Enumerable.Empty<uint>())
            {
                PoseSource? source = published.Of(body, frame);
                if (posed.Contains((frame, body)))
                {
                    Assert.NotEqual(PoseSource.Stale, source);
                }
                else
                {
                    Assert.Equal(PoseSource.Stale, source);
                    stale++;
                }
            }
        }

        _output.WriteLine($"{stale} body-frames stale, {carla.PoseSourceWrites.Count} changes put");
        Assert.True(stale > 0, "no body was stale");
        // Named as it went stale and as it was placed again, not on every frame it lasted.
        Assert.True(carla.PoseSourceWrites.Count < stale, "a stale body was named on every frame");
        Assert.Equal(0, session.Report.PoseSourceBodiesNotApplied);
    }

    [RequiresSumoTheory]
    [InlineData(0.0025)]
    [InlineData(0.05)]
    public void AVehicleSumoStopsReportingIsDrawnOnItsLastStepsFrameAtSumosPositionReadingSumoAndGoneFromTheNext(
        double worldDelta)
    {
        // The first truck reaches the end of its route and leaves SUMO. The frame of the last step SUMO reported
        // it in shows it exactly where SUMO put it then, its body lent to it on the server's render set and
        // reading sumo; from the next frame -- the second of that step at twenty ticks per step, the next step's
        // at one -- its body is parked. No frame draws it standing where it was last drawn, and nothing is named
        // stale on ground that is whole (the owner's ruling of 2026-10-06).
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var timeline = new LeavingTimeline();
        List<CoSimPoseRecord> computed = [];
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, computed);
        options.World = carla;
        options.WorldDeltaSeconds = worldDelta;
        options.OnRelease = released.Add;
        options.StepObservers.Add(timeline);

        int ticksPerStep;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            ticksPerStep = session.Clock.WorldTicksPerSumoStep;
        }

        Assert.Equal(worldDelta < 0.01 ? 20 : 1, ticksPerStep);
        RenderedVehicleInterval first = Assert.Single(released, interval => interval.VehicleId == "first");
        Assert.Equal(RenderSetReleaseReason.LeftTheSimulation, first.ReleaseReason);
        uint body = first.Actor;
        Assert.NotEqual(0u, body);

        // Every lent body on every frame was posed on that frame's tick and is never stale; the first truck's
        // last such frame is the frame of the last step SUMO reported it in.
        HashSet<(ulong Frame, uint Actor)> posed = [.. computed
            .Where(record => record.Actor != 0)
            .Select(record => ((ulong)record.TickIndex + 1, record.Actor))];
        ulong lastDrawn = 0;
        for (ulong frame = 1; frame <= (ulong)carla.Ticks; frame++)
        {
            ObservedPoseSource published = carla.PublishedPoseSourceOf(frame)!;
            foreach ((uint lent, (string vehicleId, _, _)) in carla.PublishedRenderSetOf(frame)?.Lent
                         ?? new Dictionary<uint, (string, string, ulong)>())
            {
                Assert.Contains((frame, lent), posed);
                Assert.NotEqual(PoseSource.Stale, published.Of(lent, frame));
                if (vehicleId == "first")
                {
                    lastDrawn = frame;
                }
            }
        }

        LeavingTimeline.Step lastStep = timeline.LastStepWith("first");
        Assert.Equal(timeline.FrameAt(lastStep.FrameSeconds), lastDrawn);
        Assert.Equal(("first", body), (carla.PublishedRenderSetOf(lastDrawn)!.Lent[body].VehicleId, body));

        // A step frame, reading sumo, and drawn where SUMO put it: the step's own state, at fraction zero.
        ObservedPoseSource onTheLast = carla.PublishedPoseSourceOf(lastDrawn)!;
        Assert.True(onTheLast.StepFallsOn(lastDrawn));
        Assert.Equal(PoseSource.Sumo, onTheLast.Of(body, lastDrawn));
        CoSimPoseRecord drawn = Assert.Single(computed, record => record.Pose.VehicleId == "first"
                                                                  && (ulong)record.TickIndex + 1 == lastDrawn);
        Assert.Equal(0, drawn.TickIndex % ticksPerStep);
        CoSimVehicleFrame there = lastStep.Frames["first"];
        Assert.True(Math.Sqrt(((drawn.SumoX - there.X) * (drawn.SumoX - there.X))
                              + ((drawn.SumoY - there.Y) * (drawn.SumoY - there.Y))) < 0.01,
                    "the first truck was not drawn where SUMO last had it");

        // Gone from the next frame: its body parked there, and no later frame drawing it.
        Assert.Contains(body, carla.PublishedRenderSetOf(lastDrawn + 1)!.Parked);
        Assert.Equal(ticksPerStep == 1, carla.PublishedPoseSourceOf(lastDrawn + 1)!.StepFallsOn(lastDrawn + 1));
        Assert.All(carla.PoseSourceWrites, write => Assert.Empty(write.Change.Stale));
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

    [RequiresSumoFact]
    public void AServerBuiltBeforeTheJumpStateIsSentEveryChangeWithoutItAndAJumpIsNamedSumo()
    {
        // A server that binds the call with five arguments refuses the jump list for its count. The session
        // sends the same declaration again without it, every later change the same way, and names the
        // jumping body sumo, which is what every reader then writes for the frames of the jump; the report
        // says so, and the run goes on.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        const string wrongCount = "rpclib: Function 'update_pose_source' was called with an invalid number of "
                                  + "arguments. Expected: 6, got: 7";
        var carla = new RecordedWorld { Loaded = world.AsLoaded(), KnowsNoJump = wrongCount };
        List<CoSimPoseRecord> computed = [];
        SumoDriveSessionOptions options = Options(CoSimFixtures.JumpScenario, world, computed);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            Assert.Null(session.Report.Stopped);
            Assert.Null(session.Report.PoseSourceRefused);
            Assert.Equal(wrongCount, session.Report.PoseSourceWithoutJump);
            Assert.Equal(1, session.Report.PoseSourceJumpsNamedSumo);
            Assert.Equal(1, session.Report.PoseSourceStepDeclarations);
            Assert.Contains("the server knows no jump, so 1 jump(s) were named sumo", session.Report.ToString());

            // The frame of the jump reads sumo: the one name such a server has for SUMO's later position.
            CoSimPoseRecord jump = Assert.Single(computed,
                                                 record => record.Actor != 0 && record.Case == LaneInterpolationCase.Discontinuous);
            ulong frame = (ulong)jump.TickIndex + 1;
            ObservedPoseSource published = carla.PublishedPoseSourceOf(frame)!;
            Assert.Equal(PoseSource.Sumo, published.Named[jump.Actor]);
            Assert.Equal(PoseSource.Sumo, published.Of(jump.Actor, frame));
            Assert.DoesNotContain(PoseSource.Jump, published.Named.Values);
        }

        // Asked once with the jump list, the declaration refused for its count; the same declaration again
        // without it, and every change after it, the withdrawal included.
        (PoseSourceChange asked, _) = carla.PoseSourceWrites[0];
        (PoseSourceChange again, _) = carla.PoseSourceWrites[1];
        Assert.True(asked.DeclareStep);
        Assert.False(asked.WithoutJump);
        Assert.True(again.DeclareStep);
        Assert.True(again.WithoutJump);
        Assert.All(carla.PoseSourceWrites.Skip(1), write => Assert.True(write.Change.WithoutJump));
        Assert.True(carla.PoseSourceWrites[^1].Change.IsWithdrawal);
        Assert.Equal(0u, carla.StepDeclared);
        Assert.Equal(0, carla.PoseNamedBodies);
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
