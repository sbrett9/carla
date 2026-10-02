namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A body takes its height and pitch from the OpenDRIVE profile of the road it is on, its roll from the
/// ground where that road is at grade and none on a structure, and everything from the ground where it is
/// on no road; and every SUMO lane finds the road the world build left it on.
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
    /// A deck over a road that passes beneath it, and an at-grade road across a camber, over a ground that
    /// domes up two metres under the deck -- the shape World Terrain takes under a bridge -- and falls
    /// across the deck and the cambered road. In SUMO's frame, east and north.
    /// </summary>
    private static double Ground(double x, double y)
    {
        double dome = 2.0 * Math.Max(0.0, 1.0 - (((x * x) + (y * y)) / (15.0 * 15.0)));
        return dome + Band(y, 0.04, 10.0) + Band(y + 70.0, 0.05, 8.0);

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
        .Edge("deck", (-80.0, 0.0), 0.0, 160.0)
        .Edge("under", (0.0, -40.0), Math.PI / 2.0, 80.0)
        .Edge("atgrade", (-80.0, -70.0), 0.0, 160.0)
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

        // Where the bridge seated both before: on the one height the ground holds there, the dome.
        Assert.Equal(2.0, wasDeck.Z, 2);
        Assert.Equal(2.0, wasBeneath.Z, 2);

        // On a structure, flat across: the deck four metres above the ground, the road beneath two below.
        Assert.Equal(4.0, onDeck.Road!.Value.DepartureFromGroundMetres, 2);
        Assert.Equal(-2.0, beneath.Road!.Value.DepartureFromGroundMetres, 2);
        Assert.Equal(0.0, onDeck.RollDegrees, 9);
        Assert.Equal(0.0, beneath.RollDegrees, 9);
        Assert.Equal(0.0, onDeck.PitchDegrees, 6);
        Assert.Equal(GroundSeatReason.None, onDeck.GroundReason);
    }

    [Fact]
    public void ThePitchAndClimbAreTheProfileSSlopeSignedForTheWayTheBodyTravels()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);

        // Up the ramp at 15 %, eastwards along +s: nose up, climbing.
        VehiclePose climbing = converter.Convert("d", Point, -40.0, 0.0, 90.0, Speed, "deck_0", 40.0)!.Value;
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
        Assert.True(roads.TrySeat("deck_0", 40.0, -40.0, 0.0, -1.0, 0.0, 0.0, out RoadSeat westbound, out _));
        Assert.Equal(-0.15, westbound.SlopeAlongHeading, 6);
        Assert.True(roads.TrySeat("deck_0", 40.0, -40.0, 0.0, 1.0, 0.0, 0.0, out RoadSeat eastbound, out _));
        Assert.Equal(0.15, eastbound.SlopeAlongHeading, 6);
    }

    [Fact]
    public void AtGradeTheBodyRollsExactlyAsTheGroundRollsItAndRidesTheProfile()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);
        var groundOnly = new PoseConverter(ground);

        VehiclePose pose = converter.Convert("a", Point, 0.0, -70.0, 90.0, Speed, "atgrade_0", 80.0)!.Value;
        VehiclePose was = groundOnly.Convert("a", Point, 0.0, -70.0, 90.0, Speed)!.Value;

        // The camber falls to the south, the body's right, so it rolls right side down: positive.
        Assert.True(was.RollDegrees > 2.0, $"the ground's roll is {was.RollDegrees}");
        Assert.Equal(was.RollDegrees, pose.RollDegrees, 12);
        Assert.Equal(1.0, pose.Road!.Value.RollWeight, 12);

        // The height is the road's, at its reference line -- the camber puts the lane's own ground a few
        // centimetres lower -- and well inside the at-grade agreement.
        Assert.Equal(AtGradeProfile, pose.Z, 6);
        Assert.True(Math.Abs(pose.Road!.Value.DepartureFromGroundMetres) < PoseConverter.AtGradeDepartureMetres);
    }

    [Fact]
    public void TheRollIsShedSmoothlyOnTheApproachAndNoneIsLeftOnTheDeck()
    {
        GroundSurface ground = GroundSurface();
        RoadSurface roads = Crossing().Join();
        var converter = new PoseConverter(ground, roads: roads);
        var groundOnly = new PoseConverter(ground);

        double? previousRoll = null;
        double previousWeight = 1.0;
        double worstStep = 0.0;
        for (double x = -79.0; x <= 0.0; x += 0.25)
        {
            double along = x + 80.0;
            VehiclePose pose = converter.Convert("d", Point, x, 0.0, 90.0, Speed, "deck_0", along)!.Value;
            VehiclePose was = groundOnly.Convert("d", Point, x, 0.0, 90.0, Speed)!.Value;
            RoadSeat seat = pose.Road!.Value;

            // The roll is the ground's, scaled by the weight its departure gives, and the weight never
            // rises again on the way up.
            Assert.Equal(PoseConverter.RollWeight(seat.DepartureFromGroundMetres) * was.RollDegrees,
                         pose.RollDegrees, 9);
            Assert.True(seat.RollWeight <= previousWeight + 1e-12, $"the weight rose at x = {x}");
            if (along <= 20.0)
            {
                Assert.Equal(was.RollDegrees, pose.RollDegrees, 9);
            }

            if (seat.DepartureFromGroundMetres >= PoseConverter.OnStructureDepartureMetres)
            {
                Assert.Equal(0.0, pose.RollDegrees, 12);
            }

            if (previousRoll is { } before)
            {
                worstStep = Math.Max(worstStep, Math.Abs(pose.RollDegrees - before));
            }

            previousRoll = pose.RollDegrees;
            previousWeight = seat.RollWeight;
        }

        // Shed over the metres in which the ramp leaves the ground, not in a step: the ground's own roll
        // here is a little over two degrees, and no quarter-metre takes a fifth of a degree of it away.
        Assert.True(worstStep < 0.2, $"the roll changed by {worstStep} degrees in a quarter-metre");
        Assert.Equal(0.0, previousWeight, 12);
    }

    [Fact]
    public void TheRollWeightIsOneAtGradeZeroOnAStructureAndSmoothBetween()
    {
        Assert.Equal(1.0, PoseConverter.RollWeight(0.0));
        Assert.Equal(1.0, PoseConverter.RollWeight(PoseConverter.AtGradeDepartureMetres));
        Assert.Equal(1.0, PoseConverter.RollWeight(-PoseConverter.AtGradeDepartureMetres));
        Assert.Equal(0.0, PoseConverter.RollWeight(PoseConverter.OnStructureDepartureMetres));
        Assert.Equal(0.0, PoseConverter.RollWeight(-6.0));
        Assert.Equal(0.5, PoseConverter.RollWeight(
            0.5 * (PoseConverter.AtGradeDepartureMetres + PoseConverter.OnStructureDepartureMetres)), 12);

        // Continuous everywhere, and flat at both ends of the blend.
        double previous = 1.0;
        for (double departure = 0.0; departure <= 2.0; departure += 0.001)
        {
            double weight = PoseConverter.RollWeight(departure);
            Assert.True(Math.Abs(weight - previous) < 0.0025, $"a step at {departure} m");
            previous = weight;
        }
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
        GroundSurface ground = SyntheticWorld.Build(_ => 0.0);
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
