using CarlaNet.Transport;
using CarlaNet.Transport.Streaming;
using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>A vehicle's oriented box in the world: its pose plus the box its actor description
/// reports in its own frame. The geometry an occlusion measurement works from.</summary>
public readonly record struct VehicleBox(ActorId ActorId, Transform ActorTransform, BoundingBox Box);

/// <summary>How much of one vehicle the camera cannot see, and how many silhouette samples that
/// verdict rests on (a handful of samples on a distant vehicle is a coarser number than a few
/// hundred on a near one).</summary>
public readonly record struct VehicleOcclusion(double Fraction, int Level, int Samples);

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
/// </summary>
public sealed class OcclusionEstimator : IDisposable
{
    // Enough depth captures to cover the reordering a couple of ticks of stream jitter can cause;
    // matching is by frame number, so this is a small tolerance buffer, not a queue.
    private const int RingSize = 8;

    // Points nearer than this to the camera are treated as being at or behind the lens: their
    // projection is meaningless and dividing by that depth would blow up.
    private const double NearPlaneMetres = 0.1;

    private readonly OcclusionOptions _options;
    private readonly IDisposable _subscription;
    private readonly DepthFrame?[] _ring = new DepthFrame?[RingSize];
    private int _next;
    private long _matched, _missed;

    /// <summary>Recorded frames that found a depth capture of the same instant and pose.</summary>
    public long Matched => Interlocked.Read(ref _matched);

    /// <summary>Recorded frames left without occlusion because no depth capture matched them — the
    /// depth camera lagging, stopping, or drifting off the recorded camera's pose.</summary>
    public long Missed => Interlocked.Read(ref _missed);

    /// <param name="depthStreamToken">The depth camera actor's 24-byte sensor stream token. The
    /// subscription is this estimator's own, so it neither disturbs nor depends on any other listener
    /// on that camera.</param>
    public OcclusionEstimator(CarlaClient client, byte[] depthStreamToken, OcclusionOptions? options = null)
    {
        if (depthStreamToken is not { Length: 24 })
            throw new ArgumentException("depthStreamToken must be a 24-byte sensor stream token",
                                        nameof(depthStreamToken));
        _options = options ?? OcclusionOptions.Default;
        _subscription = client.SubscribeToStream(depthStreamToken, OnDepthFrame);
    }

    private void OnDepthFrame(SensorFrame frame)
    {
        var depth = DepthFrame.FromSensorFrame(frame);
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
    public DepthFrame? MatchTo(ulong frame, double timestamp, Transform cameraTransform)
    {
        DepthFrame? best = null;
        double bestGap = double.PositiveInfinity;
        lock (_ring)
        {
            foreach (var candidate in _ring)
            {
                if (candidate is null) continue;
                if (candidate.Frame == frame) { best = candidate; bestGap = 0.0; break; }
                double gap = Math.Abs(candidate.Timestamp - timestamp);
                if (gap < bestGap) { best = candidate; bestGap = gap; }
            }
        }

        if (best is null || bestGap > _options.FrameToleranceSeconds || !IsCoLocated(best, cameraTransform))
        {
            Interlocked.Increment(ref _missed);
            return null;
        }
        Interlocked.Increment(ref _matched);
        return best;
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
    /// reported as visible — the camera has no view of them to be obstructed.
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

        var camera = new RotationBasis(depth.Transform.Rotation);
        double camX = depth.Transform.Location.X;
        double camY = depth.Transform.Location.Y;
        double camZ = depth.Transform.Location.Z;

        // Pinhole intrinsics from the frame's own field of view and size: square pixels, principal
        // point at the centre — the same convention as the recorded sensor pose and the viewer's
        // pixel-to-world picker, so every projection in this project agrees.
        double focal = depth.Width / (2.0 * Math.Tan(depth.HFovDeg * Math.PI / 360.0));
        double centreX = depth.Width / 2.0, centreY = depth.Height / 2.0;

        foreach (var vehicle in vehicles)
        {
            var measured = Measure(depth, vehicle, options, camera,
                                   camX, camY, camZ, focal, centreX, centreY);
            if (measured is { } occlusion) result[vehicle.ActorId] = occlusion;
        }
        return result;
    }

    private static VehicleOcclusion? Measure(DepthFrame depth, VehicleBox vehicle,
                                             OcclusionOptions options, RotationBasis camera,
                                             double camX, double camY, double camZ,
                                             double focal, double centreX, double centreY)
    {
        var extent = vehicle.Box.Extent;
        if (extent.X <= 0f || extent.Y <= 0f || extent.Z <= 0f) return null;

        // The box's frame is the actor's, offset and rotated by the box's own local placement.
        var actor = new RotationBasis(vehicle.ActorTransform.Rotation);
        var local = new RotationBasis(vehicle.Box.Rotation);
        var offset = actor.Rotate(new Vector3D(vehicle.Box.Location.X, vehicle.Box.Location.Y,
                                               vehicle.Box.Location.Z));
        double boxX = vehicle.ActorTransform.Location.X + offset.X;
        double boxY = vehicle.ActorTransform.Location.Y + offset.Y;
        double boxZ = vehicle.ActorTransform.Location.Z + offset.Z;
        var axisX = actor.Rotate(local.Forward);
        var axisY = actor.Rotate(local.Right);
        var axisZ = actor.Rotate(local.Up);

        // Footprint of the box in pixels, from its eight corners.
        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            double sx = (corner & 1) == 0 ? -extent.X : extent.X;
            double sy = (corner & 2) == 0 ? -extent.Y : extent.Y;
            double sz = (corner & 4) == 0 ? -extent.Z : extent.Z;
            double wx = boxX + axisX.X * sx + axisY.X * sy + axisZ.X * sz - camX;
            double wy = boxY + axisX.Y * sx + axisY.Y * sy + axisZ.Y * sz - camY;
            double wz = boxZ + axisX.Z * sx + axisY.Z * sy + axisZ.Z * sz - camZ;

            double forward = wx * camera.Forward.X + wy * camera.Forward.Y + wz * camera.Forward.Z;
            // A box with a corner at or behind the lens has no well-defined footprint; that is a
            // vehicle on top of the camera, not a subject, so leave it unmeasured.
            if (forward <= NearPlaneMetres) return null;
            double right = wx * camera.Right.X + wy * camera.Right.Y + wz * camera.Right.Z;
            double up = wx * camera.Up.X + wy * camera.Up.Y + wz * camera.Up.Z;

            double u = centreX + focal * right / forward;
            double v = centreY - focal * up / forward;
            if (u < minU) minU = u;
            if (u > maxU) maxU = u;
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
        }

        int x0 = Math.Max(0, (int)Math.Floor(minU)), x1 = Math.Min(depth.Width - 1, (int)Math.Ceiling(maxU));
        int y0 = Math.Max(0, (int)Math.Floor(minV)), y1 = Math.Min(depth.Height - 1, (int)Math.Ceiling(maxV));
        if (x0 > x1 || y0 > y1) return null;   // projects wholly outside the frame

        // Step the grid so the longer side gets about the requested number of samples, whatever the
        // vehicle's apparent size; a vehicle smaller than that is sampled at every pixel.
        int across = Math.Max(1, options.SamplesAcross);
        int span = Math.Max(x1 - x0 + 1, y1 - y0 + 1);
        int step = Math.Max(1, (span + across - 1) / across);

        // The camera axes in the box's frame, so each sample ray only costs the two scalings that
        // distinguish it from its neighbours.
        var forwardLocal = ToBox(camera.Forward, axisX, axisY, axisZ);
        var rightLocal = ToBox(camera.Right, axisX, axisY, axisZ);
        var upLocal = ToBox(camera.Up, axisX, axisY, axisZ);
        double originX = (camX - boxX) * axisX.X + (camY - boxY) * axisX.Y + (camZ - boxZ) * axisX.Z;
        double originY = (camX - boxX) * axisY.X + (camY - boxY) * axisY.Y + (camZ - boxZ) * axisY.Z;
        double originZ = (camX - boxX) * axisZ.X + (camY - boxY) * axisZ.Y + (camZ - boxZ) * axisZ.Z;

        int samples = 0, hidden = 0;
        for (int y = y0; y <= y1; y += step)
        {
            double screenUp = -(y - centreY) / focal;
            for (int x = x0; x <= x1; x += step)
            {
                double screenRight = (x - centreX) / focal;
                // Parameterised so the ray parameter IS the range along the optical axis, matching
                // what the depth capture reports.
                double dirX = forwardLocal.X + rightLocal.X * screenRight + upLocal.X * screenUp;
                double dirY = forwardLocal.Y + rightLocal.Y * screenRight + upLocal.Y * screenUp;
                double dirZ = forwardLocal.Z + rightLocal.Z * screenRight + upLocal.Z * screenUp;

                double enter = double.NegativeInfinity, exit = double.PositiveInfinity;
                if (!Slab(originX, dirX, extent.X, ref enter, ref exit)) continue;
                if (!Slab(originY, dirY, extent.Y, ref enter, ref exit)) continue;
                if (!Slab(originZ, dirZ, extent.Z, ref enter, ref exit)) continue;
                if (exit <= 0.0) continue;                      // the whole vehicle is behind the lens
                double surface = Math.Max(enter, NearPlaneMetres);
                // Every reading saturates at the depth camera's far plane, so out there the
                // comparison below would call any vehicle hidden whatever is really in front of it.
                // Leave those samples out; a vehicle wholly past the far plane reports nothing.
                if (surface >= DepthFrame.FarPlaneMetres) continue;

                samples++;
                // Anything the camera sees nearer than the vehicle's leading surface is in front of
                // it. Everything belonging to the vehicle itself lies past that surface, so its own
                // bodywork can never be counted here.
                if (depth.RangeAt(x, y) < surface - options.MarginMetres) hidden++;
            }
        }

        if (samples == 0) return null;
        double fraction = (double)hidden / samples;
        return new VehicleOcclusion(fraction, LevelFor(fraction), samples);
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
