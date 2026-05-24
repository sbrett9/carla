// Source: carla/rpc/EpisodeSettings.h
// MSGPACK_DEFINE_ARRAY(synchronous_mode, no_rendering_mode, fixed_delta_seconds,
//   substepping, max_substep_delta_time, max_substeps, max_culling_distance,
//   deterministic_ragdolls, tile_stream_distance, actor_active_distance, spectator_as_ego)
using CarlaNet.Types.Formatters;

namespace CarlaNet.Types.Rpc.Environment;

[MessagePackObject]
public record struct EpisodeSettings(
    [property: Key(0)] bool SynchronousMode,
    [property: Key(1)] bool NoRenderingMode,
    // std::optional<double> — custom formatter required (§13.2): nil=null, raw double=value
    [property: Key(2), MessagePackFormatter(typeof(NullableDoubleFormatter))] double? FixedDeltaSeconds,
    [property: Key(3)] bool Substepping,
    [property: Key(4)] double MaxSubstepDeltaTime,
    [property: Key(5)] int MaxSubsteps,
    [property: Key(6)] float MaxCullingDistance,
    [property: Key(7)] bool DeterministicRagdolls,
    [property: Key(8)] float TileStreamDistance,
    [property: Key(9)] float ActorActiveDistance,
    [property: Key(10)] bool SpectatorAsEgo);
