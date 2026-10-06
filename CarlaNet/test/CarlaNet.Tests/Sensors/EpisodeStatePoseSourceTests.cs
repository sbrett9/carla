// The pose source block of the world-observer snapshot, inside the render set block, the vehicle light
// state in each vehicle's record, and what a reader makes of them. Mirrors:
// LibCarla/source/carla/sensor/s11n/EpisodeStateSerializer.h (PoseSourceCarried, PoseSourceEntryState,
// VehicleLightStateCarried), LibCarla/source/carla/sensor/data/ActorDynamicState.h (VehicleData) and
// Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/WorldObserver.cpp.
//
// A SUMO drive poses its bodies every world tick from SUMO steps a whole number of ticks apart. The
// session declares the step once and names a body only as it stops or starts following it, and the
// server carries both on every snapshot, so any reader takes from the frame number alone whether a body
// stood at SUMO's own step or between two (the owner's ruling of 2026-10-06).
using System.Buffers.Binary;
using CarlaNet.Sensors;
using CarlaNet.Transport;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Streaming;
using static CarlaNet.Tests.Sensors.EpisodeStateRenderSetTests;
using static CarlaNet.Tests.Sensors.EpisodeStateSupervisionTests;

namespace CarlaNet.Tests.Sensors;

public class EpisodeStatePoseSourceTests
{
    private const int WideHeaderSize = 132;
    private const byte NamedSimulated = 1;
    private const byte NamedHeld = 2;

    private static readonly Entry Escort = new(21, ObservedBodyState.Lent, 4180, "escort_0", "military_truck");
    private static readonly Entry Guard = new(22, ObservedBodyState.Lent, 4100, "guard_d4_h15_t3", "guard");
    private static readonly Entry Parked = new(24, ObservedBodyState.Parked, 0, "", "");

    /// The pose source block as the server writes it: its size, the step, the frame it falls on, then the
    /// named bodies, little-endian and unpadded.
    internal static byte[] PoseSourceBlock(uint ticksPerStep, ulong stepFrame, params (uint Actor, byte State)[] named)
    {
        var body = new byte[4 + 8 + 4 + (5 * named.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ticksPerStep);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), stepFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)named.Length);
        for (int index = 0; index < named.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16 + (5 * index)), named[index].Actor);
            body[16 + (5 * index) + 4] = named[index].State;
        }

        var block = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)body.Length);
        body.CopyTo(block, 4);
        return block;
    }

    /// The render set block with what the server writes inside it after the entries, the size counting all
    /// of it.
    internal static byte[] RenderSetWith(IReadOnlyList<Entry> entries, params byte[][] after)
    {
        byte[] renderSet = Block([.. entries]);
        byte[] contents = [.. renderSet.AsSpan(4), .. after.SelectMany(part => part)];
        var block = new byte[4 + contents.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)contents.Length);
        contents.CopyTo(block, 4);
        return block;
    }

    /// A snapshot whose render set block carries a pose source, and supervision where asked.
    internal static byte[] PoseSourceSnapshot(byte[] block, bool supervised, params uint[] actors)
    {
        byte[] bytes = Snapshot(block, actors);
        bytes[32] |= (byte)SimulationState.PoseSourceCarried;
        if (supervised)
        {
            bytes[32] |= (byte)SimulationState.SupervisionCarried;
        }

        return bytes;
    }

    private static byte[] Drive(uint ticksPerStep, ulong stepFrame, params (uint Actor, byte State)[] named) =>
        PoseSourceSnapshot(RenderSetWith([Escort, Guard, Parked], PoseSourceBlock(ticksPerStep, stepFrame, named)),
                           supervised: false, 21, 22, 24, 25);

    [Fact]
    public void A_Snapshot_From_A_Server_That_Carries_No_Pose_Source_Gives_No_Body_One()
    {
        // An older server, or a world no session declared its step in: the render set alone.
        byte[] payload = Snapshot(Block(Escort, Parked), 21, 24);

        ObservedPoseSource source = EpisodeStateLayout.ReadPoseSource(payload);

        Assert.Same(ObservedPoseSource.None, source);
        Assert.False(source.IsCarried);
        Assert.Null(source.Of(21, 100));
        Assert.Null(source.ForLentBody(EpisodeStateLayout.ReadRenderSet(payload), 21, 100));
        Assert.Same(ObservedPoseSource.None, EpisodeStateSensorData.Deserialize(payload).Header.PoseSource);
    }

    [Fact]
    public void At_One_Tick_Per_Step_Every_Frame_Shows_SUMO_s_Own_Step()
    {
        ObservedPoseSource source = EpisodeStateLayout.ReadPoseSource(Drive(1, 4181));

        for (ulong frame = 4181; frame < 4241; frame++)
        {
            Assert.Equal(PoseSource.Simulated, source.Of(21, frame));
            Assert.True(source.StepFallsOn(frame));
        }
    }

    [Fact]
    public void At_Twenty_Ticks_Per_Step_One_Frame_In_Twenty_Shows_SUMO_s_Step_And_The_Rest_Are_Interpolated()
    {
        ObservedPoseSource source = EpisodeStateLayout.ReadPoseSource(Drive(20, 4181));

        PoseSource?[] frames = [.. Enumerable.Range(0, 200).Select(offset => source.Of(22, 4181 + (ulong)offset))];

        Assert.Equal(10, frames.Count(each => each == PoseSource.Simulated));
        Assert.Equal(190, frames.Count(each => each == PoseSource.Interpolated));
        Assert.All(Enumerable.Range(0, 10), step => Assert.Equal(PoseSource.Simulated, frames[step * 20]));
        Assert.Equal(PoseSource.Interpolated, source.Of(22, 4182));
        Assert.Equal(PoseSource.Interpolated, source.Of(22, 4200));
        Assert.Equal(PoseSource.Simulated, source.Of(22, 4201));
        // A frame before the step was declared is not one the step says anything about.
        Assert.Null(source.Of(22, 4180));
        Assert.Null(source.StepFallsOn(4161));
    }

    [Fact]
    public void A_Body_The_Session_Named_Is_Read_As_Named_On_Every_Frame_And_The_Others_Follow_The_Step()
    {
        // The escort left where its last pose put it, the guard placed at SUMO's later step across a
        // discontinuity.
        byte[] payload = Drive(20, 4181, (21, NamedHeld), (22, NamedSimulated));
        ObservedPoseSource source = EpisodeStateLayout.ReadPoseSource(payload);
        ObservedRenderSet renderSet = EpisodeStateLayout.ReadRenderSet(payload);

        Assert.Equal(PoseSource.Held, source.Of(21, 4181));
        Assert.Equal(PoseSource.Held, source.Of(21, 4190));
        Assert.Equal(PoseSource.Simulated, source.Of(22, 4190));
        // An actor no entry names reads the step; joined to the render set, one it holds parked, or does
        // not name at all, has none, because no session placed it.
        Assert.Equal(PoseSource.Interpolated, source.Of(23, 4190));
        Assert.Null(source.ForLentBody(renderSet, 24, 4190));
        Assert.Null(source.ForLentBody(renderSet, 25, 4190));
        Assert.Equal(PoseSource.Held, source.ForLentBody(renderSet, 21, 4190));
    }

    [Fact]
    public void The_Pose_Source_Is_Read_After_The_Supervision_And_The_Actors_After_Both()
    {
        byte[] supervision = SupervisionBlock([new Row(21, 1, new Annotation("plan/pi_escort", "transit", "lead", "x:y"))]);
        byte[] payload = PoseSourceSnapshot(
            RenderSetWith([Escort, Guard], supervision, PoseSourceBlock(20, 4181, (21, NamedHeld))),
            supervised: true, 21, 22);
        int block = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(WideHeaderSize));

        Assert.Equal(WideHeaderSize + 4 + block, EpisodeStateLayout.ActorsOffset(payload));
        EpisodeStateSensorData data = EpisodeStateSensorData.Deserialize(payload);
        Assert.Equal([21u, 22u], data.Actors.Select(actor => actor.Id));
        Assert.Equal(2, data.Header.RenderSet.Count);
        Assert.True(data.Header.Supervision.IsCarried);
        Assert.Equal(PoseSource.Held, data.Header.PoseSource.Of(21, 4200));
        Assert.Equal(20u, data.Header.PoseSource.TicksPerStep);
        Assert.Equal(4181ul, data.Header.PoseSource.StepFrame);
        // And a reader that knows the supervision and not the pose source reads it as before.
        Assert.Single(EpisodeStateLayout.ReadSupervision(payload).ByActor);
    }

    [Fact]
    public void A_Session_That_Declared_Its_Step_Before_A_Body_Is_Lent_Carries_A_Render_Set_With_No_Entries()
    {
        byte[] payload = PoseSourceSnapshot(RenderSetWith([], PoseSourceBlock(1, 7)), supervised: false, 5);

        Assert.True(EpisodeStateLayout.ReadRenderSet(payload).IsEmpty);
        Assert.True(EpisodeStateLayout.ReadPoseSource(payload).IsCarried);
        Assert.Equal([5u], EpisodeStateSensorData.Deserialize(payload).Actors.Select(actor => actor.Id));
    }

    [Fact]
    public void A_Frame_Carrying_The_Block_The_Frame_Before_Did_Shares_Its_Pose_Source()
    {
        ObservedPoseSource first = EpisodeStateLayout.ReadPoseSource(Drive(20, 4181));
        ObservedPoseSource again = EpisodeStateLayout.ReadPoseSource(Drive(20, 4181), first);
        ObservedPoseSource changed = EpisodeStateLayout.ReadPoseSource(Drive(20, 4181, (21, NamedHeld)), first);

        Assert.Same(first, again);
        Assert.NotSame(first, changed);
    }

    [Fact]
    public void A_Truncated_Or_Unknown_Pose_Source_Is_Refused_And_The_Actors_Are_Still_Read()
    {
        byte[] truncated = PoseSourceBlock(20, 4181, (21, NamedHeld));
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(4 + 12), 2u);
        byte[] payload = PoseSourceSnapshot(RenderSetWith([Escort], truncated), supervised: false, 21, 22);

        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadPoseSource(payload));
        EpisodeStateSensorData data = EpisodeStateSensorData.Deserialize(payload);
        Assert.Equal([21u, 22u], data.Actors.Select(actor => actor.Id));
        Assert.Same(ObservedPoseSource.Unreadable, data.Header.PoseSource);
        Assert.Null(data.Header.PoseSource.Of(21, 4181));

        // A state this reader does not know is not read as following the step.
        byte[] unknown = Drive(20, 4181, (21, 9));
        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadPoseSource(unknown));
    }

    [Fact]
    public void A_Pose_Source_Said_To_Be_Carried_Without_The_Render_Set_Block_It_Is_Written_Inside_Is_Refused()
    {
        byte[] payload = Snapshot(block: null, 21);
        payload[32] |= (byte)SimulationState.PoseSourceCarried;

        Assert.Throws<InvalidDataException>(() => EpisodeStateLayout.ReadPoseSource(payload));
        Assert.Equal(WideHeaderSize, EpisodeStateLayout.ActorsOffset(payload));
    }

    [Fact]
    public void A_Vehicle_s_Lights_Are_Read_Only_From_A_Snapshot_That_Says_It_Carries_Them()
    {
        // The light state is the four bytes after the failure state in the vehicle's record: offset 30 of
        // the type-dependent union, which starts 65 bytes into the actor.
        byte[] union = new byte[54];
        const VehicleLightStateFlags lit = VehicleLightStateFlags.Position | VehicleLightStateFlags.Brake;
        BinaryPrimitives.WriteUInt32LittleEndian(union.AsSpan(30), (uint)lit);

        var carried = new ActorSnapshot { Id = 21, TypeDependentState = union, LightStateCarried = true };
        var dark = new ActorSnapshot { Id = 21, TypeDependentState = new byte[54], LightStateCarried = true };
        var older = new ActorSnapshot { Id = 21, TypeDependentState = new byte[54], LightStateCarried = false };

        Assert.Equal(lit, carried.CommandedLights());
        Assert.Equal(VehicleLightStateFlags.None, dark.CommandedLights());
        // An older server's zero is no reading, never every light off.
        Assert.Null(older.CommandedLights());

        byte[] payload = Snapshot(block: null, 21);
        Assert.False(EpisodeStateLayout.CarriesVehicleLightState(payload));
        payload[32] |= (byte)SimulationState.VehicleLightStateCarried;
        Assert.True(EpisodeStateLayout.CarriesVehicleLightState(payload));
        // The flag says nothing of where the actors start.
        Assert.Equal(WideHeaderSize, EpisodeStateLayout.ActorsOffset(payload));
    }
}
