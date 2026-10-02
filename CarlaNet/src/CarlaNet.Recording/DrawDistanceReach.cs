using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>
/// Where a vehicle stood against the draw distance the recording camera's image was rendered under.
/// </summary>
/// <remarks>
/// <para>A SUMO drive can be run with a draw distance (an optional performance control, off by
/// default): every vehicle keeps its body, its pose and its truth, and a body farther than the
/// distance from a camera is not drawn in that camera's image. The truth record of such a vehicle
/// travels with that camera's capture all the same, because the vehicle is in the world; this says it
/// is not in the picture, so it is never read as a vehicle the image shows.</para>
///
/// <para><b>The renderer's own rule, from the vehicle's bounding box.</b> The engine culls each
/// primitive per view where the nearest point of the primitive's bounding sphere is farther from the
/// view's origin than its maximum draw distance (<c>r.DistanceCullToSphereEdge</c>, on by default),
/// and a lamp where its light's centre is. A vehicle's meshes and lamps lie inside the sphere of its
/// bounding box, so a vehicle whose sphere lies wholly beyond the distance has nothing drawn
/// (<see cref="Beyond"/>), and one whose sphere lies wholly inside it has everything drawn
/// (<see cref="Inside"/>). Where the distance falls across the sphere the body may be drawn without
/// the parts of it that lie beyond (<see cref="Partly"/>): its largest mesh, whose sphere is about
/// the box's, is drawn, a wheel or a lamp beyond the distance may not be. The engine's
/// <c>r.ViewDistanceScale</c> multiplies every draw distance; it is 1 at the server's default
/// quality, and the rule is taken at 1.</para>
/// </remarks>
public enum DrawDistanceReach
{
    /// <summary>
    /// Wholly inside the draw distance, or no draw distance was in force. The sidecar writes nothing
    /// for it.
    /// </summary>
    Inside,

    /// <summary>
    /// The draw distance falls across the vehicle's bounding sphere, so the image may show its body
    /// without the parts of it beyond the distance. Written <c>beyond_draw_distance="partly"</c>.
    /// </summary>
    Partly,

    /// <summary>
    /// Wholly beyond the draw distance: the image shows nothing of it. Written
    /// <c>beyond_draw_distance="wholly"</c>, and never measured for occlusion, since a depth capture
    /// drawn under the same distance does not show it either.
    /// </summary>
    Beyond,
}

/// <summary>Marks a capture's truth records against the draw distance its image was rendered under.</summary>
public static class DrawDistanceCheck
{
    /// <summary>The sidecar's word for each reach that is written, or null for one that is not.</summary>
    public static string? SidecarValue(DrawDistanceReach reach) => reach switch
    {
        DrawDistanceReach.Partly => "partly",
        DrawDistanceReach.Beyond => "wholly",
        _ => null,
    };

    /// <summary>
    /// Whether a record's vehicle may be measured for occlusion against a depth capture of the same
    /// view: not where it lies wholly beyond the draw distance, since the depth capture, drawn under
    /// the same distance, shows the ground behind it and would report it unobstructed.
    /// </summary>
    public static bool MayBeMeasuredForOcclusion(VehicleTelemetry record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.DrawDistance != DrawDistanceReach.Beyond;
    }

    /// <summary>
    /// Where one vehicle stood against a draw distance seen from a camera, and how far the centre of
    /// its bounding box was from the camera, metres.
    /// </summary>
    /// <param name="record">The vehicle's truth record, with the transform and box it was built from.</param>
    /// <param name="camera">Where the camera stood, CARLA frame.</param>
    /// <param name="drawDistanceMetres">The draw distance, metres.</param>
    public static (DrawDistanceReach Reach, double RangeMetres) Of(VehicleTelemetry record, Location camera,
                                                                    double drawDistanceMetres)
    {
        ArgumentNullException.ThrowIfNull(record);
        Transform pose = record.ActorTransform;
        BoundingBox box = record.BoundingBox;
        Vector3D offset = new RotationBasis(pose.Rotation)
            .Rotate(new Vector3D(box.Location.X, box.Location.Y, box.Location.Z));
        double dx = pose.Location.X + offset.X - camera.X;
        double dy = pose.Location.Y + offset.Y - camera.Y;
        double dz = pose.Location.Z + offset.Z - camera.Z;
        double range = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        double radius = Math.Sqrt(((double)box.Extent.X * box.Extent.X) + ((double)box.Extent.Y * box.Extent.Y)
                                  + ((double)box.Extent.Z * box.Extent.Z));
        DrawDistanceReach reach = range - radius > drawDistanceMetres
            ? DrawDistanceReach.Beyond
            : range + radius > drawDistanceMetres
                ? DrawDistanceReach.Partly
                : DrawDistanceReach.Inside;
        return (reach, range);
    }

    /// <summary>
    /// Every record marked with where its vehicle stood against the draw distance, seen from the
    /// camera; the records as they were where no distance was in force.
    /// </summary>
    /// <param name="records">The capture's truth records.</param>
    /// <param name="camera">Where the camera stood when it took the image, CARLA frame.</param>
    /// <param name="drawDistanceMetres">The draw distance the image was rendered under, or null for none.</param>
    /// <param name="beyond">How many records lie wholly beyond it.</param>
    /// <param name="partly">How many it falls across.</param>
    public static IReadOnlyList<VehicleTelemetry> Mark(IReadOnlyList<VehicleTelemetry> records, Location camera,
                                                       double? drawDistanceMetres, out int beyond, out int partly)
    {
        ArgumentNullException.ThrowIfNull(records);
        beyond = 0;
        partly = 0;
        if (drawDistanceMetres is not { } distance)
        {
            return records;
        }

        var marked = new List<VehicleTelemetry>(records.Count);
        foreach (VehicleTelemetry record in records)
        {
            (DrawDistanceReach reach, double range) = Of(record, camera, distance);
            if (reach == DrawDistanceReach.Beyond)
            {
                beyond++;
            }
            else if (reach == DrawDistanceReach.Partly)
            {
                partly++;
            }

            marked.Add(record with { DrawDistance = reach, CameraRangeMetres = range });
        }

        return marked;
    }
}
