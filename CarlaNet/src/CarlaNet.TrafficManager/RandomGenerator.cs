// Source: carla/trafficmanager/RandomGenerator.h
//
// Tiny per-vehicle PRNG wrapper. Upstream uses <c>std::mt19937</c> +
// <c>std::uniform_real_distribution&lt;double&gt;(0, 100)</c> — every call to
// <see cref="Next"/> returns a percentage in [0, 100) used by the
// "percentage" tunables in <see cref="Parameters"/> (e.g.
// SetPercentageRunningLight, SetRandomDeviceSeed, etc.).
//
// We back this with <see cref="System.Random"/> (which on .NET 6+ uses
// xoshiro256**, a high-quality non-cryptographic PRNG). Determinism is
// per-instance: two RandomGenerators constructed with the same seed produce
// the same sequence. That matches upstream's contract — the LocalizationStage
// builds one per registered actor keyed on the global seed XOR actor id.
//
// NOTE: System.Random is NOT thread-safe but the TM contract is one RNG
// per vehicle, accessed from the worker thread only, so no synchronization
// is required.
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// Per-vehicle seedable PRNG. Mirrors upstream's
/// <c>traffic_manager::RandomGenerator</c>.
/// </summary>
internal sealed class RandomGenerator
{
    private readonly Random _random;

    /// <summary>
    /// Construct with the supplied 64-bit seed. .NET's <see cref="Random"/>
    /// takes a 32-bit seed; we fold the high bits in via XOR so different
    /// upper halves still yield different sequences.
    /// </summary>
    public RandomGenerator(ulong seed)
    {
        // Fold 64 → 32 bits. Mirrors what most TM seeding paths feed (an
        // ActorId XOR'd with the global TM seed — both 32-bit quantities —
        // so the upper bits are usually zero anyway).
        int seed32 = unchecked((int)((uint)seed ^ (uint)(seed >> 32)));
        _random = new Random(seed32);
    }

    /// <summary>
    /// Returns a uniformly-distributed double in [0, 100). Matches the
    /// upstream <c>uniform_real_distribution&lt;double&gt;(0.0, 100.0)</c>
    /// range — every percentage knob in <see cref="Parameters"/> compares
    /// against this value.
    /// </summary>
    public double Next() => _random.NextDouble() * 100.0;
}
