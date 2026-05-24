// Source: carla/rpc/WalkerBoneControlOut.h — MSGPACK_DEFINE_ARRAY(bone_transforms)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct WalkerBoneControlOut(
    [property: Key(0)] IReadOnlyList<BoneTransformDataOut> BoneTransforms);
