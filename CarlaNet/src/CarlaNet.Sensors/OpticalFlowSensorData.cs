// §10.3 — Optical flow: after 48-byte header: [width][height][fov][pixels: {float X, float Y} * W*H]
namespace CarlaNet.Sensors;

public readonly struct FlowPixel(float x, float y)
{
    public float X { get; } = x;
    public float Y { get; } = y;
}

public sealed class OpticalFlowSensorData
{
    public uint Width { get; }
    public uint Height { get; }
    public float FovAngle { get; }
    public ReadOnlyMemory<byte> Raw { get; }

    private OpticalFlowSensorData(uint w, uint h, float fov, ReadOnlyMemory<byte> raw)
    { Width = w; Height = h; FovAngle = fov; Raw = raw; }

    public static OpticalFlowSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        uint width  = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        float fov   = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
        return new OpticalFlowSensorData(width, height, fov, payload[12..].ToArray());
    }

    public FlowPixel GetPixel(int x, int y)
    {
        int idx = (y * (int)Width + x) * 8;
        var s = Raw.Span;
        float fx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[idx..]));
        float fy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[(idx+4)..]));
        return new FlowPixel(fx, fy);
    }
}
