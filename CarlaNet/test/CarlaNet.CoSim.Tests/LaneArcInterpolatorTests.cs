using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The four interpolation cases, and the measurement that decides between following a lane and
/// cutting across it.
/// </summary>
public sealed class LaneArcInterpolatorTests
{
    private static CoSimVehicleFrame On(string laneId, double lanePosition, double speed = 25.0) =>
        new("v", 0.0, 0.0, 0.0, speed, laneId[..laneId.LastIndexOf('_')], laneId, lanePosition,
            "measured_truck", SumoVehicleSignals.None);

    [Fact]
    public void TheNetworkReadsItsLanesItsConnectorsAndItsFrame()
    {
        SumoRoadNetwork network = SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork);

        Assert.True(network.TryGetLane("approach_0", out SumoLane approach));
        Assert.Equal("approach", approach.EdgeId);
        Assert.False(approach.IsInternal);
        Assert.Equal(92.65, approach.DeclaredLengthMetres, 2);

        Assert.True(network.TryGetLane(":centre_0_0", out SumoLane connector));
        Assert.True(connector.IsInternal);
        Assert.Equal(9.15, connector.DeclaredLengthMetres, 2);

        Assert.Equal((0.0, 0.0), network.NetOffset);
        Assert.Equal((-100.0, -100.0, 100.0, 100.0), network.ConvBoundary);
    }

    [Fact]
    public void ARouteThroughAJunctionGoesThroughTheConnectorRatherThanStraightToTheFarLane()
    {
        SumoRoadNetwork network = SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork);

        IReadOnlyList<SumoLane> path = network.FindPath("approach_0", "turn_east_0")!;
        Assert.Equal([":centre_0_0", "turn_east_0"], path.Select(lane => lane.Id).ToArray());
    }

    [Fact]
    public void TwoLanesNothingConnectsHaveNoRoute()
    {
        SumoRoadNetwork network = SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork);
        Assert.Null(network.FindPath("turn_east_0", "approach_0"));
    }

    [Fact]
    public void OnOneLaneThePoseWalksTheLaneAndItsEndsAreTheReportedPoints()
    {
        var interpolator = Interpolator();
        CoSimVehicleFrame from = On("approach_0", 10.0);
        CoSimVehicleFrame to = On("approach_0", 30.0);

        InterpolatedState start = interpolator.Interpolate(from, to, 0.0, 1.0);
        InterpolatedState middle = interpolator.Interpolate(from, to, 0.5, 1.0);
        InterpolatedState end = interpolator.Interpolate(from, to, 1.0, 1.0);

        Assert.Equal(LaneInterpolationCase.SameLane, middle.Case);
        // The approach runs due north up x = 5.03 from y = -100.
        Assert.Equal(5.03, middle.X, 2);
        Assert.Equal(-100.0 + 20.0, middle.Y, 2);
        Assert.Equal(-100.0 + 10.0, start.Y, 2);
        Assert.Equal(-100.0 + 30.0, end.Y, 2);
        Assert.Equal(0.0, middle.HeadingDegrees, 2);
    }

    [Fact]
    public void ALaneChangeSlidesSidewaysAndArrivesOnTheNewLane()
    {
        var interpolator = Interpolator();
        CoSimVehicleFrame from = On("approach_0", 40.0);
        CoSimVehicleFrame to = On("approach_1", 65.0);

        InterpolatedState start = interpolator.Interpolate(from, to, 0.0, 1.0);
        InterpolatedState middle = interpolator.Interpolate(from, to, 0.5, 1.0);
        InterpolatedState end = interpolator.Interpolate(from, to, 1.0, 1.0);

        Assert.Equal(LaneInterpolationCase.LaneChange, middle.Case);
        Assert.Equal(5.03, start.X, 2);                       // still on the right-hand lane
        Assert.Equal(1.68, end.X, 2);                         // arrived on the left-hand one
        Assert.InRange(middle.X, 1.68, 5.03);
        Assert.Equal(-100.0 + 52.5, middle.Y, 2);             // and half way along in the meantime
    }

    [Fact]
    public void AGapNoRouteCoversIsCalledDiscontinuousAndNothingIsSlidAcrossIt()
    {
        var interpolator = Interpolator();
        InterpolatedState state = interpolator.Interpolate(
            On("turn_east_0", 10.0), On("approach_0", 10.0), 0.5, 1.0);

        Assert.Equal(LaneInterpolationCase.Discontinuous, state.Case);
        Assert.Null(interpolator.RouteDistance(On("turn_east_0", 10.0), On("approach_0", 10.0)));
    }

    [Fact]
    public void AGapFurtherThanTheVehicleCouldHaveTravelledIsCalledDiscontinuousToo()
    {
        var interpolator = Interpolator();

        // Right across the junction and out the far side at walking pace: the route exists, and the
        // vehicle cannot have driven it in one second.
        InterpolatedState state = interpolator.Interpolate(
            On("approach_0", 1.0, speed: 1.0), On("turn_east_0", 80.0, speed: 1.0), 0.5, 1.0);
        Assert.Equal(LaneInterpolationCase.Discontinuous, state.Case);

        // The same pair at a speed that covers it is interpolated.
        InterpolatedState driven = interpolator.Interpolate(
            On("approach_0", 1.0, speed: 130.0), On("turn_east_0", 80.0, speed: 130.0), 0.5, 1.0);
        Assert.Equal(LaneInterpolationCase.CrossedEdges, driven.Case);
    }

    [Fact]
    public void CrossingAJunctionFollowsTheConnectorRatherThanTheChordAcrossTheCorner()
    {
        // Fifteen metres of approach and fifteen of exit either side of a right-angle turn: the
        // measurement the whole lane-following design rests on. The vehicle's own lane runs north up
        // x = 5.03 and leaves east along y = -1.68, so the corner is near (5, -2).
        var interpolator = Interpolator();
        CoSimVehicleFrame from = On("approach_0", 92.65 - 15.0, speed: 35.0);
        CoSimVehicleFrame to = On("turn_east_0", 15.0, speed: 35.0);

        InterpolatedState followed = interpolator.Interpolate(from, to, 0.5, 1.0);
        Assert.Equal(LaneInterpolationCase.CrossedEdges, followed.Case);

        (double fromX, double fromY, _, _) = Lane("approach_0").PointAt(92.65 - 15.0);
        (double toX, double toY, _, _) = Lane("turn_east_0").PointAt(15.0);
        double chordX = (fromX + toX) / 2.0;
        double chordY = (fromY + toY) / 2.0;

        double apart = Math.Sqrt(Math.Pow(followed.X - chordX, 2) + Math.Pow(followed.Y - chordY, 2));
        Assert.True(apart > 3.0 * 3.35,
                    $"the chord and the lane are only {apart:0.00} m apart at the midpoint, so this "
                    + "geometry no longer demonstrates anything");
    }

    [RequiresSumoFact]
    public void AgainstARecordedTrackTheLaneFollowingErrorIsCentimetresAndTheChordSIsMetres()
    {
        // The honest comparison: run the scenario at the world's own tick rate and keep every pose,
        // then throw away nineteen frames in twenty and ask each interpolation to put them back.
        // One run, so the two answers are about the interpolation and not about SUMO behaving
        // differently at a different step length -- which it measurably does.
        List<CoSimVehicleFrame> track = RecordTrack("turner", steps: 260);
        Assert.True(track.Count > 200, $"only {track.Count} frames recorded");

        var interpolator = Interpolator();
        const int stride = 20;               // 1.0 s of SUMO at the 0.05 s step the fixture runs
        double worstFollowed = 0.0;
        double worstChord = 0.0;
        int crossings = 0;

        // Every alignment of the one-second window, not one in twenty of them: where the window
        // happens to fall decides how much of the turn it straddles, and the worst case is the
        // question.
        for (int start = 0; start + stride < track.Count; start++)
        {
            CoSimVehicleFrame from = track[start];
            CoSimVehicleFrame to = track[start + stride];
            if (from.EdgeId != to.EdgeId)
            {
                crossings++;
            }

            for (int offset = 1; offset < stride; offset++)
            {
                double fraction = (double)offset / stride;
                CoSimVehicleFrame truth = track[start + offset];

                InterpolatedState followed = interpolator.Interpolate(from, to, fraction, 1.0);
                worstFollowed = Math.Max(worstFollowed, Distance(followed.X, followed.Y, truth));

                double chordX = from.X + ((to.X - from.X) * fraction);
                double chordY = from.Y + ((to.Y - from.Y) * fraction);
                worstChord = Math.Max(worstChord, Distance(chordX, chordY, truth));
            }
        }

        // Measured on this fixture: following the lane is out by under a tenth of a metre and the
        // chord by 1.16 m, a third of a lane width. Two things worth knowing sit behind those
        // numbers.
        //
        // Distributing the step's distance linearly in time rather than integrating the speed ramp
        // measured 0.563 m of the lane-following error, all of it along the vehicle's own track and
        // worst where it brakes for the junction. That is what the ramp integration removes.
        //
        // And the chord's error is 1.16 m rather than the several metres a fifteen-metre approach
        // and exit would give, because SUMO does not take a right-angle turn at speed: measured, the
        // vehicle enters this junction at 6.39 m/s off a 27 m/s approach, spends 1.40 s inside the
        // connector, and covers 6.88 m in the second straddling the corner. The chord therefore
        // spans about three metres either side of the turn rather than fifteen.
        Assert.True(crossings > 0, "the recorded track never crossed an edge");
        Assert.True(worstFollowed < 0.10,
                    $"following the lane was out by {worstFollowed:0.000} m at worst");
        Assert.True(worstChord > 1.0,
                    $"the chord was only out by {worstChord:0.000} m, so this track no longer "
                    + "demonstrates the corner cut");
        Assert.True(worstChord > 10.0 * worstFollowed,
                    $"the chord was out by {worstChord:0.000} m and the lane by "
                    + $"{worstFollowed:0.000} m, which is not the separation this rests on");
    }

    private static double Distance(double x, double y, in CoSimVehicleFrame truth) =>
        Math.Sqrt(Math.Pow(x - truth.X, 2) + Math.Pow(y - truth.Y, 2));

    /// <summary>Every step of one vehicle's life, read through the bridge's own subscription.</summary>
    private static List<CoSimVehicleFrame> RecordTrack(string vehicleId, int steps)
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        var population = new SubscribedPopulation(sumo.TraCI);
        List<CoSimVehicleFrame> track = [];

        for (int step = 0; step < steps; step++)
        {
            sumo.Step();
            population.Reconcile(sumo.Simulation.DepartedVehicleIds,
                                 sumo.Simulation.ArrivedVehicleIds);
            if (population.ScreenedVehicleIds.Contains(vehicleId))
            {
                population.Promote(vehicleId);
            }

            if (population.TryReadFrame(vehicleId, out CoSimVehicleFrame frame))
            {
                track.Add(frame);
            }
        }

        return track;
    }

    private static LaneArcInterpolator Interpolator() =>
        new(SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork));

    private static SumoLane Lane(string laneId)
    {
        SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork).TryGetLane(laneId, out SumoLane lane);
        return lane;
    }
}
