// Source: carla/road/element/Geometry.h (abstract Geometry base class).
// Each subclass implements PosFromDist(s) -> DirectedPoint and DistanceTo(p) -> (s, dist).
// All math is in double precision; only narrow to float when writing into Location.
namespace CarlaNet.Map.Geom;

public abstract class Geometry
{
    protected Geometry(
        GeometryType type,
        double startOffset,
        double length,
        double heading,
        Location startPosition)
    {
        Type = type;
        Length = length;
        StartOffset = startOffset;
        Heading = heading;
        StartPosition = startPosition;
    }

    public GeometryType Type { get; }
    public double Length { get; }
    public double StartOffset { get; }
    public double Heading { get; }
    public Location StartPosition { get; }

    public abstract DirectedPoint PosFromDist(double dist);

    /// Returns (s_along_curve, perpendicular_distance) to the nearest point on this geometry.
    public abstract (float S, float Distance) DistanceTo(Location p);

    // Shared helper used by Poly3 / ParamPoly3 to rotate the local (u, v) frame to world.
    protected static (double X, double Y) RotateByAngle(double angle, double x, double y)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return (x * cos - y * sin, y * cos + x * sin);
    }

    protected static double Clamp(double value, double min, double max)
        => value < min ? min : (value > max ? max : value);
}
