// Source: carla/road/element/Geometry.cpp (GeometryArc).
// Circular arc with constant curvature. radius = 1 / curvature; sign determines turn direction.
namespace CarlaNet.Map.Geom;

public sealed class GeometryArc : Geometry
{
    public GeometryArc(
        double startOffset,
        double length,
        double heading,
        Location startPosition,
        double curvature)
        : base(GeometryType.Arc, startOffset, length, heading, startPosition)
    {
        Curvature = curvature;
    }

    public double Curvature { get; }

    public override DirectedPoint PosFromDist(double dist)
    {
        dist = Clamp(dist, 0.0, Length);
        var radius = 1.0 / Curvature;
        const double piHalf = Math.PI / 2.0;
        var tangent = Heading;
        var x = StartPosition.X + (float)(radius * Math.Cos(tangent + piHalf));
        var y = StartPosition.Y + (float)(radius * Math.Sin(tangent + piHalf));
        tangent += dist * Curvature;
        x -= (float)(radius * Math.Cos(tangent + piHalf));
        y -= (float)(radius * Math.Sin(tangent + piHalf));
        return new DirectedPoint(new Location(x, y, StartPosition.Z), tangent);
    }

    public override (float S, float Distance) DistanceTo(Location p)
        => DistanceArcToPoint(p, StartPosition, (float)Length, (float)Heading, (float)Curvature);

    // Ported from carla::geom::Math::DistanceArcToPoint (Math.cpp).
    // The y/heading/curvature flip preserves upstream's Unreal-coords workaround.
    private static (float S, float D) DistanceArcToPoint(
        Location p, Location startPos, float length, float heading, float curvature)
    {
        // Upstream's "hacky" Unreal y-axis correction.
        var py = -p.Y;
        var sy = -startPos.Y;
        var px = p.X;
        var sx = startPos.X;
        heading = -heading;
        curvature = -curvature;

        // Algorithm requires positive curvature; mirror y if negative.
        if (curvature < 0f)
        {
            py = -py;
            sy = -sy;
            heading = -heading;
            curvature = -curvature;
        }

        // Translate p relative to start, then rotate by -heading.
        var dx = px - sx;
        var dy = py - sy;
        var c = MathF.Cos(-heading);
        var s = MathF.Sin(-heading);
        var rx = dx * c - dy * s;
        var ry = dx * s + dy * c;

        var radius = 1f / curvature;
        // Circle center is at (0, radius).
        if (rx == 0f && ry == radius)
        {
            return (0f, radius);
        }

        // Project onto the circle.
        var vx = rx;
        var vy = ry - radius;
        var len = MathF.Sqrt(vx * vx + vy * vy);
        var ux = vx / len;
        var uy = vy / len;
        var ix = ux * radius;
        var iy = uy * radius + radius;

        var lastPointAngle = length / radius;
        const float piHalf = MathF.PI / 2f;
        var angle = MathF.Atan2(iy - radius, ix) + piHalf;
        if (angle < 0f) angle += MathF.PI * 2f;

        if (angle <= lastPointAngle)
        {
            var d = MathF.Sqrt((ix - rx) * (ix - rx) + (iy - ry) * (iy - ry));
            return (angle * radius, d);
        }

        // Outside arc: pick whichever endpoint is closer.
        var startDist = MathF.Sqrt(rx * rx + ry * ry);
        var endX = radius * MathF.Cos(lastPointAngle - piHalf);
        var endY = radius * MathF.Sin(lastPointAngle - piHalf) + radius;
        var endDist = MathF.Sqrt((endX - rx) * (endX - rx) + (endY - ry) * (endY - ry));
        return startDist < endDist ? (0f, startDist) : (length, endDist);
    }
}
