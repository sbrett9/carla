using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>A point in a picture, pixels: <see cref="U"/> across from its left edge, <see cref="V"/> down
/// from its top edge. Written in the sidecar as <c>x y</c>.</summary>
public readonly record struct PixelPoint(double U, double V);

/// <summary>A corner of a vehicle's box as latitude and longitude, degrees WGS84, and height, meters
/// above the ellipsoid in the bare-earth convention of the record's own point.</summary>
public readonly record struct GeodeticCorner(double Lat, double Lon, double Hae);

/// <summary>
/// A vehicle's box as one capture holds it, written on the vehicle's record in the truth sidecar for a
/// vehicle whose box fell in the picture (<c>in_frame</c> of <c>wholly</c> or <c>partly</c>) and for no
/// other, as the owner ruled on 2026-10-06. Every value is a measurement from the frame's geometry -- the
/// box's eight corners, the camera's pinhole and the picture's edges -- by the fixed methods below, or a
/// fact of the body's pose; none rests on a pass mark.
/// </summary>
/// <param name="MinU">Left edge of the axis-aligned rectangle the eight projected corners span, pixels.</param>
/// <param name="MinV">Its top edge.</param>
/// <param name="MaxU">Its right edge.</param>
/// <param name="MaxV">Its bottom edge. The rectangle is not clipped to the picture, so it can extend past
/// an edge; <paramref name="Truncation"/> says how much of it does.</param>
/// <param name="Oriented">The minimum-area rectangle enclosing the eight projected corners: its four
/// corners, clockwise in the picture from the top-most, and of two at the same height the left one
/// (<see cref="CaptureBoxes.MinimumAreaRectangle"/>). Not clipped either.</param>
/// <param name="Truncation">The share of the axis-aligned rectangle's area outside the picture, 0 (none)
/// to 1.</param>
/// <param name="PitchDeg">The body's pitch from its transform, degrees, positive nose up.</param>
/// <param name="RollDeg">The body's roll from its transform, degrees, positive right side down.</param>
/// <param name="Corners">The box's eight corners, in <see cref="CaptureBoxes.CornerNames"/> order,
/// converted exactly as the record's own point is (<see cref="CaptureBoxes.GeodeticCorners"/>).</param>
public sealed record CaptureBox(
    double MinU,
    double MinV,
    double MaxU,
    double MaxV,
    IReadOnlyList<PixelPoint> Oriented,
    double Truncation,
    double PitchDeg,
    double RollDeg,
    IReadOnlyList<GeodeticCorner> Corners);

/// <summary>
/// Measures a vehicle's <see cref="CaptureBox"/> against a capture's picture and the world's
/// georeference.
/// </summary>
public static class CaptureBoxes
{
    /// <summary>
    /// The corners of <see cref="CaptureBox.Corners"/>, in order: the bottom face going around from the
    /// front left, then the top face the same way, so corner <c>n + 4</c> stands above corner <c>n</c>.
    /// Front is the body's forward axis, the way <c>heading_deg</c> points; left and right are as seen
    /// from the driver's seat facing forward; bottom and top are along the body's up axis.
    /// </summary>
    public static IReadOnlyList<string> CornerNames { get; } =
    [
        "front_left_bottom", "front_right_bottom", "back_right_bottom", "back_left_bottom",
        "front_left_top", "front_right_top", "back_right_top", "back_left_top",
    ];

    /// <summary>Each of <see cref="CornerNames"/> as an <see cref="OrientedBox.CornerFrom"/> index: bit 0
    /// set at the front, bit 1 on the right, bit 2 at the top.</summary>
    private static readonly int[] CornerIndex = [1, 3, 2, 0, 5, 7, 6, 4];

    /// <summary>Two corners of the oriented rectangle within this many pixels of each other in height are
    /// at the same height, where the top-most is chosen to start from.</summary>
    private const double SameHeightPx = 1e-3;

    /// <summary>The box of a vehicle whose box fell in the picture.</summary>
    /// <param name="camera">The camera the picture was taken by, at the pose it was taken from.</param>
    /// <param name="box">The vehicle's box placed in the world.</param>
    /// <param name="projection">That box's projection into the picture, in it wholly or partly.</param>
    /// <param name="corners">The box's eight corners as that projection placed them.</param>
    /// <param name="record">The vehicle's truth record, whose point the corners are converted as.</param>
    /// <param name="origin">The georeference origin the record's point was converted from.</param>
    internal static CaptureBox Of(in PinholeCamera camera, in OrientedBox box, BoxProjection projection,
                                  ReadOnlySpan<PixelPoint> corners, VehicleTelemetry record, GeoLocation origin)
    {
        if (!projection.IsInPicture)
            throw new ArgumentException("only a box that fell in the picture has a box to write", nameof(projection));

        Rotation tilt = record.ActorTransform.Rotation;
        return new CaptureBox(
            projection.MinU, projection.MinV, projection.MaxU, projection.MaxV,
            MinimumAreaRectangle(corners),
            Truncation(projection, camera.Width, camera.Height),
            Degrees(tilt.Pitch), Degrees(tilt.Roll),
            GeodeticCorners(box, origin, record.HeightAlignOffset));
    }

    /// <summary>
    /// The share of a projection's axis-aligned rectangle that lies outside a picture, 0 to 1: one less
    /// the share of each axis's span inside the picture, multiplied, which is the share of the area. A
    /// span with no width counts as wholly inside along that axis where it lies within the picture.
    /// </summary>
    public static double Truncation(BoxProjection projection, int width, int height)
    {
        if (!projection.HasFootprint)
            throw new ArgumentException("a box with a corner at or behind the lens has no rectangle", nameof(projection));
        double inside = Inside(projection.MinU, projection.MaxU, width) * Inside(projection.MinV, projection.MaxV, height);
        return Math.Clamp(1.0 - inside, 0.0, 1.0);
    }

    private static double Inside(double min, double max, double size)
    {
        double span = max - min;
        if (!(span > 0.0))
            return min >= 0.0 && min <= size ? 1.0 : 0.0;
        double overlap = Math.Min(max, size) - Math.Max(min, 0.0);
        return Math.Clamp(overlap / span, 0.0, 1.0);
    }

    /// <summary>
    /// The minimum-area rectangle enclosing a set of points in a picture: its four corners, clockwise in
    /// the picture (across to the right, then down) from the top-most, and of two at the same height the
    /// left one.
    /// </summary>
    /// <remarks>
    /// The rectangle of least area enclosing a convex polygon has a side on one of the polygon's edges
    /// (Freeman and Shapira, 1975), so the convex hull's every edge is tried in turn: each points the
    /// way of one pair of the rectangle's sides, the points' extents along that edge and across it give
    /// the rectangle, and the smallest wins. Points all on one line give a rectangle of no width, and
    /// points all at one place one of no size; both are returned as four corners all the same.
    /// </remarks>
    public static PixelPoint[] MinimumAreaRectangle(ReadOnlySpan<PixelPoint> points)
    {
        if (points.IsEmpty)
            throw new ArgumentException("a rectangle encloses at least one point", nameof(points));

        List<PixelPoint> hull = ConvexHull(points);
        PixelPoint[] rectangle;
        if (hull.Count == 1)
        {
            rectangle = [hull[0], hull[0], hull[0], hull[0]];
        }
        else if (hull.Count == 2)
        {
            rectangle = [hull[0], hull[1], hull[1], hull[0]];
        }
        else
        {
            rectangle = new PixelPoint[4];
            double least = double.PositiveInfinity;
            for (int edge = 0; edge < hull.Count; edge++)
            {
                PixelPoint from = hull[edge], to = hull[(edge + 1) % hull.Count];
                double length = Math.Sqrt(((to.U - from.U) * (to.U - from.U)) + ((to.V - from.V) * (to.V - from.V)));
                double alongU = (to.U - from.U) / length, alongV = (to.V - from.V) / length;
                double acrossU = -alongV, acrossV = alongU;

                double minAlong = double.PositiveInfinity, maxAlong = double.NegativeInfinity;
                double minAcross = double.PositiveInfinity, maxAcross = double.NegativeInfinity;
                foreach (PixelPoint point in hull)
                {
                    double along = (point.U * alongU) + (point.V * alongV);
                    double across = (point.U * acrossU) + (point.V * acrossV);
                    minAlong = Math.Min(minAlong, along);
                    maxAlong = Math.Max(maxAlong, along);
                    minAcross = Math.Min(minAcross, across);
                    maxAcross = Math.Max(maxAcross, across);
                }

                double area = (maxAlong - minAlong) * (maxAcross - minAcross);
                if (area < least)
                {
                    least = area;
                    rectangle[0] = At(minAlong, minAcross);
                    rectangle[1] = At(maxAlong, minAcross);
                    rectangle[2] = At(maxAlong, maxAcross);
                    rectangle[3] = At(minAlong, maxAcross);
                }

                PixelPoint At(double along, double across) =>
                    new((along * alongU) + (across * acrossU), (along * alongV) + (across * acrossV));
            }
        }

        return Clockwise(rectangle);
    }

    /// <summary>
    /// The convex hull of the points by Andrew's monotone chain, with no point repeated and none in the
    /// middle of an edge: one point where all are at one place, two where all are on one line.
    /// </summary>
    private static List<PixelPoint> ConvexHull(ReadOnlySpan<PixelPoint> points)
    {
        PixelPoint[] sorted = points.ToArray();
        Array.Sort(sorted, (a, b) => a.U != b.U ? a.U.CompareTo(b.U) : a.V.CompareTo(b.V));

        var hull = new List<PixelPoint>((2 * sorted.Length) + 1);
        foreach (PixelPoint point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0.0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }

        int lower = hull.Count + 1;
        for (int index = sorted.Length - 2; index >= 0; index--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], sorted[index]) <= 0.0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(sorted[index]);
        }

        // The chain ends where it began.
        hull.RemoveAt(hull.Count - 1);
        if (hull.Count == 2 && hull[0] == hull[1])
            hull.RemoveAt(1);
        return hull;
    }

    private static double Cross(PixelPoint origin, PixelPoint a, PixelPoint b) =>
        ((a.U - origin.U) * (b.V - origin.V)) - ((a.V - origin.V) * (b.U - origin.U));

    /// <summary>
    /// A rectangle's four corners, given in order around it either way, clockwise in the picture from
    /// the top-most. With V down the picture, clockwise as the picture is seen is a positive signed area.
    /// </summary>
    private static PixelPoint[] Clockwise(PixelPoint[] rectangle)
    {
        double signed = 0.0;
        for (int index = 0; index < 4; index++)
        {
            PixelPoint a = rectangle[index], b = rectangle[(index + 1) % 4];
            signed += (a.U * b.V) - (b.U * a.V);
        }

        if (signed < 0.0)
            Array.Reverse(rectangle);

        int first = 0;
        for (int index = 1; index < 4; index++)
        {
            PixelPoint corner = rectangle[index], best = rectangle[first];
            if (corner.V < best.V - SameHeightPx
                || (Math.Abs(corner.V - best.V) <= SameHeightPx && corner.U < best.U))
            {
                first = index;
            }
        }

        return [rectangle[first], rectangle[(first + 1) % 4], rectangle[(first + 2) % 4], rectangle[(first + 3) % 4]];
    }

    /// <summary>
    /// A box's eight corners in <see cref="CornerNames"/> order, each converted from the world exactly as
    /// a truth record's own point is (<c>VehicleTelemetryService</c>): <see cref="Geodesy.CarlaLocalToGeodetic(GeoLocation, double, double, double)"/>
    /// from the georeference origin, less the height-align offset taken off the record's point. One offset
    /// is taken off every corner, so the box is the body's box shifted whole, never warped, and a corner
    /// and the point agree: corners at the point's height stand at its <c>hae</c>.
    /// </summary>
    /// <param name="actorTransform">The vehicle's pose, CARLA frame.</param>
    /// <param name="boundingBox">The vehicle's bounding box in its own frame.</param>
    /// <param name="origin">The georeference origin the record's point was converted from.</param>
    /// <param name="heightAlignOffset">The height-align offset taken off the record's point, meters
    /// (<see cref="VehicleTelemetry.HeightAlignOffset"/>).</param>
    public static GeodeticCorner[] GeodeticCorners(Transform actorTransform, BoundingBox boundingBox,
                                                   GeoLocation origin, double heightAlignOffset)
        => GeodeticCorners(new OrientedBox(actorTransform, boundingBox), origin, heightAlignOffset);

    internal static GeodeticCorner[] GeodeticCorners(in OrientedBox box, GeoLocation origin, double heightAlignOffset)
    {
        var corners = new GeodeticCorner[8];
        for (int index = 0; index < corners.Length; index++)
        {
            (double x, double y, double z) = box.CornerFrom(CornerIndex[index], 0.0, 0.0, 0.0);
            GeoLocation geo = Geodesy.CarlaLocalToGeodetic(origin, x, y, z);
            corners[index] = new GeodeticCorner(geo.Latitude, geo.Longitude, geo.Altitude - heightAlignOffset);
        }

        return corners;
    }

    /// <summary>An angle as degrees in (-180, 180].</summary>
    private static double Degrees(float angle)
    {
        double wrapped = Math.IEEERemainder(angle, 360.0);
        return wrapped <= -180.0 ? wrapped + 360.0 : wrapped;
    }
}
