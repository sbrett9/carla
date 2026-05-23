// CommandResponse = Response<ActorId> in C++ (carla/rpc/CommandResponse.h)
// Wire format (same as all Response<T>): MSGPACK_DEFINE_ARRAY(_data) where
// _data is std::variant<ResponseError, ActorId>:
//   [[0, ["error message"]]]  → failure
//   [[1, actor_id_uint32]]    → success
using CarlaNet.Types.Rpc.Commands;
using MessagePack.Formatters;

namespace CarlaNet.Types.Formatters;

public sealed class CommandResponseFormatter : IMessagePackFormatter<CommandResponse>
{
    public static readonly CommandResponseFormatter Instance = new();

    public CommandResponse Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        int outer = reader.ReadArrayHeader();  // MSGPACK_DEFINE_ARRAY(_data) = 1
        if (outer == 0) return CommandResponse.Success(0);
        int inner = reader.ReadArrayHeader();  // variant [idx, payload] = 2
        int idx   = reader.ReadInt32();
        if (idx == 0)  // ResponseError: MSGPACK_DEFINE_ARRAY(_what) = ["msg"]
        {
            int errArr = reader.ReadArrayHeader();
            string err = errArr > 0 ? (reader.ReadString() ?? "unknown error") : "unknown error";
            // skip remaining fields if any
            for (int i = 1; i < errArr; i++) reader.Skip();
            return CommandResponse.Failure(err);
        }
        // idx == 1 → ActorId
        uint actorId = reader.ReadUInt32();
        return CommandResponse.Success(actorId);
    }

    public void Serialize(ref MessagePackWriter writer, CommandResponse value, MessagePackSerializerOptions options)
        => throw new NotSupportedException("CommandResponse is read-only (server sends, client receives)");
}
