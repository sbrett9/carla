using System.Text;

namespace CarlaNet.CoSim;

/// <summary>
/// A place in [0, 1) for each vehicle id, drawn from a seed: the same id and seed give the same place in
/// every process on every machine, and another seed another order.
/// </summary>
/// <remarks>
/// <para><b>Not <see cref="string.GetHashCode()"/>.</b> .NET randomises string hashes per process, so a
/// ranking built on it admits a different set every time the same run is repeated and nothing says so.
/// This is FNV-1a over the id's UTF-8 bytes, mixed with the seed through SplitMix64's finaliser, both of
/// them fixed arithmetic.</para>
///
/// <para>A vehicle keeps its place for its whole life, so a ranking by it does not reshuffle from one
/// pass to the next as vehicles move, and it says nothing about where a vehicle is: shedding by it
/// leaves the rendered vehicles an unbiased sample of the eligible ones rather than, say, the ones nearest
/// the camera.</para>
/// </remarks>
public static class SeededOrder
{
    /// <summary>The vehicle's place under the seed, in [0, 1).</summary>
    public static double Of(long seed, string vehicleId)
    {
        ArgumentNullException.ThrowIfNull(vehicleId);
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        foreach (byte octet in Encoding.UTF8.GetBytes(vehicleId))
        {
            hash ^= octet;
            hash = unchecked(hash * prime);
        }

        ulong mixed = Mix(hash ^ Mix(unchecked((ulong)seed)));
        return (mixed >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>SplitMix64's finaliser: every input bit reaches every output bit.</summary>
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
