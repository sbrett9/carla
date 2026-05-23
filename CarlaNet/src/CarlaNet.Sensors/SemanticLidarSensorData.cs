// §10.6 — RayCastSemanticLidar. Same variable header as LidarSensorData.
// Point: {float x, float y, float z, float cos_inc_angle, uint32 object_idx, uint32 object_tag} = 24 bytes
namespace CarlaNet.Sensors;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SemanticLidarDetection
{
    public float X, Y, Z;
    public float CosIncidenceAngle;
    public uint ObjectIndex;
    public uint ObjectTag;
}

public sealed class SemanticLidarSensorData
{
    public float HorizontalAngle { get; }
    public uint ChannelCount { get; }
    public IReadOnlyList<uint> PointsPerChannel { get; }
    public IReadOnlyList<SemanticLidarDetection> Points { get; }

    private SemanticLidarSensorData(float ha, uint cc, IReadOnlyList<uint> ppc,
        IReadOnlyList<SemanticLidarDetection> pts)
    { HorizontalAngle = ha; ChannelCount = cc; PointsPerChannel = ppc; Points = pts; }

    public static SemanticLidarSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        float ha = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload));
        uint cc  = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);

        var ppc = new uint[cc];
        for (int i = 0; i < cc; i++)
            ppc[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload[(8 + i * 4)..]);

        int headerBytes = (2 + (int)cc) * 4;
        var pointData = payload[headerBytes..];
        uint totalPoints = 0;
        foreach (var c in ppc) totalPoints += c;

        var pts = new SemanticLidarDetection[totalPoints];
        for (int i = 0; i < totalPoints; i++)
            pts[i] = MemoryMarshal.Read<SemanticLidarDetection>(pointData[(i * 24)..]);

        return new SemanticLidarSensorData(ha, cc, ppc, pts);
    }
}
