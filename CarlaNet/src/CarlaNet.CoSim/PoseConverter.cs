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
/// <para><b>The height, pitch and roll.</b> The SUMO network is flat, so none of the three comes
/// from SUMO. They come from the world's draped ground surface, sampled in process with no round
/// trip: one sample for the height and two pairs either side of the vehicle for the two gradients.
/// A vehicle outside the grid has no height, and no pose is produced for it. The two tilt signs are
/// CARLA's own, taken from the code that turns a rotation into axes rather than chosen: see
/// <see cref="Tilt"/>.</para>
/// </remarks>
public sealed class PoseConverter
{
    private readonly GroundSurface _ground;
    private readonly IReadOnlyDictionary<string, double> _measuredSeatHeights;

    /// <param name="ground">The world's draped surface, which owns the height and the tilt.</param>
    /// <param name="measuredSeatHeights">
    /// Height of the actor origin above the contact surface per blueprint, where it has been
    /// measured by settling the body on level ground. A blueprint absent from it falls back to the
    /// measured bounding box, and the pose says it did.
    /// </param>
    public PoseConverter(GroundSurface ground,
                         IReadOnlyDictionary<string, double>? measuredSeatHeights = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        _ground = ground;
        _measuredSeatHeights = measuredSeatHeights ?? new Dictionary<string, double>();
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
    /// The pose the bridge would apply for one vehicle, or <see langword="null"/> where the vehicle
    /// stands on ground the world has no surface for.
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
                                double speedMetresPerSecond)
    {
        ArgumentNullException.ThrowIfNull(vehicleId);

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

        (double pitch, double roll) = Tilt(originX, originY, forwardX, forwardY);

        return new VehiclePose(
            vehicleId,
            extent.BlueprintId,
            originX,
            originY,
            surface - _ground.OriginHeightMetres + seat,
            yaw,
            pitch,
            roll,
            speedMetresPerSecond * forwardX,
            speedMetresPerSecond * forwardY,
            approximated);
    }

    /// <summary>
    /// The pose a SUMO frame converts to, with the frame's own position, heading and speed.
    /// </summary>
    public VehiclePose? Convert(in CoSimVehicleFrame frame, in VehicleExtent extent) =>
        Convert(frame.Id, extent, frame.X, frame.Y, frame.HeadingDegrees,
                frame.SpeedMetresPerSecond);

    /// <summary>
    /// Nose-up and right-down angles that lay the body's own axes in the ground surface's tangent
    /// plane.
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
    /// </remarks>
    private (double Pitch, double Roll) Tilt(double x, double y, double forwardX, double forwardY)
    {
        double step = _ground.CellSizeMetres;
        double rightX = -forwardY;
        double rightY = forwardX;

        double along = Gradient(x, y, forwardX, forwardY, step) ?? 0.0;
        double across = Gradient(x, y, rightX, rightY, step) ?? 0.0;

        double pitch = Math.Atan(along) * (180.0 / Math.PI);
        double roll = -Math.Asin(across / Math.Sqrt(1.0 + (along * along) + (across * across)))
                      * (180.0 / Math.PI);
        return (pitch, roll);
    }

    private double? Gradient(double x, double y, double dirX, double dirY, double step)
    {
        double? ahead = _ground.Sample(x + (step * dirX), y + (step * dirY));
        double? behind = _ground.Sample(x - (step * dirX), y - (step * dirY));
        return ahead is { } a && behind is { } b ? (a - b) / (2.0 * step) : null;
    }
}
