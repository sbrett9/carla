namespace CarlaNet.CoSim;

/// <summary>How a pose between two SUMO frames was produced.</summary>
/// <remarks>
/// Recorded on every interpolated pose, because the four are not equally trustworthy and a run that
/// produced a great many of the last one produced something nobody should capture.
/// </remarks>
public enum LaneInterpolationCase
{
    /// <summary>
    /// Both frames on one lane. The lane position is interpolated and the lane's polyline evaluated
    /// at it, which is exact on a curve by construction.
    /// </summary>
    SameLane,

    /// <summary>
    /// Both frames on one edge, on different lanes. Each lane is evaluated at the interpolated
    /// along-lane distance and the two points blended sideways, because SUMO's lane change is
    /// instantaneous in the data and a rendered vehicle teleporting sideways by a lane width is not
    /// something a tracker should be trained on.
    /// </summary>
    LaneChange,

    /// <summary>
    /// The frames are on different edges. The route between them is walked through the junction
    /// connectors and the pose placed at the interpolated distance along the concatenated shape.
    /// </summary>
    CrossedEdges,

    /// <summary>
    /// No route joins the two lanes, or the distance between them is further than the vehicle could
    /// have travelled. A SUMO teleport, or a removal and reinsertion. Nothing is interpolated: the
    /// vehicle is placed at the later frame, and a bridge releases and re-admits it rather than
    /// sliding it across the gap.
    /// </summary>
    Discontinuous,
}
