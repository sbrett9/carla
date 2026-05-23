// §13.2: carla::MsgPackAdaptors.h — std::optional<T> uses a tagged-array encoding:
//   empty  → fixarray(1) [ false ]
//   value  → fixarray(2) [ true, value ]
// Source: carla/MsgPackAdaptors.h lines 54-69 (pack) and 38-50 (convert), empirically
// confirmed: get_episode_settings field[2] arrives as fixarray(1)[false] when no fixed step.
using MessagePack.Formatters;

namespace CarlaNet.Types.Formatters;

public sealed class NullableDoubleFormatter : IMessagePackFormatter<double?>
{
    public void Serialize(ref MessagePackWriter writer, double? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteArrayHeader(1);
            writer.Write(false);
        }
        else
        {
            writer.WriteArrayHeader(2);
            writer.Write(true);
            writer.Write(value.Value);
        }
    }

    public double? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        int count = reader.ReadArrayHeader();
        if (count == 1)
        {
            reader.ReadBoolean(); // false sentinel
            return null;
        }
        if (count == 2)
        {
            reader.ReadBoolean(); // true sentinel
            return reader.ReadDouble();
        }
        throw new MessagePackSerializationException(
            $"Expected optional array of size 1 or 2, got {count}");
    }
}
