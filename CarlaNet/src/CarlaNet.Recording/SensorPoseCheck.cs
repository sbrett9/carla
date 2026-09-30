using CarlaNet.Transport;
using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>
/// Every actor's snapshot as of <paramref name="frame"/>, or as of the nearest frame still held,
/// which <paramref name="servedFrame"/> names; null when nothing is held. The shape of
/// <see cref="CarlaClient.GetSnapshotFrame"/> and <see cref="SnapshotHistory.Nearest"/>.
/// </summary>
public delegate IReadOnlyDictionary<ActorId, ActorSnapshot>? SnapshotFrameLookup(ulong frame, out ulong servedFrame);

/// <summary>
/// The pose a sensor's image was taken from: the sensor's transform in the client's world snapshot
/// of the image's own frame, with the transform in the image's sensor header checked against it and
/// both outcomes counted.
/// </summary>
/// <remarks>
/// <para><b>The header is checked, not trusted.</b> The server wrote a camera image's header when
/// the image had been read back from the GPU, and took the transform and the clock in it as they
/// stood at that moment rather than when the frame was captured; only the frame number was put back
/// to the image's. The read-back runs on the render thread up to a frame behind the game thread, so
/// under a camera moved between synchronous ticks the header already carried the next frame's pose.
/// Measured on Bahonar (2026-09-30), 89 of 90 image headers carried the pose commanded for the frame
/// after the image's, while the pixels were the image's own frame in all 90 and the snapshot of the
/// image's frame held the camera at the pose commanded for it (59 of 60, the one other frame not held
/// exactly). The server now takes the header's transform and clock in the call that captures the
/// frame (<c>ASensor::MakeCaptureHeader</c>). This check is what keeps a server without that fix, or a
/// regression of it, from putting a pose the image was not taken from into the record, and it is
/// what says so.</para>
///
/// <para><b>Which pose is used.</b> The snapshot of the image's own frame whenever the client holds
/// that frame exactly and the sensor is in it: that is the transform the world observer reported for
/// the frame the pixels show, the same snapshot the capture's truth records are read from. A header
/// further from it than the tolerance is counted in <see cref="HeaderDisagreed"/>, which a correct
/// server keeps at zero. When the frame is no longer held, or the sensor is not in it, the nearest
/// frame held would be the sensor at another instant, so the header's pose is used instead and
/// counted in <see cref="FromHeader"/>.</para>
///
/// <para>The two transforms are the same world transform read at the same instant when the server
/// is right, so the tolerance only has to absorb their single-precision encoding. A level with a
/// large-map manager is the exception: the snapshot's transform is global and the header's local, so
/// there the check would count every capture whose origin has been shifted.</para>
///
/// <para>A lookup in memory, never an RPC, so it is safe on any thread.</para>
/// </remarks>
public sealed class SensorPoseCheck
{
    /// <summary>How far apart, in metres, the header's location and the snapshot's may be.</summary>
    public const double DefaultToleranceMetres = 0.01;

    /// <summary>How far apart, in degrees, the header's axes and the snapshot's may point.</summary>
    public const double DefaultToleranceDegrees = 0.01;

    private readonly SnapshotFrameLookup _lookup;
    private readonly ActorId _sensor;
    private readonly double _toleranceMetres;
    private readonly double _toleranceDegrees;
    private long _fromSnapshot, _headerDisagreed, _fromHeader;

    /// <param name="lookup">The snapshots to read the sensor from, by frame:
    /// <see cref="CarlaClient.GetSnapshotFrame"/>.</param>
    /// <param name="sensor">The sensor actor whose images are checked.</param>
    /// <param name="toleranceMetres">How far apart the two locations may be;
    /// <see cref="DefaultToleranceMetres"/> by default.</param>
    /// <param name="toleranceDegrees">How far apart the two orientations may be;
    /// <see cref="DefaultToleranceDegrees"/> by default.</param>
    public SensorPoseCheck(SnapshotFrameLookup lookup, ActorId sensor,
                           double toleranceMetres = DefaultToleranceMetres,
                           double toleranceDegrees = DefaultToleranceDegrees)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;
        _sensor = sensor;
        _toleranceMetres = toleranceMetres;
        _toleranceDegrees = toleranceDegrees;
    }

    /// <summary>The sensor actor whose images are checked.</summary>
    public ActorId Sensor => _sensor;

    /// <summary>Images whose pose was read from the snapshot of their own frame.</summary>
    public long FromSnapshot => Interlocked.Read(ref _fromSnapshot);

    /// <summary>
    /// Of those, the images whose header carried a different pose from the snapshot's. Each is an
    /// image the header alone would have placed somewhere it was not taken from; a server that stamps
    /// the header at capture keeps this at zero.
    /// </summary>
    public long HeaderDisagreed => Interlocked.Read(ref _headerDisagreed);

    /// <summary>
    /// Images whose own frame the client no longer held, or held without the sensor in it, so their
    /// pose is the header's, unchecked.
    /// </summary>
    public long FromHeader => Interlocked.Read(ref _fromHeader);

    /// <summary>
    /// The pose the image of <paramref name="frame"/> was taken from: the sensor's transform in that
    /// frame's snapshot where the client holds it, otherwise <paramref name="header"/>.
    /// </summary>
    /// <param name="frame">The image's frame, from its header.</param>
    /// <param name="header">The transform in the image's header.</param>
    public Transform Resolve(ulong frame, Transform header)
    {
        IReadOnlyDictionary<ActorId, ActorSnapshot>? actors = _lookup(frame, out ulong served);
        if (actors is not null && served == frame && actors.TryGetValue(_sensor, out ActorSnapshot? held))
        {
            Interlocked.Increment(ref _fromSnapshot);
            if (!Agree(held.Transform, header, _toleranceMetres, _toleranceDegrees))
            {
                Interlocked.Increment(ref _headerDisagreed);
            }

            return held.Transform;
        }

        Interlocked.Increment(ref _fromHeader);
        return header;
    }

    /// <summary>
    /// Whether two transforms place a sensor at the same pose: locations within
    /// <paramref name="toleranceMetres"/>, and forward and up axes each within
    /// <paramref name="toleranceDegrees"/>.
    /// </summary>
    /// <remarks>
    /// Orientations are compared as axes, not as angles, because one orientation has more than one set
    /// of angles: a yaw of 180 is a yaw of -180, and looking straight down any yaw can be traded for
    /// roll. Those must not read as a disagreement.
    /// </remarks>
    public static bool Agree(Transform a, Transform b, double toleranceMetres, double toleranceDegrees)
    {
        double dx = (double)a.Location.X - b.Location.X;
        double dy = (double)a.Location.Y - b.Location.Y;
        double dz = (double)a.Location.Z - b.Location.Z;
        if (dx * dx + dy * dy + dz * dz > toleranceMetres * toleranceMetres)
        {
            return false;
        }

        var axesA = new RotationBasis(a.Rotation);
        var axesB = new RotationBasis(b.Rotation);
        return DegreesApart(axesA.Forward, axesB.Forward) <= toleranceDegrees
               && DegreesApart(axesA.Up, axesB.Up) <= toleranceDegrees;
    }

    // The angle between two unit vectors, from the chord between their tips rather than from their dot
    // product, which cannot resolve a hundredth of a degree in single precision.
    private static double DegreesApart(Vector3D a, Vector3D b)
    {
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y, dz = (double)a.Z - b.Z;
        double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return 2.0 * Math.Asin(Math.Min(1.0, chord / 2.0)) * 180.0 / Math.PI;
    }
}
