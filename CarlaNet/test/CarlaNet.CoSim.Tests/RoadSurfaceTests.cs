namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A body on an at-grade road is seated on the ground exactly; one on a structure takes its height and
/// pitch from its road's OpenDRIVE profile and has no roll; one between is blended continuously, by how
/// far the road departs from the ground at its reference line; a junction connector meets the roads it
/// joins without a step; one on no road sits on the ground as before; and every SUMO lane finds the road
/// the world build left it on.
/// </summary>
public sealed class RoadSurfaceTests
{
    // A body whose origin is its front bumper and whose origin sits on the surface, so a pose's position
    // is the point asked about and its height is the surface's.
    private static readonly VehicleExtent Point = new("vehicle.test.point", 0.0, 2.0, 1.5, (0.0, 0.0, 0.75));

    // A car-sized body, whose origin is 2.5 m behind the bumper.
    private static readonly VehicleExtent Car = new("vehicle.test.car", 5.0, 2.0, 1.5, (0.0, 0.0, 0.75));

    private const double Speed = 10.0;

    /// <summary>
    /// A deck over a road that passes beneath it, an at-grade road across a camber, and a four-lane
    /// at-grade road across a wider one, over a ground that domes up two metres under the deck -- the
    /// shape World Terrain takes under a bridge -- and falls across the deck and both cambered roads. In
    /// SUMO's frame, east and north.
    /// </summary>
    private static double Ground(double x, double y)
    {
        double dome = 2.0 * Math.Max(0.0, 1.0 - (((x * x) + (y * y)) / (15.0 * 15.0)));
        double wide = Band(y - 36.0, 0.05, 12.0) * Math.Clamp((x - 20.0) / 5.0, 0.0, 1.0);
        return dome + Band(y, 0.04, 10.0) + Band(y + 70.0, 0.05, 8.0) + wide;

        // A cross-slope of grade across |u| <= half, fading to nothing over four metres beyond.
        static double Band(double u, double grade, double half)
        {
            double magnitude = Math.Abs(u);
            return magnitude <= half
                ? grade * u
                : grade * Math.Sign(u) * half * Math.Max(0.0, 1.0 - ((magnitude - half) / 4.0));
        }
    }

    private static readonly double AtGradeProfile = Ground(0.0, -70.0 + (0.5 * SyntheticRoads.LaneWidth));

    // The wide road's reference line runs three and a half lanes north of its outer lane's centre.
    private static readonly double WideProfile = Ground(60.0, 30.0 + (3.5 * SyntheticRoads.LaneWidth));

    private static SyntheticRoads Crossing() => new SyntheticRoads()
        // The deck: flat, ramp up at 15 %, flat at 6 m over x in [-20, 20], ramp down, flat. Its lane runs
        // east along y = 0, through the dome's centre.
        .Road(1, "deck", "Deck Street", -1, (-80.0, 0.0), 0.0, 160.0, 1,
              [(0.0, 0.0, 0.0), (20.0, 0.0, 0.15), (60.0, 6.0, 0.0), (100.0, 6.0, -0.15), (140.0, 0.0, 0.0)])
        // The road beneath it, spanning the dome on a level chord. Its lane runs north along x = 0.
        .Road(2, "under", "Under Avenue", -1, (0.0, -40.0), Math.PI / 2.0, 80.0, 1, [(0.0, 0.0, 0.0)])
        // An at-grade road east along y = -70, across a 5 % camber, at the ground's height at its
        // reference line.
        .Road(3, "atgrade", "Level Road", -1, (-80.0, -70.0), 0.0, 160.0, 1, [(0.0, AtGradeProfile, 0.0)])
        // A four-lane at-grade road east, its outer lane along y = 30, across a 5 % cross-fall, at the
        // ground's height at its reference line.
        .Road(4, "wide", "Wide Boulevard", -1, (30.0, 30.0), 0.0, 60.0, 4, [(0.0, WideProfile, 0.0)])
        .Edge("deck", (-80.0, 0.0), 0.0, 160.0)
        .Edge("under", (0.0, -40.0), Math.PI / 2.0, 80.0)
        .Edge("atgrade", (-80.0, -70.0), 0.0, 160.0)
        .Edge("wide", (30.0, 30.0), 0.0, 60.0, lanes: 4)
        // An edge no road was written for.
        .Edge("orphan", (-80.0, 50.0), 0.0, 160.0);

    private static GroundSurface GroundSurface() =>
        SyntheticWorld.Build(at => Ground(at.X, -at.Y));

    [Fact]
    public void ABodyOnTheDeckRidesTheDeckAndOneBeneathItRidesItsOwnRoad()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);
        var groundOnly = new PoseConverter(ground);

        // The same plan position, (0, 0), on each of the two roads that cross there.
        VehiclePose onDeck = converter.Convert("d", Point, 0.0, 0.0, 90.0, Speed, "deck_0", 80.0)!.Value;
        VehiclePose beneath = converter.Convert("u", Point, 0.0, 0.0, 0.0, Speed, "under_0", 40.0)!.Value;
        VehiclePose wasDeck = groundOnly.Convert("d", Point, 0.0, 0.0, 90.0, Speed)!.Value;
        VehiclePose wasBeneath = groundOnly.Convert("u", Point, 0.0, 0.0, 0.0, Speed)!.Value;

        Assert.Equal(6.0, onDeck.Z, 3);
        Assert.Equal(0.0, beneath.Z, 3);
        Assert.Equal(1u, onDeck.Road!.Value.RoadId);
        Assert.Equal(2u, beneath.Road!.Value.RoadId);

        // Where the ground alone seated both: on the one height it holds there, the dome.
        Assert.Equal(2.0, wasDeck.Z, 2);
        Assert.Equal(2.0, wasBeneath.Z, 2);

        // On a structure, judged at each road's reference line, where its profile is defined: the deck
        // about four metres above the ground, the road beneath two below. Seated wholly on the profile,
        // flat across.
        Assert.Equal(6.0 - Ground(0.0, 0.5 * SyntheticRoads.LaneWidth),
                     onDeck.Road!.Value.DepartureFromGroundMetres, tolerance: 0.01);
        Assert.Equal(-Ground(-0.5 * SyntheticRoads.LaneWidth, 0.0),
                     beneath.Road!.Value.DepartureFromGroundMetres, tolerance: 0.01);
        Assert.Equal(0.0, onDeck.Road!.Value.GroundWeight);
        Assert.Equal(0.0, beneath.Road!.Value.GroundWeight);
        Assert.Equal(0.0, onDeck.RollDegrees, 9);
        Assert.Equal(0.0, beneath.RollDegrees, 9);
        Assert.Equal(0.0, onDeck.PitchDegrees, 6);
        Assert.Equal(GroundSeatReason.None, onDeck.GroundReason);
    }

    [Fact]
    public void OnAStructureThePitchAndClimbAreTheProfileSSlopeSignedForTheWayTheBodyTravels()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);

        // Up the ramp at 15 %, eastwards along +s, three metres above the ground: nose up, climbing.
        VehiclePose climbing = converter.Convert("d", Point, -40.0, 0.0, 90.0, Speed, "deck_0", 40.0)!.Value;
        Assert.Equal(0.0, climbing.Road!.Value.GroundWeight);
        Assert.Equal(3.0, climbing.Z, 3);
        Assert.Equal(Math.Atan(0.15) * (180.0 / Math.PI), climbing.PitchDegrees, 6);
        Assert.Equal(Speed * 0.15, climbing.VelocityZ, 6);

        // Down the far ramp: nose down, descending.
        VehiclePose descending = converter.Convert("d", Point, 40.0, 0.0, 90.0, Speed, "deck_0", 120.0)!.Value;
        Assert.Equal(3.0, descending.Z, 3);
        Assert.Equal(-Math.Atan(0.15) * (180.0 / Math.PI), descending.PitchDegrees, 6);
        Assert.Equal(-Speed * 0.15, descending.VelocityZ, 6);

        // And the same ramp travelled against +s -- westwards, heading the other way along the road --
        // descends where the eastbound body climbs.
        Assert.True(roads.TrySeat("deck_0", 40.0, -40.0, 0.0, -1.0, 0.0, 0.0, out RoadPlace westbound, out _));
        Assert.Equal(-0.15, westbound.SlopeAlongHeading, 6);
        Assert.True(roads.TrySeat("deck_0", 40.0, -40.0, 0.0, 1.0, 0.0, 0.0, out RoadPlace eastbound, out _));
        Assert.Equal(0.15, eastbound.SlopeAlongHeading, 6);
    }

    [Fact]
    public void AtGradeTheBodyIsSeatedExactlyAsTheGroundSeatsIt()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);
        var groundOnly = new PoseConverter(ground);

        VehiclePose pose = converter.Convert("a", Point, 0.0, -70.0, 90.0, Speed, "atgrade_0", 80.0)!.Value;
        VehiclePose was = groundOnly.Convert("a", Point, 0.0, -70.0, 90.0, Speed)!.Value;

        // The camber falls to the south, the body's right, so it rolls right side down: positive.
        Assert.True(was.RollDegrees > 2.0, $"the ground's roll is {was.RollDegrees}");
        AssertTheGroundSSeat(was, pose);
        Assert.Equal(1.0, pose.Road!.Value.GroundWeight);
        Assert.Equal(3u, pose.Road!.Value.RoadId);

        // On the visible road, a few centimetres below where the flat-across profile would have put it.
        Assert.Equal(0.0, pose.Z, 6);
        Assert.Equal(AtGradeProfile, pose.Road!.Value.SurfaceZMetres, 6);
        Assert.Equal(0.0, pose.Road!.Value.DepartureFromGroundMetres, tolerance: 1e-3);
    }

    [Fact]
    public void AnOuterLaneAcrossAWideCamberIsSeatedOnTheGroundWithItsWholeRoll()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);
        var groundOnly = new PoseConverter(ground);

        // The outer lane of four, 11.7 m across the cross-fall from the reference line: the flat-across
        // profile stands 0.59 m above the ground under the lane -- past the half-metre the weight was
        // first taken against at the lane -- and agrees with the ground at the reference line.
        VehiclePose pose = converter.Convert("o", Point, 60.0, 30.0, 90.0, Speed, "wide_0", 30.0)!.Value;
        VehiclePose was = groundOnly.Convert("o", Point, 60.0, 30.0, 90.0, Speed)!.Value;
        double atTheLane = WideProfile - Ground(60.0, 30.0);
        Assert.True(atTheLane > PoseConverter.AtGradeDepartureMetres, $"the lane stands {atTheLane} m off the ground");

        AssertTheGroundSSeat(was, pose);
        Assert.True(Math.Abs(was.RollDegrees) > 2.5, $"the ground's roll is {was.RollDegrees}");
        Assert.Equal(1.0, pose.Road!.Value.GroundWeight);
        Assert.Equal(4u, pose.Road!.Value.RoadId);
        Assert.Equal(0.0, pose.Road!.Value.DepartureFromGroundMetres, tolerance: 1e-3);

        // And every lane of the road alike.
        foreach (int lane in new[] { 1, 2, 3 })
        {
            double y = 30.0 + (lane * SyntheticRoads.LaneWidth);
            AssertTheGroundSSeat(groundOnly.Convert("o", Point, 60.0, y, 90.0, Speed)!.Value,
                                 converter.Convert("o", Point, 60.0, y, 90.0, Speed, $"wide_{lane}", 30.0)!.Value);
        }
    }

    [Fact]
    public void ABodyClimbingOntoTheDeckMovesFromTheGroundToTheProfileContinuously()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);
        var groundOnly = new PoseConverter(ground);
        const double Step = 0.25;

        VehiclePose? previous = null;
        double previousWeight = 1.0;
        int blended = 0;
        double worstHeightStep = 0.0;
        double worstPitchStep = 0.0;
        double worstRollStep = 0.0;
        double worstClimbMismatch = 0.0;

        // From the flat approach, up the 15 % ramp out of the ground and onto its profile, short of the
        // crest at s = 60 where the profile itself turns level without a vertical curve.
        for (double x = -79.0; x <= -25.0; x += Step)
        {
            double along = x + 80.0;
            VehiclePose pose = converter.Convert("d", Point, x, 0.0, 90.0, Speed, "deck_0", along)!.Value;
            VehiclePose was = groundOnly.Convert("d", Point, x, 0.0, 90.0, Speed)!.Value;
            RoadSeat seat = pose.Road!.Value;
            double weight = seat.GroundWeight;

            // The departure's own weight, or less where that would fall faster than the road allows: so at
            // each metre the weight is tabulated, and linear between.
            if (Math.Abs(along - Math.Round(along)) < 1e-9)
            {
                Assert.True(weight <= PoseConverter.GroundWeight(seat.DepartureFromGroundMetres) + 1e-12,
                            $"the weight {weight} exceeds the departure's at x = {x}");
            }

            Assert.True(weight <= previousWeight, $"the weight rose at x = {x}");
            // A smoothstep over that stretch falls at most one and a half times its mean rate.
            Assert.True(previousWeight - weight <= (1.5 * Step / PoseConverter.WeightChangeMetres) + 1e-9,
                        $"the weight fell {previousWeight - weight} in a quarter-metre at x = {x}");

            // On the ground before the ramp leaves it, on the profile from where it is a structure, and
            // between each blended by the one weight.
            if (weight >= 1.0)
            {
                AssertTheGroundSSeat(was, pose);
            }
            else if (weight <= 0.0)
            {
                Assert.Equal(seat.SurfaceZMetres, pose.Z, 12);
                Assert.Equal(Math.Atan(seat.SlopeAlongHeading) * (180.0 / Math.PI), pose.PitchDegrees, 9);
                Assert.Equal(0.0, pose.RollDegrees, 12);
            }
            else
            {
                blended++;
                Assert.Equal((weight * was.Z) + ((1.0 - weight) * seat.SurfaceZMetres), pose.Z, 9);
                Assert.Equal(weight * was.RollDegrees, pose.RollDegrees, 9);
            }

            // The climb is the pitch's slope times the speed, always.
            Assert.Equal(Speed * Math.Tan(pose.PitchDegrees * (Math.PI / 180.0)), pose.VelocityZ, 9);

            if (previous is { } before)
            {
                worstHeightStep = Math.Max(worstHeightStep, Math.Abs(pose.Z - before.Z));
                worstPitchStep = Math.Max(worstPitchStep, Math.Abs(pose.PitchDegrees - before.PitchDegrees));
                worstRollStep = Math.Max(worstRollStep, Math.Abs(pose.RollDegrees - before.RollDegrees));

                // And the climb is the seat's own: the height a quarter-metre on, against the mean of the
                // two climbs, so a body is tangent to the path it rides through the blend.
                double rise = (pose.Z - before.Z) / Step;
                double climb = 0.5 * (pose.VelocityZ + before.VelocityZ) / Speed;
                worstClimbMismatch = Math.Max(worstClimbMismatch, Math.Abs(rise - climb));
            }

            previous = pose;
            previousWeight = weight;
        }

        Assert.True(blended > 10, $"only {blended} points were blended");
        Assert.Equal(0.0, previousWeight);
        // A 15 % ramp leaves the ground faster than the weight may fall, so the blend is spread over ten
        // metres from the ramp's foot, and the seat's slope runs from level to the ramp's and a little past
        // it -- where the profile alone would turn 8.5 degrees in one step at the foot. A step in the pitch
        // would part the climb from the rise, checked below.
        Assert.True(worstHeightStep < 0.06, $"the height stepped {worstHeightStep} m in a quarter-metre");
        Assert.True(worstPitchStep < 1.0, $"the pitch stepped {worstPitchStep} degrees in a quarter-metre");
        Assert.True(worstRollStep < 0.2, $"the roll stepped {worstRollStep} degrees in a quarter-metre");
        Assert.True(worstClimbMismatch < 0.01, $"the climb and the seat's rise parted by {worstClimbMismatch}");
    }

    [Fact]
    public void TheGroundWeightIsOneAtGradeZeroOnAStructureAndSmoothBetween()
    {
        Assert.Equal(1.0, PoseConverter.GroundWeight(0.0));
        Assert.Equal(1.0, PoseConverter.GroundWeight(PoseConverter.AtGradeDepartureMetres));
        Assert.Equal(1.0, PoseConverter.GroundWeight(-PoseConverter.AtGradeDepartureMetres));
        Assert.Equal(0.0, PoseConverter.GroundWeight(PoseConverter.OnStructureDepartureMetres));
        Assert.Equal(0.0, PoseConverter.GroundWeight(-6.0));
        Assert.Equal(0.5, PoseConverter.GroundWeight(
            0.5 * (PoseConverter.AtGradeDepartureMetres + PoseConverter.OnStructureDepartureMetres)), 12);

        // Continuous everywhere, and flat at both ends of the blend.
        double previous = 1.0;
        for (double departure = -2.0; departure <= 2.0; departure += 0.001)
        {
            double weight = PoseConverter.GroundWeight(departure);
            Assert.True(Math.Abs(weight - previous) < 0.0025 || departure <= -1.999, $"a step at {departure} m");
            previous = weight;
        }

        foreach (double edge in new[] { PoseConverter.AtGradeDepartureMetres, PoseConverter.OnStructureDepartureMetres })
        {
            Assert.Equal(PoseConverter.GroundWeight(edge), PoseConverter.GroundWeight(edge + 1e-4), 6);
        }
    }

    /// <summary>The pose the ground alone gives, and the road's seat at grade, are one seat.</summary>
    private static void AssertTheGroundSSeat(in VehiclePose ground, in VehiclePose seated)
    {
        Assert.Equal(ground.X, seated.X);
        Assert.Equal(ground.Y, seated.Y);
        Assert.Equal(ground.Z, seated.Z);
        Assert.Equal(ground.YawDegrees, seated.YawDegrees);
        Assert.Equal(ground.PitchDegrees, seated.PitchDegrees);
        Assert.Equal(ground.RollDegrees, seated.RollDegrees);
        Assert.Equal(ground.VelocityX, seated.VelocityX);
        Assert.Equal(ground.VelocityY, seated.VelocityY);
        Assert.Equal(ground.VelocityZ, seated.VelocityZ);
    }

    [Fact]
    public void ABodyOnNoLaneOrOnALaneNoRoadCarriesSitsOnTheGroundAsBefore()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);
        var groundOnly = new PoseConverter(ground);

        // Parked off its lane: SUMO names none.
        VehiclePose parked = converter.Convert("p", Point, 3.0, 4.0, 90.0, 0.0, string.Empty, 0.0)!.Value;
        Assert.Null(parked.Road);
        Assert.Equal(GroundSeatReason.NoLane, parked.GroundReason);
        Assert.Equal(groundOnly.Convert("p", Point, 3.0, 4.0, 90.0, 0.0)!.Value with { GroundReason = GroundSeatReason.NoLane },
                     parked);

        // On an edge the OpenDRIVE has no road for.
        VehiclePose orphan = converter.Convert("o", Point, 0.0, 50.0, 90.0, Speed, "orphan_0", 80.0)!.Value;
        Assert.Equal(GroundSeatReason.NoRoad, orphan.GroundReason);
        Assert.Equal(groundOnly.Convert("o", Point, 0.0, 50.0, 90.0, Speed)!.Value.Z, orphan.Z, 12);

        // Twelve metres off the deck's lane, beside the road rather than on it.
        VehiclePose beside = converter.Convert("b", Point, -70.0, 12.0, 90.0, Speed, "deck_0", 10.0)!.Value;
        Assert.Equal(GroundSeatReason.OffTheRoad, beside.GroundReason);

        Assert.True(roads.TryGetLaneRoad("deck_0", out uint deck, out _, out _));
        Assert.Equal(1u, deck);
        Assert.False(roads.TryGetLaneRoad("orphan_0", out _, out _, out _));
        Assert.Equal(["orphan_0"], roads.Mapping.UnmappedLanes);
    }

    /// <summary>
    /// A merged road -- an edge, its connector and the edge after, collapsed into one road of three lane
    /// sections carrying only the first edge's <c>sumoId</c> -- and a junction whose one connector carries
    /// a split internal edge's two halves and a lane of another internal edge, named after neither.
    /// </summary>
    private static SyntheticRoads Merges() => new SyntheticRoads()
        .Road(10, "E1", "Merged Way", -1, (0.0, 0.0), 0.0, 100.0, 1, [(0.0, 0.0, 0.05)],
              sections: [(50.0, 1), (54.0, 1)])
        .Edge("E1", (0.0, 0.0), 0.0, 50.0)
        .Edge(":J_0", (50.0, 0.0), 0.0, 4.0)
        .Edge("E2", (54.0, 0.0), 0.0, 46.0)
        .Connection("E1", 0, "E2", 0, ":J_0_0")
        .Connection(":J_0", 0, "E2", 0)
        .Road(20, "F1", "Before", -1, (0.0, -30.0), 0.0, 50.0, 2, [(0.0, 1.0, 0.0)])
        .Road(21, null, ":K_0", 7, (50.0, -30.0), 0.0, 10.0, 2, [(0.0, 1.0, 0.1)], predecessor: 20, successor: 22)
        .Road(22, "F2", "After", -1, (60.0, -30.0), 0.0, 40.0, 2, [(0.0, 2.0, 0.0)])
        .Edge("F1", (0.0, -30.0), 0.0, 50.0, lanes: 2)
        .Edge(":K_0", (50.0, -30.0), 0.0, 5.0)
        .Edge(":K_4", (55.0, -30.0), 0.0, 5.0)
        .Edge(":K_1", (50.0, -30.0 + SyntheticRoads.LaneWidth), 0.0, 10.0)
        .Edge("F2", (60.0, -30.0), 0.0, 40.0, lanes: 2)
        .Connection("F1", 0, "F2", 0, ":K_0_0")
        .Connection(":K_0", 0, "F2", 0, ":K_4_0")
        .Connection(":K_4", 0, "F2", 0)
        .Connection("F1", 1, "F2", 1, ":K_1_0")
        .Connection(":K_1", 0, "F2", 1);

    [Fact]
    public void EveryLaneFindsTheRoadTheWorldBuildLeftItOn()
    {
        RoadSurface roads = Merges().Join();

        // The merged road: the edge its sumoId names on its first section, and the connector and the edge
        // the merge absorbed on the next two, recovered through the lane sections and SUMO's connections.
        AssertOn(roads, "E1_0", 10, 0.0, 50.0);
        AssertOn(roads, ":J_0_0", 10, 50.0, 54.0);
        AssertOn(roads, "E2_0", 10, 54.0, 100.0);

        // The split internal edge's halves, end to end on the one connector; and the lane of the internal
        // edge no connector is named after, on the connector whose links join the same two roads.
        AssertOn(roads, ":K_0_0", 21, 0.0, 5.0);
        AssertOn(roads, ":K_4_0", 21, 5.0, 10.0);
        AssertOn(roads, ":K_1_0", 21, 0.0, 10.0);
        AssertOn(roads, "F1_1", 20, 0.0, 50.0);
        AssertOn(roads, "F2_0", 22, 0.0, 40.0);

        RoadMappingSummary mapping = roads.Mapping;
        Assert.Empty(mapping.UnmappedLanes);
        Assert.Equal(mapping.Lanes, mapping.MappedLanes);
        Assert.Equal(1, mapping.LanesByKind[RoadMappingKind.MergedConnector]);
        Assert.Equal(1, mapping.LanesByKind[RoadMappingKind.MergedEdge]);
        Assert.Equal(1, mapping.LanesByKind[RoadMappingKind.SecondHalf]);
        Assert.True(mapping.Lateral.WorstAbsolute < 1e-6, $"lateral residual {mapping.Lateral}");
        Assert.True(mapping.AlongTrack.WorstAbsolute < 1e-6, $"along-track residual {mapping.AlongTrack}");

        static void AssertOn(RoadSurface roads, string lane, uint road, double startS, double endS)
        {
            Assert.True(roads.TryGetLaneRoad(lane, out uint found, out double from, out double to), $"{lane} joined to no road");
            Assert.Equal(road, found);
            Assert.Equal(startS, from, 6);
            Assert.Equal(endS, to, 6);
        }
    }

    [Fact]
    public void ABodyWhoseOriginHasNotReachedItsBumperSRoadIsSeatedOnTheRoadBehind()
    {
        // The ground well below every road, so each body is seated on its road's profile.
        GroundSurface ground = SyntheticWorld.Build(_ => -5.0);
        RoadSurface roads = Merges().Join();
        var converter = new PoseConverter(ground, roads: roads);

        // The bumper a metre onto the road after the connector, the origin 2.5 m back on the connector,
        // which climbs at 10 %.
        VehiclePose crossing = converter.Convert("k", Car, 61.0, -30.0, 90.0, Speed, "F2_0", 1.0)!.Value;
        Assert.Equal(21u, crossing.Road!.Value.RoadId);
        Assert.Equal(8.5, crossing.Road!.Value.S, 6);
        Assert.Equal(1.85, crossing.Z, 6);
        Assert.Equal(Speed * 0.1, crossing.VelocityZ, 6);

        // On the merged road the section before is the same road, at its own s.
        VehiclePose merged = converter.Convert("m", Car, 55.0, 0.0, 90.0, Speed, "E2_0", 1.0)!.Value;
        Assert.Equal(10u, merged.Road!.Value.RoadId);
        Assert.Equal(52.5, merged.Road!.Value.S, 6);
        Assert.Equal(0.05 * 52.5, merged.Z, 6);

        // And from the road before the connector, onto its first metres: on the connector.
        VehiclePose entering = converter.Convert("e", Car, 53.0, -30.0, 90.0, Speed, ":K_0_0", 3.0)!.Value;
        Assert.Equal(21u, entering.Road!.Value.RoadId);
        Assert.Equal(0.5, entering.Road!.Value.S, 6);
    }

    /// <summary>
    /// A carriageway, a junction connector and the carriageway after it, at grade on level ground, drawn
    /// as netconvert and the world build draw them where the road after the junction has a raised median
    /// along its left edge: that road's reference line runs on the median, 1.2 m above its lanes, and its
    /// profile was fitted there; the connector's reference line is the left edge of the lane it carries,
    /// on the road surface, and its profile climbs to meet the road after at the joint. In SUMO's frame.
    /// </summary>
    private static double MedianGround(double x, double y)
    {
        double across = Math.Clamp(Math.Min(y - 5.0, 12.0 - y) / 2.0, 0.0, 1.0);
        return 1.2 * Math.Clamp((x - 4.0) / 4.0, 0.0, 1.0) * across;
    }

    private static SyntheticRoads MedianJunction() => new SyntheticRoads()
        .Road(30, "in", "Approach", -1, (-60.0, 0.0), 0.0, 60.0, 1, [(0.0, 0.0, 0.0)])
        .Road(31, null, ":m_0", 9, (0.0, 0.0), 0.0, 8.0, 1, [(0.0, 0.0, 1.2 / 8.0)], predecessor: 30, successor: 32)
        .Road(32, "out", "Divided Avenue", -1, (8.0, 0.0), 0.0, 60.0, 3, [(0.0, 1.2, 0.0)])
        .Edge("in", (-60.0, 0.0), 0.0, 60.0)
        .Edge(":m_0", (0.0, 0.0), 0.0, 8.0)
        .Edge("out", (8.0, 0.0), 0.0, 60.0, lanes: 3)
        .Connection("in", 0, "out", 0, ":m_0_0")
        .Connection(":m_0", 0, "out", 0);

    [Fact]
    public void AConnectorTakesItsWeightFromTheRoadsItJoinsSoTheSeatDoesNotStepAtTheJoint()
    {
        GroundSurface ground = SyntheticWorld.Build(at => MedianGround(at.X, -at.Y));
        RoadSurface roads = MedianJunction().Join();
        var converter = new PoseConverter(ground, roads: roads);

        // Through the junction on the lanes, which are at grade throughout: on the ground at every step.
        // Weighed at its own reference line the connector would have stood 1.2 m off the ground at the
        // joint and seated the body most of the way up its profile, a metre above the road after it.
        double worst = 0.0;
        int onTheConnector = 0;
        foreach ((string lane, double from, double to) in new[] { ("in_0", 50.0, 60.0), (":m_0_0", 0.0, 8.0), ("out_0", 0.0, 10.0) })
        {
            for (double position = from; position <= to; position += 0.25)
            {
                double x = lane switch { "in_0" => -60.0 + position, ":m_0_0" => position, _ => 8.0 + position };
                VehiclePose pose = converter.Convert("m", Point, x, 0.0, 90.0, Speed, lane, position)!.Value;
                RoadSeat seat = pose.Road!.Value;
                Assert.Equal(1.0, seat.GroundWeight);
                worst = Math.Max(worst, Math.Abs(pose.Z));
                if (seat.RoadId == 31u && position > 6.0)
                {
                    onTheConnector++;
                    Assert.True(seat.DepartureFromGroundMetres > PoseConverter.AtGradeDepartureMetres,
                                $"the connector departs {seat.DepartureFromGroundMetres} m at its own reference line");
                }
            }
        }

        Assert.True(onTheConnector > 0, "no point was on the connector's last metres");
        Assert.True(worst < 1e-9, $"a body stood {worst} m off the ground");

        // And the road after it is weighed at its own reference line, where its profile meets the median.
        Assert.Equal(0.0, converter.Convert("o", Point, 30.0, 0.0, 90.0, Speed, "out_0", 22.0)!.Value.Road!.Value
                                    .DepartureFromGroundMetres, tolerance: 1e-3);
    }

    [Fact]
    public void AnOpenDriveWithNoRoadsJoinsNothingAndEveryBodyStaysOnTheGround()
    {
        RoadSurface roads = RoadSurface.Build("<OpenDRIVE/>", SumoRoadNetwork.Parse(Crossing().Network));
        Assert.Equal(0, roads.Mapping.MappedLanes);
        Assert.Equal(roads.Mapping.Lanes, roads.Mapping.UnmappedLanes.Count);

        var converter = new PoseConverter(GroundSurface(), roads: roads);
        VehiclePose pose = converter.Convert("d", Point, 0.0, 0.0, 90.0, Speed, "deck_0", 80.0)!.Value;
        Assert.Equal(GroundSeatReason.NoRoad, pose.GroundReason);
        Assert.Equal(2.0, pose.Z, 2);
    }
}
