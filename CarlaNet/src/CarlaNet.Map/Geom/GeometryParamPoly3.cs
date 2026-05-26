// Source: carla/road/element/Geometry.cpp (GeometryParamPoly3).
// Two parametric cubics (u(p), v(p)) with p ∈ [0, 1] (or [0, length] if _arcLength).
// As with Poly3, replace the boost SegmentCloudRtree with a sorted-by-s array.
namespace CarlaNet.Map.Geom;

public sealed class GeometryParamPoly3 : Geometry
{
    private readonly CubicPolynomial _polyU;
    private readonly CubicPolynomial _polyV;
    private readonly bool _arcLength;

    private readonly double[] _samplesS;
    private readonly double[] _samplesU;
    private readonly double[] _samplesV;
    private readonly double[] _samplesTu;
    private readonly double[] _samplesTv;

    public GeometryParamPoly3(
        double startOffset,
        double length,
        double heading,
        Location startPosition,
        double aU, double bU, double cU, double dU,
        double aV, double bV, double cV, double dV,
        bool arcLength)
        : base(GeometryType.Poly3Param, startOffset, length, heading, startPosition)
    {
        AU = aU; BU = bU; CU = cU; DU = dU;
        AV = aV; BV = bV; CV = cV; DV = dV;
        _polyU = new CubicPolynomial(aU, bU, cU, dU);
        _polyV = new CubicPolynomial(aV, bV, cV, dV);
        _arcLength = arcLength;
        (_samplesS, _samplesU, _samplesV, _samplesTu, _samplesTv) = PreComputeSpline();
    }

    public double AU { get; }
    public double BU { get; }
    public double CU { get; }
    public double DU { get; }
    public double AV { get; }
    public double BV { get; }
    public double CV { get; }
    public double DV { get; }

    public override DirectedPoint PosFromDist(double dist)
    {
        var (i1, i2) = GeometryPoly3.FindBracket(_samplesS, dist);
        var s1 = _samplesS[i1];
        var s2 = _samplesS[i2];
        var rate = s2 == s1 ? 1.0 : (s2 - dist) / (s2 - s1);

        var u = rate * _samplesU[i1] + (1.0 - rate) * _samplesU[i2];
        var v = rate * _samplesV[i1] + (1.0 - rate) * _samplesV[i2];
        var tu = rate * _samplesTu[i1] + (1.0 - rate) * _samplesTu[i2];
        var tv = rate * _samplesTv[i1] + (1.0 - rate) * _samplesTv[i2];
        var tangent = Math.Atan2(tv, tu);

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
        // No analytical expression; match upstream's stub.
        return (StartPosition.X, StartPosition.Y);
    }

    private (double[] s, double[] u, double[] v, double[] tu, double[] tv) PreComputeSpline()
    {
        const double intervalSize = 0.5;
        var numberIntervals = Math.Max((int)(Length / intervalSize), 5);
        var deltaP = 1.0 / numberIntervals;
        if (_arcLength)
        {
            deltaP *= Length;
        }

        var capacity = numberIntervals + 1;
        var sList = new List<double>(capacity);
        var uList = new List<double>(capacity);
        var vList = new List<double>(capacity);
        var tuList = new List<double>(capacity);
        var tvList = new List<double>(capacity);

        var paramP = 0.0;
        var currentS = 0.0;
        var lastU = _polyU.Evaluate(paramP);
        var lastV = _polyV.Evaluate(paramP);

        // Seed with the initial sample at s=0.
        sList.Add(currentS);
        uList.Add(lastU);
        vList.Add(lastV);
        tuList.Add(_polyU.Tangent(paramP));
        tvList.Add(_polyV.Tangent(paramP));

        for (var i = 0; i < numberIntervals; i++)
        {
            paramP += deltaP;
            var currentU = _polyU.Evaluate(paramP);
            var currentV = _polyV.Evaluate(paramP);
            var du = currentU - lastU;
            var dv = currentV - lastV;
            currentS += Math.Sqrt(du * du + dv * dv);

            sList.Add(currentS);
            uList.Add(currentU);
            vList.Add(currentV);
            tuList.Add(_polyU.Tangent(paramP));
            tvList.Add(_polyV.Tangent(paramP));

            lastU = currentU;
            lastV = currentV;

            if (currentS > Length)
            {
                break;
            }
        }

        return (sList.ToArray(), uList.ToArray(), vList.ToArray(), tuList.ToArray(), tvList.ToArray());
    }
}
