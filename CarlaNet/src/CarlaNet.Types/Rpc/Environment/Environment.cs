// Sources: carla/rpc/EpisodeSettings.h, WeatherParameters.h, EnvironmentObject.h,
//          OpendriveGenerationParameters.h
using CarlaNet.Types.Rpc.Enums;
using CarlaNet.Types.Formatters;

namespace CarlaNet.Types.Rpc.Environment;

// Source: carla/rpc/EpisodeSettings.h
// MSGPACK_DEFINE_ARRAY(synchronous_mode, no_rendering_mode, fixed_delta_seconds,
//   substepping, max_substep_delta_time, max_substeps, max_culling_distance,
//   deterministic_ragdolls, tile_stream_distance, actor_active_distance, spectator_as_ego)
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

// Source: carla/rpc/WeatherParameters.h
// MSGPACK_DEFINE_ARRAY(cloudiness, precipitation, precipitation_deposits, wind_intensity,
//   sun_azimuth_angle, sun_altitude_angle, fog_density, fog_distance, fog_falloff,
//   wetness, scattering_intensity, mie_scattering_scale, rayleigh_scattering_scale, dust_storm)
[MessagePackObject]
public record struct WeatherParameters(
    [property: Key(0)]  float Cloudiness,
    [property: Key(1)]  float Precipitation,
    [property: Key(2)]  float PrecipitationDeposits,
    [property: Key(3)]  float WindIntensity,
    [property: Key(4)]  float SunAzimuthAngle,
    [property: Key(5)]  float SunAltitudeAngle,
    [property: Key(6)]  float FogDensity,
    [property: Key(7)]  float FogDistance,
    [property: Key(8)]  float FogFalloff,
    [property: Key(9)]  float Wetness,
    [property: Key(10)] float ScatteringIntensity,
    [property: Key(11)] float MieScatteringScale,
    [property: Key(12)] float RayleighScatteringScale,
    [property: Key(13)] float DustStorm);

// Source: carla/rpc/EnvironmentObject.h
// MSGPACK_DEFINE_ARRAY(transform, bounding_box, id, name, type)
[MessagePackObject]
public record struct EnvironmentObject(
    [property: Key(0)] Transform Transform,
    [property: Key(1)] BoundingBox BoundingBox,
    [property: Key(2)] ulong Id,
    [property: Key(3)] string Name,
    [property: Key(4)] CityObjectLabel Type);

// Source: carla/rpc/OpendriveGenerationParameters.h
// MSGPACK_DEFINE_ARRAY(vertex_distance, max_road_length, wall_height, additional_width,
//   smooth_junctions, enable_mesh_visibility, enable_pedestrian_navigation)
// NOTE: vertex_width_resolution and simplification_percentage are NOT in MSGPACK_DEFINE_ARRAY.
[MessagePackObject]
public record struct OpendriveGenerationParameters(
    [property: Key(0)] double VertexDistance,
    [property: Key(1)] double MaxRoadLength,
    [property: Key(2)] double WallHeight,
    [property: Key(3)] double AdditionalWidth,
    [property: Key(4)] bool SmoothJunctions,
    [property: Key(5)] bool EnableMeshVisibility,
    [property: Key(6)] bool EnablePedestrianNavigation);
