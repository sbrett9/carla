// Mirrors LibCarla/source/test/common/test_lidar_data.cpp
// Tests LiDAR deserialization: channel counts, point values, header layout.
using System.Buffers.Binary;
using CarlaNet.Sensors;

namespace CarlaNet.Tests.Sensors;

public class LidarSensorTests
{
    /// Build a synthetic LiDAR payload.
    /// Header: [HorizontalAngle:f32_bits][ChannelCount:u32][PointCount[c]:u32...]
    /// Data: totalPoints * 16 bytes (x, y, z, intensity each as float-bits-as-u32)
    private static byte[] BuildLidarPayload(float horizontalAngle, uint[] pointsPerChannel)
    {
        uint channelCount = (uint)pointsPerChannel.Length;
        uint totalPoints  = 0;
        foreach (var p in pointsPerChannel) totalPoints += p;

        int headerBytes = (int)((2 + channelCount) * 4);  // ha + cc + ppc[0..n-1]
        int dataBytes   = (int)(totalPoints * 16);
        var payload     = new byte[headerBytes + dataBytes];
        var span        = payload.AsSpan();

        // Write header
        BinaryPrimitives.WriteInt32LittleEndian(span,      BitConverter.SingleToInt32Bits(horizontalAngle));
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], channelCount);
        for (int c = 0; c < (int)channelCount; c++)
            BinaryPrimitives.WriteUInt32LittleEndian(span[(8 + c * 4)..], pointsPerChannel[c]);

        // Write points: x = i*1.0, y = i*2.0, z = i*3.0, intensity = 0.5
        int offset = headerBytes;
        for (int i = 0; i < (int)totalPoints; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..],      BitConverter.SingleToInt32Bits(i * 1.0f));
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset+4)..],  BitConverter.SingleToInt32Bits(i * 2.0f));
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset+8)..],  BitConverter.SingleToInt32Bits(i * 3.0f));
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset+12)..], BitConverter.SingleToInt32Bits(0.5f));
            offset += 16;
        }
        return payload;
    }

    [Fact]
    public void LidarSensorData_Deserialize_ChannelCount()
    {
        var payload = BuildLidarPayload(45.0f, [3, 2]);
        var lidar = LidarSensorData.Deserialize(payload);
        Assert.Equal(2u, lidar.ChannelCount);
    }

    [Fact]
    public void LidarSensorData_Deserialize_HorizontalAngle()
    {
        var payload = BuildLidarPayload(45.0f, [3, 2]);
        var lidar = LidarSensorData.Deserialize(payload);
        Assert.Equal(45.0f, lidar.HorizontalAngle, 4);
    }

    [Fact]
    public void LidarSensorData_Deserialize_PointsPerChannel()
    {
        var payload = BuildLidarPayload(0f, [3, 2]);
        var lidar = LidarSensorData.Deserialize(payload);
        Assert.Equal(2, lidar.PointsPerChannel.Count);
        Assert.Equal(3u, lidar.PointsPerChannel[0]);
        Assert.Equal(2u, lidar.PointsPerChannel[1]);
    }

    [Fact]
    public void LidarSensorData_Deserialize_TotalPointCount()
    {
        var payload = BuildLidarPayload(0f, [3, 2]);
        var lidar = LidarSensorData.Deserialize(payload);
        Assert.Equal(5, lidar.Points.Count);
    }

    [Fact]
    public void LidarSensorData_Deserialize_FirstPoint_Values()
    {
        var payload = BuildLidarPayload(0f, [3, 2]);
        var lidar = LidarSensorData.Deserialize(payload);
        // Point 0: x=0*1=0, y=0*2=0, z=0*3=0, intensity=0.5
        Assert.Equal(0.0f, lidar.Points[0].X);
        Assert.Equal(0.0f, lidar.Points[0].Y);
        Assert.Equal(0.0f, lidar.Points[0].Z);
        Assert.Equal(0.5f, lidar.Points[0].Intensity, 4);
    }

    [Fact]
    public void LidarSensorData_Deserialize_ThirdPoint_Values()
    {
        var payload = BuildLidarPayload(0f, [3, 2]);
        var lidar = LidarSensorData.Deserialize(payload);
        // Point 2: x=2.0, y=4.0, z=6.0, intensity=0.5
        Assert.Equal(2.0f, lidar.Points[2].X, 4);
        Assert.Equal(4.0f, lidar.Points[2].Y, 4);
        Assert.Equal(6.0f, lidar.Points[2].Z, 4);
    }

    [Fact]
    public void LidarSensorData_Deserialize_SingleChannel()
    {
        var payload = BuildLidarPayload(90.0f, [10]);
        var lidar = LidarSensorData.Deserialize(payload);
        Assert.Equal(1u, lidar.ChannelCount);
        Assert.Equal(10, lidar.Points.Count);
        Assert.Equal(90.0f, lidar.HorizontalAngle, 4);
    }

    [Fact]
    public void LidarSensorData_Deserialize_EmptyChannel()
    {
        var payload = BuildLidarPayload(0f, [0, 5]);
        var lidar = LidarSensorData.Deserialize(payload);
        Assert.Equal(2u, lidar.ChannelCount);
        Assert.Equal(0u, lidar.PointsPerChannel[0]);
        Assert.Equal(5u, lidar.PointsPerChannel[1]);
        Assert.Equal(5, lidar.Points.Count);
    }

    [Fact]
    public void LidarSensorData_Deserialize_HorizontalAngle_Negative()
    {
        var payload = BuildLidarPayload(-120.5f, [1]);
        var lidar = LidarSensorData.Deserialize(payload);
        Assert.Equal(-120.5f, lidar.HorizontalAngle, 4);
    }
}
