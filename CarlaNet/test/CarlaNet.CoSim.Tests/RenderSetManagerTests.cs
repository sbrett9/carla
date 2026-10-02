using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Admission and release, against frames made up here rather than against a simulation: what is
/// asserted is the manager's bookkeeping, and a simulation would only make it harder to put a vehicle
/// exactly where a test wants it.
/// </summary>
public sealed class RenderSetManagerTests
{
    private static CoSimVehicleFrame At(string id, double x, double y) =>
        new(id, x, y, 90.0, 10.0, "edge", "edge_0", 5.0, "measured_truck", SumoVehicleSignals.None);

    [Fact]
    public void EveryVehicleWithAStateIsAdmittedWhereverItIs()
    {
        var manager = new RenderSetManager();

        manager.ReconcileRenderSet(0.0, Frames(At("near", 10.0, 0.0), At("far", 50_000.0, -50_000.0)));

        Assert.Equal(["far", "near"], manager.RenderedVehicleIds.Order().ToArray());
        Assert.Equal(2, manager.Admissions);
    }

    [Fact]
    public void APopulationOfThreeHundredIsAdmittedInFull()
    {
        var manager = new RenderSetManager();

        manager.ReconcileRenderSet(0.0, Frames([.. Enumerable.Range(0, 300)
            .Select(index => At($"v{index:000}", index * 3.0, 0.0))]));

        Assert.Equal(300, manager.RenderedVehicleIds.Count);
        Assert.Equal(300, manager.Admissions);
        Assert.Equal(300, manager.LastNewlyAdmitted);
    }

    [Fact]
    public void AVehicleIsAdmittedOnceAndHoldsItsPlaceForAsLongAsSumoHasIt()
    {
        List<RenderedVehicleInterval> released = [];
        var manager = new RenderSetManager(released.Add);

        for (int step = 0; step < 50; step++)
        {
            // Driving away, a kilometre a step: nothing about where it is releases it.
            manager.ReconcileRenderSet(step, Frames(At("a", step * 1000.0, 0.0)));
            Assert.Equal(step == 0 ? 1 : 0, manager.LastNewlyAdmitted);
        }

        Assert.Equal(1, manager.Admissions);
        Assert.Empty(released);
        Assert.True(manager.TryGetAdmissionInstant("a", out double admittedAt));
        Assert.Equal(0.0, admittedAt);
    }

    [Fact]
    public void AnAdmissionAndItsReleaseAreRecordedWithTheirInstantsAndTheReason()
    {
        List<RenderedVehicleInterval> released = [];
        var manager = new RenderSetManager(released.Add);

        manager.ReconcileRenderSet(12.5, Frames(At("a", 10.0, 0.0)));
        manager.ReconcileRenderSet(30.0, new Dictionary<string, CoSimVehicleFrame>());

        RenderedVehicleInterval interval = Assert.Single(released);
        Assert.Equal("a", interval.VehicleId);
        Assert.Equal(12.5, interval.AdmittedAtSeconds);
        Assert.Equal(30.0, interval.ReleasedAtSeconds);
        Assert.Equal(RenderSetReleaseReason.LeftTheSimulation, interval.ReleaseReason);
        Assert.Equal(1, manager.LastReleased);
    }

    [Fact]
    public void AVehicleThatVanishedWithoutArrivingIsReleasedAndSaidToHaveVanished()
    {
        List<RenderedVehicleInterval> released = [];
        var manager = new RenderSetManager(released.Add);

        // Both gone from the frames; only one of them is named as having gone without arriving.
        manager.ReconcileRenderSet(1.0, Frames(At("arrived", 10.0, 0.0), At("vanished", -10.0, 0.0)));
        manager.ReconcileRenderSet(2.0, new Dictionary<string, CoSimVehicleFrame>(), ["vanished"]);

        Assert.Equal(RenderSetReleaseReason.Vanished,
                     Assert.Single(released, interval => interval.VehicleId == "vanished").ReleaseReason);
        Assert.Equal(RenderSetReleaseReason.LeftTheSimulation,
                     Assert.Single(released, interval => interval.VehicleId == "arrived").ReleaseReason);
        Assert.All(released, interval => Assert.Equal(2.0, interval.ReleasedAtSeconds));
    }

    [Fact]
    public void TheEndOfASessionClosesEveryOpenInterval()
    {
        List<RenderedVehicleInterval> released = [];
        var manager = new RenderSetManager(released.Add);

        manager.ReconcileRenderSet(1.0, Frames(At("a", 10.0, 0.0), At("b", -10.0, 0.0)));
        manager.CloseAll(4.0);

        Assert.Empty(manager.RenderedVehicleIds);
        Assert.Equal(2, released.Count);
        Assert.All(released, interval =>
        {
            Assert.Equal(RenderSetReleaseReason.SessionEnded, interval.ReleaseReason);
            Assert.Equal(4.0, interval.ReleasedAtSeconds);
        });
    }

    [Fact]
    public void VehiclesAreAdmittedInAnOrderThatDoesNotDependOnADictionary()
    {
        // The same vehicles, presented in opposite orders.
        var forwards = new RenderSetManager();
        forwards.ReconcileRenderSet(0.0, Frames(At("alpha", 10.0, 0.0), At("beta", -10.0, 0.0),
                                                At("gamma", 0.0, 10.0)));

        var backwards = new RenderSetManager();
        backwards.ReconcileRenderSet(0.0, Frames(At("gamma", 0.0, 10.0), At("beta", -10.0, 0.0),
                                                 At("alpha", 10.0, 0.0)));

        Assert.Equal(["alpha", "beta", "gamma"], forwards.RenderedVehicleIds.ToArray());
        Assert.Equal(forwards.RenderedVehicleIds.ToArray(), backwards.RenderedVehicleIds.ToArray());
    }

    [Fact]
    public void TheDefaultPolicyIsEveryVehicleWithNoLimitAndLeavesNothingOut()
    {
        var manager = new RenderSetManager();

        manager.ReconcileRenderSet(0.0, Frames(At("a", 10.0, 0.0), At("b", 90_000.0, 0.0)));

        Assert.IsType<EveryVehicleRenderSetPolicy>(manager.Policy);
        Assert.Null(manager.Policy.Capacity);
        Assert.False(manager.Policy.Limits);
        Assert.Equal((2, 2, 0, 0), (manager.LastPopulation, manager.LastEligible, manager.LastShed, manager.LastHeld));
        Assert.Equal((0L, 0L), (manager.VehiclePassesOutsideThePolicy, manager.CapacityDeclines));
    }

    [Fact]
    public void ACircleAdmitsInsideItsRadiusKeepsItsVehiclesToTheWiderOneAndCountsTheRest()
    {
        List<RenderedVehicleInterval> released = [];
        var manager = new RenderSetManager(
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0, hysteresisMetres: 20.0), released.Add);

        manager.ReconcileRenderSet(0.0, Frames(At("inside", 90.0, 0.0), At("outside", 110.0, 0.0)));
        Assert.Equal(["inside"], manager.RenderedVehicleIds);
        Assert.Equal((2, 1, 1L), (manager.LastPopulation, manager.LastEligible, manager.VehiclePassesOutsideThePolicy));

        // Beyond the admit radius and inside the release radius: kept; and the other still out.
        manager.ReconcileRenderSet(1.0, Frames(At("inside", 115.0, 0.0), At("outside", 110.0, 0.0)));
        Assert.Equal(["inside"], manager.RenderedVehicleIds);
        Assert.Empty(released);

        manager.ReconcileRenderSet(2.0, Frames(At("inside", 125.0, 0.0), At("outside", 110.0, 0.0)));
        Assert.Empty(manager.RenderedVehicleIds);
        Assert.Equal(RenderSetReleaseReason.LeftTheRegion, Assert.Single(released).ReleaseReason);
        Assert.Equal(4L, manager.VehiclePassesOutsideThePolicy);
    }

    [Fact]
    public void UnderACircleWithACapacityTheNearestAreDrawnAndANearerNewcomerTakesTheFarthestPlace()
    {
        List<RenderedVehicleInterval> released = [];
        var manager = new RenderSetManager(
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0, hysteresisMetres: 20.0, capacity: 3),
            released.Add);

        manager.ReconcileRenderSet(0.0, Frames(At("d10", 10.0, 0.0), At("d20", 20.0, 0.0), At("d30", 30.0, 0.0),
                                               At("d40", 40.0, 0.0), At("d50", 50.0, 0.0)));
        Assert.Equal(["d10", "d20", "d30"], manager.RenderedVehicleIds.Order(StringComparer.Ordinal));
        Assert.Equal((5, 2), (manager.LastEligible, manager.LastShed));

        manager.ReconcileRenderSet(1.0, Frames(At("d10", 10.0, 0.0), At("d20", 20.0, 0.0), At("d30", 30.0, 0.0),
                                               At("d40", 40.0, 0.0), At("d50", 50.0, 0.0), At("d05", 5.0, 0.0)));
        Assert.Equal(["d05", "d10", "d20"], manager.RenderedVehicleIds.Order(StringComparer.Ordinal));
        RenderedVehicleInterval shed = Assert.Single(released);
        Assert.Equal(("d30", RenderSetReleaseReason.Capacity), (shed.VehicleId, shed.ReleaseReason));
        Assert.Equal(2L + 3L, manager.CapacityDeclines);
    }

    [Fact]
    public void EveryVehicleUnderACapacityKeepsTheVehiclesDrawnAndFillsAFreePlaceInTheSeedsOrder()
    {
        List<RenderedVehicleInterval> released = [];
        var policy = new EveryVehicleRenderSetPolicy(capacity: 3);
        var manager = new RenderSetManager(policy, released.Add);
        string[] ids = [.. Enumerable.Range(0, 8).Select(index => $"v{index}")];
        string[] bySeed = [.. ids.OrderBy(id => SeededOrder.Of(42, id))];

        Pass(manager, 42, 0.0, ids);
        Assert.Equal(bySeed.Take(3).Order(StringComparer.Ordinal), manager.RenderedVehicleIds.Order(StringComparer.Ordinal));
        Assert.Equal((8, 8, 5), (manager.LastPopulation, manager.LastEligible, manager.LastShed));

        // A newcomer that comes first in the seed's order takes no place from a vehicle drawn.
        string newcomer = Enumerable.Range(100, 400).Select(index => $"n{index}")
            .First(id => SeededOrder.Of(42, id) < SeededOrder.Of(42, bySeed[0]));
        Pass(manager, 42, 1.0, [.. ids, newcomer]);
        Assert.DoesNotContain(newcomer, manager.RenderedVehicleIds);
        Assert.Empty(released);

        // One drawn leaves the simulation: its place goes to the first of the rest in the seed's order.
        string leaving = bySeed[1];
        Pass(manager, 42, 2.0, [.. ids.Where(id => id != leaving), newcomer]);
        Assert.Contains(newcomer, manager.RenderedVehicleIds);
        Assert.Equal(RenderSetReleaseReason.LeftTheSimulation, Assert.Single(released).ReleaseReason);
        Assert.DoesNotContain(released, interval => interval.ReleaseReason == RenderSetReleaseReason.Capacity);
    }

    [Fact]
    public void VehiclesLeavingALimitAreReleasedInAnOrderThatDoesNotDependOnADictionary()
    {
        List<string> forwards = [];
        List<string> backwards = [];
        foreach ((List<string> order, string[] ids) in new[] { (forwards, new[] { "a", "b", "c" }),
                                                               (backwards, new[] { "c", "b", "a" }) })
        {
            var manager = new RenderSetManager(
                new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 100.0, hysteresisMetres: 20.0),
                interval => order.Add(interval.VehicleId));
            manager.ReconcileRenderSet(0.0, Frames([.. ids.Select(id => At(id, 10.0, 0.0))]));
            manager.ReconcileRenderSet(1.0, Frames([.. ids.Select(id => At(id, 500.0, 0.0))]));
        }

        Assert.Equal(["a", "b", "c"], forwards);
        Assert.Equal(forwards, backwards);
    }

    [RequiresSumoFact]
    public void TheRenderSetIsEveryVehicleSumoHasAcrossARunningSimulation()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);
        var manager = new RenderSetManager();

        Dictionary<string, CoSimVehicleFrame> frames = [];
        int most = 0;
        for (int step = 0; step < 400; step++)
        {
            sumo.Step();
            population.Reconcile(sumo.Simulation.DepartedVehicleIds,
                                 sumo.Simulation.ArrivedVehicleIds);
            population.ReadFrames(frames);
            manager.ReconcileRenderSet(sumo.Time, frames, population.LastVanished);

            Assert.Equal(sumo.Vehicles.Ids.Order(StringComparer.Ordinal),
                         manager.RenderedVehicleIds.Order(StringComparer.Ordinal));
            most = Math.Max(most, manager.RenderedVehicleIds.Count);
        }

        Assert.True(most >= 3, $"the fixture never held more than {most} vehicles at once");
        Assert.Equal(4, manager.Admissions);
    }

    private static Dictionary<string, CoSimVehicleFrame> Frames(params CoSimVehicleFrame[] frames) =>
        frames.ToDictionary(frame => frame.Id);

    /// <summary>One pass under a seed, every vehicle standing at the origin.</summary>
    private static void Pass(RenderSetManager manager, long seed, double seconds, string[] ids)
    {
        manager.BeginPass(new RenderSetPass(seconds, 1.0, seed, 10.0, 10.0, [], (_, _) => 0.0));
        manager.ReconcileRenderSet(seconds, Frames([.. ids.Select(id => At(id, 0.0, 0.0))]));
    }
}
