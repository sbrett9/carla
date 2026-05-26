// Source: carla/road/element/RoadInfoLaneMaterial.h
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneMaterial : RoadInfo
{
    public string Surface { get; }
    public double Friction { get; }
    public double Roughness { get; }

    public RoadInfoLaneMaterial(double s, string surface, double friction, double roughness)
        : base(s)
    {
        Surface = surface;
        Friction = friction;
        Roughness = roughness;
    }
}
