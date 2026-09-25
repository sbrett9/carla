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
            "named://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 270.0,
                                      hysteresisMetres: 30.0, capacity: 128))
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

        var carla = new RecordedWorld();
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
            Assert.Equal(0, session.Report.PoseDeclinesForNoBody);
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
    public void EveryTickWritesItsPosesInOneBatchOfTransformsAndNothingElse()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld();
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

        // And what a tick writes is transforms. Not a target velocity beside each one -- on a body
        // that is not simulating it would write a physics body the getter will not read, and the
        // engine logs the call as invalid.
        foreach (IReadOnlyList<Command> batch in carla.PoseBatches)
        {
            Assert.All(batch, command => Assert.IsType<ApplyTransformCommand>(command));
        }

        // One command per pose that had a body to go to, and nothing else in the batch but the
        // parking poses of the bodies given back.
        Assert.Equal(computed.Count + released.Count(each => each.Actor != 0),
                     session.Report.CommandsWritten);
    }

    [RequiresSumoFact]
    public void ABodyGivenBackIsWrittenToItsSlotInTheNextTickSBatch()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld();
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
            session.Advance();

            RenderedVehicleInterval first = released[0];
            Transform? resting = carla.ObservedTransform(first.Actor);
            Assert.NotNull(resting);
            Transform parked = resting.Value;

            // The slot is below the surface and beyond it, which is where a parked body belongs and
            // nowhere a camera aimed at the road can frame.
            Assert.True(parked.Location.Z < -100f, $"parked at z {parked.Location.Z}");
            Assert.True(parked.Location.X > 100f, $"parked at x {parked.Location.X}");
        }
    }

    [RequiresSumoFact]
    public void TheRoadAndSignalLayersAreHiddenBeforeTheFirstTickAndDrawnAgainAtTheEnd()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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
        catch (IOException)
        {
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

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
        SumoDriveSessionOptions options = Options(world, [], [], tick: null);
        options.World = carla;
        options.Epoch = SolarLeaseTests.PortEpoch();
        options.Illumination = IlluminationPolicy.Advance(1.0);

        SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.True(carla.Sun!.Advancing);
        try
        {
            carla.ThrowOnTick = new IOException("the connection to the simulator was dropped");
            session.Advance();
            Assert.Fail("the run should have failed on the dropped connection");
        }
        catch (IOException)
        {
            session.Dispose();
        }

        Assert.Equal((13.0, 2019, 9, 21, -5.0, false), (carla.Sun.SolarTime, carla.Sun.Year,
                                                        carla.Sun.Month, carla.Sun.Day,
                                                        carla.Sun.TimeZone, carla.Sun.Advancing));
    }

    [RequiresSumoFact]
    public void EveryTickIsAuditedAndASunAnotherClientMovedStopsTheRun()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld();
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
        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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

        var carla = new RecordedWorld();
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

        var sunless = new RecordedWorld { Sun = null };
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

    private static SumoDriveSessionOptions Options(SyntheticWorld world,
                                                   List<CoSimPoseRecord> computed,
                                                   List<RenderedVehicleInterval> released,
                                                   Func<bool>? tick) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 8))
        {
            TickWorld = tick,
            OnPose = computed.Add,
            OnRelease = released.Add,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
