using CarlaNet.Transport;
using CarlaNet.Transport.Streaming;
using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>A vehicle's oriented box in the world: its pose plus the box its actor description
/// reports in its own frame. The geometry an occlusion measurement works from.</summary>
public readonly record struct VehicleBox(ActorId ActorId, Transform ActorTransform, BoundingBox Box);

/// <summary>How much of one vehicle the camera cannot see, together with what that verdict rests on.
/// <paramref name="Samples"/> is how many points across the vehicle's outline were tested, and
/// <paramref name="ApparentWidthPx"/> / <paramref name="ApparentHeightPx"/> how large it appears in
/// the frame. Both matter for reading the fraction: a vehicle a kilometre away covers a few pixels
/// and yields a handful of samples, so its fraction can only take a few coarse values, where a
/// near one is measured over hundreds.</summary>
public readonly record struct VehicleOcclusion(
    double Fraction, int Level, int Samples, int ApparentWidthPx, int ApparentHeightPx);

/// <summary>
/// Measures, per vehicle and per camera, how much of the vehicle is hidden behind something nearer.
///
/// The photoreal 3D tiles are ordinary opaque geometry with no class or instance identity, so the
/// usual segmentation-based occlusion filters cannot see them at all. Depth can: a building, a tree
/// or a hillside writes the depth buffer and wins the depth test exactly when it stands between the
/// camera and a vehicle. So the test here is purely geometric — where the depth capture reports
/// something nearer than the vehicle's own leading surface, that part of the vehicle is hidden —
/// and it works the same for a Cesium building, a tree and another car.
///
/// The vehicle's silhouette is sampled in image space: the camera ray for each sampled pixel is
/// intersected with the vehicle's oriented bounding box, which both restricts sampling to the pixels
/// the vehicle actually projects onto (rather than its whole rectangular footprint) and makes the
/// comparison self-occlusion-free, because everything belonging to the vehicle lies between the
/// ray's entry and exit points. The resulting fraction of hidden samples approximates the area
/// fraction the amodal-segmentation literature calls the ratio of occluded region.
///
/// A mid-fade vehicle occluding another is measured as whatever the depth capture shows of it, which
/// depends on how far the dithered dissolve has been resolved; weighting an occluder by its opacity
/// exactly needs the occluder's identity, which depth alone does not carry.
///
/// Depth readings lose accuracy with range — measurably, and predictably enough to correct for — so
/// the threshold that decides "nearer than the vehicle" widens with range
/// (<see cref="OcclusionOptions.RangeErrorCoefficient"/>). Beyond the depth capture's far plane every
/// reading saturates and no comparison is possible at all, and those vehicles go unmeasured.
///
/// The projection of the box into the picture is <see cref="BoxProjector"/>'s, which the recorder
/// runs for every capture whether or not a depth camera is attached; this class owns only the
/// sampling of the depth capture over that projection. A vehicle that goes unmeasured says why
/// (<see cref="OcclusionUnmeasured"/>), so the sidecar never carries a silent absence.
/// </summary>
public sealed class OcclusionEstimator : IDisposable
{
    // Enough depth captures to cover the reordering a couple of ticks of stream jitter can cause;
    // matching is by frame number, so this is a small tolerance buffer, not a queue.
    private const int RingSize = 8;

    // Points nearer than this to the camera are treated as being at or behind the lens: their
    // projection is meaningless and dividing by that depth would blow up.
    private const double NearPlaneMetres = BoxProjector.NearPlaneMetres;

    private readonly OcclusionOptions _options;
    private readonly IDisposable _subscription;
    private readonly SensorPoseCheck? _depthPose;
    private readonly DepthFrame?[] _ring = new DepthFrame?[RingSize];
    private int _next;
    private long _matched, _missedNoCaptures, _missedOutOfStep, _missedPose;

    /// <summary>Recorded frames that found a depth capture of the same instant and pose.</summary>
    public long Matched => Interlocked.Read(ref _matched);

    /// <summary>Recorded frames that arrived with no depth capture available at all — the depth
    /// camera never delivered, or stopped.</summary>
    public long MissedNoCaptures => Interlocked.Read(ref _missedNoCaptures);

    /// <summary>Recorded frames whose depth captures were all of some other instant — the depth
    /// stream running too far behind or ahead of the recorded one to pair with it.</summary>
    public long MissedOutOfStep => Interlocked.Read(ref _missedOutOfStep);

    /// <summary>Recorded frames whose depth capture was of the right instant but the wrong place —
    /// the two cameras are no longer looking from the same pose.</summary>
    public long MissedPose => Interlocked.Read(ref _missedPose);

    /// <summary>Recorded frames left without occlusion, for any reason.</summary>
    public long Missed => MissedNoCaptures + MissedOutOfStep + MissedPose;

    /// <summary>Whether a matched depth capture's pose is read from the snapshot of its own frame and
    /// its header checked against it.</summary>
    public bool ChecksDepthPose => _depthPose is not null;

    /// <summary>Matched depth captures whose pose was read from the snapshot of their own frame.</summary>
    public long DepthPoseFromSnapshot => _depthPose?.FromSnapshot ?? 0;

    /// <summary>Of those, the ones whose header carried a different pose. A server that stamps the
    /// header at capture keeps this at zero; see <see cref="SensorPoseCheck"/>.</summary>
    public long DepthPoseHeaderDisagreed => _depthPose?.HeaderDisagreed ?? 0;

    /// <summary>Matched depth captures whose own frame the client no longer held, projected from their
    /// header's pose.</summary>
    public long DepthPoseFromHeader => _depthPose?.FromHeader ?? 0;

    /// <param name="depthStreamToken">The depth camera actor's 24-byte sensor stream token. The
    /// subscription is this estimator's own, so it neither disturbs nor depends on any other listener
    /// on that camera.</param>
    /// <param name="depthActorId">The depth camera actor. Given, each matched capture is projected
    /// from the depth camera's pose in the snapshot of that capture's own frame, with its header
    /// checked against it (<see cref="SensorPoseCheck"/>); null projects from the header alone.</param>
    public OcclusionEstimator(CarlaClient client, byte[] depthStreamToken, OcclusionOptions? options = null,
                              ActorId? depthActorId = null)
    {
        if (depthStreamToken is not { Length: 24 })
            throw new ArgumentException("depthStreamToken must be a 24-byte sensor stream token",
                                        nameof(depthStreamToken));
        _options = options ?? OcclusionOptions.Default;
        _depthPose = depthActorId is { } depth ? new SensorPoseCheck(client.GetSnapshotFrame, depth) : null;
        _subscription = client.SubscribeToStream(depthStreamToken, OnDepthFrame);
    }

    private void OnDepthFrame(SensorFrame frame)
    {
        var depth = DepthFrame.FromSensorFrame(frame, _options.MaxRangeMetres);
        if (depth is null) return;
        lock (_ring)
        {
            _ring[_next] = depth;
            _next = (_next + 1) % RingSize;
        }
    }

    /// <summary>
    /// The depth capture belonging to a recorded frame, or null if there is none to trust. Frame
    /// number is the primary key — under synchronous ticking both cameras render the same tick — with
    /// simulation time as the fallback for a free-running world. The camera pose is checked too: the
    /// measurement is only meaningful while the depth camera is looking from where the recorded
    /// camera is looking, and silently mismeasuring is worse than reporting nothing.
    /// </summary>
    /// <remarks>The capture returned carries the pose it is projected from, which is the depth camera's
    /// pose in the snapshot of the capture's own frame wherever the estimator knows the camera and the
    /// client holds the frame, and its header's otherwise: a header stamped after the capture was read
    /// back can carry the pose of a later frame (<see cref="SensorPoseCheck"/>), and projecting the
    /// vehicles of this frame from it would measure them against where the camera went next.</remarks>
    public DepthFrame? MatchTo(ulong frame, double timestamp, Transform cameraTransform)
        => MatchTo(frame, timestamp, cameraTransform, out _);

    /// <inheritdoc cref="MatchTo(ulong, double, Transform)"/>
    /// <param name="unpaired">Which way the pairing failed, as the counters record it, so each vehicle
    /// of the frame can say why it went unmeasured; null on a match.</param>
    public DepthFrame? MatchTo(ulong frame, double timestamp, Transform cameraTransform,
                               out OcclusionUnmeasured? unpaired)
    {
        // The two cameras arrive over separate connections, so the depth capture for this instant may
        // still be in flight when the recorded frame arrives. Wait a little for it rather than giving
        // up immediately — but only a little, since this runs on the thread reading the recorded
        // stream. If it never turns up, say which way it failed rather than just that it did.
        long deadline = Environment.TickCount64 + Math.Max(0, _options.MatchWaitMilliseconds);
        while (true)
        {
            var (best, gap, anyAvailable) = FindNearest(frame, timestamp);
            if (best is not null && gap <= _options.FrameToleranceSeconds)
            {
                DepthFrame depth = _depthPose is null
                    ? best
                    : best.WithTransform(_depthPose.Resolve(best.Frame, best.Transform));
                if (!IsCoLocated(depth, cameraTransform))
                {
                    Interlocked.Increment(ref _missedPose);
                    unpaired = OcclusionUnmeasured.DepthPoseMismatch;
                    return null;
                }
                Interlocked.Increment(ref _matched);
                unpaired = null;
                return depth;
            }
            if (Environment.TickCount64 >= deadline)
            {
                if (anyAvailable)
                {
                    Interlocked.Increment(ref _missedOutOfStep);
                    unpaired = OcclusionUnmeasured.DepthOutOfStep;
                }
                else
                {
                    Interlocked.Increment(ref _missedNoCaptures);
                    unpaired = OcclusionUnmeasured.NoDepthCapture;
                }
                return null;
            }
            Thread.Sleep(2);
        }
    }

    /// <summary>The held capture closest to an instant, how far off it is, and whether there were any
    /// captures to choose from at all.</summary>
    private (DepthFrame? Best, double Gap, bool AnyAvailable) FindNearest(ulong frame, double timestamp)
    {
        DepthFrame? best = null;
        double bestGap = double.PositiveInfinity;
        bool anyAvailable = false;
        lock (_ring)
        {
            foreach (var candidate in _ring)
            {
                if (candidate is null) continue;
                anyAvailable = true;
                if (candidate.Frame == frame) return (candidate, 0.0, true);
                double gap = Math.Abs(candidate.Timestamp - timestamp);
                if (gap < bestGap) { best = candidate; bestGap = gap; }
            }
        }
        return (best, bestGap, anyAvailable);
    }

    private bool IsCoLocated(DepthFrame depth, Transform cameraTransform)
    {
        var a = depth.Transform.Location;
        var b = cameraTransform.Location;
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        if (dx * dx + dy * dy + dz * dz > _options.PoseToleranceMetres * _options.PoseToleranceMetres)
            return false;

        var da = new RotationBasis(depth.Transform.Rotation).Forward;
        var db = new RotationBasis(cameraTransform.Rotation).Forward;
        double cos = da.X * db.X + da.Y * db.Y + da.Z * db.Z;
        return cos >= Math.Cos(_options.PoseToleranceDegrees * Math.PI / 180.0);
    }

    /// <summary>
    /// Occlusion for every supplied vehicle that projects into the depth capture. Vehicles wholly
    /// outside the frame, or straddling the camera plane, are absent from the result rather than
    /// reported as visible — the camera has no view of them to be obstructed. <see cref="Sample"/>
    /// says, per vehicle, why one is absent.
    /// </summary>
    public IReadOnlyDictionary<ActorId, VehicleOcclusion> Estimate(
        DepthFrame depth, IReadOnlyList<VehicleBox> vehicles)
        => Estimate(depth, vehicles, _options);

    /// <inheritdoc cref="Estimate(DepthFrame, IReadOnlyList{VehicleBox})"/>
    /// <remarks>The measurement is a pure function of the capture, the boxes and the tuning — nothing
    /// about it needs a live connection.</remarks>
    public static IReadOnlyDictionary<ActorId, VehicleOcclusion> Estimate(
        DepthFrame depth, IReadOnlyList<VehicleBox> vehicles, OcclusionOptions options)
    {
        var result = new Dictionary<ActorId, VehicleOcclusion>(vehicles.Count);
        if (vehicles.Count == 0) return result;

        var camera = CameraOf(depth);
        foreach (var vehicle in vehicles)
        {
            if (Sample(depth, camera, vehicle, options, out _) is { } occlusion)
                result[vehicle.ActorId] = occlusion;
        }
        return result;
    }

    /// <summary>
    /// One vehicle's occlusion against one depth capture, or null and why there is none: the box
    /// behind the lens or outside the capture, every sampled point of it beyond the capture's range,
    /// or a box the sampling grid never met.
    /// </summary>
    public VehicleOcclusion? Sample(DepthFrame depth, VehicleBox vehicle, out OcclusionUnmeasured? unmeasured)
        => Sample(depth, CameraOf(depth), vehicle, _options, out unmeasured);

    /// <inheritdoc cref="Sample(DepthFrame, VehicleBox, out OcclusionUnmeasured?)"/>
    public static VehicleOcclusion? Sample(DepthFrame depth, VehicleBox vehicle, OcclusionOptions options,
                                           out OcclusionUnmeasured? unmeasured)
        => Sample(depth, CameraOf(depth), vehicle, options, out unmeasured);

    /// <summary>The depth capture as the pinhole its pixels were rendered through: its own pose, size
    /// and field of view, so the sampling grid is laid over the picture the depth was written in.</summary>
    private static PinholeCamera CameraOf(DepthFrame depth)
        => new(depth.Transform, depth.Width, depth.Height, depth.HFovDeg);

    private static VehicleOcclusion? Sample(DepthFrame depth, in PinholeCamera camera, VehicleBox vehicle,
                                            OcclusionOptions options, out OcclusionUnmeasured? unmeasured)
    {
        var box = new OrientedBox(vehicle.ActorTransform, vehicle.Box);
        BoxProjection projection = BoxProjector.Project(camera, box);
        if (projection.InFrame == InFrame.BehindCamera)
        {
            unmeasured = OcclusionUnmeasured.BehindCamera;
            return null;
        }
        if (projection.InFrame == InFrame.None)
        {
            unmeasured = OcclusionUnmeasured.OutsideFrame;
            return null;
        }

        // The footprint clipped to the capture's pixels: never empty for a box in the picture.
        int x0 = Math.Max(0, (int)Math.Floor(projection.MinU));
        int x1 = Math.Min(depth.Width - 1, (int)Math.Ceiling(projection.MaxU));
        int y0 = Math.Max(0, (int)Math.Floor(projection.MinV));
        int y1 = Math.Min(depth.Height - 1, (int)Math.Ceiling(projection.MaxV));

        // Step the grid so the longer side gets about the requested number of samples, whatever the
        // vehicle's apparent size; a vehicle smaller than that is sampled at every pixel.
        int across = Math.Max(1, options.SamplesAcross);
        int span = Math.Max(x1 - x0 + 1, y1 - y0 + 1);
        int step = Math.Max(1, (span + across - 1) / across);

        // The camera axes in the box's frame, so each sample ray only costs the two scalings that
        // distinguish it from its neighbours.
        RotationBasis axes = camera.Basis;
        double camX = camera.Pose.Location.X, camY = camera.Pose.Location.Y, camZ = camera.Pose.Location.Z;
        var forwardLocal = ToBox(axes.Forward, box.AxisX, box.AxisY, box.AxisZ);
        var rightLocal = ToBox(axes.Right, box.AxisX, box.AxisY, box.AxisZ);
        var upLocal = ToBox(axes.Up, box.AxisX, box.AxisY, box.AxisZ);
        double originX = (camX - box.X) * box.AxisX.X + (camY - box.Y) * box.AxisX.Y + (camZ - box.Z) * box.AxisX.Z;
        double originY = (camX - box.X) * box.AxisY.X + (camY - box.Y) * box.AxisY.Y + (camZ - box.Z) * box.AxisY.Z;
        double originZ = (camX - box.X) * box.AxisZ.X + (camY - box.Y) * box.AxisZ.Y + (camZ - box.Z) * box.AxisZ.Z;

        int samples = 0, hidden = 0, met = 0;
        for (int y = y0; y <= y1; y += step)
        {
            double screenUp = -(y - camera.CentreY) / camera.Focal;
            for (int x = x0; x <= x1; x += step)
            {
                double screenRight = (x - camera.CentreX) / camera.Focal;
                // Parameterised so the ray parameter IS the range along the optical axis, matching
                // what the depth capture reports.
                double dirX = forwardLocal.X + rightLocal.X * screenRight + upLocal.X * screenUp;
                double dirY = forwardLocal.Y + rightLocal.Y * screenRight + upLocal.Y * screenUp;
                double dirZ = forwardLocal.Z + rightLocal.Z * screenRight + upLocal.Z * screenUp;

                double enter = double.NegativeInfinity, exit = double.PositiveInfinity;
                if (!Slab(originX, dirX, box.ExtentX, ref enter, ref exit)) continue;
                if (!Slab(originY, dirY, box.ExtentY, ref enter, ref exit)) continue;
                if (!Slab(originZ, dirZ, box.ExtentZ, ref enter, ref exit)) continue;
                if (exit <= 0.0) continue;                      // the whole vehicle is behind the lens
                met++;
                double surface = Math.Max(enter, NearPlaneMetres);
                // Every reading saturates at the greatest range the camera reports, so out there the
                // comparison below would call any vehicle hidden whatever is really in front of it.
                // Leave those samples out; a vehicle wholly beyond that range reports nothing.
                if (surface >= depth.MaxRangeMetres) continue;

                samples++;
                // Anything the camera sees nearer than the vehicle's leading surface is in front of
                // it. Everything belonging to the vehicle itself lies past that surface, so its own
                // bodywork can never be counted here.
                //
                // The margin grows with range because a depth reading itself falls short of the true
                // range by more and more the further out it is measured. Without that term the
                // vehicle's own surface eventually reads as standing in front of itself, and every
                // vehicle past about a kilometre reports as fully hidden. Sizing the term from the
                // vehicle's range rather than the reading's makes it very slightly generous — a
                // nearer occluder is biased less — which errs towards calling a vehicle visible.
                double margin = options.MarginMetres + options.RangeErrorCoefficient * surface * surface;
                if (depth.RangeAt(x, y) < surface - margin) hidden++;
            }
        }

        if (samples == 0)
        {
            // Nothing to compare: either no ray of the grid met the box -- a vehicle narrower than a
            // pixel, or a box with no extent -- or every point it met lies where the capture saturates.
            unmeasured = met == 0 ? OcclusionUnmeasured.NoSample : OcclusionUnmeasured.BeyondDepthRange;
            return null;
        }
        unmeasured = null;
        double fraction = (double)hidden / samples;
        // Apparent size is the full projected footprint, not the part clipped to the frame, so it
        // reads as "how big does this vehicle look" rather than "how much of it is on screen".
        return new VehicleOcclusion(
            fraction, LevelFor(fraction), samples,
            projection.ApparentWidthPx, projection.ApparentHeightPx);
    }

    /// <summary>
    /// The occlusion fraction as a coarse band, following the bands the amodal-instance-segmentation
    /// datasets report against (KINS: untouched, up to 30 %, 30-60 %, 60-90 %), with a fifth band for
    /// the effectively-invisible remainder:
    /// 0 wholly visible · 1 up to 30 % · 2 30-60 % · 3 60-90 % · 4 over 90 %.
    /// </summary>
    public static int LevelFor(double fraction) =>
        fraction <= 0.0 ? 0
        : fraction < 0.30 ? 1
        : fraction < 0.60 ? 2
        : fraction < 0.90 ? 3
        : 4;

    private static Vector3D ToBox(Vector3D world, Vector3D axisX, Vector3D axisY, Vector3D axisZ) => new(
        world.X * axisX.X + world.Y * axisX.Y + world.Z * axisX.Z,
        world.X * axisY.X + world.Y * axisY.Y + world.Z * axisY.Z,
        world.X * axisZ.X + world.Y * axisZ.Y + world.Z * axisZ.Z);

    // One axis of the ray/box slab test, narrowing the interval carried in from the previous axes.
    private static bool Slab(double origin, double direction, double extent,
                             ref double enter, ref double exit)
    {
        const double Parallel = 1e-12;
        if (Math.Abs(direction) < Parallel)
            return Math.Abs(origin) <= extent;      // parallel to this pair of faces: in or out for good
        double near = (-extent - origin) / direction;
        double far = (extent - origin) / direction;
        if (near > far) (near, far) = (far, near);
        if (near > enter) enter = near;
        if (far < exit) exit = far;
        return enter <= exit;
    }

    public void Dispose()
    {
        try { _subscription.Dispose(); } catch { /* already gone */ }
    }
}
