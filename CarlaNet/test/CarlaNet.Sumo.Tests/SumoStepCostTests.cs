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
/// while it advances, so the cost is paid inside the step whether or not anything reads them. The
/// bridge subscribes every vehicle SUMO has, because it renders every one, so these are what a
/// scenario's population costs SUMO per step -- paid in wall clock, never in what is drawn.</para>
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

    /// <summary>
    /// What asking SUMO for its collision list costs: a step that reads only the colliding-vehicles count
    /// its own answer carries, against a step that also asks for the list, and the list asked for again
    /// and again with no step between, which is the round trip alone.
    /// </summary>
    [NamedScenarioFact]
    public void TheCostOfAskingForTheCollisionListIsMeasured()
    {
        string scenario = NamedScenarioFactAttribute.Scenario!;
        double warmup = NamedScenarioFactAttribute.Warmup;
        int steps = NamedScenarioFactAttribute.Steps;

        double counted = MeasureCollisionReads(scenario, warmup, steps, askForTheList: false, out _, out _);
        double asked = MeasureCollisionReads(scenario, warmup, steps, askForTheList: true, out double roundTrip,
                                             out int population);

        output.WriteLine($"scenario   {Path.GetFileName(scenario)}");
        output.WriteLine($"warm-up    t = {warmup.ToString("0.###", CultureInfo.InvariantCulture)} s, {population} vehicles");
        output.WriteLine($"measured   {steps} steps, and the list {steps} times with no step between");
        output.WriteLine(string.Empty);
        output.WriteLine($"step, count read from its answer     {counted:0.0000} ms/step");
        output.WriteLine($"step, and the list asked for         {asked:0.0000} ms/step");
        output.WriteLine($"the list alone, no step between      {roundTrip * 1000.0:0.0} us/get");
    }

    /// <summary>
    /// Steps measured with the simulation domain subscribed as a bridge subscribes it, reading the count
    /// each step and, where asked, the list too; then the list alone, back to back. Milliseconds per step,
    /// and per get of the list alone.
    /// </summary>
    private static double MeasureCollisionReads(string scenario, double warmup, int steps, bool askForTheList,
                                                out double millisecondsPerGet, out int population)
    {
        using SumoConnection sumo = SumoConnection.Start(
            SumoInstallation.LocateOrThrow(), scenario,
            new SumoLaunchOptions { Output = _ => { } });
        if (warmup > 0)
        {
            sumo.Step(warmup);
        }

        population = sumo.Vehicles.Ids.Count;
        SumoSimulationSubscription subscription = sumo.Simulation.Subscription;
        subscription.Subscribe();
        long collisions = 0;
        Stopwatch clock = Stopwatch.StartNew();
        for (int step = 0; step < steps; step++)
        {
            sumo.Step();
            collisions += subscription.CollidingVehicleCount;
            if (askForTheList)
            {
                collisions += sumo.Simulation.Collisions.Count;
            }
        }

        clock.Stop();
        double perStep = clock.Elapsed.TotalMilliseconds / steps;

        Stopwatch gets = Stopwatch.StartNew();
        for (int get = 0; get < steps; get++)
        {
            collisions += sumo.Simulation.Collisions.Count;
        }

        gets.Stop();
        millisecondsPerGet = gets.Elapsed.TotalMilliseconds / steps;
        GC.KeepAlive(collisions);
        return perStep;
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
