// Source: carla/rpc/BoneTransformDataOut.h
// MSGPACK_DEFINE_ARRAY(bone_name, world, component, relative)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct BoneTransformDataOut(
    [property: Key(0)] string BoneName,
    [property: Key(1)] Transform World,
    [property: Key(2)] Transform Component,
    [property: Key(3)] Transform Relative);
