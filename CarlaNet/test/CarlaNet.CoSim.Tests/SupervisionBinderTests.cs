using System.Reflection;
using System.Text.Json.Nodes;
using CarlaNet.Sumo;
using CarlaNet.Types.Supervision;
using static CarlaNet.CoSim.Tests.PlanRows;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The supervision binder as a state machine, fed streams of SUMO's events a test writes: each anchor
/// commits its interval at the clock of the step that listed the event, each closing reason is the one its
/// cause names, what is in force changes on the frame at its instant and never before the window, and an
/// onset that did not happen stays null.
/// </summary>
public sealed class SupervisionBinderTests
{
    [Fact]
    public void ADepartureCommitsItsIntervalAtTheClockOfTheStepThatListedItAndAnArrivalClosesIt()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("transit", "annotated", ["test:transit"], [("v", "subject")],
                     Interval("v", "transit", Anchor(Depart()))),
        ])));

        feed.Step(0);
        feed.Step(1, departed: ["v"]);

        // Nothing is in force before the frame at the instant SUMO inserted it.
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("v").State);
        feed.Render(0);
        Assert.Equal((SupervisionState.Annotated, "Supervised/transit:transit"), feed.Of("v"));
        Assert.Equal("subject", Assert.Single(feed.Table.Of("v").Annotations).Role);
        Assert.Equal(["test:transit"], Assert.Single(feed.Table.Of("v").Annotations).Labels);

        feed.Step(2);
        feed.Render(1, drawn: ["v"]);
        feed.Step(3, arrived: ["v"]);

        // Still in force for the frame at 2 s, the last that shows it; gone from the frame at 3 s.
        Assert.Equal(SupervisionState.Annotated, feed.Of("v").State);
        feed.Render(2, drawn: ["v"]);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("v").State);

        SupervisionIntervalRecord transit = feed.Interval("transit", "transit");
        Assert.Equal((1.0, 1.0, 1UL), (transit.CommittedStartSeconds, transit.ObservedStartSeconds, transit.ObservedStartFrame - 1));
        Assert.Equal((ClosedBy.EntityArrived, 3.0, 3.0), (transit.ClosedBy, transit.ClosedAtSeconds, transit.CommittedEndSeconds));
        Assert.False(transit.BegunBeforeWindow);
        Assert.Empty(feed.Binder.Defects);
        Assert.Equal(["opened", "closed"], feed.Sink.Told.Select(told => told.Told));
        Assert.Equal(SupervisionIntervalStatus.Open, feed.Sink.Told[0].Interval.Status);
        Assert.Empty(feed.Answers.Asked);
    }

    [Fact]
    public void ADepartureDrawnOnAnotherFrameThanSumoCommittedItIsRecordedAsADefect()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("transit", "annotated", ["test:transit"], [("v", "subject")],
                     Interval("v", "transit", Anchor(Depart()))),
        ])));

        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0);
        feed.Step(2);
        feed.Render(1);
        feed.Step(3);
        feed.Render(2, drawn: ["v"]);

        Assert.Equal((1.0, 2.0), (feed.Interval("transit", "transit").CommittedStartSeconds,
                                  feed.Interval("transit", "transit").ObservedStartSeconds));
        Assert.Contains("SUMO inserted it at 1 s and the first frame drew it at 2 s", Assert.Single(feed.Binder.Defects));
    }

    [Fact]
    public void AStopAnchorCommitsOnTheStopWhoseIndexIsTheCountSumoHadCompletedAndItsBodyIsObservedStanding()
    {
        // The second of two stops: the first is no anchor's, so the index is SUMO's count, not the binder's.
        var feed = new BinderFeed(Read(Plan([
            Instance("standoff", "annotated", ["test:standoff"], [("v", "subject")],
                     Interval("v", "standoff", Anchor(Stop(1, "ahead_0", 60.0), StopEnd(1, "ahead_0", 60.0)),
                              declaredDuration: 20.0)),
        ])));
        feed.Edges["v"] = "ahead";

        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0);
        feed.Step(2, stopsStarted: ["v"]);
        feed.Render(1);
        feed.Answers.Completed["v"] = [BinderFeed.StopAt("ahead_0", 30.0, 1.0, 3.0)];
        feed.Step(4, stopsEnded: ["v"]);
        feed.Render(2);
        Assert.Equal(SupervisionIntervalStatus.Planned, feed.Interval("standoff", "standoff").Status);

        feed.Step(8, stopsStarted: ["v"]);
        Assert.Equal(SupervisionIntervalStatus.Open, feed.Interval("standoff", "standoff").Status);

        // Interpolated into the stop over the step that ends at it, the body is standing a step early.
        feed.Render(4, speeds: [("v", 3.0)]);
        feed.Render(7, speeds: [("v", 0.1)]);
        Assert.Equal((SupervisionState.Annotated, "Supervised/standoff:standoff"), feed.Of("v"));

        feed.Answers.Completed["v"].Add(BinderFeed.StopAt("ahead_0", 60.0, 7.0, 27.0));
        feed.Step(28, stopsEnded: ["v"]);
        feed.Render(27, speeds: [("v", 0.0)]);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("v").State);

        SupervisionIntervalRecord standoff = feed.Interval("standoff", "standoff");
        Assert.Equal((8.0, 7.0, 28.0), (standoff.CommittedStartSeconds, standoff.ObservedStartSeconds, standoff.CommittedEndSeconds));
        Assert.Equal((ClosedBy.Trigger, 28.0), (standoff.ClosedBy, standoff.ClosedAtSeconds));
        Assert.Equal((null, 20.0), (standoff.DeclaredStartSeconds, standoff.DeclaredDurationSeconds));
        Assert.Equal(["completed v", "completed v", "completed v", "completed v"], feed.Answers.Asked);
    }

    [Fact]
    public void AStopWhoseDrawnBodyNeverStoodStillClosesAsThePhysicalPredicateNeverHeld()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("standoff", "annotated", ["test:standoff"], [("v", "subject")],
                     Interval("v", "standoff", Anchor(Stop(0, "approach_0", 60.0), StopEnd(0, "approach_0", 60.0)))),
        ])));

        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0);
        feed.Step(2, stopsStarted: ["v"]);
        feed.Render(1, speeds: [("v", 4.0)]);
        feed.Answers.Completed["v"] = [BinderFeed.StopAt("approach_0", 60.0, 1.0, 2.0)];
        feed.Step(3, stopsEnded: ["v"]);
        feed.Render(2, speeds: [("v", 4.0)]);

        SupervisionIntervalRecord standoff = feed.Interval("standoff", "standoff");
        Assert.Equal(ClosedBy.PhysicalPredicateNeverHeld, standoff.ClosedBy);
        Assert.Null(standoff.ObservedStartSeconds);
        Assert.Contains("never stood still", Assert.Single(feed.Binder.Defects));

        // With no world, nothing is observed and nothing is a defect: the same stop closes on its trigger.
        var blind = new BinderFeed(feed.Binder.Plan);
        blind.Step(0);
        blind.Step(1, departed: ["v"]);
        blind.Render(0);
        blind.Step(2, stopsStarted: ["v"]);
        blind.Render(1);
        blind.Answers.Completed["v"] = [BinderFeed.StopAt("approach_0", 60.0, 1.0, 2.0)];
        blind.Step(3, stopsEnded: ["v"]);
        Assert.Equal(ClosedBy.Trigger, blind.Interval("standoff", "standoff").ClosedBy);
        Assert.Empty(blind.Binder.Defects);
    }

    [Fact]
    public void APhaseCommitsWhenSumoFirstReportsTheVehicleAtItsRouteIndexAskedOnlyWhenTheEdgeChanges()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("circuit", "annotated", ["test:circuit"], [("v", "subject")],
                     Interval("v", "lap", Anchor(Phase(1, 1, "turn_west"), Phase(2, 3, "approach")))),
        ])));

        feed.Answers.RouteIndices["v"] = 0;
        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0);
        feed.Step(2);
        feed.Render(1);
        feed.Edges["v"] = ":centre_2";
        feed.Step(3);
        feed.Render(2);
        feed.Edges["v"] = "turn_west";
        feed.Answers.RouteIndices["v"] = 1;
        feed.Step(4);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("v").State);
        feed.Render(3);
        Assert.Equal((SupervisionState.Annotated, "Supervised/circuit:lap"), feed.Of("v"));

        feed.Step(5);
        feed.Edges["v"] = "ahead";
        feed.Answers.RouteIndices["v"] = 2;
        feed.Step(6);
        feed.Edges["v"] = "approach";
        feed.Answers.RouteIndices["v"] = 3;
        feed.Step(7);

        SupervisionIntervalRecord lap = feed.Interval("circuit", "lap");
        Assert.Equal((4.0, 7.0, ClosedBy.Trigger), (lap.CommittedStartSeconds, lap.CommittedEndSeconds, lap.ClosedBy));
        Assert.Null(lap.ObservedStartSeconds);

        // Once on insertion and once per edge change after it, the junction's internal edge not asked about.
        Assert.Equal(4, feed.Answers.Asked.Count);
        Assert.All(feed.Answers.Asked, asked => Assert.Equal("route index v", asked));
    }

    [Fact]
    public void AnUnanchoredIntervalOpensAndClosesOnItsDeclaredSecondsWithNoCommittedOnset()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("approach", "annotated", ["test:approach"], [("v", "subject")],
                     Interval("v", "approach", declaredStart: 2.5, declaredEnd: 4.5)),
        ])));

        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0);
        feed.Step(2);
        feed.Render(1);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("v").State);
        feed.Step(3);
        feed.Render(2);
        Assert.Equal(SupervisionState.Annotated, feed.Of("v").State);
        feed.Step(4);
        feed.Render(3);
        feed.Step(5);
        feed.Render(4);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("v").State);

        SupervisionIntervalRecord approach = feed.Interval("approach", "approach");
        Assert.Equal((null, null, ClosedBy.Trigger, 4.5),
                     (approach.CommittedStartSeconds, approach.CommittedEndSeconds, approach.ClosedBy, approach.ClosedAtSeconds));
        Assert.Equal(2.5, approach.DeclaredStartSeconds);
    }

    [Fact]
    public void AnAbsenceIsInForceForTheWorldOverItsDeclaredSecondsAndClosesUnrealised()
    {
        var feed = new BinderFeed(Read(Plan(
            [Absence("missing", "relief", "s2", "s2", ["test:missing"], "site", 2.0, 4.0)],
            series: [Series("relief", "nominal", ["test:posting"], ("s1", "g", 0.0, 10.0), ("s2", null, 2.0, 4.0))],
            entities: [Entity("g", "nominal", "series:relief")])));

        feed.Step(0);
        feed.Step(1);
        feed.Render(0);
        Assert.Empty(feed.Table.Absences);
        feed.Step(2);
        feed.Render(1);
        AbsenceInForce open = Assert.Single(feed.Table.Absences);
        Assert.Equal(("Supervised/missing", "vacancy"), (open.InstanceId, open.Phase));
        Assert.Equal(["site"], open.Areas);
        Assert.Equal(["test:missing"], open.Labels);
        feed.Step(3);
        feed.Render(2);
        feed.Step(4);
        feed.Render(3);
        Assert.Empty(feed.Table.Absences);

        SupervisionIntervalRecord vacancy = feed.Interval("missing", "vacancy");
        Assert.Equal((null, ClosedBy.SlotUnrealised, 4.0, Realisation.Absent),
                     (vacancy.EntityId, vacancy.ClosedBy, vacancy.ClosedAtSeconds, vacancy.Realisation));
        Assert.Null(vacancy.CommittedStartSeconds);
        Assert.Null(vacancy.ObservedStartSeconds);
    }

    [Fact]
    public void FlowMembersCarryTheirCohortSeriesMembersTheirSeriesAndAnnotatedBeatsNominal()
    {
        var feed = new BinderFeed(Read(Plan(
            [
                Instance("haul", "nominal", ["test:routine"], [("h", "subject")]),
                Instance("transit", "annotated", ["test:transit"], [("g", "subject")],
                         Interval("g", "transit", declaredStart: 3.0, declaredEnd: 5.0)),
            ],
            series: [Series("relief", "nominal", ["test:posting"], ("s1", "g", 0.0, 10.0))],
            cohorts: [Cohort("corridor", "annotated", "test:convoy"), Cohort("quiet", "unlabelled")],
            entities: [Entity("g", "annotated", "Supervised/transit", "series:relief"), Entity("h", "nominal", "Supervised/haul")])));

        feed.Step(0);
        feed.Step(1, departed: ["h", "g", "corridor.0", "corridor.extra", "quiet.0"]);
        feed.Render(0);

        // A nominal instance with no interval is in force for the vehicle's whole life, labels and all.
        Assert.Equal((SupervisionState.Nominal, "Supervised/haul:"), feed.Of("h"));
        Assert.Equal((SupervisionState.Nominal, "series:relief:"), feed.Of("g"));
        Assert.Equal("test:guard", Assert.Single(feed.Table.Of("g").Annotations).Role);
        Assert.Equal((SupervisionState.Annotated, "cohort:corridor:"), feed.Of("corridor.0"));
        Assert.Equal(["test:convoy"], Assert.Single(feed.Table.Of("corridor.0").Annotations).Labels);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("corridor.extra").State);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("quiet.0").State);
        Assert.Equal(3, feed.Table.Vehicles.Count);

        feed.Step(2);
        feed.Render(1);
        feed.Step(3);
        feed.Render(2);

        // While its transit is open, the series member is annotated, and states only what it is annotated by.
        Assert.Equal((SupervisionState.Annotated, "Supervised/transit:transit"), feed.Of("g"));
        feed.Step(4, arrived: ["corridor.0"]);
        feed.Render(3);
        feed.Step(5);
        feed.Render(4);
        Assert.Equal((SupervisionState.Nominal, "series:relief:"), feed.Of("g"));
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("corridor.0").State);
    }

    [Fact]
    public void ABodyLostToARenderLimitEndsNothingAndTheGapIsRecordedAsNotDrawn()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("transit", "annotated", ["test:transit"], [("v", "subject")],
                     Interval("v", "transit", Anchor(Depart()))),
        ])));

        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0);
        feed.Step(2);
        feed.OutsideTheLimit.Add("v");
        feed.Step(3);
        feed.Step(4);
        feed.OutsideTheLimit.Clear();
        feed.Step(5);
        feed.OutsideTheLimit.Add("v");
        feed.Step(6, arrived: ["v"]);

        SupervisionIntervalRecord transit = feed.Interval("transit", "transit");
        Assert.Equal(ClosedBy.EntityArrived, transit.ClosedBy);
        Assert.Equal([new NotDrawnSpan(3.0, 5.0)], transit.NotDrawn);
    }

    [Theory]
    [InlineData("arrived", ClosedBy.EntityArrived)]
    [InlineData("teleported", ClosedBy.SumoRemoved)]
    [InlineData("vanished", ClosedBy.SumoRemoved)]
    [InlineData("window", ClosedBy.CaptureWindowEnd)]
    [InlineData("finished", ClosedBy.ScenarioEnd)]
    public void EachWayAVehicleOrTheRunEndsClosesTheIntervalWithItsOwnReason(string how, ClosedBy expected)
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("transit", "annotated", ["test:transit"], [("v", "subject")],
                     Interval("v", "transit", Anchor(Depart()))),
        ])));
        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0);
        switch (how)
        {
            case "arrived":
                feed.Step(2, arrived: ["v"]);
                break;
            case "teleported":
                feed.Step(2, arrived: ["v"], teleports: ["v"]);
                break;
            case "vanished":
                feed.Step(2, vanished: ["v"]);
                break;
            default:
                feed.Step(2);
                feed.Render(1);
                feed.End(1.0, scenarioFinished: how == "finished");
                break;
        }

        SupervisionIntervalRecord transit = feed.Interval("transit", "transit");
        Assert.Equal(expected, transit.ClosedBy);
        Assert.Equal(how is "window" or "finished" ? 1.0 : 2.0, transit.ClosedAtSeconds);
    }

    [Fact]
    public void ASubjectAlreadyInTheSimulationIsReadBackOnceAndItsIntervalsCarryTheirPreWindowStarts()
    {
        // The window opens at 20 s, mid-dwell: the departure, the stop under way and the phase reached are
        // SUMO's, read back once, and nothing is invented for the time before.
        var feed = new BinderFeed(Read(Plan([
            Instance("dwell", "annotated", ["test:dwell"], [("v", "subject")],
                     Interval("v", "trip", Anchor(Depart())),
                     Interval("v", "dwell", Anchor(Stop(0, "ahead_0", 60.0), StopEnd(0, "ahead_0", 60.0))),
                     Interval("v", "exit", Anchor(Phase(1, 1, "ahead")))),
        ])), windowOpensAtSeconds: 20.0).Already("v");
        feed.Edges["v"] = "ahead";
        feed.Answers.Departures["v"] = 4.0;
        feed.Answers.Upcoming["v"] = [BinderFeed.StopAt("ahead_0", 60.0, 14.0, null)];
        feed.Answers.RouteIndices["v"] = 1;

        feed.Step(20);

        // In force for the window's first frame, before anything is rendered.
        Assert.Equal((SupervisionState.Annotated, "Supervised/dwell:trip, Supervised/dwell:dwell, Supervised/dwell:exit"),
                     feed.Of("v"));
        SupervisionIntervalRecord trip = feed.Interval("dwell", "trip");
        SupervisionIntervalRecord dwell = feed.Interval("dwell", "dwell");
        SupervisionIntervalRecord exit = feed.Interval("dwell", "exit");
        Assert.Equal((5.0, true, null), (trip.CommittedStartSeconds, trip.BegunBeforeWindow, trip.ObservedStartSeconds));
        Assert.Equal((15.0, true, null), (dwell.CommittedStartSeconds, dwell.BegunBeforeWindow, dwell.ObservedStartSeconds));
        Assert.Equal((null, true, SupervisionIntervalStatus.Open), (exit.CommittedStartSeconds, exit.BegunBeforeWindow, exit.Status));
        Assert.Equal(["departure v", "route index v", "completed v", "upcoming v"], feed.Answers.Asked);

        // The stop ends in the window: the interval that began before it closes on its trigger, never
        // judged against a standstill it could not have watched begin.
        feed.Step(21);
        feed.Render(20, speeds: [("v", 0.0)]);
        feed.Answers.Completed["v"] = [BinderFeed.StopAt("ahead_0", 60.0, 14.0, 21.0)];
        feed.Step(22, stopsEnded: ["v"]);
        Assert.Equal((ClosedBy.Trigger, 22.0), (feed.Interval("dwell", "dwell").ClosedBy, feed.Interval("dwell", "dwell").CommittedEndSeconds));
        Assert.Empty(feed.Binder.Defects);
    }

    [Fact]
    public void NothingIsInForceBeforeTheWindowAndWhatHappenedInThePrewarmIsCommittedButNotObserved()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("transit", "annotated", ["test:transit"], [("v", "subject")],
                     Interval("v", "transit", Anchor(Depart()))),
        ])), windowOpensAtSeconds: 3.0);

        feed.Step(0);
        feed.Step(1, departed: ["v"]);
        feed.Render(0, inWindow: false);
        feed.Step(2);
        feed.Render(1, drawn: ["v"], inWindow: false);
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("v").State);
        feed.Step(3);
        feed.Render(2, drawn: ["v"], inWindow: false);
        Assert.Equal(SupervisionState.Annotated, feed.Of("v").State);
        feed.Step(4);
        feed.Render(3, drawn: ["v"]);

        SupervisionIntervalRecord transit = feed.Interval("transit", "transit");
        Assert.Equal((1.0, true, null), (transit.CommittedStartSeconds, transit.BegunBeforeWindow, transit.ObservedStartSeconds));
    }

    [Fact]
    public void APlanSubjectSumoNeverInsertsFailsTheRunNamingItAndClosesItsIntervalsNeverInserted()
    {
        var feed = new BinderFeed(Read(Plan([
            Instance("haul", "nominal", ["test:routine"], [("late", "subject")],
                     Interval("late", "trip", Anchor(Depart()))),
        ])));
        feed.Step(0);
        feed.Step(1);
        feed.Render(0);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => feed.Step(6, notInserted: [new VehicleNotInserted("late", 5.0, 6.0)]));

        Assert.Equal(CoSimSessionStage.Window, refused.Stage);
        Assert.Contains("SUMO never inserted a subject of the supervision plan Supervised: 'late', given up between "
                        + "5 s and 6 s, a subject of Supervised/haul", refused.Message);
        SupervisionIntervalRecord trip = feed.Interval("haul", "trip");
        Assert.Equal((ClosedBy.NeverInserted, 6.0, null), (trip.ClosedBy, trip.ClosedAtSeconds, trip.CommittedStartSeconds));
        Assert.Equal(("closed", ClosedBy.NeverInserted), (feed.Sink.Told[^1].Told, feed.Sink.Told[^1].Interval.ClosedBy));
    }

    [Fact]
    public void EveryRowOfThePlanIsBoundAndNoneIsMade()
    {
        JsonObject plan = Plan(
            [
                Instance("transit", "annotated", ["test:transit"], [("a", "test:lead"), ("b", "test:follower")],
                         Interval("a", "transit", Anchor(Depart())), Interval("b", "transit", Anchor(Depart()))),
                Absence("missing", "relief", "s2", "s2", ["test:missing"], "site", 2.0, 4.0),
            ],
            series: [Series("relief", "nominal", ["test:posting"], ("s2", null, 2.0, 4.0))]);
        var feed = new BinderFeed(Read(plan));
        feed.Step(0);
        feed.Step(1, departed: ["a", "b", "stranger"]);
        feed.Render(0);

        Assert.Equal([("Supervised/transit", "a", "transit"), ("Supervised/transit", "b", "transit"),
                      ("Supervised/missing", null, "vacancy")],
                     feed.Binder.Intervals.Select(interval => (interval.InstanceId, interval.EntityId, interval.Phase)));
        Assert.Equal(["test:lead"], feed.Table.Of("a").Annotations.Select(annotation => annotation.Role));
        Assert.Equal(SupervisionState.Unlabelled, feed.Of("stranger").State);
    }

    [Fact]
    public void TheBinderHoldsNoSolarType()
    {
        // 06 D6.21: the binder has no reader for the sun, so illumination can never write supervision.
        // Every field of the binder and of every type nested in it, generic arguments included.
        List<Type> held = [];
        foreach (Type type in new[] { typeof(SupervisionBinder) }.Concat(typeof(SupervisionBinder).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)))
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                held.AddRange(Expanded(field.FieldType));
            }
        }

        Assert.NotEmpty(held);
        Assert.DoesNotContain(held, type => type.Namespace == "CarlaNet.Types.Illumination"
                                            || type.Name.Contains("Solar", StringComparison.Ordinal)
                                            || type.Name.Contains("Sun", StringComparison.Ordinal)
                                            || type.Name.Contains("Illumination", StringComparison.Ordinal)
                                            || type.Name.Contains("Epoch", StringComparison.Ordinal));
    }

    private static IEnumerable<Type> Expanded(Type type)
    {
        yield return type;
        Type[] arguments = type.IsGenericType ? type.GetGenericArguments() : type.HasElementType ? [type.GetElementType()!] : [];
        foreach (Type argument in arguments)
        {
            foreach (Type inner in Expanded(argument))
            {
                yield return inner;
            }
        }
    }
}
