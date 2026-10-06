using CarlaNet.Types.Streaming;

namespace CarlaNet.Recording;

/// <summary>
/// Where a vehicle's drawn pose came from, as a truth record's <c>pose_source</c> writes it.
/// </summary>
/// <remarks>
/// A SUMO drive poses its bodies every world tick from SUMO steps a whole number of ticks apart:
/// <c>simulated</c> is SUMO's own step -- the frame a step falls on, or a body placed at SUMO's later step
/// across a discontinuity; <c>interpolated</c> is a pose between two steps, along the lane; <c>held</c> is
/// a body left where its last pose put it, because the session could not place it on the frame. Each is a
/// fact of the frame, taken from the world-observer snapshot of that frame (<see cref="ObservedPoseSource"/>),
/// so a recorder in any process writes the same word for the same frame.
/// </remarks>
public static class PoseSources
{
    /// <summary>The word for <paramref name="source"/>.</summary>
    public static string SidecarValue(PoseSource source) => source switch
    {
        PoseSource.Simulated => "simulated",
        PoseSource.Interpolated => "interpolated",
        PoseSource.Held => "held",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "not a pose source a record writes"),
    };
}
