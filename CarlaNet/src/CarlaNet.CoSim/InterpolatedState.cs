namespace CarlaNet.CoSim;

/// <summary>
/// A vehicle's state at an instant between two SUMO frames, in SUMO's own frame and units, so it
/// converts to a pose exactly as a reported state does.
/// </summary>
/// <param name="X">Projected easting of the front-bumper centre, metres.</param>
/// <param name="Y">Projected northing of the same point, metres.</param>
/// <param name="HeadingDegrees">
/// Degrees clockwise from north, taken from the tangent of the shape the vehicle is on rather than
/// by blending the two reported angles -- a tangent is already right through a turn and a blended
/// angle is not.
/// </param>
/// <param name="SpeedMetresPerSecond">Speed, interpolated linearly so it ramps as SUMO's did.</param>
/// <param name="Case">Which of the four cases produced it.</param>
public readonly record struct InterpolatedState(
    double X,
    double Y,
    double HeadingDegrees,
    double SpeedMetresPerSecond,
    LaneInterpolationCase Case);
