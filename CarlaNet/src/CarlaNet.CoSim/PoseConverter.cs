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
/// <para><b>The height and pitch come from the road the vehicle is on.</b> The SUMO network is flat,
/// so neither comes from SUMO. They come from the OpenDRIVE profile of the vehicle's road at the s its
/// origin projects to (<see cref="RoadSurface"/>): the height is the profile there, which is the height
/// of every lane of the road at that s, and the pitch is the profile's slope signed for the way the
/// heading runs along the road. The draped ground surface cannot stand in for it: it holds one height
/// per cell and, under every bridge deck, holds the ground beneath the deck, so a body seated on it
/// sits under a deck it should be driving on and rides humps over a road that spans the ground.</para>
///
/// <para><b>The roll comes from the ground where the road is at grade, and is zero on a
/// structure.</b> The engine builds a road flat across its width, so a body on a deck or on a road
/// spanning the ground has no cross-slope; but at grade the photoreal road has the crown and camber of
/// the real one, which the ground surface follows and which shows from any altitude. So the roll is the
/// ground surface's, scaled by a weight that is one while the road and the ground agree to within the
/// measured at-grade disagreement and falls smoothly to zero as the road departs from the ground onto a
/// structure (<see cref="RollWeight"/>).</para>
///
/// <para><b>Off every road, the ground decides all three</b>, exactly as it did before the road was
/// read: a vehicle SUMO parks off its lane, or one on an edge with no OpenDRIVE road, is seated on the
/// ground surface's height and tilted by its two gradients, and the pose says why
/// (<see cref="VehiclePose.GroundReason"/>). A vehicle outside the ground grid has no pose at all, on a
/// road or off one, as before. The two tilt signs are CARLA's own, taken from the code that turns a
/// rotation into axes rather than chosen: see <see cref="Tilt"/>.</para>
///
/// <para><b>The velocity.</b> SUMO's speed along the lane, pointed along the yaw, with a vertical
/// component of that speed times the slope along the heading the pitch was taken from -- so the
/// velocity is tangent to the path the body is seated on. SUMO's network is flat, so its speed is the
/// horizontal speed, and the horizontal speed a truth record derives from this velocity is SUMO's
/// own.</para>
/// </remarks>
public sealed class PoseConverter
{
    /// <summary>
    /// How far the road may depart from the ground under the body, in metres, and still be at grade: the
    /// body takes the ground's roll in full up to here.
    /// </summary>
    /// <remarks>
    /// The profile is the road's height at its reference line, the carriageway's left edge, and the
    /// ground under a lane several metres across a cambered surface stands a little higher or lower:
    /// measured on the shipped Arapahoe package, an at-grade road agrees with the ground at its reference
    /// line to a few centimetres, and East Arapahoe Road's outer lane, 11.7 m across from it, stands
    /// 0.36 m above the ground under it. Half a metre is
    /// above that and is the floor below which the live check against the photoreal could measure nothing,
    /// so every body on an at-grade carriageway is rolled exactly as the ground rolls it.
    /// </remarks>
    public const double AtGradeDepartureMetres = 0.5;

    /// <summary>
    /// How far the road departs from the ground, in metres, once it is certainly on a structure: the body
    /// takes none of the ground's roll from here.
    /// </summary>
    /// <remarks>
    /// <para>The smallest lift the world build counts as a deck rather than survey noise
    /// (<c>GradeSeparationOptions.MinStructureMeters</c>). Between this and
    /// <see cref="AtGradeDepartureMetres"/> the weight falls on a smoothstep, so a body climbing an
    /// approach ramp sheds the ground's cross-slope gradually -- over the metres in which the ramp leaves
    /// the ground by this last metre -- and never in a step.</para>
    ///
    /// <para>The same weight takes the roll off a body wherever else the ground under it is not its road:
    /// a car or a tree the photogrammetry reconstructed into the surface, or the side slope of an
    /// embankment where the mapped lane runs a few metres off the real carriageway. There the ground's
    /// cross-slope was never the road's.</para>
    /// </remarks>
    public const double OnStructureDepartureMetres = 1.5;

    private readonly GroundSurface _ground;
    private readonly IReadOnlyDictionary<string, double> _measuredSeatHeights;
    private readonly RoadSurface? _roads;

    /// <param name="ground">
    /// The world's draped surface, which owns the roll at grade and everything off the road network.
    /// </param>
    /// <param name="measuredSeatHeights">
    /// Height of the actor origin above the contact surface per blueprint, where it has been
    /// measured by settling the body on level ground. A blueprint absent from it falls back to the
    /// measured bounding box, and the pose says it did.
    /// </param>
    /// <param name="roads">
    /// The world's OpenDRIVE roads joined to its SUMO network, which own the height and the pitch of a
    /// body on a road. Without them every body is seated on the ground surface.
    /// </param>
    public PoseConverter(GroundSurface ground,
                         IReadOnlyDictionary<string, double>? measuredSeatHeights = null,
                         RoadSurface? roads = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        _ground = ground;
        _measuredSeatHeights = measuredSeatHeights ?? new Dictionary<string, double>();
        _roads = roads;
    }

    /// <summary>
    /// How much of the ground surface's roll a body takes, given how far its road departs from the
    /// ground under it: one at grade, zero on a structure, a smoothstep between.
    /// </summary>
    public static double RollWeight(double departureFromGroundMetres)
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
    public VehiclePose? Convert(string vehicleId,
                                in VehicleExtent extent,
                                double sumoX,
                                double sumoY,
                                double sumoAngleDegrees,
                                double speedMetresPerSecond,
                                string laneId,
                                double lanePositionMetres)
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

        (double pitch, double roll, double slope) = Tilt(originX, originY, forwardX, forwardY);
        double height = surface - _ground.OriginHeightMetres;

        // The road the vehicle is on, where it is on one: its profile's height and slope replace the
        // ground's, and the ground's roll is kept only as far as the road is at grade. The road works in
        // SUMO's frame, which is the CARLA frame with its northing negated.
        RoadSeat? onRoad = null;
        GroundSeatReason reason = laneId.Length == 0 ? GroundSeatReason.NoLane : GroundSeatReason.NoRoad;
        if (_roads is { } roads
            && roads.TrySeat(laneId, lanePositionMetres, originX, -originY, forwardX, -forwardY,
                             alongHeading, out RoadSeat road, out reason))
        {
            double departure = road.SurfaceZMetres - height;
            double weight = RollWeight(departure);
            slope = road.SlopeAlongHeading;
            pitch = Math.Atan(slope) * (180.0 / Math.PI);
            roll *= weight;
            height = road.SurfaceZMetres;
            onRoad = road with { DepartureFromGroundMetres = departure, RollWeight = weight };
        }

        return new VehiclePose(
            vehicleId,
            extent.BlueprintId,
            originX,
            originY,
            height + seat,
            yaw,
            pitch,
            roll,
            speedMetresPerSecond * forwardX,
            speedMetresPerSecond * forwardY,
            speedMetresPerSecond * slope,
            approximated)
        {
            Road = onRoad,
            GroundReason = onRoad is null ? reason : GroundSeatReason.None,
        };
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
    /// <para>On a road, the road's slope along the heading replaces this gradient for the pitch and
    /// the vertical velocity alike, under the same sign -- positive on a climb -- and the roll computed
    /// here is scaled by <see cref="RollWeight"/>.</para>
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
