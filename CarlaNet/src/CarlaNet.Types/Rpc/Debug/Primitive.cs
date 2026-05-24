// Source: carla/rpc/DebugShape.h — primitive variant types
// Point:  MSGPACK_DEFINE_ARRAY(location, size)
// Line:   MSGPACK_DEFINE_ARRAY(begin, end, thickness)
// Arrow:  MSGPACK_DEFINE_ARRAY(line, arrow_size)
// Box:    MSGPACK_DEFINE_ARRAY(box, rotation, thickness)
// String: MSGPACK_DEFINE_ARRAY(location, text, draw_shadow)
namespace CarlaNet.Types.Rpc.Debug;

public abstract record Primitive;
public record PointPrimitive(Location Location, float Size) : Primitive;
public record LinePrimitive(Location Begin, Location End, float Thickness) : Primitive;
public record ArrowPrimitive(LinePrimitive Line, float ArrowSize) : Primitive;
public record BoxPrimitive(BoundingBox Box, Rotation Rotation, float Thickness) : Primitive;
public record StringPrimitive(Location Location, string Text, bool DrawShadow) : Primitive;

public enum PrimitiveType : int
{ Point = 0, Line = 1, Arrow = 2, Box = 3, String = 4 }
