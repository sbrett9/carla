// Source: carla/road/element/Geometry.cpp (GeometryLine).
namespace CarlaNet.Map.Geom;

public sealed class GeometryLine : Geometry
{
    public GeometryLine(double startOffset, double length, double heading, Location startPosition)
        : base(GeometryType.Line, startOffset, length, heading, startPosition) { }

    public override DirectedPoint PosFromDist(double dist)
    {
        dist = Clamp(dist, 0.0, Length);
        var x = StartPosition.X + (float)(dist * Math.Cos(Heading));
        var y = StartPosition.Y + (float)(dist * Math.Sin(Heading));
        return new DirectedPoint(new Location(x, y, StartPosition.Z), Heading);
    }

    public override (float S, float Distance) DistanceTo(Location p)
    {
        var end = PosFromDist(Length).Location;
        return DistanceSegmentToPoint(p, StartPosition, end);
    }

    // Ported from carla::geom::Math::DistanceSegmentToPoint (Math.cpp).
    // Returns (arc-length from v, Euclidean distance from p to nearest segment point).
    private static (float S, float D) DistanceSegmentToPoint(Location p, Location v, Location w)
    {
        var dx = w.X - v.X;
        var dy = w.Y - v.Y;
        var l2 = dx * dx + dy * dy;
        if (l2 == 0f)
        {
            var d0 = MathF.Sqrt((p.X - v.X) * (p.X - v.X) + (p.Y - v.Y) * (p.Y - v.Y));
            return (0f, d0);
        }
        var l = MathF.Sqrt(l2);
        var dot = (p.X - v.X) * dx + (p.Y - v.Y) * dy;
        var t = dot / l2;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        var projX = v.X + t * dx;
        var projY = v.Y + t * dy;
        var dist = MathF.Sqrt((projX - p.X) * (projX - p.X) + (projY - p.Y) * (projY - p.Y));
        return (t * l, dist);
    }
}
