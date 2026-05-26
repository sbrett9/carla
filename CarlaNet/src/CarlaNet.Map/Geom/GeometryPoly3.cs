// Source: carla/road/element/Geometry.cpp (GeometryPoly3).
// t(u) = a + b*u + c*u^2 + d*u^3 — a cubic in u, evaluated densely at startup
// into (s, u, v, tangent) samples. PosFromDist binary-searches by s and lerps.
// Upstream uses boost::SegmentCloudRtree but the samples are 1D-indexed by s,
// so a sorted array works identically.
namespace CarlaNet.Map.Geom;

public sealed class GeometryPoly3 : Geometry
{
    private readonly CubicPolynomial _poly;
    private readonly double[] _samplesS;
    private readonly double[] _samplesU;
    private readonly double[] _samplesV;
    private readonly double[] _samplesT;

    public GeometryPoly3(
        double startOffset,
        double length,
        double heading,
        Location startPosition,
        double a, double b, double c, double d)
        : base(GeometryType.Poly3, startOffset, length, heading, startPosition)
    {
        A = a; B = b; C = c; D = d;
        _poly = new CubicPolynomial(a, b, c, d);
        (_samplesS, _samplesU, _samplesV, _samplesT) = PreComputeSpline();
    }

    public double A { get; }
    public double B { get; }
    public double C { get; }
    public double D { get; }

    public override DirectedPoint PosFromDist(double dist)
    {
        var (i1, i2) = FindBracket(_samplesS, dist);
        var s1 = _samplesS[i1];
        var s2 = _samplesS[i2];
        var rate = s2 == s1 ? 1.0 : (s2 - dist) / (s2 - s1);

        var u = rate * _samplesU[i1] + (1.0 - rate) * _samplesU[i2];
        var v = rate * _samplesV[i1] + (1.0 - rate) * _samplesV[i2];
        var tangent = Math.Atan(rate * _samplesT[i1] + (1.0 - rate) * _samplesT[i2]);

        var (rx, ry) = RotateByAngle(Heading, u, v);
        return new DirectedPoint(
            new Location(
                StartPosition.X + (float)rx,
                StartPosition.Y + (float)ry,
                StartPosition.Z),
            Heading + tangent);
    }

    public override (float S, float Distance) DistanceTo(Location p)
    {
        // Upstream returns (start_position.x, start_position.y) as a stub —
        // no analytical expression for a cubic-on-curve. Matched for parity.
        return (StartPosition.X, StartPosition.Y);
    }

    // Sample the polynomial at fixed delta-u steps and accumulate arc length s.
    // Returns four parallel arrays sorted by s (monotonic since ds = sqrt(du^2+dv^2) > 0).
    private (double[] s, double[] u, double[] v, double[] t) PreComputeSpline()
    {
        const double intervalSize = 0.3;
        const double deltaU = intervalSize;

        var sList = new List<double>(64);
        var uList = new List<double>(64);
        var vList = new List<double>(64);
        var tList = new List<double>(64);

        var currentU = 0.0;
        var currentS = 0.0;
        var lastU = 0.0;
        var lastV = _poly.Evaluate(currentU);

        // Seed with the initial sample at s=0.
        sList.Add(currentS);
        uList.Add(lastU);
        vList.Add(lastV);
        tList.Add(_poly.Tangent(currentU));

        while (currentS < Length + deltaU)
        {
            currentU += deltaU;
            var currentV = _poly.Evaluate(currentU);
            var du = currentU - lastU;
            var dv = currentV - lastV;
            currentS += Math.Sqrt(du * du + dv * dv);

            sList.Add(currentS);
            uList.Add(currentU);
            vList.Add(currentV);
            tList.Add(_poly.Tangent(currentU));

            lastU = currentU;
            lastV = currentV;
        }

        return (sList.ToArray(), uList.ToArray(), vList.ToArray(), tList.ToArray());
    }

    // Binary search for the segment [i, i+1] bracketing dist.
    // Clamps to the first/last segment if dist is out of range.
    internal static (int Lo, int Hi) FindBracket(double[] samplesS, double dist)
    {
        if (samplesS.Length < 2)
        {
            return (0, 0);
        }
        if (dist <= samplesS[0])
        {
            return (0, 1);
        }
        if (dist >= samplesS[^1])
        {
            return (samplesS.Length - 2, samplesS.Length - 1);
        }
        var lo = 0;
        var hi = samplesS.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (samplesS[mid] <= dist) lo = mid;
            else hi = mid;
        }
        return (lo, hi);
    }
}
