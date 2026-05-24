// Mirrors LibCarla radar sensor tests.
// RadarDetection layout (Pack=1): {float velocity, float azimuth, float altitude, float depth}
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CarlaNet.Sensors;

namespace CarlaNet.Tests.Sensors;

public class RadarSensorTests
{
    private static byte[] BuildRadarPayload(int count)
    {
        var payload = new byte[count * 16];
        var span = payload.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int off = i * 16;
            BinaryPrimitives.WriteInt32LittleEndian(span[off..],      BitConverter.SingleToInt32Bits(i * 10.0f));   // velocity
            BinaryPrimitives.WriteInt32LittleEndian(span[(off+4)..],  BitConverter.SingleToInt32Bits(0.1f * i));    // azimuth
            BinaryPrimitives.WriteInt32LittleEndian(span[(off+8)..],  BitConverter.SingleToInt32Bits(0.05f * i));   // altitude
            BinaryPrimitives.WriteInt32LittleEndian(span[(off+12)..], BitConverter.SingleToInt32Bits(50.0f + i));   // depth
        }
        return payload;
    }

    [Fact]
    public void RadarDetection_Size_Is_16_Bytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<RadarDetection>());
    }

    [Fact]
    public void RadarSensorData_Deserialize_Count()
    {
        var payload = BuildRadarPayload(5);
        var radar = RadarSensorData.Deserialize(payload);
        Assert.Equal(5, radar.Detections.Count);
    }

    [Fact]
    public void RadarSensorData_Deserialize_Velocity()
    {
        var payload = BuildRadarPayload(3);
        var radar = RadarSensorData.Deserialize(payload);
        Assert.Equal(0.0f,  radar.Detections[0].Velocity, 4);
        Assert.Equal(10.0f, radar.Detections[1].Velocity, 4);
        Assert.Equal(20.0f, radar.Detections[2].Velocity, 4);
    }

    [Fact]
    public void RadarSensorData_Deserialize_Depth()
    {
        var payload = BuildRadarPayload(3);
        var radar = RadarSensorData.Deserialize(payload);
        Assert.Equal(50.0f, radar.Detections[0].Depth, 4);
        Assert.Equal(51.0f, radar.Detections[1].Depth, 4);
        Assert.Equal(52.0f, radar.Detections[2].Depth, 4);
    }

    [Fact]
    public void RadarSensorData_Deserialize_Azimuth_Altitude()
    {
        var payload = BuildRadarPayload(4);
        var radar = RadarSensorData.Deserialize(payload);
        Assert.Equal(0f,    radar.Detections[0].Azimuth, 4);
        Assert.Equal(0.1f,  radar.Detections[1].Azimuth, 4);
        Assert.Equal(0f,    radar.Detections[0].Altitude, 4);
        Assert.Equal(0.05f, radar.Detections[1].Altitude, 4);
    }

    [Fact]
    public void RadarSensorData_Deserialize_Empty()
    {
        var payload = BuildRadarPayload(0);
        var radar = RadarSensorData.Deserialize(payload);
        Assert.Empty(radar.Detections);
    }

    [Fact]
    public void RadarSensorData_Deserialize_SingleDetection()
    {
        var payload = BuildRadarPayload(1);
        var radar = RadarSensorData.Deserialize(payload);
        Assert.Single(radar.Detections);
        Assert.Equal(50.0f, radar.Detections[0].Depth, 4);
    }
}
