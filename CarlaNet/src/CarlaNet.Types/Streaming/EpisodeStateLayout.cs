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
