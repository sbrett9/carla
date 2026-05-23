// §10.2, §10.12 — Image sensors (RGB, Depth, Semantic/Instance Segmentation, Normals, GBufferUint8)
// After 48-byte header: [width:u32][height:u32][fov_angle:f32][pixels: BGRA * W*H]
// BGRA layout: B=byte[0], G=byte[1], R=byte[2], A=byte[3] (A is always 255, not meaningful)
namespace CarlaNet.Sensors;

public readonly struct BgraPixel(byte b, byte g, byte r, byte a = 255)
{
    public byte B { get; } = b;
    public byte G { get; } = g;
    public byte R { get; } = r;
    public byte A { get; } = a;
}

public sealed class ImageSensorData
{
    public uint Width { get; }
    public uint Height { get; }
    public float FovAngle { get; }
    public ReadOnlyMemory<byte> RawBgra { get; }

    private ImageSensorData(uint w, uint h, float fov, ReadOnlyMemory<byte> raw)
    { Width = w; Height = h; FovAngle = fov; RawBgra = raw; }

    public static ImageSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        uint width  = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        float fov   = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
        var raw = payload[12..].ToArray();
        return new ImageSensorData(width, height, fov, raw);
    }

    public BgraPixel GetPixel(int x, int y)
    {
        int idx = (y * (int)Width + x) * 4;
        var s = RawBgra.Span;
        return new BgraPixel(s[idx], s[idx + 1], s[idx + 2], s[idx + 3]);
    }
}
