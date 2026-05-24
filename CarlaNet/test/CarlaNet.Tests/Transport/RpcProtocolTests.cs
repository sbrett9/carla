// Tests the streaming token wire format and RawToken msgpack serialization.
// Mirrors the token parsing behavior documented in carla/streaming/detail/Token.h
using System.Buffers.Binary;
using System.Net;
using CarlaNet.Types.Streaming;
using MessagePack;

namespace CarlaNet.Tests.Transport;

public class RpcProtocolTests
{
    // Build a 24-byte token raw buffer
    private static byte[] MakeRawToken(uint streamId, ushort port,
        byte protocol, byte addressType, ReadOnlySpan<byte> addressBytes)
    {
        var raw = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(raw,          streamId);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4), port);
        raw[6] = protocol;
        raw[7] = addressType;
        addressBytes.CopyTo(raw.AsSpan(8));
        return raw;
    }

    // ── StreamToken.Parse ────────────────────────────────────────────────────

    [Fact]
    public void StreamToken_Parse_IPv4_All_Zeros_Uses_ServerHost()
    {
        // 0.0.0.0 means "same host as the RPC server" → resolve to serverHost
        var raw = MakeRawToken(42u, 8080, 1, 1, new byte[4]);
        var token = StreamToken.Parse(raw, "127.0.0.1");

        Assert.Equal(42u, token.StreamId);
        Assert.Equal(8080, token.Port);
        Assert.Equal(StreamProtocol.Tcp, token.Protocol);
        Assert.Equal(StreamAddressType.Ipv4, token.AddressType);
        Assert.Equal("127.0.0.1", token.Address.ToString());
    }

    [Fact]
    public void StreamToken_Parse_Specific_IPv4()
    {
        var addrBytes = new byte[] { 192, 168, 1, 100 };
        var raw = MakeRawToken(1u, 2001, 1, 1, addrBytes);
        var token = StreamToken.Parse(raw, "localhost");

        Assert.Equal(1u, token.StreamId);
        Assert.Equal(2001, token.Port);
        Assert.Equal("192.168.1.100", token.Address.ToString());
    }

    [Fact]
    public void StreamToken_Parse_IPv4_Loopback()
    {
        var addrBytes = new byte[] { 127, 0, 0, 1 };
        var raw = MakeRawToken(10u, 5000, 1, 1, addrBytes);
        var token = StreamToken.Parse(raw, "192.168.0.1");

        Assert.Equal("127.0.0.1", token.Address.ToString());
        Assert.Equal(10u, token.StreamId);
        Assert.Equal(5000, token.Port);
    }

    [Fact]
    public void StreamToken_Parse_Protocol_Tcp()
    {
        var raw = MakeRawToken(1u, 2000, (byte)StreamProtocol.Tcp, (byte)StreamAddressType.Ipv4,
            new byte[] { 10, 0, 0, 1 });
        var token = StreamToken.Parse(raw, "localhost");
        Assert.Equal(StreamProtocol.Tcp, token.Protocol);
    }

    [Fact]
    public void StreamToken_Parse_StreamId_Preserved()
    {
        var raw = MakeRawToken(0xDEADBEEFu, 9999, 1, 1, new byte[] { 10, 10, 10, 10 });
        var token = StreamToken.Parse(raw, "127.0.0.1");
        Assert.Equal(0xDEADBEEFu, token.StreamId);
    }

    [Fact]
    public void StreamToken_Parse_TooShort_Throws()
    {
        var raw = new byte[10];  // too short — must be 24 bytes
        Assert.Throws<ArgumentException>(() => StreamToken.Parse(raw, "127.0.0.1"));
    }

    [Fact]
    public void StreamToken_SizeBytes_Is_24()
    {
        Assert.Equal(24, StreamToken.SizeBytes);
    }

    // ── RawToken msgpack serialization ───────────────────────────────────────

    [Fact]
    public void RawToken_MsgPack_RoundTrip()
    {
        var rawBytes = new byte[24];
        for (int i = 0; i < 24; i++) rawBytes[i] = (byte)i;
        var tok = new RawToken(rawBytes);
        var bytes = MessagePackSerializer.Serialize(tok);
        var tok2 = MessagePackSerializer.Deserialize<RawToken>(bytes);
        Assert.Equal(tok.Data, tok2.Data);
    }

    [Fact]
    public void RawToken_MsgPack_Empty_Data()
    {
        var tok = new RawToken(Array.Empty<byte>());
        var bytes = MessagePackSerializer.Serialize(tok);
        var tok2 = MessagePackSerializer.Deserialize<RawToken>(bytes);
        Assert.Empty(tok2.Data);
    }

    [Fact]
    public void RawToken_Empty_Sentinel()
    {
        Assert.Empty(RawToken.Empty.Data);
    }

    [Fact]
    public void RawToken_MsgPack_Is_1Element_Array()
    {
        // MSGPACK_DEFINE_ARRAY(data) → fixarray(1) containing bin8(24)
        var rawBytes = new byte[24];
        var tok = new RawToken(rawBytes);
        var bytes = MessagePackSerializer.Serialize(tok);
        var reader = new MessagePackReader(new System.Buffers.ReadOnlySequence<byte>(bytes));
        Assert.Equal(1, reader.ReadArrayHeader());  // 1-element fixarray
        // Next value should be a byte array (bin format)
        var inner = reader.ReadBytes();
        Assert.NotNull(inner);
        Assert.Equal(24, (int)inner!.Value.Length);
    }
}
