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
}
