// §8 — Public CarlaClient facade. All 87 RPC methods from Client.h.
// Default timeout: 5000ms (from LibCarla/source/carla/client/detail/Client.cpp).
// Default port: 2000.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using CarlaNet.Transport.MsgPackRpc;
using CarlaNet.Transport.Streaming;
using CarlaNet.Transport.TrafficManager;
using Microsoft.Extensions.Logging;

namespace CarlaNet.Transport;

// World-tick timestamp emitted by CarlaClient.OnTick. Matches upstream's
// carla.Timestamp: frame, elapsed_seconds (== SensorHeader.Timestamp),
// delta_seconds (== EpisodeState delta), platform_timestamp (== EpisodeState
// platform_ts; wall-clock seconds since simulation start).
public sealed record TickTimestamp(
    ulong Frame,
    double ElapsedSeconds,
    double DeltaSeconds,
    double PlatformTimestamp);

// Cached snapshot of one actor from the world observer stream (§10.14).
// Parsed inline without CarlaNet.Sensors dependency.
public sealed class ActorSnapshot
{
    public ActorId Id { get; init; }
    public ActorState State { get; init; }
    public Transform Transform { get; init; }
    public Vector3D Velocity { get; init; }
    public Vector3D AngularVelocity { get; init; }
    public Vector3D Acceleration { get; init; }
    // Raw 54-byte TypeDependentState union — parse with GetVehicleData() etc.
    internal byte[] TypeDependentState { get; init; } = [];
}

public sealed class CarlaClient : IAsyncDisposable
{
    private readonly MsgPackRpcClient _rpc;
    private readonly string _host;
    private readonly ILogger<CarlaClient>? _log;
    private readonly List<SensorStream> _streams = [];
    private readonly ConcurrentDictionary<ActorId, ActorSnapshot> _actorCache = new();
    private IDisposable? _worldObserver;

    public CarlaClient(string host, int port = 2000, TimeSpan? timeout = null, ILogger<CarlaClient>? logger = null)
    {
        _host = host;
        _log = logger;
        _rpc = new MsgPackRpcClient(host, port, timeout ?? TimeSpan.FromMilliseconds(5000), logger);
    }

    /// Update the per-call RPC timeout. Affects subsequent calls only.
    public void SetTimeout(TimeSpan timeout) => _rpc.SetTimeout(timeout);

    // ── §9.3 world.on_tick — fired once per world-observer frame ─────────────
    // Subscribers receive a TickTimestamp built from the SensorFrame header
    // (Frame, Timestamp) and the parsed EpisodeState header (DeltaSeconds,
    // PlatformTimestamp). Multi-threaded — handlers should be cheap or marshal
    // to their own thread / queue.
    public event Action<TickTimestamp>? OnTick;

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

    // ── Convenience: spawn a vehicle by blueprint id at a spawn-point index ──────
    // Combines GetActorDefinitionsAsync + GetMapInfoAsync + spawn_actor in one call.
    public async Task<Actor> SpawnVehicleAsync(string blueprintId = "vehicle.lincoln.mkz", int spawnIndex = 0)
    {
        var defs      = await GetActorDefinitionsAsync().ConfigureAwait(false);
        var mapInfo   = await GetMapInfoAsync().ConfigureAwait(false);
        var def       = defs.First(d => d.Id == blueprintId);
        var spawnPt   = mapInfo.RecommendedSpawnPoints[spawnIndex];
        var attrs     = def.Attributes
            .Select(a => new ActorAttributeValue(a.Id, a.Type, a.Value))
            .ToList();
        var desc = new ActorDescription(def.Uid, def.Id, attrs);
        return await _rpc.CallAsync<Actor>("spawn_actor", desc, spawnPt).ConfigureAwait(false);
    }

    // ── Convenience: spawn an RGB camera sensor attached to a parent actor ────────
    // Returns the camera Actor — actor.StreamToken gives the subscription token.
    // Default offset matches manual_control.py camera[0]: x=-2*bound_x, z=2*bound_z, pitch=+8
    public async Task<Actor> SpawnCameraAsync(
        ActorId parentId,
        int width = 1280, int height = 720,
        float boomX = -5.9f, float boomZ = 2.5f, float pitchDeg = 8f)
    {
        var defs = await GetActorDefinitionsAsync().ConfigureAwait(false);
        var def  = defs.First(d => d.Id == "sensor.camera.rgb");
        var attrs = def.Attributes
            .Select(a => a.Id switch
            {
                "image_size_x" => new ActorAttributeValue(a.Id, a.Type, width.ToString()),
                "image_size_y" => new ActorAttributeValue(a.Id, a.Type, height.ToString()),
                _              => new ActorAttributeValue(a.Id, a.Type, a.Value)
            }).ToList();
        var desc   = new ActorDescription(def.Uid, def.Id, attrs);
        var offset = new Transform(new Location(boomX, 0f, boomZ), new Rotation(pitchDeg, 0f, 0f));
        return await _rpc.CallAsync<Actor>(
            "spawn_actor_with_parent", desc, offset, parentId, AttachmentType.SpringArmGhost
        ).ConfigureAwait(false);
    }

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

    public async Task<VehicleLightStateFlags> GetVehicleLightStateAsync(ActorId id)
    {
        var s = await _rpc.CallAsync<VehicleLightState>("get_vehicle_light_state", id).ConfigureAwait(false);
        return s.Flags;
    }

    public Task SetVehicleLightStateAsync(ActorId id, VehicleLightStateFlags state)
        => _rpc.CallVoidAsync("set_vehicle_light_state", id, new VehicleLightState(state));

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
    // actor.StreamToken is the raw 24-byte vector<unsigned char> from Actor.h.
    // Use actor.StreamToken.Length == 24 to confirm the actor has a stream.

    public IDisposable SubscribeToStream(byte[] rawTokenBytes, Action<SensorFrame> callback)
    {
        var token = StreamToken.Parse(rawTokenBytes, _host);
        var stream = new SensorStream(token, callback);
        lock (_streams) { _streams.Add(stream); }
        return new StreamDisposable(stream, () => { lock (_streams) { _streams.Remove(stream); } });
    }

    // GBuffer token retrieved via get_gbuffer_token RPC, returns raw 24-byte token bytes.
    public async Task<IDisposable> SubscribeToGBufferAsync(
        ActorId actorId, uint gBufferId, Action<SensorFrame> callback)
    {
        var rawBytes = await _rpc.CallAsync<byte[]>("get_gbuffer_token", actorId, gBufferId)
            .ConfigureAwait(false);
        return SubscribeToStream(rawBytes, callback);
    }

    public Task EnableForRosAsync(byte[] rawTokenBytes)
        => _rpc.CallVoidAsync("enable_sensor_for_ros", rawTokenBytes);

    public Task DisableForRosAsync(byte[] rawTokenBytes)
        => _rpc.CallVoidAsync("disable_sensor_for_ros", rawTokenBytes);

    public Task<bool> IsEnabledForRosAsync(byte[] rawTokenBytes)
        => _rpc.CallAsync<bool>("is_sensor_enabled_for_ros", rawTokenBytes);

    // ── §8.16 Debug and Batch ─────────────────────────────────────────────────

    public Task DrawDebugShapeAsync(DebugShape shape)
        => _rpc.CallVoidAsync("draw_debug_shape", shape);

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

    // ── World Observer (§10.14) ───────────────────────────────────────────────
    // Subscribes to the episode state stream (FWorldObserver) and caches all
    // actor snapshots.  Call once after construction; required for GetActorTransform etc.

    public async Task StartWorldObserverAsync()
    {
        var info = await GetEpisodeInfoAsync().ConfigureAwait(false);
        _worldObserver = SubscribeToStream(info.Token.Data, OnWorldObserverFrame);
    }

    private void OnWorldObserverFrame(SensorFrame frame)
    {
        try
        {
            double platformTs = 0;
            float deltaS = 0;
            ParseEpisodeState(frame.Payload.Span, out platformTs, out deltaS);
            // Emit a tick event so Python world.on_tick(callback) can fire.
            var handlers = OnTick;
            if (handlers is not null)
            {
                var ts = new TickTimestamp(
                    frame.Header.Frame,
                    frame.Header.Timestamp,
                    deltaS,
                    platformTs);
                try { handlers(ts); }
                catch (Exception cbEx) { _log?.LogWarning(cbEx, "OnTick handler threw"); }
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "World observer parse error"); }
    }

    private void ParseEpisodeState(ReadOnlySpan<byte> payload, out double platformTimestamp, out float deltaSeconds)
    {
        platformTimestamp = 0;
        deltaSeconds = 0;
        // Header layout (36 bytes): episode_id(8) platform_ts(8) delta_s(4) map_origin(12) state(1) pad(3)
        if (payload.Length < 36) return;
        platformTimestamp = BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));
        deltaSeconds = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(payload[16..]));
        const int HeaderSize = 36;
        const int ActorSize  = 119;
        var actors = payload[HeaderSize..];
        int count  = actors.Length / ActorSize;
        for (int i = 0; i < count; i++)
        {
            var a  = actors.Slice(i * ActorSize, ActorSize);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(a);
            var st = (ActorState)a[4];
            // Transform: Location(12) + Rotation(12) starting at offset 5
            float lx  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[5..]));
            float ly  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[9..]));
            float lz  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[13..]));
            float rp  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[17..]));
            float ry  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[21..]));
            float rr  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[25..]));
            float vx  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[29..]));
            float vy  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[33..]));
            float vz  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[37..]));
            float avx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[41..]));
            float avy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[45..]));
            float avz = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[49..]));
            float ax  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[53..]));
            float ay  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[57..]));
            float az  = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[61..]));
            _actorCache[id] = new ActorSnapshot
            {
                Id = id, State = st,
                Transform       = new Transform(new Location(lx, ly, lz), new Rotation(rp, ry, rr)),
                Velocity        = new Vector3D(vx, vy, vz),
                AngularVelocity = new Vector3D(avx, avy, avz),
                Acceleration    = new Vector3D(ax, ay, az),
                TypeDependentState = a[65..119].ToArray()
            };
        }
    }

    // ── Actor state queries (sourced from world observer cache) ───────────────
    // Returns default(T) if actor not yet observed. Call StartWorldObserverAsync()
    // once before using these — they read from the in-memory cache.

    public Transform       GetActorTransform      (ActorId id) => _actorCache.TryGetValue(id, out var s) ? s.Transform       : default;
    public Vector3D        GetActorVelocity       (ActorId id) => _actorCache.TryGetValue(id, out var s) ? s.Velocity        : default;
    public Vector3D        GetActorAngularVelocity(ActorId id) => _actorCache.TryGetValue(id, out var s) ? s.AngularVelocity : default;
    public Vector3D        GetActorAcceleration   (ActorId id) => _actorCache.TryGetValue(id, out var s) ? s.Acceleration    : default;
    public ActorSnapshot?  GetActorSnapshot       (ActorId id) => _actorCache.TryGetValue(id, out var s) ? s : null;

    /// All actor IDs currently in the world observer cache.
    public IReadOnlyList<ActorId> GetCachedActorIds() => [.. _actorCache.Keys];

    // Decode VehicleControl from the cached TypeDependentState union.
    // VehicleData layout (pack=1): throttle(f) steer(f) brake(f) hand_brake(bool)
    //   reverse(bool) manual_gear_shift(bool) gear(i32) = 19 bytes → PackedVehicleControl
    public VehicleControl GetVehicleControl(ActorId id)
    {
        if (!_actorCache.TryGetValue(id, out var snap) || snap.TypeDependentState.Length < 19)
            return default;
        var d = snap.TypeDependentState.AsSpan();
        return new VehicleControl(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(d)),       // throttle
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(d[4..])),  // steer
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(d[8..])),  // brake
            d[12] != 0, d[13] != 0, d[14] != 0,                                              // hand_brake, reverse, manual_gear_shift
            BinaryPrimitives.ReadInt32LittleEndian(d[15..]));                                 // gear
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _worldObserver?.Dispose();
        lock (_streams) { foreach (var s in _streams) s.Dispose(); _streams.Clear(); }
        await _rpc.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class StreamDisposable(SensorStream stream, Action onDispose) : IDisposable
    {
        public void Dispose() { stream.Dispose(); onDispose(); }
    }
}
