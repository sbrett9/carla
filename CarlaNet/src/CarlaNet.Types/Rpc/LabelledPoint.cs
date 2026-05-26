// Source: carla/rpc/LabelledPoint.h — MSGPACK_DEFINE_ARRAY(_location, _label)
using CarlaNet.Types.Rpc.Enums;

namespace CarlaNet.Types.Rpc;

[MessagePackObject]
public record struct LabelledPoint(
    [property: Key(0)] Location Location,
    [property: Key(1)] CityObjectLabel Label)
{
    [IgnoreMember] public Location location => Location;
    [IgnoreMember] public CityObjectLabel label => Label;
}
