// Ported from Eclipse SUMO's reference TraCI client, tools/traci/connection.py -- the _pack and
// _sendCmd framing at connection.py:141-232.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace CarlaNet.Sumo;

/// <summary>
/// Builds one TraCI message: a command, and the four-byte frame length in front of it.
/// </summary>
/// <remarks>
/// <para>A command's own length prefix sits before the command identifier and counts itself, so the
/// payload has to be measured before the header can be written. That is why the body is built first
/// here and the header prepended in <see cref="Finish"/>, rather than the length being patched in
/// afterwards -- the header is two bytes for a short command and six for a long one, so its size
/// depends on the answer.</para>
///
/// <para>One instance is reused for the life of a connection. <see cref="BeginCommand"/> resets it.</para>
/// </remarks>
internal sealed class TraCIWriter
{
    /// <summary>
    /// The longest header: the escape byte, a four-byte length, and the command identifier. Space
    /// for it is left in front of the body so the finished message is one contiguous buffer.
    /// </summary>
    private const int MaximumHeaderLength = 6;

    /// <summary>The four-byte total length that precedes every message on the socket.</summary>
    private const int FrameLengthPrefix = 4;

    /// <summary>A command whose length does not fit in one byte is written in the escaped form.</summary>
    private const int CompactCommandLimit = 255;

    private byte[] _buffer = new byte[256];
    private int _bodyEnd = FrameLengthPrefix + MaximumHeaderLength;
    private int _commandId;

    /// <summary>
    /// Start a command addressed to one object's variable, which is the shape of every get, every
    /// set and every domain call.
    /// </summary>
    public void BeginCommand(int commandId, int variableId, string objectId)
    {
        StartBody(commandId);
        AppendUnsignedByte((byte)variableId);
        AppendObjectId(objectId);
    }

    /// <summary>
    /// Start a subscription command, whose place in the frame that a variable identifier would take
    /// is instead the interval the subscription is to cover, as two doubles.
    /// </summary>
    public void BeginSubscription(int commandId, double begin, double end, string objectId)
    {
        StartBody(commandId);
        AppendDouble(begin);
        AppendDouble(end);
        AppendObjectId(objectId);
    }

    /// <summary>
    /// Start a command that addresses no object at all -- the handshake, the step, the close.
    /// </summary>
    public void BeginBareCommand(int commandId) => StartBody(commandId);

    /// <summary>A double with no type byte, which is how the step command carries its target time.</summary>
    public void WriteRawDouble(double value) => AppendDouble(value);

    /// <summary>An unsigned byte with no type byte, which is how a subscription lists its variables.</summary>
    public void WriteRawUnsignedByte(byte value) => AppendUnsignedByte(value);

    /// <summary>A double behind its type byte.</summary>
    public void WriteDouble(double value)
    {
        AppendUnsignedByte(TraCIConstants.TYPE_DOUBLE);
        AppendDouble(value);
    }

    /// <summary>A 32-bit integer behind its type byte.</summary>
    public void WriteInt(int value)
    {
        AppendUnsignedByte(TraCIConstants.TYPE_INTEGER);
        AppendInt(value);
    }

    /// <summary>A signed byte behind its type byte.</summary>
    public void WriteByte(sbyte value)
    {
        AppendUnsignedByte(TraCIConstants.TYPE_BYTE);
        AppendUnsignedByte((byte)value);
    }

    /// <summary>A string behind its type byte.</summary>
    public void WriteString(string value)
    {
        AppendUnsignedByte(TraCIConstants.TYPE_STRING);
        AppendLengthPrefixedUtf8(value);
    }

    /// <summary>
    /// The header of a compound, declaring how many further typed values follow it. The members are
    /// written after it with the ordinary writers.
    /// </summary>
    public void WriteCompoundHeader(int memberCount)
    {
        AppendUnsignedByte(TraCIConstants.TYPE_COMPOUND);
        AppendInt(memberCount);
    }

    /// <summary>A list of strings behind its type byte.</summary>
    public void WriteStringList(IReadOnlyList<string> values)
    {
        AppendUnsignedByte(TraCIConstants.TYPE_STRINGLIST);
        AppendInt(values.Count);
        foreach (string value in values)
        {
            AppendLengthPrefixedUtf8(value);
        }
    }

    /// <summary>
    /// Close the message and return the bytes to put on the socket: the total frame length, the
    /// command's own length and identifier, and the body.
    /// </summary>
    public ReadOnlyMemory<byte> Finish()
    {
        int bodyStart = FrameLengthPrefix + MaximumHeaderLength;
        int bodyLength = _bodyEnd - bodyStart;

        // The command's declared length covers the length field itself and the command identifier.
        int commandLength = bodyLength + 2;
        int headerStart;
        if (commandLength <= CompactCommandLimit)
        {
            headerStart = bodyStart - 2;
            _buffer[headerStart] = (byte)commandLength;
            _buffer[headerStart + 1] = (byte)_commandId;
        }
        else
        {
            // A zero in the one-byte field says the real length follows as four bytes, which are
            // themselves part of the command and so counted in it.
            headerStart = bodyStart - MaximumHeaderLength;
            _buffer[headerStart] = 0;
            BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(headerStart + 1), commandLength + 4);
            _buffer[headerStart + 5] = (byte)_commandId;
        }

        int frameStart = headerStart - FrameLengthPrefix;
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(frameStart), _bodyEnd - frameStart);
        return _buffer.AsMemory(frameStart, _bodyEnd - frameStart);
    }

    private void StartBody(int commandId)
    {
        _commandId = commandId;
        _bodyEnd = FrameLengthPrefix + MaximumHeaderLength;
    }

    private void AppendObjectId(string objectId)
    {
        AppendLengthPrefixedUtf8(objectId);
    }

    private void AppendLengthPrefixedUtf8(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Reserve(4 + byteCount);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_bodyEnd), byteCount);
        _bodyEnd += 4;
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_bodyEnd));
        _bodyEnd += byteCount;
    }

    private void AppendUnsignedByte(byte value)
    {
        Reserve(1);
        _buffer[_bodyEnd++] = value;
    }

    private void AppendUnsignedByte(int value) => AppendUnsignedByte((byte)value);

    private void AppendInt(int value)
    {
        Reserve(4);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_bodyEnd), value);
        _bodyEnd += 4;
    }

    private void AppendDouble(double value)
    {
        Reserve(8);
        BinaryPrimitives.WriteDoubleBigEndian(_buffer.AsSpan(_bodyEnd), value);
        _bodyEnd += 8;
    }

    private void Reserve(int bytes)
    {
        if (_bodyEnd + bytes <= _buffer.Length)
        {
            return;
        }

        int capacity = _buffer.Length;
        while (capacity < _bodyEnd + bytes)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }
}
