// Source: carla/road/Object.h
//
// Generic OpenDRIVE <object>: walls, crosswalks, props. Upstream defines only
// private fields with no public accessors; we expose them as read/write
// properties so the parser (Wave 2) can fill them in.
namespace CarlaNet.Map.Road.Object;

public sealed class RoadObject
{
    public ObjId Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double S { get; set; }
    public double T { get; set; }
    public double ZOffset { get; set; }
    public double ValidLength { get; set; }
    public string Orientation { get; set; } = string.Empty;

    /// <summary>Note: upstream spelling is "_lenght" (typo). We preserve the corrected name here.</summary>
    public double Length { get; set; }

    public double Width { get; set; }
    public double Heading { get; set; }
    public double Pitch { get; set; }
    public double Roll { get; set; }
}
