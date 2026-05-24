// §9 — per-sensor TCP connection. One SensorStream per subscription.
//
// Protocol verified from LibCarla/source/carla/streaming/detail/tcp/Client.cpp:
//   1. Connect TCP to token.address:token.port
//   2. Send stream_id (uint32 LE, 4 bytes) — subscribes to the specific stream
//   3. Receive frames: [uint32 LE total_size][payload_bytes] repeatedly
//      payload = sensor header bytes (48) + sensor data bytes concatenated
using System.Buffers.Binary;
using System.Net.Sockets;

namespace CarlaNet.Transport.Streaming;

internal sealed class SensorStream : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Action<SensorFrame> _callback;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerTask;

    internal SensorStream(StreamToken token, Action<SensorFrame> callback)
    {
        _callback = callback;
        _tcp = new TcpClient();
        _tcp.NoDelay = true; // match libcarla's no_delay socket option
        _tcp.Connect(token.Address, token.Port);
        _stream = _tcp.GetStream();
        Console.Error.WriteLine($"[SensorStream] connected to {token.Address}:{token.Port}  stream_id={token.StreamId}");
        _readerTask = RunAsync(token.StreamId, _cts.Token);
        _readerTask.ContinueWith(
            t => Console.Error.WriteLine($"[SensorStream] reader FAILED: {t.Exception?.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task RunAsync(uint streamId, CancellationToken ct)
    {
        // Send stream_id to subscribe (Client.cpp lines 114-116)
        byte[] idBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(idBuf, streamId);
        await _stream.WriteAsync(idBuf, ct).ConfigureAwait(false);
        Console.Error.WriteLine($"[SensorStream] stream_id={streamId} sent, awaiting frames...");

        byte[] lenBuf = new byte[4];
        int frameCount = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(lenBuf, 4, ct).ConfigureAwait(false);
                int totalSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                byte[] combined = new byte[totalSize];
                await ReadExactAsync(combined, totalSize, ct).ConfigureAwait(false);
                frameCount++;
                if (frameCount <= 3)
                    Console.Error.WriteLine($"[SensorStream] frame #{frameCount}: {totalSize} bytes");
                try { _callback(new SensorFrame(combined)); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SensorStream] callback error (frame {frameCount}): {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SensorStream] read error after {frameCount} frames: {ex.Message}");
            throw;
        }
    }

    private async Task ReadExactAsync(byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
            read += await _stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Close();
        _tcp.Close();
        _cts.Dispose();
    }
}
