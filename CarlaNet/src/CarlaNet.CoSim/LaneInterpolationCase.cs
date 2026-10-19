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
    /// along-lane distance, each point at its frame's offset across its lane, and the two blended
    /// sideways. At SUMO's default the lane change is instantaneous in the data, and the blend spreads
    /// the lane width over the step, because a rendered vehicle teleporting sideways by a lane width is
    /// not something a tracker should be trained on; where the change is spread over time
    /// (<c>lanechange.duration</c>), this is the step SUMO starts reporting the new lane in, the two
    /// points are one step's sideways movement apart, and the blend carries it no further than that.
    /// </summary>
    LaneChange,

    /// <summary>
    /// The frames are on different edges. The route between them is walked through the junction
    /// connectors and the pose placed at the interpolated distance along the concatenated shape.
    /// </summary>
    CrossedEdges,

    /// <summary>
    /// The frames are on different edges and the vehicle also changed lane, so no route reaches the
    /// lane it ended on. The route to the lane the connector actually feeds is walked, and the
    /// sideways move onto the lane it reported is blended on top -- the two cases at once, which is
    /// what leaving a junction and immediately changing lane is.
    /// </summary>
    /// <remarks>
    /// Measured on the shipped Arapahoe scenario at a one-second SUMO step: 38 of these in a hundred
    /// steps at about a hundred rendered vehicles, one every two and a half simulated seconds.
    /// Treating them as discontinuities instead releases and re-admits a vehicle that did nothing
    /// but change lane, which downstream is a track that stops and restarts for no visible reason.
    /// </remarks>
    CrossedEdgesWithLaneChange,

    /// <summary>
    /// No route joins the two lanes, or the distance between them is further than the vehicle could
    /// have travelled. A SUMO teleport, or a removal and reinsertion. Nothing is interpolated: the
    /// vehicle is placed at the later frame, and a bridge releases and re-admits it rather than
    /// sliding it across the gap.
    /// </summary>
    Discontinuous,

    /// <summary>
    /// An end off every lane: a vehicle parked at a stop, whose lane SUMO reports as empty for as
    /// long as it is parked, or one pulling into or out of the stop. The reported points are blended
    /// directly, which holds a parked vehicle where it stands. Measured on the Bahonar scenario:
    /// every guard parked at its tower was filed as <see cref="Discontinuous"/> on every step before
    /// this case existed, and those filled the run report's samples.
    /// </summary>
    OffLane,
}
