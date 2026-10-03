using CarlaNet.Recording;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The whole bridge, running with nothing applied: the clock, the frame check, the lease, the
/// subscription, the render set, the lookahead, the interpolation and the conversion.
/// </summary>
public sealed class SumoDriveSessionTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void ARunWithNoFleetComputesPosesAgreesWithSumoAndAppliesNothing()
    {
        // The fixture network projects as "!", which is netconvert's own way of saying it was given
        // no projection, so the world it is packaged with declares the same.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        List<CoSimPoseRecord> computed = [];
        List<RenderedVehicleInterval> released = [];
        long ticks = 0;

        using (SumoDriveSession session = SumoDriveSession.Start(Options(world, computed, released,
                                                                 () => { ticks++; return true; })))
        {
            for (int step = 0; step < 200 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());

            Assert.True(session.Report.PosesComputed > 0);
            Assert.Equal(ticks, session.Report.Ticks);

            // The conversion checking itself: the computed pose taken back to the bumper has to be
            // the point SUMO reported, to rounding.
            Assert.True(session.Report.WorstBumperResidualMetres < 1e-9,
                        $"bumper residual {session.Report.WorstBumperResidualMetres}");

            // And the lane the bridge read has to be the lane SUMO is driving on. This is the check
            // that answers whether the network in the package is the network in the simulation.
            Assert.True(session.Report.WorstLaneGeometryResidualMetres < 0.05,
                        $"lane residual {session.Report.WorstLaneGeometryResidualMetres} m");

            Assert.Contains(LaneInterpolationCase.SameLane, session.Report.InterpolationCases.Keys);
        }

        Assert.NotEmpty(computed);
        Assert.NotEmpty(released);
        Assert.All(computed, record => Assert.Equal("vehicle.fuso.mitsubishi", record.Pose.BlueprintId));
    }

    [RequiresSumoFact]
    public void TheVehicleWhoseTypeNamesNoMeasuredBodyIsNeverPosed()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        List<CoSimPoseRecord> computed = [];

        using SumoDriveSession session = SumoDriveSession.Start(Options(world, computed, [], () => true));
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.True(session.Report.VehicleTicksWithNoMeasuredBody > 0,
                    "the unrenderable vehicle never reached the render set, so nothing was refused");
        Assert.DoesNotContain(computed, record => record.Pose.VehicleId == "unrenderable");
    }

    [RequiresSumoFact]
    public void ASessionRefusesToStartWhileSomethingElseHoldsTheWorldSPopulation()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(world, [], [], () => true);

        using PopulationLease ambient = WorldDriveAuthority.ForWorld(options.WorldKey)
            .Acquire(PopulationMode.TrafficManagerAmbient, "TrafficController on the same world");

        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => SumoDriveSession.Start(options));
        Assert.Contains("TrafficController on the same world", refused.Message);
    }

    [RequiresSumoFact]
    public void ASessionRefusesAnAsynchronousWorldAndAWorldTheNetworkDoesNotBelongTo()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        SumoDriveSessionOptions asynchronous = Options(world, [], [], () => true) with
        {
            WorldIsSynchronous = false,
        };
        Assert.Throws<CoSimSessionRefusedException>(() => SumoDriveSession.Start(asynchronous));

        using SyntheticWorld elsewhere = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork,
            "+proj=tmerc +lat_0=39.59431 +lon_0=-104.88449 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 "
            + "+units=m +no_defs");
        CoSimSessionRefusedException mismatch = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(Options(elsewhere, [], [], () => true)));
        Assert.Contains("projects as", mismatch.Message);
    }

    [RequiresSumoFact]
    public void AWorldThatStopsTickingStopsTheRun()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        using SumoDriveSession session = SumoDriveSession.Start(
            Options(world, [], [], () => false));

        CoSimSessionRefusedException failed =
            Assert.Throws<CoSimSessionRefusedException>(() => session.Advance());
        Assert.Contains("no frame", failed.Message);
    }

    [NamedCoSimRunFact]
    public void ARunAgainstARealScenarioAndWorld()
    {
        List<CoSimPoseRecord> computed = [];
        var options = new SumoDriveSessionOptions(
            NamedCoSimRunFactAttribute.Scenario!,
            NamedCoSimRunFactAttribute.WorldPackage!,
            CoSimFixtures.VehicleCatalogue,
            "named://" + Guid.NewGuid().ToString("n"))
        {
            WarmUpToSimulatedSecond = NamedCoSimRunFactAttribute.WarmUp,
            SumoStepOverrideSeconds = NamedCoSimRunFactAttribute.StepLength,
            OnPose = computed.Add,
        };

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < NamedCoSimRunFactAttribute.Steps && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        _output.WriteLine($"poses logged       {computed.Count}");
        Assert.True(session.Report.PosesComputed > 0, "the render set never held a vehicle");
        Assert.True(session.Report.WorstBumperResidualMetres < 1e-6,
                    $"bumper residual {session.Report.WorstBumperResidualMetres}");
    }

    [RequiresSumoFact]
    public void AnAdmittedVehicleIsLentABodyAndGivesItBackWhenItLeaves()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<RenderedVehicleInterval> released = [];
        List<CoSimPoseRecord> computed = [];
        SumoDriveSessionOptions options = Options(world, computed, released, tick: null);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            Assert.True(session.Report.BodiesSpawned > 0, "no body was ever lent");
        }

        // One blueprint in the scenario, so every body is one; and each is spawned once, never
        // during a tick and never again.
        Assert.All(carla.Spawned, blueprint => Assert.Equal("vehicle.fuso.mitsubishi", blueprint));

        // Every vehicle that was rendered gave its body back, over an interval that runs forwards.
        Assert.NotEmpty(released);
        Assert.All(released, interval =>
            Assert.True(interval.ReleasedAtSeconds >= interval.AdmittedAtSeconds,
                        $"{interval.VehicleId} was released before it was admitted"));
        Assert.All(released.Where(interval => interval.VehicleId != "unrenderable"),
                   interval => Assert.NotEqual(0u, interval.Actor));

        // And the vehicle whose type names no measured body was admitted to the render set, was
        // never lent one, and is recorded as having held none. A body of some other shape would
        // have been available and is never a substitute.
        RenderedVehicleInterval unrenderable =
            Assert.Single(released, interval => interval.VehicleId == "unrenderable");
        Assert.Equal(0u, unrenderable.Actor);
        Assert.DoesNotContain(computed, record => record.Pose.VehicleId == "unrenderable");

        // And the poses name the body they were written to.
        Assert.All(computed, record => Assert.NotEqual(0u, record.Actor));

        // The session's end is the one moment a pooled actor is destroyed.
        Assert.All(carla.Batches[^1], command => Assert.IsType<DestroyActorCommand>(command));
        Assert.Equal(carla.Spawned.Count, carla.Batches[^1].Count);
    }

    [RequiresSumoFact]
    public void EveryTickWritesEachPoseThenItsVelocityInOneBatchAndOnlyLampsBeside()
    {
        // A surface that climbs both eastwards and southwards, so every arm of the cross has a
        // vertical velocity for the batch to carry.
        using SyntheticWorld world = SyntheticWorld.Write(
            at => (0.04 * at.X) + (0.03 * at.Y), CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = Options(world, computed, released, tick: null);
        options.World = carla;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());

        // One round trip per tick that had anything to write, and never more: the whole point of
        // the batch is that N vehicles cost one.
        Assert.True(session.Report.Batches > 0, "nothing was ever written");
        Assert.True(session.Report.Batches <= session.Report.Ticks,
                    $"{session.Report.Batches} batches for {session.Report.Ticks} ticks");
        Assert.Equal(0, session.Report.BatchFailures);

        // What a tick writes is pairs: a transform, then a target velocity for the same body, with a
        // body's lamps beside them where they are written (their own tests say when). A posed body's
        // velocity is the one its own pose carries, climb included; a parked body's is zero. Each pose
        // is matched to the tick it was computed on, which is the tick the world was on when the batch
        // arrived.
        Dictionary<(long Tick, uint Actor), VehiclePose> posed = computed
            .Where(record => record.Actor != 0)
            .ToDictionary(record => (record.TickIndex, record.Actor), record => record.Pose);
        int poses = 0;
        int parkings = 0;
        int lamps = 0;
        foreach ((IReadOnlyList<Command> written, long tick) in carla.DrivenBatches)
        {
            lamps += written.Count(command => command is SetVehicleLightStateCommand);
            List<Command> batch = [.. written.Where(command => command is not SetVehicleLightStateCommand)];
            Assert.Equal(0, batch.Count % 2);
            for (int index = 0; index < batch.Count; index += 2)
            {
                ApplyTransformCommand transform = Assert.IsType<ApplyTransformCommand>(batch[index]);
                ApplyTargetVelocityCommand velocity =
                    Assert.IsType<ApplyTargetVelocityCommand>(batch[index + 1]);
                Assert.Equal(transform.Actor, velocity.Actor);

                if (posed.TryGetValue((tick, transform.Actor), out VehiclePose pose)
                    && transform.Transform == ExpectedTransform(pose))
                {
                    Assert.Equal(ExpectedVelocity(pose), velocity.Velocity);
                    poses++;
                }
                else
                {
                    Assert.True(transform.Transform.Location.Z < -100f,
                                $"a transform at tick {tick} is neither a pose nor a parking slot");
                    Assert.Equal(new Vector3D(0f, 0f, 0f), velocity.Velocity);
                    parkings++;
                }
            }
        }

        // Every pose that had a body to go to, every body given back, their lamps, and nothing else.
        Assert.Equal(posed.Count, poses);
        Assert.Equal(released.Count(each => each.Actor != 0), parkings);
        Assert.Equal(session.Report.LampCommandsWritten, lamps);
        Assert.Equal((2 * (poses + parkings)) + lamps, session.Report.CommandsWritten);

        // The slope reached the batch, both ways.
        Assert.Contains(posed.Values, pose => pose.VelocityZ > 0.1);
        Assert.Contains(posed.Values, pose => pose.VelocityZ < -0.1);

        // And no velocity reached a body whose physics was still on: the pool disables it before a
        // body is ever lent, and a velocity written before that goes to the simulating body.
        Assert.Equal(0, carla.VelocityWritesWhileSimulating);
    }

    [RequiresSumoFact]
    public void ABodyWhoseVehicleDrivesOffTheGroundIsLeftWhereItWasAndToldItIsStill()
    {
        // A 26 m grid from -76 m to 80 m under a network whose arms reach 100 m. That is inside the
        // one cell a session lets a network overhang its surface, so the session starts, and the
        // outer end of every arm has no ground under it. Every vehicle SUMO has is rendered, so a
        // vehicle still holds its body as it drives off the surface.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!", cellSize: 26.0, min: -76.0, cells: 7);

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        Assert.True(session.Report.PosesRefusedForMissingGround > 0, "no vehicle ever left the ground");

        // A body with no pose this tick gets a velocity and no transform: zero, and it stays where
        // its last pose put it. It had been moving -- it was given a non-zero velocity on an earlier
        // tick -- so the zero is the hold and not a default.
        Dictionary<uint, bool> movedBefore = [];
        int holds = 0;
        foreach ((IReadOnlyList<Command> batch, long tick) in carla.DrivenBatches)
        {
            for (int index = 0; index < batch.Count; index++)
            {
                if (batch[index] is not ApplyTargetVelocityCommand velocity)
                {
                    continue;
                }

                bool afterItsTransform = index > 0
                                         && batch[index - 1] is ApplyTransformCommand transform
                                         && transform.Actor == velocity.Actor;
                if (afterItsTransform)
                {
                    movedBefore[velocity.Actor] = movedBefore.GetValueOrDefault(velocity.Actor)
                                                  || velocity.Velocity != new Vector3D(0f, 0f, 0f);
                    continue;
                }

                holds++;
                Assert.Equal(new Vector3D(0f, 0f, 0f), velocity.Velocity);
                Assert.True(movedBefore.GetValueOrDefault(velocity.Actor),
                            $"actor {velocity.Actor} was held still at tick {tick} before it ever moved");
                Assert.DoesNotContain(batch, command => command is ApplyTransformCommand held
                                                        && held.Actor == velocity.Actor);
            }
        }

        Assert.True(holds > 0, "no body was ever held still");
    }

    [RequiresSumoFact]
    public void ABodyGivenBackIsWrittenToItsSlotInTheNextTickSBatch()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = Options(world, [], released, tick: null);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            // Run until the first vehicle has been released, then one more step so the parking pose
            // it queued is written.
            for (int step = 0; step < 400 && released.Count == 0 && session.Advance(); step++)
            {
            }

            Assert.NotEmpty(released);
            RenderedVehicleInterval first = released[0];

            // Given back, and not yet written: it still reports the speed it was last driven at.
            Vector3D driven = carla.ObservedVelocity(first.Actor)!.Value;
            Assert.True(Math.Sqrt((driven.X * driven.X) + (driven.Y * driven.Y)) > 1.0,
                        $"the body was reporting {driven} before it was parked");

            session.Advance();

            Transform? resting = carla.ObservedTransform(first.Actor);
            Assert.NotNull(resting);
            Transform parked = resting.Value;

            // The slot is below the surface and beyond it, which is where a parked body belongs and
            // nowhere a camera aimed at the road can frame.
            Assert.True(parked.Location.Z < -100f, $"parked at z {parked.Location.Z}");
            Assert.True(parked.Location.X > 100f, $"parked at x {parked.Location.X}");

            // And it reports standing still, rather than its last speed from beyond the sandbox for
            // as long as it stays parked.
            Assert.Equal(new Vector3D(0f, 0f, 0f), carla.ObservedVelocity(first.Actor));
        }
    }

    [RequiresSumoFact]
    public void TheRoadAndSignalLayersAreHiddenBeforeTheFirstTickAndDrawnAgainAtTheEnd()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            // Written before the world had produced a single frame, so no capture of this run can
            // contain either of them and no two frames of it can disagree about whether they did.
            Assert.Equal(2, carla.LayerWrites.Count);
            Assert.All(carla.LayerWrites, write => Assert.Equal(0, write.AtTick));
            Assert.All(carla.LayerWrites, write => Assert.False(write.Visible));

            // And the run says so itself, because a frame with no road mesh in it and a frame of a
            // world that has no road mesh look the same.
            Assert.False(session.Report.LayerVisibility[LayerVisibilityLease.RoadLayer]);
            Assert.False(session.Report.LayerVisibility[LayerVisibilityLease.SignalLayer]);

            for (int step = 0; step < 40 && session.Advance(); step++)
            {
            }

            // Fixed for the run: nothing wrote a layer again once the world started producing
            // frames.
            Assert.All(carla.LayerWrites, write => Assert.Equal(0, write.AtTick));
        }

        Assert.Equal(4, carla.LayerWrites.Count);
        Assert.All(carla.LayerWrites.Skip(2), write => Assert.True(write.Visible));
    }

    [RequiresSumoFact]
    public void ASessionAskedToRenderTheRoadMeshRendersIt()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.RoadLayerVisible = true;

        using SumoDriveSession session = SumoDriveSession.Start(options);

        Assert.True(session.Report.LayerVisibility[LayerVisibilityLease.RoadLayer]);
        Assert.False(session.Report.LayerVisibility[LayerVisibilityLease.SignalLayer]);
        Assert.Contains(carla.LayerWrites,
                        write => write.Layer == LayerVisibilityLease.RoadLayer && write.Visible);
    }

    [RequiresSumoFact]
    public void ASessionThatFailsMidRunStillGivesTheWorldBackAsItFoundIt()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.True(carla.Settings.SynchronousMode);

        try
        {
            // Run far enough in that the pool holds bodies, then drop the connection under it.
            for (int step = 0; step < 60 && session.Advance(); step++)
            {
            }

            Assert.NotEmpty(carla.Spawned);
            carla.ThrowOnTick = new IOException("the connection to the simulator was dropped");
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            Assert.Fail("the run should have failed on the dropped connection");
        }
        catch (CoSimSessionRefusedException refused)
        {
            // A dropped connection is the run's refusal, carrying the connection's own failure.
            Assert.IsType<IOException>(refused.InnerException);
            Assert.Equal(CoSimStopCause.WorldConnectionLost, refused.Cause);

            // What an operator's harness does next, and the only thing it can do.
            session.Dispose();
        }

        // The world is asynchronous again, and every body the session spawned is gone. A session
        // that failed must not leave an editor waiting for a tick from a process that has stopped.
        Assert.Equal(before, carla.Settings);
        Assert.False(carla.Settings.SynchronousMode);
        Assert.All(carla.Batches[^1], command => Assert.IsType<DestroyActorCommand>(command));

        // And the road network is drawn again. Layer visibility is global state of the same class
        // as the world's clock: a run that hid the road mesh and threw must not leave an operator
        // looking at a world with no roads in it.
        Assert.Equal(4, carla.LayerWrites.Count);
        Assert.All(carla.LayerWrites.Skip(2), write => Assert.True(write.Visible));
    }

    [RequiresSumoFact]
    public void ASessionThatNeverStartsLeavesTheWorldAsItFoundIt()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        // Something else holds the world's population, which is refused after the session has
        // already taken the world's clock.
        using PopulationLease ambient = WorldDriveAuthority.ForWorld(options.WorldKey)
            .Acquire(PopulationMode.TrafficManagerAmbient, "TrafficController on the same world");

        Assert.Throws<PopulationAuthorityHeldException>(() => SumoDriveSession.Start(options));
        Assert.Equal(before, carla.Settings);

        // The layers were written before the population was refused, so they too are given back.
        Assert.Equal(4, carla.LayerWrites.Count);
        Assert.All(carla.LayerWrites.Skip(2), write => Assert.True(write.Visible));
    }

    [RequiresSumoFact]
    public void TheSunIsBoundBeforeTheFirstTickToTheInstantTheFirstFrameRenders()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.WarmUpToSimulatedSecond = 2.0;
        options.Epoch = SolarEpoch.Declare("2026-03-21T07:00:00+03:30", 3.5, "2026-03-21T03:30:00Z",
                                           calendarAdvances: true, dstInEffect: false);
        options.Illumination = IlluminationPolicy.FreezeAtWindowStart();

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            // SUMO was fast-forwarded to two seconds and nothing has ticked yet, so the sun holds
            // the civil instant of the first frame the session will render, in the civil zone.
            Assert.Equal(0, carla.Ticks);
            Assert.All(carla.SolarWrites, write => Assert.Equal(0, write.AtTick));
            Assert.Equal((2026, 3, 21), (carla.Sun!.Year, carla.Sun.Month, carla.Sun.Day));
            Assert.Equal((7, 0, 2), SolarPositionModel.EngineClock(carla.Sun.SolarTime));
            Assert.Equal(3.5, carla.Sun.TimeZone);
            Assert.Equal("2026-03-21T07:00:02+03:30",
                         SolarEpoch.FormatCivil(session.Sun!.Declared.WindowOpenCivil));

            for (int step = 0; step < 40 && session.Advance(); step++)
            {
            }

            // Frozen: forty steps of ticks later the sun has not moved, and nothing wrote it again.
            Assert.Equal((7, 0, 2), SolarPositionModel.EngineClock(carla.Sun.SolarTime));
            Assert.Equal(2, carla.SolarWrites.Count);
        }

        // Given back as found, the class-default sun an unconfigured world holds.
        Assert.Equal((13.0, 2019, 9, 21, -5.0), (carla.Sun.SolarTime, carla.Sun.Year,
                                                 carla.Sun.Month, carla.Sun.Day, carla.Sun.TimeZone));
    }

    [RequiresSumoFact]
    public void ASessionThatFailsMidRunStillGivesTheSunBack()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Epoch = SolarLeaseTests.PortEpoch();
        options.Illumination = IlluminationPolicy.Advance(1.0);

        SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.False(carla.Sun!.Advancing);
        Assert.Equal(3.5, carla.Sun.TimeZone);
        try
        {
            carla.ThrowOnTick = new IOException("the connection to the simulator was dropped");
            session.Advance();
            Assert.Fail("the run should have failed on the dropped connection");
        }
        catch (CoSimSessionRefusedException refused) when (refused.InnerException is IOException)
        {
            session.Dispose();
        }

        Assert.Equal((13.0, 2019, 9, 21, -5.0, false), (carla.Sun.SolarTime, carla.Sun.Year,
                                                        carla.Sun.Month, carla.Sun.Day,
                                                        carla.Sun.TimeZone, carla.Sun.Advancing));
    }

    [RequiresSumoFact]
    public void AnAdvancingSessionWritesTheSunForEveryFrameBeforeItsCueAndHoldsAcrossTheMinute()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        // Simulated second zero is two seconds before 07:01 at the port, and the fixture steps at the
        // world's delta, so eighty ticks cross the minute the engine's own advance renders as 07:00:00.
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Epoch = SolarEpoch.Declare("2026-03-21T07:00:58+03:30", 3.5, "2026-03-21T03:30:58Z",
                                           calendarAdvances: true, dstInEffect: false);
        options.Illumination = IlluminationPolicy.Advance(1.0);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 80 && session.Advance(); step++)
        {
        }

        // The bind, then one write per tick, each arriving while the world was still on the tick
        // before the frame it was written for, and the engine's own advance off throughout.
        long ticks = session.Report.Ticks;
        Assert.True(ticks >= 60, $"{ticks} ticks");
        List<(string Call, long AtTick)> writes = [.. carla.SolarWrites];
        Assert.Equal(["set_solar_epoch", "set_time_advance"], writes.Take(2).Select(write => write.Call));
        Assert.Equal(ticks, writes.Skip(2).Count());
        Assert.Equal(Enumerable.Range(0, (int)ticks).Select(tick => ("set_solar_epoch", (long)tick)),
                     writes.Skip(2));
        Assert.False(carla.Sun!.Advancing);
        Assert.Equal(ticks, session.Sun!.FrameWrites);

        // Every tick audited and none stopped, the minute crossed on the clock the engine holds.
        SolarAudit audit = session.SunAudit!;
        Assert.Equal(ticks, audit.AuditedTicks);
        Assert.Null(audit.Failure);
        Assert.InRange(audit.WorstClock!.ClockResidualSeconds, -0.5, 0.5);
        Assert.Equal((7, 1, 0), (SolarPositionModel.EngineClock(audit.Last!.Observed.SolarTimeHours).Hour,
                                 SolarPositionModel.EngineClock(audit.Last.Observed.SolarTimeHours).Minute,
                                 0));
        Assert.Contains($"  written          for {ticks} frames", session.Report.ToString());
    }

    [RequiresSumoFact]
    public void EveryTickIsAuditedAndASunAnotherClientMovedStopsTheRun()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        SumoDriveSession session = SumoDriveSession.Start(options);
        try
        {
            for (int step = 0; step < 20 && session.Advance(); step++)
            {
            }

            // Every tick, not a sample of them, and the snapshot carried the corrected elevation on
            // every one.
            SolarAudit audit = session.SunAudit!;
            Assert.Equal(session.Report.Ticks, audit.AuditedTicks);
            Assert.Equal(audit.AuditedTicks, audit.TicksWithCorrectedElevation);
            Assert.NotNull(audit.AtWindowOpen);

            // A minute of clock moved by something other than the session.
            carla.Sun!.SolarTime += 1.0 / 60.0;
            SolarAuditFailedException failed =
                Assert.Throws<SolarAuditFailedException>(() => session.Advance());
            Assert.Equal(session.Report.Ticks, failed.Sample!.TickIndex);
        }
        finally
        {
            session.Dispose();
        }

        // Stopped, not corrected: the session wrote the sun when it bound it and when it gave it
        // back, and at no point in between.
        Assert.Equal(["set_solar_epoch", "set_time_advance", "set_solar_epoch", "set_time_advance"],
                     carla.SolarWrites.Select(write => write.Call));
        Assert.Equal((13.0, 2019, 9, 21), (carla.Sun.SolarTime, carla.Sun.Year, carla.Sun.Month,
                                           carla.Sun.Day));
    }

    [RequiresSumoFact]
    public void AWorldWhoseSunIsComputedElsewhereIsRefusedBeforeItRenders()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        // The package's origin is 0, 0; the world's georeference, and so its sun, is at the port.
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        carla.Sun!.Latitude = 27.15012;
        carla.Sun.Longitude = 56.18065;
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        SolarAuditFailedException failed =
            Assert.Throws<SolarAuditFailedException>(() => SumoDriveSession.Start(options));

        Assert.Contains("not the world package's origin", failed.Message);
        Assert.Equal(0, carla.Ticks);
        Assert.Equal(before, carla.Settings);
        Assert.Equal((13.0, 2019, 9, 21), (carla.Sun.SolarTime, carla.Sun.Year, carla.Sun.Month,
                                           carla.Sun.Day));
    }

    [RequiresSumoFact]
    public void EveryRenderedFrameCarriesItsDeclarationAndTheAuditOfItsOwnTick()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Epoch = SolarEpoch.Declare("2026-03-21T07:00:00+03:30", 3.5, "2026-03-21T03:30:00Z",
                                           calendarAdvances: true, dstInEffect: false);
        options.Illumination = IlluminationPolicy.FreezeAtWindowStart();

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 10 && session.Advance(); step++)
        {
        }

        // One declaration per rendered frame, keyed by the frame the tick produced.
        IIlluminationSource source = session.Illumination;
        Assert.Equal((ulong)session.Report.Ticks, source.NewestFrame);
        Assert.True(source.TryGetDeclaration(1, out IlluminationDeclaration first));
        Assert.True(source.TryGetDeclaration((ulong)session.Report.Ticks, out IlluminationDeclaration last));

        // Simulated second zero is 07:00 at the port. The first frame renders that instant; the
        // fixture steps at the world's delta, so the tenth frame renders nine ticks of 0.05 s later.
        Assert.Equal("freeze_at_window_start", first.Policy);
        Assert.True(first.EpochHonoured);
        Assert.True(first.Audited);
        Assert.Equal(options.Epoch.Digest, first.EpochDigest);
        Assert.Equal(10, session.Report.Ticks);
        Assert.Equal("2026-03-21T07:00:00+03:30", first.DeclaredCivil);
        Assert.Equal("2026-03-21T03:30:00Z", first.DeclaredUtc);
        Assert.Equal("2026-03-21T07:00:00+03:30", first.SunDeclared);
        Assert.Equal("2026-03-21T07:00:00.45+03:30", last.DeclaredCivil);
        Assert.Equal("2026-03-21T07:00:00+03:30", last.SunDeclared);

        // Both elevations, named, and which one the declaration is made against.
        Assert.Equal("refraction_corrected", first.DeclaredElevationKind);
        Assert.True(first.SunCorrectedElevationDeclaredDegrees > first.SunElevationDeclaredDegrees);
        Assert.Equal(0.001, first.ResidualClockSeconds!.Value, 6);
        Assert.True(first.ResidualDegrees < 1e-4);

        // And the run says the same thing once, for the whole run.
        string report = session.Report.ToString();
        Assert.Contains("epoch              t = 0 is 2026-03-21T07:00:00+03:30", report);
        Assert.Contains("illumination       freeze_at_window_start, date held; each frame lit by its "
                        + "declared civil instant", report);
        Assert.Contains("found holding    2019-09-21 13:00:00.000", report);
        Assert.Contains("deg refraction_corrected (geometric", report);
        Assert.Contains($"audit            {session.Report.Ticks} ticks", report);
        Assert.True(session.Report.WorstSolarResidualDegrees < 1e-4);
    }

    [RequiresSumoFact]
    public void AFrameUnderTheIgnorePolicySaysItsLightingHonoursNoEpoch()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Illumination = IlluminationPolicy.Ignore();

        using SumoDriveSession session = SumoDriveSession.Start(options);
        session.Advance();

        Assert.True(session.Illumination.TryGetDeclaration(1, out IlluminationDeclaration declared));
        Assert.Equal("ignore", declared.Policy);
        Assert.False(declared.EpochHonoured);
        Assert.False(declared.Audited);
        Assert.NotNull(declared.DeclaredCivil);
        Assert.Null(declared.ResidualDegrees);
        Assert.Contains("not bound, so not audited", session.Report.ToString());
        Assert.Null(session.Report.WorstSolarResidualDegrees);
    }

    [Fact]
    public void ASessionThatRendersAWorldAndDeclaresNoPolicyIsRefusedBeforeAnythingIsTouched()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Illumination = null;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        // The recommendation is named, and not applied.
        Assert.Contains("declares no illumination policy", refused.Message);
        Assert.Contains("freeze_at_window_start (recommended", refused.Message);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.LayerWrites);
        Assert.Empty(carla.SolarWrites);
    }

    [Fact]
    public void APolicyThatBindsTheSunNeedsAnEpochToBindItFrom()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Epoch = null;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Contains("declares no epoch", refused.Message);
        Assert.Empty(carla.SettingsWrites);
    }

    [RequiresSumoFact]
    public void TheIgnorePolicyLeavesTheSunAloneAndStillRefusesAWorldWithNone()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Epoch = null;
        options.Illumination = IlluminationPolicy.Ignore();

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            session.Advance();
            Assert.Null(session.Sun);
            Assert.Empty(carla.SolarWrites);
        }

        var sunless = new RecordedWorld { Sun = null, Loaded = world.AsLoaded() };
        options.World = sunless;
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        Assert.Contains("reports no sun", refused.Message);

        // Given back on the way out, as any other refusal after the world was taken.
        Assert.False(sunless.Settings.SynchronousMode);

        options.Illumination = IlluminationPolicy.Ignore(requireSun: false);
        using SumoDriveSession allowed = SumoDriveSession.Start(options);
        Assert.True(allowed.Advance());
    }

    [RequiresSumoFact]
    public void ASessionThatRendersNothingDeclaresNothing()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        SumoDriveSessionOptions options = Options(world, [], [], () => true);
        options.Epoch = null;
        options.Illumination = null;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.True(session.Advance());
        Assert.Null(session.Sun);
    }

    [RequiresSumoFact]
    public void ASessionGivenTwoWaysToAdvanceTheWorldIsRefused()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        SumoDriveSessionOptions options = Options(world, [], [], () => true);
        options.World = new RecordedWorld();

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        Assert.Contains("both a CARLA world and a tick delegate", refused.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(-0.25)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ARealTimeFactorThatDescribesNoPaceIsRefusedBeforeAnythingIsTouched(double factor)
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.RealTimeFactor = factor;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Contains("real-time factor", refused.Message);
        Assert.Contains("1.0 holds the world to the pace of real traffic", refused.Message);
        Assert.Equal(0, carla.Descriptions);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.LayerWrites);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void APacingWindowThatIsNotAPositiveLengthOfTimeIsRefused(double windowSeconds)
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        SumoDriveSessionOptions options = Options(world, [], [], () => true);
        options.PacingWindowSeconds = windowSeconds;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        Assert.Contains("pacing window", refused.Message);
    }

    [RequiresSumoFact]
    public void ASessionHeldToRealTimeCuesEveryWorldTickOnTheWallClockFromItsFirstTick()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var clock = new SteppedWallClock();
        List<double> cuedAt = [];
        SumoDriveSessionOptions options = Options(world, [], [], () =>
        {
            // The instant the cue went out, then the world's own four milliseconds on the frame.
            cuedAt.Add(clock.NowSeconds);
            clock.AdvanceMilliseconds(4);
            return true;
        });
        options.RealTimeFactor = 1.0;
        options.PacingWindowSeconds = 0.5;
        options.WallClock = clock;
        // Two world ticks per SUMO step, so a pace kept per step rather than per tick would show.
        options.SumoStepOverrideSeconds = 0.1;
        // Two simulated seconds of fast-forward before the first tick.
        options.WarmUpToSimulatedSecond = 2.0;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.Equal(2, session.Clock.WorldTicksPerSumoStep);
        for (int step = 0; step < 40 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        Assert.Equal(80, cuedAt.Count);

        // The fast-forward is not paced: the first cue went out at once, rather than two seconds
        // late for a schedule that started at simulated zero.
        Assert.Equal(0.0, cuedAt[0]);
        Assert.Equal(79, clock.Waits.Count);

        // Every world tick, not every SUMO step, went out one world delta after the one before.
        for (int cue = 0; cue < cuedAt.Count; cue++)
        {
            Assert.Equal(cue * 0.05, cuedAt[cue], 9);
        }

        RealTimePacer pacing = session.Report.Pacing;
        Assert.Equal(1.0, pacing.DeclaredFactor);
        Assert.Equal(1.0, pacing.AchievedFactor!.Value, 9);
        Assert.Equal(7, pacing.CompletedWindows);
        Assert.Equal(1.0, pacing.WorstWindowFactor!.Value, 9);
        Assert.Equal(0.0, pacing.WorstBehindScheduleSeconds);

        string report = session.Report.ToString();
        Assert.Equal(3.95, pacing.WallSeconds, 9);
        Assert.Contains("pacing             held to 1 x real time; achieved 1 x over ", report);
        Assert.Contains("  windows          7 of 0.5 s; last 1 x, worst 1 x", report);
        Assert.Contains("  behind schedule  0.000 s at the last tick, worst 0.000 s", report);
    }

    [RequiresSumoFact]
    public void AnUnpacedSessionWaitsForNothingAndStillSaysHowFastItWent()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var clock = new SteppedWallClock();
        SumoDriveSessionOptions options = Options(world, [], [], () =>
        {
            clock.AdvanceMilliseconds(5);
            return true;
        });
        options.WallClock = clock;
        options.PacingWindowSeconds = 0.1;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 40 && session.Advance(); step++)
        {
        }

        Assert.Empty(clock.Waits);
        RealTimePacer pacing = session.Report.Pacing;
        Assert.False(pacing.Paced);
        Assert.Equal(10.0, pacing.AchievedFactor!.Value, 9);
        Assert.Contains("pacing             not paced, as fast as the machine allows; achieved 10 x",
                        session.Report.ToString());
        Assert.DoesNotContain("behind schedule", session.Report.ToString());
    }

    [Fact]
    public void ASessionRefusesAPackageThatIsNotTheLoadedWorldAndTouchesNothing()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        // Another build of the same area: the same origin, grid and road network, and a ground
        // surface a metre higher across the eastern half.
        using SyntheticWorld rebuilt = SyntheticWorld.Write(
            at => at.X > 0.0 ? 1.0 : 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = rebuilt.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Contains("does not describe the world the server has loaded", refused.Message);
        Assert.Contains("the loaded world's bare-earth ground grid has SHA-1", refused.Message);
        Assert.Equal(1, carla.Descriptions);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.LayerWrites);
        Assert.Empty(carla.SolarWrites);
        Assert.Empty(carla.Spawned);

        // And its grids are not handed to the world's truth telemetry: they are not the world's. Nor
        // is the catalogue's table of kinds, from a session that never started.
        Assert.Empty(carla.Adoptions);
        Assert.Empty(carla.SpecialTypeAdoptions);
    }

    [RequiresSumoFact]
    public void AnAdmittedPackageSGridsAreHandedToTheTruthTelemetryOnceTheCheckHasPassed()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        using SumoDriveSession session = SumoDriveSession.Start(options);

        // Once, the package the session was given, and only after the world had described itself:
        // what is handed over is what the check has just shown the world to hold.
        (string package, int afterDescriptions) = Assert.Single(carla.Adoptions);
        Assert.Equal(world.PackagePath, package);
        Assert.Equal(1, afterDescriptions);

        for (int step = 0; step < 10 && session.Advance(); step++)
        {
        }

        Assert.Single(carla.Adoptions);
    }

    [RequiresSumoFact]
    public void TheCatalogueSKindsAreHandedToTheTruthTelemetryOnceThePackageIsAdmitted()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        using SumoDriveSession session = SumoDriveSession.Start(options);

        // Once, the session's own catalogue's whole table, and only after the world had described
        // itself: a body's truth then carries the kind its catalogue class curates (doc 06 D6.18).
        (IReadOnlyDictionary<string, string> kinds, int afterDescriptions) =
            Assert.Single(carla.SpecialTypeAdoptions);
        Assert.Equal(1, afterDescriptions);
        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);
        Assert.Equal(catalogue.SpecialTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                     kinds.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        // The body the fixture scenario draws is the Fuso, whose bus class curates no kind.
        Assert.Equal(string.Empty, kinds["vehicle.fuso.mitsubishi"]);
        Assert.Equal("emergency", kinds["vehicle.ambulance.ford"]);

        for (int step = 0; step < 10 && session.Advance(); step++)
        {
        }

        Assert.Single(carla.SpecialTypeAdoptions);
    }

    [Fact]
    public void ASessionRefusesAWorldThatCarriesNoBareEarthRecord()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        // A stock map: nothing published, as the recorded world answers by default.
        var carla = new RecordedWorld();
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Contains("carries no bare-earth reference record", refused.Message);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.Adoptions);
        Assert.Empty(carla.SpecialTypeAdoptions);
    }

    private static SumoDriveSessionOptions Options(SyntheticWorld world,
                                                   List<CoSimPoseRecord> computed,
                                                   List<RenderedVehicleInterval> released,
                                                   Func<bool>? tick) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = tick,
            OnPose = computed.Add,
            OnRelease = released.Add,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

    /// <summary>
    /// The transform a pose should be written as, spelled out from its fields rather than taken from
    /// the code under test.
    /// </summary>
    private static Transform ExpectedTransform(in VehiclePose pose) =>
        new(new Location((float)pose.X, (float)pose.Y, (float)pose.Z),
            new Rotation((float)pose.PitchDegrees, (float)pose.YawDegrees, (float)pose.RollDegrees));

    /// <summary>The velocity a pose should be written with, spelled out from its fields.</summary>
    private static Vector3D ExpectedVelocity(in VehiclePose pose) =>
        new((float)pose.VelocityX, (float)pose.VelocityY, (float)pose.VelocityZ);
}
