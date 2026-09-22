using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The whole bridge, running with nothing applied: the clock, the frame check, the lease, the
/// subscription, the render set, the lookahead, the interpolation and the conversion.
/// </summary>
public sealed class GhostSessionTests
{
    private readonly ITestOutputHelper _output;

    public GhostSessionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void AGhostRunComputesPosesAgreesWithSumoAndAppliesNothing()
    {
        // The fixture network projects as "!", which is netconvert's own way of saying it was given
        // no projection, so the world it is packaged with declares the same.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        List<GhostPoseRecord> ghost = [];
        List<RenderedVehicleInterval> released = [];
        long ticks = 0;

        using (GhostSession session = GhostSession.Start(Options(world, ghost, released,
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

        Assert.NotEmpty(ghost);
        Assert.NotEmpty(released);
        Assert.All(ghost, record => Assert.Equal("vehicle.fuso.mitsubishi", record.Pose.BlueprintId));
    }

    [RequiresSumoFact]
    public void TheVehicleWhoseTypeNamesNoMeasuredBodyIsNeverPosed()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        List<GhostPoseRecord> ghost = [];

        using GhostSession session = GhostSession.Start(Options(world, ghost, [], () => true));
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.True(session.Report.VehicleTicksWithNoMeasuredBody > 0,
                    "the unrenderable vehicle never reached the render set, so nothing was refused");
        Assert.DoesNotContain(ghost, record => record.Pose.VehicleId == "unrenderable");
    }

    [RequiresSumoFact]
    public void ASessionRefusesToStartWhileSomethingElseHoldsTheWorldSPopulation()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        GhostSessionOptions options = Options(world, [], [], () => true);

        using PopulationLease ambient = WorldDriveAuthority.ForWorld(options.WorldKey)
            .Acquire(PopulationMode.TrafficManagerAmbient, "TrafficController on the same world");

        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => GhostSession.Start(options));
        Assert.Contains("TrafficController on the same world", refused.Message);
    }

    [RequiresSumoFact]
    public void ASessionRefusesAnAsynchronousWorldAndAWorldTheNetworkDoesNotBelongTo()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        GhostSessionOptions asynchronous = Options(world, [], [], () => true) with
        {
            WorldIsSynchronous = false,
        };
        Assert.Throws<CoSimSessionRefusedException>(() => GhostSession.Start(asynchronous));

        using SyntheticWorld elsewhere = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork,
            "+proj=tmerc +lat_0=39.59431 +lon_0=-104.88449 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 "
            + "+units=m +no_defs");
        CoSimSessionRefusedException mismatch = Assert.Throws<CoSimSessionRefusedException>(
            () => GhostSession.Start(Options(elsewhere, [], [], () => true)));
        Assert.Contains("projects as", mismatch.Message);
    }

    [RequiresSumoFact]
    public void AWorldThatStopsTickingStopsTheRun()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        using GhostSession session = GhostSession.Start(
            Options(world, [], [], () => false));

        CoSimSessionRefusedException failed =
            Assert.Throws<CoSimSessionRefusedException>(() => session.Advance());
        Assert.Contains("no frame", failed.Message);
    }

    [NamedGhostRunFact]
    public void AGhostRunAgainstARealScenarioAndWorld()
    {
        List<GhostPoseRecord> ghost = [];
        var options = new GhostSessionOptions(
            NamedGhostRunFactAttribute.Scenario!,
            NamedGhostRunFactAttribute.WorldPackage!,
            CoSimFixtures.VehicleCatalogue,
            "named://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 270.0,
                                      hysteresisMetres: 30.0, capacity: 128))
        {
            WarmUpToSimulatedSecond = NamedGhostRunFactAttribute.WarmUp,
            SumoStepOverrideSeconds = NamedGhostRunFactAttribute.StepLength,
            OnPose = ghost.Add,
        };

        using GhostSession session = GhostSession.Start(options);
        for (int step = 0; step < NamedGhostRunFactAttribute.Steps && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        _output.WriteLine($"poses logged       {ghost.Count}");
        Assert.True(session.Report.PosesComputed > 0, "the render set never held a vehicle");
        Assert.True(session.Report.WorstBumperResidualMetres < 1e-6,
                    $"bumper residual {session.Report.WorstBumperResidualMetres}");
    }

    private static GhostSessionOptions Options(SyntheticWorld world,
                                               List<GhostPoseRecord> ghost,
                                               List<RenderedVehicleInterval> released,
                                               Func<bool> tick) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 8))
        {
            TickWorld = tick,
            OnPose = ghost.Add,
            OnRelease = released.Add,
        };
}
