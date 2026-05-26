// Source: carla/road/element/RoadInfoLaneWidth.h
//
// Per-lane width as a cubic polynomial in (s - record_s). At least one record
// is required per lane (except the zero-width center lane).
using CarlaNet.Map.Geom;

namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneWidth : RoadInfo
{
    public CubicPolynomial Polynomial { get; }

    public RoadInfoLaneWidth(double s, double a, double b, double c, double d)
        : base(s)
    {
        Polynomial = new CubicPolynomial(a, b, c, d, s);
    }
}
