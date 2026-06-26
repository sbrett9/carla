using System.Buffers.Binary;
using System.IO.Compression;

namespace CarlaNet.Recording;

/// <summary>
/// Zero-dependency PNG writer: a CARLA camera BGRA frame -> 8-bit truecolor (RGB) PNG. Lossless. The
/// alpha channel is dropped (it is a meaningless constant 255 for CARLA cameras). The IDAT zlib stream
/// is produced with .NET's <see cref="ZLibStream"/> and chunk checksums with a small built-in CRC-32,
/// so there is no third-party imaging dependency. Scanlines use filter 0 (None) for simplicity.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static void WriteBgraToFile(ReadOnlyMemory<byte> bgra, int width, int height, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        WriteBgra(bgra, width, height, fs);
    }

    public static void WriteBgra(ReadOnlyMemory<byte> bgra, int width, int height, Stream output)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("invalid image dimensions");
        if (bgra.Length < (long)width * height * 4)
            throw new ArgumentException("BGRA buffer too small for the given dimensions");

        output.Write(Signature, 0, Signature.Length);

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // color type 2 = truecolor RGB
        ihdr[10] = 0;   // compression method (deflate)
        ihdr[11] = 0;   // filter method
        ihdr[12] = 0;   // interlace = none
        WriteChunk(output, "IHDR", ihdr);

        // IDAT: per-scanline [filter=0][RGB...] bytes, zlib-compressed.
        byte[] compressed;
        var span = bgra.Span;
        using (var ms = new MemoryStream(width * height * 3 / 2 + 1024))
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = width * 3;
                byte[] line = new byte[stride + 1];
                line[0] = 0;   // filter type None
                for (int y = 0; y < height; y++)
                {
                    int src = y * width * 4;
                    int di = 1;
                    for (int x = 0; x < width; x++)
                    {
                        line[di++] = span[src + 2];   // R
                        line[di++] = span[src + 1];   // G
                        line[di++] = span[src + 0];   // B
                        src += 4;
                    }
                    zlib.Write(line, 0, line.Length);
                }
            }
            compressed = ms.ToArray();
        }
        WriteChunk(output, "IDAT", compressed);

        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        output.Write(len);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        output.Write(typeBytes);

        if (data.Length > 0) output.Write(data);

        uint crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
