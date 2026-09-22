using System.Diagnostics;
using System.Globalization;
using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// Measures what a step costs with nothing subscribed, with the population subscribed and unread,
/// and with it subscribed and read.
/// </summary>
/// <remarks>
/// <para>The three numbers matter because of how SUMO charges a subscription: it fills the results
/// while it advances, so the cost lands inside the step whether or not anything reads them. That
/// makes the <i>subscribed</i> set a budget the bridge controls in its own right, separately from
/// the render set, and it is the reason the subscription here takes vehicles one at a time.</para>
///
/// <para>All three keep the departure and arrival lists, so the only difference between the first
/// and the second is the subscription itself, and between the second and the third the decode.
/// Every run maintains the subscribed set against the churn -- a subscribed set left to decay over
/// a few hundred steps measures a shrinking population rather than a steady one.</para>
///
/// <para>Opt-in, because a timing measured on the fixture network measures process startup and a
/// timing asserted in CI is a flaky test. It reports rather than asserts: what it establishes is a
/// number to put beside another number, and the judgement about whether the number is good enough
/// belongs to whoever is reading it.</para>
/// </remarks>
public class SumoStepCostTests(ITestOutputHelper output)
{
    [NamedScenarioFact]
    public void TheCostOfASubscribedPopulationIsMeasured()
    {
        string scenario = NamedScenarioFactAttribute.Scenario!;
        double warmup = NamedScenarioFactAttribute.Warmup;
        int steps = NamedScenarioFactAttribute.Steps;

        output.WriteLine($"scenario   {Path.GetFileName(scenario)}");
        output.WriteLine($"warm-up    t = {warmup.ToString("0.###", CultureInfo.InvariantCulture)} s");
        output.WriteLine($"measured   {steps} steps");
        output.WriteLine(string.Empty);

        Measurement bare = Measure(scenario, warmup, steps, Subscribe.Nothing);
        Measurement unread = Measure(scenario, warmup, steps, Subscribe.WithoutReading);
        Measurement read = Measure(scenario, warmup, steps, Subscribe.AndRead);

        output.WriteLine($"vehicles at the warm-up step        {bare.Population}");
        output.WriteLine($"nothing subscribed                  {bare.MillisecondsPerStep:0.00} ms/step");
        output.WriteLine($"population subscribed, not read     {unread.MillisecondsPerStep:0.00} ms/step");
        output.WriteLine($"population subscribed and read      {read.MillisecondsPerStep:0.00} ms/step");
        output.WriteLine($"vehicle-states read per step        {read.StatesPerStep:0.0}");

        Assert.True(bare.Population > 0, "The scenario had no vehicles at the warm-up step.");
    }

    private enum Subscribe
    {
        Nothing,
        WithoutReading,
        AndRead,
    }

    private readonly record struct Measurement(int Population, double MillisecondsPerStep, double StatesPerStep);

    private static Measurement Measure(string scenario, double warmup, int steps, Subscribe mode)
    {
        using SumoConnection sumo = SumoConnection.Start(
            SumoInstallation.LocateOrThrow(), scenario,
            new SumoLaunchOptions { Output = _ => { } });

        if (warmup > 0)
        {
            // One call, not a loop: a step naming a target time advances straight to it.
            sumo.Step(warmup);
        }

        IReadOnlyList<string> present = sumo.Vehicles.Ids;
        int population = present.Count;
        if (mode != Subscribe.Nothing)
        {
            foreach (string vehicleId in present)
            {
                sumo.Vehicles.Subscription.Add(vehicleId);
            }
        }

        Dictionary<string, SumoVehicleState> states = [];
        long statesRead = 0;
        Stopwatch clock = Stopwatch.StartNew();
        for (int step = 0; step < steps; step++)
        {
            sumo.Step();

            IReadOnlyList<string> departed = sumo.Simulation.DepartedVehicleIds;
            IReadOnlyList<string> arrived = sumo.Simulation.ArrivedVehicleIds;
            if (mode != Subscribe.Nothing)
            {
                foreach (string vehicleId in departed)
                {
                    sumo.Vehicles.Subscription.Add(vehicleId);
                }

                foreach (string vehicleId in arrived)
                {
                    sumo.Vehicles.Subscription.Release(vehicleId);
                }
            }

            if (mode == Subscribe.AndRead)
            {
                statesRead += sumo.Vehicles.Subscription.Read(states);
            }
        }

        clock.Stop();
        return new Measurement(population, clock.Elapsed.TotalMilliseconds / steps,
                               (double)statesRead / steps);
    }
}
