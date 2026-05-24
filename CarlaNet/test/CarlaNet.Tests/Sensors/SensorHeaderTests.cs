// Binary structure verification for the 48-byte sensor header
// Mirrors: LibCarla/source/carla/sensor/s11n/SensorHeaderSerializer.h
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CarlaNet.Sensors;
using CarlaNet.Transport.Streaming;

namespace CarlaNet.Tests.Sensors;

public class SensorHeaderTests
{
    [Fact]
    public void SensorHeader_Size_Is_48_Bytes()
    {
        Assert.Equal(48, Unsafe.SizeOf<SensorHeader>());
    }

    [Fact]
    public void RawSensorHeader_Size_Is_48_Bytes()
    {
        Assert.Equal(48, Unsafe.SizeOf<RawSensorHeader>());
    }

    [Fact]
    public void SensorHeader_Fields_Parse_Correctly()
    {
        // Build a synthetic 48-byte header matching the documented field layout
        var bytes = new byte[48];
        var span = bytes.AsSpan();

        // offset 0: sensor_type (u64)
        BinaryPrimitives.WriteUInt64LittleEndian(span, 5UL);
        // offset 8: frame (u64)
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 100UL);
        // offset 16: timestamp (double)
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], BitConverter.DoubleToInt64Bits(123.456));
        // offset 24-35: location.x/y/z (float)
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], BitConverter.SingleToInt32Bits(1.0f));
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], BitConverter.SingleToInt32Bits(2.0f));
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], BitConverter.SingleToInt32Bits(3.0f));
        // offset 36-47: rotation.pitch/yaw/roll (float)
        BinaryPrimitives.WriteInt32LittleEndian(span[36..], BitConverter.SingleToInt32Bits(10.0f));
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], BitConverter.SingleToInt32Bits(90.0f));
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], BitConverter.SingleToInt32Bits(0.0f));

        var header = MemoryMarshal.Read<SensorHeader>(span);

        Assert.Equal(5UL,  header.SensorType);
        Assert.Equal(100UL, header.Frame);
        Assert.Equal(123.456, header.Timestamp, 10);
        Assert.Equal(1.0f,  header.LocationX);
        Assert.Equal(2.0f,  header.LocationY);
        Assert.Equal(3.0f,  header.LocationZ);
        Assert.Equal(10.0f, header.RotationPitch);
        Assert.Equal(90.0f, header.RotationYaw);
        Assert.Equal(0.0f,  header.RotationRoll);
    }

    [Fact]
    public void SensorHeader_Location_Helper_Property()
    {
        var bytes = new byte[48];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], BitConverter.SingleToInt32Bits(5f));
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], BitConverter.SingleToInt32Bits(6f));
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], BitConverter.SingleToInt32Bits(7f));

        var header = MemoryMarshal.Read<SensorHeader>(span);
        Assert.Equal(5f, header.Location.X);
        Assert.Equal(6f, header.Location.Y);
        Assert.Equal(7f, header.Location.Z);
    }

    [Fact]
    public void SensorHeader_Rotation_Helper_Property()
    {
        var bytes = new byte[48];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[36..], BitConverter.SingleToInt32Bits(15f));
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], BitConverter.SingleToInt32Bits(45f));
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], BitConverter.SingleToInt32Bits(-5f));

        var header = MemoryMarshal.Read<SensorHeader>(span);
        Assert.Equal(15f,  header.Rotation.Pitch);
        Assert.Equal(45f,  header.Rotation.Yaw);
        Assert.Equal(-5f,  header.Rotation.Roll);
    }

    [Fact]
    public void SensorHeader_Transform_Helper_Property()
    {
        var bytes = new byte[48];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], BitConverter.SingleToInt32Bits(1f));
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], BitConverter.SingleToInt32Bits(180f));

        var header = MemoryMarshal.Read<SensorHeader>(span);
        Assert.Equal(1f,    header.Transform.Location.X);
        Assert.Equal(180f,  header.Transform.Rotation.Yaw);
    }

    [Fact]
    public void RawSensorHeader_Fields_Match_SensorHeader()
    {
        // Both structs must parse identically from the same bytes
        var bytes = new byte[48];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(span,     99UL);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 42UL);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], BitConverter.DoubleToInt64Bits(3.14));

        var sh  = MemoryMarshal.Read<SensorHeader>(span);
        var rsh = MemoryMarshal.Read<RawSensorHeader>(span);

        Assert.Equal(sh.SensorType, rsh.SensorType);
        Assert.Equal(sh.Frame,      rsh.Frame);
        Assert.Equal(sh.Timestamp,  rsh.Timestamp);
    }
}
