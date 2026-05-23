// §8 — Public CarlaClient facade. All 87 RPC methods from Client.h.
// Default timeout: 5000ms (from LibCarla/source/carla/client/detail/Client.cpp).
// Default port: 2000.
using System.Reflection;
using CarlaNet.Transport.MsgPackRpc;
using CarlaNet.Transport.Streaming;
using CarlaNet.Transport.TrafficManager;
using Microsoft.Extensions.Logging;

namespace CarlaNet.Transport;

public sealed class CarlaClient : IAsyncDisposable
{
    private readonly MsgPackRpcClient _rpc;
    private readonly string _host;
    private readonly ILogger<CarlaClient>? _log;
    private readonly List<SensorStream> _streams = [];

    public CarlaClient(string host, int port = 2000, TimeSpan? timeout = null, ILogger<CarlaClient>? logger = null)
    {
        _host = host;
        _log = logger;
        _rpc = new MsgPackRpcClient(host, port, timeout ?? TimeSpan.FromMilliseconds(5000), logger);
    }

    // ── §8.1 Session / Traffic Manager ───────────────────────────────────────

    public string GetClientVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public Task<string> GetServerVersionAsync()
        => _rpc.CallAsync<string>("version");

    public Task<bool> IsTrafficManagerRunningAsync(ushort port)
        => _rpc.CallAsync<bool>("is_traffic_manager_running", port);

    public Task<(string Host, ushort Port)> GetTrafficManagerRunningAsync(ushort port)
        => _rpc.CallAsync<(string, ushort)>("get_traffic_manager_running", port);

    public Task AddTrafficManagerRunningAsync(string host, ushort port)
        => _rpc.CallVoidAsync("add_traffic_manager_running", (host, port));

    public Task DestroyTrafficManagerAsync(ushort port)
        => _rpc.CallVoidAsync("destroy_traffic_manager", port);

    public TrafficManagerClient GetTrafficManager(ushort port = 8000)
        => new(_host, port);

    // ── §8.2 Episode Management ───────────────────────────────────────────────

    public Task LoadEpisodeAsync(string mapName, bool resetSettings = true, MapLayer layer = MapLayer.All)
        => _rpc.CallVoidAsync("load_new_episode", mapName, resetSettings, layer);

    public Task LoadLevelLayerAsync(MapLayer layer)
        => _rpc.CallVoidAsync("load_map_layer", layer);

    public Task UnloadLevelLayerAsync(MapLayer layer)
        => _rpc.CallVoidAsync("unload_map_layer", layer);

    public Task<EpisodeInfo> GetEpisodeInfoAsync()
        => _rpc.CallAsync<EpisodeInfo>("get_episode_info");

    public Task<EpisodeSettings> GetEpisodeSettingsAsync()
        => _rpc.CallAsync<EpisodeSettings>("get_episode_settings");

    public Task<ulong> SetEpisodeSettingsAsync(EpisodeSettings settings)
        => _rpc.CallAsync<ulong>("set_episode_settings", settings);

    public Task<ulong> SendTickCueAsync()
        => _rpc.CallAsync<ulong>("tick_cue");

    // ── §8.3 Map and World Data ───────────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetAvailableMapsAsync()
        => _rpc.CallAsync<IReadOnlyList<string>>("get_available_maps");

    public Task<MapInfo> GetMapInfoAsync()
        => _rpc.CallAsync<MapInfo>("get_map_info");

    public Task<string> GetMapDataAsync()
        => _rpc.CallAsync<string>("get_map_data");

    public Task<byte[]> GetNavigationMeshAsync()
        => _rpc.CallAsync<byte[]>("get_navigation_mesh");

    public Task<IReadOnlyList<ActorDefinition>> GetActorDefinitionsAsync()
        => _rpc.CallAsync<IReadOnlyList<ActorDefinition>>("get_actor_definitions");

    public Task<IReadOnlyList<BoundingBox>> GetLevelBoundingBoxesAsync(byte queriedTag)
        => _rpc.CallAsync<IReadOnlyList<BoundingBox>>("get_all_level_BBs", queriedTag);

    public Task<IReadOnlyList<EnvironmentObject>> GetEnvironmentObjectsAsync(byte queriedTag)
        => _rpc.CallAsync<IReadOnlyList<EnvironmentObject>>("get_environment_objects", queriedTag);

    public Task EnableEnvironmentObjectsAsync(IReadOnlyList<ulong> ids, bool enable)
        => _rpc.CallVoidAsync("enable_environment_objects", ids, enable);

    public Task CopyOpenDriveToServerAsync(string openDrive, OpendriveGenerationParameters p)
        => _rpc.CallVoidAsync("copy_opendrive_to_file", openDrive, p);

    public Task<IReadOnlyList<string>> GetNamesOfAllObjectsAsync()
        => _rpc.CallAsync<IReadOnlyList<string>>("get_names_of_all_objects");

    // ── §8.4 File Management ──────────────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetRequiredFilesAsync(string folder = "", bool download = true)
        => _rpc.CallAsync<IReadOnlyList<string>>("get_required_files", folder, download);

    public Task RequestFileAsync(string name)
        => _rpc.CallVoidAsync("request_file", name);

    public Task<byte[]> GetCacheFileAsync(string name, bool requestOtherwise = true)
        => _rpc.CallAsync<byte[]>("get_cache_file", name, requestOtherwise);

    // ── §8.5 Material and Texture ─────────────────────────────────────────────

    public Task ApplyTextureToActorAsync(ActorId id, MaterialParameter param, TextureColor texture)
        => _rpc.CallVoidAsync("apply_texture_to_actor", id, param, texture);

    public Task ApplyFloatTextureToActorAsync(ActorId id, MaterialParameter param, TextureFloatColor texture)
        => _rpc.CallVoidAsync("apply_float_color_texture_to_objects", id, param, texture);

    public Task ApplyColorTextureToObjectsAsync(IReadOnlyList<string> names, MaterialParameter param, TextureColor texture)
        => _rpc.CallVoidAsync("apply_color_texture_to_objects", names, param, texture);

    public Task ApplyFloatColorTextureToObjectsAsync(IReadOnlyList<string> names, MaterialParameter param, TextureFloatColor texture)
        => _rpc.CallVoidAsync("apply_float_color_texture_to_objects", names, param, texture);

    // ── §8.6 Actor Queries ────────────────────────────────────────────────────

    public Task<Actor> GetSpectatorAsync()
        => _rpc.CallAsync<Actor>("get_spectator");

    public Task<IReadOnlyList<Actor>> GetActorsByIdAsync(IReadOnlyList<ActorId> ids)
        => _rpc.CallAsync<IReadOnlyList<Actor>>("get_actors_by_id", ids);

    public Task<string> GetActorNameAsync(ActorId id)
        => _rpc.CallAsync<string>("get_actor_name", id);

    public Task<string> GetActorClassNameAsync(ActorId id)
        => _rpc.CallAsync<string>("get_actor_class_name", id);

    // ── §8.7 Actor Lifecycle ──────────────────────────────────────────────────

    public Task<Actor> SpawnActorAsync(ActorDescription desc, Transform transform)
        => _rpc.CallAsync<Actor>("spawn_actor", desc, transform);

    public Task<Actor> SpawnActorWithParentAsync(ActorDescription desc, Transform transform,
        ActorId parentId, AttachmentType attachType)
        => _rpc.CallAsync<Actor>("spawn_actor_with_parent", desc, transform, parentId, attachType);

    public Task<bool> DestroyActorAsync(ActorId id)
        => _rpc.CallAsync<bool>("destroy_actor", id);

    // ── §8.8 Actor Transform and Physics ──────────────────────────────────────

    public Task SetActorLocationAsync(ActorId id, Location location)
        => _rpc.CallVoidAsync("set_actor_location", id, location);

    public Task SetActorTransformAsync(ActorId id, Transform transform)
        => _rpc.CallVoidAsync("set_actor_transform", id, transform);

    public Task SetActorTargetVelocityAsync(ActorId id, Vector3D velocity)
        => _rpc.CallVoidAsync("set_actor_target_velocity", id, velocity);

    public Task SetActorTargetAngularVelocityAsync(ActorId id, Vector3D angularVelocity)
        => _rpc.CallVoidAsync("set_actor_target_angular_velocity", id, angularVelocity);

    public Task EnableActorConstantVelocityAsync(ActorId id, Vector3D velocity)
        => _rpc.CallVoidAsync("enable_actor_constant_velocity", id, velocity);

    public Task DisableActorConstantVelocityAsync(ActorId id)
        => _rpc.CallVoidAsync("disable_actor_constant_velocity", id);

    public Task AddActorImpulseAsync(ActorId id, Vector3D impulse)
        => _rpc.CallVoidAsync("add_actor_impulse", id, impulse);

    public Task AddActorImpulseAtLocationAsync(ActorId id, Vector3D impulse, Vector3D location)
        => _rpc.CallVoidAsync("add_actor_impulse_at_location", id, impulse, location);

    public Task AddActorForceAsync(ActorId id, Vector3D force)
        => _rpc.CallVoidAsync("add_actor_force", id, force);

    public Task AddActorForceAtLocationAsync(ActorId id, Vector3D force, Vector3D location)
        => _rpc.CallVoidAsync("add_actor_force_at_location", id, force, location);

    public Task AddActorAngularImpulseAsync(ActorId id, Vector3D impulse)
        => _rpc.CallVoidAsync("add_actor_angular_impulse", id, impulse);

    public Task AddActorTorqueAsync(ActorId id, Vector3D torque)
        => _rpc.CallVoidAsync("add_actor_torque", id, torque);

    public Task SetActorSimulatePhysicsAsync(ActorId id, bool enabled)
        => _rpc.CallVoidAsync("set_actor_simulate_physics", id, enabled);

    public Task SetActorCollisionsAsync(ActorId id, bool enabled)
        => _rpc.CallVoidAsync("set_actor_collisions", id, enabled);

    public Task SetActorDeadAsync(ActorId id)
        => _rpc.CallVoidAsync("set_actor_dead", id);

    public Task SetActorEnableGravityAsync(ActorId id, bool enabled)
        => _rpc.CallVoidAsync("set_actor_enable_gravity", id, enabled);

    // ── §8.9 Vehicle Control ──────────────────────────────────────────────────

    public Task ApplyControlToVehicleAsync(ActorId id, VehicleControl control)
        => _rpc.CallVoidAsync("apply_control_to_vehicle", id, control);

    public Task ApplyAckermannControlToVehicleAsync(ActorId id, VehicleAckermannControl control)
        => _rpc.CallVoidAsync("apply_ackermann_control_to_vehicle", id, control);

    public Task<AckermannControllerSettings> GetAckermannControllerSettingsAsync(ActorId id)
        => _rpc.CallAsync<AckermannControllerSettings>("get_ackermann_controller_settings", id);

    public Task ApplyAckermannControllerSettingsAsync(ActorId id, AckermannControllerSettings settings)
        => _rpc.CallVoidAsync("apply_ackermann_controller_settings", id, settings);

    public Task SetActorAutopilotAsync(ActorId id, bool enabled)
        => _rpc.CallVoidAsync("set_actor_autopilot", id, enabled);

    public Task ShowVehicleDebugTelemetryAsync(ActorId id, bool enabled)
        => _rpc.CallVoidAsync("show_vehicle_debug_telemetry", id, enabled);

    public Task EnableCarSimAsync(ActorId id, string simfilePath)
        => _rpc.CallVoidAsync("enable_carsim", id, simfilePath);

    public Task UseCarSimRoadAsync(ActorId id, bool enabled)
        => _rpc.CallVoidAsync("use_carsim_road", id, enabled);

    public Task EnableChronoPhysicsAsync(ActorId id, ulong maxSubsteps, float maxDt,
        string vehicleJson, string powertrainJson, string tireJson, string baseJsonPath)
        => _rpc.CallVoidAsync("enable_chrono_physics", id, maxSubsteps, maxDt,
            vehicleJson, powertrainJson, tireJson, baseJsonPath);

    // ── §8.10 Vehicle Physics ─────────────────────────────────────────────────

    public Task<VehiclePhysicsControl> GetVehiclePhysicsControlAsync(ActorId id)
        => _rpc.CallAsync<VehiclePhysicsControl>("get_vehicle_physics_control", id);

    public Task ApplyPhysicsControlToVehicleAsync(ActorId id, VehiclePhysicsControl control)
        => _rpc.CallVoidAsync("apply_physics_control", id, control);

    public Task<IReadOnlyList<Transform>> GetVehicleBoneWorldTransformsAsync(ActorId id)
        => _rpc.CallAsync<IReadOnlyList<Transform>>("get_vehicle_bone_world_transforms", id);

    public Task<VehicleLightStateFlags> GetVehicleLightStateAsync(ActorId id)
        => _rpc.CallAsync<VehicleLightStateFlags>("get_vehicle_light_state", id);

    public Task SetVehicleLightStateAsync(ActorId id, VehicleLightStateFlags state)
        => _rpc.CallVoidAsync("set_vehicle_light_state", id, state);

    public Task OpenVehicleDoorAsync(ActorId id, VehicleDoor door)
        => _rpc.CallVoidAsync("open_vehicle_door", id, door);

    public Task CloseVehicleDoorAsync(ActorId id, VehicleDoor door)
        => _rpc.CallVoidAsync("close_vehicle_door", id, door);

    public Task<IReadOnlyList<(ActorId, VehicleLightStateFlags)>> GetVehiclesLightStatesAsync()
        => _rpc.CallAsync<IReadOnlyList<(ActorId, VehicleLightStateFlags)>>("get_vehicles_light_states");

    public Task SetWheelSteerDirectionAsync(ActorId id, VehicleWheelLocation wheel, float angleDeg)
        => _rpc.CallVoidAsync("set_wheel_steer_direction", id, wheel, angleDeg);

    public Task<float> GetWheelSteerAngleAsync(ActorId id, VehicleWheelLocation wheel)
        => _rpc.CallAsync<float>("get_wheel_steer_angle", id, wheel);

    public Task<float> GetVehicleSpeedLimitAsync(ActorId id)
        => _rpc.CallAsync<float>("get_vehicle_speed_limit", id);

    // ── §8.11 Walker Control ──────────────────────────────────────────────────

    public Task ApplyControlToWalkerAsync(ActorId id, WalkerControl control)
        => _rpc.CallVoidAsync("apply_control_to_walker", id, control);

    public Task<WalkerBoneControlOut> GetBonesTransformAsync(ActorId id)
        => _rpc.CallAsync<WalkerBoneControlOut>("get_bones_transform", id);

    public Task SetBonesTransformAsync(ActorId id, WalkerBoneControlIn bones)
        => _rpc.CallVoidAsync("set_bones_transform", id, bones);

    public Task BlendPoseAsync(ActorId id, float blend)
        => _rpc.CallVoidAsync("blend_pose", id, blend);

    public Task GetPoseFromAnimationAsync(ActorId id)
        => _rpc.CallVoidAsync("get_pose_from_animation", id);

    // ── §8.12 Traffic Lights ──────────────────────────────────────────────────

    public Task SetTrafficLightStateAsync(ActorId id, TrafficLightState state)
        => _rpc.CallVoidAsync("set_traffic_light_state", id, state);

    public Task SetTrafficLightGreenTimeAsync(ActorId id, float greenTime)
        => _rpc.CallVoidAsync("set_traffic_light_green_time", id, greenTime);

    public Task SetTrafficLightYellowTimeAsync(ActorId id, float yellowTime)
        => _rpc.CallVoidAsync("set_traffic_light_yellow_time", id, yellowTime);

    public Task SetTrafficLightRedTimeAsync(ActorId id, float redTime)
        => _rpc.CallVoidAsync("set_traffic_light_red_time", id, redTime);

    public Task FreezeTrafficLightAsync(ActorId id, bool freeze)
        => _rpc.CallVoidAsync("freeze_traffic_light", id, freeze);

    public Task ResetTrafficLightGroupAsync(ActorId id)
        => _rpc.CallVoidAsync("reset_traffic_light_group", id);

    public Task ResetAllTrafficLightsAsync()
        => _rpc.CallVoidAsync("reset_all_traffic_lights");

    public Task FreezeAllTrafficLightsAsync(bool frozen)
        => _rpc.CallVoidAsync("freeze_all_traffic_lights", frozen);

    public Task<IReadOnlyList<BoundingBox>> GetLightBoxesAsync(ActorId id)
        => _rpc.CallAsync<IReadOnlyList<BoundingBox>>("get_light_boxes", id);

    public Task<IReadOnlyList<ActorId>> GetGroupTrafficLightsAsync(ActorId id)
        => _rpc.CallAsync<IReadOnlyList<ActorId>>("get_group_traffic_lights", id);

    // ── §8.13 Weather and Scene Lighting ──────────────────────────────────────

    public Task<WeatherParameters> GetWeatherParametersAsync()
        => _rpc.CallAsync<WeatherParameters>("get_weather_parameters");

    public Task SetWeatherParametersAsync(WeatherParameters weather)
        => _rpc.CallVoidAsync("set_weather_parameters", weather);

    public Task<bool> IsWeatherEnabledAsync()
        => _rpc.CallAsync<bool>("is_weather_enabled");

    public Task<IReadOnlyList<LightState>> QueryLightsStateAsync()
        => _rpc.CallAsync<IReadOnlyList<LightState>>("query_lights_state");

    public Task UpdateServerLightsStateAsync(IReadOnlyList<LightState> lights, bool discardClient = false)
        => _rpc.CallVoidAsync("update_lights_state", lights, discardClient);

    public Task UpdateDayNightCycleAsync(bool active)
        => _rpc.CallVoidAsync("update_day_night_cycle", active);

    // ── §8.14 Recorder and Replayer ───────────────────────────────────────────

    public Task<string> StartRecorderAsync(string name, bool additionalData)
        => _rpc.CallAsync<string>("start_recorder", name, additionalData);

    public Task StopRecorderAsync()
        => _rpc.CallVoidAsync("stop_recorder");

    public Task<string> ShowRecorderFileInfoAsync(string name, bool showAll)
        => _rpc.CallAsync<string>("show_recorder_file_info", name, showAll);

    public Task<string> ShowRecorderCollisionsAsync(string name, char type1, char type2)
        => _rpc.CallAsync<string>("show_recorder_collisions", name, type1, type2);

    public Task<string> ShowRecorderActorsBlockedAsync(string name, double minTime, double minDistance)
        => _rpc.CallAsync<string>("show_recorder_actors_blocked", name, minTime, minDistance);

    public Task<string> ReplayFileAsync(string name, double start, double duration,
        uint followId, bool replaySensors)
        => _rpc.CallAsync<string>("replay_file", name, start, duration, followId, replaySensors);

    public Task SetReplayerTimeFactorAsync(double timeFactor)
        => _rpc.CallVoidAsync("set_replayer_time_factor", timeFactor);

    public Task SetReplayerIgnoreHeroAsync(bool ignoreHero)
        => _rpc.CallVoidAsync("set_replayer_ignore_hero", ignoreHero);

    public Task SetReplayerIgnoreSpectatorAsync(bool ignoreSpectator)
        => _rpc.CallVoidAsync("set_replayer_ignore_spectator", ignoreSpectator);

    public Task StopReplayerAsync(bool keepActors)
        => _rpc.CallVoidAsync("stop_replayer", keepActors);

    // ── §8.15 Sensor Subscription ─────────────────────────────────────────────

    public IDisposable SubscribeToStream(RawToken rawToken, Action<SensorFrame> callback)
    {
        var token = StreamToken.Parse(rawToken.Data, _host);
        var stream = new SensorStream(token, callback);
        lock (_streams) { _streams.Add(stream); }
        return new StreamDisposable(stream, () => { lock (_streams) { _streams.Remove(stream); } });
    }

    public Task EnableForRosAsync(RawToken rawToken)
        => _rpc.CallVoidAsync("enable_sensor_for_ros", rawToken);

    public Task DisableForRosAsync(RawToken rawToken)
        => _rpc.CallVoidAsync("disable_sensor_for_ros", rawToken);

    public Task<bool> IsEnabledForRosAsync(RawToken rawToken)
        => _rpc.CallAsync<bool>("is_sensor_enabled_for_ros", rawToken);

    // ── §8.16 Debug and Batch ─────────────────────────────────────────────────

    public Task ApplyBatchAsync(IReadOnlyList<Command> commands, bool doTickCue)
        => _rpc.CallVoidAsync("apply_batch", commands, doTickCue);

    public Task<IReadOnlyList<CommandResponse>> ApplyBatchSyncAsync(
        IReadOnlyList<Command> commands, bool doTickCue)
        => _rpc.CallAsync<IReadOnlyList<CommandResponse>>("apply_batch_sync", commands, doTickCue);

    // ── §8.17 Raycast and Queries ─────────────────────────────────────────────

    public Task<(bool Hit, LabelledPoint Point)> ProjectPointAsync(
        Location location, Vector3D direction, float searchDistance)
        => _rpc.CallAsync<(bool, LabelledPoint)>("project_point", location, direction, searchDistance);

    public Task<IReadOnlyList<LabelledPoint>> CastRayAsync(Location start, Location end)
        => _rpc.CallAsync<IReadOnlyList<LabelledPoint>>("cast_ray", start, end);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        lock (_streams) { foreach (var s in _streams) s.Dispose(); _streams.Clear(); }
        await _rpc.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class StreamDisposable(SensorStream stream, Action onDispose) : IDisposable
    {
        public void Dispose() { stream.Dispose(); onDispose(); }
    }
}
