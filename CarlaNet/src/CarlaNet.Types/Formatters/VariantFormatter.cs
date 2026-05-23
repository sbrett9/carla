// §13.1: std::variant<> serializes as a 2-element msgpack array [index, payload].
// Used for Command.CommandType and DebugShape.primitive.
using System.Buffers;
using MessagePack.Formatters;

namespace CarlaNet.Types.Formatters;

public static class VariantReader
{
    public static (int Index, byte[] Payload) Read(ref MessagePackReader reader)
    {
        int count = reader.ReadArrayHeader();
        if (count != 2)
            throw new MessagePackSerializationException($"Expected variant array of 2, got {count}");
        int index = reader.ReadInt32();
        ReadOnlySequence<byte> payloadSeq = reader.ReadRaw();
        return (index, payloadSeq.ToArray());
    }

    public static void Write(ref MessagePackWriter writer, int index, Action<IBufferWriter<byte>> writePayload)
    {
        writer.WriteArrayHeader(2);
        writer.Write(index);
        var tempBuffer = new ArrayBufferWriter<byte>();
        writePayload(tempBuffer);
        writer.WriteRaw(tempBuffer.WrittenSpan);
    }
}
