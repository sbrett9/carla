// §10.7 — Radar. After 48-byte header: flat array of 16-byte RadarDetection.
// {float velocity, float azimuth, float altitude, float depth}
namespace CarlaNet.Sensors;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RadarDetection
{
    public float Velocity;   // m/s
    public float Azimuth;    // radians
    public float Altitude;   // radians
    public float Depth;      // meters
}

public sealed class RadarSensorData
{
    public IReadOnlyList<RadarDetection> Detections { get; }

    private RadarSensorData(IReadOnlyList<RadarDetection> d) { Detections = d; }

    public static RadarSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        int count = payload.Length / 16;
        var d = new RadarDetection[count];
        for (int i = 0; i < count; i++)
            d[i] = MemoryMarshal.Read<RadarDetection>(payload[(i * 16)..]);
        return new RadarSensorData(d);
    }
}
