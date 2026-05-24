// Source: carla/rpc/MapInfo.h — MSGPACK_DEFINE_ARRAY(name, recommended_spawn_points)
namespace CarlaNet.Types.Rpc.Actors;

[MessagePackObject]
public record struct MapInfo(
    [property: Key(0)] string Name,
    [property: Key(1)] IReadOnlyList<Transform> RecommendedSpawnPoints);
