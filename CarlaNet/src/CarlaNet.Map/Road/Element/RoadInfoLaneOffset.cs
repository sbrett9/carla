// Source: carla/road/element/RoadInfoLaneOffset.h
//
// "The lane offset record defines a lateral shift of the lane reference line
//  (which is usually identical to the road reference line)." Used to model
//  inner-city / 2+1 layouts where the centerline shifts laterally.
using CarlaNet.Map.Geom;

namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneOffset : RoadInfo
{
    public CubicPolynomial Polynomial { get; }

    public RoadInfoLaneOffset(double s, double a, double b, double c, double d)
        : base(s)
    {
        Polynomial = new CubicPolynomial(a, b, c, d, s);
    }
}
