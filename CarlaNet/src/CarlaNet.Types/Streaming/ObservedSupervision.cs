using CarlaNet.Types.Supervision;

namespace CarlaNet.Types.Streaming;

/// <summary>
/// The supervision a world-observer snapshot carried: the plan it is bound from, what the scenario's
/// author asserts of the vehicle each lent body drew on that frame, and the absences in force for the
/// world as a whole.
/// </summary>
/// <remarks>
/// <para><b>Held on the server, so every reader reads the same truth.</b> A co-simulation session puts
/// supervision in force on the server as it changes (<c>update_supervision</c>), and the server carries
/// what it holds on every snapshot beside the render set, paired to the frame like the rest of the
/// snapshot. No client keeps it in its own process, so two clients of one world -- a recorder beside the
/// session and one in another process, the live CoT feed -- can never hold different truth for the same
/// frame (doc 06 §8.2, the ruling of 2026-10-05).</para>
///
/// <para><b>Rows only where the author asserts something.</b> A body has a row only where the vehicle
/// it draws is annotated or nominal; a body the same frame's render set names lent with no row draws a
/// vehicle that is unlabelled, which costs the snapshot nothing however many there are. An actor the
/// render set does not name lent is no subject of the plan at all. <see cref="ForVehicles"/> joins the
/// two and answers every lent vehicle's state explicitly, unlabelled included, as a truth record writes
/// it.</para>
///
/// <para>A snapshot that carries no supervision -- no session has bound a plan, or the one that did has
/// withdrawn it -- reads as <see cref="None"/>, which is not the same as every vehicle unlabelled: it
/// says no plan was in force.</para>
///
/// <para>Immutable, so a reader on another thread never sees one half-built, and frames whose blocks are
/// byte for byte the same share one instance.</para>
/// </remarks>
public sealed class ObservedSupervision
{
    private readonly Dictionary<uint, SupervisionInForce> _byActor;
    private readonly AbsenceInForce[] _absences;
    private readonly byte[] _block;

    /// <param name="plan">The plan the supervision is bound from.</param>
    /// <param name="byActor">
    /// Every body whose vehicle is annotated or nominal, with its supervision. An unlabelled entry is
    /// left out, as the server leaves it out.
    /// </param>
    /// <param name="absences">The absences in force.</param>
    public ObservedSupervision(SupervisionPlanIdentity plan,
                               IEnumerable<KeyValuePair<uint, SupervisionInForce>> byActor,
                               IEnumerable<AbsenceInForce> absences)
        : this(plan ?? throw new ArgumentNullException(nameof(plan)), byActor, absences, [])
    {
    }

    private ObservedSupervision(SupervisionPlanIdentity? plan,
                                IEnumerable<KeyValuePair<uint, SupervisionInForce>> byActor,
                                IEnumerable<AbsenceInForce> absences, byte[] block)
    {
        ArgumentNullException.ThrowIfNull(byActor);
        ArgumentNullException.ThrowIfNull(absences);
        Plan = plan;
        _byActor = [];
        foreach ((uint actor, SupervisionInForce supervision) in byActor)
        {
            if (supervision.State != SupervisionState.Unlabelled)
            {
                _byActor[actor] = supervision;
            }
        }

        _absences = [.. absences];
        _block = block;
    }

    /// <summary>The supervision of a snapshot that carried none: no plan was in force.</summary>
    public static ObservedSupervision None { get; } = new(null, [], [], []);

    /// <summary>
    /// The supervision of a snapshot that said it carried a block this reader could not read: a plan
    /// was in force, and what it asserted on the frame is unknown. Never read as <see cref="None"/>,
    /// which would say no plan was in force.
    /// </summary>
    public static ObservedSupervision Unreadable { get; } = new(null, [], [], []);

    /// <summary>Whether the snapshot said it carried supervision and its block could not be read.</summary>
    public bool IsUnreadable => ReferenceEquals(this, Unreadable);

    /// <summary>The plan the supervision is bound from, or <see langword="null"/> where none was in force.</summary>
    public SupervisionPlanIdentity? Plan { get; }

    /// <summary>Whether the snapshot carried supervision: a plan was in force on its frame.</summary>
    public bool IsCarried => Plan is not null;

    /// <summary>Every body whose vehicle was annotated or nominal on the frame, by actor id.</summary>
    public IReadOnlyDictionary<uint, SupervisionInForce> ByActor => _byActor;

    /// <summary>The absences in force on the frame, in the order they opened.</summary>
    public IReadOnlyList<AbsenceInForce> Absences => _absences;

    /// <summary>
    /// What the author asserted on the frame of the vehicle a body drew: its row, or
    /// <see cref="SupervisionInForce.Unlabelled"/> where it has none; <see langword="null"/> where the
    /// snapshot carried no supervision.
    /// </summary>
    /// <remarks>
    /// Read it for a body the same frame's render set names lent. Of any other actor the plan asserts
    /// nothing, because it draws no SUMO vehicle; <see cref="ForVehicles"/> makes that join.
    /// </remarks>
    public SupervisionInForce? Of(uint actorId)
    {
        if (!IsCarried)
        {
            return null;
        }

        return _byActor.TryGetValue(actorId, out SupervisionInForce? row) ? row : SupervisionInForce.Unlabelled;
    }

    /// <summary>
    /// Every vehicle the same frame's render set names lent, by SUMO vehicle id, with what the author
    /// asserted of it: annotated and nominal from their rows, unlabelled for every other. Empty where the
    /// snapshot carried no supervision.
    /// </summary>
    /// <param name="renderSet">The render set the same snapshot carried.</param>
    public IReadOnlyDictionary<string, SupervisionInForce> ForVehicles(ObservedRenderSet renderSet)
    {
        ArgumentNullException.ThrowIfNull(renderSet);
        var vehicles = new Dictionary<string, SupervisionInForce>(StringComparer.Ordinal);
        if (!IsCarried)
        {
            return vehicles;
        }

        foreach (ObservedBody body in renderSet.Bodies.Values)
        {
            if (body.State == ObservedBodyState.Lent)
            {
                vehicles[body.VehicleId] = _byActor.TryGetValue(body.ActorId, out SupervisionInForce? row)
                    ? row
                    : SupervisionInForce.Unlabelled;
            }
        }

        return vehicles;
    }

    /// <summary>
    /// Read a supervision block, or answer <paramref name="previous"/> itself where the block is the same
    /// bytes as the one it was read from: supervision changes only when an interval opens or closes, or a
    /// body changes hands, so most frames carry the block the frame before did.
    /// </summary>
    /// <param name="block">The block after its size field, as the server lays it out.</param>
    /// <param name="previous">The supervision read from the frame before, if any.</param>
    /// <exception cref="InvalidDataException">
    /// The block ends part-way through what it says it holds, or names a state this reader does not know.
    /// </exception>
    internal static ObservedSupervision Read(ReadOnlySpan<byte> block, ObservedSupervision? previous)
    {
        if (previous is not null && previous.IsCarried && block.SequenceEqual(previous._block))
        {
            return previous;
        }

        var reader = new SnapshotBlockReader(block, "supervision");
        string planId = reader.Name();
        int vocabularyVersion = (int)reader.UInt32();
        string vocabularyDigest = reader.Name();

        uint rowCount = reader.UInt32();
        var rows = new List<KeyValuePair<uint, SupervisionInForce>>((int)Math.Min(rowCount, 65536u));
        for (uint row = 0; row < rowCount; row++)
        {
            uint actorId = reader.UInt32();
            byte state = reader.Byte();
            SupervisionState asserted = state switch
            {
                AnnotatedState => SupervisionState.Annotated,
                NominalState => SupervisionState.Nominal,
                _ => throw new InvalidDataException(
                    $"The supervision block gives actor {actorId} the state {state}, which is neither "
                    + $"annotated ({AnnotatedState}) nor nominal ({NominalState})."),
            };

            ushort annotationCount = reader.UInt16();
            var annotations = new AnnotationInForce[annotationCount];
            for (int index = 0; index < annotationCount; index++)
            {
                string instanceId = reader.Name();
                string phase = reader.Name();
                string role = reader.Name();
                string[] labels = reader.Names();
                annotations[index] = new AnnotationInForce(instanceId, labels, phase, role);
            }

            rows.Add(KeyValuePair.Create(actorId, new SupervisionInForce(asserted, annotations)));
        }

        uint absenceCount = reader.UInt32();
        var absences = new List<AbsenceInForce>((int)Math.Min(absenceCount, 65536u));
        for (uint absence = 0; absence < absenceCount; absence++)
        {
            string instanceId = reader.Name();
            string phase = reader.Name();
            string[] labels = reader.Names();
            string[] areas = reader.Names();
            absences.Add(new AbsenceInForce(instanceId, labels, areas, phase));
        }

        return new ObservedSupervision(new SupervisionPlanIdentity(planId, vocabularyVersion, vocabularyDigest),
                                       rows, absences, block.ToArray());
    }

    /// <summary>The server's value for an annotated row: <c>EpisodeStateSerializer::SupervisionEntryState::Annotated</c>.</summary>
    internal const byte AnnotatedState = 1;

    /// <summary>The server's value for a nominal row: <c>EpisodeStateSerializer::SupervisionEntryState::Nominal</c>.</summary>
    internal const byte NominalState = 2;
}
