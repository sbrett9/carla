// §8 — Public CarlaClient facade. All 87 RPC methods from Client.h.
// Default timeout: 5000ms (from LibCarla/source/carla/client/detail/Client.cpp).
// Default port: 2000.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
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
    // Raw 54-byte TypeDependentState union — parse with ParseVehicleState() etc.
    internal byte[] TypeDependentState { get; init; } = [];

    /// <summary>
    /// Decode the <c>VehicleData</c> branch of the type-dependent union
    /// (carla/sensor/data/ActorDynamicState.h, <c>#pragma pack(1)</c>). Only meaningful
    /// when this snapshot is a vehicle actor. Byte offsets within <see cref="TypeDependentState"/>:
    /// <list type="bullet">
    ///   <item><c>control</c> (PackedVehicleControl, 19 B): throttle/steer/brake f32 ×3 (12) +
    ///         hand_brake/reverse/manual_gear_shift bool ×3 (3) + gear i32 (4)</item>
    ///   <item><c>speed_limit</c> f32 @ 19</item>
    ///   <item><c>traffic_light_state</c> u8 @ 23</item>
    ///   <item><c>has_traffic_light</c> bool @ 24</item>
    /// </list>
    /// Returns Green / 0 / false when the union is too short (not-yet-populated snapshot).
    /// </summary>
    internal VehicleObservedState ParseVehicleState()
    {
        ReadOnlySpan<byte> s = TypeDependentState;
        if (s.Length < 25)
            return new VehicleObservedState(0f, TrafficLightState.Green, false);
        float speedLimit = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(s[19..]));
        var tlState = (TrafficLightState)s[23];
        bool atTrafficLight = s[24] != 0;
        return new VehicleObservedState(speedLimit, tlState, atTrafficLight);
    }

    /// <summary>
    /// Decode the type-dependent union as a traffic light's state. Byte offsets within
    /// <see cref="TypeDependentState"/>, matching <c>detail::TrafficLightData</c>, which is
    /// declared under <c>#pragma pack(1)</c> and so has no padding:
    /// <list type="bullet">
    ///   <item><c>sign_id</c> char[32] @ 0 — the OpenDRIVE signal id, NUL-terminated</item>
    ///   <item><c>green_time</c> / <c>yellow_time</c> / <c>red_time</c> / <c>elapsed_time</c>
    ///         f32 @ 32, 36, 40, 44</item>
    ///   <item><c>pole_index</c> u32 @ 48</item>
    ///   <item><c>time_is_frozen</c> bool @ 52</item>
    ///   <item><c>state</c> u8 @ 53</item>
    /// </list>
    /// Returns null when the union is too short, or when the signal id is empty — a light spawned
    /// as a plain actor rather than from OpenDRIVE writes no id, and cannot be matched to the map.
    /// </summary>
    internal TrafficLightObservedState? ParseTrafficLightState()
    {
        ReadOnlySpan<byte> s = TypeDependentState;
        if (s.Length < 54) return null;
        ReadOnlySpan<byte> idBytes = s[..32];
        int end = idBytes.IndexOf((byte)0);
        if (end < 0) end = idBytes.Length;
        if (end == 0) return null;
        string signId = System.Text.Encoding.ASCII.GetString(idBytes[..end]);
        return new TrafficLightObservedState(
            signId,
            (TrafficLightState)s[53],
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[32..])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[36..])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[40..])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s[44..])),
            BinaryPrimitives.ReadUInt32LittleEndian(s[48..]),
            s[52] != 0);
    }
}

/// <summary>
/// A traffic light's state as the world observer reports it, keyed by the OpenDRIVE signal id the
/// light was generated from. Read for every light every tick, so a vehicle can be told the state of
/// the signal governing its approach from any distance, rather than only while it overlaps that
/// signal's stop-line trigger box.
/// </summary>
public readonly record struct TrafficLightObservedState(
    string SignId,
    TrafficLightState State,
    float GreenTime,
    float YellowTime,
    float RedTime,
    float ElapsedTime,
    uint PoleIndex,
    bool TimeIsFrozen);

/// <summary>
/// Per-vehicle dynamic state decoded from the world-observer snapshot's type-dependent
/// union: the server-reported speed limit (km/h, as CARLA's <c>Vehicle::GetSpeedLimit</c>
/// returns it), the current traffic-light state affecting the vehicle, and whether it is
/// currently at a traffic light. The server fills these each tick from the vehicle's AI
/// controller (WorldObserver.cpp); a dormant vehicle reports Green / its stored limit / false.
/// </summary>
public readonly record struct VehicleObservedState(
    float SpeedLimit,
    TrafficLightState TrafficLightState,
    bool AtTrafficLight);

public sealed class CarlaClient : IAsyncDisposable
{
    private readonly MsgPackRpcClient _rpc;
    private readonly string _host;
    private readonly ILogger<CarlaClient>? _log;
    private readonly List<SensorStream> _streams = [];
    private readonly ConcurrentDictionary<ActorId, ActorSnapshot> _actorCache = new();
    // Ids seen in the current world-observer snapshot; reused each frame to evict destroyed actors.
    // Touched only on the single world-observer stream-reader thread (see OnWorldObserverFrame).
    private readonly HashSet<ActorId> _observedIds = new();
    private IDisposable? _worldObserver;

    // Solar / time-of-day state from the latest world-observer snapshot (§10.14 extended header):
    // [solar_time, year, month, day, time_zone, lat, lon, elevation_deg, azimuth_deg, advancing, rate].
    // Updated lock-free each tick in ParseEpisodeState so the recorder pairs frames with the sun with
    // no RPC and no polling; empty until the first snapshot arrives.
    private volatile double[] _solar = System.Array.Empty<double>();

    // ── Staging-fade state (see SetActorFadeAsync / GetActorOpacity / IsActorEstablished) ──
    // set_actor_fade writes straight to render state server-side and has no read-back, so the client
    // that issued it is the only holder of the value. Recording it here is what lets consumers in this
    // process — the truth telemetry above all — know how visible a vehicle currently is and whether it
    // has finished arriving. Written from whichever thread calls SetActorFadeAsync and read from the
    // recorder's stream threads, hence the concurrent map.
    private readonly ConcurrentDictionary<ActorId, (double Opacity, bool Established)> _fade = new();

    /// <summary>
    /// Opacity at or above which a fading-in vehicle counts as having arrived. Staging traffic drives
    /// the fade from the fraction of the vehicle's footprint inside the scene interior, so it reaches
    /// exactly 1 only once the whole footprint is across; this tolerance absorbs that last sliver.
    /// </summary>
    public const double EstablishedOpacity = 0.99;

    // ── Telemetry-truth decoupling state (set by GenerateWorldFromOsmWithElevationAsync) ──
    // The digital-twin reports each vehicle's ELLIPSOIDAL-WGS84 altitude (hae) from the
    // bare-earth DTM (Cesium World Terrain), NOT from where the vehicle physically sits. With
    // --height-align area/origin the road mesh (and the cars on it) is shifted onto the photoreal
    // DSM by a constant offset; these fields let the telemetry path recover the bare-earth truth
    // without sampling Cesium live (see get_vehicle_telemetry in the Python shim).
    //
    // LastHeightAlignOffset = the constant road-height offset applied (photoreal-ground gap; 0 for
    //   "none"). The constant-offset modes also shift the collidable "ground" layer by this same
    //   constant (SetLayerOffsetAsync) so BOTH on- and off-road vehicles sit at DTM + offset;
    //   telemetry recovers bare-earth truth by subtracting it unconditionally: hae = physical - offset.
    // LastGroundDtmSamples = the per-road-point bare-earth DTM samples as GeoLocation
    //   (Latitude, Longitude, ellipsoidal Altitude), BEFORE the offset was added. Lat/lon are the
    //   exact input sample coordinates (not server-echoed); a nearest/IDW lookup at a vehicle's
    //   lat/lon yields the bare-earth ground there (telemetry diagnostic hae_dtm, and the hook for a
    //   future spatially-varying drape where the offset is no longer a single constant).
    /// <summary>Whether a road station dropped onto bare earth by the de-spike is lifted back
    /// onto the photoreal. Off: the photoreal is a surface model, so it cannot tell a road under
    /// a tree canopy from a road on a steep bank, and lifting the first puts the road on the
    /// canopy. A property rather than a parameter because the Python shim calls
    /// GenerateWorldFromOsmWithElevationAsync positionally, so adding one shifts every argument
    /// after it.</summary>
    public bool PhotorealClampEnabled { get; set; }

    /// <summary>How many smoothing passes to run along each road over the bare-earth heights,
    /// before any grade-separation lift is added. Zero leaves them as sampled.
    ///
    /// The drape already does this to its grid, two box-blur passes in DrapeTerrain.Despike, and
    /// the surface a road follows there is the blurred one. Reading bare earth point by point
    /// skips it, so a road inherits every wrinkle of a terrain model that knows nothing about
    /// roads. Measured on Arapahoe_I25 the worst bend between adjacent stations is 5.99 m as
    /// sampled and 1.46 m after one pass.
    ///
    /// The kernel is 1-2-1, which leaves any straight grade exactly where it was: it can only
    /// remove curvature, never tilt a hill. It runs on the terrain alone, so a deck keeps the full
    /// height its lift gives it.</summary>
    public int TerrainSmoothingPasses { get; set; } = 1;

    /// <summary>Where to write a record of every height the elevation pass used, or null for
    /// none.
    ///
    /// Without it the heights a build used cannot be recovered afterwards. The .xodr keeps only
    /// the fitted result, and re-sampling the terrain from outside does not reproduce what the
    /// build saw: the pipeline queries Cesium point by point, while anything offline has only a
    /// cached grid, and the two disagree by a quarter of a metre at the 95th percentile. Anything
    /// smaller than that cannot be told from the difference between the two, which is a poor
    /// footing for judging a bump.</summary>
    public string? ElevationDiagnosticsPath { get; set; }

    /// <summary>How far east the road network was slid to sit on the roadway in the imagery,
    /// in metres, as passed to netconvert. Heights are then read at the position the map data
    /// gave a road, not the position it was moved to.</summary>
    public double RoadOffsetEast { get; set; }

    /// <summary>How far north the road network was slid. See <see cref="RoadOffsetEast"/>.</summary>
    public double RoadOffsetNorth { get; set; }

    public double LastHeightAlignOffset { get; private set; }
    public IReadOnlyList<GeoLocation> LastGroundDtmSamples { get; private set; } = [];

    // ── Per-point drape state (set by GenerateWorldFromOsmWithElevationAsync in "drape" mode) ──
    // When LastDrapeActive, telemetry recovers bare-earth HAE per-vehicle from a PER-CELL offset
    // field (DrapedZ − DTM) over the OSM sandbox, instead of the single LastHeightAlignOffset.
    // The offset grid is exposed as a bulk float32 LE byte[] so the shim bulk-copies it into numpy
    // (np.frombuffer) and does an O(1) bilinear lookup by the vehicle's local X/Y — no per-element
    // pythonnet marshalling, no live Cesium sampling.
    public bool LastDrapeActive { get; private set; }
    public int LastDrapeNumCols { get; private set; }
    public int LastDrapeNumRows { get; private set; }
    public double LastDrapeMinX { get; private set; }
    public double LastDrapeMinY { get; private set; }
    public double LastDrapeCellSize { get; private set; }
    public byte[] LastDrapedOffsetBytes { get; private set; } = [];   // row-major float32 LE, DrapedZ-DTM (m)
    public byte[] LastDrapedDtmBytes { get; private set; } = [];      // row-major float32 LE, bare-earth DTM (ellipsoidal m)

    // Cached parse of the drape grids for point sampling (re-parsed only when the underlying bytes change).
    private float[]? _drapeDtmGrid, _drapeOffGrid;
    private byte[]? _drapeDtmRef, _drapeOffRef;

    /// <summary>
    /// Ground-surface elevation (ellipsoidal metres, = draped DTM + offset) under CARLA-local (x, y),
    /// sampled bilinearly from the drape terrain grid — a non-physics lookup, independent of Cesium
    /// streaming/LOD, unlike a downward raycast. Returns null when there is no active drape or (x, y)
    /// is outside the drape grid (e.g. beyond the OSM sandbox). For an AGL readout, subtract this from
    /// the camera's absolute elevation (georeference origin height + local z).
    /// </summary>
    public double? SampleDrapeGroundElevation(double x, double y)
    {
        if (!LastDrapeActive) return null;
        int nc = LastDrapeNumCols, nr = LastDrapeNumRows;
        if (nc < 2 || nr < 2 || LastDrapeCellSize <= 0) return null;

        // Fractional grid coordinates; outside the grid extent reads as unknown (no edge extension).
        double fx = (x - LastDrapeMinX) / LastDrapeCellSize;
        double fy = (y - LastDrapeMinY) / LastDrapeCellSize;
        if (fx < 0 || fy < 0 || fx > nc - 1 || fy > nr - 1) return null;

        if (!ReferenceEquals(LastDrapedDtmBytes, _drapeDtmRef))
        { _drapeDtmGrid = ToFloatGrid(LastDrapedDtmBytes); _drapeDtmRef = LastDrapedDtmBytes; }
        if (!ReferenceEquals(LastDrapedOffsetBytes, _drapeOffRef))
        { _drapeOffGrid = ToFloatGrid(LastDrapedOffsetBytes); _drapeOffRef = LastDrapedOffsetBytes; }

        int need = nc * nr;
        if (_drapeDtmGrid is null || _drapeOffGrid is null ||
            _drapeDtmGrid.Length < need || _drapeOffGrid.Length < need) return null;

        // DrapedZ = DTM + (DrapedZ - DTM); both grids are row-major (row = Y index from MinY, col = X).
        return Bilinear(_drapeDtmGrid, nc, nr, fx, fy) + Bilinear(_drapeOffGrid, nc, nr, fx, fy);
    }

    private static float[] ToFloatGrid(byte[] b)
    {
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);   // row-major float32, little-endian host
        return f;
    }

    private static double Bilinear(float[] grid, int nc, int nr, double fx, double fy)
    {
        int c0 = Math.Clamp((int)Math.Floor(fx), 0, nc - 1);
        int r0 = Math.Clamp((int)Math.Floor(fy), 0, nr - 1);
        int c1 = Math.Min(c0 + 1, nc - 1);
        int r1 = Math.Min(r0 + 1, nr - 1);
        double tx = fx - c0, ty = fy - r0;
        double top = grid[r0 * nc + c0] + (grid[r0 * nc + c1] - grid[r0 * nc + c0]) * tx;
        double bot = grid[r1 * nc + c0] + (grid[r1 * nc + c1] - grid[r1 * nc + c0]) * tx;
        return top + (bot - top) * ty;
    }

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

    // Upstream OpendriveGenerationParameters defaults (PythonAPI Client.cpp). A
    // `record struct default` is all-zero, which is wrong here — substitute these.
    private static readonly OpendriveGenerationParameters DefaultOpendriveParams =
        new(VertexDistance: 2.0, MaxRoadLength: 50.0, WallHeight: 1.0, AdditionalWidth: 0.6,
            SmoothJunctions: true, EnableMeshVisibility: true, EnablePedestrianNavigation: true);

    /// Equivalent of libcarla client.generate_opendrive_world: copy the .xodr to the
    /// server then load the special "OpenDriveMap" episode (Simulator::LoadOpenDriveEpisode).
    public async Task GenerateOpenDriveWorldAsync(
        string openDrive,
        OpendriveGenerationParameters parameters = default,
        bool resetSettings = true)
    {
        if (parameters.Equals(default(OpendriveGenerationParameters)))
            parameters = DefaultOpendriveParams;
        await CopyOpenDriveToServerAsync(openDrive, parameters).ConfigureAwait(false);
        await LoadEpisodeAsync("OpenDriveMap", resetSettings).ConfigureAwait(false);
    }

    /// Drop an .osm and fabricate the level at runtime: convert OSM→OpenDRIVE via the
    /// native netconvert (CarlaNet.Map.OsmConverter), then generate the OpenDRIVE world.
    public async Task GenerateWorldFromOsmAsync(
        string osmPath,
        CarlaNet.Map.OsmConversionOptions? osmOptions = null,
        OpendriveGenerationParameters parameters = default,
        bool resetSettings = true,
        CancellationToken ct = default)
    {
        var xodr = await new CarlaNet.Map.OsmConverter(osmOptions)
            .ConvertFileAsync(osmPath, ct).ConfigureAwait(false);
        // Inject stop/give-way signs from the OSM (netconvert never emits them) so the native
        // spawner renders real, sensor-detectable signs. The parsed map's geoReference is the
        // projection origin; a no-op when the OSM carries no such nodes.
        var signMap = CarlaNet.Map.OpenDrive.OpenDriveParser.Load(xodr);
        if (signMap != null)
            xodr = CarlaNet.Map.OpenDrive.SignInjector.InjectSigns(xodr, osmPath, signMap);
        await GenerateOpenDriveWorldAsync(xodr, parameters, resetSettings).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> GetNamesOfAllObjectsAsync()
        => _rpc.CallAsync<IReadOnlyList<string>>("get_names_of_all_objects");

    /// Full headless digital-twin world build (no editor): OSM -> flat .xodr (offline),
    /// extract road reference-line samples + reproject to WGS84 (offline), spawn/point a
    /// Cesium globe at the .xodr origin in the running episode, sample terrain heights,
    /// inject them into the .xodr &lt;elevationProfile&gt;, then generate the elevated OpenDRIVE
    /// world and re-establish Cesium as the visual overlay. Returns the elevated .xodr.
    ///
    /// The server must be running (any episode) and ticking (async mode). osmOptions MUST
    /// pin the origin (OriginLatitude/Longitude) so the .xodr is georeferenced; the origin
    /// is taken from the parsed map's geoReference. If originHeightOverride is null, the
    /// ellipsoidal height sampled at the origin is used as the vertical datum.
    public async Task<string> GenerateWorldFromOsmWithElevationAsync(
        string osmPath,
        string ionToken,
        long ionAssetId,
        long groundIonAssetId = 1,
        CarlaNet.Map.OsmConversionOptions? osmOptions = null,
        OpendriveGenerationParameters parameters = default,
        double sampleStepMeters = 10.0,
        double? originHeightOverride = null,
        double outlierThresholdMeters = 4.0,
        string heightAlign = "none",
        bool groundCollision = true,
        TimeSpan? cesiumSettle = null,
        double terrainResMeters = 2.0,
        double terrainMarginMeters = 30.48,
        int drapeChunkCells = 64,
        double drapeMaxDrapeMeters = 5.0,
        string? drapeCacheDir = null,
        CancellationToken ct = default)
    {
        // 1) OSM -> flat .xodr (offline, native netconvert). Also capture the SUMO network when
        //    traffic-light generation is on — its <tlLogic> phase programs drive TrafficLightInjector.
        var (flatXodr, sumoNet) = await new CarlaNet.Map.OsmConverter(osmOptions)
            .ConvertFileWithNetworkAsync(osmPath, ct).ConfigureAwait(false);

        // 2) Parse + extract reference-line samples + reproject (all offline). The origin
        //    is the .xodr geoReference (pinned by osmOptions).
        var map = CarlaNet.Map.OpenDrive.OpenDriveParser.Load(flatXodr)
            ?? throw new InvalidOperationException("generated .xodr failed to parse");
        var origin = map.GeoReference;
        var samples = CarlaNet.Map.OpenDrive.ElevationInjector
            .ExtractCenterlineSamples(map, sampleStepMeters);
        // Every height is read at the position the map data gave a road, not the position the
        // road was slid to. netconvert has already moved the geometry, so undoing the shift here
        // is what makes the order sample-then-shift: a road keeps the elevation of the ground it
        // was drawn on and carries it sideways, rather than picking up whatever it was moved over.
        //
        // The same positions are used to match a sample to its OSM way, so the bridge and tunnel
        // records still line up with the ways they came from.
        //
        // netconvert's --offset.y is northing; the OpenDRIVE reader flips it into CARLA's
        // -Y = north, so undoing a northward shift adds to Y.
        var probes = samples;
        if (RoadOffsetEast != 0.0 || RoadOffsetNorth != 0.0)
        {
            var moved = new CarlaNet.Map.OpenDrive.CenterlineSample[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                moved[i] = new CarlaNet.Map.OpenDrive.CenterlineSample(
                    samples[i].RoadId, samples[i].S,
                    samples[i].X - RoadOffsetEast, samples[i].Y + RoadOffsetNorth);
            }
            probes = moved;
            Console.WriteLine($"[road offset] heights read {RoadOffsetEast:+0.00;-0.00;0} m east / "
                + $"{RoadOffsetNorth:+0.00;-0.00;0} m north of where the roads now sit");
        }
        var geo = CarlaNet.Map.OpenDrive.ElevationInjector.ToGeo(probes, origin);

        // 2a) Read the vertical structure out of the OSM. netconvert discards `layer`/`bridge`, so
        //     without this the .xodr has no record of which road passes over which and a sampled
        //     surface gives a deck and the road beneath it the same height. Projected against the
        //     same origin as the centreline samples, so the two are directly comparable.
        var osmLayers = CarlaNet.Map.OpenDrive.OsmRoadLayers.Read(osmPath, origin);
        bool anyLayeredWays = false;
        foreach (var w in osmLayers.Ways) if (w.Layer != 0) { anyLayeredWays = true; break; }

        // 3) Configure the layered Cesium globe at the origin in the CURRENT episode (the server's
        //    startup map): the visual "photoreal" tileset (ionAssetId) plus, when groundIonAssetId>0,
        //    a hidden collidable bare-earth "ground" tileset (Cesium World Terrain) — the road-Z
        //    sample source. No flat world build needed; sampling only needs the tileset present.
        await ConfigureCesiumGeoreferenceAsync(origin, ionToken, ionAssetId, groundIonAssetId, refresh: true)
            .ConfigureAwait(false);

        // De-risk: a VISIBLE World Terrain is proven to sample; whether a HIDDEN tileset samples is
        // unverified. So reveal the ground layer while we sample it, then hide it again — road-Z
        // sampling never depends on hidden-tileset streaming. (Falls back to "" = first tileset when
        // no ground layer was requested.)
        bool sampleGround = groundIonAssetId > 0;
        string sampleSelector = sampleGround ? "ground" : "";
        if (sampleGround)
            await SetLayerVisibleAsync("ground", true).ConfigureAwait(false);

        if (cesiumSettle is { } settle)
            await Task.Delay(settle, ct).ConfigureAwait(false);

        // 4) Sample heights: origin first (vertical datum), then every road sample.
        var points = new List<GeoLocation>(geo.Count + 1)
        {
            new GeoLocation(origin.Latitude, origin.Longitude, 0.0)
        };
        foreach (var g in geo)
            points.Add(new GeoLocation(g.Latitude, g.Longitude, 0.0));

        var heights = await SampleTerrainHeightsAsync(points, sampleSelector, ct: ct).ConfigureAwait(false);
        if (heights.Count != points.Count)
            throw new InvalidOperationException(
                $"terrain-height result count {heights.Count} != requested {points.Count}");

        // 4a-drape) "drape" mode: sample photoreal + bare-earth heights over a regular grid covering
        //   the whole OSM bounds (the physics sandbox), then de-spike into a draped surface + per-cell
        //   offset field. The ground layer is revealed here, so the grid samples in the same window
        //   as the road heights.
        bool drape = string.Equals(heightAlign, "drape", StringComparison.OrdinalIgnoreCase);
        // "terrain": the road follows bare earth and the imagery is brought to it, rather than the
        // road being brought to the imagery. The photoreal is a SURFACE model -- it carries trees,
        // canopies and awnings -- so a road that follows it rides over whatever stands beside the
        // carriageway. Measured across the road stations of Arapahoe_I25, bare earth bends by a
        // median of 4.2 cm per station against 49.5 cm for the raw photoreal, and its worst bend is
        // 2.87 m against 12.09 m for the road as the drape builds it.
        //
        // The imagery is then shifted by the one systematic gap between the two surfaces, so it sits
        // on the terrain the road is on. That gap is a property of how the two tilesets were
        // produced, not of the ground: -0.81 m on Arapahoe_I25 and -0.83 m on Hormuz, near enough
        // identical on two maps half a world apart.
        bool terrainAlign = string.Equals(heightAlign, "terrain", StringComparison.OrdinalIgnoreCase);

        // The sandbox rectangle covers the OSM bounds exactly (no outward expansion), gridded at the
        // terrain resolution. It is computed for EVERY height-align mode: "drape" samples this grid
        // to build the collision heightfield, and boundary-aware traffic uses the same rectangle as
        // its staging extent whether or not a heightfield was built. Gridding it identically in all
        // modes keeps the staging ring the same for a given area however the road was seated.
        // terrainMargin is the INWARD staging ring reserved at the OSM edge for traffic entry/exit,
        // not a terrain extension — it is recorded with the bounds and read by get_staging_bounds.
        var osmBounds = CarlaNet.Map.OpenDrive.DrapeTerrain.ReadOsmBounds(osmPath);
        if (drape && osmBounds is null)
            throw new InvalidOperationException("height-align drape requires an OSM <bounds> element");
        CarlaNet.Map.OpenDrive.DrapeGridSpec sandboxSpec = default;
        bool haveSandbox = osmBounds is not null;
        if (osmBounds is { } bounds)
        {
            sandboxSpec = CarlaNet.Map.OpenDrive.DrapeTerrain.MakeGridFromGeoBounds(
                origin, bounds.MinLat, bounds.MinLon, bounds.MaxLat, bounds.MaxLon,
                terrainResMeters, marginMeters: 0.0);
        }
        else
        {
            Console.WriteLine("[staging] the OSM has no <bounds> element; "
                + "boundary-aware traffic will have no staging rectangle.");
        }

        // The photoreal (DSM) and bare-earth (DTM) grids over the sandbox, NaN-filled so both the
        // drape and the grade-separation lift read the same surfaces. Sampled here, while the ground
        // layer is revealed; the surface is not de-spiked until the deck footprints are known.
        double[] drapeSurface = [], drapeGround = [];
        double drapeSystematicOffset = 0.0;
        CarlaNet.Map.OpenDrive.DrapeTerrain.DrapeResult drapeRes = default;
        if (drape)
        {
            Console.WriteLine($"[drape] grid {sandboxSpec.NumCols}x{sandboxSpec.NumRows} = {sandboxSpec.NodeCount} nodes, "
                + $"cell {terrainResMeters} m");
            var (dsm, dtm) = await SampleDrapeGridCachedAsync(
                sandboxSpec, ionAssetId, groundIonAssetId, drapeCacheDir, drapeChunkCells, ct: ct)
                .ConfigureAwait(false);
            drapeSurface = CarlaNet.Map.OpenDrive.DrapeTerrain.FillMissing(dsm, sandboxSpec);
            drapeGround = CarlaNet.Map.OpenDrive.DrapeTerrain.FillMissing(dtm, sandboxSpec);
            drapeSystematicOffset = CarlaNet.Map.OpenDrive.DrapeTerrain.SystematicOffset(
                drapeSurface, drapeGround, drapeMaxDrapeMeters);
            Console.WriteLine("[drape] photoreal-vs-bare-earth offset over open ground = "
                + $"{drapeSystematicOffset:F2} m");
        }

        // 4b) Height reconciliation (09_Layer_Architecture follow-up). The bare-earth GROUND layer
        //     (DTM) sits at a different height than the visible PHOTOREAL surface (DSM), so road-Z
        //     taken from the DTM is offset from where the photogrammetry renders. Sample the photoreal
        //     layer at the same points and shift ALL road heights by ONE constant offset so they sit on
        //     the photoreal street. No per-point layer selection, no smoothing — a single scalar:
        //       "origin" = the gap at the origin point;
        //       "area"   = the median gap over the map's road points (calibrated over the whole tile);
        //       "none"   = no correction.
        //     Offset is computed from the data — no user correction factor.
        double heightOffset = 0.0;
        bool wantAlign = ionAssetId > 0
            && (heightAlign == "origin" || heightAlign == "area" || terrainAlign);
        // The photoreal surface is also where a bridge deck's real clearance is read, so sample it at
        // the road points whenever the OSM records anything off grade. The drape mode already has
        // both surfaces on its grid and needs no second pass.
        bool needPhotorealAtRoadPoints = ionAssetId > 0 && (wantAlign || (anyLayeredWays && !drape));
        IReadOnlyList<GeoLocation> photo = [];
        if (needPhotorealAtRoadPoints)
            photo = await SampleTerrainHeightsAsync(points, "photoreal", ct: ct).ConfigureAwait(false);
        if (wantAlign)
        {
            if (photo.Count == points.Count)
            {
                if (heightAlign == "origin")
                {
                    double pg = photo[0].Altitude, wt = heights[0].Altitude;
                    if (double.IsFinite(pg) && double.IsFinite(wt)) heightOffset = pg - wt;
                    Console.WriteLine($"[height-align origin] photoreal-ground gap at origin = {heightOffset:F2} m");
                }
                else // "area" and "terrain": median of (photoreal - ground) over the road points
                {
                    var diffs = new List<double>(samples.Count);
                    for (int i = 1; i < points.Count; i++)
                    {
                        double pg = photo[i].Altitude, wt = heights[i].Altitude;
                        if (double.IsFinite(pg) && double.IsFinite(wt)) diffs.Add(pg - wt);
                    }
                    if (diffs.Count > 0)
                    {
                        diffs.Sort();
                        heightOffset = diffs[diffs.Count / 2]; // median
                        Console.WriteLine($"[height-align area] photoreal-ground gap over {diffs.Count} road pts: "
                            + $"median {heightOffset:F2} m  (min {diffs[0]:F2}, max {diffs[^1]:F2})");
                    }
                }
            }
            else
            {
                Console.WriteLine($"[height-align] photoreal sample count {photo.Count} != {points.Count}; no offset applied.");
            }
        }
        Console.WriteLine($"[height-align {heightAlign}] road-Z offset applied = {heightOffset:F2} m");

        if (sampleGround)
            await SetLayerVisibleAsync("ground", false).ConfigureAwait(false);

        // 4c) Route each road to the surface its OSM layer selects. A vertical sample returns one
        //     height per plan position, so a deck and the road beneath it come back identical and no
        //     threshold on that one number can separate them; the OSM layer tags are the only record
        //     of which is which. A deck reads the photoreal, where it is the topmost surface and so
        //     carries its own measured clearance; everything at grade reads bare earth, which nothing
        //     overhead can contaminate. What comes back is a lift above the at-grade surface the
        //     chosen height-align mode already produces, so an extract with nothing off grade
        //     reproduces the previous behaviour exactly.
        double atGradeOffset = drape ? drapeSystematicOffset : heightOffset;
        var gradeLift = new double[samples.Count];
        var raisedSamples = new bool[samples.Count];
        IReadOnlyList<CarlaNet.Map.OpenDrive.OsmRoadWay> elevatedStructures = [];
        // One description of a deck's footprint, shared by the road elevation (which spans it on the
        // roads underneath) and the collision heightfield (which anchors it to the ground).
        var separationOptions = new CarlaNet.Map.OpenDrive.GradeSeparationOptions();
        if (anyLayeredWays && (drape || photo.Count == points.Count))
        {
            var surfaceAtSample = new double[samples.Count];
            var groundAtSample = new double[samples.Count];
            double[]? atGradeAtSample = null;
            if (drape)
            {
                // The at-grade draped surface as it stands before any deck footprint is anchored to
                // the ground. Away from a footprint it is the final surface, so it is what a road
                // spanning a footprint must join up to at either end. (Anchoring only lowers cells
                // inside footprints, which is where the span replaces the elevation anyway.)
                var openDrape = CarlaNet.Map.OpenDrive.DrapeTerrain.Despike(
                    drapeSurface, drapeGround, sandboxSpec, drapeMaxDrapeMeters,
                    atGradeOffsetMeters: drapeSystematicOffset);
                atGradeAtSample = new double[samples.Count];
                for (int i = 0; i < samples.Count; i++)
                    atGradeAtSample[i] = CarlaNet.Map.OpenDrive.DrapeTerrain.SampleBilinear(
                        openDrape.DrapedZ, sandboxSpec, probes[i].X, probes[i].Y);
            }

            for (int i = 0; i < samples.Count; i++)
            {
                if (drape)
                {
                    surfaceAtSample[i] = CarlaNet.Map.OpenDrive.DrapeTerrain.SampleBilinear(
                        drapeSurface, sandboxSpec, probes[i].X, probes[i].Y);
                    groundAtSample[i] = CarlaNet.Map.OpenDrive.DrapeTerrain.SampleBilinear(
                        drapeGround, sandboxSpec, probes[i].X, probes[i].Y);
                }
                else
                {
                    surfaceAtSample[i] = photo[i + 1].Altitude;
                    groundAtSample[i] = heights[i + 1].Altitude;
                }
            }

            // Under "terrain" the at-grade road IS bare earth, so that is the surface a lift is
            // added to. The offset still describes where the imagery ends up, so a deck lands on
            // the shifted photoreal: lift = surface - ground - offset, which is zero over open
            // ground and the deck's own measured clearance under a structure.
            var atGradeSurface = terrainAlign ? groundAtSample : atGradeAtSample;
            var separation = CarlaNet.Map.OpenDrive.GradeSeparation.Compute(
                map, probes, osmLayers, surfaceAtSample, groundAtSample, atGradeOffset,
                separationOptions, atGradeSurface);
            gradeLift = separation.Lift;
            elevatedStructures = separation.ElevatedWays;
            for (int i = 0; i < samples.Count; i++) raisedSamples[i] = gradeLift[i] != 0.0;
        }
        else if (anyLayeredWays)
        {
            Console.WriteLine("[GradeSeparation] the OSM records layered ways but no photoreal surface "
                + "was sampled at the road points; every road stays at grade.");
        }

        if (drape)
        {
            // De-spike only now: the draped surface has to know the deck footprints, because one
            // height per cell cannot hold both a deck and the road under it. Anchoring those cells to
            // the ground leaves the lower road something to sit on and lets the deck carry its own
            // road-mesh collision.
            drapeRes = CarlaNet.Map.OpenDrive.DrapeTerrain.Despike(
                drapeSurface, drapeGround, sandboxSpec, drapeMaxDrapeMeters,
                elevatedStructures: elevatedStructures, atGradeOffsetMeters: drapeSystematicOffset,
                structureHalfWidthMeters: separationOptions.StructureFootprintHalfWidthMeters);
            Console.WriteLine("[drape] de-spiked draped surface + offset field built"
                + (elevatedStructures.Count > 0
                    ? $"; {elevatedStructures.Count} elevated structures anchored to the ground beneath them."
                    : "."));
        }

        // Persist the data the telemetry path needs to report bare-earth HAE truth decoupled from
        // this visual shift (consumed by get_vehicle_telemetry in the Python shim; no live Cesium
        // sampling in the 5 Hz loop). The constant offset recovers on-road truth exactly today;
        // the per-point DTM table survives a future spatially-varying drape and off-road vehicles.
        // Build the table from the INPUT sample lat/lon (points[1..]) so coordinates are exact
        // regardless of whether the server echoes lat/lon back in the height results.
        // "terrain" leaves the road on bare earth, so there is nothing for telemetry to subtract.
        LastHeightAlignOffset = terrainAlign ? 0.0 : heightOffset;
        var dtmSamples = new GeoLocation[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            dtmSamples[i] = new GeoLocation(points[i + 1].Latitude, points[i + 1].Longitude, heights[i + 1].Altitude);
        LastGroundDtmSamples = dtmSamples;

        // originHeight stays the GROUND (DTM) origin sample — the georeference datum is unchanged.
        // Only the road MESH is shifted by heightOffset (visual seating on the photoreal); the
        // reported telemetry HAE is now decoupled from that shift and reports bare-earth DTM truth.
        double originHeight = originHeightOverride ?? heights[0].Altitude;

        // Smooth the bare-earth heights along each road before anything is added to them. A
        // terrain model is not a road surface: it knows nothing about carriageways, and a road
        // that follows it point by point inherits every wrinkle. The drape blurs its grid for the
        // same reason; sampling point by point skips that, which is why this mode needs its own.
        // Grouped by road because a road is what a profile runs along, and the smoothing must not
        // reach across a junction into a road going somewhere else.
        var groundHeights = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++) groundHeights[i] = heights[i + 1].Altitude;
        // Only the terrain mode. 'none' and the constant-offset modes are meant to hand back bare
        // earth as sampled, and the drape follows its own already-blurred grid instead of these.
        int passes = terrainAlign ? TerrainSmoothingPasses : 0;
        int smoothed = SmoothAlongRoads(samples, groundHeights, passes);
        if (smoothed > 0)
        {
            Console.WriteLine($"[elevation] smoothed bare earth along {smoothed} road(s), "
                + $"{passes} pass(es)");
        }

        var roadEllipsoidal = new double[samples.Count];
        // At-grade roads conform to the SAME draped surface as the terrain (bilinear at each
        // centerline point), so the road mesh and the collision heightfield coincide — no seam. A
        // road the OSM places off grade adds its lift on top, which puts a deck back on the photoreal
        // surface it was measured from and leaves the heightfield below it as the road it crosses.
        if (drape)
        {
            for (int i = 0; i < samples.Count; i++)
                roadEllipsoidal[i] = CarlaNet.Map.OpenDrive.DrapeTerrain.SampleBilinear(
                    drapeRes.DrapedZ, sandboxSpec, probes[i].X, probes[i].Y) + gradeLift[i];

            // Optionally put back the stations the de-spike dropped onto bare earth. It anchors
            // a cell to the ground wherever the photoreal stands well above it, reading that as
            // a structure. Over bare terrain that is wrong and the road follows the ground down
            // into the hillside it should lie on -- but over a tree-lined street it is right,
            // and lifting the road back onto the photoreal there puts it on the canopy. The
            // photoreal is a surface model: it cannot tell a road under trees from a road on a
            // steep bank, so this is off until something can.
            if (PhotorealClampEnabled)
            {
            var photorealAtSample = new double[samples.Count];
            for (int i = 0; i < samples.Count; i++)
                photorealAtSample[i] = CarlaNet.Map.OpenDrive.DrapeTerrain.SampleBilinear(
                    drapeSurface, sandboxSpec, probes[i].X, probes[i].Y);
            var clamp = CarlaNet.Map.OpenDrive.PhotorealClamp.Apply(
                probes, roadEllipsoidal, photorealAtSample, raisedSamples);
            Console.WriteLine(
                $"[photoreal clamp] {clamp.Lifted} station(s) lifted back onto the photoreal"
                + (clamp.Lifted > 0 ? $" (largest {clamp.LargestLiftMeters:F2} m)" : "")
                + $"; left alone: {clamp.UnderDeck} under a deck, {clamp.Sustained} on a "
                + $"sustained grade, {clamp.Raised} deliberately raised.");
            }
        }
        else
        {
            // "terrain" leaves the road on bare earth and moves the imagery instead, so it adds no
            // offset here; the constant-offset modes raise the road onto the imagery.
            double roadShift = terrainAlign ? 0.0 : heightOffset;
            for (int i = 0; i < samples.Count; i++)
                roadEllipsoidal[i] = groundHeights[i] + roadShift + gradeLift[i];
        }

        // Record what this build actually used, before the fit turns it into cubics.
        if (!string.IsNullOrWhiteSpace(ElevationDiagnosticsPath))
        {
            WriteElevationDiagnostics(
                ElevationDiagnosticsPath!, probes, geo, heights, photo, gradeLift, raisedSamples,
                roadEllipsoidal, originHeight, heightAlign);
        }

        // 5) Inject the sampled heights into the .xodr <elevationProfile>. The C1 fit removes the
        //    crease the piecewise-linear ramps left at every sample boundary, and carries height
        //    and slope through junction connectors so the surface does not step at intersections.
        var elevatedXodr = CarlaNet.Map.OpenDrive.ElevationInjector.InjectElevation(
            flatXodr, samples, roadEllipsoidal, originHeight,
            CarlaNet.Map.OpenDrive.ElevationFitMode.MonotoneCubicHermite, outlierThresholdMeters,
            raisedSamples);

        // 5b) Inject stop/give-way signs from the OSM (netconvert never emits them). Placement is
        //     by road/s + a shoulder offset, unaffected by the elevation just added, and uses the
        //     same geoReference origin; a no-op when the OSM carries no such nodes.
        elevatedXodr = CarlaNet.Map.OpenDrive.SignInjector.InjectSigns(elevatedXodr, osmPath, map);

        // 5c) Group the netconvert traffic lights per phase and link them to their junctions.
        //     netconvert emits the light <signal>s and one all-heads <controller> per junction but
        //     no <junction><controller> link and no phase split, so CARLA orphans every light
        //     (issue #1) and would flash whole junctions green. TrafficLightInjector rebuilds the
        //     controllers per phase from the SUMO <tlLogic> and adds the links; a no-op when
        //     traffic-light generation is off (sumoNet null).
        if (sumoNet != null)
            elevatedXodr = CarlaNet.Map.OpenDrive.TrafficLightInjector.InjectTrafficLights(elevatedXodr, sumoNet);

        // 6) Generate the elevated OpenDRIVE world (builds road mesh + waypoints at correct Z).
        await GenerateOpenDriveWorldAsync(elevatedXodr, parameters).ConfigureAwait(false);

        // 7) The reload destroyed the runtime Cesium actors — re-establish them as the
        //    visual overlay. CRITICAL: set the georeference OriginHeight to the sampled
        //    ground height (NOT the .xodr's altitude=0), so ellipsoidal-height originHeight
        //    maps to Unreal z=0 and the photogrammetry sits ON the roads (which were injected
        //    as z = ellipsoidal - originHeight). Using 0 floats the globe ~originHeight metres
        //    above the roads.
        var alignedOrigin = new GeoLocation(origin.Latitude, origin.Longitude, originHeight);
        await ConfigureCesiumGeoreferenceAsync(alignedOrigin, ionToken, ionAssetId, groundIonAssetId, refresh: true)
            .ConfigureAwait(false);

        if (drape)
        {
            // "drape" mode: the draped heightfield IS the collision/seating surface over the whole
            // OSM sandbox (on- and off-road). Build it from the de-spiked grid (local Z = ellipsoidal
            // - originHeight, metres) and turn the bare-earth "ground" tileset collision OFF (it stays
            // hidden as the truth sample source; the heightfield owns physics). No constant offset
            // applies in drape mode.
            int n = drapeRes.DrapedZ.Length;
            var hf = new double[n];
            for (int i = 0; i < n; i++) hf[i] = drapeRes.DrapedZ[i] - originHeight;
            await BuildDrapedTerrainAsync(
                sandboxSpec.MinX, sandboxSpec.MinY, sandboxSpec.CellSize, sandboxSpec.NumCols, sandboxSpec.NumRows, hf)
                .ConfigureAwait(false);
            if (groundIonAssetId > 0)
                await SetLayerCollisionAsync("ground", false).ConfigureAwait(false);

            // Persist the per-cell offset field (DrapedZ - DTM) and the bare-earth DTM (= DrapedZ -
            // Offset) as bulk float32 LE buffers for the telemetry path (shim bilinear lookup by
            // vehicle local X/Y): hae = physical - offset = DTM + pivot; hae_dtm = DTM.
            var offBytes = new byte[n * sizeof(float)];
            var dtmBytes = new byte[n * sizeof(float)];
            var offF = new float[n];
            var dtmF = new float[n];
            for (int i = 0; i < n; i++) { offF[i] = (float)drapeRes.Offset[i]; dtmF[i] = (float)(drapeRes.DrapedZ[i] - drapeRes.Offset[i]); }
            System.Buffer.BlockCopy(offF, 0, offBytes, 0, offBytes.Length);
            System.Buffer.BlockCopy(dtmF, 0, dtmBytes, 0, dtmBytes.Length);
            LastDrapeActive = true;
            LastDrapeNumCols = sandboxSpec.NumCols;
            LastDrapeNumRows = sandboxSpec.NumRows;
            LastDrapeMinX = sandboxSpec.MinX;
            LastDrapeMinY = sandboxSpec.MinY;
            LastDrapeCellSize = sandboxSpec.CellSize;
            LastDrapedOffsetBytes = offBytes;
            LastDrapedDtmBytes = dtmBytes;
            LastHeightAlignOffset = 0.0;   // drape uses the per-cell field, not the scalar
        }
        else if (terrainAlign)
        {
            // "terrain": the road is on bare earth, so the collidable ground layer stays exactly
            // where it is and the two coincide with no shift at all. The imagery moves instead --
            // by the same systematic gap, in the opposite direction -- so the photoreal sits on the
            // terrain the road runs on rather than the road being lifted into the canopy.
            //
            // Telemetry needs no correction in this mode: the road is at bare earth, so the
            // reported height is already bare-earth truth and LastHeightAlignOffset stays zero.
            LastDrapeActive = false;
            LastHeightAlignOffset = 0.0;
            if (ionAssetId > 0 && heightOffset != 0.0)
            {
                await SetLayerOffsetAsync("photoreal", -heightOffset).ConfigureAwait(false);
                Console.WriteLine($"[height-align terrain] imagery shifted {-heightOffset:F2} m onto "
                    + "the terrain; roads and collidable ground left on bare earth");
            }
            if (groundIonAssetId > 0)
            {
                await SetLayerOffsetAsync("ground", 0.0).ConfigureAwait(false);
                await SetLayerCollisionAsync("ground", groundCollision).ConfigureAwait(false);
            }
        }
        else if (groundIonAssetId > 0)
        {
            // Constant-offset modes ('area'/'origin'): drop the collidable bare-earth "ground" layer
            // by the height-align offset so its collision mesh coincides with the offset road mesh.
            // Because those modes apply a single CONSTANT offset (road = DTM + heightOffset), a
            // constant ground shift re-coincides them EXACTLY everywhere — on-road vehicles no longer
            // float above the lowered road, and off-road vehicles still ride a (now offset) collidable
            // surface instead of falling through. The truth georeference is untouched (GetCesiumOrigin
            // and height sampling stay bare-earth). MUST run AFTER the ConfigureCesiumGeoreferenceAsync
            // above (which reassigns tilesets to the default georeference). No-op when heightOffset == 0
            // ('none'). Telemetry then subtracts the same constant everywhere (see
            // get_vehicle_telemetry); LastHeightAlignOffset carries it.
            LastDrapeActive = false;
            await SetLayerOffsetAsync("ground", heightOffset).ConfigureAwait(false);
            // Ground collision is now SAFE to leave ON under area/origin (it coincides with the road),
            // giving on-road seating + off-road support. eo_observer's V key still toggles it.
            await SetLayerCollisionAsync("ground", groundCollision).ConfigureAwait(false);
        }

        // Record the sandbox extent + inward staging ring for boundary-aware traffic. Deliberately
        // outside the height-align branches: the ring is a property of the OSM area, and every mode
        // leaves a collidable surface under it (the draped heightfield, or the ground layer shifted
        // to meet the road).
        if (haveSandbox)
        {
            await SetStagingBoundsAsync(
                sandboxSpec.MinX, sandboxSpec.MinY, sandboxSpec.MaxX, sandboxSpec.MaxY, terrainMarginMeters)
                .ConfigureAwait(false);
            Console.WriteLine($"[staging] sandbox ({sandboxSpec.MinX:F1}, {sandboxSpec.MinY:F1}) .. "
                + $"({sandboxSpec.MaxX:F1}, {sandboxSpec.MaxY:F1}) m, staging margin {terrainMarginMeters:F1} m (inward)");
        }

        return elevatedXodr;
    }

    // ── Cesium terrain-height sampling (digital-twin elevation pipeline) ──────────
    // Samples ground heights from the Cesium 3D tileset present in the loaded world,
    // for the digital-twin OpenDRIVE <elevation> injection (CarlaNet.Map.OpenDrive.
    // ElevationInjector). Input/output are GeoLocation(latitude, longitude, altitude);
    // on output altitude carries the sampled ellipsoidal height (double.NaN where the
    // tileset had no height at that point).
    //
    // The server splits this into request_terrain_heights + poll_terrain_heights
    // because Cesium's sampler resolves asynchronously on the game thread across
    // several ticks. We kick it off then poll until results arrive. REQUIRES the
    // server to be ticking (async mode, e.g. during world generation) AND a Cesium
    // tileset present in the level.
    /// <summary>Runs a 1-2-1 smoothing along each road, in place. Returns how many roads were
    /// touched.
    ///
    /// Samples arrive grouped by road and ascending in s, so a road is a contiguous run. The ends
    /// are left alone: they are where roads meet, and moving one would pull a junction apart.
    ///
    /// A 1-2-1 kernel reproduces any straight line exactly, so a constant grade comes through
    /// untouched however many passes are run. Only curvature is reduced, which at ten-metre
    /// spacing is where a bump lives.</summary>
    private static int SmoothAlongRoads(
        IReadOnlyList<CarlaNet.Map.OpenDrive.CenterlineSample> samples,
        double[] heights,
        int passes)
    {
        if (passes <= 0 || samples.Count < 3) return 0;

        int touched = 0;
        int start = 0;
        while (start < samples.Count)
        {
            int end = start;
            while (end + 1 < samples.Count && samples[end + 1].RoadId == samples[start].RoadId)
            {
                ++end;
            }
            int count = end - start + 1;
            if (count >= 3)
            {
                var work = new double[count];
                for (int p = 0; p < passes; ++p)
                {
                    work[0] = heights[start];
                    work[count - 1] = heights[end];
                    for (int k = 1; k < count - 1; ++k)
                    {
                        double a = heights[start + k - 1];
                        double b = heights[start + k];
                        double c = heights[start + k + 1];
                        work[k] = double.IsFinite(a) && double.IsFinite(c)
                            ? 0.25 * a + 0.5 * b + 0.25 * c
                            : b;
                    }
                    for (int k = 0; k < count; ++k) heights[start + k] = work[k];
                }
                ++touched;
            }
            start = end + 1;
        }
        return touched;
    }

    /// <summary>Writes one row per centreline sample: where it is, every height the elevation
    /// pass read there, and what the road ended up at.
    ///
    /// This is the record that makes a build answerable afterwards. A bump in the road can then be
    /// attributed to the terrain, to a grade-separation lift, or to the fit, by reading the row
    /// rather than by re-sampling and hoping the sampling agrees.</summary>
    private static void WriteElevationDiagnostics(
        string path,
        IReadOnlyList<CarlaNet.Map.OpenDrive.CenterlineSample> samples,
        IReadOnlyList<CarlaNet.Map.OpenDrive.GeoSample> geo,
        IReadOnlyList<GeoLocation> ground,
        IReadOnlyList<GeoLocation> photoreal,
        IReadOnlyList<double> lift,
        IReadOnlyList<bool> raised,
        IReadOnlyList<double> roadEllipsoidal,
        double originHeight,
        string heightAlign)
    {
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // ground and photoreal carry the origin at index 0, so a sample's own reading is one on.
            bool havePhotoreal = photoreal.Count == samples.Count + 1;
            var text = new StringBuilder();
            text.Append("# height-align=").Append(heightAlign)
                .Append("  origin_height=").Append(F(originHeight))
                .Append("  samples=").Append(samples.Count).Append('\n');
            text.Append("road\ts\tx\ty\tlatitude\tlongitude\tground\tphotoreal\tlift\t"
                + "raised\troad_ellipsoidal\troad_z\n");
            for (int i = 0; i < samples.Count; ++i)
            {
                double g = i + 1 < ground.Count ? ground[i + 1].Altitude : double.NaN;
                double s = havePhotoreal ? photoreal[i + 1].Altitude : double.NaN;
                text.Append(samples[i].RoadId).Append('\t')
                    .Append(F(samples[i].S)).Append('\t')
                    .Append(F(samples[i].X)).Append('\t')
                    .Append(F(samples[i].Y)).Append('\t')
                    .Append(F(geo[i].Latitude)).Append('\t')
                    .Append(F(geo[i].Longitude)).Append('\t')
                    .Append(F(g)).Append('\t')
                    .Append(F(s)).Append('\t')
                    .Append(F(lift[i])).Append('\t')
                    .Append(raised[i] ? '1' : '0').Append('\t')
                    .Append(F(roadEllipsoidal[i])).Append('\t')
                    .Append(F(roadEllipsoidal[i] - originHeight)).Append('\n');
            }
            File.WriteAllText(path, text.ToString());
            Console.WriteLine($"[elevation] wrote {samples.Count} sample rows to {path}");
        }
        catch (Exception e)
        {
            // A diagnostic must never be the reason a build fails.
            Console.WriteLine($"[elevation] could not write diagnostics to {path}: {e.Message}");
        }
    }

    private static string F(double v) =>
        double.IsNaN(v) ? "nan" : v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// Point the loaded world's CesiumGeoreference at <paramref name="origin"/>
    /// (latitude, longitude, altitude=OriginHeight) and optionally set the ion token /
    /// asset id on its tilesets. Call after generate_opendrive_world so the reloaded
    /// OpenDriveMap lines up with the active .xodr before sampling terrain heights.
    /// Configure the layered Cesium globe (08_Layer_Architecture). <paramref name="ionAssetId"/>
    /// is the visual "photoreal" layer; <paramref name="groundIonAssetId"/> (&gt;0) adds a hidden,
    /// collidable bare-earth "ground" layer (e.g. Cesium World Terrain asset 1) used as the
    /// height-sample source.
    public Task<bool> ConfigureCesiumGeoreferenceAsync(
        GeoLocation origin, string ionToken = "", long ionAssetId = 0,
        long groundIonAssetId = 0, bool refresh = true)
        => _rpc.CallAsync<bool>("configure_cesium_georeference",
               origin, ionToken, ionAssetId, groundIonAssetId, refresh);

    /// Show/hide the Cesium photogrammetry overlay in the loaded world (all tilesets).
    public Task<bool> SetCesiumVisibleAsync(bool visible)
        => _rpc.CallAsync<bool>("set_cesium_visible", visible);

    /// Set the CesiumSunSky solar clock (local hours in the map-longitude time zone, wrapped
    /// into [0,24)) and refresh lighting. False if the world has no CesiumSunSky.
    public Task<bool> SetSolarTimeAsync(double hours)
        => _rpc.CallAsync<bool>("set_solar_time", hours);

    /// Set the CesiumSunSky calendar date (seasonal sun angle) and refresh lighting.
    /// False if the world has no CesiumSunSky.
    public Task<bool> SetSolarDateAsync(long year, long month, long day)
        => _rpc.CallAsync<bool>("set_solar_date", year, month, day);

    /// Current solar clock/date/origin, packed as
    /// [solar_time, year, month, day, time_zone, lat, lon, advancing, rate]; empty if no sun.
    public Task<IReadOnlyList<double>> GetSolarStateAsync()
        => _rpc.CallAsync<IReadOnlyList<double>>("get_solar_state");

    /// Enable/disable automatic advancement of the solar clock (the sun moves as the scene runs).
    /// `rate` is sun-clock seconds per real/sim second (1.0 = real time). False if no CesiumSunSky.
    public Task<bool> SetTimeAdvanceAsync(bool enabled, double rate)
        => _rpc.CallAsync<bool>("set_time_advance", enabled, rate);

    /// Enable/disable physics collision on the Cesium photogrammetry tilesets (all).
    /// Collision is ON by default; this toggle never changes spawn defaults.
    public Task<bool> SetCesiumCollisionAsync(bool enabled)
        => _rpc.CallAsync<bool>("set_cesium_collision", enabled);

    /// Show/hide the CARLA OpenDRIVE road-mesh RENDERING. Collision is unaffected —
    /// vehicles still drive on the (invisible) roads. Stops the road mesh z-fighting
    /// with the photoreal Cesium streets.
    public Task<bool> SetRoadRenderedAsync(bool rendered)
        => _rpc.CallAsync<bool>("set_road_rendered", rendered);

    /// Per-layer visibility (08_Layer_Architecture). <paramref name="layer"/> is a Cesium
    /// tileset tag ("photoreal"/"ground", "" = all tilesets), "road" (the OpenDRIVE mesh) or
    /// "signals" (the traffic lights and stop/yield/speed-limit signs generated from OpenDRIVE;
    /// signals matched to a hand-placed actor are not affected). Rendering only, so hidden
    /// signals keep their stop-line trigger volumes and vehicles go on obeying them.
    public Task<bool> SetLayerVisibleAsync(string layer, bool visible)
        => _rpc.CallAsync<bool>("set_layer_visible", layer, visible);

    /// Per-layer physics collision (08_Layer_Architecture). Same layer naming as
    /// <see cref="SetLayerVisibleAsync"/>; independent of visibility.
    public Task<bool> SetLayerCollisionAsync(string layer, bool enabled)
        => _rpc.CallAsync<bool>("set_layer_collision", layer, enabled);

    /// Per-layer VERTICAL OFFSET. Moves the tagged Cesium tileset layer
    /// up/down by <paramref name="offsetMeters"/> (signed, +up) via a dedicated georeference,
    /// without moving the truth georeference (GetCesiumOrigin / sampling stay truthed). Used to
    /// drop the collidable bare-earth "ground" layer by the height-align offset so its collision
    /// coincides with the offset road mesh. 0 reassigns the layer to the default georeference.
    public Task<bool> SetLayerOffsetAsync(string layer, double offsetMeters)
        => _rpc.CallAsync<bool>("set_layer_offset", layer, offsetMeters);

    /// Build/replace the draped collision heightfield over the OSM sandbox ("drape" mode). Heights
    /// are world Z in METRES, row-major [row*numCols + col], length numCols*numRows; grid corner
    /// (col 0,row 0) at world (originX, originY) metres, +col=+X, +row=+Y, spacing cellSize m.
    public Task<bool> BuildDrapedTerrainAsync(
        double originX, double originY, double cellSize, int numCols, int numRows, double[] heights)
        => _rpc.CallAsync<bool>("build_draped_terrain",
               originX, originY, cellSize, numCols, numRows, heights);

    /// Record the sandbox extent (CARLA-local metres) and the inward staging-ring margin reserved at
    /// its edge for traffic entry/exit. Written by the digital-twin build in every height-align mode.
    public Task<bool> SetStagingBoundsAsync(
        double minX, double minY, double maxX, double maxY, double marginMeters)
        => _rpc.CallAsync<bool>("set_staging_bounds", minX, minY, maxX, maxY, marginMeters);

    /// Staging bounds for boundary-aware traffic: the sandbox extent in CARLA-local metres plus the
    /// inward staging-ring margin, as [minX, minY, maxX, maxY, margin]. Empty for a world that was
    /// loaded rather than generated. The scene perimeter (region of interest) = these bounds inset
    /// by the margin.
    public Task<IReadOnlyList<double>> GetStagingBoundsAsync()
        => _rpc.CallAsync<IReadOnlyList<double>>("get_staging_bounds");

    /// The Cesium georeference origin as GeoLocation(latitude, longitude, ellipsoidal height m).
    /// True elevation of a local Unreal point = this height + the point's local Z.
    public Task<GeoLocation> GetCesiumOriginAsync()
        => _rpc.CallAsync<GeoLocation>("get_cesium_origin");

    public async Task<IReadOnlyList<GeoLocation>> SampleTerrainHeightsAsync(
        IReadOnlyList<GeoLocation> points,
        string tilesetSelector = "",
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            return [];

        // tilesetSelector picks the layer to sample (e.g. "ground" = bare-earth World Terrain);
        // empty = the first tileset found (back-compatible single-tileset behaviour).
        await _rpc.CallAsync<bool>("request_terrain_heights", points, tilesetSelector ?? "")
            .ConfigureAwait(false);

        var poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // poll returns empty while sampling is in progress; non-empty == done;
            // a server-side failure surfaces as a CarlaRpcException.
            var results = await _rpc.CallAsync<IReadOnlyList<GeoLocation>>("poll_terrain_heights")
                .ConfigureAwait(false);
            if (results.Count > 0)
                return results;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"terrain-height sampling did not complete within {(timeout ?? TimeSpan.FromSeconds(120)).TotalSeconds:0} s");
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
    }

    // ── Draped-terrain grid sampling ("drape" mode) ───────────────────────────
    // Sample one Cesium layer over a regular grid (DrapeTerrain), CHUNKED so the "most-detailed"
    // sampler doesn't stream the whole OSM tile's fine 3D Tiles at once. Returns row-major heights
    // [row*NumCols+col] (NaN where the tileset had no surface). Requires the tilesets present +
    // (for "ground") revealed, same as the road-Z sampling in GenerateWorldFromOsmWithElevationAsync.
    public async Task<double[]> SampleDrapeGridAsync(
        CarlaNet.Map.OpenDrive.DrapeGridSpec spec,
        string selector,
        int chunkCells = 64,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var heights = new double[spec.NodeCount];
        for (int r0 = 0; r0 < spec.NumRows; r0 += chunkCells)
        {
            int nr = Math.Min(chunkCells, spec.NumRows - r0);
            for (int c0 = 0; c0 < spec.NumCols; c0 += chunkCells)
            {
                ct.ThrowIfCancellationRequested();
                int nc = Math.Min(chunkCells, spec.NumCols - c0);
                var pts = CarlaNet.Map.OpenDrive.DrapeTerrain.BlockGeoPoints(spec, c0, r0, nc, nr);
                var res = await SampleTerrainHeightsAsync(pts, selector, timeout, ct: ct).ConfigureAwait(false);
                if (res.Count != pts.Count)
                    throw new InvalidOperationException(
                        $"drape sample chunk count {res.Count} != requested {pts.Count}");
                int k = 0;
                for (int r = r0; r < r0 + nr; ++r)
                    for (int c = c0; c < c0 + nc; ++c)
                        heights[r * spec.NumCols + c] = res[k++].Altitude;
            }
        }
        return heights;
    }

    /// Sample DSM ("photoreal") + DTM ("ground") over the grid, reusing a binary disk cache keyed by
    /// (grid geometry, asset ids) when <paramref name="cacheDir"/> is non-empty (so the minutes-long
    /// sampling is paid once per area). Returns row-major (Dsm, Dtm) height arrays.
    public async Task<(double[] Dsm, double[] Dtm)> SampleDrapeGridCachedAsync(
        CarlaNet.Map.OpenDrive.DrapeGridSpec spec,
        long photoAsset,
        long groundAsset,
        string? cacheDir = null,
        int chunkCells = 64,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        string? path = string.IsNullOrEmpty(cacheDir)
            ? null
            : System.IO.Path.Combine(cacheDir,
                CarlaNet.Map.OpenDrive.DrapeTerrain.CacheFileName(spec, photoAsset, groundAsset));
        if (path != null &&
            CarlaNet.Map.OpenDrive.DrapeTerrain.TryReadCache(path, spec, photoAsset, groundAsset, out var cDsm, out var cDtm))
        {
            return (cDsm, cDtm);
        }
        var dsm = await SampleDrapeGridAsync(spec, "photoreal", chunkCells, timeout, ct).ConfigureAwait(false);
        var dtm = await SampleDrapeGridAsync(spec, "ground", chunkCells, timeout, ct).ConfigureAwait(false);
        if (path != null)
            CarlaNet.Map.OpenDrive.DrapeTerrain.WriteCache(path, spec, photoAsset, groundAsset, dsm, dtm);
        return (dsm, dtm);
    }

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

    /// <summary>
    /// Set the actor's staging fade: 0 = fully opaque, 1 = fully dissolved away. Writes the value to
    /// Custom Primitive Data index 8 on every primitive component server-side; vehicle materials read
    /// it to drive a dithered opacity dissolve for boundary-aware traffic entering/leaving the scene.
    /// The value is also remembered locally (<see cref="GetActorOpacity"/>,
    /// <see cref="IsActorEstablished"/>) because the server keeps no readable copy of it.
    /// </summary>
    public Task SetActorFadeAsync(ActorId id, double hide)
    {
        double opacity = 1.0 - Math.Clamp(hide, 0.0, 1.0);
        _fade.AddOrUpdate(
            id,
            _ => (opacity, opacity >= EstablishedOpacity),
            (_, previous) => (opacity, previous.Established || opacity >= EstablishedOpacity));
        return _rpc.CallVoidAsync("set_actor_fade", id, hide);
    }

    /// <summary>
    /// The actor's staging opacity as this client last set it: 1 = fully opaque, 0 = fully dissolved
    /// away. An actor nobody has faded reads 1 — nothing has asked for it to be anything else.
    /// </summary>
    public double GetActorOpacity(ActorId id) => _fade.TryGetValue(id, out var fade) ? fade.Opacity : 1.0;

    /// <summary>
    /// Whether the actor has ever been fully opaque. Boundary-aware staging traffic spawns a vehicle
    /// transparent out in the entry ring and fades it up as it crosses into the scene interior, so
    /// this latches the first time the vehicle is wholly inside and stays true while it fades back
    /// out on its way off the map — it answers "has this vehicle arrived", not "is it opaque right
    /// now". Actors nobody has faded are established from the moment they are seen.
    /// </summary>
    public bool IsActorEstablished(ActorId id) => !_fade.TryGetValue(id, out var fade) || fade.Established;

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
        // Server returns std::vector<CommandResponse> directly (no Response<T> wrap), so use raw path.
        => _rpc.CallRawAsync<IReadOnlyList<CommandResponse>>("apply_batch", commands, doTickCue);

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
        // Header layout (124 bytes): episode_id(8) platform_ts(8) delta_s(4) map_origin(12) state(1)
        // pad(3), then 11 appended solar doubles at offset 36 (§10.14 extended header).
        if (payload.Length < 36) return;
        platformTimestamp = BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));
        deltaSeconds = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(payload[16..]));
        const int HeaderSize = 124;
        if (payload.Length < HeaderSize) return;   // extended (with-solar) header required
        // Cache the solar block (11 doubles at offset 36) paired to this tick.
        var solar = new double[11];
        for (int k = 0; k < 11; k++)
            solar[k] = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(payload[(36 + k * 8)..]));
        _solar = solar;
        const int ActorSize  = 119;
        var actors = payload[HeaderSize..];
        int count  = actors.Length / ActorSize;
        _observedIds.Clear();
        for (int i = 0; i < count; i++)
        {
            var a  = actors.Slice(i * ActorSize, ActorSize);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(a);
            var st = (ActorState)a[4];
            // A destroyed actor may appear for a final tick tagged PendingKill/Invalid before it drops
            // out of the snapshot; treat it as already gone so it is evicted below and stops emitting.
            if (st == ActorState.PendingKill || st == ActorState.Invalid)
                continue;
            _observedIds.Add(id);
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
        // The episode state is a full snapshot of every live actor each tick, so evict any cached actor
        // absent from it (destroyed since the last tick). Without this the cache — and every telemetry
        // consumer reading GetCachedActorIds — grows without bound as traffic spawns and despawns, keeping
        // destroyed vehicles emitting and raising the per-frame cost over a long run. Removing during a
        // ConcurrentDictionary enumeration is safe.
        foreach (var kv in _actorCache)
            if (!_observedIds.Contains(kv.Key))
            {
                _actorCache.TryRemove(kv.Key, out _);
                // Drop the staging-fade record with the actor, so a recycled id never inherits the
                // arrival state of the vehicle that held it before.
                _fade.TryRemove(kv.Key, out _);
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

    /// <summary>
    /// Per-vehicle traffic-light state, speed limit, and at-traffic-light flag, decoded from the
    /// cached world-observer snapshot (no RPC). Valid only for vehicle actors — the type-dependent
    /// union carries different data for walkers/traffic-lights. Returns Green / 0 / false when the
    /// actor is not yet in the cache, so a caller defaults to "unimpeded" rather than "stop".
    /// </summary>
    /// <summary>
    /// Learn which actors are traffic lights, so their state can afterwards be read from the cached
    /// snapshot with no further RPC. Traffic lights are spawned when the map loads and live for the
    /// whole episode, so this is done once; call it again only if the map is reloaded.
    /// </summary>
    /// <remarks>
    /// The world-observer snapshot does not say what type an actor is — the type-dependent union is
    /// discriminated by a type the server knows and does not transmit — so the set has to be
    /// established over RPC. Lights whose signal id is empty are kept out: they were placed by hand
    /// rather than generated from OpenDRIVE, and nothing in the map can refer to them.
    /// </remarks>
    public async Task<int> RefreshTrafficLightRegistryAsync()
    {
        var ids = _actorCache.Keys.ToList();
        if (ids.Count == 0) return 0;
        var actors = await GetActorsByIdAsync(ids).ConfigureAwait(false);
        var found = new Dictionary<ActorId, string>();
        foreach (var actor in actors)
        {
            if (!actor.Description.Id.StartsWith("traffic.traffic_light", StringComparison.Ordinal))
                continue;
            if (!_actorCache.TryGetValue(actor.Id, out var snap)) continue;
            if (snap.ParseTrafficLightState() is not { } tl) continue;
            found[actor.Id] = tl.SignId;
        }
        _trafficLightIds = found;
        return found.Count;
    }

    private IReadOnlyDictionary<ActorId, string> _trafficLightIds =
        new Dictionary<ActorId, string>();

    /// <summary>
    /// The live state of every generated traffic light, keyed by the OpenDRIVE signal id it was
    /// built from. Read from the cached snapshot, so this costs one pass over the known lights and
    /// no RPC. Empty until <see cref="RefreshTrafficLightRegistryAsync"/> has run.
    /// </summary>
    public IReadOnlyDictionary<string, TrafficLightState> GetTrafficLightStatesBySignId()
    {
        var states = new Dictionary<string, TrafficLightState>(_trafficLightIds.Count);
        foreach (var (actorId, signId) in _trafficLightIds)
        {
            if (!_actorCache.TryGetValue(actorId, out var snap)) continue;
            if (snap.ParseTrafficLightState() is not { } tl) continue;
            states[signId] = tl.State;
        }
        return states;
    }

    public VehicleObservedState GetActorVehicleState(ActorId id)
        => _actorCache.TryGetValue(id, out var s)
            ? s.ParseVehicleState()
            : new VehicleObservedState(0f, TrafficLightState.Green, false);

    /// All actor IDs currently in the world observer cache.
    // NOTE: must materialize as a concrete array (not the `[.. _actorCache.Keys]`
    // collection-expression form) — that compiles to <>z__ReadOnlyArray<T> which
    // MessagePack-csharp has no formatter for and crashes RPC serialization.
    public IReadOnlyList<ActorId> GetCachedActorIds() => _actorCache.Keys.ToArray();

    /// Solar / time-of-day state from the latest world-observer snapshot, paired to the current tick
    /// (no RPC, no poll): [solar_time, year, month, day, time_zone, lat, lon, elevation_deg,
    /// azimuth_deg, advancing, rate]. Empty until the first snapshot arrives. Requires the world
    /// observer to be running (StartWorldObserverAsync).
    public IReadOnlyList<double> GetCachedSolarState() => _solar;

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
