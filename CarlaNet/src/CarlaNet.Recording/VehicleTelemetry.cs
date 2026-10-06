using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Streaming;

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
    /// Where this vehicle's box fell against the recording camera's picture -- wholly in it, partly,
    /// wholly outside it, or with a corner at or behind the lens -- read off the box's eight corners
    /// projected through the camera (<see cref="BoxProjector"/>). Null where no camera projected it,
    /// which is every record of the live pull. Camera-relative, like <see cref="Occlusion"/>, so
    /// meaningful only on a record that travels with a sensor pose.
    /// </summary>
    public InFrame? InFrame { get; init; }

    /// <summary>
    /// Fraction of this vehicle's silhouette hidden from the recording camera by anything nearer —
    /// photoreal buildings and trees, terrain relief, other vehicles — on 0 (wholly visible) to 1
    /// (wholly hidden). NaN when it was not measured, and <see cref="OcclusionUnmeasured"/> then says
    /// why. Camera-relative, so it is a property of the (vehicle, sensor) pair and only ever
    /// meaningful on a record that travels with a sensor pose.
    /// </summary>
    public double Occlusion { get; init; } = double.NaN;

    /// <summary>
    /// Why <see cref="Occlusion"/> was not measured, where it was not: the vehicle's box behind the
    /// lens or outside the picture, the vehicle beyond the draw distance, no depth camera or no depth
    /// capture paired with the frame, or a sampling that met nothing to compare. Null where it was
    /// measured, and on a record no recorder has said anything about.
    /// </summary>
    public OcclusionUnmeasured? OcclusionUnmeasured { get; init; }

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
    /// including any part outside the frame. Needs only the projection, not a depth capture, so it is
    /// set wherever <see cref="InFrame"/> is; 0 where the box was not projected or has no footprint.</summary>
    public int ApparentWidthPx { get; init; }

    /// <summary>How tall the vehicle appears in the frame, in pixels. See
    /// <see cref="ApparentWidthPx"/>.</summary>
    public int ApparentHeightPx { get; init; }

    /// <summary>
    /// Where the vehicle stood against the draw distance the recording camera's image was rendered
    /// under: inside it, which is every vehicle of a run with none; partly beyond it; or wholly beyond
    /// it, so the image does not show it although it is in the world (<see cref="DrawDistanceReach"/>).
    /// Camera-relative, like <see cref="Occlusion"/>, so meaningful only on a record that travels with
    /// a sensor pose.
    /// </summary>
    public DrawDistanceReach DrawDistance { get; init; }

    /// <summary>
    /// How far the centre of the vehicle's bounding box was from the recording camera, metres, where
    /// a draw distance was in force or the box fell in the picture; NaN otherwise. What
    /// <see cref="DrawDistance"/> rests on, and one of the box fields of a vehicle in the picture: one
    /// range, from one center to one camera, whichever marked it.
    /// </summary>
    public double CameraRangeMetres { get; init; } = double.NaN;

    /// <summary>
    /// This vehicle's box against the recording camera's picture and the world -- the pixel rectangles,
    /// the share outside the picture, the body's tilt and the box's eight corners in latitude, longitude
    /// and bare-earth height (<see cref="CaptureBox"/>) -- where its box fell in the picture, wholly or
    /// partly. Null for a vehicle outside the picture or with a corner behind the lens, and on every
    /// record no camera projected, which is every record of the live pull.
    /// </summary>
    public CaptureBox? Box { get; init; }

    /// <summary>
    /// The height-align offset, meters, taken off the vehicle's physical altitude to give
    /// <see cref="Hae"/> its bare-earth convention (<c>VehicleTelemetryService.OffsetAt</c> at the
    /// vehicle's point): 0 with no shift. Kept so the corners of the vehicle's box are given the same
    /// convention as its point. Not serialized.
    /// </summary>
    public double HeightAlignOffset { get; init; }

    /// <summary>
    /// The direction the body points, degrees clockwise from true north: its transform's yaw, so the
    /// heading the imagery shows. Not <see cref="CourseDeg"/>, the direction it moves, which differs while
    /// a vehicle turns or changes lane. NaN where no transform was read.
    /// </summary>
    public double HeadingDeg { get; init; } = double.NaN;

    /// <summary>
    /// The vehicle's staging opacity: 1 = fully opaque, below 1 = part-way through the dissolve that
    /// fades boundary-aware traffic in and out at the scene edge.
    /// </summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>
    /// The vehicle's pose in simulator coordinates, paired with <see cref="BoundingBox"/> to give the
    /// oriented box an occlusion test or a bounding-box projection works from. Geometry rather than
    /// telemetry: it is not serialized to the sidecar as it stands; the <see cref="Box"/> of a vehicle
    /// in the picture, its tilt included, is measured from it.
    /// </summary>
    public Transform ActorTransform { get; init; }

    /// <summary>The vehicle's bounding box in its own frame (centre offset, half-extents, rotation),
    /// as reported by the actor description. See <see cref="ActorTransform"/>.</summary>
    public BoundingBox BoundingBox { get; init; }

    /// <summary>
    /// The vehicle this body rendered at the record's frame, where a render set lent it -- the set
    /// the server published with the frame, or a source in the recorder's own process: a pooled body
    /// carries a succession of vehicles over a run, so the actor id alone names none of them. Null
    /// where no render set named the body, which is every run whose actors are their vehicles.
    /// </summary>
    public RenderedVehicle? Rendered { get; init; }

    /// <summary>
    /// The lights commanded on for the vehicle on the record's frame, as the world-observer snapshot of
    /// that frame carried them, by whichever client set them. Null where that snapshot did not carry them
    /// -- one from a server built before it did -- so a record never says no light was on for want of a
    /// reading. Written on a record whose vehicle is in the picture (<see cref="CotWriter"/>).
    /// </summary>
    public VehicleLightStateFlags? Lights { get; init; }

    /// <summary>
    /// Where the pose the vehicle's body was drawn at on the record's frame came from, as the
    /// world-observer snapshot of that frame carried it: SUMO's own step, interpolated between two, or
    /// held where the session could not place it (<see cref="ObservedPoseSource"/>). Null for a vehicle
    /// no session lent a body, and where the snapshot carried no pose source. Written on a record whose
    /// vehicle is in the picture (<see cref="CotWriter"/>).
    /// </summary>
    public PoseSource? PoseSource { get; init; }
}
