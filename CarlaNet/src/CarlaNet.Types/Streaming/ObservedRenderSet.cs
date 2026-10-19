using System.Diagnostics.CodeAnalysis;

namespace CarlaNet.Types.Streaming;

/// <summary>How a co-simulation session's body pool held one body on a frame.</summary>
/// <remarks>The values are the server's: <c>EpisodeStateSerializer::RenderSetEntryState</c>.</remarks>
public enum ObservedBodyState : byte
{
    /// <summary>Lent to a vehicle and drawn for it on the frame.</summary>
    Lent = 1,

    /// <summary>Given back and parked out of sight: drawn for nobody on the frame.</summary>
    Parked = 2,
}

/// <summary>One body a world-observer snapshot's render set named.</summary>
/// <param name="ActorId">The body: the CARLA actor.</param>
/// <param name="State">Whether it was lent or parked on the frame.</param>
/// <param name="VehicleId">The vehicle it was drawn for; empty when parked.</param>
/// <param name="VehicleTypeId">That vehicle's declared type; empty when parked.</param>
/// <param name="AdmittedFrame">
/// The first frame the body was drawn for this vehicle, so a track that begins mid-scene can be told
/// from one that entered; zero when parked.
/// </param>
public sealed record ObservedBody(
    uint ActorId,
    ObservedBodyState State,
    string VehicleId,
    string VehicleTypeId,
    ulong AdmittedFrame);

/// <summary>
/// The render set a world-observer snapshot carried: every body a co-simulation session's pool had
/// lent on that frame, with the vehicle each was drawn for, and every body it had parked.
/// </summary>
/// <remarks>
/// <para>A pooled body is an ordinary vehicle actor, and between loans it stands parked out of sight
/// below the ground, so the snapshot's actors alone say neither which vehicles the frame drew nor who
/// any of them is. The session names each body to the server as it lends it and as it gives it back,
/// and the server carries what it named on every snapshot, paired to the frame like the rest of the
/// snapshot, so every client reads the same set whichever process it runs in.</para>
///
/// <para>An actor the set does not name is one no session named, and is reported as it always was.
/// A snapshot that carries no set -- no session has named a body -- reads as <see cref="None"/>.</para>
///
/// <para>Immutable, so a reader on another thread never sees one half-built, and frames whose sets
/// are byte for byte the same share one instance.</para>
/// </remarks>
public sealed class ObservedRenderSet
{
    private readonly Dictionary<uint, ObservedBody> _bodies;
    private readonly byte[] _block;

    /// <param name="bodies">Every body the snapshot named. A body appears once.</param>
    public ObservedRenderSet(IEnumerable<ObservedBody> bodies)
        : this(bodies, [])
    {
    }

    private ObservedRenderSet(IEnumerable<ObservedBody> bodies, byte[] block)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        _bodies = [];
        foreach (ObservedBody body in bodies)
        {
            _bodies[body.ActorId] = body;
        }

        _block = block;
    }

    /// <summary>The set of a snapshot that carried none: every actor reads as it always did.</summary>
    public static ObservedRenderSet None { get; } = new([]);

    /// <summary>How many bodies the snapshot named, lent or parked.</summary>
    public int Count => _bodies.Count;

    /// <summary>Whether the snapshot named no body at all.</summary>
    public bool IsEmpty => _bodies.Count == 0;

    /// <summary>Every body the snapshot named, by actor id.</summary>
    public IReadOnlyDictionary<uint, ObservedBody> Bodies => _bodies;

    /// <summary>How many of the named bodies were lent and drawn.</summary>
    public int LentCount => _bodies.Values.Count(body => body.State == ObservedBodyState.Lent);

    /// <summary>Whether the actor is a body that stood parked out of sight on the frame.</summary>
    public bool IsParked(uint actorId) =>
        _bodies.TryGetValue(actorId, out ObservedBody? body) && body.State == ObservedBodyState.Parked;

    /// <summary>
    /// The body as the set named it where it was lent on the frame, or <see langword="null"/>: the
    /// form <see cref="TryGetLent"/> takes for a caller with no out parameters, such as Python.
    /// </summary>
    public ObservedBody? Lent(uint actorId) => TryGetLent(actorId, out ObservedBody? body) ? body : null;

    /// <summary>The vehicle a body was drawn for on the frame, where it was lent one.</summary>
    public bool TryGetLent(uint actorId, [MaybeNullWhen(false)] out ObservedBody body)
    {
        if (_bodies.TryGetValue(actorId, out ObservedBody? named) && named.State == ObservedBodyState.Lent)
        {
            body = named;
            return true;
        }

        body = null;
        return false;
    }

    /// <summary>
    /// Read a render set block, or answer <paramref name="previous"/> itself where the block is the
    /// same bytes as the one it was read from: the set changes only when a body is lent or given
    /// back, so most frames carry the block the frame before did.
    /// </summary>
    /// <param name="block">
    /// The block's entries: its count, then each entry, as the server lays them out, and nothing after
    /// them (<see cref="Measure"/>).
    /// </param>
    /// <param name="previous">The set read from the frame before, if any.</param>
    /// <exception cref="InvalidDataException">The block ends part-way through an entry.</exception>
    internal static ObservedRenderSet Read(ReadOnlySpan<byte> block, ObservedRenderSet? previous)
    {
        if (previous is not null && block.SequenceEqual(previous._block))
        {
            return previous;
        }

        var reader = new SnapshotBlockReader(block, "render set");
        uint count = reader.UInt32();
        var bodies = new List<ObservedBody>((int)Math.Min(count, 65536u));
        for (uint index = 0; index < count; index++)
        {
            uint actorId = reader.UInt32();
            var state = (ObservedBodyState)reader.Byte();
            ulong admitted = reader.UInt64();
            string vehicleId = reader.Name();
            string vehicleTypeId = reader.Name();
            if (state is ObservedBodyState.Lent or ObservedBodyState.Parked)
            {
                bodies.Add(new ObservedBody(actorId, state, vehicleId, vehicleTypeId, admitted));
            }
        }

        return new ObservedRenderSet(bodies, block.ToArray());
    }

    /// <summary>
    /// How many bytes of the render set block its count and entries take, walked without decoding a
    /// name: what follows them inside the block -- the supervision block, where the snapshot carries
    /// one -- is not the set's.
    /// </summary>
    /// <param name="block">The render set block after its size field.</param>
    /// <exception cref="InvalidDataException">The block ends part-way through an entry.</exception>
    internal static int Measure(ReadOnlySpan<byte> block)
    {
        var reader = new SnapshotBlockReader(block, "render set");
        uint count = reader.UInt32();
        for (uint index = 0; index < count; index++)
        {
            // Actor id, state and admitted frame, then the vehicle and its type.
            reader.Skip(4 + 1 + 8);
            reader.SkipName();
            reader.SkipName();
        }

        return reader.Position;
    }
}
