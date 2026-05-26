// §5.1, §7 — msgpack-RPC SERVER matching rpclib wire format.
//
// WIRE FORMAT (verified — see CarlaNetSupplementary.md §1):
//   rpclib uses raw msgpack streaming with NO length prefix.
//   MessagePackStreamReader handles message boundary detection on the receive side.
//
// Request:  [0, msg_id, "method_name", [[false], arg0, arg1, ...]]
//             (the outer [false] is the Metadata::MakeSync wrapper — every
//              bound CARLA server function gets Metadata as its first implicit arg)
//
// Response: [1, msg_id, error_or_nil, result_or_nil]   (rpclib detail/response.cc)
//
// This is the SERVER counterpart to MsgPackRpcClient. The TM acts as both a
// client (sending commands to the CARLA simulator on port 2000) AND a server
// (receiving the per-frame snapshot callback the simulator pushes back on the
// TM's own port — default 8000). See TrafficManagerServer.h for the registered
// handler set.
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CarlaNet.Transport.MsgPackRpc.Server;

/// <summary>
/// Async TCP msgpack-RPC server compatible with rpclib's wire format.
/// Accepts many concurrent connections; each connection is serviced by its
/// own <see cref="Task"/>. Handlers registered via
/// <c>RegisterHandler</c> / <c>RegisterVoidHandler</c> are invoked on
/// thread-pool threads (one <see cref="Task"/> per request).
/// </summary>
public sealed class MsgPackRpcServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, IRpcHandler> _handlers = new();
    private readonly ILogger? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Task> _connectionTasks = new();
    private Task? _acceptLoop;
    private int _nextConnectionId;
    private volatile bool _disposed;

    public MsgPackRpcServer(int port, ILogger? logger = null)
        : this(IPAddress.Any, port, logger) { }

    public MsgPackRpcServer(IPAddress address, int port, ILogger? logger = null)
    {
        _listener = new TcpListener(address, port);
        _log = logger;
        Port = port;
    }

    /// <summary>The TCP port the server is bound to.</summary>
    public int Port { get; }

    /// <summary>
    /// True once <see cref="StartAsync"/> has been called and the listener is
    /// accepting connections.
    /// </summary>
    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    // ── Handler registration ───────────────────────────────────────────────

    /// <summary>
    /// Register a handler with no arguments and a return value.
    /// </summary>
    public void RegisterHandler<TResult>(string method, Func<TResult> handler)
        => _handlers[method] = new ZeroArgHandler<TResult>(handler);

    /// <summary>
    /// Register a handler with one strongly-typed argument and a return value.
    /// </summary>
    public void RegisterHandler<TArg, TResult>(string method, Func<TArg, TResult> handler)
        => _handlers[method] = new OneArgHandler<TArg, TResult>(handler);

    /// <summary>
    /// Register a handler with two strongly-typed arguments and a return value.
    /// </summary>
    public void RegisterHandler<TArg1, TArg2, TResult>(string method, Func<TArg1, TArg2, TResult> handler)
        => _handlers[method] = new TwoArgHandler<TArg1, TArg2, TResult>(handler);

    /// <summary>
    /// Register a handler with three strongly-typed arguments and a return value.
    /// </summary>
    public void RegisterHandler<TArg1, TArg2, TArg3, TResult>(string method, Func<TArg1, TArg2, TArg3, TResult> handler)
        => _handlers[method] = new ThreeArgHandler<TArg1, TArg2, TArg3, TResult>(handler);

    /// <summary>Register a void handler with no arguments.</summary>
    public void RegisterVoidHandler(string method, Action handler)
        => _handlers[method] = new ZeroArgVoidHandler(handler);

    /// <summary>Register a void handler with one argument.</summary>
    public void RegisterVoidHandler<TArg>(string method, Action<TArg> handler)
        => _handlers[method] = new OneArgVoidHandler<TArg>(handler);

    /// <summary>Register a void handler with two arguments.</summary>
    public void RegisterVoidHandler<TArg1, TArg2>(string method, Action<TArg1, TArg2> handler)
        => _handlers[method] = new TwoArgVoidHandler<TArg1, TArg2>(handler);

    /// <summary>Register a void handler with three arguments.</summary>
    public void RegisterVoidHandler<TArg1, TArg2, TArg3>(string method, Action<TArg1, TArg2, TArg3> handler)
        => _handlers[method] = new ThreeArgVoidHandler<TArg1, TArg2, TArg3>(handler);

    /// <summary>True if a handler is registered for the given method name.</summary>
    public bool HasHandler(string method) => _handlers.ContainsKey(method);

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Start the listener and begin accepting connections.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_acceptLoop is not null) throw new InvalidOperationException("Server already started.");
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the listener and tear down all active connections. Waits for
    /// in-flight handlers to drain.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        try { _cts.Cancel(); } catch { /* already cancelled */ }
        try { _listener.Stop(); } catch { /* already stopped */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* expected on shutdown */ }
        }
        Task[] tasks = _connectionTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* expected */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    // ── Accept loop ───────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                _log?.LogError(ex, "AcceptTcpClientAsync failed");
                return;
            }

            client.NoDelay = true; // low-latency small writes (matches libcarla socket option)
            int connId = Interlocked.Increment(ref _nextConnectionId);
            Task task = Task.Run(() => HandleConnectionAsync(connId, client, ct), ct);
            _connectionTasks[connId] = task;
            _ = task.ContinueWith(_ => _connectionTasks.TryRemove(connId, out Task? _),
                TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(int connId, TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var writeLock = new SemaphoreSlim(1, 1);
            var reader = new MessagePackStreamReader(stream);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ReadOnlySequence<byte>? msgSeq;
                    try
                    {
                        msgSeq = await reader.ReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }

                    if (msgSeq is null) break; // peer closed

                    // MessagePackStreamReader reuses internal buffers — snapshot before async dispatch.
                    var snapshot = new ReadOnlySequence<byte>(msgSeq.Value.ToArray());
                    _ = Task.Run(() => DispatchAsync(snapshot, stream, writeLock, ct), ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log?.LogError(ex, "RPC connection {Id} reader loop failed", connId);
            }
            finally
            {
                writeLock.Dispose();
            }
        }
    }

    // ── Request parsing & dispatch ────────────────────────────────────────

    private async Task DispatchAsync(
        ReadOnlySequence<byte> requestBytes,
        NetworkStream outStream,
        SemaphoreSlim writeLock,
        CancellationToken ct)
    {
        uint msgId = 0;
        bool haveMsgId = false;
        bool wasNotification = false;
        try
        {
            var rdr = new MessagePackReader(requestBytes);
            int outer = rdr.ReadArrayHeader();
            // msgpack-rpc: request is 4 elements, notification is 3 (no msg_id).
            if (outer != 4 && outer != 3)
                throw new InvalidDataException($"Bad request array length {outer}");

            int type = rdr.ReadInt32();
            if (outer == 4 && type == 0)
            {
                msgId = rdr.ReadUInt32();
                haveMsgId = true;
            }
            else if (outer == 3 && type == 2)
            {
                wasNotification = true;
            }
            else
            {
                throw new InvalidDataException($"Unexpected msg type={type} with array len={outer}");
            }

            string method = rdr.ReadString() ?? throw new InvalidDataException("Method name was nil");
            int paramsCount = rdr.ReadArrayHeader();

            // Strip the leading [false] Metadata::MakeSync wrapper if present.
            // C++ CARLA always sends it; bare msgpack-rpc clients may not.
            // Detection: first param is a 1-elem array containing a bool.
            int remainingParams = paramsCount;
            if (paramsCount > 0 && rdr.NextMessagePackType == MessagePackType.Array)
            {
                var peek = rdr.CreatePeekReader();
                int hdr = peek.ReadArrayHeader();
                if (hdr == 1 && peek.NextMessagePackType == MessagePackType.Boolean)
                {
                    rdr.ReadArrayHeader();
                    rdr.ReadBoolean();
                    remainingParams = paramsCount - 1;
                }
            }

            if (!_handlers.TryGetValue(method, out var handler))
            {
                await SendErrorAsync(outStream, writeLock, msgId,
                    $"unknown method '{method}'", wasNotification).ConfigureAwait(false);
                return;
            }

            if (remainingParams != handler.ArgCount)
            {
                await SendErrorAsync(outStream, writeLock, msgId,
                    $"wrong argument count for '{method}': expected {handler.ArgCount}, got {remainingParams}",
                    wasNotification).ConfigureAwait(false);
                return;
            }

            object? result;
            try
            {
                result = handler.Invoke(ref rdr);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Handler '{Method}' threw", method);
                await SendErrorAsync(outStream, writeLock, msgId, ex.Message, wasNotification).ConfigureAwait(false);
                return;
            }

            if (!wasNotification)
                await SendResultAsync(outStream, writeLock, msgId, result, handler.HasReturn).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.LogError(ex, "Dispatch failed");
            if (haveMsgId)
            {
                try { await SendErrorAsync(outStream, writeLock, msgId, ex.Message, wasNotification).ConfigureAwait(false); }
                catch { /* connection probably dead */ }
            }
        }
    }

    // ── Response writing ──────────────────────────────────────────────────

    private static async Task SendResultAsync(
        NetworkStream stream, SemaphoreSlim writeLock,
        uint msgId, object? result, bool hasReturn)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.WriteArrayHeader(4);
        w.Write(1);            // type = response
        w.Write(msgId);
        w.WriteNil();          // error = nil (success)
        if (!hasReturn || result is null)
            w.WriteNil();
        else
            MessagePackSerializer.Serialize(result.GetType(), ref w, result);
        w.Flush();
        await WriteLockedAsync(stream, writeLock, buffer.WrittenMemory).ConfigureAwait(false);
    }

    private static async Task SendErrorAsync(
        NetworkStream stream, SemaphoreSlim writeLock,
        uint msgId, string message, bool wasNotification)
    {
        if (wasNotification) return;
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.WriteArrayHeader(4);
        w.Write(1);
        w.Write(msgId);
        w.Write(message);      // error = string
        w.WriteNil();          // result = nil
        w.Flush();
        await WriteLockedAsync(stream, writeLock, buffer.WrittenMemory).ConfigureAwait(false);
    }

    private static async Task WriteLockedAsync(
        NetworkStream stream, SemaphoreSlim writeLock, ReadOnlyMemory<byte> bytes)
    {
        await writeLock.WaitAsync().ConfigureAwait(false);
        try { await stream.WriteAsync(bytes).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MsgPackRpcServer));
    }

    // ── Handler types ─────────────────────────────────────────────────────
    //
    // Each handler subclass captures the strongly-typed delegate plus a
    // self-contained Invoke that deserializes its arguments straight out of
    // the request stream (no boxing of intermediate object[]). This lets the
    // JIT specialize each Deserialize<T> call at construction-time generic
    // instantiation.

    private interface IRpcHandler
    {
        int ArgCount { get; }
        bool HasReturn { get; }
        object? Invoke(ref MessagePackReader rdr);
    }

    private sealed class ZeroArgHandler<TResult>(Func<TResult> fn) : IRpcHandler
    {
        public int ArgCount => 0;
        public bool HasReturn => true;
        public object? Invoke(ref MessagePackReader rdr) => fn();
    }

    private sealed class OneArgHandler<TArg, TResult>(Func<TArg, TResult> fn) : IRpcHandler
    {
        public int ArgCount => 1;
        public bool HasReturn => true;
        public object? Invoke(ref MessagePackReader rdr)
        {
            var a = MessagePackSerializer.Deserialize<TArg>(ref rdr);
            return fn(a);
        }
    }

    private sealed class TwoArgHandler<TArg1, TArg2, TResult>(Func<TArg1, TArg2, TResult> fn) : IRpcHandler
    {
        public int ArgCount => 2;
        public bool HasReturn => true;
        public object? Invoke(ref MessagePackReader rdr)
        {
            var a = MessagePackSerializer.Deserialize<TArg1>(ref rdr);
            var b = MessagePackSerializer.Deserialize<TArg2>(ref rdr);
            return fn(a, b);
        }
    }

    private sealed class ThreeArgHandler<TArg1, TArg2, TArg3, TResult>(Func<TArg1, TArg2, TArg3, TResult> fn) : IRpcHandler
    {
        public int ArgCount => 3;
        public bool HasReturn => true;
        public object? Invoke(ref MessagePackReader rdr)
        {
            var a = MessagePackSerializer.Deserialize<TArg1>(ref rdr);
            var b = MessagePackSerializer.Deserialize<TArg2>(ref rdr);
            var c = MessagePackSerializer.Deserialize<TArg3>(ref rdr);
            return fn(a, b, c);
        }
    }

    private sealed class ZeroArgVoidHandler(Action fn) : IRpcHandler
    {
        public int ArgCount => 0;
        public bool HasReturn => false;
        public object? Invoke(ref MessagePackReader rdr) { fn(); return null; }
    }

    private sealed class OneArgVoidHandler<TArg>(Action<TArg> fn) : IRpcHandler
    {
        public int ArgCount => 1;
        public bool HasReturn => false;
        public object? Invoke(ref MessagePackReader rdr)
        {
            var a = MessagePackSerializer.Deserialize<TArg>(ref rdr);
            fn(a);
            return null;
        }
    }

    private sealed class TwoArgVoidHandler<TArg1, TArg2>(Action<TArg1, TArg2> fn) : IRpcHandler
    {
        public int ArgCount => 2;
        public bool HasReturn => false;
        public object? Invoke(ref MessagePackReader rdr)
        {
            var a = MessagePackSerializer.Deserialize<TArg1>(ref rdr);
            var b = MessagePackSerializer.Deserialize<TArg2>(ref rdr);
            fn(a, b);
            return null;
        }
    }

    private sealed class ThreeArgVoidHandler<TArg1, TArg2, TArg3>(Action<TArg1, TArg2, TArg3> fn) : IRpcHandler
    {
        public int ArgCount => 3;
        public bool HasReturn => false;
        public object? Invoke(ref MessagePackReader rdr)
        {
            var a = MessagePackSerializer.Deserialize<TArg1>(ref rdr);
            var b = MessagePackSerializer.Deserialize<TArg2>(ref rdr);
            var c = MessagePackSerializer.Deserialize<TArg3>(ref rdr);
            fn(a, b, c);
            return null;
        }
    }
}
