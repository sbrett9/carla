// Source: carla/road/element/Geometry.h (DirectedPoint struct).
// (Location, tangent[rad], pitch[rad]) — output of Geometry.PosFromDist.
namespace CarlaNet.Map.Geom;

public record struct DirectedPoint(Location Location, double Tangent, double Pitch)
{
    public DirectedPoint() : this(new Location(0f, 0f, 0f), 0.0, 0.0) { }

    public DirectedPoint(Location location, double tangent) : this(location, tangent, 0.0) { }

    public DirectedPoint(float x, float y, float z, double tangent)
        : this(new Location(x, y, z), tangent, 0.0) { }

    /// Translate the location laterally (perpendicular to tangent) by lateral_offset.
    /// Matches the C++ ApplyLateralOffset: normal = (sin(t), -cos(t), 0).
    public void ApplyLateralOffset(float lateralOffset)
    {
        var normalX = (float)Math.Sin(Tangent);
        var normalY = -(float)Math.Cos(Tangent);
        Location = new Location(
            Location.X + lateralOffset * normalX,
            Location.Y + lateralOffset * normalY,
            Location.Z);
    }
}
