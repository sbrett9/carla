using System.Globalization;
using System.Text.Json.Nodes;
using CarlaNet.Types.Supervision;
using Xunit.Abstractions;
using static CarlaNet.CoSim.Tests.PlanRows;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session on a compiled scenario binds its plan as it runs: against a real SUMO with no CARLA, each
/// interval opens and closes on the event SUMO listed, every rendered frame carries what is in force at its
/// instant, two runs bind the same rows, a window that opens mid-dwell carries the dwell from its first
/// frame, and a plan subject SUMO drops fails the run.
/// </summary>
public sealed class SupervisionBinderSessionTests
{
    private const int StepLimit = 60;

    private readonly ITestOutputHelper _output;

    public SupervisionBinderSessionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void EachIntervalOpensAndClosesOnTheEventSumoListedAndEveryFrameCarriesWhatIsInForceAtItsInstant()
    {
        using CompiledFixture compiled = Supervised();
        var frames = new FrameSupervision();

        Run run = Drive(compiled, stepSeconds: 1.0, frames);

        _output.WriteLine(string.Join(Environment.NewLine, run.Intervals.Select(Describe)));
        SupervisionIntervalRecord standoff = IntervalOf(run, "standoff");
        Assert.Equal((11.0, 31.0, ClosedBy.Trigger), (standoff.CommittedStartSeconds, standoff.CommittedEndSeconds, standoff.ClosedBy));
        Assert.Equal((null, 20.0), (standoff.DeclaredStartSeconds, standoff.DeclaredDurationSeconds));
        Assert.Null(standoff.ObservedStartSeconds);

        SupervisionIntervalRecord approach = IntervalOf(run, "approach");
        Assert.Equal((null, ClosedBy.Trigger, 3.0), (approach.CommittedStartSeconds, approach.ClosedBy, approach.ClosedAtSeconds));

        SupervisionIntervalRecord exit = IntervalOf(run, "exit");
        Assert.Equal((10.0, ClosedBy.EntityArrived, 16.0), (exit.CommittedStartSeconds, exit.ClosedBy, exit.ClosedAtSeconds));

        SupervisionIntervalRecord vacancy = IntervalOf(run, "vacancy");
        Assert.Equal((ClosedBy.SlotUnrealised, 9.0), (vacancy.ClosedBy, vacancy.ClosedAtSeconds));
        Assert.All(run.Intervals, interval => Assert.False(interval.BegunBeforeWindow));
        Assert.Empty(run.Defects);

        // Every frame, as the server would be told it before the frame's cue.
        Assert.Equal("annotated Supervised/approach:approach", frames.At(1.0, "dweller"));
        Assert.Equal("annotated Supervised/approach:approach", frames.At(2.95, "dweller"));
        Assert.Equal("unlabelled", frames.At(3.0, "dweller"));
        Assert.Equal("unlabelled", frames.At(10.95, "dweller"));
        Assert.Equal("annotated Supervised/standoff:standoff", frames.At(11.0, "dweller"));
        Assert.Equal("annotated Supervised/standoff:standoff", frames.At(30.95, "dweller"));
        Assert.Equal("unlabelled", frames.At(31.0, "dweller"));
        Assert.Equal("nominal Supervised/haul:", frames.At(20.0, "parker"));
        Assert.Equal("nominal series:relief:", frames.At(9.95, "passer"));
        Assert.Equal("annotated Supervised/transit:exit", frames.At(10.0, "passer"));
        Assert.Equal("annotated cohort:corridor:", frames.At(12.0, "corridor.2"));
        Assert.Equal("unlabelled", frames.At(4.95, "corridor.0"));
        Assert.Equal("annotated cohort:corridor:", frames.At(5.0, "corridor.0"));
        Assert.Equal(string.Empty, frames.AbsencesAt(4.95));
        Assert.Equal("Supervised/missing", frames.AbsencesAt(5.0));
        Assert.Equal("Supervised/missing", frames.AbsencesAt(8.95));
        Assert.Equal(string.Empty, frames.AbsencesAt(9.0));

        // Asked of SUMO: the dweller's completed stops as its stop started and as it ended, and the left-turner's
        // route index on insertion and on reaching its second part -- not on the junction's internal edge, and
        // not again once nothing waits on it -- and nothing about any other vehicle.
        Assert.Equal(2 + 2, run.VehicleQueries);
    }

    [RequiresSumoFact]
    public void TwoRunsOfOneScenarioBindTheSameRowsDifferingOnlyInTheirTimes()
    {
        // 06 D6.8: the row set is the plan's, so a run with another step and a run whose window opens
        // late bind the same (instance, participant, phase) triples as the first; only the times and the
        // outcomes may differ.
        using CompiledFixture compiled = Supervised();
        Run whole = Drive(compiled, stepSeconds: 1.0);
        Run finer = Drive(compiled, stepSeconds: 0.5);
        Run late = Drive(compiled, stepSeconds: 1.0, warmUpTo: 15.0);

        (string, string?, string)[] triples = Triples(whole);
        Assert.Equal(PlanTriples(whole.Plan), triples);
        Assert.Equal(triples, Triples(finer));
        Assert.Equal(triples, Triples(late));

        // And the times did differ.
        Assert.NotEqual(IntervalOf(whole, "standoff").CommittedStartSeconds, IntervalOf(finer, "standoff").CommittedStartSeconds);
        Assert.NotEqual(IntervalOf(whole, "approach").BegunBeforeWindow, IntervalOf(late, "approach").BegunBeforeWindow);
    }

    [RequiresSumoFact]
    public void AWindowOpeningMidDwellCarriesTheDwellFromItsFirstFrameWithItsPreWindowStart()
    {
        using CompiledFixture compiled = Supervised();
        var frames = new FrameSupervision();

        // SUMO is fast-forwarded to 15 s, four seconds into the dweller's twenty-second stop.
        Run run = Drive(compiled, stepSeconds: 1.0, frames, warmUpTo: 15.0, beforeTheFirstTick: session =>
        {
            Assert.Equal("annotated Supervised/standoff:standoff",
                         FrameSupervision.Describe(session.Supervision.Of("dweller")));
            Assert.Equal("annotated Supervised/transit:exit",
                         FrameSupervision.Describe(session.Supervision.Of("passer")));
        });

        Assert.Equal("annotated Supervised/standoff:standoff", frames.At(15.0, "dweller"));
        SupervisionIntervalRecord standoff = IntervalOf(run, "standoff");
        Assert.Equal((11.0, true, null), (standoff.CommittedStartSeconds, standoff.BegunBeforeWindow, standoff.ObservedStartSeconds));
        Assert.Equal((31.0, ClosedBy.Trigger), (standoff.CommittedEndSeconds, standoff.ClosedBy));

        // A phase entered before the window: open, with no instant invented for it.
        SupervisionIntervalRecord exit = IntervalOf(run, "exit");
        Assert.Equal((null, true, ClosedBy.EntityArrived), (exit.CommittedStartSeconds, exit.BegunBeforeWindow, exit.ClosedBy));

        // Over before the window: its declared seconds, and nothing in force on any frame.
        SupervisionIntervalRecord approach = IntervalOf(run, "approach");
        Assert.Equal((ClosedBy.Trigger, 3.0, true), (approach.ClosedBy, approach.ClosedAtSeconds, approach.BegunBeforeWindow));
        Assert.Equal(ClosedBy.SlotUnrealised, IntervalOf(run, "vacancy").ClosedBy);
        Assert.Equal(string.Empty, frames.AbsencesAt(15.0));
        Assert.Empty(run.Defects);
    }

    [RequiresSumoFact]
    public void UnderAWorldTheEntryIsObservedOnTheFrameSumoCommittedItTheStopAtItsStandstillAndTheServerIsTold()
    {
        using CompiledFixture compiled = CompiledFixture.Write(routes: CoSimFixtures.SupervisedRoutes);
        compiled.WritePlan(WithRows(compiled.PlanDocument(),
            [
                Instance("dwell", "annotated", ["test:dwell"], [("dweller", "subject")],
                         Interval("dweller", "trip", Anchor(Depart())),
                         Interval("dweller", "standoff", Anchor(Stop(0, "ahead_0", 60.0), StopEnd(0, "ahead_0", 60.0)),
                                  declaredDuration: 20.0)),
            ],
            entities: [Entity("dweller", "annotated", "Supervised/dwell")]));
        compiled.WriteLock(SolarLeaseTests.PortEpoch());
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var options = new SumoDriveSessionOptions(compiled.Scenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
                                                  "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

        IReadOnlyList<SupervisionIntervalRecord> intervals;
        IReadOnlyList<string> defects;
        double step;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            step = session.Clock.SumoStepSeconds;
            while (session.Advance() && session.RenderedTimeSeconds < 40.0)
            {
            }

            intervals = session.SupervisionBinder!.Intervals;
            defects = [.. session.SupervisionBinder.Defects];
        }

        _output.WriteLine(string.Join(Environment.NewLine, intervals.Select(Describe)));
        SupervisionIntervalRecord trip = intervals.Single(interval => interval.Phase == "trip");
        SupervisionIntervalRecord standoff = intervals.Single(interval => interval.Phase == "standoff");

        // Drawn from the frame SUMO first reports it in (03 D3.6): the two onsets are one instant.
        Assert.NotNull(trip.CommittedStartSeconds);
        Assert.Equal(trip.CommittedStartSeconds!.Value, trip.ObservedStartSeconds!.Value, 6);

        // Standing still within the step it was interpolated into the stop over, never after it.
        double committed = standoff.CommittedStartSeconds!.Value;
        double observed = standoff.ObservedStartSeconds!.Value;
        _output.WriteLine($"stop committed {committed} s, observed {observed} s, step {step} s");
        Assert.InRange(observed, committed - step - 1e-6, committed + 1e-6);
        Assert.Equal(ClosedBy.Trigger, standoff.ClosedBy);
        Assert.Empty(defects);

        // What is in force reached the server, for the body drawing the dweller.
        Assert.Contains(carla.SupervisionWrites, write => write.Change.Bodies.Any(body =>
            body.Supervision.State == SupervisionState.Annotated
            && body.Supervision.Annotations.Any(annotation => annotation.InstanceId == "Supervised/dwell"
                                                              && annotation.Phase == "standoff")));
        Assert.Equal("RightAngleTurn", carla.SupervisionWrites[0].Change.Plan!.PlanId);
    }

    [RequiresSumoFact]
    public void APlanSubjectSumoNeverInsertsFailsTheRunNamingIt()
    {
        string configuration = File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<collision.action value=\"warn\"/>",
            "<collision.action value=\"warn\"/>\n        <max-depart-delay value=\"3\"/>", StringComparison.Ordinal);
        using CompiledFixture compiled = CompiledFixture.Write(configuration: configuration, routes: CoSimFixtures.UndepartedRoutes);
        compiled.WritePlan(WithRows(compiled.PlanDocument(),
            [Instance("haul", "nominal", ["test:routine"], [("late", "subject")], Interval("late", "trip", Anchor(Depart())))],
            entities: [Entity("blocker", "unlabelled"), Entity("late", "nominal", "Supervised/haul")]));
        compiled.WriteLock(SolarLeaseTests.PortEpoch());
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        using SumoDriveSession session = SumoDriveSession.Start(Options(compiled.Scenario, world, 1.0, null));
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(() =>
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }
        });

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Window, refused.Stage);
        Assert.Contains("SUMO never inserted a subject of the supervision plan RightAngleTurn: 'late', given up between "
                        + "5 s and 6 s", refused.Message);
        Assert.NotNull(session.Report.Stopped);
        SupervisionIntervalRecord trip = Assert.Single(session.SupervisionBinder!.Intervals);
        Assert.Equal((ClosedBy.NeverInserted, 6.0), (trip.ClosedBy, trip.ClosedAtSeconds));
    }

    /// <summary>
    /// The fixture's every kind of subject, compiled: the dweller's stop and an unanchored approach, the
    /// left-turner's second route part, a nominal instance and a nominal series member, an absence and an
    /// annotated flow.
    /// </summary>
    internal static CompiledFixture Supervised()
    {
        CompiledFixture compiled = CompiledFixture.Write(routes: CoSimFixtures.SupervisedRoutes);
        compiled.WritePlan(WithRows(compiled.PlanDocument(),
            [
                Instance("standoff", "annotated", ["test:standoff"], [("dweller", "subject")],
                         Interval("dweller", "standoff", Anchor(Stop(0, "ahead_0", 60.0), StopEnd(0, "ahead_0", 60.0)),
                                  declaredDuration: 20.0)),
                Instance("approach", "annotated", ["test:approach"], [("dweller", "subject")],
                         Interval("dweller", "approach", declaredStart: 1.0, declaredEnd: 3.0)),
                Instance("transit", "annotated", ["test:transit"], [("passer", "subject")],
                         Interval("passer", "exit", Anchor(Phase(1, 1, "turn_west")))),
                Instance("haul", "nominal", ["test:routine"], [("parker", "subject")]),
                Absence("missing", "relief", "s2", "s2", ["test:missing"], "site", 5.0, 9.0),
            ],
            series: [Series("relief", "nominal", ["test:posting"], ("s1", "passer", 2.0, 30.0), ("s2", null, 5.0, 9.0))],
            cohorts: [Cohort("corridor", "annotated", "test:convoy")],
            entities:
            [
                Entity("dweller", "annotated", "Supervised/standoff", "Supervised/approach"),
                Entity("parker", "nominal", "Supervised/haul"),
                Entity("passer", "annotated", "Supervised/transit", "series:relief"),
            ]));
        compiled.WriteLock(SolarLeaseTests.PortEpoch());
        return compiled;
    }

    private Run Drive(CompiledFixture compiled, double stepSeconds, FrameSupervision? frames = null,
                      double? warmUpTo = null, Action<SumoDriveSession>? beforeTheFirstTick = null)
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(compiled.Scenario, world, stepSeconds, frames);
        options.WarmUpToSimulatedSecond = warmUpTo ?? 0.0;
        Run run;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            if (frames is not null)
            {
                frames.Table = session.Supervision;
            }

            beforeTheFirstTick?.Invoke(session);
            for (int step = 0; step < StepLimit / stepSeconds && session.Advance(); step++)
            {
            }

            run = new Run(session.SupervisionBinder!.Plan, session.SupervisionBinder);
            run.CountQueries(session.Report.VehicleQueries);
        }

        run.Snapshot();
        return run;
    }

    private static SumoDriveSessionOptions Options(string scenario, SyntheticWorld world, double stepSeconds,
                                                   FrameSupervision? frames)
    {
        var options = new SumoDriveSessionOptions(
            scenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            SumoStepOverrideSeconds = stepSeconds,
        };
        if (frames is not null)
        {
            options.StepObservers.Add(frames);
        }

        return options;
    }

    private static SupervisionIntervalRecord IntervalOf(Run run, string phase) =>
        run.Intervals.Single(interval => interval.Phase == phase);

    private static (string, string?, string)[] Triples(Run run) =>
        [.. run.Intervals.Select(interval => (interval.InstanceId, interval.EntityId, interval.Phase)).Order()];

    private static (string, string?, string)[] PlanTriples(SupervisionPlan plan) =>
        [.. plan.Instances.SelectMany(instance => instance.Intervals.Select(interval =>
            (instance.InstanceId, interval.EntityId, interval.Phase))).Order()];

    private static string Describe(SupervisionIntervalRecord interval) =>
        $"{interval.InstanceId} {interval.EntityId} {interval.Phase}: committed {interval.CommittedStartSeconds}"
        + $"..{interval.CommittedEndSeconds}, closed {interval.ClosedBy} at {interval.ClosedAtSeconds}, "
        + $"before window {interval.BegunBeforeWindow}";

    /// <summary>What one run bound, kept once the session is gone.</summary>
    private sealed class Run(SupervisionPlan plan, SupervisionBinder binder)
    {
        public SupervisionPlan Plan { get; } = plan;

        public IReadOnlyList<SupervisionIntervalRecord> Intervals { get; private set; } = [];

        public IReadOnlyList<string> Defects { get; private set; } = [];

        public long VehicleQueries { get; private set; }

        public void Snapshot()
        {
            Intervals = binder.Intervals;
            Defects = [.. binder.Defects];
        }

        public void CountQueries(long count) => VehicleQueries = count;
    }

    /// <summary>
    /// What is in force on every rendered frame, read before the binder is told the frame -- the state the
    /// session put on the server before that frame's cue.
    /// </summary>
    private sealed class FrameSupervision : ISumoStepObserver
    {
        private readonly Dictionary<long, (Dictionary<string, string> Vehicles, string Absences)> _frames = [];

        public DriveSupervision? Table { get; set; }

        public static string Describe(SupervisionInForce held) =>
            held.State == SupervisionState.Unlabelled
                ? "unlabelled"
                : $"{CoreVocabulary.Name(held.State)} "
                  + string.Join(", ", held.Annotations.Select(annotation => $"{annotation.InstanceId}:{annotation.Phase}"));

        public void OnSumoStep(SumoStepRecord step)
        {
        }

        public void OnFrameRendered(RenderedFrameRecord frame)
        {
            DriveSupervision table = Table!;
            _frames[Key(frame.SimulatedTimeSeconds)] = (
                table.Vehicles.ToDictionary(entry => entry.Key, entry => Describe(entry.Value)),
                string.Join(", ", table.Absences.Select(absence => absence.InstanceId)));
        }

        public void OnSessionEnded(SessionEndRecord end)
        {
        }

        public string At(double seconds, string vehicle) =>
            _frames[Key(seconds)].Vehicles.GetValueOrDefault(vehicle, "unlabelled");

        public string AbsencesAt(double seconds) => _frames[Key(seconds)].Absences;

        private static long Key(double seconds) => (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
    }
}
