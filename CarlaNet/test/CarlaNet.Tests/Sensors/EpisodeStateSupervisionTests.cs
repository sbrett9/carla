// The supervision block of the world-observer snapshot, inside the render set block, and what a reader
// makes of it. Mirrors: LibCarla/source/carla/sensor/s11n/EpisodeStateSerializer.h (SupervisionCarried,
// SupervisionEntryState) and Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/WorldObserver.cpp.
//
// Supervision -- what the scenario's author asserts of each vehicle on each frame -- is held on the
// server, as the owner ruled on 2026-10-05, so no two clients of one world ever hold different truth.
// The session puts it in force as it changes, and the server writes what it holds on every snapshot,
// after the render set's entries and inside the render set block, so a reader that knows only the render
// set skips it unread. Every row is a vehicle's: the block holds nothing for the world apart from the
// plan (06 §3.5, the owner's ruling of 2026-10-05).
using System.Buffers.Binary;
using System.Text;
using CarlaNet.Sensors;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;
using static CarlaNet.Tests.Sensors.EpisodeStateRenderSetTests;

namespace CarlaNet.Tests.Sensors;

public class EpisodeStateSupervisionTests
{
    private const int WideHeaderSize = 132;
    private const int ActorSize = 119;
    private const byte Annotated = 1;
    private const byte Nominal = 2;

    /// One pattern instance in force for a body, as the server writes it.
    internal sealed record Annotation(string Instance, string Phase, string Role, params string[] Labels);

    /// One body's supervision row, as the server writes it.
    internal sealed record Row(uint Actor, byte State, params Annotation[] Annotations);

    internal const string PlanId = "Shahid_Bahonar_Port_PatternOfLife";
    internal const uint VocabularyVersion = 3;
    internal const string VocabularyDigest = "e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad";

    /// The supervision block as the server writes it: its size, the plan, the vocabulary version and
    /// digest, then the rows, little-endian and unpadded.
    internal static byte[] SupervisionBlock(IReadOnlyList<Row> rows, string plan = PlanId)
    {
        var body = new List<byte>();
        body.AddRange(Name(plan));
        body.AddRange(LittleEndian(VocabularyVersion));
        body.AddRange(Name(VocabularyDigest));
        body.AddRange(LittleEndian((uint)rows.Count));
        foreach (Row row in rows)
        {
            body.AddRange(LittleEndian(row.Actor));
            body.Add(row.State);
            body.AddRange(LittleEndian16((ushort)row.Annotations.Length));
            foreach (Annotation annotation in row.Annotations)
            {
                body.AddRange(Name(annotation.Instance));
                body.AddRange(Name(annotation.Phase));
                body.AddRange(Name(annotation.Role));
                body.AddRange(Names(annotation.Labels));
            }
        }

        return [.. LittleEndian((uint)body.Count), .. body];
    }

    /// The render set block as the server writes it once a plan is held: its size, its count, every
    /// entry, then the supervision block, the size counting all of it.
    internal static byte[] RenderSetWithSupervision(IReadOnlyList<Entry> entries, byte[] supervision)
    {
        byte[] renderSet = Block([.. entries]);
        // The render set's own block, its size field dropped, then the supervision.
        byte[] contents = [.. renderSet.AsSpan(4), .. supervision];
        return [.. LittleEndian((uint)contents.Length), .. contents];
    }

    /// A snapshot as a server that carries the corrected elevation writes it, its render set block
    /// carrying supervision: the header with both flags, the block, then one actor record per id.
    internal static byte[] SupervisedSnapshot(byte[] block, params uint[] actors)
    {
        byte[] bytes = Snapshot(block, actors);
        bytes[32] |= (byte)SimulationState.SupervisionCarried;
        return bytes;
    }

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] LittleEndian16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Name(string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        return [.. LittleEndian16((ushort)utf8.Length), .. utf8];
    }

    private static byte[] Names(IReadOnlyList<string> names) =>
        [.. LittleEndian16((ushort)names.Count), .. names.SelectMany(Name)];

    // Bahonar's escort lead, annotated; a guard on its tower posting, nominal; an ambient flow vehicle,
    // unlabelled; and a body parked out of sight.
    private static readonly Entry Escort = new(21, ObservedBodyState.Lent, 4180, "escort_0", "military_truck");
    private static readonly Entry Guard = new(22, ObservedBodyState.Lent, 4100, "guard_d4_h15_t3", "guard");
    private static readonly Entry Ambient = new(23, ObservedBodyState.Lent, 4170, "corridor_d0_p0_h6.12", "car");
    private static readonly Entry Parked = new(24, ObservedBodyState.Parked, 0, "", "");

    private static readonly Row EscortRow = new(21, Annotated,
        new Annotation("Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3", "transit", "bahonar:lead",
                       "bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"));

    private static readonly Row GuardRow = new(22, Nominal,
        new Annotation("Shahid_Bahonar_Port_PatternOfLife/pi_tower_posting_d4_h15_t3", "dwell", "bahonar:guard",
                       "bahonar:tower_posting"));

    private static byte[] Drive(params Row[] rows) => SupervisedSnapshot(
        RenderSetWithSupervision([Escort, Guard, Ambient, Parked], SupervisionBlock(rows)),
        21, 22, 23, 24);

    [Fact]
    public void A_Snapshot_Of_A_World_No_Session_Supervises_Carries_None()
    {
        byte[] payload = Snapshot(Block(Escort, Parked), 21, 24);

        ObservedSupervision supervision = EpisodeStateLayout.ReadSupervision(payload);

        Assert.Same(ObservedSupervision.None, supervision);
        Assert.False(supervision.IsCarried);
        Assert.Null(supervision.Of(21));
        Assert.Empty(supervision.ForVehicles(EpisodeStateLayout.ReadRenderSet(payload)));
        Assert.Same(ObservedSupervision.None, EpisodeStateSensorData.Deserialize(payload).Header.Supervision);
    }

    [Fact]
    public void The_Supervision_Is_Read_After_The_Render_Set_And_The_Actors_After_Both()
    {
        byte[] payload = Drive(EscortRow, GuardRow);
        int block = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(WideHeaderSize));

        Assert.Equal(WideHeaderSize + 4 + block, EpisodeStateLayout.ActorsOffset(payload));
        EpisodeStateSensorData data = EpisodeStateSensorData.Deserialize(payload);
        Assert.Equal([21u, 22u, 23u, 24u], data.Actors.Select(actor => actor.Id));
        Assert.All(data.Actors, actor => Assert.Equal(ActorState.Active, actor.State));
        Assert.True(data.Header.SimulationState.HasFlag(SimulationState.SupervisionCarried));
        Assert.Equal(4, data.Header.RenderSet.Count);

        ObservedSupervision supervision = data.Header.Supervision;
        Assert.Equal(new SupervisionPlanIdentity(PlanId, 3, VocabularyDigest), supervision.Plan);
        Assert.Equal(2, supervision.ByActor.Count);

        SupervisionInForce escort = supervision.Of(21)!;
        Assert.Equal(SupervisionState.Annotated, escort.State);
        AnnotationInForce lead = Assert.Single(escort.Annotations);
        Assert.Equal("Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3", lead.InstanceId);
        Assert.Equal("transit", lead.Phase);
        Assert.Equal("bahonar:lead", lead.Role);
        Assert.Equal(["bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"], lead.Labels);

        SupervisionInForce guard = supervision.Of(22)!;
        Assert.Equal(SupervisionState.Nominal, guard.State);
        Assert.Equal(["bahonar:tower_posting"], Assert.Single(guard.Annotations).Labels);
    }

    [Fact]
    public void Every_Lent_Vehicle_Is_Read_With_Its_State_Unlabelled_Included_And_No_Parked_Body()
    {
        byte[] payload = Drive(EscortRow, GuardRow);
        ObservedSupervision supervision = EpisodeStateLayout.ReadSupervision(payload);

        IReadOnlyDictionary<string, SupervisionInForce> vehicles =
            supervision.ForVehicles(EpisodeStateLayout.ReadRenderSet(payload));

        // The state is always written: a truth record names every vehicle's, and unlabelled is one.
        Assert.Equal(["corridor_d0_p0_h6.12", "escort_0", "guard_d4_h15_t3"], vehicles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(SupervisionState.Annotated, vehicles["escort_0"].State);
        Assert.Equal(SupervisionState.Nominal, vehicles["guard_d4_h15_t3"].State);
        Assert.Same(SupervisionInForce.Unlabelled, vehicles["corridor_d0_p0_h6.12"]);
        Assert.Same(SupervisionInForce.Unlabelled, supervision.Of(23));
    }

    [Fact]
    public void A_Reader_That_Knows_Only_The_Render_Set_Finds_The_Actors_And_The_Set_And_Skips_The_Supervision()
    {
        // A reader built with the render set and before supervision: it finds the actors by the render set
        // block's size and reads the set's count and entries, as EpisodeStateLayout did before.
        byte[] payload = Drive(EscortRow, GuardRow);

        (int actorsOffset, uint[] named) = ReadAsTheRenderSetAloneWasRead(payload);

        Assert.Equal(EpisodeStateLayout.ActorsOffset(payload), actorsOffset);
        Assert.Equal([21u, 22u, 23u, 24u], named);
        uint[] actors = [.. Enumerable.Range(0, (payload.Length - actorsOffset) / ActorSize)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(actorsOffset + (ActorSize * index))))];
        Assert.Equal([21u, 22u, 23u, 24u], actors);
    }

    [Fact]
    public void A_World_Supervised_Before_A_Body_Is_Lent_Carries_A_Render_Set_With_No_Entries()
    {
        // The plan is bound before SUMO has inserted anything: the render set block is written with no
        // entries, and every render set reader reads it as no set at all.
        byte[] payload = SupervisedSnapshot(RenderSetWithSupervision([], SupervisionBlock([])), 5);

        ObservedRenderSet renderSet = EpisodeStateLayout.ReadRenderSet(payload);
        Assert.True(renderSet.IsEmpty);
        Assert.False(renderSet.IsParked(5));
        Assert.Null(renderSet.Lent(5));

        ObservedSupervision supervision = EpisodeStateLayout.ReadSupervision(payload);
        Assert.True(supervision.IsCarried);
        Assert.Empty(supervision.ByActor);
        Assert.Equal([5u], EpisodeStateSensorData.Deserialize(payload).Actors.Select(actor => actor.Id));

        // And a reader that knows only the render set finds the actor, and names no body.
        (int actorsOffset, uint[] named) = ReadAsTheRenderSetAloneWasRead(payload);
        Assert.Empty(named);
        Assert.Equal(EpisodeStateLayout.ActorsOffset(payload), actorsOffset);
    }

    [Fact]
    public void An_Unlabelled_Vehicle_Costs_The_Snapshot_Nothing_However_Many_There_Are()
    {
        // Three hundred lent bodies, every vehicle unlabelled: the supervision block is the same bytes as
        // with none lent, so the population adds the render set's entries and nothing to the supervision.
        Entry[] fleet = [.. Enumerable.Range(0, 300)
            .Select(index => new Entry((uint)(100 + index), ObservedBodyState.Lent, 7, $"parked_{index:000}", "car"))];
        byte[] none = SupervisionBlock([]);
        byte[] payload = SupervisedSnapshot(RenderSetWithSupervision(fleet, none), [.. fleet.Select(entry => entry.Actor)]);

        int renderSetEntries = Block(fleet).Length - 4;
        int carried = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(WideHeaderSize));
        Assert.Equal(renderSetEntries + none.Length, carried);

        ObservedSupervision supervision = EpisodeStateLayout.ReadSupervision(payload);
        IReadOnlyDictionary<string, SupervisionInForce> vehicles =
            supervision.ForVehicles(EpisodeStateLayout.ReadRenderSet(payload));
        Assert.Equal(300, vehicles.Count);
        Assert.All(vehicles.Values, state => Assert.Same(SupervisionInForce.Unlabelled, state));

        // One annotated vehicle costs its row and nothing else: its actor, state and annotation count,
        // then each field of its one annotation behind its 16-bit length or count.
        byte[] one = SupervisionBlock([EscortRow with { Actor = 100 }]);
        Annotation lead = EscortRow.Annotations[0];
        int row = 4 + 1 + 2
                  + 2 + lead.Instance.Length
                  + 2 + lead.Phase.Length
                  + 2 + lead.Role.Length
                  + 2 + lead.Labels.Sum(label => 2 + label.Length);
        Assert.Equal(row, one.Length - none.Length);
        Assert.Equal(149, row - 7);
    }

    [Fact]
    public void A_Frame_Carrying_The_Block_The_Frame_Before_Did_Shares_Its_Supervision()
    {
        ObservedSupervision first = EpisodeStateLayout.ReadSupervision(Drive(EscortRow, GuardRow));
        ObservedSupervision again = EpisodeStateLayout.ReadSupervision(Drive(EscortRow, GuardRow), first);
        ObservedSupervision changed = EpisodeStateLayout.ReadSupervision(Drive(EscortRow), first);

        Assert.Same(first, again);
        Assert.NotSame(first, changed);
        Assert.Same(SupervisionInForce.Unlabelled, changed.Of(22));
    }

    [Fact]
    public void A_Change_Of_Supervision_Alone_Leaves_The_Frame_s_Render_Set_The_One_Read_Before()
    {
        // The set's own bytes did not change, only the supervision after them, so a reader keeps the one
        // set it already parsed.
        ObservedRenderSet first = EpisodeStateLayout.ReadRenderSet(Drive(EscortRow, GuardRow));
        ObservedRenderSet next = EpisodeStateLayout.ReadRenderSet(Drive(EscortRow), first);

        Assert.Same(first, next);
    }

    [Fact]
    public void A_Truncated_Supervision_Block_Is_Refused_And_The_Actors_And_The_Render_Set_Are_Still_Read()
    {
        // The supervision says it holds two rows and carries one: the render set block's size is honest
        // about the bytes it has, so the actors are found after it, but no row is read as missing.
        byte[] supervision = SupervisionBlock([EscortRow]);
        int rowCountAt = 4 + 2 + PlanId.Length + 4 + 2 + VocabularyDigest.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(supervision.AsSpan(rowCountAt), 2u);
        byte[] payload = SupervisedSnapshot(RenderSetWithSupervision([Escort], supervision), 21, 22);

        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadSupervision(payload));

        EpisodeStateSensorData data = EpisodeStateSensorData.Deserialize(payload);
        Assert.Equal([21u, 22u], data.Actors.Select(actor => actor.Id));
        Assert.Equal(1, data.Header.RenderSet.Count);
        // Read as unreadable, not as none: a plan was in force, and what it asserted is unknown.
        Assert.Same(ObservedSupervision.Unreadable, data.Header.Supervision);
        Assert.True(data.Header.Supervision.IsUnreadable);
        Assert.False(data.Header.Supervision.IsCarried);
    }

    [Fact]
    public void A_Row_In_A_State_This_Reader_Does_Not_Know_Is_Refused_Rather_Than_Read_As_Unlabelled()
    {
        byte[] payload = SupervisedSnapshot(
            RenderSetWithSupervision([Escort], SupervisionBlock([EscortRow with { State = 3 }])), 21);

        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadSupervision(payload));
    }

    [Fact]
    public void Supervision_Said_To_Be_Carried_Without_The_Render_Set_Block_It_Is_Written_Inside_Is_Refused()
    {
        byte[] payload = Snapshot(block: null, 21);
        payload[32] |= (byte)SimulationState.SupervisionCarried;

        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadSupervision(payload));
        Assert.Equal(WideHeaderSize, EpisodeStateLayout.ActorsOffset(payload));
    }

    /// <summary>
    /// The snapshot as a reader built with the render set and before supervision read it: the actors
    /// after the render set block by its size, and the set's count and entries from its start, with
    /// whatever follows the entries inside the block left alone.
    /// </summary>
    private static (int ActorsOffset, uint[] Named) ReadAsTheRenderSetAloneWasRead(byte[] payload)
    {
        if ((payload[32] & 0x10) == 0)
        {
            return (WideHeaderSize, []);
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(WideHeaderSize));
        int at = WideHeaderSize + 4;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at));
        at += 4;
        var named = new List<uint>();
        for (uint entry = 0; entry < count; entry++)
        {
            named.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at)));
            at += 4 + 1 + 8;
            at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at));
            at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at));
        }

        return (WideHeaderSize + 4 + (int)size, [.. named]);
    }
}
