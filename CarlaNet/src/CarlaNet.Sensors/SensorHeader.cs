// Source: LibCarla/source/carla/sensor/s11n/SensorHeaderSerializer.h
// Common 48-byte sensor header — same layout as RawSensorHeader in Transport.Streaming.
// Pack=1, no implicit padding.
// Offset  Size  Field
// 0       8     sensor_type   uint64
// 8       8     frame         uint64
// 16      8     timestamp     double
// 24      4     location.x    float
// 28      4     location.y    float
// 32      4     location.z    float
// 36      4     rotation.pitch float
// 40      4     rotation.yaw   float
// 44      4     rotation.roll  float
// Total = 48 bytes
namespace CarlaNet.Sensors;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SensorHeader
{
    public ulong SensorType;
    public ulong Frame;
    public double Timestamp;
    public float LocationX, LocationY, LocationZ;
    public float RotationPitch, RotationYaw, RotationRoll;

    public Location Location => new(LocationX, LocationY, LocationZ);
    public Rotation Rotation => new(RotationPitch, RotationYaw, RotationRoll);
    public Transform Transform => new(Location, Rotation);
}
// Static assertion (checked at runtime in tests): Unsafe.SizeOf<SensorHeader>() == 48
