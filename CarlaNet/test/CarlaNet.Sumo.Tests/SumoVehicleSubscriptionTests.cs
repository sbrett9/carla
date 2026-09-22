using System.Collections.Concurrent;
using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// The read path: what a subscription delivers, who decides which vehicles are in it, and what
/// happens when a vehicle SUMO has removed is released.
/// </summary>
public class SumoVehicleSubscriptionTests
{
    /// <summary>How many steps the two fixture vehicles need to clear a 200 m edge.</summary>
    private const int StepsToClearTheEdge = 2000;

    [RequiresSumoFact]
    public void ASubscribedVehicleDeliversItsStateWithEveryStep()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        sumo.Step();
        sumo.Vehicles.Subscription.Add("first");
        sumo.Step();

        SumoVehicleState state = Assert.Single(sumo.Vehicles.Subscription.Read()).Value;

        Assert.Equal("first", state.Id);
        Assert.Equal("car", state.TypeId);
        Assert.Equal("west_to_east", state.EdgeId);
        Assert.Equal("west_to_east_0", state.LaneId);

        // The fixture network runs due east from the origin, and SUMO's heading is degrees
        // clockwise from north, so a vehicle on it is heading 90.
        Assert.Equal(90.0, state.HeadingDegrees, precision: 6);
        Assert.True(state.X > 0);
        Assert.True(state.SpeedMetresPerSecond >= 0);
    }

    /// <summary>
    /// Which vehicles are subscribed is the caller's decision. A subscription is charged inside the
    /// step whether or not it is read, so a bridge rendering a subset of the population subscribes
    /// that subset -- and this is what says it can.
    /// </summary>
    [RequiresSumoFact]
    public void OnlySubscribedVehiclesDeliverAnything()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        StepUntilBothVehiclesAreRunning(sumo);

        Assert.Equal(2, sumo.Vehicles.Count);

        sumo.Vehicles.Subscription.Add("second");
        sumo.Step();

        Assert.Equal(["second"], sumo.Vehicles.Subscription.DeliveredVehicleIds);
    }

    /// <summary>
    /// The subscription and a direct read are two paths through the same codec. They disagree if
    /// either the subscription block or the get response is being read at the wrong offset.
    /// </summary>
    [RequiresSumoFact]
    public void ASubscribedStateMatchesTheSameStateReadDirectly()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        StepUntilBothVehiclesAreRunning(sumo);
        sumo.Vehicles.Subscription.Add("first");

        // Far enough in that the vehicle is moving and off the very start of the edge.
        for (int step = 0; step < 100; step++)
        {
            sumo.Step();
        }

        SumoVehicleState subscribed = Assert.Single(sumo.Vehicles.Subscription.Read()).Value;
        SumoVehicleState direct = sumo.Vehicles.ReadStateWithoutSubscription("first");

        Assert.Equal(direct, subscribed);
    }

    /// <summary>
    /// A subscribed vehicle SUMO removes is simply absent from the next step's results. There is no
    /// separate notification, and that absence is how a bridge learns of it without asking.
    /// </summary>
    [RequiresSumoFact]
    public void AVehicleThatArrivesStopsDelivering()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        StepUntilBothVehiclesAreRunning(sumo);
        sumo.Vehicles.Subscription.Add("first");

        Assert.True(StepUntilArrived(sumo, "first"), "'first' never left the network.");

        Assert.DoesNotContain("first", sumo.Vehicles.Subscription.DeliveredVehicleIds);
        Assert.Null(sumo.Vehicles.Subscription.TryRead("first"));

        // Still subscribed as far as this client knows: SUMO dropped the subscription with the
        // vehicle without being asked, which is exactly why releasing it must not send anything.
        Assert.Contains("first", sumo.Vehicles.Subscription.SubscribedVehicleIds);
    }

    /// <summary>
    /// Releasing a vehicle SUMO has already removed must not raise, and must not make SUMO log.
    /// </summary>
    /// <remarks>
    /// This is the behaviour the previous binding got wrong in both directions, measured on a
    /// 388-vehicle run: it raised, and it produced one <c>Answered with error to command 0xd4: The
    /// subscription to remove was not found</c> line on SUMO's console per arrival -- several a
    /// second, burying everything else SUMO had to say. SUMO's own output is captured here and
    /// asserted on, because "it did not throw" would have passed for the version that logged.
    /// </remarks>
    [RequiresSumoFact]
    public void ReleasingAnArrivedVehicleNeitherRaisesNorMakesSumoLog()
    {
        ConcurrentQueue<string> output = [];
        using SumoConnection sumo = SumoFixtures.Open(
            SumoFixtures.TwoVehicles, new SumoLaunchOptions { Output = output.Enqueue });

        StepUntilBothVehiclesAreRunning(sumo);
        sumo.Vehicles.Subscription.Add("first");
        Assert.True(StepUntilArrived(sumo, "first"), "'first' never left the network.");

        sumo.Vehicles.Subscription.Release("first");
        sumo.Step();

        Assert.DoesNotContain("first", sumo.Vehicles.Subscription.SubscribedVehicleIds);
        Assert.DoesNotContain(output, line => line.Contains("subscription", StringComparison.OrdinalIgnoreCase));

        // Releasing something that was never subscribed is equally quiet.
        sumo.Vehicles.Subscription.Release("never_existed");
    }

    /// <summary>
    /// The other half of the same fact, exercised so that <see cref="SumoVehicleSubscription.Release"/>
    /// is shown to be avoiding something real rather than being a synonym.
    /// </summary>
    /// <remarks>
    /// Unsubscribing a vehicle SUMO has removed is what SUMO refuses. It is a recoverable refusal --
    /// the connection carries on -- and it is why the release path sends nothing at all.
    /// </remarks>
    [RequiresSumoFact]
    public void UnsubscribingAnArrivedVehicleIsWhatSumoRefuses()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        StepUntilBothVehiclesAreRunning(sumo);
        sumo.Vehicles.Subscription.Add("first");
        Assert.True(StepUntilArrived(sumo, "first"), "'first' never left the network.");

        TraCIException refusal = Assert.Throws<TraCIException>(
            () => sumo.Vehicles.Subscription.Unsubscribe("first"));
        Assert.Equal(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, refusal.CommandId);

        double before = sumo.Time;
        sumo.Step();
        Assert.Equal(before + sumo.StepLength, sumo.Time, precision: 9);
    }

    /// <summary>
    /// Unsubscribing a vehicle that is still driving is the supported call, and its point is to take
    /// that vehicle's cost back out of the step.
    /// </summary>
    [RequiresSumoFact]
    public void UnsubscribingARunningVehicleStopsItsDelivery()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        StepUntilBothVehiclesAreRunning(sumo);
        sumo.Vehicles.Subscription.Add("first");
        sumo.Vehicles.Subscription.Add("second");
        sumo.Step();
        Assert.Equal(2, sumo.Vehicles.Subscription.Read().Count);

        sumo.Vehicles.Subscription.Unsubscribe("first");
        sumo.Step();

        Assert.Equal(["second"], sumo.Vehicles.Subscription.DeliveredVehicleIds);
    }

    /// <summary>
    /// A variable whose value SUMO writes as untyped fields is refused at subscribe time. Read
    /// generically it would not fail; it would consume the wrong number of bytes and mis-decode
    /// every value after it in the frame.
    /// </summary>
    [RequiresSumoFact]
    public void SubscribingAVariableWithNoDecoderIsRefused()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        sumo.Step();

        Assert.Throws<ArgumentException>(() => sumo.TraCI.Subscribe(
            TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, "first", [TraCIConstants.VAR_BEST_LANES]));
    }

    /// <summary>
    /// A session can be told to subscribe something other than the default set, which is the knob a
    /// bridge turns when it wants less than the whole state.
    /// </summary>
    [RequiresSumoFact]
    public void TheSubscribedVariableSetIsTheCallersToChoose()
    {
        using SumoConnection sumo = SumoFixtures.Open(
            SumoFixtures.TwoVehicles,
            new SumoLaunchOptions
            {
                Output = _ => { },
                SubscribedVehicleVariables = [TraCIVariables.Position, TraCIVariables.Speed],
            });

        StepUntilBothVehiclesAreRunning(sumo);
        sumo.Vehicles.Subscription.Add("first");
        sumo.Step();

        Assert.True(sumo.Vehicles.Subscription.TryReadVariable("first", TraCIVariables.Speed,
                                                               out TraCIValue speed));
        Assert.Equal(TraCIValueKind.Double, speed.Kind);
        Assert.False(sumo.Vehicles.Subscription.TryReadVariable("first", TraCIVariables.Signals, out _));

        // Reading a whole state needs the whole set, and says which variable is missing rather than
        // returning a state with a made-up field in it.
        Assert.Throws<TraCIException>(() => sumo.Vehicles.Subscription.Read());
    }

    private static void StepUntilBothVehiclesAreRunning(SumoConnection sumo)
    {
        for (int step = 0; step < StepsToClearTheEdge && sumo.Vehicles.Count < 2; step++)
        {
            sumo.Step();
        }
    }

    private static bool StepUntilArrived(SumoConnection sumo, string vehicleId)
    {
        for (int step = 0; step < StepsToClearTheEdge; step++)
        {
            sumo.Step();
            if (sumo.Simulation.ArrivedVehicleIds.Contains(vehicleId))
            {
                return true;
            }
        }

        return false;
    }
}
