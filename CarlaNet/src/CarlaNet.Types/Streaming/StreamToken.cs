// Source: carla/streaming/detail/Token.h — #pragma pack(push,1), 24 bytes total
// Layout: [stream_id:u32][port:u16][protocol:u8][address_type:u8][address_bytes:16]
using System.Buffers.Binary;
using System.Net;

namespace CarlaNet.Types.Streaming;

public enum StreamProtocol : byte { NotSet = 0, Tcp = 1, Udp = 2 }
public enum StreamAddressType : byte { NotSet = 0, Ipv4 = 1, Ipv6 = 2 }

public readonly struct StreamToken
{
    public const int SizeBytes = 24;

    public uint StreamId { get; }
    public ushort Port { get; }
    public StreamProtocol Protocol { get; }
    public StreamAddressType AddressType { get; }
    public IPAddress Address { get; }

    private StreamToken(uint streamId, ushort port, StreamProtocol protocol,
                        StreamAddressType addressType, IPAddress address)
    {
        StreamId = streamId; Port = port; Protocol = protocol;
        AddressType = addressType; Address = address;
    }

    public static StreamToken Parse(ReadOnlySpan<byte> raw, string serverHost)
    {
        if (raw.Length < SizeBytes)
            throw new ArgumentException($"Token must be {SizeBytes} bytes, got {raw.Length}");

        uint streamId    = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        ushort port      = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
        var protocol     = (StreamProtocol)raw[6];
        var addressType  = (StreamAddressType)raw[7];

        IPAddress address;
        if (addressType == StreamAddressType.Ipv4)
        {
            // 4 bytes IPv4, 12 bytes padding
            address = new IPAddress(raw.Slice(8, 4));
            // If the address is 0.0.0.0 (unset), fall back to the RPC server host
            if (address.Equals(IPAddress.Any))
                address = IPAddress.Parse(serverHost);
        }
        else if (addressType == StreamAddressType.Ipv6)
        {
            address = new IPAddress(raw.Slice(8, 16));
        }
        else
        {
            address = IPAddress.Parse(serverHost);
        }

        return new StreamToken(streamId, port, protocol, addressType, address);
    }
}
