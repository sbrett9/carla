namespace CarlaNet.CoSim;

/// <summary>
/// A vehicle's state at an instant between two SUMO frames, in SUMO's own frame and units, so it
/// converts to a pose exactly as a reported state does.
/// </summary>
/// <param name="X">
/// Projected easting of the front-bumper centre, metres: on the lane's centre line, or as far across
/// it as SUMO has the vehicle part-way through a lane change spread over time.
/// </param>
/// <param name="Y">Projected northing of the same point, metres.</param>
/// <param name="HeadingDegrees">
/// Degrees clockwise from north: the tangent of the lane under the bumper. It steps at every corner of
/// the lane's polyline and does not turn through a lane change, so the session does not turn a body by
/// it; a body's heading comes from the path its bumper takes (<see cref="PathHeading"/>).
/// </param>
/// <param name="SpeedMetresPerSecond">Speed, interpolated linearly so it ramps as SUMO's did.</param>
/// <param name="Case">Which of the cases produced it.</param>
/// <param name="LaneId">
/// The lane the front bumper is on at this instant: the lane of the route the interpolation walked
/// that the point was evaluated on, which through a junction is the connector rather than either
/// reported lane. Through a lane change -- inside an edge, or onto the lane beside the one a connector
/// feeds -- it is whichever of the two lanes the sideways blend is nearer, and the two run alongside
/// each other on one road. Empty where the vehicle is on no lane: parked off it, or pulling into or out
/// of the stop.
/// </param>
/// <param name="LanePositionMetres">
/// How far along <paramref name="LaneId"/> the front bumper is, against the lane's declared length as
/// TraCI reports a lane position.
/// </param>
/// <remarks>
/// The lane is carried because the pose's height comes from the road the vehicle is on, and SUMO's
/// position alone does not say which road that is where two cross at different levels: the deck and
/// the road beneath it share their plan position.
/// </remarks>
public readonly record struct InterpolatedState(
    double X,
    double Y,
    double HeadingDegrees,
    double SpeedMetresPerSecond,
    LaneInterpolationCase Case,
    string LaneId = "",
    double LanePositionMetres = 0.0);
