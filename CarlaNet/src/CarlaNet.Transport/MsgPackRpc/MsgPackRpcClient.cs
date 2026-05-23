// §5.1, §7 — msgpack-RPC over TCP matching rpclib wire format.
// Frame: [uint32 LE length][msgpack payload]
// Request payload:  [0, msg_id, "method", [args...]]
// Response payload: [1, msg_id, error_or_nil, result_or_nil]
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CarlaNet.Transport.MsgPackRpc;

internal sealed class MsgPackRpcClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private readonly TimeSpan _timeout;
    private readonly ILogger? _log;
    private readonly Task _readerTask;
    private uint _nextMsgId;
    private volatile bool _disposed;

    public MsgPackRpcClient(string host, int port, TimeSpan timeout, ILogger? logger = null)
    {
        _timeout = timeout;
        _log = logger;
        _tcp = new TcpClient();
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
        _readerTask = RunReaderAsync();
    }

    public async Task<T> CallAsync<T>(string method, params object?[] args)
    {
        uint msgId = Interlocked.Increment(ref _nextMsgId);
        byte[] frame = BuildRequestFrame(msgId, method, args);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[msgId] = tcs;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await _stream.WriteAsync(frame).ConfigureAwait(false); }
        finally { _writeLock.Release(); }

        byte[] responsePayload = await tcs.Task.WaitAsync(_timeout).ConfigureAwait(false);
        return UnpackResult<T>(responsePayload);
    }

    public async Task CallVoidAsync(string method, params object?[] args)
        => await CallAsync<object?>(method, args).ConfigureAwait(false);

    private static byte[] BuildRequestFrame(uint msgId, string method, object?[] args)
    {
        // [0, msg_id, method_str, [args]]
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(0);          // type = request
        writer.Write(msgId);
        writer.Write(method);
        writer.WriteArrayHeader(args.Length);
        foreach (var arg in args)
            MessagePackSerializer.Serialize(ref writer, arg);
        writer.Flush();

        byte[] payload = buffer.WrittenSpan.ToArray();
        // Prepend 4-byte LE length prefix (rpclib convention)
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static T UnpackResult<T>(byte[] responsePayload)
    {
        var reader = new MessagePackReader(responsePayload);
        int count = reader.ReadArrayHeader();
        if (count != 4) throw new InvalidDataException($"Unexpected response array size {count}");
        reader.ReadInt32();       // type = 1
        reader.ReadUInt32();      // msg_id (already matched by reader loop)
        if (!reader.TryReadNil()) // error field
        {
            string err = reader.ReadString() ?? "unknown error";
            throw new CarlaRpcException(err);
        }
        return MessagePackSerializer.Deserialize<T>(ref reader);
    }

    private async Task RunReaderAsync()
    {
        byte[] lenBuf = new byte[4];
        try
        {
            while (!_disposed)
            {
                await ReadExactAsync(lenBuf, 4).ConfigureAwait(false);
                uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                byte[] payload = new byte[payloadLen];
                await ReadExactAsync(payload, (int)payloadLen).ConfigureAwait(false);

                // Peek msg_id from the response array (type=1 at [0], msg_id at [1])
                var peekReader = new MessagePackReader(payload);
                peekReader.ReadArrayHeader();
                peekReader.ReadInt32(); // type
                uint msgId = peekReader.ReadUInt32();

                if (_pending.TryRemove(msgId, out var tcs))
                    tcs.SetResult(payload);
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            _log?.LogError(ex, "RPC reader loop failed");
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(ex);
        }
    }

    private async Task ReadExactAsync(byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
            read += await _stream.ReadAsync(buf.AsMemory(read, count - read)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _stream.Close();
        _tcp.Close();
        try { await _readerTask.ConfigureAwait(false); } catch { /* already logged */ }
        _writeLock.Dispose();
    }
}
