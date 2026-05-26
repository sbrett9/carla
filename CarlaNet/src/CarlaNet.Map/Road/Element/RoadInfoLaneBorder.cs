// Source: carla/road/element/RoadInfoLaneBorder.h
//
// Alternative to lane-width: defines the outer border of the lane directly.
// Lane borders and widths are mutually exclusive for a given lane.
using CarlaNet.Map.Geom;

namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneBorder : RoadInfo
{
    public CubicPolynomial Polynomial { get; }

    public RoadInfoLaneBorder(double s, double a, double b, double c, double d)
        : base(s)
    {
        Polynomial = new CubicPolynomial(a, b, c, d, s);
    }
}
