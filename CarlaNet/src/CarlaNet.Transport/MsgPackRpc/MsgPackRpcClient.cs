// §5.1, §7 — msgpack-RPC over TCP matching rpclib wire format.
//
// WIRE FORMAT (verified from rpclib source — Build/_deps/rpclib-src/):
//   rpclib uses raw msgpack streaming with NO length prefix.
//   Sends/receives raw msgpack bytes via async_write/async_read_some + unpacker.
//   MessagePackStreamReader handles message boundary detection on the receive side.
//
// Request:  [0, msg_id, "method_name", [arg0, arg1, ...]]  — raw msgpack array
// Response: [1, msg_id, error_or_nil, result_or_nil]        — raw msgpack array
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CarlaNet.Transport.MsgPackRpc;

internal sealed class MsgPackRpcClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ReadOnlySequence<byte>>> _pending = new();
    private TimeSpan _timeout;
    private readonly ILogger? _log;
    private readonly Task _readerTask;
    private uint _nextMsgId = uint.MaxValue; // Interlocked.Increment wraps to 0 on first call
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

    /// Update the per-call timeout. Affects all subsequent CallAsync invocations.
    public void SetTimeout(TimeSpan timeout) => _timeout = timeout;

    public async Task<T> CallAsync<T>(string method, params object?[] args)
    {
        uint msgId = Interlocked.Increment(ref _nextMsgId);
        byte[] request = BuildRequest(msgId, method, args);

        var tcs = new TaskCompletionSource<ReadOnlySequence<byte>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[msgId] = tcs;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await _stream.WriteAsync(request).ConfigureAwait(false); }
        finally { _writeLock.Release(); }

        ReadOnlySequence<byte> responseSeq = await tcs.Task.WaitAsync(_timeout).ConfigureAwait(false);
        return UnpackResult<T>(responseSeq);
    }

    public async Task CallVoidAsync(string method, params object?[] args)
        => await CallAsync<object?>(method, args).ConfigureAwait(false);

    private static byte[] BuildRequest(uint msgId, string method, object?[] args)
    {
        // Raw msgpack — no length prefix (rpclib uses streaming unpacker).
        // CARLA wraps every bound function with Metadata as the first param.
        // Source: carla/rpc/Client.h line 33 — _client.call(fn, Metadata::MakeSync(), args...)
        // Metadata::MakeSync() = {_asynchronous_call=false} => serializes as [false]
        // So params array is: [[false], arg0, arg1, ...]
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(0);                    // type = request
        writer.Write(msgId);
        writer.Write(method);
        writer.WriteArrayHeader(1 + args.Length);  // Metadata + user args
        // Metadata::MakeSync() serializes as [false]
        writer.WriteArrayHeader(1);
        writer.Write(false);                // _asynchronous_call = false
        foreach (var arg in args)
            MessagePackSerializer.Serialize(ref writer, arg);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static T UnpackResult<T>(ReadOnlySequence<byte> seq)
    {
        var reader = new MessagePackReader(seq);
        int count = reader.ReadArrayHeader();
        if (count != 4) throw new InvalidDataException($"Unexpected response array size {count}");
        reader.ReadInt32();        // type = 1
        reader.ReadUInt32();       // msg_id (already correlated by reader loop)
        if (!reader.TryReadNil())  // error field: nil = success, str = error
        {
            string err = reader.ReadString() ?? "unknown error";
            throw new CarlaRpcException(err);
        }
        // Result field — two cases (verified against carla/rpc/Response.h):
        //
        // Response<T>    → MSGPACK_DEFINE_ARRAY(_data), _data = std::variant<ResponseError,T>
        //   error:   [[0, ["msg"]]]
        //   success: [[1, value]]
        //
        // Response<void> → MSGPACK_DEFINE_ARRAY(_data), _data = std::optional<ResponseError>
        //   success: [[false]]          (optional empty → [false] per MsgPackAdaptors.h)
        //   error:   [[true, ["msg"]]]  (optional has value → [true, val])
        int outer = reader.ReadArrayHeader();  // MSGPACK_DEFINE_ARRAY wraps in 1-element array
        if (outer == 0) return default!;
        int inner = reader.ReadArrayHeader();  // variant [idx,val] = 2; optional [bool] = 1, [bool,val] = 2

        if (inner == 1)
        {
            // Response<void> success: [[false]] — optional is empty
            reader.Skip();
            return default!;
        }

        // inner == 2: Response<void> error [[true,[msg]]] or Response<T> variant [[idx,val]]
        // Distinguish by next msgpack type: bool = void error path, int = value variant
        if (reader.NextMessagePackType == MessagePackType.Boolean)
        {
            reader.ReadBoolean(); // true (has error); false shouldn't reach inner==2 but guard anyway
            reader.ReadArrayHeader(); // ResponseError MSGPACK_DEFINE_ARRAY(_what)
            string carlaErr = reader.ReadString() ?? "unknown CARLA error";
            throw new CarlaRpcException(carlaErr);
        }

        int idx = reader.ReadInt32(); // variant index: 0=ResponseError, 1=T
        if (idx == 0)
        {
            reader.ReadArrayHeader(); // ResponseError MSGPACK_DEFINE_ARRAY(_what)
            string carlaErr = reader.ReadString() ?? "unknown CARLA error";
            throw new CarlaRpcException(carlaErr);
        }
        return MessagePackSerializer.Deserialize<T>(ref reader);
    }

    private async Task RunReaderAsync()
    {
        // MessagePackStreamReader reads from the raw TCP stream and returns
        // one complete msgpack message at a time with no framing required.
        var msgpackReader = new MessagePackStreamReader(_stream);
        try
        {
            while (!_disposed)
            {
                ReadOnlySequence<byte>? msgSeq = await msgpackReader
                    .ReadAsync(CancellationToken.None).ConfigureAwait(false);

                if (msgSeq is null) break; // stream closed

                // Peek the msg_id without consuming: [type, msg_id, ...]
                var peekReader = new MessagePackReader(msgSeq.Value);
                peekReader.ReadArrayHeader();
                peekReader.ReadInt32();          // type
                uint msgId = peekReader.ReadUInt32();

                if (_pending.TryRemove(msgId, out var tcs))
                {
                    // MessagePackStreamReader reuses internal buffers on the next ReadAsync.
                    // Copy the sequence to a flat array so UnpackResult can safely read it later.
                    var snapshot = new ReadOnlySequence<byte>(msgSeq.Value.ToArray());
                    tcs.SetResult(snapshot);
                }
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            _log?.LogError(ex, "RPC reader loop failed");
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(ex);
        }
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
