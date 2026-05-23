// §13.2: std::optional<double> serializes as msgpack nil (empty) or raw double (present).
// MessagePack-CSharp's default double? formatter wraps in an array — wrong for CARLA.
using MessagePack.Formatters;

namespace CarlaNet.Types.Formatters;

public sealed class NullableDoubleFormatter : IMessagePackFormatter<double?>
{
    public void Serialize(ref MessagePackWriter writer, double? value, MessagePackSerializerOptions options)
    {
        if (value is null)
            writer.WriteNil();
        else
            writer.Write(value.Value);
    }

    public double? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;
        return reader.ReadDouble();
    }
}
