// Source: carla/rpc/WalkerBoneControlIn.h — MSGPACK_DEFINE_ARRAY(bone_transforms)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct WalkerBoneControlIn(
    [property: Key(0)] IReadOnlyList<BoneTransformDataIn> BoneTransforms);
