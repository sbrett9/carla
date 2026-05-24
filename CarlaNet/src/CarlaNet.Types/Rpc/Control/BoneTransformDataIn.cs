// Source: carla/rpc/BoneTransformDataIn.h — std::pair<std::string, geom::Transform>
// Serialized as 2-element array [bone_name, transform]
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct BoneTransformDataIn(
    [property: Key(0)] string BoneName,
    [property: Key(1)] Transform Transform);
