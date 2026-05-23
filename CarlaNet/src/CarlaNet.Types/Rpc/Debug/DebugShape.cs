// Source: carla/rpc/DebugShape.h
// Outer MSGPACK_DEFINE_ARRAY(primitive, color, life_time, persistent_lines)
// primitive is std::variant<Point, Line, Arrow, Box, String> -> [index, payload]
// Each subtype MSGPACK:
//   Point:  MSGPACK_DEFINE_ARRAY(location, size)
//   Line:   MSGPACK_DEFINE_ARRAY(begin, end, thickness)
//   Arrow:  MSGPACK_DEFINE_ARRAY(line, arrow_size)
//   Box:    MSGPACK_DEFINE_ARRAY(box, rotation, thickness)
//   String: MSGPACK_DEFINE_ARRAY(location, text, draw_shadow)
namespace CarlaNet.Types.Rpc.Debug;

public abstract record Primitive;
public record PointPrimitive(Location Location, float Size) : Primitive;
public record LinePrimitive(Location Begin, Location End, float Thickness) : Primitive;
public record ArrowPrimitive(LinePrimitive Line, float ArrowSize) : Primitive;
public record BoxPrimitive(BoundingBox Box, Rotation Rotation, float Thickness) : Primitive;
public record StringPrimitive(Location Location, string Text, bool DrawShadow) : Primitive;

public enum PrimitiveType : int
{ Point = 0, Line = 1, Arrow = 2, Box = 3, String = 4 }

// The DebugShape formatter writes: [primitive_variant, color, life_time, persistent_lines]
// where primitive_variant is [primitive_index, [primitive_fields...]]
public record DebugShape(
    Primitive Primitive,
    Color Color,
    float LifeTime,
    bool PersistentLines);
