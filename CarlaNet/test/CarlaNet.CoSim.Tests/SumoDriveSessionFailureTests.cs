using System.Diagnostics;
using CarlaNet.Sumo;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What a session does when one side fails part-way: SUMO dying or going silent, the CARLA server
/// stalling or dropping the connection, a vehicle taken out while it holds a body, a vehicle SUMO cannot
/// route or insert, a collision, and a sun that goes away. Either side stalling stops both, the refusal
/// says where, and the world, the bodies and the lease are given back on the way out.
/// </summary>
public sealed class SumoDriveSessionFailureTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-failure-" + Guid.NewGuid().ToString("n"));

    public SumoDriveSessionFailureTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    // -- SUMO -------------------------------------------------------------------------------------

    [RequiresSumoFact]
    public void ASumoThatStopsAnsweringStopsTheRunInTheWindowAndEverythingIsGivenBack()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);
        options.SumoAnswerTimeoutSeconds = 1.0;

        SumoDriveSession session = SumoDriveSession.Start(options);
        int sumo = session.Sumo.ProcessId!.Value;
        try
        {
            AdvanceUntilABodyIsHeld(session, carla);
            long ticks = session.Report.Ticks;
            long steps = session.Report.SumoSteps;

            ProcessSuspension.Suspend(sumo);
            CoSimSessionRefusedException refused = RefusalWithin(session, TimeSpan.FromSeconds(30));

            _output.WriteLine(refused.Message);
            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Equal(CoSimStopCause.SumoConnectionLost, refused.Cause);
            Assert.Equal("sumo-connection-lost", refused.CauseName);
            Assert.Contains("SUMO did not answer within 1 s", refused.Message);
            Assert.IsType<FatalTraCIError>(refused.InnerException);

            // The step's ticks were rendered before SUMO was asked for the next step, which never
            // answered, and nothing was ticked after it went silent.
            Assert.Equal(steps, session.Report.SumoSteps);
            Assert.Equal(carla.Ticks, session.Report.Ticks);
            CoSimRunStop stopped = session.Report.Stopped!;
            Assert.Equal(CoSimStopCause.SumoConnectionLost, stopped.Cause);
            Assert.Equal(session.Report.Ticks, stopped.CompleteTicks);
            Assert.True(stopped.CompleteTicks > ticks);
            Assert.Equal(session.RenderedTimeSeconds - session.Clock.WorldDeltaSeconds,
                         stopped.LastCompleteSeconds!.Value, 9);
            Assert.StartsWith("STOPPED            sumo-connection-lost at Window", session.Report.ToString());
        }
        finally
        {
            var shutdown = Stopwatch.StartNew();
            session.Dispose();
            shutdown.Stop();
            if (ProcessSuspension.IsRunning(sumo))
            {
                ProcessSuspension.Resume(sumo);
            }

            // A SUMO that stopped answering is ended, not waited on for the grace a SUMO that is
            // exiting gets.
            Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(5),
                        $"disposing the session took {shutdown.Elapsed.TotalSeconds:0.0} s");
        }

        AssertGivenBack(carla, before, options);
        Assert.False(ProcessSuspension.IsRunning(sumo), "the silent SUMO was left running");
    }

    [RequiresSumoFact]
    public void ASumoThatDiesMidRunStopsTheRunInTheWindowAndEverythingIsGivenBack()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);

        SumoDriveSession session = SumoDriveSession.Start(options);
        try
        {
            AdvanceUntilABodyIsHeld(session, carla);
            using (Process sumo = Process.GetProcessById(session.Sumo.ProcessId!.Value))
            {
                sumo.Kill();
                sumo.WaitForExit();
            }

            CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
                () => session.Advance());

            _output.WriteLine(refused.Message);
            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Equal(CoSimStopCause.SumoConnectionLost, refused.Cause);
            Assert.IsType<FatalTraCIError>(refused.InnerException);
            Assert.Equal(CoSimStopCause.SumoConnectionLost, session.Report.Stopped!.Cause);
        }
        finally
        {
            session.Dispose();
        }

        AssertGivenBack(carla, before, options);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ABoundOnSumoSAnswersThatIsNoLengthOfTimeIsRefusedBeforeAnythingIsTouched(double seconds)
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla);
        options.SumoAnswerTimeoutSeconds = seconds;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Contains("SUMO's answers are bounded at", refused.Message);
        Assert.Equal(0, carla.Descriptions);
        Assert.Empty(carla.SettingsWrites);
    }

    // -- CARLA ------------------------------------------------------------------------------------

    [RequiresSumoFact]
    public void ATickCueThatNeverReturnsStopsTheRunInTheWindowWithTheTimeoutInside()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);

        SumoDriveSession session = SumoDriveSession.Start(options);
        try
        {
            AdvanceUntilABodyIsHeld(session, carla);
            long ticks = session.Report.Ticks;
            double lastComplete = session.RenderedTimeSeconds - session.Clock.WorldDeltaSeconds;

            // What the client raises once its per-call timeout passes with no answer to the cue.
            carla.ThrowOnTick = new TimeoutException("The operation has timed out.");
            CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
                () => session.Advance());

            _output.WriteLine(refused.Message);
            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Equal(CoSimStopCause.WorldConnectionLost, refused.Cause);
            Assert.Equal("world-connection-lost", refused.CauseName);
            Assert.IsType<TimeoutException>(refused.InnerException);
            Assert.Contains("(Tick)", refused.Message);
            Assert.Equal(new CoSimRunStop(CoSimSessionStage.Window, CoSimStopCause.WorldConnectionLost,
                                          lastComplete, ticks, refused.Message),
                         session.Report.Stopped);
        }
        finally
        {
            carla.ThrowOnTick = null;
            session.Dispose();
        }

        AssertGivenBack(carla, before, options);
    }

    [RequiresSumoFact]
    public void AConnectionThatDropsMidRunStopsTheRunAndTheSessionGivesBackWhatItCanReach()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Verbose(Driving(world, carla), out List<string> console);

        SumoDriveSession session = SumoDriveSession.Start(options);
        var dropped = new IOException("Unable to write data to the transport connection.");
        AggregateException shutdown;
        try
        {
            AdvanceUntilABodyIsHeld(session, carla);
            carla.Sever(dropped);

            CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
                () => session.Advance());

            _output.WriteLine(refused.Message);
            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Equal(CoSimStopCause.WorldConnectionLost, refused.Cause);
            Assert.Same(dropped, refused.InnerException);
        }
        finally
        {
            shutdown = Assert.Throws<AggregateException>(session.Dispose);
        }

        // What only the server could hold is named, each with the connection's own failure inside it;
        // what is this process's own was given back regardless.
        foreach (Exception failure in shutdown.InnerExceptions)
        {
            _output.WriteLine(failure.Message);
            Assert.Same(dropped, failure.InnerException);
        }

        Assert.Equal(
            ["Could not give back the world's sun", "Could not destroy the bodies the session spawned",
             "Could not draw the rendering layers again", "Could not give back the world's settings"],
            shutdown.InnerExceptions.Select(failure => failure.Message.Split(':')[0]));
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
        Assert.True(SpinUntil(() => console.Any(line => line.Contains("TraCI requested termination"))),
                    "SUMO was not told the session was over");
    }

    [RequiresSumoFact]
    public void AConnectionThatDropsWhileTheClockIsTakenIsRefusedAtLaunchAndSumoIsStopped()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Verbose(Driving(world, carla), out List<string> console);
        var dropped = new IOException("An existing connection was forcibly closed by the remote host.");
        carla.SeverAt(nameof(ICarlaWorld.WriteLayerVisible), dropped);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Launch, refused.Stage);
        Assert.Equal(CoSimStopCause.WorldConnectionLost, refused.Cause);
        Assert.Same(dropped, refused.InnerException);
        Assert.Contains("taking the world's clock and rendering layers", refused.Message);

        // The clock was taken before the connection dropped and cannot be given back over it; that is
        // named, and SUMO -- this process's own -- was stopped regardless.
        Exception settings = Assert.Single(refused.GiveBackFailures);
        Assert.StartsWith("Could not give back the world's settings", settings.Message);
        Assert.Same(dropped, settings.InnerException);
        Assert.True(SpinUntil(() => console.Any(line => line.Contains("TraCI requested termination"))),
                    "SUMO was not stopped");
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
    }

    [RequiresSumoFact]
    public void AConnectionThatDropsWhileTheSunIsBoundIsRefusedAtPreRollAndTheLeaseIsGivenBack()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Verbose(Driving(world, carla), out List<string> console);
        var dropped = new IOException("Unable to read data from the transport connection.");
        carla.SeverAt(nameof(ICarlaWorld.WriteSolarEpoch), dropped);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.PreRoll, refused.Stage);
        Assert.Equal(CoSimStopCause.WorldConnectionLost, refused.Cause);
        Assert.Same(dropped, refused.InnerException);
        Assert.Equal(["Could not draw the rendering layers again", "Could not give back the world's settings"],
                     refused.GiveBackFailures.Select(failure => failure.Message.Split(':')[0]));
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
        Assert.True(SpinUntil(() => console.Any(line => line.Contains("TraCI requested termination"))),
                    "SUMO was not stopped");
    }

    [Fact]
    public void AConnectionThatDropsWhileTheLoadedWorldIsReadIsRefusedAtValidation()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        carla.SeverAt(nameof(ICarlaWorld.DescribeLoadedWorld), new IOException("connection refused"));

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(Driving(world, carla)));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Equal(CoSimStopCause.WorldConnectionLost, refused.Cause);
        Assert.IsType<IOException>(refused.InnerException);
        Assert.Empty(refused.GiveBackFailures);
    }

    [Fact]
    public void AFileThatCannotBeReadIsNotMistakenForADroppedConnection()
    {
        // An IOException from a file is not the server going away, and is not reported as one.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla) with
        {
            CataloguePath = Path.Combine(_directory, "no-such-catalogue.json"),
        };

        Assert.Throws<FileNotFoundException>(() => SumoDriveSession.Start(options));
        Assert.False(carla.Severed);
    }

    [RequiresSumoFact]
    public void ARunThatHasStoppedIsNeverAdvancedAgain()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        using SumoDriveSession session = SumoDriveSession.Start(Driving(world, carla));
        AdvanceUntilABodyIsHeld(session, carla);

        carla.ProducesFrames = false;
        CoSimSessionRefusedException first = Assert.Throws<CoSimSessionRefusedException>(() => session.Advance());
        Assert.Equal(CoSimStopCause.WorldTickTimeout, first.Cause);
        long ticks = carla.Ticks;
        long steps = session.Report.SumoSteps;

        // Asked again, with the world producing frames once more: neither side moves.
        carla.ProducesFrames = true;
        CoSimSessionRefusedException again = Assert.Throws<CoSimSessionRefusedException>(() => session.Advance());

        Assert.Equal(CoSimStopCause.WorldTickTimeout, again.Cause);
        Assert.Equal(CoSimSessionStage.Window, again.Stage);
        Assert.Contains("has already stopped (world-tick-timeout)", again.Message);
        Assert.Equal(ticks, carla.Ticks);
        Assert.Equal(steps, session.Report.SumoSteps);
    }

    // -- A vehicle removed while held -------------------------------------------------------------

    [RequiresSumoFact]
    public void AVehicleTakenOutWhileItHoldsABodyGivesItBackAndNothingWritesToItAgain()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        List<RenderedVehicleInterval> released = [];
        Dictionary<string, long> releasedBeforeTick = [];
        SumoDriveSessionOptions options = Driving(world, carla);
        options.OnPose = poses.Add;
        options.OnRelease = interval =>
        {
            released.Add(interval);
            releasedBeforeTick[interval.VehicleId] = carla.Ticks;
        };

        using SumoDriveSession session = SumoDriveSession.Start(options);
        CoSimPoseRecord held = AdvanceUntilABodyIsHeld(session, carla, poses);
        string vehicle = held.Pose.VehicleId;
        uint body = held.Actor;

        // Taken out between two steps, as another client would: SUMO drops the subscription and lists
        // no arrival for it. The frames already buffered are ones in which it existed, so it is
        // rendered up to the last of them; the session learns of it at the next step.
        session.Sumo.Vehicles.Remove(vehicle);
        for (int step = 0; step < 1000 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        RenderedVehicleInterval gone = Assert.Single(released, interval => interval.VehicleId == vehicle);
        Assert.Equal(RenderSetReleaseReason.Vanished, gone.ReleaseReason);
        Assert.Equal(body, gone.Actor);
        Assert.Equal(1, session.Report.Releases[RenderSetReleaseReason.Vanished]);
        Assert.DoesNotContain(released, interval => interval.VehicleId != vehicle
                                                    && interval.ReleaseReason == RenderSetReleaseReason.Vanished);
        Assert.Contains(released, interval => interval.ReleaseReason == RenderSetReleaseReason.LeftTheSimulation);

        // From its release on, nothing is written for it: no pose is computed for it, its body goes
        // back to its slot at the head of the next batch, and every later transform to that body is the
        // pose of another vehicle the body was lent to.
        long from = releasedBeforeTick[vehicle];
        Assert.DoesNotContain(poses, record => record.Pose.VehicleId == vehicle && record.TickIndex >= from);
        HashSet<long> lentOn = poses
            .Where(record => record.Actor == body && record.TickIndex >= from)
            .Select(record => record.TickIndex)
            .ToHashSet();
        bool parked = false;
        foreach ((IReadOnlyList<Command> batch, long tick) in carla.DrivenBatches.Where(entry => entry.AtTick >= from))
        {
            for (int index = 0; index < batch.Count; index++)
            {
                if (batch[index] is not ApplyTransformCommand transform || transform.Actor != body)
                {
                    continue;
                }

                if (transform.Transform.Location.Z < -100f)
                {
                    // Back in its slot, at the head of the batch -- only other parkings come before
                    // it -- and told it is standing still. The first is this vehicle's release; any
                    // later one is the release of a vehicle it was lent to afterwards.
                    Assert.All(batch.Take(index).OfType<ApplyTransformCommand>(),
                               earlier => Assert.True(earlier.Transform.Location.Z < -100f));
                    Assert.Equal(new Vector3D(0f, 0f, 0f),
                                 Assert.IsType<ApplyTargetVelocityCommand>(batch[index + 1]).Velocity);
                    parked |= tick == from;
                    continue;
                }

                Assert.Contains(tick, lentOn);
            }
        }

        Assert.True(parked, "the body was not written back to its slot in the batch after its release");
    }

    // -- Route errors and insertion ---------------------------------------------------------------

    [RequiresSumoFact]
    public void AScenarioThatTellsSumoToCarryOnPastARouteItCannotFollowIsRefusedBeforeSumoStarts()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Verbose(Driving(world, carla), out List<string> console) with
        {
            ScenarioPath = FixtureScenario("<ignore-route-errors value=\"true\"/>", verbose: true),
        };
        options.SumoOutput = console.Add;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Contains("sets ignore-route-errors to 'true'", refused.Message);
        Assert.Contains("SUMO has not been started", refused.Message);
        Assert.Empty(console);
        Assert.Empty(carla.SettingsWrites);
    }

    [RequiresSumoFact]
    public void ARouteSumoCannotFollowStopsTheRunQuotingSumo()
    {
        // The same route, with SUMO left to stop at it as it does by default.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla) with { ScenarioPath = DisconnectedRouteScenario() };

        SumoDriveSession session = SumoDriveSession.Start(options);
        try
        {
            CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(() =>
            {
                for (int step = 0; step < 2000; step++)
                {
                    session.Advance();
                }
            });

            _output.WriteLine(refused.Message);
            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Equal(CoSimStopCause.SumoConnectionLost, refused.Cause);
            Assert.Contains("Vehicle 'broken' has no valid route. No connection between edge 'ahead' and edge "
                            + "'approach'.", refused.Message);
            Assert.Equal(CoSimStopCause.SumoConnectionLost, session.Report.Stopped!.Cause);
        }
        finally
        {
            session.Dispose();
        }

        AssertGivenBack(carla, before, options);
    }

    [RequiresSumoFact]
    public void AVehicleSumoGivesUpInsertingIsRecordedWhenItLeavesTheQueue()
    {
        using SyntheticWorld world = Fixture();
        List<VehicleNotInserted> handed = [];
        SumoDriveSessionOptions options = Counting(world) with { ScenarioPath = BlockedInsertionScenario() };
        options.OnVehicleNotInserted = handed.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 200 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());

        // Measured: 'stuck' waits in SUMO's queue from 1.05 s and is gone from it at 4.1 s, having
        // waited past max-depart-delay, with nothing on SUMO's console and no state change.
        VehicleNotInserted dropped = Assert.Single(handed);
        Assert.Equal("stuck", dropped.VehicleId);
        Assert.Equal(4.05, dropped.WaitingAtSeconds, 9);
        Assert.Equal(4.10, dropped.GoneAtSeconds, 9);
        Assert.Equal(1, session.Report.VehiclesNotInserted);
        Assert.Equal(dropped, Assert.Single(session.Report.VehiclesNotInsertedSamples));
        Assert.Equal(0, session.Report.VehiclesAwaitingInsertion);
        Assert.Contains("not inserted       1 vehicle(s) SUMO gave up inserting", session.Report.ToString());
    }

    [RequiresSumoFact]
    public void AVehicleDroppedOnTheStepAfterTheFastForwardIsRecorded()
    {
        // Fast-forwarded to the last frame that still has 'stuck' waiting: the queue the fast-forward
        // left is what its disappearance on the next step is noticed against.
        using SyntheticWorld world = Fixture();
        List<VehicleNotInserted> handed = [];
        SumoDriveSessionOptions options = Counting(world) with { ScenarioPath = BlockedInsertionScenario() };
        options.WarmUpToSimulatedSecond = 4.05;
        options.OnVehicleNotInserted = handed.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);

        VehicleNotInserted dropped = Assert.Single(handed);
        Assert.Equal(("stuck", 4.05, 4.10), (dropped.VehicleId, dropped.WaitingAtSeconds, dropped.GoneAtSeconds));
    }

    [RequiresSumoFact]
    public void AScenarioWhoseEveryVehicleIsInsertedRecordsNone()
    {
        using SyntheticWorld world = Fixture();
        List<VehicleNotInserted> handed = [];
        SumoDriveSessionOptions options = Counting(world);
        options.OnVehicleNotInserted = handed.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.Empty(handed);
        Assert.Equal(0, session.Report.VehiclesNotInserted);
    }

    // -- Collisions -------------------------------------------------------------------------------

    [RequiresSumoFact]
    public void ACollisionIsRecordedAsOneSpanNamingBothBodiesAndTheRunGoesOn()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CollisionSpan> spans = [];

        // The fixture carries warn, as a capture scenario does; SUMO's default would move the collider on.
        SumoDriveSessionOptions options = Driving(world, carla);
        options.OnCollision = spans.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.Equal("warn", session.Report.CollisionHandling.Action);

        // Until 'goer' has departed and 'turner', ahead of it, holds a body.
        for (int step = 0; step < 200 && session.Advance(); step++)
        {
            if (session.RenderedVehicleIds.Contains("turner") && session.Sumo.Time >= 2.1)
            {
                break;
            }
        }

        // 'goer' put right behind 'turner' in its lane, overlapping its rear by two metres, as a
        // client moving a vehicle would.
        (double x, double y) = session.Sumo.Vehicles.Position("turner");
        session.Sumo.Vehicles.MoveToXY("goer", "approach", 0, x, y - 5.0);
        double movedAt = session.Sumo.Time;
        for (int step = 0; step < 200; step++)
        {
            Assert.True(session.Advance(), "the run did not go on after the collision");
            if (spans.Count > 0)
            {
                break;
            }
        }

        _output.WriteLine(session.Report.ToString());
        CollisionSpan span = Assert.Single(spans);
        Assert.Equal(("goer", "turner"), (span.Collision.ColliderId, span.Collision.VictimId));
        Assert.Equal("collision", span.Collision.Kind);
        Assert.Equal("approach_0", span.Collision.LaneId);
        Assert.Equal(movedAt + session.Clock.SumoStepSeconds, span.BeganAtSeconds, 9);
        Assert.True(span.EndedAtSeconds > span.BeganAtSeconds + session.Clock.SumoStepSeconds,
                    "the collision lasted one step, so one span per step would not have shown");
        Assert.NotEqual(0u, span.ColliderActor);
        Assert.NotEqual(0u, span.VictimActor);
        Assert.NotEqual(span.ColliderActor, span.VictimActor);
        Assert.Equal(1, session.Report.Collisions);
        Assert.Null(session.Report.Stopped);

        // SUMO said it too, in its own words, and the report kept them.
        Assert.True(SpinUntil(() => session.Report.SumoWarnings > 0), "no warning was counted");
        Assert.Contains(session.Report.SumoWarningSamples, line => line.Contains("collision with vehicle 'turner'"));
    }

    [RequiresSumoFact]
    public void ARunWithNoCollisionRecordsNoneAndSaysWhatSumoWouldHaveDone()
    {
        using SyntheticWorld world = Fixture();
        List<CollisionSpan> spans = [];
        SumoDriveSessionOptions options = Counting(world);
        options.OnCollision = spans.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.Empty(spans);
        Assert.Equal(0, session.Report.Collisions);

        // The fixture names warn, which governs, and the report says so.
        Assert.Contains("collisions         registered, warned and carried on (collision.action 'warn')",
                        session.Report.ToString());
    }

    // -- A world with no sun ----------------------------------------------------------------------

    [RequiresSumoFact]
    public void ASunThatGoesAwayMidRunStopsTheRunInTheWindowAndTheWorldIsGivenBack()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);

        SumoDriveSession session = SumoDriveSession.Start(options);
        try
        {
            AdvanceUntilABodyIsHeld(session, carla);
            carla.Sun = null;

            SolarAuditFailedException refused = Assert.Throws<SolarAuditFailedException>(() => session.Advance());

            _output.WriteLine(refused.Message);
            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Equal(CoSimStopCause.SolarStateDisagreement, refused.Cause);
            Assert.Contains("published no sun", refused.Message);
            Assert.Equal(CoSimStopCause.SolarStateDisagreement, session.Report.Stopped!.Cause);
        }
        finally
        {
            // Nothing in the world holds the session's sun any more, so there is nothing to give back,
            // and the shutdown does not fail for it.
            session.Dispose();
        }

        Assert.True(session.Report.Sun!.SunGoneWhenGivenBack);
        Assert.Equal(before, carla.Settings);
        Assert.All(carla.Batches[^1], command => Assert.IsType<DestroyActorCommand>(command));
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
        Assert.All(carla.LayerWrites.Skip(2), write => Assert.True(write.Visible));
    }

    [RequiresSumoFact]
    public void AnAdvancingSunThatGoesAwayStopsTheRunAtTheWriteBeforeTheFrameIsRendered()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla);
        options.Illumination = IlluminationPolicy.Advance(1.0);

        SumoDriveSession session = SumoDriveSession.Start(options);
        try
        {
            AdvanceUntilABodyIsHeld(session, carla);
            long ticks = carla.Ticks;
            carla.Sun = null;

            CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(() => session.Advance());

            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Equal(CoSimStopCause.SolarStateDisagreement, refused.Cause);
            Assert.Contains("The world refused the sun written for the frame", refused.Message);
            Assert.Equal(ticks, carla.Ticks);
        }
        finally
        {
            session.Dispose();
        }

        Assert.True(session.Report.Sun!.SunGoneWhenGivenBack);
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
    }

    [RequiresSumoFact]
    public void ASnapshotThatStopsCarryingTheSunStopsTheRunAndTheSunIsStillGivenBack()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);

        SumoDriveSession session = SumoDriveSession.Start(options);
        try
        {
            AdvanceUntilABodyIsHeld(session, carla);
            carla.ObserverPublishesNoSun = true;

            SolarAuditFailedException refused = Assert.Throws<SolarAuditFailedException>(() => session.Advance());
            Assert.Equal(CoSimSessionStage.Window, refused.Stage);
            Assert.Null(refused.Sample);
        }
        finally
        {
            session.Dispose();
        }

        Assert.False(session.Report.Sun!.SunGoneWhenGivenBack);
        AssertGivenBack(carla, before, options);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still open somewhere is not worth failing a test over; the temporary directory
            // is the operating system's to clean up.
        }
    }

    // -- helpers ----------------------------------------------------------------------------------

    /// <summary>
    /// The world is as the session found it: its settings, its layers drawn, every body destroyed, the
    /// sun as it was found, and the population lease free.
    /// </summary>
    private static void AssertGivenBack(RecordedWorld carla, EpisodeSettings before, SumoDriveSessionOptions options)
    {
        Assert.Equal(before, carla.Settings);
        Assert.Equal(4, carla.LayerWrites.Count);
        Assert.All(carla.LayerWrites.Skip(2), write => Assert.True(write.Visible));
        Assert.NotEmpty(carla.Spawned);
        Assert.All(carla.Batches[^1], command => Assert.IsType<DestroyActorCommand>(command));
        Assert.Equal(carla.Spawned.Count, carla.Batches[^1].Count);
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
        Assert.Equal((13.0, 2019, 9, 21), (carla.Sun!.SolarTime, carla.Sun.Year, carla.Sun.Month, carla.Sun.Day));
    }

    /// <summary>Advance until some vehicle holds a body, answering the first pose written to one.</summary>
    private static CoSimPoseRecord AdvanceUntilABodyIsHeld(SumoDriveSession session, RecordedWorld carla,
                                                           List<CoSimPoseRecord>? poses = null)
    {
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
            if (poses is null ? carla.Spawned.Count > 0 : poses.Any(record => record.Actor != 0))
            {
                return poses?.First(record => record.Actor != 0) ?? default;
            }
        }

        throw new InvalidOperationException("no vehicle was ever given a body");
    }

    /// <summary>
    /// The refusal the next advance raises, waited for no longer than <paramref name="limit"/>, so a
    /// session that never gives up on a silent SUMO fails the test rather than hanging it.
    /// </summary>
    private static CoSimSessionRefusedException RefusalWithin(SumoDriveSession session, TimeSpan limit)
    {
        Task<bool> advance = Task.Run(session.Advance);
        if (!SpinWait.SpinUntil(() => advance.IsCompleted, limit))
        {
            throw new TimeoutException($"The session was still waiting on SUMO after {limit.TotalSeconds} s.");
        }

        Assert.True(advance.IsFaulted, "the advance returned rather than refusing");
        return Assert.IsType<CoSimSessionRefusedException>(advance.Exception!.InnerException);
    }

    private static bool SpinUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static SyntheticWorld Fixture() =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

    private static SumoDriveSessionOptions Counting(SyntheticWorld world) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

    private static SumoDriveSessionOptions Driving(SyntheticWorld world, RecordedWorld carla)
    {
        SumoDriveSessionOptions options = Counting(world);
        options.TickWorld = null;
        options.World = carla;
        return options;
    }

    /// <summary>The same session on the fixture scenario with SUMO's verbose output on, captured.</summary>
    private SumoDriveSessionOptions Verbose(SumoDriveSessionOptions options, out List<string> console)
    {
        List<string> lines = [];
        console = lines;
        SumoDriveSessionOptions verbose = options with { ScenarioPath = FixtureScenario(string.Empty, verbose: true) };
        verbose.SumoOutput = line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        };
        return verbose;
    }

    /// <summary>
    /// The fixture scenario beside copies of its network and routes, with <paramref name="processing"/>
    /// added to its processing options.
    /// </summary>
    private string FixtureScenario(string processing, bool verbose = false)
    {
        string configuration = File.ReadAllText(CoSimFixtures.RightAngleTurnScenario)
            .Replace("<time-to-teleport value=\"-1\"/>", "<time-to-teleport value=\"-1\"/>\n        " + processing,
                     StringComparison.Ordinal);
        if (verbose)
        {
            configuration = configuration.Replace("<report>", "<report>\n        <verbose value=\"true\"/>",
                                                  StringComparison.Ordinal);
        }

        return Scenario(configuration, "RightAngleTurn.rou.xml",
                        File.ReadAllText(Path.ChangeExtension(CoSimFixtures.RightAngleTurnScenario, ".rou.xml")));
    }

    /// <summary>
    /// A scenario whose third vehicle is routed from <c>ahead</c> to <c>approach</c>, which no
    /// connection joins. Measured: SUMO stops at it with an error naming both edges, and under
    /// <c>ignore-route-errors</c> instead keeps the vehicle standing at the end of <c>ahead</c> and says
    /// nothing.
    /// </summary>
    private string DisconnectedRouteScenario() =>
        Scenario(File.ReadAllText(CoSimFixtures.RightAngleTurnScenario)
                     .Replace("<step-length value=\"0.05\"/>",
                              "<step-length value=\"0.05\"/>\n        <route-steps value=\"5\"/>",
                              StringComparison.Ordinal),
                 "RightAngleTurn.rou.xml",
                 "<routes>\n"
                 + "    <vType id=\"measured_truck\" vClass=\"truck\" length=\"7.0184\" width=\"2.5074\" "
                 + "maxSpeed=\"30.00\" sigma=\"0\" tau=\"1.0\"><param key=\"carla:blueprint\" "
                 + "value=\"vehicle.fuso.mitsubishi\"/></vType>\n"
                 + "    <vehicle id=\"early\" type=\"measured_truck\" depart=\"0\" departLane=\"0\">"
                 + "<route edges=\"approach ahead\"/></vehicle>\n"
                 + "    <vehicle id=\"fence\" type=\"measured_truck\" depart=\"20\" departLane=\"0\">"
                 + "<route edges=\"approach ahead\"/></vehicle>\n"
                 + "    <vehicle id=\"broken\" type=\"measured_truck\" depart=\"31\" departLane=\"0\">"
                 + "<route edges=\"ahead approach\"/></vehicle>\n"
                 + "</routes>\n");

    /// <summary>
    /// A scenario in which a vehicle cannot be inserted: another stands at the start of its lane for the
    /// whole run, and <c>max-depart-delay</c> is three seconds.
    /// </summary>
    private string BlockedInsertionScenario() =>
        Scenario(File.ReadAllText(CoSimFixtures.RightAngleTurnScenario)
                     .Replace("<time-to-teleport value=\"-1\"/>",
                              "<time-to-teleport value=\"-1\"/>\n        <max-depart-delay value=\"3\"/>",
                              StringComparison.Ordinal),
                 "RightAngleTurn.rou.xml",
                 "<routes>\n"
                 + "    <vType id=\"measured_truck\" vClass=\"truck\" length=\"7.0184\" width=\"2.5074\" "
                 + "maxSpeed=\"30.00\" sigma=\"0\" tau=\"1.0\"><param key=\"carla:blueprint\" "
                 + "value=\"vehicle.fuso.mitsubishi\"/></vType>\n"
                 + "    <vehicle id=\"blocker\" type=\"measured_truck\" depart=\"0\" departLane=\"0\" "
                 + "departPos=\"10\"><route edges=\"approach ahead\"/>"
                 + "<stop lane=\"approach_0\" endPos=\"12\" duration=\"1000\"/></vehicle>\n"
                 + "    <vehicle id=\"stuck\" type=\"measured_truck\" depart=\"1\" departLane=\"0\" "
                 + "departPos=\"base\"><route edges=\"approach ahead\"/></vehicle>\n"
                 + "    <vehicle id=\"free\" type=\"measured_truck\" depart=\"2\" departLane=\"1\">"
                 + "<route edges=\"approach ahead\"/></vehicle>\n"
                 + "</routes>\n");

    /// <summary>A configuration beside the fixture network and the named route file; its full path.</summary>
    private string Scenario(string configuration, string routeFileName, string routes)
    {
        string directory = Path.Combine(_directory, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.Copy(CoSimFixtures.RightAngleTurnNetwork, Path.Combine(directory, "RightAngleTurn.net.xml"));
        File.WriteAllText(Path.Combine(directory, routeFileName), routes);
        string path = Path.Combine(directory, "scenario.sumocfg");
        File.WriteAllText(path, configuration);
        return path;
    }
}
