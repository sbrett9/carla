namespace CarlaNet.CoSim;

/// <summary>
/// The CARLA transform and velocity a SUMO vehicle's state converts to: what the bridge would
/// apply.
/// </summary>
/// <param name="VehicleId">SUMO's vehicle id.</param>
/// <param name="BlueprintId">The measured body the pose was computed for.</param>
/// <param name="X">CARLA-local easting, metres.</param>
/// <param name="Y">CARLA-local northing negated, metres.</param>
/// <param name="Z">CARLA-local height, metres above the georeference origin.</param>
/// <param name="YawDegrees">CARLA yaw, normalised to the half-open turn ending at 180.</param>
/// <param name="PitchDegrees">Nose-up angle from the ground surface's along-heading gradient.</param>
/// <param name="RollDegrees">Right-down angle from the surface's across-heading gradient.</param>
/// <param name="VelocityX">
/// CARLA-frame velocity, metres per second: SUMO's speed along the lane times the forward vector of
/// the yaw.
/// </param>
/// <param name="VelocityY">CARLA-frame velocity, metres per second, as <paramref name="VelocityX"/>.</param>
/// <param name="VelocityZ">
/// CARLA-frame vertical velocity, metres per second: the same speed times the ground surface's
/// along-heading gradient, the slope the pitch comes from. SUMO's network is flat, so its speed is the
/// horizontal speed, and this is what makes the velocity tangent to the draped path the body is
/// seated on rather than level beneath it.
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
    bool SeatHeightWasApproximated);
