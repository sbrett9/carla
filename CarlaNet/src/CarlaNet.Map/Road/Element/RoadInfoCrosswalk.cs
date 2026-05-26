// Source: carla/road/element/RoadInfoCrosswalk.h
namespace CarlaNet.Map.Road.Element;

/// <summary>One vertex of a crosswalk polygon, in road-local (u,v,z) coords.</summary>
public readonly record struct CrosswalkPoint(double U, double V, double Z);

public sealed class RoadInfoCrosswalk : RoadInfo
{
    public string Name { get; }
    public double T { get; }
    public double ZOffset { get; }
    public double Heading { get; }
    public double Pitch { get; }
    public double Roll { get; }
    public string Orientation { get; }
    public double Width { get; }
    public double Length { get; }
    public List<CrosswalkPoint> Points { get; }

    public RoadInfoCrosswalk(
        double s,
        string name,
        double t,
        double zOffset,
        double hdg,
        double pitch,
        double roll,
        string orientation,
        double width,
        double length,
        List<CrosswalkPoint> points)
        : base(s)
    {
        Name = name;
        T = t;
        ZOffset = zOffset;
        Heading = hdg;
        Pitch = pitch;
        Roll = roll;
        Orientation = orientation;
        Width = width;
        Length = length;
        Points = points;
    }
}
