using System.Buffers.Binary;

namespace CarlaNet.Types.Streaming;

/// <summary>
/// Where things are in the world-observer's episode-state header, and how wide its solar block is.
/// Mirrors <c>LibCarla/source/carla/sensor/s11n/EpisodeStateSerializer.h</c>.
/// </summary>
/// <remarks>
/// <para>The header is a packed struct whose size is the offset of the actor array that follows
/// it, so a reader has to know which size it is reading before it can read any actor. The original
/// 36 bytes are followed by the sun: eleven doubles from a server that carries the geometric
/// elevation only, twelve from one that also carries the refraction-corrected elevation the sun's
/// light is rotated by. The server says which in its flags byte, whatever the sun, because the flag
/// describes the layout rather than the reading.</para>
///
/// <para>A render set block can follow the header, before the first actor: the bodies a
/// co-simulation session's pool has lent, each with the vehicle it is drawn for, and those it has
/// parked out of sight (<see cref="ObservedRenderSet"/>). The server carries it only once a session
/// has named a body, and says so with <see cref="RenderSetCarried"/>, so a snapshot of a world no
/// session has named a body in is laid out exactly as before.</para>
///
/// <para>A supervision block can follow the render set's entries, inside the render set block: the plan
/// a co-simulation session has bound, and what the author asserts of the vehicle each lent body draws
/// (<see cref="ObservedSupervision"/>). The server carries it only while a plan
/// is held, says so with <see cref="SupervisionCarried"/>, and always writes the render set block with
/// it -- with no entries where no body is lent yet -- whose size counts it. So a reader that knows the
/// render set and not the supervision finds the actors and the render set as before and skips the
/// supervision unread, and every reader of a world no session supervises reads it as before.</para>
///
/// <para>Both readers of the header in this tree -- the client's own world-observer parse and the
/// episode-state sensor decoder -- read it through here, so the two cannot come to disagree about
/// where the actors start.</para>
/// </remarks>
public static class EpisodeStateLayout
{
    /// <summary>Offset of the simulation-state flags byte.</summary>
    public const int FlagsOffset = 32;

    /// <summary>Offset of the first solar double, after the flags and their padding.</summary>
    public const int SolarOffset = 36;

    /// <summary>The flag saying the solar block holds a sun that was measured this tick.</summary>
    public const byte SolarStateValid = 0x4;

    /// <summary>The flag saying the solar block is twelve doubles wide.</summary>
    public const byte SolarCorrectedElevationCarried = 0x8;

    /// <summary>Solar doubles in a header that carries the geometric elevation only.</summary>
    public const int GeometricSolarValues = 11;

    /// <summary>Solar doubles in a header that also carries the refraction-corrected elevation.</summary>
    public const int CorrectedSolarValues = 12;

    /// <summary>
    /// The flag saying a render set block follows the header, before the first actor. Mirrors
    /// <c>EpisodeStateSerializer::RenderSetCarried</c>.
    /// </summary>
    public const byte RenderSetCarried = 0x10;

    /// <summary>
    /// The flag saying a supervision block follows the render set's entries, inside the render set
    /// block. Mirrors <c>EpisodeStateSerializer::SupervisionCarried</c>.
    /// </summary>
    public const byte SupervisionCarried = 0x20;

    /// <summary>
    /// Where the first actor starts: straight after the header, or after the render set block where
    /// the snapshot carries one, the supervision block inside it included. Zero where the payload is
    /// too short to say.
    /// </summary>
    /// <remarks>
    /// The block states its own size, so the actors are found whether or not its entries can be
    /// read. A size that runs past the payload answers the payload's end: no actor is read from a
    /// truncated snapshot rather than a block's bytes read as actors.
    /// </remarks>
    public static int ActorsOffset(ReadOnlySpan<byte> payload)
    {
        int header = HeaderSize(payload);
        if (header == 0 || payload.Length < header || (payload[FlagsOffset] & RenderSetCarried) == 0)
        {
            return header;
        }

        if (payload.Length < header + 4)
        {
            return payload.Length;
        }

        long offset = header + 4L + BinaryPrimitives.ReadUInt32LittleEndian(payload[header..]);
        return offset > payload.Length ? payload.Length : (int)offset;
    }

    /// <summary>
    /// The render set the snapshot carried, or <see cref="ObservedRenderSet.None"/> where it carried
    /// none.
    /// </summary>
    /// <param name="payload">The snapshot, from its episode-state header on.</param>
    /// <param name="previous">
    /// The set read from the frame before, which is answered again, instance and all, where this
    /// snapshot's block is the same bytes: a set changes only when a body is lent or given back.
    /// </param>
    /// <exception cref="InvalidDataException">The block is shorter than it says, or ends part-way
    /// through an entry.</exception>
    public static ObservedRenderSet ReadRenderSet(ReadOnlySpan<byte> payload, ObservedRenderSet? previous = null)
    {
        if (!Carries(payload, RenderSetCarried))
        {
            return ObservedRenderSet.None;
        }

        // The set's own entries, and not the supervision block that can follow them inside the block.
        ReadOnlySpan<byte> block = RenderSetBlock(payload);
        return ObservedRenderSet.Read(block[..ObservedRenderSet.Measure(block)], previous);
    }

    /// <summary>
    /// The supervision the snapshot carried, or <see cref="ObservedSupervision.None"/> where it carried
    /// none.
    /// </summary>
    /// <param name="payload">The snapshot, from its episode-state header on.</param>
    /// <param name="previous">
    /// The supervision read from the frame before, which is answered again, instance and all, where this
    /// snapshot's block is the same bytes.
    /// </param>
    /// <exception cref="InvalidDataException">The snapshot says it carries supervision outside a render
    /// set block; or the block is shorter than it says, ends part-way through what it holds, or names a
    /// state this reader does not know.</exception>
    public static ObservedSupervision ReadSupervision(ReadOnlySpan<byte> payload, ObservedSupervision? previous = null)
    {
        if (!Carries(payload, SupervisionCarried))
        {
            return ObservedSupervision.None;
        }

        if (!Carries(payload, RenderSetCarried))
        {
            throw new InvalidDataException(
                "The snapshot says it carries supervision and no render set block, which the supervision "
                + "block is written inside.");
        }

        ReadOnlySpan<byte> block = RenderSetBlock(payload);
        ReadOnlySpan<byte> after = block[ObservedRenderSet.Measure(block)..];
        if (after.Length < 4)
        {
            throw new InvalidDataException(
                $"The snapshot says it carries supervision and its render set block ends {after.Length} "
                + "byte(s) after the render set's entries, before the supervision block's size.");
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(after);
        if (4L + size > after.Length)
        {
            throw new InvalidDataException(
                $"The snapshot's supervision block says it is {size} bytes, and the render set block it is "
                + $"written inside ends {after.Length - 4} bytes after its size.");
        }

        return ObservedSupervision.Read(after.Slice(4, (int)size), previous);
    }

    /// <summary>The render set block after its size field, where the snapshot says it carries one.</summary>
    /// <exception cref="InvalidDataException">The block is shorter than it says.</exception>
    private static ReadOnlySpan<byte> RenderSetBlock(ReadOnlySpan<byte> payload)
    {
        int header = HeaderSize(payload);
        if (payload.Length < header + 4)
        {
            throw new InvalidDataException(
                $"The snapshot says it carries a render set and ends {payload.Length - header} byte(s) "
                + "after its header, before the block's size.");
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(payload[header..]);
        if (header + 4L + size > payload.Length)
        {
            throw new InvalidDataException(
                $"The snapshot's render set block says it is {size} bytes, and the snapshot ends "
                + $"{payload.Length - header - 4} bytes after its size.");
        }

        return payload.Slice(header + 4, (int)size);
    }

    /// <summary>Whether the snapshot's header is whole and its flags byte carries the flag.</summary>
    private static bool Carries(ReadOnlySpan<byte> payload, byte flag)
    {
        int header = HeaderSize(payload);
        return header != 0 && payload.Length >= header && (payload[FlagsOffset] & flag) != 0;
    }

    /// <summary>
    /// The header's size, and so the offset of the first actor, or zero where the payload is too
    /// short to say.
    /// </summary>
    public static int HeaderSize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= FlagsOffset)
        {
            return 0;
        }

        return SolarOffset + (8 * SolarValueCount(payload[FlagsOffset]));
    }

    /// <summary>
    /// The solar block in the order the server packs it, or empty where the server says it
    /// measured no sun or the payload is shorter than its own header.
    /// </summary>
    /// <remarks>
    /// Empty rather than the header's defaults, because those defaults are a well-formed reading --
    /// midnight of year 0 at latitude 0, longitude 0 -- and a recorded artifact must never assert a
    /// sun that was not there.
    /// </remarks>
    public static double[] ReadSolar(ReadOnlySpan<byte> payload)
    {
        int size = HeaderSize(payload);
        if (size == 0 || payload.Length < size || (payload[FlagsOffset] & SolarStateValid) == 0)
        {
            return [];
        }

        var solar = new double[SolarValueCount(payload[FlagsOffset])];
        for (int index = 0; index < solar.Length; index++)
        {
            solar[index] = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(payload[(SolarOffset + (index * 8))..]));
        }

        return solar;
    }

    private static int SolarValueCount(byte flags) =>
        (flags & SolarCorrectedElevationCarried) != 0 ? CorrectedSolarValues : GeometricSolarValues;
}
