using CarlaNet.Sumo;
using CarlaNet.Types.Geom;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The optional render set that follows the cameras, driven pass by pass through the render-set manager
/// against frames made up here, so that a vehicle can be put exactly where a threshold is.
/// </summary>
/// <remarks>
/// <para>One camera looks straight down from 100 m over the CARLA origin with 90 degrees across a square
/// picture, so its footprint is the square 100 m either side of the origin. The pass says SUMO steps a
/// second, the longest body is 10 m and reaches 10 m, and the fastest plausible vehicle does 40 m/s, so
/// the margin is 10 + 40 = 50 m; the lead is 3 s and the lag 5 s; the hysteresis is 15 m. So a
/// vehicle at speed v is admitted within 50 + 3v of the footprint, kept within 50 + 120 + 15 = 185 m of
/// it, and held 5 s after it last was.</para>
///
/// <para>SUMO's frame has y north and CARLA's y south; the vehicles here stand on y = 0, where the two
/// agree.</para>
/// </remarks>
public sealed class CameraFootprintRenderSetPolicyTests
{
    private const ActorId Overhead = 7;
    private const double Step = 1.0;

    private static readonly CameraView Nadir =
        new(Overhead, Pose(0, 0, 100, -90, 0), new CameraOptics(1000, 1000, 90.0));

    [Fact]
    public void AVehicleIsAdmittedItsLeadAheadOfTheFootprintSoItsFirstPoseIsOutOfView()
    {
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) = Following();

        // Closing on the footprint's east edge at 20 m/s: admitted within 50 + 3 x 20 = 110 m of it, at
        // x = 210 or nearer, which the pass at t = 10 (x = 200) is the first to find.
        double? admittedAt = null;
        for (int second = 0; second <= 15 && admittedAt is null; second++)
        {
            Pass(policy, manager, second, [Nadir], At("closer", 400.0 - (20.0 * second), 0.0, speed: 20.0));
            if (manager.RenderedVehicleIds.Contains("closer"))
            {
                admittedAt = second;
            }
        }

        Assert.Equal(10.0, admittedAt);

        // Admitted with its bumper still 100 m short of the view, and first drawn where the pass before
        // found it: farther out than its body reaches and its lead carries it.
        GroundPolygon view = Assert.Single(policy.Footprints).Now;
        Assert.Equal(100.0, view.DistanceTo(400.0 - (20.0 * 10), 0.0), 6);
        Assert.True(view.DistanceTo(400.0 - (20.0 * 9), 0.0) > 10.0 + (20.0 * 3.0));
    }

    [Fact]
    public void AStoppedVehicleIsAdmittedWithinTheMarginAndNotBeyondIt()
    {
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) = Following();

        Pass(policy, manager, 0, [Nadir], At("beyond", 155.0, 0.0), At("within", 145.0, 0.0));

        Assert.Equal(["within"], manager.RenderedVehicleIds);
    }

    [Fact]
    public void AVehicleIsHeldTheReleaseLagAfterItLeavesTheKeptBandAndThenReleased()
    {
        List<RenderedVehicleInterval> released = [];
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) = Following(onRelease: released.Add);

        // Leaving at 20 m/s from the middle of the view: out of the view at t = 5, out of the kept band
        // (185 m past the edge, x = 285) first at t = 15, and held until the lag has run at t = 20.
        for (int second = 0; second <= 25; second++)
        {
            Pass(policy, manager, second, [Nadir], At("leaver", 20.0 * second, 0.0, speed: 20.0));
            bool rendered = manager.RenderedVehicleIds.Contains("leaver");
            Assert.True(rendered == (second < 20), $"at t = {second} the vehicle was {(rendered ? "" : "not ")}rendered");
            if (second is >= 15 and < 20)
            {
                Assert.Equal(1, manager.LastHeld);
                Assert.Equal(1, manager.LastEligible);
            }
        }

        RenderedVehicleInterval interval = Assert.Single(released);
        Assert.Equal((0.0, 20.0, RenderSetReleaseReason.LeftTheRegion),
                     (interval.AdmittedAtSeconds, interval.ReleasedAtSeconds, interval.ReleaseReason));
    }

    [Fact]
    public void AVehicleThatComesBackWithinTheLagIsNeverReleasedAndItsLagStartsAgain()
    {
        List<RenderedVehicleInterval> released = [];
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) = Following(onRelease: released.Add);

        Pass(policy, manager, 0, [Nadir], At("wanderer", 0.0, 0.0));
        double[] outThenBack = [300.0, 300.0, 300.0, 300.0, 0.0, 300.0, 300.0, 300.0, 300.0];
        for (int index = 0; index < outThenBack.Length; index++)
        {
            Pass(policy, manager, index + 1, [Nadir], At("wanderer", outThenBack[index], 0.0));
        }

        Assert.Contains("wanderer", manager.RenderedVehicleIds);
        Assert.Empty(released);
    }

    [Fact]
    public void AVehicleOnTheAdmissionThresholdDoesNotFlicker()
    {
        // With no lag, so that the distance band alone has to hold it.
        List<RenderedVehicleInterval> released = [];
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) =
            Following(releaseLag: 0.0, onRelease: released.Add);

        // Stopped, it is admitted within 50 m of the view; stepping to 5 m either side of that threshold
        // on alternate passes must not release it, because it is kept to 185 m.
        for (int second = 0; second < 12; second++)
        {
            Pass(policy, manager, second, [Nadir], At("edge", second % 2 == 0 ? 145.0 : 155.0, 0.0));
            Assert.Contains("edge", manager.RenderedVehicleIds);
        }

        Assert.Equal(1, manager.Admissions);
        Assert.Empty(released);
    }

    [Fact]
    public void UnderTheCapacityOneSeedAlwaysAdmitsTheSameVehiclesAndAnotherSeedMayNot()
    {
        CoSimVehicleFrame[] inView = Enumerable.Range(0, 20)
            .Select(index => At($"v{index:00}", -90.0 + (9.0 * index), 0.0))
            .ToArray();

        // Presented in opposite orders, under one seed: the same five.
        IReadOnlyCollection<string> forwards = AdmittedUnder(42, inView);
        IReadOnlyCollection<string> backwards = AdmittedUnder(42, [.. inView.Reverse()]);
        Assert.Equal(forwards.Order(), backwards.Order());

        // Which five is the seed's order, not the vehicles' positions or ids.
        Assert.Equal(inView.Select(frame => frame.Id).OrderBy(id => SeededOrder.Of(42, id)).Take(5).Order(),
                     forwards.Order());

        // And ten seeds do not all pick the same five.
        var sets = Enumerable.Range(1, 10)
            .Select(seed => string.Join(",", AdmittedUnder(seed, inView).Order()))
            .ToHashSet();
        Assert.True(sets.Count > 1, "every seed admitted the same vehicles, so the ranking ignores the seed");

        // SeededOrder is a fixed function, not a per-process hash: these values hold on any machine.
        Assert.Equal(SeededOrder.Of(42, "v07"), SeededOrder.Of(42, "v07"));
        Assert.NotEqual(SeededOrder.Of(42, "v07"), SeededOrder.Of(43, "v07"));
    }

    [Fact]
    public void AVehicleInViewKeepsItsPlaceAgainstANewcomerAndAnApproachingOneGivesItsPlaceUp()
    {
        List<RenderedVehicleInterval> released = [];
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) =
            Following(capacity: 1, onRelease: released.Add);

        // The newcomer is chosen to come first in the seed's order, so only incumbency keeps it out.
        string[] ids = Enumerable.Range(0, 50).Select(index => $"n{index}").ToArray();
        string incumbent = ids.OrderBy(id => SeededOrder.Of(42, id)).Last();
        string newcomer = ids.OrderBy(id => SeededOrder.Of(42, id)).First();

        Pass(policy, manager, 0, [Nadir], At(incumbent, 0.0, 0.0));
        Pass(policy, manager, 1, [Nadir], At(incumbent, 0.0, 0.0), At(newcomer, 10.0, 0.0));
        Assert.Equal([incumbent], manager.RenderedVehicleIds);
        Assert.Equal(1, manager.LastShed);

        // Drifted out of view but inside the band it is kept in, it gives its place to one in view.
        Pass(policy, manager, 2, [Nadir], At(incumbent, 150.0, 0.0), At(newcomer, 10.0, 0.0));
        Assert.Equal([newcomer], manager.RenderedVehicleIds);
        Assert.Equal(RenderSetReleaseReason.Capacity, Assert.Single(released).ReleaseReason);
    }

    [Fact]
    public void ACameraAddedMidRunTakesOverFromTheCircleAndTheCircleReturnsWhenItIsRemoved()
    {
        List<RenderedVehicleInterval> released = [];
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) =
            Following(circleX: 1000.0, onRelease: released.Add);
        CoSimVehicleFrame inCircle = At("circled", 1000.0, 0.0);
        CoSimVehicleFrame inView = At("viewed", 0.0, 0.0);

        // No camera: the circle, which has no lag.
        Pass(policy, manager, 0, [], inCircle, inView);
        Assert.Equal((RenderSetRule.Circle, 0.0), (policy.ActiveRule, policy.ReleaseLagSeconds));
        Assert.Equal(["circled"], manager.RenderedVehicleIds);

        // A camera: its view is admitted, and the circle's vehicle, outside it, is held for the lag and
        // then released.
        for (int second = 1; second <= 7; second++)
        {
            Pass(policy, manager, second, [Nadir], inCircle, inView);
            Assert.Equal(RenderSetRule.Cameras, policy.ActiveRule);
            Assert.Contains("viewed", manager.RenderedVehicleIds);
            Assert.True(manager.RenderedVehicleIds.Contains("circled") == (second < 6),
                        $"at t = {second} the circle's vehicle was held wrongly");
        }

        // The camera removed: the circle again, at once, with no lag for the vehicle outside it.
        Pass(policy, manager, 8, [], inCircle, inView);
        Assert.Equal(RenderSetRule.Circle, policy.ActiveRule);
        Assert.Equal(["circled"], manager.RenderedVehicleIds);
        Assert.Empty(policy.Footprints);
        Assert.Equal(["circled", "viewed"], released.Select(interval => interval.VehicleId));
    }

    [Fact]
    public void WithNoCameraThePolicyIsExactlyTheCircle()
    {
        var circle = new RegionRenderSetPolicy(30.0, -20.0, admitRadiusMetres: 100.0, hysteresisMetres: 15.0,
                                               capacity: 4);
        var policy = new CameraFootprintRenderSetPolicy(circle);
        policy.BeginPass(PassAt(0.0, 42, []));
        var random = new Random(7);
        for (int trial = 0; trial < 500; trial++)
        {
            double x = (random.NextDouble() * 400.0) - 200.0;
            double y = (random.NextDouble() * 400.0) - 200.0;
            bool held = random.Next(2) == 0;
            CoSimVehicleFrame frame = At($"t{trial}", x, y, speed: random.NextDouble() * 30.0);
            Assert.Equal(circle.ShouldRender(frame, held), policy.ShouldRender(frame, held));
            Assert.Equal(circle.Rank(frame, held), policy.Rank(frame, held));
        }

        Assert.Equal((RenderSetRule.Circle, 0.0, 0.0, 4),
                     (policy.ActiveRule, policy.AdmitLeadSeconds, policy.ReleaseLagSeconds, policy.Capacity));
    }

    [Fact]
    public void AMovingCameraSweepsItsFootprintAheadAndAJumpIsCarriedNowhere()
    {
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) = Following();

        // Flying east at 50 m/s: carried over the step and the lead, 4 s, the view reaches 100 m past
        // x = 50 + 200 -- so a stopped vehicle at x = 380 is within the 50 m margin of it, where the view
        // at the pose read ends at x = 150.
        Pass(policy, manager, 0, [Nadir], At("ahead", 380.0, 0.0));
        Assert.Empty(manager.RenderedVehicleIds);
        Pass(policy, manager, 1, [Nadir with { Pose = Pose(50, 0, 100, -90, 0) }], At("ahead", 380.0, 0.0));
        Assert.Equal(["ahead"], manager.RenderedVehicleIds);
        Assert.Equal(50.0, Assert.Single(policy.Footprints).SpeedMetresPerSecond, 3);

        // Five kilometres in a step is no flight: the view is where the camera is, and nothing ahead.
        Pass(policy, manager, 2, [Nadir with { Pose = Pose(5050, 0, 100, -90, 0) }], At("far", 5380.0, 0.0));
        Assert.DoesNotContain("far", manager.RenderedVehicleIds);
        Assert.Equal(0.0, Assert.Single(policy.Footprints).SpeedMetresPerSecond);
    }

    [Fact]
    public void WithEveryVehicleAsTheFallbackNoCameraLeavesEveryVehicleDrawnAndACameraLimitsThem()
    {
        // The cameras with no circle: until a camera is registered, every vehicle SUMO has is drawn.
        List<RenderedVehicleInterval> released = [];
        var policy = new CameraFootprintRenderSetPolicy(new EveryVehicleRenderSetPolicy(), hysteresisMetres: 15.0,
                                                        admitLeadSeconds: 3.0, releaseLagSeconds: 0.0,
                                                        minimumPixels: 2.0, maximumSpeedMetresPerSecond: 40.0);
        var manager = new RenderSetManager(policy, released.Add);
        CoSimVehicleFrame far = At("far", 5000.0, 0.0);
        CoSimVehicleFrame near = At("near", 0.0, 0.0);

        Pass(policy, manager, 0, [], far, near);
        Assert.Equal((RenderSetRule.Every, true), (policy.ActiveRule, policy.Limits));
        Assert.Equal(["far", "near"], manager.RenderedVehicleIds.Order(StringComparer.Ordinal));
        Assert.Equal(0, manager.VehiclePassesOutsideThePolicy);

        Pass(policy, manager, 1, [Nadir], far, near);
        Assert.Equal(["near"], manager.RenderedVehicleIds);
        Assert.Equal(RenderSetReleaseReason.LeftTheRegion, Assert.Single(released).ReleaseReason);
        Assert.Equal(1, manager.VehiclePassesOutsideThePolicy);
        Assert.Null(policy.Capacity);
    }

    [Fact]
    public void AnUnusableSettingIsRefusedWhereThePolicyIsBuilt()
    {
        var circle = new RegionRenderSetPolicy(0.0, 0.0, 100.0, 15.0, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraFootprintRenderSetPolicy(circle, admitLeadSeconds: -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraFootprintRenderSetPolicy(circle, releaseLagSeconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraFootprintRenderSetPolicy(circle, minimumPixels: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraFootprintRenderSetPolicy(circle, maximumSpeedMetresPerSecond: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraFootprintRenderSetPolicy(circle, hysteresisMetres: 0.0));
        Assert.Throws<ArgumentException>(
            () => new CameraFootprintRenderSetPolicy(new CameraFootprintRenderSetPolicy(circle)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegionRenderSetPolicy(0.0, 0.0, 0.0, 15.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegionRenderSetPolicy(0.0, 0.0, 100.0, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegionRenderSetPolicy(0.0, 0.0, 100.0, 15.0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EveryVehicleRenderSetPolicy(0));
    }

    private static IReadOnlyCollection<string> AdmittedUnder(long seed, CoSimVehicleFrame[] frames)
    {
        (CameraFootprintRenderSetPolicy policy, RenderSetManager manager) = Following(capacity: 5);
        Pass(policy, manager, 0, [Nadir], seed, frames);
        return [.. manager.RenderedVehicleIds];
    }

    private static (CameraFootprintRenderSetPolicy Policy, RenderSetManager Manager) Following(
        int capacity = 8, double circleX = 0.0, double releaseLag = 5.0,
        Action<RenderedVehicleInterval>? onRelease = null)
    {
        var policy = new CameraFootprintRenderSetPolicy(
            new RegionRenderSetPolicy(circleX, 0.0, admitRadiusMetres: 50.0, hysteresisMetres: 15.0, capacity),
            hysteresisMetres: 15.0, admitLeadSeconds: 3.0, releaseLagSeconds: releaseLag, minimumPixels: 2.0,
            maximumSpeedMetresPerSecond: 40.0);
        return (policy, new RenderSetManager(policy, onRelease));
    }

    private static void Pass(CameraFootprintRenderSetPolicy policy, RenderSetManager manager, double seconds,
                             CameraView[] cameras, params CoSimVehicleFrame[] frames) =>
        Pass(policy, manager, seconds, cameras, 42, frames);

    private static void Pass(CameraFootprintRenderSetPolicy policy, RenderSetManager manager, double seconds,
                             CameraView[] cameras, long seed, CoSimVehicleFrame[] frames)
    {
        manager.BeginPass(PassAt(seconds, seed, cameras));
        Assert.Equal(cameras.Length, policy.Footprints.Count);
        manager.ReconcileRenderSet(seconds, frames.ToDictionary(frame => frame.Id));
    }

    private static RenderSetPass PassAt(double seconds, long seed, CameraView[] cameras) =>
        new(seconds, Step, seed, LongestBodyMetres: 10.0, BodyReachMetres: 10.0, cameras, (_, _) => 0.0);

    private static CoSimVehicleFrame At(string id, double x, double y, double speed = 0.0) =>
        new(id, x, y, 90.0, speed, "edge", "edge_0", 5.0, "measured_truck", SumoVehicleSignals.None);

    private static Transform Pose(double x, double y, double z, double pitch, double yaw) =>
        new(new Location((float)x, (float)y, (float)z), new Rotation((float)pitch, (float)yaw, 0f));
}
