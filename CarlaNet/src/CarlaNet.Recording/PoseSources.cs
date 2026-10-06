using CarlaNet.Types.Streaming;

namespace CarlaNet.Recording;

/// <summary>
/// Where a vehicle's drawn pose came from, as a truth record's <c>pose_source</c> writes it.
/// </summary>
/// <remarks>
/// A SUMO drive poses its bodies every world tick from SUMO steps a whole number of ticks apart, and the
/// owner ruled the four words on 2026-10-06: <c>sumo</c> where the frame falls on a SUMO step and the
/// position is SUMO's own; <c>interpolated</c> on a frame between SUMO steps, the position filled in along
/// the lane; <c>jump</c> where SUMO reported a step too far from the last to drive in one step, the body
/// shown at SUMO's later position for the frames of that step; and <c>stale</c> where the body could not be
/// placed on the frame and stands where it was last drawn. Each is a fact of the frame, taken from the
/// world-observer snapshot of that frame (<see cref="ObservedPoseSource"/>), so a recorder in any process
/// writes the same word for the same frame.
/// </remarks>
public static class PoseSources
{
    /// <summary>The word for <paramref name="source"/>.</summary>
    public static string SidecarValue(PoseSource source) => source switch
    {
        PoseSource.Sumo => "sumo",
        PoseSource.Interpolated => "interpolated",
        PoseSource.Jump => "jump",
        PoseSource.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "not a pose source a record writes"),
    };
}
