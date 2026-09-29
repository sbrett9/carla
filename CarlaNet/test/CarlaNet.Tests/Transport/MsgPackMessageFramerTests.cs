// MsgPackMessageFramer: splitting the RPC byte stream into whole msgpack messages.
//
// rpclib sends raw msgpack with no length prefix, so the framer finds each message's end by walking
// its tokens, and has to resume that walk wherever a read happened to stop - inside a header, inside
// a string, between two values of a nested map. These tests split messages at every byte, pack
// several into one read, and frame a Bahonar-sized grid reply, which the stream reader this replaced
// needed ~114 s to frame.
using System.Buffers;
using System.Diagnostics;
using CarlaNet.Transport.MsgPackRpc;
using Xunit.Abstractions;

namespace CarlaNet.Tests.Transport;

public class MsgPackMessageFramerTests(ITestOutputHelper output)
{
    // ── Test streams and messages ──────────────────────────────────────────

    /// A read-only stream that hands out the given pieces one read at a time, as a socket delivers
    /// segments. A read into a smaller buffer takes the front of the piece and leaves the rest.
    private sealed class PiecewiseStream(IEnumerable<ReadOnlyMemory<byte>> pieces) : Stream
    {
        private readonly IEnumerator<ReadOnlyMemory<byte>> _pieces = pieces.GetEnumerator();
        private ReadOnlyMemory<byte> _current;

        public override int Read(Span<byte> buffer)
        {
            while (_current.IsEmpty)
            {
                if (!_pieces.MoveNext()) return 0;
                _current = _pieces.Current;
            }
            int n = Math.Min(_current.Length, buffer.Length);
            _current.Span[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static PiecewiseStream Pieces(params byte[][] pieces)
        => new(pieces.Select(p => new ReadOnlyMemory<byte>(p)));

    private static PiecewiseStream FixedPieces(byte[] data, int pieceSize)
        => new(Enumerable.Range(0, (data.Length + pieceSize - 1) / pieceSize)
            .Select(i => new ReadOnlyMemory<byte>(data, i * pieceSize, Math.Min(pieceSize, data.Length - i * pieceSize))));

    private static async Task<List<byte[]>> ReadAll(MsgPackMessageFramer framer)
    {
        var messages = new List<byte[]>();
        while (await framer.ReadAsync() is { } message)
            messages.Add(message.ToArray());
        return messages;
    }

    /// Writes msgpack byte by byte, so every form a header can take appears - including the 16- and
    /// 32-bit forms a writer only chooses for large counts and lengths.
    private sealed class TokenWriter
    {
        private readonly List<byte> _bytes = new();

        public TokenWriter Bytes(params byte[] bytes) { _bytes.AddRange(bytes); return this; }
        public TokenWriter U16(int value) => Bytes((byte)(value >> 8), (byte)value);
        public TokenWriter U32(long value) => Bytes((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

        public TokenWriter Text(int length)
        {
            for (int i = 0; i < length; i++) _bytes.Add((byte)('a' + i % 26));
            return this;
        }

        public TokenWriter Text(string ascii) { foreach (char c in ascii) _bytes.Add((byte)c); return this; }

        public byte[] ToArray() => _bytes.ToArray();
    }

    /// One message holding every msgpack token type and header form, nested.
    private static byte[] EveryTokenType()
    {
        var t = new TokenWriter();
        t.Bytes(0xdc).U16(39);                                  // array16 of everything below
        t.Bytes(0x05);                                          // positive fixint
        t.Bytes(0xfd);                                          // negative fixint
        t.Bytes(0xc0).Bytes(0xc2).Bytes(0xc3);                  // nil, false, true
        t.Bytes(0xcc, 200);                                     // uint8
        t.Bytes(0xcd).U16(60_000);                              // uint16
        t.Bytes(0xce).U32(4_000_000_000);                       // uint32
        t.Bytes(0xcf).U32(1).U32(2);                            // uint64
        t.Bytes(0xd0, 0x9c);                                    // int8
        t.Bytes(0xd1).U16(0x8000);                              // int16
        t.Bytes(0xd2).U32(0x8000_0000);                         // int32
        t.Bytes(0xd3).U32(0xffff_ffff).U32(0xffff_fffe);        // int64
        t.Bytes(0xca).U32(0x3fc0_0000);                         // float32 1.5
        t.Bytes(0xcb).U32(0x4002_0000).U32(0);                  // float64 2.25
        t.Bytes(0xa3).Text(3);                                  // fixstr
        t.Bytes(0xd9, 40).Text(40);                             // str8
        t.Bytes(0xda).U16(300).Text(300);                       // str16
        t.Bytes(0xdb).U32(5).Text(5);                           // str32
        t.Bytes(0xc4, 3).Text(3);                               // bin8
        t.Bytes(0xc5).U16(300).Text(300);                       // bin16
        t.Bytes(0xc6).U32(4).Text(4);                           // bin32
        t.Bytes(0xd4, 0x01).Text(1);                            // fixext1
        t.Bytes(0xd5, 0x02).Text(2);                            // fixext2
        t.Bytes(0xd6, 0x03).Text(4);                            // fixext4
        t.Bytes(0xd7, 0x04).Text(8);                            // fixext8
        t.Bytes(0xd8, 0x05).Text(16);                           // fixext16
        t.Bytes(0xc7, 3, 0x06).Text(3);                         // ext8
        t.Bytes(0xc8).U16(2).Bytes(0x07).Text(2);               // ext16
        t.Bytes(0xc9).U32(1).Bytes(0x08).Text(1);               // ext32
        t.Bytes(0x90);                                          // empty fixarray
        t.Bytes(0x80);                                          // empty fixmap
        t.Bytes(0xdc).U16(3).Bytes(1, 2, 3);                    // array16 [1, 2, 3]
        t.Bytes(0xdd).U32(2).Bytes(0xa1).Text("x")              // array32 ["x", [[], {1: 2}]]
            .Bytes(0x92, 0x90, 0x81, 0x01, 0x02);
        t.Bytes(0xde).U16(2)                                    // map16 {"a": 1.5f, "b": [nil]}
            .Bytes(0xa1).Text("a").Bytes(0xca).U32(0x3fc0_0000)
            .Bytes(0xa1).Text("b").Bytes(0x91, 0xc0);
        t.Bytes(0xdf).U32(2)                                    // map32 {1: {2: {3: "deep"}}, "k": []}
            .Bytes(0x01, 0x81, 0x02, 0x81, 0x03, 0xa4).Text("deep")
            .Bytes(0xa1).Text("k").Bytes(0xdd).U32(0);
        t.Bytes(0xdf).U32(0);                                   // empty map32
        t.Bytes(0x91, 0x91, 0x91, 0x91, 0x91, 0x07);            // [[[[[7]]]]]
        t.Bytes(0x9f);                                          // fixarray of 15 float32
        for (int i = 0; i < 15; i++) t.Bytes(0xca).U32(0x4000_0000 + i);
        return t.ToArray();
    }

    /// A small message to follow another, so a framer that reads past a message's end is caught.
    private static readonly byte[] Follower = [0x92, 0x01, 0xa2, (byte)'o', (byte)'k'];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void The_Test_Message_Is_One_Valid_Msgpack_Value()
    {
        // MessagePack-CSharp's own reader must agree the message is a single value ending at its last
        // byte; otherwise the tests below would prove nothing.
        byte[] message = EveryTokenType();
        var reader = new MessagePackReader(message);
        reader.Skip();
        Assert.True(reader.End);
    }

    // ── Splits ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]          // grows the buffer many times
    [InlineData(64 * 1024)]  // the default: the message fits the buffer
    public async Task Every_Token_Type_Frames_When_Split_At_Every_Byte(int initialBufferSize)
    {
        byte[] message = EveryTokenType();
        for (int split = 1; split < message.Length; split++)
        {
            var framer = new MsgPackMessageFramer(
                Pieces(message[..split], Concat(message[split..], Follower)), initialBufferSize);

            var framed = await ReadAll(framer);

            Assert.Equal(2, framed.Count);
            Assert.Equal(message, framed[0]);
            Assert.Equal(Follower, framed[1]);
        }
    }

    [Fact]
    public async Task A_Message_Delivered_One_Byte_Per_Read_Frames_Once()
    {
        byte[] stream = Concat(EveryTokenType(), Follower, EveryTokenType());
        var framer = new MsgPackMessageFramer(FixedPieces(stream, 1), initialBufferSize: 4);

        var framed = await ReadAll(framer);

        Assert.Equal(3, framed.Count);
        Assert.Equal(EveryTokenType(), framed[0]);
        Assert.Equal(Follower, framed[1]);
        Assert.Equal(EveryTokenType(), framed[2]);
    }

    [Fact]
    public async Task Several_Messages_In_One_Read_Each_Frame_Once()
    {
        // Top-level scalars, a nil, and one message over the copy limit, which is handed over in the
        // buffer it arrived in with the messages after it moved to a fresh one.
        byte[] large = new TokenWriter().Bytes(0x92, 0x01, 0xc6)
            .U32(MsgPackMessageFramer.CopyLimit + 100).Text(MsgPackMessageFramer.CopyLimit + 100).ToArray();
        byte[][] messages = [EveryTokenType(), [0x07], [0xc0], large, Follower, [0xa1, (byte)'z']];
        var framer = new MsgPackMessageFramer(Pieces(Concat(messages)));

        var framed = await ReadAll(framer);

        Assert.Equal(messages.Length, framed.Count);
        for (int i = 0; i < messages.Length; i++)
            Assert.Equal(messages[i], framed[i]);
    }

    [Theory]
    [InlineData("DD0000000105", 0, 5)]          // top-level array32 header
    [InlineData("91DE00010102", 1, 3)]          // map16 header
    [InlineData("91DF0000000101C0", 1, 5)]      // map32 header
    [InlineData("91D9026162", 1, 2)]            // str8 header
    [InlineData("91DB000000026162", 1, 5)]      // str32 header
    [InlineData("91C500020102", 1, 3)]          // bin16 header
    [InlineData("91C9000000010700", 1, 5)]      // ext32 header: length, then type
    public void A_Header_Straddling_Reads_Is_Resumed_From_Its_First_Byte(string hex, int headerAt, int headerLength)
    {
        // The scanner consumes nothing of a header it cannot finish, so the bytes it needs are still
        // in front of it once the rest arrives.
        byte[] message = Convert.FromHexString(hex);
        for (int cut = headerAt + 1; cut < headerAt + headerLength; cut++)
        {
            var scanner = new MsgPackMessageScanner();
            int position = 0;

            Assert.False(scanner.TryAdvance(message.AsSpan(0, cut), ref position));
            Assert.Equal(headerAt, position);
            Assert.True(scanner.InMessage);

            Assert.True(scanner.TryAdvance(message, ref position));
            Assert.Equal(message.Length, position);
            Assert.False(scanner.InMessage);
        }
    }

    // ── Ownership, stream end, bad input ───────────────────────────────────

    [Fact]
    public async Task A_Returned_Message_Survives_Later_Reads()
    {
        // The copy-before-reuse guarantee the RPC client and server rely on: a pending call reads its
        // reply after the reader loop has moved on. A small message is copied out of a buffer that is
        // then reused; a large one keeps the buffer it arrived in and the framer takes a fresh one.
        byte[] small = [0x92, 0x01, 0xa5, .. "first"u8];
        byte[] large = new TokenWriter().Bytes(0xc6)
            .U32(MsgPackMessageFramer.CopyLimit * 2).Text(MsgPackMessageFramer.CopyLimit * 2).ToArray();
        var rest = Enumerable.Range(0, 500).Select(i => new byte[] { 0x92, 0xcd, (byte)(i >> 8), (byte)i, 0xc3 });
        byte[] stream = Concat([small, large, .. rest, large]);
        var framer = new MsgPackMessageFramer(FixedPieces(stream, 1000), initialBufferSize: 16);

        ReadOnlyMemory<byte> first = (await framer.ReadAsync())!.Value;
        ReadOnlyMemory<byte> second = (await framer.ReadAsync())!.Value;
        var later = await ReadAll(framer);

        Assert.Equal(small, first.ToArray());
        Assert.Equal(large, second.ToArray());
        Assert.Equal(501, later.Count);
        Assert.Equal(large, later[^1]);
    }

    [Fact]
    public async Task An_Empty_Stream_Yields_No_Message()
    {
        var framer = new MsgPackMessageFramer(Pieces());
        Assert.Null(await framer.ReadAsync());
    }

    [Fact]
    public async Task A_Message_Cut_Off_By_The_End_Of_The_Stream_Is_Discarded()
    {
        byte[] message = EveryTokenType();
        var framer = new MsgPackMessageFramer(Pieces(Follower, message[..^1]));

        Assert.Equal(Follower, (await framer.ReadAsync())!.Value.ToArray());
        Assert.Null(await framer.ReadAsync());
    }

    [Fact]
    public async Task A_Byte_That_Is_Not_Msgpack_Throws()
    {
        // 0xc1 is the one code msgpack never uses. The RPC client fails its pending calls with this.
        var framer = new MsgPackMessageFramer(Pieces(new byte[] { 0x92, 0x01, 0xc1 }));
        await Assert.ThrowsAsync<MessagePackSerializationException>(async () => await framer.ReadAsync());
    }

    // ── Scale ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Bahonar_Sized_Grid_Reply_Frames_In_Linear_Time()
    {
        // The get_bare_earth_dtm_grid reply for Bahonar: [1, msgid, nil, [[1, <7,611,381 float32>]]],
        // 38 MB. MessagePackStreamReader took ~114 s to frame it from a MemoryStream.
        const int cells = 7_611_381;
        var writer = new ArrayBufferWriter<byte>(cells * 5 + 64);
        var w = new MessagePackWriter(writer);
        w.WriteArrayHeader(4);
        w.Write(1);
        w.Write(7u);
        w.WriteNil();
        w.WriteArrayHeader(1);
        w.WriteArrayHeader(2);
        w.Write(1);
        w.WriteArrayHeader(cells);
        for (int i = 0; i < cells; i++) w.Write(1600f + (i % 1000) * 0.25f);
        w.Flush();
        byte[] reply = writer.WrittenSpan.ToArray();

        // Socket-sized reads, with the next reply's first bytes arriving behind this one.
        var framer = new MsgPackMessageFramer(FixedPieces(Concat(reply, Follower), 64 * 1024));
        var clock = Stopwatch.StartNew();
        ReadOnlyMemory<byte>? framed = await framer.ReadAsync();
        clock.Stop();
        output.WriteLine($"{reply.Length:N0}-byte reply framed in {clock.Elapsed.TotalMilliseconds:F0} ms");

        Assert.NotNull(framed);
        Assert.True(framed.Value.Span.SequenceEqual(reply));
        Assert.Equal(Follower, (await framer.ReadAsync())!.Value.ToArray());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
            $"framing took {clock.Elapsed.TotalSeconds:F1} s");
    }
}
