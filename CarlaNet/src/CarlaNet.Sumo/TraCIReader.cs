// Ported from Eclipse SUMO's reference TraCI client, tools/traci/storage.py, and the value
// dispatch in tools/traci/domain.py (_parse).
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace CarlaNet.Sumo;

/// <summary>
/// Reads one TraCI frame: big-endian scalars, length-prefixed strings, and typed values.
/// </summary>
/// <remarks>
/// <para>TraCI is big-endian throughout, which is the opposite of the machine this runs on, so
/// every multi-byte read goes through <see cref="BinaryPrimitives"/> rather than reinterpreting
/// memory.</para>
///
/// <para>One instance is reused for the life of a connection: <see cref="Reset"/> points it at the
/// next frame's bytes. A frame is decoded strictly front to back and every read advances the
/// position, so a value read at the wrong width does not fail where it happens -- it fails,
/// or worse does not fail, somewhere later in the frame. That is why every unexpected type byte
/// raises <see cref="FatalTraCIError"/> instead of being skipped.</para>
/// </remarks>
internal sealed class TraCIReader
{
    private byte[] _content = [];
    private int _length;
    private int _position;

    /// <summary>Whether any bytes of the frame are still unread.</summary>
    public bool HasMore => _position < _length;

    /// <summary>How many bytes of the frame remain unread.</summary>
    public int Remaining => _length - _position;

    /// <summary>Point this reader at a new frame, from the start.</summary>
    public void Reset(byte[] content, int length)
    {
        _content = content;
        _length = length;
        _position = 0;
    }

    /// <summary>One unsigned byte.</summary>
    public byte ReadUnsignedByte()
    {
        Require(1);
        return _content[_position++];
    }

    /// <summary>One signed byte.</summary>
    public sbyte ReadByte() => (sbyte)ReadUnsignedByte();

    /// <summary>A 32-bit signed integer.</summary>
    public int ReadInt()
    {
        Require(4);
        int value = BinaryPrimitives.ReadInt32BigEndian(_content.AsSpan(_position));
        _position += 4;
        return value;
    }

    /// <summary>A double.</summary>
    public double ReadDouble()
    {
        Require(8);
        double value = BinaryPrimitives.ReadDoubleBigEndian(_content.AsSpan(_position));
        _position += 8;
        return value;
    }

    /// <summary>
    /// A length that is normally one byte but escapes to four when it does not fit, which is how
    /// TraCI prefixes a response block and a polygon's point count.
    /// </summary>
    public int ReadLength()
    {
        byte compact = ReadUnsignedByte();
        return compact > 0 ? compact : ReadInt();
    }

    /// <summary>A string: a four-byte length in bytes, then that many bytes of UTF-8.</summary>
    public string ReadString()
    {
        int length = ReadInt();
        if (length < 0)
        {
            throw new FatalTraCIError($"A TraCI string declared a negative length of {length}.");
        }

        Require(length);
        string value = Encoding.UTF8.GetString(_content, _position, length);
        _position += length;
        return value;
    }

    /// <summary>A list of strings: a four-byte count, then that many strings.</summary>
    public string[] ReadStringList()
    {
        int count = ReadCount("string list");
        string[] values = new string[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = ReadString();
        }

        return values;
    }

    /// <summary>
    /// A value preceded by its type byte, which is how every subscribed variable and every answer
    /// to a get command arrives.
    /// </summary>
    /// <remarks>
    /// The variable identifier is taken only so that a failure can name it. Variables SUMO answers
    /// with a compound whose members are <i>not</i> individually typed -- the best-lanes list, the
    /// leader, the next traffic lights -- need a decoder of their own and are rejected before a
    /// subscription is ever made; see <see cref="TraCIVariables.EnsureDecodable"/>.
    /// </remarks>
    public TraCIValue ReadTypedValue(int variableId)
    {
        byte type = ReadUnsignedByte();
        switch (type)
        {
            case TraCIConstants.TYPE_INTEGER:
                return TraCIValue.Number(TraCIValueKind.Integer, ReadInt());
            case TraCIConstants.TYPE_BYTE:
                return TraCIValue.Number(TraCIValueKind.Byte, ReadByte());
            case TraCIConstants.TYPE_UBYTE:
                return TraCIValue.Number(TraCIValueKind.UnsignedByte, ReadUnsignedByte());
            case TraCIConstants.TYPE_DOUBLE:
                return TraCIValue.Number(TraCIValueKind.Double, ReadDouble());
            case TraCIConstants.TYPE_STRING:
                return TraCIValue.Reference(TraCIValueKind.String, ReadString());
            case TraCIConstants.TYPE_STRINGLIST:
                return TraCIValue.Reference(TraCIValueKind.StringList, ReadStringList());
            case TraCIConstants.TYPE_DOUBLELIST:
                return TraCIValue.Reference(TraCIValueKind.DoubleList, ReadDoubleList());
            case TraCIConstants.POSITION_2D:
                return TraCIValue.Position(TraCIValueKind.Position2D, ReadDouble(), ReadDouble(), 0);
            case TraCIConstants.POSITION_3D:
                return TraCIValue.Position(TraCIValueKind.Position3D, ReadDouble(), ReadDouble(), ReadDouble());
            case TraCIConstants.POSITION_LON_LAT:
                return TraCIValue.Position(TraCIValueKind.GeoPosition2D, ReadDouble(), ReadDouble(), 0);
            case TraCIConstants.POSITION_LON_LAT_ALT:
                return TraCIValue.Position(TraCIValueKind.GeoPosition3D, ReadDouble(), ReadDouble(), ReadDouble());
            case TraCIConstants.TYPE_COLOR:
                return TraCIValue.Reference(TraCIValueKind.Color,
                                            (ReadUnsignedByte(), ReadUnsignedByte(),
                                             ReadUnsignedByte(), ReadUnsignedByte()));
            case TraCIConstants.TYPE_POLYGON:
                return TraCIValue.Reference(TraCIValueKind.Polygon, ReadPolygon());
            case TraCIConstants.TYPE_COMPOUND:
                return TraCIValue.Reference(TraCIValueKind.Compound, ReadCompoundMembers(variableId));
            default:
                throw new FatalTraCIError(
                    $"TraCI variable 0x{variableId:x2} arrived with value type 0x{type:x2}, which this "
                    + "client cannot decode. The rest of the frame cannot be read past an unknown "
                    + "value, because its width is part of what is unknown.");
        }
    }

    private double[] ReadDoubleList()
    {
        int count = ReadCount("double list");
        double[] values = new double[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = ReadDouble();
        }

        return values;
    }

    private (double X, double Y)[] ReadPolygon()
    {
        int count = ReadLength();
        (double X, double Y)[] points = new (double, double)[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = (ReadDouble(), ReadDouble());
        }

        return points;
    }

    private TraCIValue[] ReadCompoundMembers(int variableId)
    {
        int count = ReadCount("compound");
        TraCIValue[] members = new TraCIValue[count];
        for (int index = 0; index < count; index++)
        {
            members[index] = ReadTypedValue(variableId);
        }

        return members;
    }

    private int ReadCount(string what)
    {
        int count = ReadInt();
        if (count < 0 || count > Remaining)
        {
            throw new FatalTraCIError(
                $"A TraCI {what} declared {count} elements with {Remaining} bytes of frame left, so "
                + "the frame is being read at the wrong offset.");
        }

        return count;
    }

    private void Require(int bytes)
    {
        if (_position + bytes > _length)
        {
            throw new FatalTraCIError(
                $"A TraCI frame of {_length} bytes ran out while reading {bytes} more at offset "
                + $"{_position}. The frame was shorter than what it declared it held.");
        }
    }
}
