using System.Globalization;
using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What an observer registered on the session is told: every SUMO frame read, with what SUMO did to its
/// vehicles in that step at the clock TraCI reports it at; every frame rendered; and the session's end.
/// </summary>
/// <remarks>
/// <para>Run with no CARLA at all, against a real SUMO, on the cross with two vehicles that each make a
/// stop -- one on its lane, one parked off it. SUMO is stepped a whole second at a time, twenty world
/// ticks to a step, so SUMO's own stamps and TraCI's clock are a second apart and a confusion of the two
/// is a second wrong rather than a rounding error.</para>
///
/// <para>When each event happened is taken from a second SUMO running the same scenario at the same step
/// alone, reading each list with a direct get rather than a subscription: two read paths that agree only
/// if the session's events are the step's own.</para>
/// </remarks>
public sealed class SumoDriveSessionStepObserverTests
{
    private const double StepSeconds = 1.0;
    private const int StepLimit = 120;

    private readonly ITestOutputHelper _output;

    public SumoDriveSessionStepObserverTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void EveryFrameReadAndEveryFrameRenderedIsToldToEveryObserverInTurn()
    {
        List<string> told = [];
        var first = new RecordingObserver("first", told);
        var second = new RecordingObserver("second", told);
        var late = new RecordingObserver("late", told);
        Drive drive = Run(CoSimFixtures.DwellScenario, [first, second], late: late);

        // Every SUMO frame the session read -- the fast-forward's, the lookahead after it and one per
        // advance -- each at TraCI's clock, a step apart, with the events of the step that produced it.
        Assert.Equal(drive.SumoSteps + 1, first.Steps.Count);
        Assert.True(first.Steps[0].AfterFastForward);
        Assert.All(first.Steps.Skip(1), step => Assert.False(step.AfterFastForward));
        for (int index = 0; index < first.Steps.Count; index++)
        {
            SumoStepRecord step = first.Steps[index];
            Assert.Equal(first.Steps[0].FrameSeconds + (index * StepSeconds), step.FrameSeconds, 9);
            Assert.Equal(step.FrameSeconds, step.Events.TimeSeconds);
            Assert.Equal(step.FrameSeconds, step.Pass.SimulatedTimeSeconds);
            Assert.Equal(Math.Max(0, index - 1) * drive.TicksPerStep, step.WorldTick);
            Assert.Equal(step.Pass.Population, first.Populations[index].Count);
        }

        // The fast-forward took no step, so its frame has no events; both vehicles were inserted later,
        // each told once, on the frame SUMO first reports it in.
        Assert.Equal(0, first.Steps[0].Events.Count);
        Assert.Single(first.Steps, step => step.Events.Departed.Contains("dweller"));
        Assert.Single(first.Steps, step => step.Events.Departed.Contains("parker"));
        Assert.All(first.Steps, step => Assert.Empty(step.NotInserted));

        // Every frame rendered, in order, at its own instant, with no world to draw a render set or a sun.
        Assert.Equal(drive.Ticks, first.Frames.Count);
        for (int tick = 0; tick < first.Frames.Count; tick++)
        {
            RenderedFrameRecord frame = first.Frames[tick];
            Assert.Equal((ulong)tick + 1, frame.Frame);
            Assert.Equal(tick, frame.TickIndex);
            Assert.Equal(drive.FirstRenderedSeconds + (tick * drive.WorldDeltaSeconds), frame.SimulatedTimeSeconds, 9);
            Assert.Equal(drive.Clock.IsCaptureTick(tick), frame.IsCaptureTick);
            Assert.True(frame.InWindow);
            Assert.Null(frame.RenderSet);
            Assert.Null(frame.Illumination);
        }

        // The end, once: SUMO had nothing left, and the last frame whose truth holds is the last told.
        SessionEndRecord end = Assert.Single(first.Ends);
        Assert.True(end.ScenarioFinished);
        Assert.Null(end.Stopped);
        Assert.Equal(first.Steps[^1].FrameSeconds, end.LastFrameSeconds);
        Assert.Equal(first.Frames[^1].Frame, end.LastRenderedFrame);
        Assert.Equal(first.Frames[^1].SimulatedTimeSeconds, end.LastRenderedSeconds);

        // Both observers were told the same records, the first before the second every time; one added
        // once the session had started was told nothing.
        Assert.Equal(first.Steps, second.Steps);
        Assert.Equal(first.Frames, second.Frames);
        Assert.Equal(first.Ends, second.Ends);
        Assert.All(told.Chunk(2), pair => Assert.Equal(["first", "second"], pair.Select(entry => entry.Split(' ')[0])));
        Assert.Empty(late.Steps);
        Assert.Empty(late.Frames);
        Assert.Empty(late.Ends);
    }

    [RequiresSumoFact]
    public void AStopIsToldBeginningAndEndingAtTheClockSumoListsItAtAndSumoStampsItAStepEarlier()
    {
        var observer = new RecordingObserver("asker", []) { AskOnEvents = true };
        Drive drive = Run(CoSimFixtures.DwellScenario, [observer]);
        Dictionary<(string Vehicle, string Kind), List<double>> listed = WhatSumoLists(CoSimFixtures.DwellScenario);

        // Every event the session told is the one a SUMO alone lists, at the same instant.
        Assert.Equal(Order(listed), Order(Told(observer.Steps)));

        // The stop on the lane, begun and ended once; the parking stay begun as a stop and as parking on
        // one frame, and back on the lane one frame before it ended as a stop.
        double dwellBegan = Assert.Single(listed[("dweller", "stop started")]);
        double dwellEnded = Assert.Single(listed[("dweller", "stop ended")]);
        double parkBegan = Assert.Single(listed[("parker", "stop started")]);
        double parkEnded = Assert.Single(listed[("parker", "stop ended")]);
        Assert.Equal(parkBegan, Assert.Single(listed[("parker", "parking started")]));
        Assert.Equal(parkEnded - StepSeconds, Assert.Single(listed[("parker", "parking ended")]));
        Assert.Equal(3.0, dwellEnded - dwellBegan, 9);

        // What SUMO answered on demand carries its own stamps, a step before the clock the events were told
        // at: a departure, and a stop's arrival and departure.
        foreach (string vehicleId in (string[])["dweller", "parker"])
        {
            double departed = Assert.Single(listed[(vehicleId, "departed")]);
            Assert.Equal(departed - StepSeconds, observer.Departures[vehicleId]!.Value, 9);
        }

        SumoStop dwell = Assert.Single(observer.CompletedStops["dweller"]!);
        Assert.Equal(dwellBegan - StepSeconds, dwell.ArrivalSeconds!.Value, 9);
        Assert.Equal(dwellEnded - StepSeconds, dwell.DepartureSeconds!.Value, 9);
        Assert.Equal(3.0, dwell.DurationSeconds);
        Assert.Null(dwell.UntilSeconds);
        Assert.Equal("ahead_0", dwell.LaneId);

        SumoStop park = Assert.Single(observer.CompletedStops["parker"]!);
        Assert.Equal(parkBegan - StepSeconds, park.ArrivalSeconds!.Value, 9);
        Assert.Equal(parkEnded - StepSeconds, park.DepartureSeconds!.Value, 9);
        Assert.True(park.IsParking);

        // Two departures and two stop endings: four questions, counted on the report.
        Assert.Equal(4, drive.Report.VehicleQueries);
        Assert.Contains("vehicle queries    4 asked of SUMO on demand, one round trip each", drive.Report.ToString());
        Assert.Equal(0, drive.Report.EmergencyStops);
        Assert.Contains("emergency stops    0, each a vehicle SUMO stopped dead at the end of a lane", drive.Report.ToString());
    }

    [RequiresSumoFact]
    public void AQuestionAboutAVehicleSumoNoLongerHasIsAnsweredWithNothingAndTheRunGoesOn()
    {
        List<string> unknown = [];
        var observer = new RecordingObserver("asker", [])
        {
            Ask = step =>
            {
                foreach (string vehicleId in step.Events.Arrived)
                {
                    if (step.Vehicles.Departure(vehicleId) is null && step.Vehicles.CompletedStops(vehicleId) is null)
                    {
                        unknown.Add(vehicleId);
                    }
                }
            },
        };

        Drive drive = Run(CoSimFixtures.DwellScenario, [observer]);

        Assert.Equal(["dweller", "parker"], unknown.Order(StringComparer.Ordinal));
        Assert.Null(drive.Report.Stopped);
        Assert.True(Assert.Single(observer.Ends).ScenarioFinished);
    }

    [RequiresSumoFact]
    public void ATeleportInARunThatAcceptedTeleportingIsCountedAndToldAtItsClock()
    {
        var observer = new RecordingObserver("watcher", []);
        Drive drive = Run(CoSimFixtures.JamScenario, [observer], allowTeleporting: true);
        Dictionary<(string Vehicle, string Kind), List<double>> listed = WhatSumoLists(CoSimFixtures.JamScenario);

        double jumped = Assert.Single(listed[("follower", "teleport started")]);
        SumoStepRecord told = Assert.Single(observer.Steps, step => step.Events.TeleportsStarted.Count > 0);
        Assert.Equal(["follower"], told.Events.TeleportsStarted);
        Assert.Equal(jumped, told.FrameSeconds);

        // With nowhere further along its route to put it, SUMO took it off the network on the same step.
        Assert.Contains("follower", told.Events.Arrived);

        Assert.Equal(1, drive.Report.Teleports);
        Assert.Equal([new SumoVehicleEvent("follower", jumped)], drive.Report.TeleportSamples);
        string report = drive.Report.ToString();
        Assert.Contains("teleports          1 begun", report);
        Assert.Contains($"  teleport         'follower' at t={Seconds(jumped)} s", report);
    }

    [RequiresSumoFact]
    public void AnObserverThatFailsAtTheEndStopsNeitherTheOthersBeingToldNorTheShutDown()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var failing = new RecordingObserver("failing", []) { FailAtTheEnd = true };
        var after = new RecordingObserver("after", []);
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.DwellScenario, [failing, after]);

        SumoDriveSession session = SumoDriveSession.Start(options);
        session.Advance();
        AggregateException failed = Assert.Throws<AggregateException>(session.Dispose);

        Exception failure = Assert.Single(failed.InnerExceptions);
        Assert.Contains("tell an observer (RecordingObserver) the session has ended", failure.Message);
        SessionEndRecord end = Assert.Single(after.Ends);
        Assert.False(end.ScenarioFinished);
        Assert.Throws<ObjectDisposedException>(() => session.Sumo.Step());
    }

    /// <summary>One world-less run of a scenario to its end, with the observers given.</summary>
    private Drive Run(string scenario, IReadOnlyList<ISumoStepObserver> observers,
                      bool allowTeleporting = false, ISumoStepObserver? late = null)
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(world, scenario, observers);
        options.AllowTeleporting = allowTeleporting;

        Drive drive;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            if (late is not null)
            {
                options.StepObservers.Add(late);
            }

            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }

            Assert.Equal(20, session.Clock.WorldTicksPerSumoStep);
            drive = new Drive(session.Report, session.Clock, session.FirstRenderedSeconds,
                              session.Clock.WorldDeltaSeconds, session.Clock.WorldTicksPerSumoStep);
        }

        _output.WriteLine(drive.Report.ToString());
        return drive;
    }

    private static SumoDriveSessionOptions Options(SyntheticWorld world, string scenario,
                                                   IReadOnlyList<ISumoStepObserver> observers)
    {
        var options = new SumoDriveSessionOptions(
            scenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            SumoStepOverrideSeconds = StepSeconds,
        };
        foreach (ISumoStepObserver observer in observers)
        {
            options.StepObservers.Add(observer);
        }

        return options;
    }

    /// <summary>
    /// Every event a SUMO running the scenario alone at the same step lists, read with a direct get on
    /// each step, by vehicle and kind, at the clock TraCI reported with it.
    /// </summary>
    private static Dictionary<(string Vehicle, string Kind), List<double>> WhatSumoLists(string scenario)
    {
        (int Variable, string Kind)[] lists =
        [
            (TraCIConstants.VAR_DEPARTED_VEHICLES_IDS, "departed"),
            (TraCIConstants.VAR_ARRIVED_VEHICLES_IDS, "arrived"),
            (TraCIConstants.VAR_STOP_STARTING_VEHICLES_IDS, "stop started"),
            (TraCIConstants.VAR_STOP_ENDING_VEHICLES_IDS, "stop ended"),
            (TraCIConstants.VAR_PARKING_STARTING_VEHICLES_IDS, "parking started"),
            (TraCIConstants.VAR_PARKING_ENDING_VEHICLES_IDS, "parking ended"),
            (TraCIConstants.VAR_TELEPORT_STARTING_VEHICLES_IDS, "teleport started"),
            (TraCIConstants.VAR_EMERGENCYSTOPPING_VEHICLES_IDS, "emergency stop"),
        ];
        Dictionary<(string Vehicle, string Kind), List<double>> listed = [];
        using SumoConnection sumo = CoSimFixtures.Open(scenario, new SumoLaunchOptions
        {
            ExtraArguments = ["--step-length", "1"],
            Output = _ => { },
        });
        for (int step = 0; step < StepLimit && sumo.Simulation.ExpectedVehicleCount > 0; step++)
        {
            sumo.Step();
            double now = sumo.Simulation.Time;
            foreach ((int variable, string kind) in lists)
            {
                foreach (string vehicleId in sumo.Simulation.Read(variable).AsStringList)
                {
                    Append(listed, (vehicleId, kind), now);
                }
            }
        }

        return listed;
    }

    /// <summary>Every event the observer was told, keyed the way <see cref="WhatSumoLists"/> keys them.</summary>
    private static Dictionary<(string Vehicle, string Kind), List<double>> Told(IEnumerable<SumoStepRecord> steps)
    {
        Dictionary<(string Vehicle, string Kind), List<double>> told = [];
        foreach (SumoStepRecord step in steps)
        {
            SumoStepEvents events = step.Events;
            foreach ((IReadOnlyList<string> vehicles, string kind) in new[]
                     {
                         (events.Departed, "departed"), (events.Arrived, "arrived"),
                         (events.StopsStarted, "stop started"), (events.StopsEnded, "stop ended"),
                         (events.ParkingStarted, "parking started"), (events.ParkingEnded, "parking ended"),
                         (events.TeleportsStarted, "teleport started"), (events.EmergencyStops, "emergency stop"),
                     })
            {
                foreach (string vehicleId in vehicles)
                {
                    Append(told, (vehicleId, kind), step.FrameSeconds);
                }
            }
        }

        return told;
    }

    private static List<string> Order(Dictionary<(string Vehicle, string Kind), List<double>> events) =>
        [.. events.SelectMany(entry => entry.Value.Select(at => $"{Seconds(at)} {entry.Key.Vehicle} {entry.Key.Kind}"))
                  .Order(StringComparer.Ordinal)];

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Append(Dictionary<(string Vehicle, string Kind), List<double>> events,
                                              (string Vehicle, string Kind) key, double at)
    {
        if (!events.TryGetValue(key, out List<double>? instants))
        {
            instants = [];
            events[key] = instants;
        }

        instants.Add(at);
    }

    /// <summary>What one run left behind.</summary>
    private sealed record Drive(
        CoSimRunReport Report,
        CoSimClock Clock,
        double FirstRenderedSeconds,
        double WorldDeltaSeconds,
        int TicksPerStep)
    {
        public long SumoSteps => Report.SumoSteps;

        public long Ticks => Report.Ticks;
    }

    /// <summary>
    /// An observer that keeps everything it is told, copying what a record only lends, and asks SUMO
    /// what its caller has it ask.
    /// </summary>
    private sealed class RecordingObserver(string name, List<string> told) : ISumoStepObserver
    {
        public List<SumoStepRecord> Steps { get; } = [];

        /// <summary>The vehicles each step's frames held, copied as the step was told.</summary>
        public List<HashSet<string>> Populations { get; } = [];

        public List<RenderedFrameRecord> Frames { get; } = [];

        public List<SessionEndRecord> Ends { get; } = [];

        /// <summary>Each vehicle's departure, asked on the step it was listed as departed.</summary>
        public Dictionary<string, double?> Departures { get; } = [];

        /// <summary>Each vehicle's completed stops, asked on the step it was last listed as ending one.</summary>
        public Dictionary<string, IReadOnlyList<SumoStop>?> CompletedStops { get; } = [];

        /// <summary>
        /// Ask SUMO once for a vehicle's departure when it is listed as departed, and once for its
        /// completed stops when it is listed as ending one.
        /// </summary>
        public bool AskOnEvents { get; init; }

        /// <summary>Anything else to ask SUMO on each step, through the step's own questions.</summary>
        public Action<SumoStepRecord>? Ask { get; init; }

        public bool FailAtTheEnd { get; init; }

        public void OnSumoStep(SumoStepRecord step)
        {
            told.Add($"{name} step {step.FrameSeconds}");
            Steps.Add(step);
            Populations.Add([.. step.Frames.Keys]);
            if (AskOnEvents)
            {
                foreach (string vehicleId in step.Events.Departed)
                {
                    Departures[vehicleId] = step.Vehicles.Departure(vehicleId);
                }

                foreach (string vehicleId in step.Events.StopsEnded)
                {
                    CompletedStops[vehicleId] = step.Vehicles.CompletedStops(vehicleId);
                }
            }

            Ask?.Invoke(step);
        }

        public void OnFrameRendered(RenderedFrameRecord frame)
        {
            told.Add($"{name} frame {frame.Frame}");
            Frames.Add(frame);
        }

        public void OnSessionEnded(SessionEndRecord end)
        {
            told.Add($"{name} end");
            Ends.Add(end);
            if (FailAtTheEnd)
            {
                throw new InvalidOperationException("an observer that fails at the end");
            }
        }
    }
}
