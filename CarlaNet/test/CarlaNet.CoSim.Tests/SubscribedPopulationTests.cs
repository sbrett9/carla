using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The subscription, against a running SUMO. What is asserted is the property the design rests on:
/// every vehicle SUMO has delivers its full state, on the step it departed rather than the step after,
/// for one subscribe command over its whole life.
/// </summary>
public sealed class SubscribedPopulationTests
{
    [RequiresSumoFact]
    public void EveryDepartedVehicleDeliversItsFullStateOnTheStepItDeparted()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);

        sumo.Step();
        population.Reconcile(sumo.Simulation.DepartedVehicleIds, sumo.Simulation.ArrivedVehicleIds);

        Assert.Contains("turner", population.SubscribedVehicleIds);
        Assert.True(population.TryReadFrame("turner", out CoSimVehicleFrame frame));
        Assert.Equal("turner", frame.Id);
        Assert.Equal("measured_truck", frame.TypeId);
        Assert.Equal("approach_0", frame.LaneId);
        Assert.True(frame.LanePositionMetres > 0.0);

        Dictionary<string, CoSimVehicleFrame> frames = [];
        Assert.Equal(population.SubscribedVehicleIds.Count, population.ReadFrames(frames));
        Assert.Contains("turner", frames.Keys);
    }

    [RequiresSumoFact]
    public void AVehicleTakenOutBetweenTwoStepsIsNamedAsVanished()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);

        sumo.Step();
        population.Reconcile(sumo.Simulation.DepartedVehicleIds, sumo.Simulation.ArrivedVehicleIds);
        sumo.Vehicles.Remove("turner");
        sumo.Step();
        population.Reconcile(sumo.Simulation.DepartedVehicleIds, sumo.Simulation.ArrivedVehicleIds);

        Dictionary<string, CoSimVehicleFrame> frames = [];
        population.ReadFrames(frames);

        Assert.DoesNotContain("turner", frames.Keys);
        Assert.Equal(["turner"], population.LastVanished);
        Assert.DoesNotContain("turner", population.SubscribedVehicleIds);
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
        }

        Assert.True(sawAnArrival, "no vehicle reached its destination inside 400 steps");
        Assert.DoesNotContain(sumoSaid,
            line => line.Contains("subscription", StringComparison.OrdinalIgnoreCase));
    }

    [RequiresSumoFact]
    public void TheSubscriptionCostsOneSubscribeCommandPerVehicleForItsWholeLife()
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);
        Dictionary<string, CoSimVehicleFrame> frames = [];

        // Twelve simulated seconds at 0.05 s a step: past the last vehicle's declared departure.
        for (int step = 0; step < 240; step++)
        {
            sumo.Step();
            population.Reconcile(sumo.Simulation.DepartedVehicleIds,
                                 sumo.Simulation.ArrivedVehicleIds);
            population.ReadFrames(frames);
            Assert.Equal(sumo.Vehicles.Ids.Count, frames.Count);
        }

        Assert.Equal(4, population.Subscribes);
    }
}
