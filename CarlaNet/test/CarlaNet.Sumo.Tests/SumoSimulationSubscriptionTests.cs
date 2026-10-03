using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// The simulation domain read by subscription: the same lists a direct get answers, with the clock of
/// the step they happened in, and a vehicle's departure and stops asked once when an event says they
/// have changed.
/// </summary>
/// <remarks>
/// Run on the single edge at a whole-second step, so SUMO's own stamps and TraCI's clock are a second
/// apart and a test that confused one with the other would be wrong by a second rather than by a
/// rounding error.
/// </remarks>
public class SumoSimulationSubscriptionTests
{
    /// <summary>Steps enough for both fixture vehicles to make their stops and clear the edge.</summary>
    private const int StepLimit = 200;

    /// <summary>
    /// The subscription and a direct get are two paths to the same values; they disagree if either is
    /// read at the wrong offset, or if a list read by subscription belonged to some other step.
    /// </summary>
    [RequiresSumoFact]
    public void EverySubscribedValueIsTheOneADirectGetAnswersOnEveryStep()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.Stops);
        SumoSimulationSubscription subscription = sumo.Simulation.Subscription;
        subscription.Subscribe();

        int[] lists =
        [
            TraCIConstants.VAR_DEPARTED_VEHICLES_IDS,
            TraCIConstants.VAR_ARRIVED_VEHICLES_IDS,
            TraCIConstants.VAR_PENDING_VEHICLES,
            TraCIConstants.VAR_STOP_STARTING_VEHICLES_IDS,
            TraCIConstants.VAR_STOP_ENDING_VEHICLES_IDS,
            TraCIConstants.VAR_PARKING_STARTING_VEHICLES_IDS,
            TraCIConstants.VAR_PARKING_ENDING_VEHICLES_IDS,
            TraCIConstants.VAR_TELEPORT_STARTING_VEHICLES_IDS,
            TraCIConstants.VAR_EMERGENCYSTOPPING_VEHICLES_IDS,
        ];
        int entries = 0;
        for (int step = 0; step < StepLimit && sumo.Simulation.ExpectedVehicleCount > 0; step++)
        {
            sumo.Step();

            Assert.Equal(sumo.Simulation.Time, subscription.Time);
            Assert.Equal(sumo.Simulation.ExpectedVehicleCount, subscription.ExpectedVehicleCount);
            foreach (int list in lists)
            {
                Assert.True(subscription.TryReadVariable(list, out TraCIValue subscribed),
                            $"variable 0x{list:x2} was not delivered at t={subscription.Time} s");
                Assert.Equal(sumo.Simulation.Read(list).AsStringList, subscribed.AsStringList);
            }

            entries += subscription.ReadEvents().Count;
        }

        // Two departures, two arrivals, two stops begun and ended and a parking stay begun and ended:
        // the comparison was made with something in the lists, not only over empty ones.
        Assert.Equal(10, entries);
        Assert.Equal(0, sumo.Simulation.ExpectedVehicleCount);
    }

    [RequiresSumoFact]
    public void ASubscribeIsAnsweredWithTheCurrentValuesBeforeAnyStep()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.Stops);

        sumo.Simulation.Subscription.Subscribe();

        Assert.True(sumo.Simulation.Subscription.IsSubscribed);
        Assert.Equal(SumoSimulationSubscription.StepVariables, sumo.Simulation.Subscription.Variables);
        Assert.Equal(0.0, sumo.Simulation.Subscription.Time);
        Assert.Equal(2, sumo.Simulation.Subscription.ExpectedVehicleCount);
        Assert.Equal(0, sumo.Simulation.Subscription.ReadEvents().Count);
    }

    [RequiresSumoFact]
    public void ReadingADomainNobodySubscribedIsRefusedNamingWhatIsMissing()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.Stops);
        sumo.Step();

        Assert.False(sumo.Simulation.Subscription.IsSubscribed);
        TraCIException refused = Assert.Throws<TraCIException>(() => sumo.Simulation.Subscription.ReadEvents());
        Assert.Contains("has not been subscribed", refused.Message);
    }

    /// <summary>
    /// Every event is listed on the step TraCI reports it at, and what SUMO stamps it with for itself is
    /// one step before that: a stop's arrival and departure, and a vehicle's departure.
    /// </summary>
    [RequiresSumoFact]
    public void EachEventCarriesTheClockOfItsStepAndSumoStampsItOneStepEarlier()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.Stops);
        sumo.Simulation.Subscription.Subscribe();
        List<SumoStepEvents> steps = [];
        Dictionary<string, double> departures = [];
        Dictionary<string, double> delays = [];
        IReadOnlyList<SumoStop> made = [];
        while (sumo.Simulation.Subscription.ExpectedVehicleCount > 0 && steps.Count < StepLimit)
        {
            sumo.Step();
            SumoStepEvents events = sumo.Simulation.Subscription.ReadEvents();
            steps.Add(events);

            // Asked once, on the step the vehicle is listed as departed.
            foreach (string vehicleId in events.Departed)
            {
                departures[vehicleId] = sumo.Vehicles.Departure(vehicleId)!.Value;
                delays[vehicleId] = sumo.Vehicles.DepartDelay(vehicleId);
            }

            // And once on the step it is listed as ending a stop, while SUMO still has it.
            if (events.StopsEnded.Contains("dweller"))
            {
                made = sumo.Vehicles.CompletedStops("dweller");
            }
        }

        double step = sumo.StepLength;
        Assert.Equal(1.0, step);
        Assert.All(steps, events => Assert.Equal(Math.Round(events.TimeSeconds), events.TimeSeconds));

        // Inserted at once, and late: the waiter is stamped with the second SUMO inserted it, and its
        // delay is that against the zero it was declared to depart at.
        foreach (string vehicleId in (string[])["dweller", "waiter"])
        {
            double listed = Assert.Single(steps, events => events.Departed.Contains(vehicleId)).TimeSeconds;
            Assert.Equal(listed - step, departures[vehicleId]);
            Assert.Equal(departures[vehicleId], delays[vehicleId]);
        }

        Assert.Equal(0.0, delays["dweller"]);
        Assert.True(delays["waiter"] > 0.0, $"waiter was inserted {delays["waiter"]} s late");

        // Two stops begun and ended, the second of them a parking stay: listed as parking on the step it
        // began, and as back on the lane one step before it was listed as ended.
        double[] started = [.. steps.Where(events => events.StopsStarted.Contains("dweller")).Select(events => events.TimeSeconds)];
        double[] ended = [.. steps.Where(events => events.StopsEnded.Contains("dweller")).Select(events => events.TimeSeconds)];
        Assert.Equal(2, started.Length);
        Assert.Equal(2, ended.Length);
        double parked = Assert.Single(steps, events => events.ParkingStarted.Contains("dweller")).TimeSeconds;
        double unparked = Assert.Single(steps, events => events.ParkingEnded.Contains("dweller")).TimeSeconds;
        Assert.Equal(started[1], parked);
        Assert.Equal(ended[1] - step, unparked);

        // What SUMO holds for them, as it answered on the step the second ended.
        Assert.Equal(2, made.Count);
        for (int index = 0; index < 2; index++)
        {
            Assert.Equal(started[index] - step, made[index].ArrivalSeconds);
            Assert.Equal(ended[index] - step, made[index].DepartureSeconds);

            // Declared with a duration and nothing else: a length, so no declared instant.
            Assert.Equal(2.0, made[index].DurationSeconds);
            Assert.Null(made[index].UntilSeconds);
            Assert.Null(made[index].IntendedArrivalSeconds);
            Assert.Equal("west_to_east_0", made[index].LaneId);
        }

        Assert.Equal(60.0, made[0].EndPositionMetres);
        Assert.Equal(SumoStopFlags.None, made[0].Flags);
        Assert.Equal(150.0, made[1].EndPositionMetres);
        Assert.Equal(SumoStopFlags.Parking, made[1].Flags);
        Assert.True(made[1].IsParking);
    }

    [RequiresSumoFact]
    public void TheStopsAheadOfAVehicleAreTheOnesItHasNotYetLeft()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.Stops);
        sumo.Simulation.Subscription.Subscribe();
        sumo.Step();

        // Inserted and on its way: both stops ahead, neither reached.
        IReadOnlyList<SumoStop> ahead = sumo.Vehicles.Stops("dweller");
        Assert.Equal([60.0, 150.0], ahead.Select(stop => stop.EndPositionMetres));
        Assert.All(ahead, stop => Assert.Null(stop.ArrivalSeconds));
        Assert.Empty(sumo.Vehicles.CompletedStops("dweller"));
        Assert.Single(sumo.Vehicles.Stops("dweller", limit: 1));

        // Halted at the first: it is under way, so still ahead, now with its arrival.
        while (!sumo.Simulation.Subscription.ReadEvents().StopsStarted.Contains("dweller"))
        {
            sumo.Step();
        }

        SumoStop underWay = sumo.Vehicles.Stops("dweller")[0];
        Assert.Equal(sumo.Simulation.Subscription.Time - sumo.StepLength, underWay.ArrivalSeconds);
        Assert.Null(underWay.DepartureSeconds);
        Assert.Empty(sumo.Vehicles.CompletedStops("dweller"));
    }

    [RequiresSumoFact]
    public void AVehicleSumoDoesNotKnowIsRefusedAndTheConnectionCarriesOn()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.Stops);
        sumo.Simulation.Subscription.Subscribe();
        sumo.Step();

        Assert.Throws<TraCIException>(() => sumo.Vehicles.CompletedStops("nobody"));
        Assert.Throws<TraCIException>(() => sumo.Vehicles.Departure("nobody"));

        sumo.Step();
        Assert.Equal(2.0, sumo.Simulation.Subscription.Time);
    }
}
