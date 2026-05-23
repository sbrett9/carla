// §10.5 — RayCastLidar.
// After 48-byte header: variable uint32[] header then 16-byte LidarDetection per point.
// Header: [HorizontalAngle:f32_as_u32][ChannelCount:u32][PointCount[0]:u32]...[PointCount[C-1]:u32]
// §13.5: HorizontalAngle stored as uint32 bits, must use BitConverter.Int32BitsToSingle.
namespace CarlaNet.Sensors;

public readonly struct LidarDetection(float x, float y, float z, float intensity)
{
    public float X { get; } = x;
    public float Y { get; } = y;
    public float Z { get; } = z;
    public float Intensity { get; } = intensity;
}

public sealed class LidarSensorData
{
    public float HorizontalAngle { get; }
    public uint ChannelCount { get; }
    public IReadOnlyList<uint> PointsPerChannel { get; }
    public IReadOnlyList<LidarDetection> Points { get; }

    private LidarSensorData(float ha, uint cc, IReadOnlyList<uint> ppc, IReadOnlyList<LidarDetection> pts)
    { HorizontalAngle = ha; ChannelCount = cc; PointsPerChannel = ppc; Points = pts; }

    public static LidarSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        // §13.5: reinterpret uint32 bits as float
        float ha = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(payload));
        uint channelCount = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);

        var ppc = new uint[channelCount];
        for (int i = 0; i < channelCount; i++)
            ppc[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload[(8 + i * 4)..]);

        int headerBytes = (2 + (int)channelCount) * 4;
        var pointData = payload[headerBytes..];
        uint totalPoints = 0;
        foreach (var c in ppc) totalPoints += c;

        var points = new LidarDetection[totalPoints];
        for (int i = 0; i < totalPoints; i++)
        {
            int off = i * 16;
            float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pointData[off..]));
            float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pointData[(off+4)..]));
            float z = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pointData[(off+8)..]));
            float intensity = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pointData[(off+12)..]));
            points[i] = new LidarDetection(x, y, z, intensity);
        }
        return new LidarSensorData(ha, channelCount, ppc, points);
    }
}
