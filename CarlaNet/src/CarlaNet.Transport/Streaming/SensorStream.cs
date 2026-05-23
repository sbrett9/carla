// §9 — per-sensor TCP connection. One SensorStream per subscription.
// Wire: [uint32 LE total_size][header_bytes + payload_bytes]
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

    internal SensorStream(string host, int port, Action<SensorFrame> callback)
    {
        _callback = callback;
        _tcp = new TcpClient();
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
        _readerTask = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(lenBuf, 4, ct).ConfigureAwait(false);
                int totalSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                byte[] combined = new byte[totalSize];
                await ReadExactAsync(combined, totalSize, ct).ConfigureAwait(false);
                _callback(new SensorFrame(combined));
            }
        }
        catch (OperationCanceledException) { }
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
