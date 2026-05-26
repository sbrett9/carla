// Source: carla/road/element/RoadInfoGeometry.h
using CarlaNet.Map.Geom;

namespace CarlaNet.Map.Road.Element;

/// <summary>
/// Wraps one of the five OpenDRIVE planView geometry primitives (Line/Arc/Spiral/Poly3/ParamPoly3).
/// </summary>
public sealed class RoadInfoGeometry : RoadInfo
{
    /// <summary>The underlying curve segment. Polymorphic — see <see cref="Geometry"/>.</summary>
    public Geometry Geometry { get; }

    public RoadInfoGeometry(double s, Geometry geometry)
        : base(s)
    {
        Geometry = geometry ?? throw new System.ArgumentNullException(nameof(geometry));
    }
}
