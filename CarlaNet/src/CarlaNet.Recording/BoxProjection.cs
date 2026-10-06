using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>
/// Where a vehicle's box fell against a camera's picture: a fact of the frame's geometry, read off the
/// box's eight corners projected through the camera's pinhole. The only lines it is read against are
/// the picture's edges and the camera's near plane; nothing in it is a judgement of the vehicle.
/// </summary>
public enum InFrame
{
    /// <summary>Every corner projects inside the picture. Written <c>in_frame="wholly"</c>.</summary>
    Wholly,

    /// <summary>The corners' rectangle crosses an edge of the picture, so part of the box is in it and
    /// part is not. Written <c>in_frame="partly"</c>.</summary>
    Partly,

    /// <summary>The corners' rectangle lies wholly outside the picture. Written <c>in_frame="none"</c>.</summary>
    None,

    /// <summary>A corner of the box is at or behind the lens, so the box has no projection: a vehicle
    /// on top of the camera, not a subject of it. Written <c>in_frame="behind_camera"</c>.</summary>
    BehindCamera,
}

/// <summary>
/// A camera as a pinhole: the pose its picture was taken from and the intrinsics of the picture --
/// square pixels, the principal point at the picture's centre, the focal length from the picture's
/// width and horizontal field of view. The one convention every projection in this project agrees on:
/// the intrinsics the recorded sensor pose carries, the occlusion sampler and the viewer's
/// pixel-to-world picker.
/// </summary>
public readonly struct PinholeCamera
{
    /// <param name="pose">Where the camera stood and looked, CARLA frame.</param>
    /// <param name="width">The picture's width in pixels.</param>
    /// <param name="height">The picture's height in pixels.</param>
    /// <param name="hFovDeg">The picture's horizontal field of view, degrees, between 0 and 180 exclusive.</param>
    public PinholeCamera(Transform pose, int width, int height, double hFovDeg)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "a picture is at least one pixel wide");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "a picture is at least one pixel high");
        if (!(hFovDeg > 0.0 && hFovDeg < 180.0))
            throw new ArgumentOutOfRangeException(nameof(hFovDeg), hFovDeg,
                                                  "a horizontal field of view is between 0 and 180 degrees, exclusive");
        Pose = pose;
        Width = width;
        Height = height;
        HFovDeg = hFovDeg;
        Basis = new RotationBasis(pose.Rotation);
        Focal = width / (2.0 * Math.Tan(hFovDeg * Math.PI / 360.0));
        CentreX = width / 2.0;
        CentreY = height / 2.0;
    }

    public Transform Pose { get; }
    public int Width { get; }
    public int Height { get; }
    public double HFovDeg { get; }

    /// <summary>The camera's forward, right and up axes in the world.</summary>
    public RotationBasis Basis { get; }

    /// <summary>Focal length in pixels.</summary>
    public double Focal { get; }

    /// <summary>The principal point: the picture's centre.</summary>
    public double CentreX { get; }
    public double CentreY { get; }
}

/// <summary>
/// A vehicle's box projected into a picture: where it fell, the pixel rectangle its eight corners span
/// and how large it appears. The rectangle is the full footprint, the part outside the picture
/// included, and is meaningless with a corner at or behind the lens (<see cref="HasFootprint"/>).
/// </summary>
public readonly record struct BoxProjection(InFrame InFrame, double MinU, double MaxU, double MinV, double MaxV)
{
    /// <summary>Whether the box has a rectangle at all: none with a corner at or behind the lens.</summary>
    public bool HasFootprint => InFrame != InFrame.BehindCamera;

    /// <summary>Whether any of the box is in the picture.</summary>
    public bool IsInPicture => InFrame is InFrame.Wholly or InFrame.Partly;

    /// <summary>How wide the box appears, in pixels: its full footprint, the part outside the picture
    /// included, so it reads as "how big does this vehicle look" rather than "how much of it is on
    /// screen". 0 where there is no footprint.</summary>
    public int ApparentWidthPx => HasFootprint ? (int)Math.Round(MaxU - MinU) : 0;

    /// <summary>How tall the box appears, in pixels. See <see cref="ApparentWidthPx"/>.</summary>
    public int ApparentHeightPx => HasFootprint ? (int)Math.Round(MaxV - MinV) : 0;
}

/// <summary>
/// A vehicle's box placed in the world: its centre, its three axes and its half-extents along them,
/// from the actor's pose and the box's own placement in the actor's frame.
/// </summary>
internal readonly struct OrientedBox
{
    public readonly double X, Y, Z;
    public readonly Vector3D AxisX, AxisY, AxisZ;
    public readonly double ExtentX, ExtentY, ExtentZ;

    public OrientedBox(Transform actorTransform, BoundingBox box)
    {
        // The box's frame is the actor's, offset and rotated by the box's own local placement.
        var actor = new RotationBasis(actorTransform.Rotation);
        var local = new RotationBasis(box.Rotation);
        var offset = actor.Rotate(new Vector3D(box.Location.X, box.Location.Y, box.Location.Z));
        X = actorTransform.Location.X + offset.X;
        Y = actorTransform.Location.Y + offset.Y;
        Z = actorTransform.Location.Z + offset.Z;
        AxisX = actor.Rotate(local.Forward);
        AxisY = actor.Rotate(local.Right);
        AxisZ = actor.Rotate(local.Up);
        ExtentX = box.Extent.X;
        ExtentY = box.Extent.Y;
        ExtentZ = box.Extent.Z;
    }

    /// <summary>Corner <paramref name="corner"/> (0 to 7, one bit per axis) relative to a point in the world.</summary>
    public (double X, double Y, double Z) CornerFrom(int corner, double fromX, double fromY, double fromZ)
    {
        double sx = (corner & 1) == 0 ? -ExtentX : ExtentX;
        double sy = (corner & 2) == 0 ? -ExtentY : ExtentY;
        double sz = (corner & 4) == 0 ? -ExtentZ : ExtentZ;
        return (X + AxisX.X * sx + AxisY.X * sy + AxisZ.X * sz - fromX,
                Y + AxisX.Y * sx + AxisY.Y * sy + AxisZ.Y * sz - fromY,
                Z + AxisX.Z * sx + AxisY.Z * sy + AxisZ.Z * sz - fromZ);
    }
}

/// <summary>
/// Projects a vehicle's oriented box through a pinhole camera and says where it fell against the
/// picture. Needs only the camera's pose, the picture's size and its field of view, so it runs for
/// every capture whether or not a depth camera is attached; the occlusion sampler
/// (<see cref="OcclusionEstimator"/>) works from the same projection.
/// </summary>
public static class BoxProjector
{
    /// <summary>Points nearer than this to the camera along its axis are treated as being at or behind
    /// the lens: their projection is meaningless and dividing by that depth would blow up.</summary>
    public const double NearPlaneMetres = 0.1;

    /// <summary>The sidecar's word for where a box fell.</summary>
    public static string SidecarValue(InFrame inFrame) => inFrame switch
    {
        InFrame.Wholly => "wholly",
        InFrame.Partly => "partly",
        InFrame.None => "none",
        InFrame.BehindCamera => "behind_camera",
        _ => throw new ArgumentOutOfRangeException(nameof(inFrame), inFrame, "not a place a box falls"),
    };

    /// <summary>Where a vehicle's box falls against the camera's picture, and the rectangle it spans.</summary>
    /// <param name="camera">The camera the picture was taken by, at the pose it was taken from.</param>
    /// <param name="actorTransform">The vehicle's pose, CARLA frame.</param>
    /// <param name="box">The vehicle's bounding box in its own frame.</param>
    public static BoxProjection Project(in PinholeCamera camera, Transform actorTransform, BoundingBox box)
        => Project(camera, new OrientedBox(actorTransform, box));

    internal static BoxProjection Project(in PinholeCamera camera, in OrientedBox box)
    {
        RotationBasis axes = camera.Basis;
        double camX = camera.Pose.Location.X, camY = camera.Pose.Location.Y, camZ = camera.Pose.Location.Z;

        // Footprint of the box in pixels, from its eight corners.
        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            (double wx, double wy, double wz) = box.CornerFrom(corner, camX, camY, camZ);
            double forward = wx * axes.Forward.X + wy * axes.Forward.Y + wz * axes.Forward.Z;
            // A box with a corner at or behind the lens has no well-defined footprint.
            if (forward <= NearPlaneMetres)
                return new BoxProjection(InFrame.BehindCamera, double.NaN, double.NaN, double.NaN, double.NaN);
            double right = wx * axes.Right.X + wy * axes.Right.Y + wz * axes.Right.Z;
            double up = wx * axes.Up.X + wy * axes.Up.Y + wz * axes.Up.Z;

            double u = camera.CentreX + camera.Focal * right / forward;
            double v = camera.CentreY - camera.Focal * up / forward;
            if (u < minU) minU = u;
            if (u > maxU) maxU = u;
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
        }

        // The picture spans 0 to Width across and 0 to Height down. A rectangle that only touches an
        // edge from outside shows no pixel, so it is outside.
        InFrame where =
            maxU <= 0.0 || minU >= camera.Width || maxV <= 0.0 || minV >= camera.Height ? InFrame.None
            : minU >= 0.0 && maxU <= camera.Width && minV >= 0.0 && maxV <= camera.Height ? InFrame.Wholly
            : InFrame.Partly;
        return new BoxProjection(where, minU, maxU, minV, maxV);
    }

    /// <summary>
    /// Every record marked with where its box fell against the camera's picture and how large it
    /// appears there, from the pose the record's own geometry was read at.
    /// </summary>
    /// <param name="records">The capture's truth records.</param>
    /// <param name="camera">The camera the capture's picture was taken by, at the pose it was taken from.</param>
    public static IReadOnlyList<VehicleTelemetry> Mark(IReadOnlyList<VehicleTelemetry> records, in PinholeCamera camera)
    {
        ArgumentNullException.ThrowIfNull(records);
        var marked = new List<VehicleTelemetry>(records.Count);
        foreach (VehicleTelemetry record in records)
        {
            BoxProjection projection = Project(camera, record.ActorTransform, record.BoundingBox);
            marked.Add(record with
            {
                InFrame = projection.InFrame,
                ApparentWidthPx = projection.ApparentWidthPx,
                ApparentHeightPx = projection.ApparentHeightPx,
            });
        }

        return marked;
    }
}
