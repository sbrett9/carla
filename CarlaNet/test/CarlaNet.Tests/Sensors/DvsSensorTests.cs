// DVS camera event sensor tests.
// DvsEvent must be exactly 20 bytes: ushort x(2) + ushort y(2) + long t(8) + byte pol(1) + byte[7] pad = 20.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CarlaNet.Sensors;

namespace CarlaNet.Tests.Sensors;

public class DvsSensorTests
{
    [Fact]
    public unsafe void DvsEvent_Size_Is_20_Bytes()
    {
        // §13.3 — DVSEvent must be exactly 20 bytes with Pack=1 and explicit 7-byte padding
        Assert.Equal(20, sizeof(DvsEvent));
    }

    [Fact]
    public unsafe void DvsEvent_SizeOf_Matches_Unsafe_SizeOf()
    {
        Assert.Equal(sizeof(DvsEvent), Unsafe.SizeOf<DvsEvent>());
    }

    [Fact]
    public void DvsSensorData_Deserialize_Dimensions()
    {
        // Build payload: [width:u32][height:u32][fov:f32][zero events]
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,          320u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 240u);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), BitConverter.SingleToInt32Bits(80f));

        var dvs = DvsSensorData.Deserialize(payload);
        Assert.Equal(320u, dvs.Width);
        Assert.Equal(240u, dvs.Height);
        Assert.Equal(80f,  dvs.FovAngle);
        Assert.Empty(dvs.Events);
    }

    [Fact]
    public unsafe void DvsSensorData_Deserialize_Events()
    {
        int evtSize = sizeof(DvsEvent); // == 20
        var payload = new byte[12 + evtSize * 2];
        var span = payload.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, 100u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 100u);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], BitConverter.SingleToInt32Bits(90f));

        // Event 0: x=10, y=20, t=12345, pol=1
        int off = 12;
        BinaryPrimitives.WriteUInt16LittleEndian(span[off..],     10);    // x
        BinaryPrimitives.WriteUInt16LittleEndian(span[(off+2)..], 20);    // y
        BinaryPrimitives.WriteInt64LittleEndian(span[(off+4)..], 12345L); // t
        span[off + 12] = 1;                                                // polarity

        // Event 1: x=50, y=60, t=99999, pol=0
        off += evtSize;
        BinaryPrimitives.WriteUInt16LittleEndian(span[off..],     50);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(off+2)..], 60);
        BinaryPrimitives.WriteInt64LittleEndian(span[(off+4)..], 99999L);
        span[off + 12] = 0;

        var dvs = DvsSensorData.Deserialize(payload);

        Assert.Equal(2, dvs.Events.Count);
        Assert.Equal((ushort)10,  dvs.Events[0].X);
        Assert.Equal((ushort)20,  dvs.Events[0].Y);
        Assert.Equal(12345L,      dvs.Events[0].TimestampMicros);
        Assert.Equal((byte)1,     dvs.Events[0].Polarity);

        Assert.Equal((ushort)50,  dvs.Events[1].X);
        Assert.Equal((ushort)60,  dvs.Events[1].Y);
        Assert.Equal(99999L,      dvs.Events[1].TimestampMicros);
        Assert.Equal((byte)0,     dvs.Events[1].Polarity);
    }
}
