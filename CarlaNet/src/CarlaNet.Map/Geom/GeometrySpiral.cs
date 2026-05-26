// Source: carla/road/element/Geometry.cpp (GeometrySpiral).
// Clothoid: curvature varies linearly along arc-length. Evaluates by sampling
// the standard-spiral Fresnel integrals at s_o and s_o+dist, then translating /
// rotating into world coords.
namespace CarlaNet.Map.Geom;

public sealed class GeometrySpiral : Geometry
{
    public GeometrySpiral(
        double startOffset,
        double length,
        double heading,
        Location startPosition,
        double curveStart,
        double curveEnd)
        : base(GeometryType.Spiral, startOffset, length, heading, startPosition)
    {
        CurveStart = curveStart;
        CurveEnd = curveEnd;
    }

    public double CurveStart { get; }
    public double CurveEnd { get; }

    public override DirectedPoint PosFromDist(double dist)
    {
        dist = Clamp(dist, 0.0, Length);

        var curveDot = (CurveEnd - CurveStart) / Length;
        var sO = CurveStart / curveDot;
        var s = sO + dist;

        Fresnel.OdrSpiral(s, curveDot, out var x, out var y, out var t);
        Fresnel.OdrSpiral(sO, curveDot, out var xO, out var yO, out var tO);

        x -= xO;
        y -= yO;
        t -= tO;

        var (rx, ry) = RotateByAngle(Heading - tO, x, y);
        return new DirectedPoint(
            new Location(
                StartPosition.X + (float)rx,
                StartPosition.Y + (float)ry,
                StartPosition.Z),
            Heading + t);
    }

    public override (float S, float Distance) DistanceTo(Location p)
    {
        // Upstream has no analytical expression for a spiral; it returns the raw
        // (dx, dy) offset from start as a stub. Matched here for parity.
        return (p.X - StartPosition.X, p.Y - StartPosition.Y);
    }
}
