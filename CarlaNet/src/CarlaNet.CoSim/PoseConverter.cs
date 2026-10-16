namespace CarlaNet.CoSim;

/// <summary>
/// Turns a SUMO vehicle's state into the CARLA transform and velocity that would put the rendered
/// body exactly where SUMO says the vehicle is.
/// </summary>
/// <remarks>
/// <para>Four conversions, and every one of them has a way of being wrong that looks right.</para>
///
/// <para><b>The frame.</b> CARLA's local frame is east, negated north, up; SUMO's projected frame
/// from the world's own transverse-Mercator string is east, north. With the world built at the same
/// pinned origin and with normalisation disabled, the conversion is a sign on the northing and no
/// offset at all -- which is worth asserting rather than assuming, because a network built at a
/// different origin converts to a plausible position several hundred metres away.</para>
///
/// <para><b>The yaw.</b> SUMO's angle is degrees clockwise from north. The CARLA yaw that produces
/// the same course is that angle less ninety, derived rather than asserted: the shipped truth path
/// computes a course from a CARLA yaw as <c>atan2(cos yaw, -sin yaw)</c>, so the forward vector is
/// <c>(cos yaw, sin yaw)</c> and north is negative y. Requiring the course to equal SUMO's angle
/// gives <c>sin yaw = -cos angle</c> and <c>cos yaw = sin angle</c>.</para>
///
/// <para><b>The reference point.</b> SUMO reports the centre of the front bumper; CARLA places the
/// actor at its origin, which is not the centre of its body. The shift back along the vehicle's own
/// heading is the measured distance from bumper centre to origin, and the sideways shift is the
/// measured lateral offset of the box centre. Both come from the catalogue and neither is half the
/// vType's declared length.</para>
///
/// <para><b>The height, pitch and roll.</b> The SUMO network is flat, so none of the three comes from
/// SUMO. Two surfaces offer them, and each is right where the other is wrong. The draped ground surface
/// is the photoreal road a camera sees, across the whole width of the road, camber and crown included;
/// but it holds one height per cell, and under every bridge deck it holds the ground beneath the deck, so
/// a body seated on it alone sits under a deck it should be driving on and rides humps over a road that
/// spans the ground. The OpenDRIVE profile of the vehicle's road (<see cref="RoadSurface"/>) knows both
/// levels of a crossing; but it is the road's height at its reference line, the carriageway's left edge,
/// built flat across, so on a cambered road its outer lanes stand above the road a camera sees --
/// measured at grade on Arapahoe, by a median 0.35 m at 13.5 m and more across, where the ground
/// surface agrees with the photoreal to 0.02 m.</para>
///
/// <para><b>So the body takes the ground where its road is at grade and the road where it is on a
/// structure</b>, by one weight, <see cref="GroundWeight"/>, of how far the road's profile departs from
/// the ground at its reference line at the body's s -- where the profile is defined, so a lane's
/// distance across a cambered road never moves it. At grade (weight one) the seat is exactly the
/// ground's: its height, the pitch and roll of its two gradients, and the climb of the pitch's slope. On
/// a structure (weight zero) the height and pitch are the profile's, signed for the way the heading runs
/// along the road, and there is no roll, the engine's road being flat across. Between, each is blended
/// by the weight: the height <c>w·z_ground + (1 − w)·z_road</c>, the roll <c>w·roll_ground</c>, and the
/// pitch and the climb from the blended seat's own slope along the heading -- the two slopes blended,
/// and the change of the weight itself, so a body is tangent to the path it actually rides out of the
/// ground and onto the deck.</para>
///
/// <para><b>The weight is built once per road and never changes faster than the road allows.</b> It is
/// tabulated every quarter-metre along each road when the converter is made, and held so that it falls
/// from one to zero over no less than <see cref="WeightChangeMetres"/>, on a smoothstep: where the ground
/// at a reference line drops off at a deck's footprint, the transition is spread back over the at-grade
/// side instead of happening within a cell. A junction connector keeps its own weight but is corrected
/// at each end to meet the carriageway it joins there, since netconvert draws a connector's reference
/// line along the left edge of the connection it came from, which need not lie where the road it joins
/// draws its own -- on Arapahoe a lane width or more apart at one joint in ten, and as far as 20 m; the
/// connector section a merge absorbed is weighed across from the sections either side
/// (<see cref="CarriedWeight"/>). Where a reference line runs off the ground grid, the ground is read at
/// the grid's nearest point.</para>
///
/// <para><b>Off every road, the ground decides all three</b>, exactly as it did before the road was
/// read: a vehicle SUMO parks off its lane, or one on an edge with no OpenDRIVE road, is seated on the
/// ground surface's height and tilted by its two gradients, and the pose says why
/// (<see cref="VehiclePose.GroundReason"/>). A vehicle outside the ground grid has no pose at all, on a
/// road or off one, as before. The two tilt signs are CARLA's own, taken from the code that turns a
/// rotation into axes rather than chosen: see <see cref="Tilt"/>.</para>
///
/// <para><b>The velocity.</b> The path's where the session gives one: the bumper's own movement since
/// the body's last pose (<see cref="PathHeading"/>), lateral movement included, so a truth record's
/// course and speed are the motion the imagery shows. Otherwise -- a body's first pose, a step across a
/// jump -- SUMO's speed along the lane, pointed along the yaw. Either way the vertical component is the
/// horizontal speed times the slope along the heading the pitch was taken from, so the body climbs as it
/// is pitched.</para>
///
/// <para><b>The yaw is given, not derived.</b> The heading a body takes is the caller's: the session's
/// is the heading of the body's own path, with SUMO's reported angle recorded beside it. Only the
/// rotation about the bumper depends on it; the bumper is where the caller put it.</para>
/// </remarks>
public sealed class PoseConverter
{
    /// <summary>
    /// How far the road's profile may depart from the ground at its reference line, in metres, and the
    /// road still be at grade there: the body's seat is the ground's exactly up to here.
    /// </summary>
    /// <remarks>
    /// The profile was fitted to the ground along the reference line, so where the road is at grade the
    /// two agree there to the fit: measured every 2 m along the reference lines of the shipped packages'
    /// at-grade roads, within 0.5 m at 99.3 % of the points on Arapahoe and 99.0 % on Bahonar (median
    /// 0.013 m and 0.005 m, 99th percentile 0.42 m and 0.51 m). Half a metre is also the floor below which
    /// the live check against the photoreal could measure nothing.
    /// </remarks>
    public const double AtGradeDepartureMetres = 0.5;

    /// <summary>
    /// How far the road's profile departs from the ground at its reference line, in metres, once the road
    /// is certainly on a structure: the body's seat is the road's from here, with no roll.
    /// </summary>
    /// <remarks>
    /// The smallest lift the world build counts as a deck rather than survey noise
    /// (<c>GradeSeparationOptions.MinStructureMeters</c>). Between this and
    /// <see cref="AtGradeDepartureMetres"/> the weight falls on a smoothstep, so a body climbing an
    /// approach ramp moves from the ground onto the deck's profile, and sheds the ground's cross-slope,
    /// over the metres in which the ramp leaves the ground by this last metre, and never in a step.
    /// </remarks>
    public const double OnStructureDepartureMetres = 1.5;

    /// <summary>
    /// The shortest stretch of road over which a body's ground weight may fall from one to zero, metres.
    /// </summary>
    /// <remarks>
    /// <para>The departure can change far faster along a road than any ramp climbs: where the reference
    /// line crosses the edge of a deck's footprint, under which the ground grid is anchored to bare earth,
    /// the ground there drops by a metre within a cell. Weighed by the departure alone, a body there moved
    /// from the ground onto the profile within a metre or two -- its height continuous, but its pitch
    /// turning by up to 57 degrees in one tick (measured on Arapahoe's Yosemite Street deck, road
    /// 2086).</para>
    ///
    /// <para>So the weight at s is the departure's, but never more than the departure's weight anywhere
    /// within this distance plus a smoothstep of how far away that is. Where a structure begins the weight
    /// falls on that smoothstep over the at-grade side, where the two surfaces stand less than half a metre
    /// apart, and reaches zero with no slope, so the body meets the deck's profile without a kink; on
    /// the structure itself it stays zero. A gentle ramp is moved by no more than a few hundredths.</para>
    /// </remarks>
    public const double WeightChangeMetres = 10.0;

    /// <summary>How far apart along a road its weight is tabulated, metres.</summary>
    private const double WeightSampleMetres = 0.25;

    private readonly GroundSurface _ground;
    private readonly double _gridMinX;
    private readonly double _gridMinY;
    private readonly double _gridMaxX;
    private readonly double _gridMaxY;
    private readonly IReadOnlyDictionary<string, double> _measuredSeatHeights;
    private readonly RoadSurface? _roads;
    private readonly Dictionary<uint, double[]> _weights = [];

    /// <param name="ground">
    /// The world's draped surface, which owns the seat of a body on an at-grade road and of everything off
    /// the road network.
    /// </param>
    /// <param name="measuredSeatHeights">
    /// Height of the actor origin above the contact surface per blueprint, where it has been
    /// measured by settling the body on level ground. A blueprint absent from it falls back to the
    /// measured bounding box, and the pose says it did.
    /// </param>
    /// <param name="roads">
    /// The world's OpenDRIVE roads joined to its SUMO network, which own the height and pitch of a body on
    /// a structure and say how far each road departs from the ground. Without them every body is seated
    /// on the ground surface.
    /// </param>
    public PoseConverter(GroundSurface ground,
                         IReadOnlyDictionary<string, double>? measuredSeatHeights = null,
                         RoadSurface? roads = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        _ground = ground;
        // The grid's extent drawn in by a micrometre, so a point clamped onto it is never read as off it.
        (double minX, double minY, double maxX, double maxY) = ground.Extent;
        (_gridMinX, _gridMinY, _gridMaxX, _gridMaxY) = (minX + 1e-6, minY + 1e-6, maxX - 1e-6, maxY - 1e-6);
        _measuredSeatHeights = measuredSeatHeights ?? new Dictionary<string, double>();
        _roads = roads;

        // The carriageways first: a junction connector's weight is made to meet theirs at its ends.
        foreach (RoadProfile road in (roads?.Roads.Values ?? []).OrderBy(road => road.IsJunction))
        {
            _weights[road.Id] = WeighAlong(road);
        }
    }

    /// <summary>
    /// How much of its seat a body on a road takes from the ground surface, given how far the road's
    /// profile departs from the ground at its reference line: one at grade, zero on a structure, a
    /// smoothstep between.
    /// </summary>
    public static double GroundWeight(double departureFromGroundMetres)
    {
        double magnitude = Math.Abs(departureFromGroundMetres);
        if (magnitude <= AtGradeDepartureMetres)
        {
            return 1.0;
        }

        if (magnitude >= OnStructureDepartureMetres || double.IsNaN(magnitude))
        {
            return 0.0;
        }

        double u = (magnitude - AtGradeDepartureMetres) / (OnStructureDepartureMetres - AtGradeDepartureMetres);
        return 1.0 - (u * u * (3.0 - (2.0 * u)));
    }

    /// <summary>
    /// The CARLA yaw that gives a body SUMO's heading, normalised to the half-open turn ending at
    /// 180 degrees.
    /// </summary>
    public static double YawFromSumoAngle(double sumoAngleDegrees)
    {
        double yaw = Math.IEEERemainder(sumoAngleDegrees - 90.0, 360.0);
        return yaw <= -180.0 ? yaw + 360.0 : yaw;
    }

    /// <summary>
    /// The pose the bridge would apply for a vehicle whose lane is not known, seated on the ground
    /// surface, or <see langword="null"/> where it stands on ground the world has no surface for.
    /// </summary>
    /// <param name="vehicleId">SUMO's vehicle id, carried onto the pose.</param>
    /// <param name="extent">The measured body, from the catalogue.</param>
    /// <param name="sumoX">SUMO easting of the front-bumper centre, metres.</param>
    /// <param name="sumoY">SUMO northing of the front-bumper centre, metres.</param>
    /// <param name="sumoAngleDegrees">SUMO heading, degrees clockwise from north.</param>
    /// <param name="speedMetresPerSecond">SUMO speed along the lane.</param>
    public VehiclePose? Convert(string vehicleId,
                                in VehicleExtent extent,
                                double sumoX,
                                double sumoY,
                                double sumoAngleDegrees,
                                double speedMetresPerSecond) =>
        Convert(vehicleId, extent, sumoX, sumoY, sumoAngleDegrees, speedMetresPerSecond,
                string.Empty, 0.0);

    /// <summary>
    /// The pose the bridge would apply for one vehicle on a lane, or <see langword="null"/> where the
    /// vehicle stands on ground the world has no surface for.
    /// </summary>
    /// <param name="vehicleId">SUMO's vehicle id, carried onto the pose.</param>
    /// <param name="extent">The measured body, from the catalogue.</param>
    /// <param name="sumoX">SUMO easting of the front-bumper centre, metres.</param>
    /// <param name="sumoY">SUMO northing of the front-bumper centre, metres.</param>
    /// <param name="sumoAngleDegrees">SUMO heading, degrees clockwise from north.</param>
    /// <param name="speedMetresPerSecond">SUMO speed along the lane.</param>
    /// <param name="laneId">
    /// The lane the front bumper is on, which names the road the body is seated on; empty where SUMO
    /// reports none, which seats it on the ground.
    /// </param>
    /// <param name="lanePositionMetres">How far along that lane the front bumper is.</param>
    /// <param name="pathVelocity">
    /// The body's horizontal velocity from its path, in SUMO's frame (east, north), metres per second;
    /// null to point SUMO's speed along the heading.
    /// </param>
    public VehiclePose? Convert(string vehicleId,
                                in VehicleExtent extent,
                                double sumoX,
                                double sumoY,
                                double sumoAngleDegrees,
                                double speedMetresPerSecond,
                                string laneId,
                                double lanePositionMetres,
                                (double X, double Y)? pathVelocity = null)
    {
        ArgumentNullException.ThrowIfNull(vehicleId);
        ArgumentNullException.ThrowIfNull(laneId);

        double yaw = YawFromSumoAngle(sumoAngleDegrees);
        double radians = yaw * (Math.PI / 180.0);
        double forwardX = Math.Cos(radians);
        double forwardY = Math.Sin(radians);

        // The bumper's position in the CARLA frame, then the shift back to the actor origin: the
        // measured bumper-to-origin distance along the heading and the measured lateral offset
        // across it, both rotated by the yaw.
        double bumperX = sumoX;
        double bumperY = -sumoY;
        double alongHeading = extent.BumperToOriginMetres;
        double across = extent.LateralOffsetMetres;
        double originX = bumperX - ((alongHeading * forwardX) - (across * forwardY));
        double originY = bumperY - ((alongHeading * forwardY) + (across * forwardX));

        if (_ground.Sample(originX, originY) is not { } surface)
        {
            return null;
        }

        bool approximated = !_measuredSeatHeights.TryGetValue(extent.BlueprintId, out double seat);
        if (approximated)
        {
            seat = extent.ApproximateSeatHeightMetres;
        }

        // The ground's seat, exactly as the bridge has always computed it: the height under the origin and
        // the two gradients. It is the whole seat off the road network and on an at-grade road.
        (double pitch, double roll, double slope) = Tilt(originX, originY, forwardX, forwardY);
        double height = surface - _ground.OriginHeightMetres;

        // The road the vehicle is on, where it is on one. The road works in SUMO's frame, which is the
        // CARLA frame with its northing negated.
        RoadSeat? onRoad = null;
        GroundSeatReason reason = laneId.Length == 0 ? GroundSeatReason.NoLane : GroundSeatReason.NoRoad;
        if (_roads is { } roads
            && roads.TrySeat(laneId, lanePositionMetres, originX, -originY, forwardX, -forwardY,
                             alongHeading, out RoadPlace road, out reason))
        {
            (double weight, double weightPerS, double departure) = WeightAt(road.Road, road.S);
            if (weight < 1.0)
            {
                double roadHeight = road.SurfaceZMetres;
                double roadSlope = road.SlopeAlongHeading;
                if (weight <= 0.0)
                {
                    height = roadHeight;
                    slope = roadSlope;
                }
                else
                {
                    // The seat's own slope: the two slopes blended, and the weight's change carrying the
                    // seat from one surface towards the other as the road leaves the ground.
                    double weightPerMetre = weightPerS * road.AlongPerMetre;
                    slope = (weight * slope) + ((1.0 - weight) * roadSlope)
                            + ((height - roadHeight) * weightPerMetre);
                    height = (weight * height) + ((1.0 - weight) * roadHeight);
                }

                pitch = Math.Atan(slope) * (180.0 / Math.PI);
                roll *= weight;
            }

            onRoad = new RoadSeat(road.RoadId, road.S, road.SurfaceZMetres, road.SlopeAlongHeading,
                                  departure, weight);
        }

        // The horizontal velocity: the path's, in the CARLA frame, or SUMO's speed along the yaw.
        (double velocityX, double velocityY) = pathVelocity is { } path
            ? (path.X, -path.Y)
            : (speedMetresPerSecond * forwardX, speedMetresPerSecond * forwardY);

        return new VehiclePose(
            vehicleId,
            extent.BlueprintId,
            originX,
            originY,
            height + seat,
            yaw,
            pitch,
            roll,
            velocityX,
            velocityY,
            Math.Sqrt((velocityX * velocityX) + (velocityY * velocityY)) * slope,
            approximated)
        {
            Road = onRoad,
            GroundReason = onRoad is null ? reason : GroundSeatReason.None,
        };
    }

    /// <summary>
    /// How much of its seat a body on a road at s takes from the ground, how fast that changes along +s,
    /// and the road's own departure from the ground at its reference line there.
    /// </summary>
    private (double Weight, double PerS, double Departure) WeightAt(RoadProfile road, double s)
    {
        (double weight, double perS) = TabulatedWeight(road, s);
        return (weight, perS, road.Elevation(s).Z - GroundAtReference(road, s));
    }

    /// <summary>
    /// A road's ground weight every quarter-metre along it, built once.
    /// </summary>
    /// <remarks>
    /// <para>First the weight of the road's departure from the ground at its reference line
    /// (<see cref="GroundWeight"/>). On the connector section a merge absorbed it is instead carried
    /// across, on a smoothstep, from the weight just before the section to the weight just after it (see
    /// <see cref="CarriedWeight"/>): round a collapsed dead end the reference line swings across the street
    /// and its departure measures the ground beside it.</para>
    ///
    /// <para>Then the whole is held to the weight at every sample within
    /// <see cref="WeightChangeMetres"/> plus a smoothstep of the distance to it.</para>
    ///
    /// <para>On a junction connector the connector's own weight is kept, and corrected at each end by
    /// however far it differs there from the weight of the carriageway it joins, the correction fading
    /// over the connector's length or <see cref="WeightChangeMetres"/>, whichever is shorter: a connector's
    /// reference line is the left edge of the connection it was drawn from and need not lie where the road
    /// it joins draws its own -- on Arapahoe a lane width or more apart at one joint in ten, and as far as
    /// 20 m -- so the two can read different ground at one joint. The correction takes up that difference
    /// and leaves what a long connector reads between its ends, so a connector beneath a deck stays on its
    /// profile. It is made before the weight is held to its neighbours, and again after, since holding it
    /// can lower an end.</para>
    /// </remarks>
    private double[] WeighAlong(RoadProfile road)
    {
        int segments = Math.Max(1, (int)Math.Ceiling(road.Length / WeightSampleMetres));
        double step = road.Length / segments;
        var own = new double[segments + 1];
        for (int index = 0; index <= segments; index++)
        {
            double s = index * step;
            own[index] = GroundWeight(road.Elevation(s).Z - GroundAtReference(road, s));
        }

        double OwnAt(double s)
        {
            double at = Math.Clamp(s, 0.0, road.Length) / step;
            int index = Math.Min((int)at, segments - 1);
            return own[index] + ((at - index) * (own[index + 1] - own[index]));
        }

        // A merge's absorbed connector: carried across from just before it to just after it.
        foreach (CarriedWeight carried in road.IsJunction ? [] : road.CarriedWeights)
        {
            double span = carried.ToS - carried.FromS;
            double before = OwnAt(carried.FromS);
            double after = OwnAt(carried.ToS);
            for (int index = 0; index <= segments; index++)
            {
                double s = index * step;
                if (span > 0.0 && s >= carried.FromS && s <= carried.ToS)
                {
                    double u = (s - carried.FromS) / span;
                    own[index] = before + ((after - before) * u * u * (3.0 - (2.0 * u)));
                }
            }
        }

        // A junction connector: its own weight, corrected to meet the carriageways' at its ends -- before
        // the weight is held to its neighbours, and again after, since holding it can lower an end.
        MeetTheCarriageways(road, own);
        double[] weights = HeldToTheirNeighbours(own, step);
        MeetTheCarriageways(road, weights);
        return weights;
    }

    /// <summary>
    /// Correct a junction connector's weights by however far they differ at each end from the weight of
    /// the carriageway it joins there, each correction fading on a smoothstep over the connector's length
    /// or <see cref="WeightChangeMetres"/>, whichever is shorter, so a long connector keeps its own weight
    /// between; nothing for any other road.
    /// </summary>
    private void MeetTheCarriageways(RoadProfile road, double[] weights)
    {
        int segments = weights.Length - 1;
        double step = road.Length / segments;
        double reach = Math.Min(road.Length, WeightChangeMetres);
        foreach (CarriedWeight carried in road.IsJunction ? road.CarriedWeights : [])
        {
            double atStart = weights[0];
            double atEnd = weights[segments];
            double before = ReferenceEquals(carried.Before, road) ? atStart : TabulatedWeight(carried.Before, carried.BeforeS).Weight;
            double after = ReferenceEquals(carried.After, road) ? atEnd : TabulatedWeight(carried.After, carried.AfterS).Weight;
            for (int index = 0; index <= segments; index++)
            {
                double fromStart = Math.Min(1.0, index * step / reach);
                double fromEnd = Math.Min(1.0, (segments - index) * step / reach);
                double fadeStart = 1.0 - (fromStart * fromStart * (3.0 - (2.0 * fromStart)));
                double fadeEnd = 1.0 - (fromEnd * fromEnd * (3.0 - (2.0 * fromEnd)));
                weights[index] = Math.Clamp(weights[index] + (fadeStart * (before - atStart)) + (fadeEnd * (after - atEnd)),
                                            0.0, 1.0);
            }
        }
    }

    /// <summary>
    /// Weights every <paramref name="step"/> metres, each held to every other within
    /// <see cref="WeightChangeMetres"/> plus a smoothstep of the distance between them.
    /// </summary>
    private static double[] HeldToTheirNeighbours(double[] own, double step)
    {
        int segments = own.Length - 1;
        int reach = (int)Math.Ceiling(WeightChangeMetres / step);
        var weights = new double[segments + 1];
        for (int index = 0; index <= segments; index++)
        {
            double least = own[index];
            for (int other = Math.Max(0, index - reach); other <= Math.Min(segments, index + reach) && least > 0.0; other++)
            {
                if (own[other] >= least)
                {
                    continue;
                }

                double u = Math.Abs(index - other) * step / WeightChangeMetres;
                if (u < 1.0)
                {
                    least = Math.Min(least, own[other] + (u * u * (3.0 - (2.0 * u))));
                }
            }

            weights[index] = least;
        }

        return weights;
    }

    /// <summary>
    /// A road's tabulated weight at s, linear between its samples and held at its ends beyond them, and its
    /// rate of change along +s.
    /// </summary>
    private (double Weight, double PerS) TabulatedWeight(RoadProfile road, double s)
    {
        double[] weights = _weights[road.Id];
        int segments = weights.Length - 1;
        double step = road.Length / segments;
        double at = Math.Clamp(s, 0.0, road.Length) / step;
        int index = Math.Min((int)at, segments - 1);
        double change = weights[index + 1] - weights[index];
        return (weights[index] + ((at - index) * change),
                s > 0.0 && s < road.Length ? change / step : 0.0);
    }

    /// <summary>
    /// The ground surface's height above the georeference origin at a road's reference line at s, read at
    /// the nearest point of the grid where the reference line runs off it -- the left edge of a road along
    /// the grid's own edge -- so the departure carries on continuously rather than changing what it
    /// measures.
    /// </summary>
    private double GroundAtReference(RoadProfile road, double s)
    {
        (double x, double y) = road.ReferencePoint(s);
        double carlaX = Math.Clamp(x, _gridMinX, _gridMaxX);
        double carlaY = Math.Clamp(-y, _gridMinY, _gridMaxY);
        return (_ground.Sample(carlaX, carlaY) ?? _ground.OriginHeightMetres) - _ground.OriginHeightMetres;
    }

    /// <summary>
    /// The pose a SUMO frame converts to, with the frame's own position, heading, speed and lane.
    /// </summary>
    public VehiclePose? Convert(in CoSimVehicleFrame frame, in VehicleExtent extent) =>
        Convert(frame.Id, extent, frame.X, frame.Y, frame.HeadingDegrees,
                frame.SpeedMetresPerSecond, frame.LaneId, frame.LanePositionMetres);

    /// <summary>
    /// Nose-up and right-down angles that lay the body's own axes in the ground surface's tangent
    /// plane, and the along-heading gradient the pitch was taken from.
    /// </summary>
    /// <remarks>
    /// <para>Central differences one grid cell either side, which is the finest step the surface
    /// actually resolves. A vehicle near the edge of the grid has a sample fall outside it; the
    /// gradient is then taken as zero rather than as a one-sided difference against nothing, because
    /// a tilt invented at the sandbox boundary would be indistinguishable from a measured one.</para>
    ///
    /// <para><b>The two signs are CARLA's own, derived from the code that turns a rotation into
    /// axes.</b> <c>Math::GetForwardVector</c> is
    /// <c>(cos yaw cos pitch, sin yaw cos pitch, sin pitch)</c> and <c>Math::GetRightVector</c>'s
    /// third component is <c>-cos pitch sin roll</c> (<c>LibCarla/source/carla/geom/Math.cpp</c>
    /// lines 117-136), and a <c>carla::geom::Rotation</c> reaches the engine as
    /// <c>FRotator{pitch, yaw, roll}</c> with no sign change (<c>Rotation.h:221</c>), so these are
    /// the engine's conventions and not a second set. Requiring the forward axis to rise with the
    /// surface gives a <b>positive</b> pitch on a climb, and requiring the right axis to rise where
    /// the surface rises to the right gives a <b>negative</b> roll, because the right axis's height
    /// is the <i>negative</i> sine of the roll.</para>
    ///
    /// <para>Both are the opposite of what this converter first carried, where each was written as
    /// an intention and marked unconfirmed. A vehicle on a climb was nosing down into the hill and
    /// a vehicle on a camber was leaning the wrong way, at twice the slope angle from where it
    /// belongs -- about eleven degrees of error on a one-in-ten grade, which is plain in an oblique
    /// frame and invisible in a residual, since the pose and the truth record agreed with each other
    /// throughout.</para>
    ///
    /// <para><b>And the roll is the exact seating rather than the small-angle one.</b> With
    /// <c>a = dg/df</c> and <c>b = dg/dr</c>, laying both horizontal axes in the tangent plane gives
    /// <c>pitch = atan(a)</c> and <c>roll = -asin(b / sqrt(1 + a^2 + b^2))</c>. Rolling by
    /// <c>atan(b)</c> instead -- the pair of independent gradients the runtime section writes -- is
    /// the same thing only where the pitch is zero, because the roll turns about an axis the pitch
    /// has already tilted. On a compound one-in-ten slope the difference is small, a hundredth of a
    /// degree, and the exact form costs one square root, so there is nothing to trade.</para>
    ///
    /// <para><b>The gradient is returned beside the angles</b> because the vertical velocity is the
    /// speed times the same number: one slope decides both how the body is tilted and how fast it
    /// climbs, so the two cannot disagree. Where the gradient is taken as zero at the edge of the
    /// grid, the body is level there and its vertical velocity is zero with it.</para>
    ///
    /// <para>On a road, this is the whole tilt at grade; on a structure the road's slope along the
    /// heading replaces this gradient for the pitch and the vertical velocity alike, under the same sign
    /// -- positive on a climb -- and the roll is none; between, both are blended by
    /// <see cref="GroundWeight"/>.</para>
    /// </remarks>
    private (double Pitch, double Roll, double AlongGradient) Tilt(double x, double y,
                                                                   double forwardX, double forwardY)
    {
        double step = _ground.CellSizeMetres;
        double rightX = -forwardY;
        double rightY = forwardX;

        double along = Gradient(x, y, forwardX, forwardY, step) ?? 0.0;
        double across = Gradient(x, y, rightX, rightY, step) ?? 0.0;

        double pitch = Math.Atan(along) * (180.0 / Math.PI);
        double roll = -Math.Asin(across / Math.Sqrt(1.0 + (along * along) + (across * across)))
                      * (180.0 / Math.PI);
        return (pitch, roll, along);
    }

    private double? Gradient(double x, double y, double dirX, double dirY, double step)
    {
        double? ahead = _ground.Sample(x + (step * dirX), y + (step * dirY));
        double? behind = _ground.Sample(x - (step * dirX), y - (step * dirY));
        return ahead is { } a && behind is { } b ? (a - b) / (2.0 * step) : null;
    }
}
