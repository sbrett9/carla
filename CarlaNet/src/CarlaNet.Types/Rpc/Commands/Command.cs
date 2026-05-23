// Source: carla/rpc/Command.h
// std::variant<> index order is the declaration order in CommandType (verified from source).
// Wire format: MSGPACK_DEFINE_ARRAY(command) -> [[variant_index, [field0, field1, ...]]]
using CarlaNet.Types.Rpc.Control;
using CarlaNet.Types.Rpc.Physics;
using CarlaNet.Types.Rpc.Enums;
using CarlaNet.Types.Formatters;

namespace CarlaNet.Types.Rpc.Commands;

// Variant indices — declaration order in Command.h std::variant<>
public enum CommandType : int
{
    SpawnActor = 0,
    DestroyActor = 1,
    ApplyVehicleControl = 2,
    ApplyVehicleAckermannControl = 3,
    ApplyWalkerControl = 4,
    ApplyVehiclePhysicsControl = 5,
    ApplyTransform = 6,
    ApplyWalkerState = 7,      // source-verified: ApplyWalkerState before ApplyLocation
    ApplyTargetVelocity = 8,
    ApplyTargetAngularVelocity = 9,
    ApplyImpulse = 10,
    ApplyForce = 11,
    ApplyAngularImpulse = 12,
    ApplyTorque = 13,
    SetSimulatePhysics = 14,
    SetEnableGravity = 15,
    SetAutopilot = 16,
    ShowDebugTelemetry = 17,
    SetVehicleLightState = 18,
    ApplyLocation = 19,        // source-verified: ApplyLocation at index 19
    ConsoleCommand = 20,
    SetTrafficLightState = 21
}

// Each command payload is serialized as a msgpack array per its MSGPACK_DEFINE_ARRAY.
// The Command struct wraps one of these in [[index, payload]].
// CommandFormatter handles the full [[variant_idx, payload]] encoding.

[MessagePackFormatter(typeof(CommandFormatter))]
public abstract record Command;

// MSGPACK_DEFINE_ARRAY(description, transform, parent, do_after)
public record SpawnActorCommand(
    ActorDescription Description,
    Transform Transform,
    ActorId? Parent,
    IReadOnlyList<Command> DoAfter) : Command;

// MSGPACK_DEFINE_ARRAY(actor)
public record DestroyActorCommand(ActorId Actor) : Command;

// MSGPACK_DEFINE_ARRAY(actor, control)
public record ApplyVehicleControlCommand(ActorId Actor, VehicleControl Control) : Command;
public record ApplyVehicleAckermannControlCommand(ActorId Actor, VehicleAckermannControl Control) : Command;
public record ApplyWalkerControlCommand(ActorId Actor, WalkerControl Control) : Command;

// MSGPACK_DEFINE_ARRAY(actor, physics_control)
public record ApplyVehiclePhysicsControlCommand(ActorId Actor, VehiclePhysicsControl PhysicsControl) : Command;

// MSGPACK_DEFINE_ARRAY(actor, transform)
public record ApplyTransformCommand(ActorId Actor, Transform Transform) : Command;

// MSGPACK_DEFINE_ARRAY(actor, transform, speed)
public record ApplyWalkerStateCommand(ActorId Actor, Transform Transform, float Speed) : Command;

// MSGPACK_DEFINE_ARRAY(actor, velocity)
public record ApplyTargetVelocityCommand(ActorId Actor, Vector3D Velocity) : Command;

// MSGPACK_DEFINE_ARRAY(actor, angular_velocity)
public record ApplyTargetAngularVelocityCommand(ActorId Actor, Vector3D AngularVelocity) : Command;

// MSGPACK_DEFINE_ARRAY(actor, impulse)
public record ApplyImpulseCommand(ActorId Actor, Vector3D Impulse) : Command;
public record ApplyAngularImpulseCommand(ActorId Actor, Vector3D Impulse) : Command;

// MSGPACK_DEFINE_ARRAY(actor, force)
public record ApplyForceCommand(ActorId Actor, Vector3D Force) : Command;

// MSGPACK_DEFINE_ARRAY(actor, torque)
public record ApplyTorqueCommand(ActorId Actor, Vector3D Torque) : Command;

// MSGPACK_DEFINE_ARRAY(actor, enabled)
public record SetSimulatePhysicsCommand(ActorId Actor, bool Enabled) : Command;
public record SetEnableGravityCommand(ActorId Actor, bool Enabled) : Command;

// MSGPACK_DEFINE_ARRAY(actor, enabled) — tm_port NOT in MSGPACK (source-verified)
public record SetAutopilotCommand(ActorId Actor, bool Enabled) : Command;
public record ShowDebugTelemetryCommand(ActorId Actor, bool Enabled) : Command;

// MSGPACK_DEFINE_ARRAY(actor, light_state)
public record SetVehicleLightStateCommand(ActorId Actor, VehicleLightStateFlags LightState) : Command;

// MSGPACK_DEFINE_ARRAY(actor, location)
public record ApplyLocationCommand(ActorId Actor, Location Location) : Command;

// MSGPACK_DEFINE_ARRAY(cmd)
public record ConsoleCommandCommand(string Cmd) : Command;

// MSGPACK_DEFINE_ARRAY(actor, traffic_light_state)
public record SetTrafficLightStateCommand(ActorId Actor, TrafficLightState TrafficLightState) : Command;

// Source: carla/rpc/CommandResponse.h — using CommandResponse = Response<ActorId>
// Wire format: [[variant_idx, payload]] per Response<T>'s MSGPACK_DEFINE_ARRAY(_data)
//   [[0, ["error message"]]] = failure
//   [[1, actor_id]]          = success
[MessagePackFormatter(typeof(CommandResponseFormatter))]
public readonly struct CommandResponse
{
    public bool HasError { get; }
    public string? Error { get; }
    public uint ActorId { get; }

    private CommandResponse(uint actorId, string? error) { ActorId = actorId; Error = error; HasError = error is not null; }
    public static CommandResponse Success(uint actorId) => new(actorId, null);
    public static CommandResponse Failure(string error) => new(0, error);
    public override string ToString() => HasError ? $"Error({Error})" : $"Ok({ActorId})";
}
