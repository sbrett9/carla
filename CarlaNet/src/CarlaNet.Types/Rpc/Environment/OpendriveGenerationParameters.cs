// Source: carla/rpc/OpendriveGenerationParameters.h
// MSGPACK_DEFINE_ARRAY(vertex_distance, max_road_length, wall_height, additional_width,
//   smooth_junctions, enable_mesh_visibility, enable_pedestrian_navigation)
// NOTE: vertex_width_resolution and simplification_percentage are NOT in MSGPACK_DEFINE_ARRAY.
namespace CarlaNet.Types.Rpc.Environment;

[MessagePackObject]
public record struct OpendriveGenerationParameters(
    [property: Key(0)] double VertexDistance,
    [property: Key(1)] double MaxRoadLength,
    [property: Key(2)] double WallHeight,
    [property: Key(3)] double AdditionalWidth,
    [property: Key(4)] bool SmoothJunctions,
    [property: Key(5)] bool EnableMeshVisibility,
    [property: Key(6)] bool EnablePedestrianNavigation);
