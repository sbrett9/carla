using System.Globalization;
using CarlaNet.Sumo;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Lighting;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Each body's lamps are SUMO's signals for the frame it is rendered from, mapped bit by bit, with the
/// headlights the sun asserts; written when they change and when a body is lent, and switched off when it
/// is given back.
/// </summary>
/// <remarks>
/// On the fixture network, whose vehicles brake for the junction and indicate for their turns and their
/// lane change. The fixture steps SUMO at the world's delta, so every tick is rendered at a SUMO frame
/// exactly, and a second SUMO running the same scenario alone says what each vehicle signalled at it.
/// </remarks>
public sealed class SumoDriveSessionLampTests
{
    private const VehicleLightStateFlags Headlights = VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam;

    private readonly ITestOutputHelper _output;

    public SumoDriveSessionLampTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void EachBodyShowsTheSignalsSumoGaveForTheFrameItIsRenderedFrom()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Driving(world, carla, Noon());
        options.OnPose = poses.Add;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            Assert.False(session.Report.Headlights!.OnAtStart);
            Assert.Equal(0, session.Report.Headlights.Switches);
        }

        // What SUMO signalled, frame by frame, running the same scenario and seed alone.
        Dictionary<(long Millisecond, string Vehicle), SumoVehicleSignals> signalled = SignalsSumoGives(400);

        List<CoSimPoseRecord> bodies = [.. poses.Where(record => record.Actor != 0)];
        Assert.NotEmpty(bodies);
        foreach (CoSimPoseRecord record in bodies)
        {
            // The frame a pose is rendered from is SUMO's at the tick's instant, a vehicle's first
            // included: no vehicle is drawn on a tick before the frame SUMO first reports it in.
            Assert.Equal(signalled[(Millisecond(record.SimulatedTimeSeconds), record.Pose.VehicleId)],
                         record.Signals);
            Assert.Equal(VehicleLampMapping.FromSumo(record.Signals), record.Lamps);
        }

        // The fixture's traffic does brake and indicate, so the mapping had something to map.
        Assert.Contains(bodies, record => record.Lamps.HasFlag(VehicleLightStateFlags.Brake));
        Assert.Contains(bodies, record => record.Lamps.HasFlag(VehicleLightStateFlags.RightBlinker));
        Assert.Contains(bodies, record => record.Lamps.HasFlag(VehicleLightStateFlags.LeftBlinker));
    }

    [RequiresSumoFact]
    public void LampsAreWrittenWhenABodyIsLentAndWhenTheyChangeAndAtNoOtherTick()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Driving(world, carla, Noon());
        options.OnPose = poses.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        // Which lamps each tick should have written for a posed body: its first tick on that body --
        // whatever the lamps, None included, because the actor keeps the last vehicle's -- and every
        // tick its lamps differ from its previous tick's.
        HashSet<(long Tick, uint Actor, VehicleLightStateFlags Lamps)> expected = [];
        Dictionary<string, (uint Actor, VehicleLightStateFlags Lamps)> previous = [];
        foreach (CoSimPoseRecord record in poses.Where(record => record.Actor != 0).OrderBy(record => record.TickIndex))
        {
            if (!previous.TryGetValue(record.Pose.VehicleId, out (uint Actor, VehicleLightStateFlags Lamps) last)
                || last.Actor != record.Actor || last.Lamps != record.Lamps)
            {
                expected.Add((record.TickIndex, record.Actor, record.Lamps));
            }

            previous[record.Pose.VehicleId] = (record.Actor, record.Lamps);
        }

        // What each batch wrote after its poses, leaving out the darkening of bodies given back.
        HashSet<(long Tick, uint Actor, VehicleLightStateFlags Lamps)> written = [];
        foreach ((IReadOnlyList<Command> batch, long tick) in carla.DrivenBatches)
        {
            for (int index = 0; index < batch.Count; index++)
            {
                if (batch[index] is SetVehicleLightStateCommand lamps && !AfterAParking(batch, index))
                {
                    Assert.True(written.Add((tick, lamps.Actor, lamps.LightState)),
                                $"tick {tick} wrote actor {lamps.Actor}'s lamps twice");
                }
            }
        }

        Assert.Equal(expected.OrderBy(entry => entry), written.OrderBy(entry => entry));
        Assert.Contains(expected, entry => entry.Lamps == VehicleLightStateFlags.None);
        Assert.True(written.Count < poses.Count(record => record.Actor != 0) / 2,
                    "lamps were written on most ticks rather than on a change");
    }

    [RequiresSumoFact]
    public void ABodyGivenBackIsDarkenedAndABodyLentAgainIsGivenItsNewVehicleSLamps()
    {
        // At night every body is lit, so a body carried from one vehicle to the next would carry lamps.
        // The succession fixture's second vehicle borrows the body its predecessor gave back.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        List<(RenderedVehicleInterval Interval, long AtTick)> released = [];
        SumoDriveSessionOptions options = Driving(world, carla, SolarLeaseTests.PortEpoch(),
                                                  CoSimFixtures.SuccessionScenario);
        options.OnPose = poses.Add;
        options.OnRelease = interval => released.Add((interval, carla.Ticks));

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        Assert.True(session.Report.Headlights!.OnAtStart);
        Assert.All(poses.Where(record => record.Actor != 0), record => Assert.True(record.Lamps.HasFlag(Headlights)));

        // Every parking is followed, in the same batch, by the body's lamps switched off.
        int parkings = 0;
        foreach ((IReadOnlyList<Command> batch, long _) in carla.DrivenBatches)
        {
            for (int index = 0; index < batch.Count; index++)
            {
                if (batch[index] is ApplyTransformCommand parked && parked.Transform.Location.Z < -100f)
                {
                    parkings++;
                    SetVehicleLightStateCommand dark = Assert.IsType<SetVehicleLightStateCommand>(batch[index + 2]);
                    Assert.Equal((parked.Actor, VehicleLightStateFlags.None), (dark.Actor, dark.LightState));
                }
            }
        }

        // A body given back is parked by the next tick; the one the run's last step gave back has no
        // next tick, and is destroyed with the rest when the session ends.
        Assert.Equal(released.Count(entry => entry.Interval.Actor != 0 && entry.AtTick < carla.Ticks), parkings);
        Assert.True(parkings > 0, "no body was given back while the run went on");

        // A body lent to a second vehicle wears that vehicle's lamps from its first tick.
        Assert.Contains(poses.Where(record => record.Actor != 0).GroupBy(record => record.Actor),
                        body => body.Select(record => record.Pose.VehicleId).Distinct().Count() > 1);
        foreach (IGrouping<(uint Actor, string Vehicle), CoSimPoseRecord> loan in poses
                     .Where(record => record.Actor != 0)
                     .GroupBy(record => (record.Actor, record.Pose.VehicleId)))
        {
            CoSimPoseRecord first = loan.MinBy(record => record.TickIndex);
            Assert.Contains(carla.DrivenBatches, entry => entry.AtTick == first.TickIndex
                && entry.Batch.OfType<SetVehicleLightStateCommand>()
                    .Any(lamps => lamps.Actor == first.Actor && lamps.LightState == first.Lamps));
        }

        // Nothing parked is left lit.
        Assert.All(carla.Spawned.Select((_, index) => (uint)(index + 1)).Where(actor => carla.ObservedTransform(actor) is { } at
                                                                                          && at.Location.Z < -100f),
                   actor => Assert.Equal(VehicleLightStateFlags.None, carla.LampsOf(actor)));
    }

    [RequiresSumoFact]
    public void ABodyLentToAVehicleShowingNoLampsIsStillWrittenDark()
    {
        // A vehicle departing straight ahead at midday shows no lamp on its first tick: its body's lamps
        // are written anyway, because the actor keeps whatever it last held.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Driving(world, carla, Noon());
        options.OnPose = poses.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        List<CoSimPoseRecord> loans = [.. poses.Where(record => record.Actor != 0)
                                              .GroupBy(record => (record.Actor, record.Pose.VehicleId))
                                              .Select(loan => loan.MinBy(record => record.TickIndex))];
        Assert.Contains(loans, first => first.Lamps == VehicleLightStateFlags.None);
        Assert.All(loans, first => Assert.Contains(
            carla.DrivenBatches, entry => entry.AtTick == first.TickIndex
                && entry.Batch.OfType<SetVehicleLightStateCommand>()
                    .Any(lamps => lamps.Actor == first.Actor && lamps.LightState == first.Lamps)));
    }

    [RequiresSumoFact]
    public void EveryLampCommandLeavesEachPosedBodyHoldingTheLampsItsPoseRecordSays()
    {
        // At night, with one body handed from one vehicle to the next and darkened in between.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Driving(world, carla, SolarLeaseTests.PortEpoch(),
                                                  CoSimFixtures.SuccessionScenario);
        options.OnPose = poses.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.Contains(poses.Where(record => record.Actor != 0).GroupBy(record => record.Actor),
                        body => body.Select(record => record.Pose.VehicleId).Distinct().Count() > 1);

        // Replayed in order, every lamp command leaves each posed body holding exactly the lamps its pose
        // record says -- on the tick a body is lent to its second vehicle included.
        Dictionary<uint, VehicleLightStateFlags> held = [];
        ILookup<long, CoSimPoseRecord> posedAt = poses.Where(record => record.Actor != 0).ToLookup(record => record.TickIndex);
        foreach ((IReadOnlyList<Command> batch, long tick) in carla.DrivenBatches)
        {
            foreach (SetVehicleLightStateCommand lamps in batch.OfType<SetVehicleLightStateCommand>())
            {
                held[lamps.Actor] = lamps.LightState;
            }

            foreach (CoSimPoseRecord record in posedAt[tick])
            {
                Assert.Equal(record.Lamps, held.GetValueOrDefault(record.Actor));
            }
        }
    }

    [RequiresSumoFact]
    public void ASunCrossingTheThresholdSwitchesEveryVehicleSHeadlightsOnTogether()
    {
        // Two seconds before the sun at the package's origin falls below three degrees, advancing.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Driving(world, carla, TwoSecondsBeforeTheSunReaches(3.0));
        options.Illumination = IlluminationPolicy.Advance(1.0);
        options.WarmUpToSimulatedSecond = 1.0;
        options.OnPose = poses.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 120 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());
        Assert.False(session.Report.Headlights!.OnAtStart);
        Assert.Equal(1, session.Report.Headlights.Switches);

        List<CoSimPoseRecord> bodies = [.. poses.Where(record => record.Actor != 0)];
        long switchedAt = bodies.Where(record => record.Lamps.HasFlag(Headlights)).Min(record => record.TickIndex);
        Assert.All(bodies, record => Assert.Equal(record.TickIndex >= switchedAt,
                                                  record.Lamps.HasFlag(Headlights)));
        Assert.Contains(bodies, record => record.TickIndex < switchedAt);

        // Every body posed on the tick they switched had its lamps written in that tick's batch.
        IReadOnlyList<Command> batch = carla.DrivenBatches.Single(entry => entry.AtTick == switchedAt).Batch;
        Assert.All(bodies.Where(record => record.TickIndex == switchedAt),
                   record => Assert.Contains(batch, command => command is SetVehicleLightStateCommand lamps
                                                               && lamps.Actor == record.Actor
                                                               && lamps.LightState.HasFlag(Headlights)));
    }

    [RequiresSumoFact]
    public void WithLampsNotDrivenNoLampIsEverWrittenAndTheSignalsAreStillRecorded()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Driving(world, carla, SolarLeaseTests.PortEpoch());
        options.VehicleLampsDriven = false;
        options.OnPose = poses.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.DoesNotContain(carla.Batches.SelectMany(batch => batch), command => command is SetVehicleLightStateCommand);
        Assert.All(poses, record => Assert.Equal(VehicleLightStateFlags.None, record.Lamps));
        Assert.Contains(poses, record => record.Signals != SumoVehicleSignals.None);
        Assert.Contains("lamps              not driven", session.Report.ToString());
    }

    [RequiresSumoFact]
    public void ASunTheSessionDoesNotBindDrivesNoHeadlights()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Driving(world, carla, null);
        options.Illumination = IlluminationPolicy.Ignore();
        options.OnPose = poses.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.Null(session.Report.Headlights);
        Assert.All(poses.Where(record => record.Actor != 0),
                   record => Assert.Equal(VehicleLampMapping.FromSumo(record.Signals), record.Lamps));
        Assert.Contains("headlights not driven, no bound sun", session.Report.ToString());
    }

    [Fact]
    public void AHeadlightBandThatIsEmptyIsRefusedBeforeAnythingIsTouched()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla, Noon());
        options.HeadlightOnBelowDegrees = 6.0;
        options.HeadlightOffAboveDegrees = 3.0;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Equal(0, carla.Descriptions);
    }

    /// <summary>Whether a command follows a parking pair: the darkening of a body given back.</summary>
    private static bool AfterAParking(IReadOnlyList<Command> batch, int index) =>
        index >= 2 && batch[index - 2] is ApplyTransformCommand parked && parked.Transform.Location.Z < -100f;

    /// <summary>
    /// Each vehicle's signal word at each SUMO frame of the fixture scenario, from a SUMO running it
    /// alone.
    /// </summary>
    private static Dictionary<(long Millisecond, string Vehicle), SumoVehicleSignals> SignalsSumoGives(int steps)
    {
        Dictionary<(long, string), SumoVehicleSignals> signalled = [];
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        for (int step = 0; step <= steps + 2; step++)
        {
            long now = Millisecond(sumo.Time);
            foreach (string vehicle in sumo.Vehicles.Ids)
            {
                signalled[(now, vehicle)] = sumo.Vehicles.Signals(vehicle);
            }

            sumo.Step();
        }

        return signalled;
    }

    private static long Millisecond(double seconds) => (long)Math.Round(seconds * 1000.0);

    /// <summary>Midday at the package's origin, 0 degrees north and east, with the sun near overhead.</summary>
    private static SolarEpoch Noon() =>
        SolarEpoch.Declare("2026-03-21T12:00:00+00:00", 0.0, "2026-03-21T12:00:00Z",
                           calendarAdvances: true, dstInEffect: false);

    /// <summary>
    /// An epoch whose simulated second zero is two seconds before the sun at the package's origin sets
    /// through <paramref name="elevation"/> degrees, found with the engine's own algorithm.
    /// </summary>
    private static SolarEpoch TwoSecondsBeforeTheSunReaches(double elevation)
    {
        DateTime instant = new(2026, 3, 21, 17, 30, 0, DateTimeKind.Unspecified);
        while (SolarPositionModel.AtInstant(0.0, 0.0, 0.0, instant).ElevationDegrees >= elevation)
        {
            instant = instant.AddSeconds(1);
        }

        DateTime zero = instant.AddSeconds(-2);
        string civil = zero.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return SolarEpoch.Declare(civil + "+00:00", 0.0, civil + "Z", calendarAdvances: true, dstInEffect: false);
    }

    private static SyntheticWorld Fixture() =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

    private static SumoDriveSessionOptions Driving(SyntheticWorld world, RecordedWorld carla, SolarEpoch? epoch,
                                                   string? scenario = null) =>
        new(scenario ?? CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            Epoch = epoch,
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
