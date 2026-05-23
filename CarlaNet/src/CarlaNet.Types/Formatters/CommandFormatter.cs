// Serializes carla::rpc::Command to its wire format.
//
// Source: carla/rpc/Command.h + carla/MsgPackAdaptors.h
//
// Command struct: MSGPACK_DEFINE_ARRAY(command) where command is std::variant<...>
// std::variant serializes as [variant_index, payload].
// So each Command wire bytes = [[variant_idx, [field0, field1, ...]]]
//
// SpawnActor.parent is std::optional<ActorId> = [false] | [true, actor_id]
// do_after is std::vector<Command> — each element recursively follows this formatter.
using System.Buffers;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Enums;
using MessagePack.Formatters;

namespace CarlaNet.Types.Formatters;

public sealed class CommandFormatter : IMessagePackFormatter<Command?>
{
    public static readonly CommandFormatter Instance = new();

    public Command? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        => throw new NotSupportedException("Commands are write-only (apply_batch sends, not receives)");

    public void Serialize(ref MessagePackWriter writer, Command? value, MessagePackSerializerOptions options)
    {
        if (value is null) { writer.WriteNil(); return; }

        // MSGPACK_DEFINE_ARRAY(command) → 1-element outer array
        writer.WriteArrayHeader(1);

        // std::variant → [variant_idx, payload]
        var buf = new ArrayBufferWriter<byte>();
        int idx = WritePayload(buf, value, options);

        writer.WriteArrayHeader(2);
        writer.Write(idx);
        writer.WriteRaw(buf.WrittenSpan);
    }

    private static int WritePayload(IBufferWriter<byte> buf, Command value, MessagePackSerializerOptions options)
    {
        var w = new MessagePackWriter(buf);
        int idx = value switch
        {
            SpawnActorCommand c => WriteSpawnActor(ref w, c, options),
            DestroyActorCommand c => Write1(ref w, (int)CommandType.DestroyActor, c.Actor),
            ApplyVehicleControlCommand c => Write2Obj(ref w, (int)CommandType.ApplyVehicleControl, c.Actor, c.Control, options),
            ApplyVehicleAckermannControlCommand c => Write2Obj(ref w, (int)CommandType.ApplyVehicleAckermannControl, c.Actor, c.Control, options),
            ApplyWalkerControlCommand c => Write2Obj(ref w, (int)CommandType.ApplyWalkerControl, c.Actor, c.Control, options),
            ApplyVehiclePhysicsControlCommand c => Write2Obj(ref w, (int)CommandType.ApplyVehiclePhysicsControl, c.Actor, c.PhysicsControl, options),
            ApplyTransformCommand c => Write2Obj(ref w, (int)CommandType.ApplyTransform, c.Actor, c.Transform, options),
            ApplyWalkerStateCommand c => WriteWalkerState(ref w, c, options),
            ApplyTargetVelocityCommand c => Write2Obj(ref w, (int)CommandType.ApplyTargetVelocity, c.Actor, c.Velocity, options),
            ApplyTargetAngularVelocityCommand c => Write2Obj(ref w, (int)CommandType.ApplyTargetAngularVelocity, c.Actor, c.AngularVelocity, options),
            ApplyImpulseCommand c => Write2Obj(ref w, (int)CommandType.ApplyImpulse, c.Actor, c.Impulse, options),
            ApplyForceCommand c => Write2Obj(ref w, (int)CommandType.ApplyForce, c.Actor, c.Force, options),
            ApplyAngularImpulseCommand c => Write2Obj(ref w, (int)CommandType.ApplyAngularImpulse, c.Actor, c.Impulse, options),
            ApplyTorqueCommand c => Write2Obj(ref w, (int)CommandType.ApplyTorque, c.Actor, c.Torque, options),
            SetSimulatePhysicsCommand c => Write2Bool(ref w, (int)CommandType.SetSimulatePhysics, c.Actor, c.Enabled),
            SetEnableGravityCommand c => Write2Bool(ref w, (int)CommandType.SetEnableGravity, c.Actor, c.Enabled),
            SetAutopilotCommand c => Write2Bool(ref w, (int)CommandType.SetAutopilot, c.Actor, c.Enabled),
            ShowDebugTelemetryCommand c => Write2Bool(ref w, (int)CommandType.ShowDebugTelemetry, c.Actor, c.Enabled),
            SetVehicleLightStateCommand c => WriteVehicleLightState(ref w, c),
            ApplyLocationCommand c => Write2Obj(ref w, (int)CommandType.ApplyLocation, c.Actor, c.Location, options),
            ConsoleCommandCommand c => WriteConsoleCommand(ref w, c),
            SetTrafficLightStateCommand c => WriteTrafficLightState(ref w, c),
            _ => throw new NotSupportedException($"Unknown command type: {value.GetType().Name}")
        };
        w.Flush();
        return idx;
    }

    private static int WriteSpawnActor(ref MessagePackWriter w, SpawnActorCommand c, MessagePackSerializerOptions options)
    {
        // MSGPACK_DEFINE_ARRAY(description, transform, parent, do_after)
        // parent = std::optional<ActorId>
        w.WriteArrayHeader(4);
        MessagePackSerializer.Serialize(ref w, c.Description, options);
        MessagePackSerializer.Serialize(ref w, c.Transform, options);
        // std::optional<ActorId>: [false] or [true, actor_id]
        if (c.Parent is null) { w.WriteArrayHeader(1); w.Write(false); }
        else { w.WriteArrayHeader(2); w.Write(true); w.Write(c.Parent.Value); }
        // do_after: recursive vector<Command>
        w.WriteArrayHeader(c.DoAfter?.Count ?? 0);
        if (c.DoAfter is not null)
            foreach (var sub in c.DoAfter)
                MessagePackSerializer.Serialize(ref w, sub, options);
        return (int)CommandType.SpawnActor;
    }

    private static int WriteWalkerState(ref MessagePackWriter w, ApplyWalkerStateCommand c, MessagePackSerializerOptions options)
    {
        // MSGPACK_DEFINE_ARRAY(actor, transform, speed)
        w.WriteArrayHeader(3);
        w.Write(c.Actor);
        MessagePackSerializer.Serialize(ref w, c.Transform, options);
        w.Write(c.Speed);
        return (int)CommandType.ApplyWalkerState;
    }

    private static int WriteVehicleLightState(ref MessagePackWriter w, SetVehicleLightStateCommand c)
    {
        // MSGPACK_DEFINE_ARRAY(actor, light_state)
        w.WriteArrayHeader(2);
        w.Write(c.Actor);
        w.Write((uint)c.LightState);
        return (int)CommandType.SetVehicleLightState;
    }

    private static int WriteConsoleCommand(ref MessagePackWriter w, ConsoleCommandCommand c)
    {
        // MSGPACK_DEFINE_ARRAY(cmd)
        w.WriteArrayHeader(1);
        w.Write(c.Cmd);
        return (int)CommandType.ConsoleCommand;
    }

    private static int WriteTrafficLightState(ref MessagePackWriter w, SetTrafficLightStateCommand c)
    {
        // MSGPACK_DEFINE_ARRAY(actor, traffic_light_state)
        w.WriteArrayHeader(2);
        w.Write(c.Actor);
        w.Write((uint)c.TrafficLightState);
        return (int)CommandType.SetTrafficLightState;
    }

    private static int Write1(ref MessagePackWriter w, int idx, uint actorId)
    {
        w.WriteArrayHeader(1);
        w.Write(actorId);
        return idx;
    }

    private static int Write2Bool(ref MessagePackWriter w, int idx, uint actorId, bool flag)
    {
        w.WriteArrayHeader(2);
        w.Write(actorId);
        w.Write(flag);
        return idx;
    }

    private static int Write2Obj<T>(ref MessagePackWriter w, int idx, uint actorId, T obj, MessagePackSerializerOptions options)
    {
        w.WriteArrayHeader(2);
        w.Write(actorId);
        MessagePackSerializer.Serialize(ref w, obj, options);
        return idx;
    }
}
