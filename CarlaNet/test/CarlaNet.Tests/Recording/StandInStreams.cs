using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport.Streaming;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Recording;

/// <summary>
/// A stand-in for the server's streaming side: a subscriber connects, names its stream by id, and is
/// sent length-prefixed frames of a 48-byte sensor header and a payload -- the world observer's
/// snapshots and a camera's images alike.
/// </summary>
internal sealed class StandInStreams : IDisposable
{
    private readonly TimeSpan _patience;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<NetworkStream>> _subscribers = new();
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private readonly CancellationTokenSource _stop = new();

    /// <param name="patience">How long a send waits for its stream's subscriber to connect.</param>
    public StandInStreams(TimeSpan patience)
    {
        _patience = patience;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    /// The 24-byte token a client subscribes to this stream with.
    public byte[] Token(uint streamId)
    {
        var token = new byte[StreamToken.SizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(token, streamId);
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(4), (ushort)Port);
        token[6] = (byte)StreamProtocol.Tcp;
        token[7] = (byte)StreamAddressType.Ipv4;
        IPAddress.Loopback.GetAddressBytes().CopyTo(token, 8);
        return token;
    }

    public async Task SendAsync(uint streamId, ulong frame, double timestamp, Transform sensor, byte[] payload)
    {
        NetworkStream stream = await Subscriber(streamId).Task.WaitAsync(_patience);
        var message = new byte[4 + SensorFrame.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)(SensorFrame.HeaderSize + payload.Length));
        Span<byte> header = message.AsSpan(4, SensorFrame.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], frame);
        BinaryPrimitives.WriteDoubleLittleEndian(header[16..], timestamp);
        WriteTransform(header[24..], sensor);
        payload.CopyTo(message.AsSpan(4 + SensorFrame.HeaderSize));
        await stream.WriteAsync(message);
    }

    /// A transform as the server writes one: location then rotation, six little-endian floats.
    public static void WriteTransform(Span<byte> target, Transform transform)
    {
        BinaryPrimitives.WriteSingleLittleEndian(target, transform.Location.X);
        BinaryPrimitives.WriteSingleLittleEndian(target[4..], transform.Location.Y);
        BinaryPrimitives.WriteSingleLittleEndian(target[8..], transform.Location.Z);
        BinaryPrimitives.WriteSingleLittleEndian(target[12..], transform.Rotation.Pitch);
        BinaryPrimitives.WriteSingleLittleEndian(target[16..], transform.Rotation.Yaw);
        BinaryPrimitives.WriteSingleLittleEndian(target[20..], transform.Rotation.Roll);
    }

    private TaskCompletionSource<NetworkStream> Subscriber(uint streamId) =>
        _subscribers.GetOrAdd(streamId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(connection);
                NetworkStream stream = connection.GetStream();
                var id = new byte[4];
                await stream.ReadExactlyAsync(id, _stop.Token);
                Subscriber(BinaryPrimitives.ReadUInt32LittleEndian(id)).TrySetResult(stream);
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException
                                            or SocketException or IOException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (TcpClient connection in _connections) connection.Dispose();
        _stop.Dispose();
    }
}
