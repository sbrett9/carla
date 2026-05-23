// §10.4, §13.3 — DVS camera events.
// After 48-byte header: [width:u32][height:u32][fov:f32][events: DVSEvent * N]
// DVSEvent: {uint16 x, uint16 y, int64 t, bool pol, byte[7] padding} = 20 bytes
// The 7-byte padding MUST be declared explicitly (§13.3).
namespace CarlaNet.Sensors;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct DvsEvent
{
    public ushort X;
    public ushort Y;
    public long TimestampMicros;
    public byte Polarity;          // bool as 1 byte
    public fixed byte Padding[7];  // explicit padding to reach 20 bytes (§13.3)
}

public sealed class DvsSensorData
{
    public uint Width { get; }
    public uint Height { get; }
    public float FovAngle { get; }
    public IReadOnlyList<DvsEvent> Events { get; }

    private DvsSensorData(uint w, uint h, float fov, IReadOnlyList<DvsEvent> events)
    { Width = w; Height = h; FovAngle = fov; Events = events; }

    public static unsafe DvsSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        uint width  = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        float fov   = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));

        var eventData = payload[12..];
        int evtSize = sizeof(DvsEvent); // must equal 20
        int count = eventData.Length / evtSize;
        var events = new DvsEvent[count];
        for (int i = 0; i < count; i++)
            events[i] = MemoryMarshal.Read<DvsEvent>(eventData[(i * evtSize)..]);
        return new DvsSensorData(width, height, fov, events);
    }
}
