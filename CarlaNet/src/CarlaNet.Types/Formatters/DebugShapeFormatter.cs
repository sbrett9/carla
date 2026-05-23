// Serializes carla::rpc::DebugShape to its wire format.
//
// Source: carla/rpc/DebugShape.h
// MSGPACK_DEFINE_ARRAY(primitive, color, life_time, persistent_lines)
// primitive is std::variant<Point, Line, Arrow, Box, String> → [index, [fields...]]
//
// Full DebugShape wire bytes = [[variant_idx, [fields...]], color, life_time, persistent_lines]
using System.Buffers;
using CarlaNet.Types.Rpc.Debug;
using MessagePack.Formatters;

namespace CarlaNet.Types.Formatters;

public sealed class DebugShapeFormatter : IMessagePackFormatter<DebugShape?>
{
    public static readonly DebugShapeFormatter Instance = new();

    public DebugShape? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        => throw new NotSupportedException("DebugShape is write-only");

    public void Serialize(ref MessagePackWriter writer, DebugShape? value, MessagePackSerializerOptions options)
    {
        if (value is null) { writer.WriteNil(); return; }

        // MSGPACK_DEFINE_ARRAY(primitive, color, life_time, persistent_lines)
        writer.WriteArrayHeader(4);

        // primitive: std::variant → [idx, payload]
        var buf = new ArrayBufferWriter<byte>();
        int idx = WritePrimitive(buf, value.Primitive, options);
        writer.WriteArrayHeader(2);
        writer.Write(idx);
        writer.WriteRaw(buf.WrittenSpan);

        // color, life_time, persistent_lines
        MessagePackSerializer.Serialize(ref writer, value.Color, options);
        writer.Write(value.LifeTime);
        writer.Write(value.PersistentLines);
    }

    private static int WritePrimitive(IBufferWriter<byte> buf, Primitive primitive, MessagePackSerializerOptions options)
    {
        var w = new MessagePackWriter(buf);
        int idx;
        switch (primitive)
        {
            case PointPrimitive p:
                idx = (int)PrimitiveType.Point;
                w.WriteArrayHeader(2);
                MessagePackSerializer.Serialize(ref w, p.Location, options);
                w.Write(p.Size);
                break;
            case LinePrimitive l:
                idx = (int)PrimitiveType.Line;
                w.WriteArrayHeader(3);
                MessagePackSerializer.Serialize(ref w, l.Begin, options);
                MessagePackSerializer.Serialize(ref w, l.End, options);
                w.Write(l.Thickness);
                break;
            case ArrowPrimitive a:
                // MSGPACK_DEFINE_ARRAY(line, arrow_size) — line is itself a LinePrimitive
                idx = (int)PrimitiveType.Arrow;
                w.WriteArrayHeader(2);
                // Serialize the LinePrimitive as [begin, end, thickness]
                w.WriteArrayHeader(3);
                MessagePackSerializer.Serialize(ref w, a.Line.Begin, options);
                MessagePackSerializer.Serialize(ref w, a.Line.End, options);
                w.Write(a.Line.Thickness);
                w.Write(a.ArrowSize);
                break;
            case BoxPrimitive b:
                idx = (int)PrimitiveType.Box;
                w.WriteArrayHeader(3);
                MessagePackSerializer.Serialize(ref w, b.Box, options);
                MessagePackSerializer.Serialize(ref w, b.Rotation, options);
                w.Write(b.Thickness);
                break;
            case StringPrimitive s:
                idx = (int)PrimitiveType.String;
                w.WriteArrayHeader(3);
                MessagePackSerializer.Serialize(ref w, s.Location, options);
                w.Write(s.Text);
                w.Write(s.DrawShadow);
                break;
            default:
                throw new NotSupportedException($"Unknown primitive: {primitive.GetType().Name}");
        }
        w.Flush();
        return idx;
    }
}
