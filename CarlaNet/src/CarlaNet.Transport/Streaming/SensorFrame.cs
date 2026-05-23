// §9 — a single received sensor frame: 48-byte header + variable payload.
// header_offset = sizeof(SensorHeaderSerializer::Header) = 48 (source-verified).
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CarlaNet.Types.Geom;

namespace CarlaNet.Transport.Streaming;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RawSensorHeader
{
    public ulong SensorType;
    public ulong Frame;
    public double Timestamp;
    public float LocationX, LocationY, LocationZ;
    public float RotationPitch, RotationYaw, RotationRoll;
    // sizeof == 48
}

public sealed class SensorFrame
{
    public const int HeaderSize = 48;

    public RawSensorHeader Header { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public Location SensorLocation => new(Header.LocationX, Header.LocationY, Header.LocationZ);
    public Rotation SensorRotation => new(Header.RotationPitch, Header.RotationYaw, Header.RotationRoll);
    public Transform SensorTransform => new(SensorLocation, SensorRotation);

    internal SensorFrame(ReadOnlySpan<byte> combined)
    {
        if (combined.Length < HeaderSize)
            throw new ArgumentException($"Frame too small: {combined.Length} < {HeaderSize}");
        Header = MemoryMarshal.Read<RawSensorHeader>(combined);
        var data = new byte[combined.Length - HeaderSize];
        combined[HeaderSize..].CopyTo(data);
        Payload = data;
    }
}
