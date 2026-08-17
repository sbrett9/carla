using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>
/// One vehicle's truth telemetry at an instant — the field set of Docs/CAT_Research/Findings/
/// 09_Telemetry_CoT_Contract. Heights are ellipsoidal WGS84 (HAE). This is the single source of truth
/// consumed by both the CoT-XML sidecar (recorder) and the Python get_vehicle_telemetry shim.
/// </summary>
public sealed record VehicleTelemetry(
    uint Id,
    string TypeId,
    string BaseType,
    string SpecialType,
    string Color,
    string RoleName,
    double Lat,
    double Lon,
    double Hae,
    double HaeDtm,
    double SpeedMps,
    double CourseDeg,
    double Vx,
    double Vy,
    double Vz,
    double LengthM,
    double WidthM,
    double HeightM)
{
    /// <summary>
    /// Fraction of this vehicle's silhouette hidden from the recording camera by anything nearer —
    /// photoreal buildings and trees, terrain relief, other vehicles — on 0 (wholly visible) to 1
    /// (wholly hidden). NaN when it was not measured: no depth capture paired with the frame, or the
    /// vehicle projects outside it. Camera-relative, so it is a property of the (vehicle, sensor)
    /// pair and only ever meaningful on a record that travels with a sensor pose.
    /// </summary>
    public double Occlusion { get; init; } = double.NaN;

    /// <summary>The <see cref="Occlusion"/> fraction as a coarse band — see
    /// <c>OcclusionEstimator.LevelFor</c>. -1 when occlusion was not measured.</summary>
    public int OcclusionLevel { get; init; } = -1;

    /// <summary>
    /// How many points across the vehicle's outline the <see cref="Occlusion"/> fraction was measured
    /// over. Few samples means few possible values: a vehicle covering a handful of pixels can only
    /// report halves and thirds, however many decimal places the fraction is written to. 0 when
    /// occlusion was not measured.
    /// </summary>
    public int OcclusionSamples { get; init; }

    /// <summary>How wide the vehicle appears in the frame, in pixels — its full projected footprint,
    /// including any part outside the frame. 0 when occlusion was not measured.</summary>
    public int ApparentWidthPx { get; init; }

    /// <summary>How tall the vehicle appears in the frame, in pixels. See
    /// <see cref="ApparentWidthPx"/>.</summary>
    public int ApparentHeightPx { get; init; }

    /// <summary>
    /// The vehicle's staging opacity: 1 = fully opaque, below 1 = part-way through the dissolve that
    /// fades boundary-aware traffic in and out at the scene edge.
    /// </summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>
    /// The vehicle's pose in simulator coordinates, paired with <see cref="BoundingBox"/> to give the
    /// oriented box an occlusion test or a bounding-box projection works from. Geometry rather than
    /// telemetry: it is not part of the CoT contract and is not serialized to the sidecar.
    /// </summary>
    public Transform ActorTransform { get; init; }

    /// <summary>The vehicle's bounding box in its own frame (centre offset, half-extents, rotation),
    /// as reported by the actor description. See <see cref="ActorTransform"/>.</summary>
    public BoundingBox BoundingBox { get; init; }
}
