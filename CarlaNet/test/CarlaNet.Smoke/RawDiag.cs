// Raw protocol diagnostic — bypasses MsgPackRpcClient entirely.
// Sends a version() request as raw msgpack and hex-dumps the first bytes received.
using System.Buffers;
using System.Net.Sockets;
using MessagePack;

namespace CarlaNet.Smoke;

public static class RawDiag
{
    public static async Task RunAsync(string host, int port)
    {
        Console.WriteLine($"\n[RAW DIAG] Connecting to {host}:{port}");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        var stream = tcp.GetStream();
        Console.WriteLine("[RAW DIAG] Connected");

        // Build version() request with CARLA Metadata prefix:
        // [0, msgid, "version", [[false]]]
        // Metadata::MakeSync() = {_asynchronous_call=false} -> [false]
        // Source: carla/rpc/Client.h line 33 and carla/rpc/Metadata.h
        var buf = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buf);
        writer.WriteArrayHeader(4);
        writer.Write(0);           // type = request
        writer.Write((uint)1);     // msg_id
        writer.Write("version");   // method
        writer.WriteArrayHeader(1); // params: 1 element (Metadata only, no user args)
        writer.WriteArrayHeader(1); // Metadata = [false]
        writer.Write(false);        // _asynchronous_call = false
        writer.Flush();

        byte[] request = buf.WrittenSpan.ToArray();
        Console.WriteLine($"[RAW DIAG] Sending {request.Length} bytes: {Hex(request)}");
        Console.WriteLine($"[RAW DIAG] (decoded: [0, 1, \"version\", [[false]]])");
        await stream.WriteAsync(request);
        Console.WriteLine("[RAW DIAG] Sent. Waiting for response (3s timeout)...");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        byte[] recv = new byte[1024];
        int n = 0;
        try
        {
            n = await stream.ReadAsync(recv.AsMemory(0, 1024), cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[RAW DIAG] TIMEOUT — server sent nothing in 3 seconds");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RAW DIAG] Read error: {ex.Message}");
            return;
        }

        if (n == 0)
        {
            Console.WriteLine("[RAW DIAG] Server closed connection immediately");
            return;
        }

        Console.WriteLine($"[RAW DIAG] Received {n} bytes: {Hex(recv.AsSpan(0, n))}");

        try
        {
            var reader = new MessagePackReader(new ReadOnlySequence<byte>(recv, 0, n));
            int arrLen = reader.ReadArrayHeader();
            int type   = reader.ReadInt32();
            uint msgId = reader.ReadUInt32();
            bool hasError = !reader.TryReadNil();
            Console.WriteLine($"[RAW DIAG] Parsed: type={type} msg_id={msgId} array_len={arrLen} has_error={hasError}");
            if (!hasError)
            {
                var result = MessagePackSerializer.Deserialize<object>(ref reader);
                Console.WriteLine($"[RAW DIAG] Result: {result}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RAW DIAG] Parse error: {ex.Message}");
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var arr = bytes.ToArray();
        return string.Join(" ", arr.Take(32).Select(b => b.ToString("X2")))
               + (arr.Length > 32 ? "..." : "");
    }
}
