using CarlaNet.Types.Supervision;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The supervision in force is put on the server, onto the bodies that draw the vehicles it is about, and
/// every snapshot carries it: so every client of the world reads the same truth for the same frame, as the
/// owner ruled on 2026-10-05, and none holds its own.
/// </summary>
/// <remarks>
/// The succession fixture hands one body from <c>first</c> to <c>second</c> with a spell parked between,
/// which is the case a supervision held per body gets wrong if it is not dropped and named again: the body
/// drawing <c>second</c> carrying what was asserted of <c>first</c>. Here the test is the binder, stating
/// each vehicle's supervision once, as an interval opens.
/// </remarks>
public sealed class SumoDriveSessionSupervisionTests
{
    private static readonly SupervisionPlanIdentity Plan = new("Succession", CoreVocabulary.Version, "5ucce55");

    private static readonly SupervisionInForce Transit = new(SupervisionState.Annotated,
        [new AnnotationInForce("Succession/pi_first_transit", ["test:off_pattern_transit"], "transit", "subject")]);

    private static readonly SupervisionInForce Ordinary = new(SupervisionState.Nominal,
        [new AnnotationInForce("Succession/pi_second_haul", ["test:routine_haul"], "haul", "subject")]);

    private readonly ITestOutputHelper _output;

    public SumoDriveSessionSupervisionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void EveryFrameTheServerCarriesTheSupervisionInForceOnTheBodyDrawingEachVehicle()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            // Bound, and every interval stated, before the first frame.
            session.Supervision.Bind(Plan);
            session.Supervision.Set("first", Transit);
            session.Supervision.Set("second", Ordinary);

            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            long frames = session.Report.Ticks;
            bool sawFirst = false, sawSecond = false;
            for (ulong frame = 1; frame <= (ulong)frames; frame++)
            {
                PublishedSupervision published = carla.PublishedSupervisionOf(frame)
                    ?? throw new Xunit.Sdk.XunitException($"frame {frame} carried no supervision");
                Assert.Equal(Plan, published.Plan);

                // A row on exactly the bodies drawing a vehicle the author asserts something of, and the
                // row is that vehicle's, whichever vehicle the body drew before.
                var lent = carla.PublishedRenderSetOf(frame)?.Lent
                    ?? new Dictionary<uint, (string VehicleId, string VehicleTypeId, ulong AdmittedFrame)>();
                Dictionary<uint, SupervisionInForce> expected = lent
                    .Where(pair => pair.Value.VehicleId is "first" or "second")
                    .ToDictionary(pair => pair.Key, pair => pair.Value.VehicleId == "first" ? Transit : Ordinary);
                Assert.Equal(expected.OrderBy(pair => pair.Key), published.ByActor.OrderBy(pair => pair.Key));
                sawFirst |= lent.Values.Any(vehicle => vehicle.VehicleId == "first");
                sawSecond |= lent.Values.Any(vehicle => vehicle.VehicleId == "second");
            }

            Assert.True(sawFirst && sawSecond, "the run never drew both vehicles");

            // Put fresh, once, before the first cue; then only on a change: the body lent to each
            // vehicle, given back between them.
            (SupervisionChange opening, long openedAt) = carla.SupervisionWrites[0];
            Assert.True(opening.Fresh);
            Assert.Equal(0, openedAt);
            Assert.Equal(Plan, opening.Plan);
            Assert.Single(carla.SupervisionWrites, write => write.Change.Fresh);
            // Every change after the fresh opening names bodies, and no change names anything else: there is
            // no row for the world (06 §3.5). The opening may name none, put before any body is lent.
            Assert.All(carla.SupervisionWrites.SkipLast(1),
                       write => Assert.True(write.Change.Fresh || write.Change.Bodies.Count > 0));
            Assert.True(carla.SupervisionWrites.Count <= 5,
                        $"{carla.SupervisionWrites.Count} changes put over {frames} ticks");
            // No body is ever named unlabelled: the one body was given back between its two vehicles, and
            // the server dropped what it carried then, so an unlabelled vehicle cost nothing to put.
            Assert.All(carla.SupervisionWrites, write => Assert.All(
                write.Change.Bodies, body => Assert.NotEqual(SupervisionState.Unlabelled, body.Supervision.State)));
            Assert.Equal(carla.SupervisionWrites.Count, session.Report.SupervisionUpdates);
            Assert.Equal(0, session.Report.SupervisionBodiesNotApplied);
            Assert.Null(session.Report.SupervisionRefused);
        }

        // Disposed: the plan withdrawn, and the bodies gone with their own supervision.
        Assert.Null(carla.SupervisionPlanHeld);
        Assert.Equal(0, carla.SupervisedBodies);
        Assert.Null(carla.SupervisionWrites[^1].Change.Plan);
    }

    [RequiresSumoFact]
    public void ABodyHandedOnCarriesTheNextVehiclesSupervisionEvenWhereItIsTheSameAsTheLast()
    {
        // Both vehicles carry one whole-life assertion, as every member of a cohort does: the server drops
        // what the body carried when it was given back, so the session names it again for the next
        // vehicle, although it is word for word what the body carried before.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world);
        options.World = carla;
        var cohort = new SupervisionInForce(SupervisionState.Annotated,
            [new AnnotationInForce("Succession/pi_measured_trucks", ["test:heavy_goods_at_night"], "", "")]);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        session.Supervision.Bind(Plan);
        session.Supervision.Set("first", cohort);
        session.Supervision.Set("second", cohort);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        int framesDrawingSecond = 0;
        for (ulong frame = 1; frame <= (ulong)session.Report.Ticks; frame++)
        {
            var lent = carla.PublishedRenderSetOf(frame)?.Lent
                ?? new Dictionary<uint, (string VehicleId, string VehicleTypeId, ulong AdmittedFrame)>();
            PublishedSupervision published = carla.PublishedSupervisionOf(frame)!;
            foreach ((uint body, (string vehicleId, _, _)) in lent)
            {
                Assert.True(published.ByActor.TryGetValue(body, out SupervisionInForce? row),
                            $"frame {frame}: the body drawing {vehicleId} carries nothing");
                Assert.Equal(cohort, row);
                framesDrawingSecond += vehicleId == "second" ? 1 : 0;
            }
        }

        Assert.True(framesDrawingSecond > 0, "the run never drew the second vehicle");
    }

    [RequiresSumoFact]
    public void NothingIsPutToTheServerUntilAPlanIsBound()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 100 && session.Advance(); step++)
            {
            }

            Assert.Empty(carla.SupervisionWrites);
            Assert.Null(carla.PublishedSupervisionOf((ulong)session.Report.Ticks));
            Assert.Equal(0, session.Report.SupervisionUpdates);
            Assert.Contains("supervision        0 change(s) put to the server", session.Report.ToString());
        }

        // Nothing was put, so nothing is withdrawn.
        Assert.Empty(carla.SupervisionWrites);
    }

    [RequiresSumoFact]
    public void AServerThatRefusesTheSupervisionIsAskedOnceAndTheRunGoesOn()
    {
        // A server built before it carried supervision has no such call. Nothing in the run depends on
        // it, so the session says what the server said and goes on, and puts nothing more.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        const string refusal = "unknown method 'update_supervision'";
        var carla = new RecordedWorld { Loaded = world.AsLoaded(), RefusesSupervision = refusal };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            session.Supervision.Bind(Plan);
            session.Supervision.Set("first", Transit);
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            Assert.Single(carla.SupervisionWrites);
            Assert.Equal(refusal, session.Report.SupervisionRefused);
            Assert.Equal(0, session.Report.SupervisionUpdates);
            Assert.Null(session.Report.Stopped);
            Assert.Contains(refusal, session.Report.ToString());
            Assert.True(session.Report.RenderSetUpdates > 0);
        }

        // Refused, so nothing is withdrawn either.
        Assert.Single(carla.SupervisionWrites);
    }

    [RequiresSumoFact]
    public void ASessionThatRendersNoWorldPutsNoSupervision()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(CoSimFixtures.RightAngleTurnScenario, world);
        options.TickWorld = () => true;
        options.Illumination = null;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        session.Supervision.Bind(Plan);
        session.Supervision.Set("first", Transit);
        for (int step = 0; step < 50 && session.Advance(); step++)
        {
        }

        Assert.Equal(0, session.Report.SupervisionUpdates);
        Assert.Null(session.Report.SupervisionRefused);
    }

    private static SumoDriveSessionOptions Options(string scenario, SyntheticWorld world) =>
        new(scenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
