// Source: carla/rpc/CommandResponse.h — using CommandResponse = Response<ActorId>
// Wire format: [[variant_idx, payload]] per Response<T>'s MSGPACK_DEFINE_ARRAY(_data)
//   [[0, ["error message"]]] = failure
//   [[1, actor_id]]          = success
using CarlaNet.Types.Formatters;

namespace CarlaNet.Types.Rpc.Commands;

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
