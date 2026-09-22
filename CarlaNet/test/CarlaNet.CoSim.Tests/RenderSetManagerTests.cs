using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Admission, release and the capacity, against frames made up here rather than against a
/// simulation: what is asserted is the manager's arithmetic, and a simulation would only make it
/// harder to put a vehicle exactly on a boundary.
/// </summary>
public sealed class RenderSetManagerTests
{
    private static CoSimVehicleFrame At(string id, double x, double y) =>
        new(id, x, y, 90.0, 10.0, "edge", "edge_0", 5.0, "measured_truck", SumoVehicleSignals.None);

    [Fact]
    public void AVehicleOnTheBoundaryDoesNotFlickerBetweenAdmittedAndReleased()
    {
        var policy = new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0,
                                               hysteresisMetres: 20.0, capacity: 8);
        var manager = new RenderSetManager(policy);

        // Just outside the admit radius: not admitted.
        manager.ReconcileRenderSet(0.0, Frames(At("a", 101.0, 0.0)));
        Assert.Empty(manager.RenderedVehicleIds);

        // Just inside it: admitted.
        manager.ReconcileRenderSet(1.0, Frames(At("a", 99.0, 0.0)));
        Assert.Contains("a", manager.RenderedVehicleIds);

        // Back outside the admit radius but inside the release radius: still admitted. Without the
        // hysteresis this is the tick the vehicle vanishes on, and the next one it reappears on.
        manager.ReconcileRenderSet(2.0, Frames(At("a", 110.0, 0.0)));
        Assert.Contains("a", manager.RenderedVehicleIds);

        // Past the release radius: released.
        manager.ReconcileRenderSet(3.0, Frames(At("a", 121.0, 0.0)));
        Assert.Empty(manager.RenderedVehicleIds);
    }

    [Fact]
    public void TheCapacityAdmitsTheNearestAndDeclinesTheRestWithoutFailing()
    {
        var policy = new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0,
                                               hysteresisMetres: 10.0, capacity: 2);
        var manager = new RenderSetManager(policy);

        manager.ReconcileRenderSet(0.0, Frames(
            At("far", 90.0, 0.0), At("near", 10.0, 0.0), At("middle", 50.0, 0.0)));

        Assert.Equal(["middle", "near"], manager.RenderedVehicleIds.Order().ToArray());
        Assert.Equal(1, manager.CapacityDeclines);
        Assert.Equal(2, manager.Admissions);
    }

    [Fact]
    public void AnAdmissionAndItsReleaseAreRecordedWithTheirInstantsAndTheReason()
    {
        List<RenderedVehicleInterval> released = [];
        var policy = new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0,
                                               hysteresisMetres: 10.0, capacity: 8);
        var manager = new RenderSetManager(policy, released.Add);

        manager.ReconcileRenderSet(12.5, Frames(At("a", 10.0, 0.0)));
        manager.ReconcileRenderSet(30.0, Frames(At("a", 500.0, 0.0)));

        RenderedVehicleInterval interval = Assert.Single(released);
        Assert.Equal("a", interval.VehicleId);
        Assert.Equal(12.5, interval.AdmittedAtSeconds);
        Assert.Equal(30.0, interval.ReleasedAtSeconds);
        Assert.Equal(RenderSetReleaseReason.LeftTheRegion, interval.ReleaseReason);
    }

    [Fact]
    public void AVehicleSumoRemovesIsReleasedAndSaidToHaveLeftTheSimulation()
    {
        List<RenderedVehicleInterval> released = [];
        var policy = new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0,
                                               hysteresisMetres: 10.0, capacity: 8);
        var manager = new RenderSetManager(policy, released.Add);

        manager.ReconcileRenderSet(1.0, Frames(At("a", 10.0, 0.0)));
        manager.ReconcileRenderSet(2.0, new Dictionary<string, CoSimVehicleFrame>());

        Assert.Equal(RenderSetReleaseReason.LeftTheSimulation, Assert.Single(released).ReleaseReason);
    }

    [Fact]
    public void TwoIdenticallyRankedVehiclesAreAdmittedInAnOrderThatDoesNotDependOnADictionary()
    {
        var policy = new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0,
                                               hysteresisMetres: 10.0, capacity: 1);

        // Two vehicles the same distance from the centre, presented in opposite orders.
        var forwards = new RenderSetManager(policy);
        forwards.ReconcileRenderSet(0.0, Frames(At("alpha", 10.0, 0.0), At("beta", -10.0, 0.0)));

        var backwards = new RenderSetManager(policy);
        backwards.ReconcileRenderSet(0.0, Frames(At("beta", -10.0, 0.0), At("alpha", 10.0, 0.0)));

        Assert.Equal(forwards.RenderedVehicleIds, backwards.RenderedVehicleIds);
        Assert.Equal("alpha", Assert.Single(forwards.RenderedVehicleIds));
    }

    [Fact]
    public void APolicyWithNoHysteresisIsRefusedWhereItIsBuilt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RegionRenderSetPolicy(0.0, 0.0, 100.0, hysteresisMetres: 0.0, capacity: 8));
    }

    [RequiresSumoFact]
    public void TheSubscriptionTierFollowsTheRenderSetAcrossARunningSimulation()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);

        // A disc around the junction: a vehicle 100 m down the approach is outside it and is
        // screened, and one on the junction is inside it and carries the full state set.
        var policy = new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 30.0,
                                               hysteresisMetres: 10.0, capacity: 8);
        var manager = new RenderSetManager(policy);

        Dictionary<string, (double X, double Y)> positions = [];
        Dictionary<string, CoSimVehicleFrame> frames = [];
        bool sawTheTurnerRendered = false;
        bool sawAVehicleScreenedButNotPromoted = false;
        bool sawADemotion = false;

        for (int step = 0; step < 400; step++)
        {
            sumo.Step();
            population.Reconcile(sumo.Simulation.DepartedVehicleIds,
                                 sumo.Simulation.ArrivedVehicleIds);
            population.ReadPositions(positions);
            long tierChangesBefore = population.TierChanges;
            int promotedBefore = population.PromotedVehicleIds.Count;
            manager.ReconcileSubscriptions(population, positions);
            population.ReadFrames(frames);
            manager.ReconcileRenderSet(sumo.Time, frames);

            sawTheTurnerRendered |= manager.RenderedVehicleIds.Contains("turner");
            sawAVehicleScreenedButNotPromoted |=
                population.ScreenedVehicleIds.Count > population.PromotedVehicleIds.Count;
            sawADemotion |= population.TierChanges > tierChangesBefore
                            && population.PromotedVehicleIds.Count < promotedBefore;
        }

        Assert.True(sawTheTurnerRendered, "the turning vehicle never entered the render set");
        Assert.True(sawAVehicleScreenedButNotPromoted,
                    "every screened vehicle was promoted at every step, so the subscription tier is "
                    + "not governed by the render set at all");
        Assert.True(sawADemotion, "no vehicle ever left the subscription margin");
    }

    private static Dictionary<string, CoSimVehicleFrame> Frames(params CoSimVehicleFrame[] frames) =>
        frames.ToDictionary(frame => frame.Id);
}
