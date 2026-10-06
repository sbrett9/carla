namespace CarlaNet.Recording;

/// <summary>
/// Why a vehicle's record carries no occlusion measurement: the recorder's own state, written beside the
/// in-picture fact (<see cref="InFrame"/>) so an absent fraction is never read as "nothing in the way".
/// </summary>
/// <remarks>
/// One reason is written, the one nearest the vehicle itself where several hold: the vehicle's own
/// geometry against the picture first, then the draw distance it was drawn under, then what the depth
/// camera delivered for the whole capture, and last what its sampling met. Nothing here rests on a
/// threshold besides the picture's edges and the camera's near plane; the depth camera's range is the
/// range it was spawned to report over.
/// </remarks>
public enum OcclusionUnmeasured
{
    /// <summary>A corner of the vehicle's box is at or behind the lens, so the box has no projection to
    /// sample. Written <c>behind_camera</c>.</summary>
    BehindCamera,

    /// <summary>The box projects wholly outside the picture: the camera has no view of it to be
    /// obstructed. Written <c>outside_frame</c>.</summary>
    OutsideFrame,

    /// <summary>Wholly beyond the draw distance the image was rendered under: drawn in neither the image
    /// nor the depth capture, which would show the ground behind it as an unobstructed view of it
    /// (<see cref="DrawDistanceCheck.MayBeMeasuredForOcclusion"/>). Written <c>beyond_draw_distance</c>.</summary>
    BeyondDrawDistance,

    /// <summary>The recorder was given no depth camera, so nothing was measured for any vehicle.
    /// Written <c>no_depth_camera</c>.</summary>
    NoDepthCamera,

    /// <summary>The depth camera delivered no capture at all to pair the frame with
    /// (<see cref="OcclusionEstimator.MissedNoCaptures"/>). Written <c>no_depth_capture</c>.</summary>
    NoDepthCapture,

    /// <summary>Every depth capture held was of some other instant
    /// (<see cref="OcclusionEstimator.MissedOutOfStep"/>). Written <c>depth_out_of_step</c>.</summary>
    DepthOutOfStep,

    /// <summary>The depth capture of the instant was taken from another pose than the picture
    /// (<see cref="OcclusionEstimator.MissedPose"/>). Written <c>depth_pose_mismatch</c>.</summary>
    DepthPoseMismatch,

    /// <summary>Every sampled point of the vehicle's surface lies at or beyond the range the depth camera
    /// reports over, where every reading saturates and nothing can be told apart. Written
    /// <c>beyond_depth_range</c>.</summary>
    BeyondDepthRange,

    /// <summary>No ray of the sampling grid over the box's pixel rectangle met the box: a vehicle narrower
    /// than the grid's step, which is one pixel at its finest, or a box with no extent. Written
    /// <c>no_sample</c>.</summary>
    NoSample,
}

/// <summary>Marks a capture's truth records with why their occlusion was not measured.</summary>
public static class UnmeasuredOcclusion
{
    /// <summary>The sidecar's word for each reason.</summary>
    public static string SidecarValue(OcclusionUnmeasured reason) => reason switch
    {
        OcclusionUnmeasured.BehindCamera => "behind_camera",
        OcclusionUnmeasured.OutsideFrame => "outside_frame",
        OcclusionUnmeasured.BeyondDrawDistance => "beyond_draw_distance",
        OcclusionUnmeasured.NoDepthCamera => "no_depth_camera",
        OcclusionUnmeasured.NoDepthCapture => "no_depth_capture",
        OcclusionUnmeasured.DepthOutOfStep => "depth_out_of_step",
        OcclusionUnmeasured.DepthPoseMismatch => "depth_pose_mismatch",
        OcclusionUnmeasured.BeyondDepthRange => "beyond_depth_range",
        OcclusionUnmeasured.NoSample => "no_sample",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "not a reason occlusion goes unmeasured"),
    };

    /// <summary>
    /// The reason a record's own geometry gives before any depth capture is consulted -- its box
    /// behind the lens or outside the picture, or the vehicle wholly beyond the draw distance -- or null
    /// where nothing in it keeps the vehicle from being measured.
    /// </summary>
    /// <param name="record">A record already marked with where its box fell (<see cref="BoxProjector.Mark"/>).</param>
    /// <exception cref="InvalidOperationException">The record's box was never projected.</exception>
    public static OcclusionUnmeasured? FromGeometry(VehicleTelemetry record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.InFrame switch
        {
            null => throw new InvalidOperationException(
                $"vehicle {record.Id} was never projected into the picture, so nothing can be said of its occlusion"),
            InFrame.BehindCamera => OcclusionUnmeasured.BehindCamera,
            InFrame.None => OcclusionUnmeasured.OutsideFrame,
            _ => DrawDistanceCheck.MayBeMeasuredForOcclusion(record) ? null : OcclusionUnmeasured.BeyondDrawDistance,
        };
    }

    /// <summary>
    /// Every record marked with why its occlusion was not measured: the reason its own geometry gives
    /// where it gives one, and <paramref name="forTheCapture"/> -- what the depth camera did, or that
    /// there was none -- for the rest.
    /// </summary>
    /// <param name="records">The capture's truth records, each already marked with where its box fell.</param>
    /// <param name="forTheCapture">The reason that holds for every vehicle of this capture.</param>
    public static IReadOnlyList<VehicleTelemetry> Mark(IReadOnlyList<VehicleTelemetry> records,
                                                       OcclusionUnmeasured forTheCapture)
    {
        ArgumentNullException.ThrowIfNull(records);
        var marked = new List<VehicleTelemetry>(records.Count);
        foreach (VehicleTelemetry record in records)
        {
            marked.Add(record with
            {
                Occlusion = double.NaN,
                OcclusionLevel = -1,
                OcclusionSamples = 0,
                OcclusionUnmeasured = FromGeometry(record) ?? forTheCapture,
            });
        }

        return marked;
    }
}
