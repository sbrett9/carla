// Mirrors LibCarla/source/test/client/test_image.cpp
// Tests image and GBuffer sensor deserialization.
using System.Buffers.Binary;
using CarlaNet.Sensors;

namespace CarlaNet.Tests.Sensors;

public class ImageSensorTests
{
    // Build a synthetic image payload: 12-byte header + W*H BGRA pixels
    private static byte[] BuildImagePayload(uint w, uint h, float fov,
        byte b = 10, byte g = 20, byte r = 30, byte a = 255)
    {
        var payload = new byte[12 + w * h * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, w);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), h);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), BitConverter.SingleToInt32Bits(fov));
        for (int i = 12; i < payload.Length; i += 4)
        { payload[i] = b; payload[i+1] = g; payload[i+2] = r; payload[i+3] = a; }
        return payload;
    }

    // ── ImageSensorData ───────────────────────────────────────────────────────

    [Fact]
    public void ImageSensorData_Deserialize_Dimensions()
    {
        var payload = BuildImagePayload(640, 480, 90f);
        var img = ImageSensorData.Deserialize(payload);
        Assert.Equal(640u, img.Width);
        Assert.Equal(480u, img.Height);
        Assert.Equal(90f,  img.FovAngle);
    }

    [Fact]
    public void ImageSensorData_Deserialize_PixelCount()
    {
        var payload = BuildImagePayload(100, 50, 60f);
        var img = ImageSensorData.Deserialize(payload);
        Assert.Equal(100u * 50u * 4u, (uint)img.RawBgra.Length);
    }

    [Fact]
    public void ImageSensorData_GetPixel_BGRA_Layout()
    {
        var payload = BuildImagePayload(10, 10, 90f, b: 10, g: 20, r: 30, a: 255);
        var img = ImageSensorData.Deserialize(payload);
        var px = img.GetPixel(0, 0);
        Assert.Equal(10,  px.B);
        Assert.Equal(20,  px.G);
        Assert.Equal(30,  px.R);
        Assert.Equal(255, px.A);
    }

    [Fact]
    public void ImageSensorData_GetPixel_Interior_Pixel()
    {
        // Use distinct per-pixel colors by encoding the pixel index into the green channel
        uint w = 8, h = 4;
        var payload = new byte[12 + w * h * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, w);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), h);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), BitConverter.SingleToInt32Bits(75f));
        for (int i = 0; i < (int)(w * h); i++)
        {
            int off = 12 + i * 4;
            payload[off]   = 1;
            payload[off+1] = (byte)i;  // G = pixel index (unique per pixel)
            payload[off+2] = 2;
            payload[off+3] = 255;
        }
        var img = ImageSensorData.Deserialize(payload);
        // Pixel at (x=3, y=2): index = 2 * 8 + 3 = 19
        var px = img.GetPixel(3, 2);
        Assert.Equal(19, px.G);
    }

    [Fact]
    public void ImageSensorData_Deserialize_1x1()
    {
        var payload = BuildImagePayload(1, 1, 45f, b: 11, g: 22, r: 33, a: 255);
        var img = ImageSensorData.Deserialize(payload);
        Assert.Equal(1u, img.Width);
        Assert.Equal(1u, img.Height);
        Assert.Equal(4, img.RawBgra.Length);
    }

    // ── GBufferUint8SensorData ────────────────────────────────────────────────

    [Fact]
    public void GBufferUint8SensorData_Deserialize_SameAsImage()
    {
        var payload = BuildImagePayload(32, 32, 90f);
        var gbuf = GBufferUint8SensorData.Deserialize(payload);
        Assert.Equal(32u, gbuf.Width);
        Assert.Equal(32u, gbuf.Height);
        Assert.Equal(90f, gbuf.FovAngle);
    }

    [Fact]
    public void GBufferUint8SensorData_GetPixel_Returns_BGRA_Tuple()
    {
        var payload = BuildImagePayload(5, 5, 60f, b: 50, g: 100, r: 150, a: 255);
        var gbuf = GBufferUint8SensorData.Deserialize(payload);
        var (B, G, R, A) = gbuf.GetPixel(0, 0);
        Assert.Equal(50,  B);
        Assert.Equal(100, G);
        Assert.Equal(150, R);
        Assert.Equal(255, A);
    }

    [Fact]
    public void GBufferUint8SensorData_PixelCount()
    {
        var payload = BuildImagePayload(64, 48, 90f);
        var gbuf = GBufferUint8SensorData.Deserialize(payload);
        Assert.Equal(64u * 48u * 4u, (uint)gbuf.RawBgra.Length);
    }
}
