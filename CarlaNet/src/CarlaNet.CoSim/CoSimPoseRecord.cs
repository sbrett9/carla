namespace CarlaNet.CoSim;

/// <summary>
/// One pose the bridge would have applied, at one world tick, with nothing applied.
/// </summary>
/// <param name="TickIndex">Ticks since the session's first, counting from zero.</param>
/// <param name="SimulatedTimeSeconds">The rendered instant, which is what a frame would be stamped with.</param>
/// <param name="IsCaptureTick">Whether a recorder would have emitted a frame on this tick.</param>
/// <param name="Pose">The transform and velocity that would have gone into the batch.</param>
/// <param name="Case">How the sub-step pose was produced.</param>
/// <param name="SumoX">Where SUMO said the front bumper was, for a reader checking the conversion.</param>
/// <param name="SumoY">The same, northing.</param>
/// <param name="SumoHeadingDegrees">And the heading SUMO reported, clockwise from north.</param>
public readonly record struct CoSimPoseRecord(
    long TickIndex,
    double SimulatedTimeSeconds,
    bool IsCaptureTick,
    VehiclePose Pose,
    LaneInterpolationCase Case,
    double SumoX,
    double SumoY,
    double SumoHeadingDegrees);
