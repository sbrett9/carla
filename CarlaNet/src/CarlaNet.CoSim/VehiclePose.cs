namespace CarlaNet.CoSim;

/// <summary>
/// The CARLA transform and velocity a SUMO vehicle's state converts to: what the bridge would
/// apply.
/// </summary>
/// <param name="VehicleId">SUMO's vehicle id.</param>
/// <param name="BlueprintId">The measured body the pose was computed for.</param>
/// <param name="X">CARLA-local easting, metres.</param>
/// <param name="Y">CARLA-local northing negated, metres.</param>
/// <param name="Z">
/// CARLA-local height, metres above the georeference origin: the surface under the body's origin plus
/// its seat height. The surface is the draped ground surface off every road and on a road at grade, the
/// profile of the OpenDRIVE road the body is on where that road is a structure, and a blend of the two
/// between (<see cref="Road"/>, <see cref="RoadSeat.GroundWeight"/>).
/// </param>
/// <param name="YawDegrees">CARLA yaw, normalised to the half-open turn ending at 180.</param>
/// <param name="PitchDegrees">
/// Nose-up angle from the same seat's slope along the heading: the ground surface's gradient at grade
/// and off the road, the road profile's on a structure, the blended seat's own between.
/// </param>
/// <param name="RollDegrees">
/// Right-down angle from the ground surface's across-heading gradient: in full off a road and on a road
/// at grade, not at all on a structure, scaled by the weight between.
/// </param>
/// <param name="VelocityX">
/// CARLA-frame velocity, metres per second: SUMO's speed along the lane times the forward vector of
/// the yaw.
/// </param>
/// <param name="VelocityY">CARLA-frame velocity, metres per second, as <paramref name="VelocityX"/>.</param>
/// <param name="VelocityZ">
/// CARLA-frame vertical velocity, metres per second: the same speed times the slope along the heading
/// the pitch comes from. SUMO's network is flat, so its speed is the horizontal speed, and this is what
/// makes the velocity tangent to the path the body is seated on rather than level beneath it.
/// </param>
/// <param name="SeatHeightWasApproximated">
/// Whether the height came from the measured bounding box rather than from a settling measurement.
/// Carried on the pose rather than assumed, so a run can say how many of its poses rest on an
/// approximation.
/// </param>
public readonly record struct VehiclePose(
    string VehicleId,
    string BlueprintId,
    double X,
    double Y,
    double Z,
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees,
    double VelocityX,
    double VelocityY,
    double VelocityZ,
    bool SeatHeightWasApproximated)
{
    /// <summary>
    /// The road the body was on, how far it departs from the ground at its reference line, and how much
    /// of the seat the ground gave; <see langword="null"/> where the body was on no road.
    /// </summary>
    public RoadSeat? Road { get; init; }

    /// <summary>
    /// Why the body was on no road; <see cref="GroundSeatReason.None"/> where it was on one.
    /// </summary>
    public GroundSeatReason GroundReason { get; init; }
}
