// Finds where each msgpack message ends in a byte stream that arrives in pieces.
//
// rpclib sends raw msgpack with no length prefix, so the only way to find a message's end is to walk
// its tokens. MessagePackStreamReader walks the buffered message from its first byte every time more
// bytes arrive, so a message delivered in k reads costs k walks: framing a 38 MB reply of 7.6 M
// floats took ~114 s. This scanner remembers where it stopped - how many values each open array or
// map still holds, and how many payload bytes of the current value are still to come - and resumes
// from there, so every byte of a message is examined once.
using System.Buffers.Binary;

namespace CarlaNet.Transport.MsgPackRpc;

/// <summary>
/// Incremental msgpack message boundary scanner. Feed it a buffer and a position; it consumes whole
/// tokens until either the top-level message ends or the buffer runs out, and carries its state to
/// the next call.
/// </summary>
internal sealed class MsgPackMessageScanner
{
    // Values still to come in each open container, innermost last. The bottom entry is the message
    // itself, which is one value. A map of n pairs is 2n values.
    private long[] _remaining = new long[16];
    private int _depth;

    // Bytes of the current value still to pass over: a scalar's body, or a str/bin/ext payload.
    private long _skip;

    /// True from a message's first byte until its last.
    public bool InMessage => _depth > 0 || _skip > 0;

    /// <summary>
    /// Scan <paramref name="buffer"/> from <paramref name="position"/>. Returns true when a message
    /// ends, with <paramref name="position"/> just past its last byte; the next call starts a new
    /// message there. Returns false when the buffer runs out first, with
    /// <paramref name="position"/> at the first byte not consumed - which may be the start of a
    /// token header that has not fully arrived. The next call must pass the same bytes from that
    /// position on, followed by whatever has arrived since.
    /// </summary>
    public bool TryAdvance(ReadOnlySpan<byte> buffer, ref int position)
    {
        int pos = position;
        if (!InMessage)
        {
            if (pos >= buffer.Length) return false;
            _remaining[0] = 1;
            _depth = 1;
        }

        while (true)
        {
            if (_skip > 0)
            {
                long available = buffer.Length - pos;
                if (available < _skip)
                {
                    _skip -= available;
                    position = buffer.Length;
                    return false;
                }
                pos += (int)_skip;
                _skip = 0;
            }

            if (_depth == 0)
            {
                position = pos;
                return true;
            }

            if (!TryReadHeader(buffer[pos..], out int header, out long body, out long children))
            {
                position = pos;
                return false;
            }
            pos += header;

            // The token begins one value of the innermost open container. A container with values
            // opens a level; any level left with nothing to come closes, and so may its parents.
            // Closing before the body has been passed over is safe: the body holds no tokens.
            _remaining[_depth - 1]--;
            if (children > 0) Push(children);
            while (_depth > 0 && _remaining[_depth - 1] == 0) _depth--;
            _skip = body;
        }
    }

    private void Push(long children)
    {
        if (_depth == _remaining.Length) Array.Resize(ref _remaining, _depth * 2);
        _remaining[_depth++] = children;
    }

    /// <summary>
    /// Decode the header of the token at the start of <paramref name="span"/>: the bytes that say how
    /// long the token is. Returns false when those bytes have not all arrived. <paramref name="body"/>
    /// is the bytes after the header that belong to the token; <paramref name="children"/> is the
    /// number of values an array or map holds, and zero for anything else.
    /// </summary>
    private static bool TryReadHeader(ReadOnlySpan<byte> span, out int header, out long body, out long children)
    {
        header = 1;
        body = 0;
        children = 0;
        if (span.IsEmpty) return false;

        byte code = span[0];
        switch (code)
        {
            case <= 0x7f:                                       // positive fixint
            case >= 0xe0:                                       // negative fixint
            case 0xc0: case 0xc2: case 0xc3:                    // nil, false, true
                return true;
            case <= 0x8f: children = 2 * (code & 0x0f); return true;   // fixmap
            case <= 0x9f: children = code & 0x0f; return true;         // fixarray
            case <= 0xbf: body = code & 0x1f; return true;             // fixstr

            case 0xcc: case 0xd0: body = 1; return true;        // uint8, int8
            case 0xcd: case 0xd1: body = 2; return true;        // uint16, int16
            case 0xce: case 0xd2: case 0xca: body = 4; return true;   // uint32, int32, float32
            case 0xcf: case 0xd3: case 0xcb: body = 8; return true;   // uint64, int64, float64

            // fixext: a type byte, then 1, 2, 4, 8 or 16 data bytes.
            case 0xd4: body = 2; return true;
            case 0xd5: body = 3; return true;
            case 0xd6: body = 5; return true;
            case 0xd7: body = 9; return true;
            case 0xd8: body = 17; return true;

            case 0xc4: case 0xd9:                               // bin8, str8
                if (span.Length < 2) return false;
                header = 2; body = span[1]; return true;
            case 0xc5: case 0xda:                               // bin16, str16
                if (span.Length < 3) return false;
                header = 3; body = BinaryPrimitives.ReadUInt16BigEndian(span[1..]); return true;
            case 0xc6: case 0xdb:                               // bin32, str32
                if (span.Length < 5) return false;
                header = 5; body = BinaryPrimitives.ReadUInt32BigEndian(span[1..]); return true;

            // ext: the length counts the data only; the type byte follows the length.
            case 0xc7:
                if (span.Length < 2) return false;
                header = 2; body = 1 + span[1]; return true;
            case 0xc8:
                if (span.Length < 3) return false;
                header = 3; body = 1 + BinaryPrimitives.ReadUInt16BigEndian(span[1..]); return true;
            case 0xc9:
                if (span.Length < 5) return false;
                header = 5; body = 1 + (long)BinaryPrimitives.ReadUInt32BigEndian(span[1..]); return true;

            case 0xdc:                                          // array16
                if (span.Length < 3) return false;
                header = 3; children = BinaryPrimitives.ReadUInt16BigEndian(span[1..]); return true;
            case 0xdd:                                          // array32
                if (span.Length < 5) return false;
                header = 5; children = BinaryPrimitives.ReadUInt32BigEndian(span[1..]); return true;
            case 0xde:                                          // map16
                if (span.Length < 3) return false;
                header = 3; children = 2L * BinaryPrimitives.ReadUInt16BigEndian(span[1..]); return true;
            case 0xdf:                                          // map32
                if (span.Length < 5) return false;
                header = 5; children = 2L * BinaryPrimitives.ReadUInt32BigEndian(span[1..]); return true;

            default:                                            // 0xc1, never used
                throw new MessagePackSerializationException($"Invalid msgpack code 0x{code:x2}.");
        }
    }
}
