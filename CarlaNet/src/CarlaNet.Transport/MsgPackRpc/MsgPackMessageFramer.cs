// Splits a msgpack byte stream into whole top-level messages, in time proportional to its size.
//
// Used on the receive side of both MsgPackRpcClient and MsgPackRpcServer in place of
// MessagePackStreamReader, whose cost grows with the square of a message's size (see
// MsgPackMessageScanner). Bytes are read into one buffer that doubles when a message outgrows it;
// the scanner resumes where it stopped after each read.
namespace CarlaNet.Transport.MsgPackRpc;

/// <summary>
/// Reads complete msgpack messages from a stream. Each message returned belongs to the caller: the
/// framer never writes to its bytes again, so it may be read after the next <see cref="ReadAsync"/>.
/// </summary>
internal sealed class MsgPackMessageFramer
{
    /// Messages up to this size are copied out, so the buffer they arrived in can be reused. A larger
    /// message is handed over in the buffer itself and the framer starts a fresh one, rather than
    /// copying a reply of tens of megabytes once more.
    internal const int CopyLimit = 64 * 1024;

    private const int DefaultBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly int _initialSize;
    private readonly MsgPackMessageScanner _scanner = new();
    private byte[] _buffer;
    private int _start;   // first byte of the message being scanned
    private int _scan;    // first byte the scanner has not consumed
    private int _end;     // end of the bytes read so far

    public MsgPackMessageFramer(Stream stream, int initialBufferSize = DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialBufferSize, 1);
        _stream = stream;
        _initialSize = initialBufferSize;
        _buffer = new byte[initialBufferSize];
    }

    /// <summary>
    /// The next complete top-level message, or null once the stream ends. A message cut off by the
    /// end of the stream is discarded. Exceptions from the stream, and a
    /// <see cref="MessagePackSerializationException"/> for bytes that are not msgpack, propagate.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // A previous read may have brought in several messages, so look before reading.
            if (_scanner.TryAdvance(_buffer.AsSpan(0, _end), ref _scan))
                return TakeMessage();

            MakeRoom();
            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            _end += read;
        }
    }

    private ReadOnlyMemory<byte> TakeMessage()
    {
        int length = _scan - _start;
        if (length <= CopyLimit)
        {
            byte[] copy = _buffer.AsSpan(_start, length).ToArray();
            _start = _scan;
            return copy;
        }

        var message = new ReadOnlyMemory<byte>(_buffer, _start, length);
        int tail = _end - _scan;
        byte[] fresh = GC.AllocateUninitializedArray<byte>(Math.Max(_initialSize, tail));
        _buffer.AsSpan(_scan, tail).CopyTo(fresh);
        _buffer = fresh;
        _start = _scan = 0;
        _end = tail;
        return message;
    }

    /// Make space after the buffered bytes for the next read, keeping the message being scanned.
    private void MakeRoom()
    {
        int live = _end - _start;
        if (live == 0)
        {
            _start = _scan = _end = 0;
            return;
        }
        if (_end < _buffer.Length) return;

        // Slide the message to the front when that frees at least half the buffer; otherwise double
        // it. Either way every byte is moved O(1) times on average, so framing stays linear.
        byte[] target = _buffer;
        if (live > _buffer.Length / 2)
        {
            long doubled = Math.Min(2L * _buffer.Length, Array.MaxLength);
            if (doubled <= live)
                throw new InvalidDataException($"A msgpack message exceeds {Array.MaxLength} bytes.");
            target = GC.AllocateUninitializedArray<byte>((int)doubled);
        }
        _buffer.AsSpan(_start, live).CopyTo(target);
        _buffer = target;
        _scan -= _start;
        _end = live;
        _start = 0;
    }
}
