using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
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
        };
}
