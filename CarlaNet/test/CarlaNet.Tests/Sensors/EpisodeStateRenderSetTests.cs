// The render set block of the world-observer snapshot, and where it puts the actors.
// Mirrors: LibCarla/source/carla/sensor/s11n/EpisodeStateSerializer.h (RenderSetCarried,
// RenderSetEntryState) and Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/WorldObserver.cpp.
//
// A SUMO drive's vehicles are a pool of bodies, each lent to a SUMO vehicle while it is drawn and
// parked 300 m below the ground between loans. The session names each body to the server as it lends
// it and gives it back, and the server writes what it was told between the header and the actors, so
// every client reads which bodies a frame drew, and for whom, with the frame's own actors.
using System.Buffers.Binary;
using System.Text;
using CarlaNet.Sensors;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Sensors;

public class EpisodeStateRenderSetTests
{
    private const int WideHeaderSize = 132;
    private const int ActorSize = 119;

    /// One render set entry as the server writes it.
    internal sealed record Entry(uint Actor, ObservedBodyState State, ulong Admitted, string Vehicle, string Type);

    /// The block's bytes: its size, its count, then every entry, little-endian and unpadded.
    internal static byte[] Block(params Entry[] entries)
    {
        var body = new List<byte>();
        body.AddRange(LittleEndian((uint)entries.Length));
        foreach (Entry entry in entries)
        {
            body.AddRange(LittleEndian(entry.Actor));
            body.Add((byte)entry.State);
            var admitted = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(admitted, entry.Admitted);
            body.AddRange(admitted);
            body.AddRange(Name(entry.Vehicle));
            body.AddRange(Name(entry.Type));
        }

        return [.. LittleEndian((uint)body.Count), .. body];
    }

    /// A snapshot as a server that carries the corrected elevation writes it: the 132-byte header,
    /// the render set block where one is given, then one actor record per id.
    internal static byte[] Snapshot(byte[]? block, params uint[] actors)
    {
        int blockSize = block?.Length ?? 0;
        var bytes = new byte[WideHeaderSize + blockSize + (ActorSize * actors.Length)];
        bytes[32] = (byte)(SimulationState.SolarCorrectedElevationCarried
                           | (block is null ? SimulationState.None : SimulationState.RenderSetCarried));
        block?.CopyTo(bytes, WideHeaderSize);
        for (int i = 0; i < actors.Length; i++)
        {
            Span<byte> actor = bytes.AsSpan(WideHeaderSize + blockSize + (ActorSize * i), ActorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(actor, actors[i]);
            actor[4] = (byte)ActorState.Active;
        }

        return bytes;
    }

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Name(string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        var bytes = new byte[2 + utf8.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)utf8.Length);
        utf8.CopyTo(bytes, 2);
        return bytes;
    }

    private static readonly Entry Lent = new(21, ObservedBodyState.Lent, 4180, "escort_0", "military_truck");
    private static readonly Entry Parked = new(22, ObservedBodyState.Parked, 0, "", "");

    [Fact]
    public void A_Snapshot_Of_A_World_No_Session_Named_A_Body_In_Is_Laid_Out_And_Read_As_Before()
    {
        byte[] payload = Snapshot(block: null, 21, 22, 23);

        Assert.Equal(WideHeaderSize, EpisodeStateLayout.ActorsOffset(payload));
        Assert.Same(ObservedRenderSet.None, EpisodeStateLayout.ReadRenderSet(payload));

        EpisodeStateSensorData data = EpisodeStateSensorData.Deserialize(payload);
        Assert.Equal([21u, 22u, 23u], data.Actors.Select(actor => actor.Id));
        Assert.True(data.Header.RenderSet.IsEmpty);
    }

    [Fact]
    public void The_Actors_Start_After_The_Render_Set_Block()
    {
        byte[] block = Block(Lent, Parked);
        byte[] payload = Snapshot(block, 21, 22, 23);

        // Read as actors, the block would make a first actor out of its own size and count, and put
        // every real actor at the wrong offset.
        Assert.Equal(WideHeaderSize + block.Length, EpisodeStateLayout.ActorsOffset(payload));
        EpisodeStateSensorData data = EpisodeStateSensorData.Deserialize(payload);
        Assert.Equal([21u, 22u, 23u], data.Actors.Select(actor => actor.Id));
        Assert.All(data.Actors, actor => Assert.Equal(ActorState.Active, actor.State));
        Assert.True(data.Header.SimulationState.HasFlag(SimulationState.RenderSetCarried));
        Assert.Equal(2, data.Header.RenderSet.Count);
    }

    [Fact]
    public void A_Lent_Body_Is_Read_With_Its_Vehicle_And_A_Parked_One_As_Parked()
    {
        ObservedRenderSet renderSet = EpisodeStateLayout.ReadRenderSet(Snapshot(Block(Lent, Parked), 21, 22, 23));

        Assert.True(renderSet.TryGetLent(21, out ObservedBody? lent));
        Assert.Equal(new ObservedBody(21, ObservedBodyState.Lent, "escort_0", "military_truck", 4180), lent);
        Assert.False(renderSet.IsParked(21));

        Assert.True(renderSet.IsParked(22));
        Assert.False(renderSet.TryGetLent(22, out _));

        // An actor the set does not name is neither: it reads as it always did.
        Assert.False(renderSet.IsParked(23));
        Assert.False(renderSet.TryGetLent(23, out _));
        Assert.Equal(1, renderSet.LentCount);
    }

    [Fact]
    public void A_Vehicle_Name_Is_Read_As_The_UTF8_It_Was_Written_In()
    {
        var named = new Entry(30, ObservedBodyState.Lent, 7, "flow_Bāhonar.12", "pick-up/π");

        ObservedRenderSet renderSet = EpisodeStateLayout.ReadRenderSet(Snapshot(Block(named), 30));

        Assert.True(renderSet.TryGetLent(30, out ObservedBody? body));
        Assert.Equal("flow_Bāhonar.12", body.VehicleId);
        Assert.Equal("pick-up/π", body.VehicleTypeId);
    }

    [Fact]
    public void A_Frame_Carrying_The_Block_The_Frame_Before_Did_Shares_Its_Set()
    {
        // The set changes only when a body is lent or given back, so most frames carry the bytes the
        // frame before did, and the client keeps one instance for all of them.
        ObservedRenderSet first = EpisodeStateLayout.ReadRenderSet(Snapshot(Block(Lent, Parked), 21, 22));
        ObservedRenderSet again = EpisodeStateLayout.ReadRenderSet(Snapshot(Block(Lent, Parked), 21, 22), first);
        ObservedRenderSet changed = EpisodeStateLayout.ReadRenderSet(
            Snapshot(Block(Lent with { Vehicle = "escort_1" }, Parked), 21, 22), first);

        Assert.Same(first, again);
        Assert.NotSame(first, changed);
        Assert.True(changed.TryGetLent(21, out ObservedBody? body));
        Assert.Equal("escort_1", body.VehicleId);
    }

    [Fact]
    public void A_Block_That_Ends_Part_Way_Through_An_Entry_Is_Refused_And_The_Actors_Are_Still_Found()
    {
        // The block says it holds two entries and carries one: its stated size is honest about the
        // bytes it has, so the actors are found after it, but the set is not read as one entry short.
        byte[] whole = Block(Lent);
        BinaryPrimitives.WriteUInt32LittleEndian(whole.AsSpan(4), 2u);
        byte[] payload = Snapshot(whole, 21, 22);

        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadRenderSet(payload));
        Assert.Equal(WideHeaderSize + whole.Length, EpisodeStateLayout.ActorsOffset(payload));

        // The decoder reads such a snapshot's actors and no set, rather than losing the frame.
        EpisodeStateSensorData data = EpisodeStateSensorData.Deserialize(payload);
        Assert.Equal([21u, 22u], data.Actors.Select(actor => actor.Id));
        Assert.True(data.Header.RenderSet.IsEmpty);
    }

    [Fact]
    public void A_Block_Larger_Than_The_Snapshot_Puts_No_Actor_At_A_Wrong_Offset()
    {
        byte[] block = Block(Lent);
        BinaryPrimitives.WriteUInt32LittleEndian(block, 100_000u);
        byte[] payload = Snapshot(block, 21);

        Assert.Equal(payload.Length, EpisodeStateLayout.ActorsOffset(payload));
        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadRenderSet(payload));
    }
}
