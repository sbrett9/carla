using CarlaNet.Recording;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A limit on which vehicles get a body is an optional performance control, off by default: with none,
/// every vehicle SUMO has is drawn; with a circle or a capacity, a vehicle outside it has no body, no
/// pose and no place in any frame's render set, and the report counts it. Every vehicle inside it is
/// drawn exactly where it would be with no limit -- a vehicle SUMO inserts from the frame SUMO first
/// reports it in, and one admitted part-way through its drive from the frame of its admission, at its
/// interpolated position.
/// </summary>
/// <remarks>
/// The fixture's cross in SUMO's frame: <c>approach</c> runs north from y = -100 to the junction,
/// <c>turn_east</c> east along y = -1.68 from x = 10.7 to 100, <c>ahead</c> north and <c>turn_west</c>
/// west. <c>turner</c> takes the east arm; <c>goer</c>, <c>changer</c> and <c>unrenderable</c> never
/// come within 60 m of it. Every vehicle departs at y = -100.
/// </remarks>
public sealed class SumoDriveSessionRenderSetLimitTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionRenderSetLimitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void WithNoLimitEveryVehicleIsDrawnAndThePassesSaySo()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(world, carla, [], passes);
        Assert.IsType<EveryVehicleRenderSetPolicy>(options.RenderSet);
        Assert.Null(options.RenderSet.Capacity);
        Assert.False(options.RenderSet.Limits);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Run(session, 300);

        Assert.All(passes, pass =>
        {
            Assert.False(pass.Limited);
            Assert.Equal((pass.Population, pass.Population, 0), (pass.Eligible, pass.Admitted, pass.Shed));
            Assert.Equal(RenderSetRule.Every, pass.Rule);
        });
        Assert.False(session.Report.RenderSetLimits);
        Assert.Equal((0L, 0L), (session.Report.VehiclePassesOutsideThePolicy, session.Report.CapacityDeclines));
        string report = session.Report.ToString();
        Assert.Contains("render set         every vehicle SUMO has", report);
        Assert.Contains("every vehicle SUMO had", report);
        Assert.DoesNotContain("limit            optional", report);
    }

    [RequiresSumoFact]
    public void UnderACircleAVehicleOutsideItHasNoBodyNoPoseAndNoPlaceInAnyFrameAndIsCounted()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        List<AdmissionPass> passes = [];
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = Options(world, carla, computed, passes);
        options.RenderSet = EastArm();
        options.OnRelease = released.Add;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            Run(session, 400);
            _output.WriteLine(session.Report.ToString());

            // Only the vehicle that drives into the circle is ever posed or drawn.
            Assert.NotEmpty(computed);
            Assert.All(computed, record => Assert.Equal("turner", record.Pose.VehicleId));
            for (ulong frame = session.RenderSet.NewestFrame!.Value; frame > 0
                 && session.RenderSet.TryGetRenderSet(frame, out RenderSet set); frame--)
            {
                Assert.All(set.ByActor.Values, vehicle => Assert.Equal("turner", vehicle.SumoId));
            }

            // And only its body was ever named to the server as drawing a vehicle.
            Assert.All(carla.RenderSetWrites.SelectMany(write => write.Lent),
                       lent => Assert.Equal("turner", lent.VehicleId));

            // The report states the policy and counts what it left out.
            Assert.True(session.Report.RenderSetLimits);
            Assert.True(session.Report.VehiclePassesOutsideThePolicy > 0);
            Assert.Contains(passes, pass => pass.Limited && pass.Population > pass.Eligible);
            Assert.All(passes, pass => Assert.Equal(RenderSetRule.Circle, pass.Rule));
            string report = session.Report.ToString();
            Assert.Contains("render set         circle of 25 m around (70, -1.7) in SUMO meters", report);
            Assert.Contains("a vehicle outside it is simulated by SUMO and has no body, no frame and no truth record",
                            report);
            Assert.Contains("vehicle-passes outside the policy", report);
        }

        // The one body was spawned for the one vehicle the circle drew.
        Assert.Single(carla.Spawned);
        RenderedVehicleInterval turner = Assert.Single(released, interval => interval.VehicleId == "turner");
        Assert.Contains(turner.ReleaseReason, new[] { RenderSetReleaseReason.LeftTheRegion,
                                                      RenderSetReleaseReason.LeftTheSimulation,
                                                      RenderSetReleaseReason.SessionEnded });
    }

    [RequiresSumoFact]
    public void AVehicleAdmittedPartWayThroughItsDriveIsDrawnFromItsAdmissionWhereItWouldBeWithNoLimit()
    {
        using SyntheticWorld world = Fixture();
        Dictionary<(string, long), CoSimPoseRecord> unlimited = Drive(world, null);
        Dictionary<(string, long), CoSimPoseRecord> limited = Drive(world, EastArm());

        Assert.NotEmpty(limited);
        long firstWithNoLimit = unlimited.Keys.Where(key => key.Item1 == "turner").Min(key => key.Item2);
        long firstInTheCircle = limited.Keys.Min(key => key.Item2);
        Assert.True(firstInTheCircle > firstWithNoLimit + 20,
                    "turner was drawn under the circle before it could have reached it");

        // Wherever it is drawn under the limit, it is where SUMO had it with no limit: the same point,
        // lane and interpolation case, tick for tick -- the frame of its admission included, so it is
        // not drawn from anywhere else first.
        foreach (((string vehicle, long tick), CoSimPoseRecord record) in limited)
        {
            CoSimPoseRecord reference = unlimited[(vehicle, tick)];
            Assert.Equal((reference.SumoX, reference.SumoY, reference.LaneId, reference.Case),
                         (record.SumoX, record.SumoY, record.LaneId, record.Case));
        }
    }

    [RequiresSumoFact]
    public void AVehicleSumoInsertsInsideTheLimitIsDrawnFromTheFrameSumoFirstReportsIt()
    {
        // A circle over where every vehicle departs: each is admitted at the frame SUMO first reports
        // it in, and drawn from that frame, exactly as with no limit, until it drives out.
        using SyntheticWorld world = Fixture();
        var origin = new RegionRenderSetPolicy(5.0, -100.0, admitRadiusMetres: 20.0, hysteresisMetres: 5.0);
        Dictionary<(string, long), CoSimPoseRecord> unlimited = Drive(world, null);
        Dictionary<(string, long), CoSimPoseRecord> limited = Drive(world, origin);

        foreach (string vehicle in new[] { "turner", "goer", "changer" })
        {
            long firstWithNoLimit = unlimited.Keys.Where(key => key.Item1 == vehicle).Min(key => key.Item2);
            long firstUnderTheLimit = limited.Keys.Where(key => key.Item1 == vehicle).Min(key => key.Item2);
            Assert.Equal(firstWithNoLimit, firstUnderTheLimit);
            CoSimPoseRecord reference = unlimited[(vehicle, firstWithNoLimit)];
            CoSimPoseRecord record = limited[(vehicle, firstUnderTheLimit)];
            Assert.Equal((reference.SumoX, reference.SumoY), (record.SumoX, record.SumoY));
            Assert.True(limited.Keys.Count(key => key.Item1 == vehicle)
                        < unlimited.Keys.Count(key => key.Item1 == vehicle),
                        $"{vehicle} was drawn after it left the circle");
        }
    }

    [RequiresSumoFact]
    public void UnderACapacityAVehicleDrawnKeepsItsBodyAndTheRestAreCountedAsDeclined()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        List<AdmissionPass> passes = [];
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = Options(world, carla, computed, passes);
        options.RenderSet = new EveryVehicleRenderSetPolicy(capacity: 1);
        options.OnRelease = released.Add;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            Run(session, 400);
            _output.WriteLine(session.Report.ToString());

            // Never more than one drawn at once, and the one drawn kept its place until SUMO removed it.
            Assert.All(computed.GroupBy(record => record.TickIndex), tick => Assert.Single(tick));
            Assert.DoesNotContain(released, interval => interval.ReleaseReason == RenderSetReleaseReason.Capacity);
            Assert.Contains(passes, pass => pass.Shed > 0 && pass.Capacity == 1 && pass.Admitted <= 1);
            Assert.True(session.Report.CapacityDeclines > 0);
            Assert.Equal(1, session.Report.RenderSetCapacity);
            Assert.Contains("declined for the capacity of 1", session.Report.ToString());
        }

        Assert.Single(carla.Spawned);
    }

    [RequiresSumoFact]
    public void ASessionGivenNoPolicyIsRefusedBeforeAnythingIsStarted()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, carla, [], []);
        options.RenderSet = null!;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Empty(carla.SettingsWrites);
    }

    /// <summary>The circle around the east arm, which only <c>turner</c> enters.</summary>
    private static RegionRenderSetPolicy EastArm() =>
        new(70.0, -1.68, admitRadiusMetres: 25.0, hysteresisMetres: 5.0);

    /// <summary>One drive of the fixture, keeping every pose written to a body by vehicle and tick.</summary>
    private static Dictionary<(string, long), CoSimPoseRecord> Drive(SyntheticWorld world, IRenderSetPolicy? policy)
    {
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        SumoDriveSessionOptions options = Options(world, carla, computed, []);
        if (policy is not null)
        {
            options.RenderSet = policy;
        }

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            Run(session, 400);
        }

        return computed.Where(record => record.Actor != 0)
            .ToDictionary(record => (record.Pose.VehicleId, record.TickIndex));
    }

    private static void Run(SumoDriveSession session, int steps)
    {
        for (int step = 0; step < steps && session.Advance(); step++)
        {
        }
    }

    private static SyntheticWorld Fixture() =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

    private static SumoDriveSessionOptions Options(SyntheticWorld world, RecordedWorld carla,
                                                   List<CoSimPoseRecord> computed, List<AdmissionPass> passes) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            OnPose = computed.Add,
            OnAdmissionPass = passes.Add,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
