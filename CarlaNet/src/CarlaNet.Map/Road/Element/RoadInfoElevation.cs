// Source: carla/road/element/RoadInfoElevation.h
using CarlaNet.Map.Geom;

namespace CarlaNet.Map.Road.Element;

/// <summary>Elevation profile entry — a cubic polynomial keyed at <c>s</c>.</summary>
public sealed class RoadInfoElevation : RoadInfo
{
    /// <summary>(a + b·ds + c·ds² + d·ds³) with ds = s − record_s.</summary>
    public CubicPolynomial Polynomial { get; }

    public RoadInfoElevation(double s, double a, double b, double c, double d)
        : base(s)
    {
        Polynomial = new CubicPolynomial(a, b, c, d, s);
    }
}
