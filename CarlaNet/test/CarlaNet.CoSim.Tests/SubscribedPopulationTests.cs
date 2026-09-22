using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The two-tier subscription, against a running SUMO. What is asserted is the property the design
/// rests on: a vehicle costs one variable until the render set wants it, and the full set arrives on
/// the step it is promoted rather than the step after.
/// </summary>
public sealed class SubscribedPopulationTests
{
    [RequiresSumoFact]
    public void EveryDepartedVehicleIsScreenedAndNoneIsPromotedUnasked()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);

        sumo.Step();
        population.Reconcile(sumo.Simulation.DepartedVehicleIds, sumo.Simulation.ArrivedVehicleIds);

        Assert.Contains("turner", population.ScreenedVehicleIds);
        Assert.Empty(population.PromotedVehicleIds);

        Dictionary<string, (double X, double Y)> positions = [];
        Assert.Equal(population.ScreenedVehicleIds.Count, population.ReadPositions(positions));
        Assert.Contains("turner", positions.Keys);
    }

    [RequiresSumoFact]
    public void AScreenedVehicleDeliversNoStateAndAPromotedOneDeliversItOnTheSameStep()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);

        sumo.Step();
        population.Reconcile(sumo.Simulation.DepartedVehicleIds, sumo.Simulation.ArrivedVehicleIds);

        Assert.False(population.TryReadFrame("turner", out _));

        population.Promote("turner");
        Assert.True(population.TryReadFrame("turner", out CoSimVehicleFrame frame));
        Assert.Equal("turner", frame.Id);
        Assert.Equal("measured_truck", frame.TypeId);
        Assert.Equal("approach_0", frame.LaneId);
        Assert.True(frame.LanePositionMetres > 0.0);
    }

    [RequiresSumoFact]
    public void ADemotedVehicleStopsDeliveringStateAndKeepsDeliveringPosition()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);

        sumo.Step();
        population.Reconcile(sumo.Simulation.DepartedVehicleIds, sumo.Simulation.ArrivedVehicleIds);
        population.Promote("turner");
        population.Demote("turner");

        sumo.Step();
        Assert.False(population.TryReadFrame("turner", out _));

        Dictionary<string, (double X, double Y)> positions = [];
        population.ReadPositions(positions);
        Assert.Contains("turner", positions.Keys);
    }

    [RequiresSumoFact]
    public void AnArrivedVehicleIsForgottenWithoutAskingSumoToUnsubscribeIt()
    {
        List<string> sumoSaid = [];
        using SumoConnection sumo = CoSimFixtures.Open(
            CoSimFixtures.RightAngleTurnScenario,
            new SumoLaunchOptions { Output = sumoSaid.Add });
        var population = new SubscribedPopulation(sumo.TraCI);

        bool sawAnArrival = false;
        for (int step = 0; step < 400 && !sawAnArrival; step++)
        {
            sumo.Step();
            IReadOnlyList<string> arrived = sumo.Simulation.ArrivedVehicleIds;
            sawAnArrival = arrived.Count > 0;
            population.Reconcile(sumo.Simulation.DepartedVehicleIds, arrived);
            foreach (string vehicleId in population.ScreenedVehicleIds.ToList())
            {
                population.Promote(vehicleId);
            }
        }

        Assert.True(sawAnArrival, "no vehicle reached its destination inside 400 steps");
        Assert.DoesNotContain(sumoSaid,
            line => line.Contains("subscription", StringComparison.OrdinalIgnoreCase));
    }

    [RequiresSumoFact]
    public void TheScreeningTierCostsOneSubscribeCommandPerVehicleForItsWholeLife()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);

        // Twelve simulated seconds at 0.05 s a step: past the last vehicle's declared departure.
        for (int step = 0; step < 240; step++)
        {
            sumo.Step();
            population.Reconcile(sumo.Simulation.DepartedVehicleIds,
                                 sumo.Simulation.ArrivedVehicleIds);
        }

        Assert.Equal(4, population.ScreeningSubscribes);
        Assert.Equal(0, population.TierChanges);
    }
}
