// Source: carla/rpc/DebugShape.h
using CarlaNet.Types.Formatters;
// Outer MSGPACK_DEFINE_ARRAY(primitive, color, life_time, persistent_lines)
// primitive is std::variant<Point, Line, Arrow, Box, String> -> [index, payload]
// DebugShapeFormatter handles [[variant_idx, [fields...]], color, life_time, persistent_lines]
namespace CarlaNet.Types.Rpc.Debug;

[MessagePackFormatter(typeof(DebugShapeFormatter))]
public record DebugShape(
    Primitive Primitive,
    Color Color,
    float LifeTime,
    bool PersistentLines);
