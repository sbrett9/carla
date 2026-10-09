using System.Collections.Concurrent;

namespace CarlaNet.CoSim;

/// <summary>
/// Grants population authority over one world to at most one holder at a time.
/// </summary>
/// <remarks>
/// <para><b>What this is, and what it is not.</b> A runtime warning an operator can ignore is not a
/// lockout. Three mechanisms together make it structural, and this is the second of them from the
/// client's side.</para>
///
/// <para>The first is the assembly graph: <c>CarlaNet.CoSim</c> does not reference
/// <c>CarlaNet.TrafficManager</c>, so nothing in the playback bridge can name the traffic manager's
/// types, let alone start it. That is checkable at compile time and it is already true; it does not
/// stop a different component in the same process, or a second process, from starting ambient
/// traffic against the same server.</para>
///
/// <para>This class is the second: an exclusive lease, taken as part of starting a session, whose
/// denial <b>fails the session start with the current holder named</b>. It is process-scoped, which
/// covers a run that starts both modes from one harness -- the common case, and the one the plan
/// records as having been made by accident before. <b>It does not cover a second process</b>, and
/// nothing in a client can: that needs the lease to live in the episode, where it is visible to a
/// client that did not create it and outlives the one that did, exactly as staging bounds already
/// do. Until it does, this is ergonomics, and saying so is part of it.</para>
///
/// <para>The third mechanism, every component that creates a vehicle announcing it to the holder,
/// belongs with the server-held lease and is not here either.</para>
///
/// <para><b>A mode that generates no population takes no lease.</b> A storyboard places named
/// entities and invents nobody, so it never acquires and is never refused. Whether it may coexist
/// with a given population mode is a separate question the lease deliberately does not answer: it
/// turns on whether every storyboard entity is mirrored into SUMO, which the lease cannot see.</para>
/// </remarks>
public sealed class WorldDriveAuthority
{
    private static readonly ConcurrentDictionary<string, WorldDriveAuthority> Worlds = new();

    private readonly object _gate = new();
    private PopulationLease? _held;

    private WorldDriveAuthority(string worldKey)
    {
        WorldKey = worldKey;
    }

    /// <summary>Which world this is the authority over.</summary>
    public string WorldKey { get; }

    /// <summary>The lease currently granted, or <see langword="null"/> where the world is free.</summary>
    public PopulationLease? CurrentHolder
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    /// <summary>
    /// The authority over one world, named by whatever identifies it to every component that could
    /// claim it -- the server's address and the loaded map.
    /// </summary>
    public static WorldDriveAuthority ForWorld(string worldKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldKey);
        return Worlds.GetOrAdd(worldKey, key => new WorldDriveAuthority(key));
    }

    /// <summary>Whether a mode generates a population, and therefore claims the lease at all.</summary>
    public static bool ClaimsPopulationAuthority(PopulationMode mode) =>
        mode != PopulationMode.StoryboardExecution;

    /// <summary>
    /// Take population authority over the world, or refuse naming whoever holds it.
    /// </summary>
    /// <param name="mode">Which mode is claiming it.</param>
    /// <param name="holder">
    /// Who is claiming it, in words a refusal can print: a component and enough of a process to find
    /// it again.
    /// </param>
    /// <exception cref="PopulationAuthorityHeldException">Somebody else has it.</exception>
    public PopulationLease Acquire(PopulationMode mode, string holder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        if (!ClaimsPopulationAuthority(mode))
        {
            throw new CoSimSessionRefusedException(
                $"{mode} generates no population, so it has no population authority to take. A mode "
                + "that takes none also commands no sun, which is the reason this is refused rather "
                + "than granted harmlessly.");
        }

        lock (_gate)
        {
            if (_held is { IsReleased: false } existing)
            {
                throw new PopulationAuthorityHeldException(mode, existing);
            }

            _held = new PopulationLease(this, mode, holder);
            return _held;
        }
    }

    /// <summary>
    /// Refuse a component that is about to generate a population while somebody else holds the
    /// world.
    /// </summary>
    /// <remarks>
    /// For a caller that does not want the lease itself -- ambient traffic checking before it spawns
    /// anything, so the refusal happens before the first vehicle rather than after the hundredth.
    /// </remarks>
    public void RequireAvailable(PopulationMode mode)
    {
        lock (_gate)
        {
            if (_held is { IsReleased: false } existing)
            {
                throw new PopulationAuthorityHeldException(mode, existing);
            }
        }
    }

    internal void Release(PopulationLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_held, lease))
            {
                _held = null;
            }
        }
    }
}
