namespace CarlaNet.Recording;

/// <summary>
/// Tuning for the depth-based occlusion measurement (see <see cref="OcclusionEstimator"/>).
/// The two constructor parameters are the ones worth exposing to a run; the rest are coherence
/// tolerances that only matter if the depth camera stops tracking the recorded camera.
/// </summary>
/// <param name="MarginMetres">
/// How much nearer than the vehicle's own near surface something has to be before it counts as
/// occluding it. The measurement compares a depth reading against the front face of the vehicle's
/// bounding box, which stands slightly proud of the real bodywork, so a small margin keeps the
/// vehicle's own surfaces — mirrors, a raised roof line over a sloping bonnet — from reading as
/// occluders. Too large and a genuine occluder pressed against the vehicle is missed.
/// </param>
/// <param name="SamplesAcross">
/// Target number of samples along the longer side of the vehicle's projected footprint. The
/// footprint is sampled on a pixel grid stepped to hit roughly this many samples across, so cost per
/// vehicle is bounded regardless of how large it appears; a vehicle smaller than this in pixels is
/// sampled at every pixel. Higher is smoother and slower.
/// </param>
public sealed record OcclusionOptions(double MarginMetres, int SamplesAcross)
{
    /// <summary>Settings used when a caller asks for occlusion without tuning it.</summary>
    public static OcclusionOptions Default { get; } = new(1.0, 24);

    /// <summary>
    /// How far apart in simulation time a depth capture and the recorded frame may be and still be
    /// treated as the same instant. Only used when the two cameras disagree on frame number, which
    /// synchronous ticking prevents.
    /// </summary>
    public double FrameToleranceSeconds { get; init; } = 0.05;

    /// <summary>How far the depth camera may sit from the recorded camera before the pair is treated
    /// as no longer co-located and the frame's occlusion is abandoned rather than mismeasured.</summary>
    public double PoseToleranceMetres { get; init; } = 0.5;

    /// <summary>How far the depth camera may be pointing away from the recorded camera's boresight
    /// before the pair is treated as no longer co-aligned. See <see cref="PoseToleranceMetres"/>.</summary>
    public double PoseToleranceDegrees { get; init; } = 1.0;
}
