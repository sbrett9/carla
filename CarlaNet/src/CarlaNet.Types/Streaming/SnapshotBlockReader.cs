using System.Buffers.Binary;
using System.Text;

namespace CarlaNet.Types.Streaming;

/// <summary>
/// Little-endian reads over one block of a world-observer snapshot -- the render set's entries or the
/// supervision block -- refusing one that ends part-way through what it says it holds.
/// </summary>
/// <remarks>
/// A name is a 16-bit length and that many bytes of UTF-8, as the server writes every name in both
/// blocks; a list of names is a 16-bit count and that many names.
/// </remarks>
internal ref struct SnapshotBlockReader
{
    private readonly ReadOnlySpan<byte> _block;
    private readonly string _blockName;
    private int _at;

    /// <param name="block">The block's bytes.</param>
    /// <param name="blockName">What the block is, for the refusal: <c>render set</c>, <c>supervision</c>.</param>
    public SnapshotBlockReader(ReadOnlySpan<byte> block, string blockName)
    {
        _block = block;
        _blockName = blockName;
        _at = 0;
    }

    /// <summary>How many bytes have been read.</summary>
    public readonly int Position => _at;

    public byte Byte() => Take(1)[0];

    public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public string Name()
    {
        ushort size = UInt16();
        return size == 0 ? string.Empty : Encoding.UTF8.GetString(Take(size));
    }

    public string[] Names()
    {
        ushort count = UInt16();
        if (count == 0)
        {
            return [];
        }

        var names = new string[count];
        for (int index = 0; index < count; index++)
        {
            names[index] = Name();
        }

        return names;
    }

    /// <summary>Pass over a name without decoding it.</summary>
    public void SkipName() => Take(UInt16());

    /// <summary>Pass over a number of bytes without reading them.</summary>
    public void Skip(int size) => Take(size);

    private ReadOnlySpan<byte> Take(int size)
    {
        if (_at + size > _block.Length)
        {
            throw new InvalidDataException(
                $"The {_blockName} block ends at byte {_block.Length}, part-way through an entry that "
                + $"needs {_at + size}.");
        }

        ReadOnlySpan<byte> taken = _block.Slice(_at, size);
        _at += size;
        return taken;
    }
}
