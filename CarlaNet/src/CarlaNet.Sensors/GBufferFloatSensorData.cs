// §10.13 — GBuffer float: after 48-byte header: [width][height][fov][pixels: RGBA float * W*H]
namespace CarlaNet.Sensors;

public readonly struct FloatRgbaPixel(float r, float g, float b, float a)
{
    public float R { get; } = r;
    public float G { get; } = g;
    public float B { get; } = b;
    public float A { get; } = a;
}

public sealed class GBufferFloatSensorData
{
    public uint Width { get; }
    public uint Height { get; }
    public float FovAngle { get; }
    public ReadOnlyMemory<byte> RawFloatRgba { get; }

    private GBufferFloatSensorData(uint w, uint h, float fov, ReadOnlyMemory<byte> raw)
    { Width = w; Height = h; FovAngle = fov; RawFloatRgba = raw; }

    public static GBufferFloatSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        uint width  = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        float fov   = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
        return new GBufferFloatSensorData(width, height, fov, payload[12..].ToArray());
    }

    public FloatRgbaPixel GetPixel(int x, int y)
    {
        int idx = (y * (int)Width + x) * 16;
        var s = RawFloatRgba.Span;
        float r = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[idx..]));
        float g = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[(idx+4)..]));
        float b = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[(idx+8)..]));
        float a = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[(idx+12)..]));
        return new FloatRgbaPixel(r, g, b, a);
    }
}
