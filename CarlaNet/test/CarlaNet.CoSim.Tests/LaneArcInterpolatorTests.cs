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
    public void PartWayThroughALaneChangeSpreadOverTimeThePoseIsBesideItsLaneWhereSumoHasIt()
    {
        // SUMO reports the lane the change started on, with the vehicle 1.0 m and then 1.056 m to the
        // left of its centre line: the approach runs north, so left is west, towards smaller x.
        var interpolator = Interpolator();
        CoSimVehicleFrame from = On("approach_0", 40.0) with { LateralOffsetMetres = 1.0 };
        CoSimVehicleFrame to = On("approach_0", 41.0) with { LateralOffsetMetres = 1.056 };

        InterpolatedState start = interpolator.Interpolate(from, to, 0.0, 0.05);
        InterpolatedState middle = interpolator.Interpolate(from, to, 0.5, 0.05);
        InterpolatedState end = interpolator.Interpolate(from, to, 1.0, 0.05);

        Assert.Equal(LaneInterpolationCase.SameLane, middle.Case);
        Assert.Equal(5.03 - 1.0, start.X, 6);
        Assert.Equal(5.03 - 1.028, middle.X, 6);
        Assert.Equal(5.03 - 1.056, end.X, 6);
        Assert.Equal(-100.0 + 40.5, middle.Y, 6);
        Assert.Equal("approach_0", middle.LaneId);
        Assert.Equal(0.0, middle.HeadingDegrees, 6);          // the lane's tangent, as before
    }

    [Fact]
    public void TheStepInWhichSumoStartsReportingTheNewLaneMovesTheVehicleCentimetresNotALaneWidth()
    {
        // Measured through TraCI on this network at a 3 s lane change: half way across, SUMO moves the
        // vehicle onto approach_1 and its lateral offset from +1.675 m to -1.619 m, which is the same
        // place one step further across.
        var interpolator = Interpolator();
        CoSimVehicleFrame from = On("approach_0", 60.31) with { LateralOffsetMetres = 1.675 };
        CoSimVehicleFrame to = On("approach_1", 60.96) with { LateralOffsetMetres = -1.619 };

        InterpolatedState start = interpolator.Interpolate(from, to, 0.0, 0.05);
        InterpolatedState middle = interpolator.Interpolate(from, to, 0.5, 0.05);
        InterpolatedState end = interpolator.Interpolate(from, to, 1.0, 0.05);

        Assert.Equal(LaneInterpolationCase.LaneChange, middle.Case);
        Assert.Equal(5.03 - 1.675, start.X, 6);
        Assert.Equal(1.68 + 1.619, end.X, 6);
        Assert.InRange(middle.X, end.X, start.X);
    }

    /// <summary>
    /// One 40 m approach, a 10 m connector, and a two-lane exit the connector feeds only the right
    /// lane of. Written out here rather than built with netconvert because the whole point of it is
    /// a connection that reaches one lane of an edge and not its sibling, which is a property of the
    /// connection table rather than of any geometry.
    /// </summary>
    private const string ExitWithASecondLane = """
        <net>
          <location netOffset="0.00,0.00" convBoundary="0.00,-10.00,100.00,10.00"
                    origBoundary="0.00,-10.00,100.00,10.00" projParameter="!"/>
          <edge id=":junction_0" function="internal">
            <lane id=":junction_0_0" index="0" speed="20.00" length="10.00" width="3.20"
                  shape="40.00,-1.60 50.00,-1.60"/>
          </edge>
          <edge id="approach" from="west" to="junction">
            <lane id="approach_0" index="0" speed="20.00" length="40.00" width="3.20"
                  shape="0.00,-1.60 40.00,-1.60"/>
          </edge>
          <edge id="exit" from="junction" to="east">
            <lane id="exit_0" index="0" speed="20.00" length="50.00" width="3.20"
                  shape="50.00,-1.60 100.00,-1.60"/>
            <lane id="exit_1" index="1" speed="20.00" length="50.00" width="3.20"
                  shape="50.00,1.60 100.00,1.60"/>
          </edge>
          <connection from="approach" to="exit" fromLane="0" toLane="0" via=":junction_0_0"
                      dir="s" state="M"/>
          <connection from=":junction_0" to="exit" fromLane="0" toLane="0" dir="s" state="M"/>
        </net>
        """;

    [Fact]
    public void LeavingAJunctionAndChangingLaneAtOnceIsALaneChangeAndNotADiscontinuity()
    {
        // The vehicle ends up on a lane the connector does not feed, so no route reaches it at all.
        // There is a route to its sibling, and the difference between the two is a sideways move.
        var interpolator = new LaneArcInterpolator(SumoRoadNetwork.Parse(ExitWithASecondLane));
        CoSimVehicleFrame from = On("approach_0", 35.0, speed: 20.0);
        CoSimVehicleFrame to = On("exit_1", 5.0, speed: 20.0);

        InterpolatedState start = interpolator.Interpolate(from, to, 0.0, 1.0);
        InterpolatedState end = interpolator.Interpolate(from, to, 1.0, 1.0);

        Assert.Equal(LaneInterpolationCase.CrossedEdgesWithLaneChange, end.Case);
        Assert.Equal(-1.60, start.Y, 2);          // still on the lane it came in on
        Assert.Equal(35.0, start.X, 2);
        Assert.Equal(1.60, end.Y, 2);             // arrived on the lane it reported
        Assert.Equal(55.0, end.X, 2);
        Assert.Equal(20.0, interpolator.RouteDistance(from, to)!.Value, 6);
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

    [RequiresSumoFact]
    public void SumoSpreadsALaneChangeOverItsDurationAndSwitchesTheReportedLaneHalfWay()
    {
        // What SUMO 1.27.0 reports while a lane change is spread over time, read through the bridge's
        // own subscription: `changer` departs on the right-hand lane of the approach and has to move
        // left to turn left.
        List<CoSimVehicleFrame> track = RecordTrack("changer", steps: 400, LaneChangeOverThreeSeconds);
        List<int> changing = Enumerable.Range(0, track.Count)
            .Where(index => Math.Abs(track[index].LateralOffsetMetres) > 1e-9).ToList();
        Assert.NotEmpty(changing);

        // It takes the duration: sixty 0.05 s steps from the last step on the centre line to the first
        // back on it.
        Assert.Equal(60, (changing[^1] + 1) - (changing[0] - 1));
        Assert.True(changing.Zip(changing.Skip(1)).All(pair => pair.Second == pair.First + 1),
                    "the lateral offset returned to zero part-way through the change");

        // The reported lane changes once, from the lane it started on to the one it moves to, at the
        // step the offset passes half the two lanes' widths and is carried over to the new lane.
        List<int> switches = changing.Where(index => track[index].LaneId != track[index - 1].LaneId).ToList();
        int flip = Assert.Single(switches);
        Assert.Equal("approach_0", track[flip - 1].LaneId);
        Assert.Equal("approach_1", track[flip].LaneId);
        Assert.Equal(3.35 / 2.0, track[flip - 1].LateralOffsetMetres, 0.06);
        Assert.Equal(-3.35 / 2.0, track[flip].LateralOffsetMetres, 0.06);

        // The reported position includes the offset, and moves across at a steady 3.35 m over 3 s.
        foreach (int index in changing.Skip(1))
        {
            double across = track[index - 1].X - track[index].X;   // the approach runs north: left is -x
            Assert.Equal(3.35 / 3.0 * 0.05, across, 0.001);
        }

        // And SUMO's angle turns with the sideways movement, where the lane runs due north.
        double furthest = changing.Max(index => 360.0 - track[index].HeadingDegrees);
        Assert.True(furthest > 3.0, $"SUMO's angle turned only {furthest:0.00} degrees from the lane");
    }

    [RequiresSumoFact]
    public void ThroughALaneChangeSpreadOverTimeThePoseIsSumoSPositionAndNeverJumps()
    {
        List<CoSimVehicleFrame> track = RecordTrack("changer", steps: 400, LaneChangeOverThreeSeconds);
        var interpolator = Interpolator();
        double worstAtFrames = 0.0;
        double worstStep = 0.0;

        for (int index = 1; index < track.Count; index++)
        {
            CoSimVehicleFrame from = track[index - 1];
            CoSimVehicleFrame to = track[index];
            InterpolatedState start = interpolator.Interpolate(from, to, 0.0, 0.05);
            InterpolatedState end = interpolator.Interpolate(from, to, 1.0, 0.05);
            Assert.NotEqual(LaneInterpolationCase.Discontinuous, end.Case);
            worstAtFrames = Math.Max(worstAtFrames, Math.Max(Distance(start.X, start.Y, from),
                                                             Distance(end.X, end.Y, to)));
            worstStep = Math.Max(worstStep, Distance(end.X, end.Y, from) - from.SpeedMetresPerSecond * 0.05);
        }

        // Measured: SUMO's own position at every frame to well under a millimetre -- through the step
        // its reported lane changes, which on the lane's centre line alone was a lane width of
        // sideways jump -- and no step further than SUMO's own movement plus a few centimetres.
        Assert.True(worstAtFrames < 1e-3, $"the pose left SUMO's position by {worstAtFrames:0.0000} m");
        Assert.True(worstStep < 0.1, $"a step moved {worstStep:0.000} m further than SUMO's speed");
    }

    private static readonly SumoLaunchOptions LaneChangeOverThreeSeconds = new()
    {
        ExtraArguments = ["--lanechange.duration", "3"],
        Output = _ => { },
    };

    private static double Distance(double x, double y, in CoSimVehicleFrame truth) =>
        Math.Sqrt(Math.Pow(x - truth.X, 2) + Math.Pow(y - truth.Y, 2));

    /// <summary>Every step of one vehicle's life, read through the bridge's own subscription.</summary>
    private static List<CoSimVehicleFrame> RecordTrack(string vehicleId, int steps,
                                                       SumoLaunchOptions? options = null)
    {
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario, options);
        var population = new SubscribedPopulation(sumo.TraCI);
        List<CoSimVehicleFrame> track = [];

        for (int step = 0; step < steps; step++)
        {
            sumo.Step();
            population.Reconcile(sumo.Simulation.DepartedVehicleIds,
                                 sumo.Simulation.ArrivedVehicleIds);
            if (population.TryReadFrame(vehicleId, out CoSimVehicleFrame frame))
            {
                track.Add(frame);
            }
        }

        return track;
    }

    // A vehicle parked at a stop: SUMO reports no lane for it for as long as it is parked.
    private static CoSimVehicleFrame Parked(double x, double y, double heading = 348.9,
                                            double speed = 0.0) =>
        new("guard", x, y, heading, speed, "", "", 306.10, "measured_truck", SumoVehicleSignals.None);

    [Fact]
    public void AVehicleParkedAtBothEndsIsHeldWhereItStandsAndIsNotADiscontinuity()
    {
        InterpolatedState state = Interpolator().Interpolate(
            Parked(728.47, 1394.75), Parked(728.47, 1394.75), 0.5, 1.0);

        Assert.Equal(LaneInterpolationCase.OffLane, state.Case);
        Assert.Equal(728.47, state.X, 9);
        Assert.Equal(1394.75, state.Y, 9);
        Assert.Equal(348.9, state.HeadingDegrees, 9);
    }

    [Fact]
    public void PullingIntoAStopIsBlendedAcrossTheMeasuredStepToTheKerb()
    {
        // Measured on Bahonar through TraCI over sixteen guards: 3.20 m to 7.05 m in the step the
        // lane became empty, at as little as 0.09 m/s, which a limit from speed alone calls a jump.
        CoSimVehicleFrame onLane = On("approach_0", 50.0, speed: 0.12) with { X = 721.42, Y = 1394.75 };
        CoSimVehicleFrame parked = Parked(728.47, 1394.75, speed: 0.12);

        InterpolatedState state = Interpolator().Interpolate(onLane, parked, 0.5, 1.0);

        Assert.Equal(LaneInterpolationCase.OffLane, state.Case);
        Assert.Equal(724.945, state.X, 6);
    }

    [Fact]
    public void AnOffLaneMoveTheVehicleCouldNotHaveMadeIsStillADiscontinuity()
    {
        InterpolatedState state = Interpolator().Interpolate(
            Parked(728.47, 1394.75), Parked(778.47, 1394.75), 0.5, 1.0);

        Assert.Equal(LaneInterpolationCase.Discontinuous, state.Case);
        Assert.Equal(778.47, state.X, 9);
    }

    [Fact]
    public void ALaneTheNetworkDoesNotKnowIsStillADiscontinuity()
    {
        InterpolatedState state = Interpolator().Interpolate(
            On("nowhere_0", 10.0), On("nowhere_0", 12.0), 0.5, 1.0);

        Assert.Equal(LaneInterpolationCase.Discontinuous, state.Case);
    }

    [Fact]
    public void TheRunReportSamplesAVehicleOnceAndCountsTheTicksItRecurredOn()
    {
        var sampler = new DiscontinuitySampler(limit: 20);
        CoSimVehicleFrame a = On("turn_east_0", 10.0) with { Id = "a" };
        CoSimVehicleFrame b = On("turn_east_0", 10.0) with { Id = "b" };
        for (int tick = 0; tick < 30; tick++)
        {
            sampler.Sample(a, a, null);
        }
        sampler.Sample(b, b, null);

        Assert.Equal(2, sampler.Samples.Count);
        string[] lines = [.. sampler.Lines()];
        Assert.StartsWith("a: turn_east_0@10.00", lines[0]);
        Assert.EndsWith("on 30 ticks", lines[0]);
        Assert.DoesNotContain("ticks", lines[1]);
    }

    [Fact]
    public void TheLaneTheBumperIsOnAndHowFarAlongItAreCarriedThroughEveryCase()
    {
        // The pose's height comes from the road the vehicle is on, so every interpolated state names the
        // lane it was evaluated on and where along it -- not the two reported lanes.
        var interpolator = Interpolator();

        InterpolatedState along = interpolator.Interpolate(On("approach_0", 10.0), On("approach_0", 30.0), 0.5, 1.0);
        Assert.Equal(("approach_0", 20.0), (along.LaneId, Math.Round(along.LanePositionMetres, 6)));

        // A lane change within an edge: the lane the sideways blend is nearer, at the shared position.
        InterpolatedState early = interpolator.Interpolate(On("approach_0", 40.0), On("approach_1", 65.0), 0.25, 1.0);
        InterpolatedState late = interpolator.Interpolate(On("approach_0", 40.0), On("approach_1", 65.0), 0.75, 1.0);
        Assert.Equal("approach_0", early.LaneId);
        Assert.Equal("approach_1", late.LaneId);
        Assert.Equal(46.25, early.LanePositionMetres, 6);

        // Across a junction: on the connector in the middle of the step, at its own position.
        CoSimVehicleFrame from = On("approach_0", 92.65 - 2.0, speed: 10.0);
        CoSimVehicleFrame to = On("turn_east_0", 2.0, speed: 10.0);
        double route = interpolator.RouteDistance(from, to)!.Value;
        InterpolatedState inside = interpolator.Interpolate(from, to, 0.5, 1.0);
        Assert.Equal(LaneInterpolationCase.CrossedEdges, inside.Case);
        Assert.Equal(":centre_0_0", inside.LaneId);
        Assert.Equal((0.5 * route) - 2.0, inside.LanePositionMetres, 6);
        Assert.Equal("approach_0", interpolator.Interpolate(from, to, 0.0, 1.0).LaneId);
        Assert.Equal("turn_east_0", interpolator.Interpolate(from, to, 1.0, 1.0).LaneId);

        // Out of a junction onto the lane beside the one the connector feeds.
        var exit = new LaneArcInterpolator(SumoRoadNetwork.Parse(ExitWithASecondLane));
        InterpolatedState leaving = exit.Interpolate(On("approach_0", 35.0, speed: 20.0),
                                                     On("exit_1", 5.0, speed: 20.0), 1.0, 1.0);
        Assert.Equal(("exit_1", 5.0), (leaving.LaneId, Math.Round(leaving.LanePositionMetres, 6)));

        // A discontinuity is placed at the later frame, on its lane.
        InterpolatedState jumped = interpolator.Interpolate(On("turn_east_0", 10.0), On("approach_0", 12.0), 0.5, 1.0);
        Assert.Equal(("approach_0", 12.0), (jumped.LaneId, jumped.LanePositionMetres));

        // And a vehicle parked off its lane is on none.
        InterpolatedState parked = interpolator.Interpolate(Parked(728.47, 1394.75), Parked(728.47, 1394.75), 0.5, 1.0);
        Assert.Equal(string.Empty, parked.LaneId);
    }

    private static LaneArcInterpolator Interpolator() =>
        new(SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork));

    private static SumoLane Lane(string laneId)
    {
        SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork).TryGetLane(laneId, out SumoLane lane);
        return lane;
    }
}
