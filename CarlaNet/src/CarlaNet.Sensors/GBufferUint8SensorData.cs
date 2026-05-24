// §10.12 — GBuffer Uint8 sensor (semantic/instance segmentation encoded as BGRA tags).
// Identical binary layout to §10.2 ImageSerializer: 12-byte header + BGRA pixels.
// Source: LibCarla/source/carla/sensor/s11n/GBufferUint8Serializer.h
namespace CarlaNet.Sensors;

public sealed class GBufferUint8SensorData
{
    public uint Width { get; }
    public uint Height { get; }
    public float FovAngle { get; }
    public ReadOnlyMemory<byte> RawBgra { get; }

    private GBufferUint8SensorData(uint w, uint h, float fov, ReadOnlyMemory<byte> raw)
    { Width = w; Height = h; FovAngle = fov; RawBgra = raw; }

    public static GBufferUint8SensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        uint width  = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        float fov   = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
        var raw = payload[12..].ToArray();
        return new GBufferUint8SensorData(width, height, fov, raw);
    }

    // Get BGRA pixel at pixel coordinates (x, y).
    // Tag values: B=label, G=object_idx_low, R=object_idx_high, A=255.
    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        int idx = (y * (int)Width + x) * 4;
        var s = RawBgra.Span;
        return (s[idx], s[idx + 1], s[idx + 2], s[idx + 3]);
    }
}
