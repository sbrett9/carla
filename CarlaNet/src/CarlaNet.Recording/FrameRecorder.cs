using System.Globalization;
using System.Threading.Channels;
using CarlaNet.Sensors;
using CarlaNet.Transport;
using CarlaNet.Transport.Streaming;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Recording;

/// <summary>
/// Native capture-to-disk recorder. Subscribes to a camera's sensor stream, decimates to a target rate,
/// and for each captured frame writes a lossless PNG of the imagery plus a CoT-XML telemetry sidecar,
/// paired by a filename stem that begins with the camera's name (<see cref="CameraName.StillStem"/>).
/// All decoding/encoding/IO happens on the .NET thread pool — the frame buffer never crosses to Python
/// and the GIL is never held, so the viewer stays smooth while recording.
///
/// Construction starts recording; <see cref="Dispose"/> stops it (flushes pending captures).
/// </summary>
/// <remarks>
/// <para><b>A still is written with the truth of its own frame, or not at all.</b> The image of a frame
/// arrives some ticks after the world observer's snapshot of that frame, so the recorder reads each
/// capture's vehicles, render set, supervision and camera pose from the client's snapshot of the
/// image's own frame (<see cref="CarlaClient.GetSnapshotFrame(ulong)"/>), and holds the client's
/// snapshots open for as long as it records (<see cref="SnapshotHold"/>): a frame is released once an
/// image of a later frame has been prepared, less a margin, and never while an image of it could still
/// arrive. Where the frame's truth is nonetheless not to be had -- the frame was never observed, or an
/// image arrived so late that the client's capacity had dropped it -- the still is dropped and counted
/// in <see cref="FrameUnpaired"/>, never written beside a neighbouring frame's truth: every vehicle
/// moves every tick, and nothing downstream is made to check such a pairing. A run's closeout holds the
/// count at zero.</para>
///
/// <para><b>The stream thread never waits on the server.</b> The camera's frames arrive on a thread
/// that reads its socket and calls <see cref="OnFrame"/> inline, and that thread does only what needs
/// nothing from the server: decimate, copy the pixels, read the cached sun and hand the frame on. The
/// truth records, the occlusion measurement and the platform pose are built on one preparation task
/// behind it, because building them can ask the server for things -- the description of an actor seen
/// for the first time, and the bare-earth reference record and its grids on the first capture of a
/// world -- and those are answered only from the synchronous server's RPC drain.</para>
///
/// <para>A synchronous server does not drop a sensor message: before it writes a camera's next image it
/// waits until the previous one has been written to that camera's socket
/// (<c>LibCarla/source/carla/streaming/detail/tcp/ServerSession.cpp</c>, <c>ServerSession::Write</c>).
/// So a stream thread that waits on the server while the image behind the one it is handling fills the
/// socket holds the server's next frame, and the server's next frame is where the drain that would
/// answer it runs. Measured with a 320x180 camera at every tick: a stream callback that waits on four
/// sequential synchronous round trips per frame stops the world at the fifth tick, and on two at the
/// ninth; one that waits on one, or on none, runs forty ticks; a 64x36 image, whose messages the socket
/// buffers absorb, runs forty ticks even with the wait. Recording a 320x180 camera stopped the world at
/// the fifth tick in exactly this way, on the first capture's actor descriptions and bare-earth
/// fetch.</para>
/// </remarks>
public sealed class FrameRecorder : IDisposable
{
    /// <summary>A decimated frame as the stream thread hands it on: nothing asked of the server yet.
    /// <paramref name="HeaderHFovDeg"/> is the field of view the image's own header reports.</summary>
    private sealed record Arrival(DateTime CapturedUtc, ulong Frame, double SimTimeSeconds,
                                  Transform HeaderTransform, int Width, int Height, double HeaderHFovDeg,
                                  ReadOnlyMemory<byte> Bgra, IReadOnlyList<double> Solar);

    private sealed record Job(DateTime CapturedUtc, int Width, int Height,
                              ReadOnlyMemory<byte> Bgra, IReadOnlyList<VehicleTelemetry> Telemetry,
                              IReadOnlyList<double> Solar, SensorPose? Sensor,
                              CaptureIdentity Capture, SidecarVehicles Vehicles, double? DrawDistance,
                              CaptureSupervision Supervision);

    private readonly CarlaClient _client;
    private readonly string _dir;
    private readonly string _name;
    private readonly IDisposable _nameHeld;
    private readonly double _periodSeconds;
    private readonly string _affiliation;
    private readonly double _stale;
    private readonly VehicleTelemetryService _telemetry;
    private readonly bool _haveOrigin;
    private readonly GeoLocation _origin;
    private readonly SensorPlatformOptions? _platform;
    private readonly string? _runId, _scenarioId;
    private readonly long? _seed;

    private readonly OcclusionEstimator? _occlusion;
    private readonly IIlluminationSource? _illumination;
    private readonly RenderSetPairing? _renderSet;
    private readonly SensorPoseCheck? _sensorPose;
    private readonly double? _drawDistance;

    private readonly Channel<Arrival> _arrivals;
    private readonly Task _preparation;
    private readonly Channel<Job> _channel;
    private readonly Task[] _workers;
    private readonly SnapshotHold _snapshots;
    private readonly IDisposable _subscription;
    private readonly CaptureInstantClock _captureClock = new();

    private double _lastCaptureSimTime = double.NegativeInfinity;
    private Transform? _prevSensorTf;
    private double _prevSensorSimTime = double.NegativeInfinity;
    private long _saved, _dropped;
    private long _frameUnpaired;
    private long _illuminationPaired, _illuminationUnpaired;
    private long _solarBlockMissing;
    private long _drawDistanceCaptures, _beyondDrawDistance, _partlyBeyondDrawDistance;
    private long _supervisionPaired, _supervisionUnpaired;
    private long _lightsUnknown, _poseSourceUnknown;

    /// <summary>
    /// How long a capture waits for the declaration of its own frame when it arrives before the
    /// source has audited that frame. The image is read back from the GPU after the tick's snapshot
    /// is published, so it is not expected to wait at all; the bound is what keeps a frame the source
    /// will never answer for from holding a worker.
    /// </summary>
    private static readonly TimeSpan IlluminationWait = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Frames kept behind the last one prepared, past what the pairing needs: images arrive in frame
    /// order on one stream, so once an image is prepared no earlier frame of that stream will be asked
    /// for, and the depth capture paired to a still by simulation time in a free-running world is of a
    /// frame near the still's. Nothing is lost by keeping those few frames a moment longer.
    /// </summary>
    public const int FramesKeptBehind = 4;

    public long Saved => Interlocked.Read(ref _saved);
    public long Dropped => Interlocked.Read(ref _dropped);
    public bool HaveTelemetryOrigin => _haveOrigin;
    public string Directory => _dir;

    /// <summary>The recorded camera's name: every still's file name begins with it, and it is the
    /// callsign of the camera's platform track (<see cref="CameraName"/>).</summary>
    public string Name => _name;

    /// <summary>
    /// Stills dropped because the truth of their own frame was not to be had: the client held no snapshot
    /// of the image's frame when the image arrived -- the frame was never observed, or the image came so
    /// late that the client's capacity had dropped it -- or the frame's records could not be built. No
    /// such still is written, beside a neighbouring frame's truth or beside none; every still written
    /// carries its own frame's. A run's closeout holds this at zero.
    /// </summary>
    public long FrameUnpaired => Interlocked.Read(ref _frameUnpaired);

    /// <summary>Captures written with the illumination declaration of their own frame.</summary>
    public long IlluminationPaired => Interlocked.Read(ref _illuminationPaired);

    /// <summary>
    /// Captures written without one, although an illumination source was given: frames the source
    /// did not answer for. Every one is a still whose sun cannot be traced to a declaration.
    /// </summary>
    public long IlluminationUnpaired => Interlocked.Read(ref _illuminationUnpaired);

    /// <summary>
    /// Captures written without a solar block: no <c>_solar</c> element in the sidecar and no
    /// <c>carla:solar</c> chunk in the PNG, because the snapshot nearest the pixels carried no sun or
    /// fewer than the eleven values that make one. Every one is a still with no recorded sun and no
    /// illumination band, which a corpus can neither stratify nor replay, so a run's closeout holds it
    /// at zero.
    /// </summary>
    public long SolarBlockMissing => Interlocked.Read(ref _solarBlockMissing);

    /// <summary>Whether captures list their frame's render set rather than every vehicle actor.</summary>
    public bool PairsRenderSet => _renderSet is not null;

    /// <summary>Captures whose vehicle list is the render set of the frame their truth describes.</summary>
    public long RenderSetPaired => _renderSet?.Paired ?? 0;

    /// <summary>
    /// Captures written with no vehicle list, although a render-set source was given: frames whose
    /// set the source no longer held, or never had. Each such sidecar says <c>vehicles="unknown"</c>,
    /// so its empty list is not read as an empty scene.
    /// </summary>
    public long RenderSetUnpaired => _renderSet?.Unpaired ?? 0;

    /// <summary>Bodies a paired capture's render set held that no truth record described, summed
    /// over the captures.</summary>
    public long RenderSetBodiesMissing => _renderSet?.BodiesMissing ?? 0;

    /// <summary>
    /// Captures written with the supervision in force on their own frame, as the server carried it on
    /// that frame's snapshot: the plan and every drawn SUMO vehicle's state. Zero where no
    /// plan was in force, which a capture of such a frame says nothing about.
    /// </summary>
    public long SupervisionPaired => Interlocked.Read(ref _supervisionPaired);

    /// <summary>
    /// Captures written with their supervision unknown, although a plan was in force: the client could
    /// not read the supervision block of the image's own frame, or held no snapshot of the frame for a
    /// capture that carries no vehicle truth. Each such sidecar says <c>supervision="unknown"</c> and
    /// carries none, never a neighbouring frame's, so a run's closeout holds it at zero.
    /// </summary>
    public long SupervisionUnpaired => Interlocked.Read(ref _supervisionUnpaired);

    /// <summary>
    /// Captures written with <c>lights="unknown"</c>: a vehicle in the picture went without its lights
    /// because the snapshot of the capture's own frame did not carry them -- a server built before it
    /// did. No light is guessed, so a run's closeout holds this at zero.
    /// </summary>
    public long LightsUnknown => Interlocked.Read(ref _lightsUnknown);

    /// <summary>
    /// Captures written with <c>pose_source="unknown"</c>: a drawn SUMO vehicle in the picture went without
    /// its pose source because the snapshot of the capture's own frame did not carry one -- a server built
    /// before it did, or one that refused the session's -- or the frame came before the session declared
    /// its step. No pose source is guessed, so a run's closeout holds this at zero.
    /// </summary>
    public long PoseSourceUnknown => Interlocked.Read(ref _poseSourceUnknown);

    /// <summary>
    /// Captures whose image was rendered under a draw distance, and whose sidecar therefore states it
    /// and marks every vehicle beyond it.
    /// </summary>
    public long DrawDistanceCaptures => Interlocked.Read(ref _drawDistanceCaptures);

    /// <summary>
    /// Vehicle records marked wholly beyond the draw distance, summed over the captures: vehicles in
    /// the world and in the truth that the image does not show.
    /// </summary>
    public long VehiclesBeyondDrawDistance => Interlocked.Read(ref _beyondDrawDistance);

    /// <summary>
    /// Vehicle records the draw distance fell across, summed over the captures: vehicles the image may
    /// show without the parts of them beyond it.
    /// </summary>
    public long VehiclesPartlyBeyondDrawDistance => Interlocked.Read(ref _partlyBeyondDrawDistance);

    /// <summary>
    /// Whether captures carry a per-vehicle occlusion measurement. Every capture carries, for every
    /// vehicle, where its box fell against the picture (<c>in_frame</c>) and its apparent size, and for
    /// one unmeasured why (<c>occlusion_unmeasured</c>), whether or not a depth camera was given.
    /// </summary>
    public bool MeasuresOcclusion => _occlusion is not null;

    /// <summary>Captures whose vehicles were measured against a depth capture of the same instant.</summary>
    public long OcclusionMeasured => _occlusion?.Matched ?? 0;

    /// <summary>Captures left without occlusion because no usable depth capture matched them.</summary>
    public long OcclusionUnmatched => _occlusion?.Missed ?? 0;

    /// <summary>Of those, the ones that found no depth capture at all.</summary>
    public long OcclusionNoDepthCaptures => _occlusion?.MissedNoCaptures ?? 0;

    /// <summary>Of those, the ones whose depth captures were all of some other instant.</summary>
    public long OcclusionDepthOutOfStep => _occlusion?.MissedOutOfStep ?? 0;

    /// <summary>Of those, the ones whose depth capture was of the wrong pose.</summary>
    public long OcclusionDepthWrongPose => _occlusion?.MissedPose ?? 0;

    /// <summary>Whether each capture's pose is read from the snapshot of its own frame, with its image
    /// header checked against it (<see cref="SensorPoseCheck"/>), rather than taken from the header.</summary>
    public bool ChecksSensorPose => _sensorPose is not null;

    /// <summary>Captures whose platform pose is the camera's in the snapshot of their own frame.</summary>
    public long SensorPoseFromSnapshot => _sensorPose?.FromSnapshot ?? 0;

    /// <summary>
    /// Of those, the captures whose image header carried a different pose: each would have been
    /// written somewhere its image was not taken from had the header been trusted. A server that
    /// stamps the header when it captures the frame keeps this at zero.
    /// </summary>
    public long SensorPoseHeaderDisagreed => _sensorPose?.HeaderDisagreed ?? 0;

    /// <summary>Captures whose own frame's snapshot did not hold the camera -- or, for a capture that
    /// carries no vehicle truth, whose frame the client did not hold -- written with their header's pose
    /// unchecked.</summary>
    public long SensorPoseFromHeader => _sensorPose?.FromHeader ?? 0;

    /// <summary>Whether the depth capture occlusion is measured against is projected from the depth
    /// camera's pose in the snapshot of its own frame, with its header checked the same way.</summary>
    public bool ChecksDepthPose => _occlusion?.ChecksDepthPose ?? false;

    /// <summary>Matched depth captures projected from the depth camera's pose in the snapshot of their
    /// own frame.</summary>
    public long OcclusionDepthPoseFromSnapshot => _occlusion?.DepthPoseFromSnapshot ?? 0;

    /// <summary>Of those, the ones whose header carried a different pose; zero from a correct server.</summary>
    public long OcclusionDepthPoseHeaderDisagreed => _occlusion?.DepthPoseHeaderDisagreed ?? 0;

    /// <summary>Matched depth captures whose own frame the client did not hold with the depth camera in
    /// it, projected from their header's pose.</summary>
    public long OcclusionDepthPoseFromHeader => _occlusion?.DepthPoseFromHeader ?? 0;

    /// <param name="streamToken">The camera actor's StreamToken (24-byte sensor stream token).</param>
    /// <param name="hz">Captures per second (may be fractional). Decimated against sim time.</param>
    /// <param name="platform">Collection-platform options; when supplied (and a georeference origin is
    /// available) each capture records the camera as a CoT air track. Its field of view is the one
    /// every vehicle's box is projected into the picture with, so the sidecar's <c>in_frame</c> and
    /// apparent sizes agree with the intrinsics it writes; a recorder given none projects with the
    /// field of view each image's own header reports. Null disables the platform track.</param>
    /// <param name="runId">Identifier grouping every artifact produced by this execution. Recorded on
    /// each capture so stills and sidecars can be gathered back into a run after the fact.</param>
    /// <param name="scenarioId">The scenario driving this run, where there is one.</param>
    /// <param name="seed">Seed the run was started with, recorded so it can be reproduced.</param>
    /// <param name="depthStreamToken">StreamToken of a depth camera held at the recorded camera's
    /// pose and field of view. Supplying it adds a per-vehicle occlusion measurement to each capture,
    /// at the cost of a second subscription to that camera. Null leaves occlusion unmeasured, and
    /// every vehicle record says so (<c>occlusion_unmeasured="no_depth_camera"</c>).</param>
    /// <param name="occlusion">Tuning for that measurement; defaults when null.</param>
    /// <param name="illumination">What declared the sun this run is lit by. Supplying it writes, beside
    /// each capture's sun, the declaration for that capture's frame and the audit's residual on it,
    /// so the still's illumination is traceable to what the run said it should be. Null writes the
    /// sun alone.</param>
    /// <param name="renderSet">What lent the vehicle bodies this run renders, where they are lent from
    /// a pool. Supplying it lists, in each capture, only the bodies its frame rendered, each named by
    /// the vehicle it rendered (<c>sumo_id</c>), and leaves out every body parked between loans; a
    /// frame whose set is no longer held lists no vehicle and says so. Null lists, where the server
    /// published a session's render set with the frame, the bodies that set says the frame drew, each
    /// named by its vehicle; and otherwise every vehicle actor, which is right wherever each actor is
    /// its own vehicle.</param>
    /// <param name="cameraActorId">The recorded camera actor. Given, each capture's pose -- its
    /// platform point and boresight, and the pose its occlusion is measured from -- is the camera's
    /// in the snapshot of the image's own frame, with the image header's checked against it and
    /// counted (<see cref="SensorPoseCheck"/>). Null takes the pose from the header unchecked.</param>
    /// <param name="depthActorId">The depth camera actor, for the same check on the depth capture
    /// occlusion is measured against. Used only with <paramref name="depthStreamToken"/>.</param>
    /// <param name="drawDistanceMetres">The draw distance the run's vehicle bodies are drawn under,
    /// metres, for a recorder given no <paramref name="renderSet"/> source: each capture then states
    /// it and marks every vehicle beyond it, seen from the capture's camera, so a vehicle in the world
    /// that the image does not show is never listed as seen. Where a source is given, each frame's own
    /// set says what it was drawn under -- including that the server refused a distance, so nothing
    /// was culled -- and this is not read. Null marks nothing.</param>
    /// <param name="cameraName">The recorded camera's name (<see cref="CameraName"/>), as the server
    /// holds it (<see cref="CameraName.Of"/>): every still is written as
    /// <c>&lt;name&gt;_&lt;local capture time&gt;.png</c> and <c>.xml</c>, and the platform track's
    /// callsign is the name, so a <paramref name="platform"/> with another callsign is refused. Null
    /// takes the platform's callsign, or with no platform the default of
    /// <paramref name="cameraActorId"/>, <c>CARLA-SENSOR-&lt;id&gt;</c>, which is an unnamed camera's
    /// name only on a server built before it named cameras; a recorder given none of the three is
    /// refused. A name the rule refuses as a camera's (<see cref="CameraName.HeldProblem"/>) is refused
    /// here, and so is one another recorder in this process holds: the recorder holds its name until
    /// it is disposed.</param>
    /// <remarks>
    /// The supervision in force is given by no parameter: it is held on the server and carried on every
    /// world-observer snapshot, and each capture takes its own frame's from the snapshot its vehicles are
    /// read from (<see cref="CaptureSupervision"/>), so a recorder in any process writes the same.
    /// </remarks>
    public FrameRecorder(CarlaClient client, byte[] streamToken, string dir, double hz,
                         string affiliation = "n", double staleSeconds = 3.0,
                         SensorPlatformOptions? platform = null, int workers = 0,
                         string? runId = null, string? scenarioId = null, long? seed = null,
                         byte[]? depthStreamToken = null, OcclusionOptions? occlusion = null,
                         IIlluminationSource? illumination = null, IRenderSetSource? renderSet = null,
                         ActorId? cameraActorId = null, ActorId? depthActorId = null,
                         double? drawDistanceMetres = null, string? cameraName = null)
    {
        if (streamToken is not { Length: 24 })
            throw new ArgumentException("streamToken must be a 24-byte sensor stream token", nameof(streamToken));
        if (drawDistanceMetres is { } limit && (!double.IsFinite(limit) || limit <= 0.0))
            throw new ArgumentOutOfRangeException(nameof(drawDistanceMetres), limit,
                                                  "a draw distance is a positive number of meters, or null for none");
        // Every vehicle of every capture is projected into the picture with this field of view, so
        // one the projection cannot be made with is refused here rather than failing every capture.
        if (platform is not null && !(platform.HFovDeg > 0.0 && platform.HFovDeg < 180.0))
            throw new ArgumentOutOfRangeException(nameof(platform), platform.HFovDeg,
                                                  "the platform's horizontal field of view is between 0 and 180 degrees, exclusive");

        string name = cameraName ?? platform?.Callsign
                      ?? (cameraActorId is { } unnamed
                          ? CameraName.Default(unnamed)
                          : throw new ArgumentException(
                              "a recorder names every still after its camera: give the camera's name, or "
                              + "the camera actor to take its default name from", nameof(cameraName)));
        if (CameraName.HeldProblem(name, cameraActorId) is { } refused)
            throw new ArgumentException(refused, nameof(cameraName));
        if (platform is not null && platform.Callsign != name)
            throw new ArgumentException($"the platform track's callsign is the camera's name, and "
                                        + $"'{platform.Callsign}' is not '{name}'", nameof(platform));

        _name = name;
        _nameHeld = CameraName.Hold(name, cameraActorId, dir);
        try
        {
            _client = client;
            _dir = dir;
            _periodSeconds = 1.0 / Math.Max(0.01, hz);
            _affiliation = affiliation;
            _stale = staleSeconds;
            _platform = platform;
            // A run identifier is always present, so captures can be gathered back into a run even when
            // the caller supplied nothing. Derived from the start instant, which is unique enough per
            // recorder and reads plainly in a directory listing.
            _runId = string.IsNullOrEmpty(runId)
                ? "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                : runId;
            _scenarioId = scenarioId;
            _seed = seed;
            System.IO.Directory.CreateDirectory(dir);

            _telemetry = new VehicleTelemetryService(client);
            try { _origin = _telemetry.GetOrigin(); _haveOrigin = true; }
            catch { _haveOrigin = false; }

            if (depthStreamToken is not null)
                _occlusion = new OcclusionEstimator(client, depthStreamToken, occlusion, depthActorId);
            _illumination = illumination;
            _renderSet = renderSet is null ? null : new RenderSetPairing(renderSet);
            _sensorPose = cameraActorId is { } camera ? new SensorPoseCheck(client.GetSnapshotFrame, camera) : null;
            _drawDistance = drawDistanceMetres;

            int n = workers > 0 ? workers : Math.Max(2, Environment.ProcessorCount / 2);
            _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(Math.Max(4, n * 2))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = true,
            });
            _workers = new Task[n];
            for (int i = 0; i < n; i++) _workers[i] = Task.Run(WorkerLoopAsync);

            // Between the stream thread and the preparation task. Dropping when full is what keeps the
            // stream thread from ever blocking; a dropped frame is counted.
            _arrivals = Channel.CreateBounded<Arrival>(new BoundedChannelOptions(Math.Max(4, n * 2))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true,
            });
            _preparation = Task.Run(PreparationLoopAsync);

            // Taken before the subscription, so the frames of the images already in flight are held
            // when the first of them arrives.
            _snapshots = client.HoldSnapshotFrames();
            try
            {
                // Independent subscription to the camera stream (does not disturb the display listener).
                _subscription = client.SubscribeToStream(streamToken, OnFrame);
            }
            catch
            {
                _snapshots.Dispose();
                throw;
            }
        }
        catch
        {
            // A recorder that never started gives its camera's name back, or no later recorder in
            // this process could take it.
            _nameHeld.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs on the camera stream's thread, once per frame the server sends. Asks nothing of the server:
    /// see the remarks on the class for why that is a rule and not a preference.
    /// </summary>
    private void OnFrame(SensorFrame frame)
    {
        double t = frame.Header.Timestamp;
        if (t - _lastCaptureSimTime < _periodSeconds) return;
        _lastCaptureSimTime = t;

        ImageSensorData img;
        try { img = ImageSensorData.Deserialize(frame.Payload.Span); }
        catch { return; }

        int w = (int)img.Width, h = (int)img.Height;
        if (w <= 0 || h <= 0 || img.RawBgra.Length < (long)w * h * 4) return;
        // With no platform the header's field of view is the only one the vehicles can be projected
        // with, and an image whose header carries none usable is as malformed as one with no size.
        if (_platform is null && !(img.FovAngle > 0f && img.FovAngle < 180f)) return;

        // Solar state read lock-free from the world-observer cache (no RPC, no poll), now rather than on
        // the preparation task, so it is the sun of the tick nearest the pixels.
        IReadOnlyList<double> solar = _client.GetCachedSolarState();

        // RawBgra is already a private copy produced by Deserialize, so it can be handed on without
        // copying again.
        var arrival = new Arrival(_captureClock.Next(DateTime.UtcNow), frame.Header.Frame, t,
                                  frame.SensorTransform, w, h, img.FovAngle, img.RawBgra, solar);
        if (!_arrivals.Writer.TryWrite(arrival))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// Turns each arrival into a capture: its truth records, its occlusion measurement and its platform
    /// pose. One task, in arrival order, because the platform's course and speed come from the pose of
    /// the capture before; free to wait on the server, because it is not the thread reading a socket
    /// the server is waiting to write to.
    /// </summary>
    private async Task PreparationLoopAsync()
    {
        try
        {
            await foreach (Arrival arrival in _arrivals.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Job? job = Prepare(arrival);
                // Images arrive in frame order on one stream, so no earlier frame of this camera will
                // be asked for again: the client may drop the frames before this one, less the margin.
                _snapshots.Release(arrival.Frame > FramesKeptBehind ? arrival.Frame - FramesKeptBehind : 0);
                if (job is not null && !_channel.Writer.TryWrite(job))
                    Interlocked.Increment(ref _dropped);
            }
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// The capture an arrival becomes, or null where it becomes none: a still whose own frame's truth
    /// is not to be had is dropped and counted (<see cref="FrameUnpaired"/>).
    /// </summary>
    private Job? Prepare(Arrival arrival)
    {
        IReadOnlyList<VehicleTelemetry> recs = Array.Empty<VehicleTelemetry>();
        ObservedRenderSet servedRenderSet = ObservedRenderSet.None;
        ObservedSupervision servedSupervision = ObservedSupervision.None;
        ulong? supervisionFrame = null;
        if (_haveOrigin)
        {
            // The truth is read as of the frame named in this image's header, not as of whatever the
            // observer delivered last: the image is read back from the GPU asynchronously and arrives on
            // its own stream, so by now the newest snapshot is usually a tick or more past the pixels,
            // and it can be behind them when the observer thread was held up. Descriptions are cached,
            // so this is fast once every actor has been seen.
            IReadOnlyList<VehicleTelemetry>? atFrame;
            try
            {
                atFrame = _telemetry.ComputeAt(_origin, arrival.Frame, out servedRenderSet, out servedSupervision);
            }
            catch
            {
                atFrame = null;
            }

            // The frame's truth is not to be had: the client never held the frame, or dropped it before
            // this image arrived, or the frame's records could not be built. The nearest frame's truth
            // is another instant's, so the still is not written.
            if (atFrame is null)
            {
                Interlocked.Increment(ref _frameUnpaired);
                return null;
            }

            recs = atFrame;
            supervisionFrame = arrival.Frame;
        }

        // Where the bodies are lent from a pool, the world's vehicle actors are not the scene's
        // vehicles: a body between loans stands parked out of sight below the ground, and a lent one
        // renders whichever vehicle borrowed it. So the records are cut to the bodies the records'
        // own frame rendered, each named by its vehicle, before anything is measured against the
        // imagery -- a parked body is neither reported nor measured.
        SidecarVehicles vehicles = SidecarVehicles.World;
        double? drawDistance = _drawDistance;
        if (_renderSet is not null && _haveOrigin)
        {
            PairedTruth paired = _renderSet.Pair(recs, arrival.Frame);
            recs = paired.Records;
            vehicles = paired.Vehicles;
            // The frame's own set says what it was drawn under, so a distance the server refused,
            // or one changed during the run, is the one the image was rendered with.
            drawDistance = paired.DrawDistanceMetres;
        }
        else if (!servedRenderSet.IsEmpty)
        {
            // No source in this process, and the server published the session's render set with the
            // records' own frame: the records are already cut to the bodies that frame drew, each
            // named by its vehicle, so the sidecar says so rather than claiming every vehicle actor.
            vehicles = SidecarVehicles.Rendered;
        }

        // The supervision in force on the image's own frame, as the server carried it, from the same
        // read as the vehicles above, so a body's is the one it carried for the vehicle it drew then. A
        // capture with no vehicle truth reads the frame's snapshot for it alone, and where the client
        // does not hold the frame its supervision is unknown, never a neighbour's (CaptureSupervision.For).
        if (supervisionFrame is null
            && _client.GetSnapshotFrame(arrival.Frame, out _, out ObservedSupervision heldSupervision) is not null)
        {
            supervisionFrame = arrival.Frame;
            servedSupervision = heldSupervision;
        }

        CaptureSupervision supervision = CaptureSupervision.For(arrival.Frame, supervisionFrame, servedSupervision,
                                                                _client.GetCachedSupervision());
        if (supervision.State == SidecarSupervision.InForce)
            Interlocked.Increment(ref _supervisionPaired);
        else if (supervision.State == SidecarSupervision.Unknown)
            Interlocked.Increment(ref _supervisionUnpaired);

        // The pose these pixels were taken from: the camera in the snapshot of the image's own frame,
        // with the header checked against it rather than trusted, because a header stamped after the
        // read-back carried the pose of the frame after the image's (SensorPoseCheck). Everything
        // below that places the camera works from this one pose.
        Transform cameraPose = _sensorPose?.Resolve(arrival.Frame, arrival.HeaderTransform)
                               ?? arrival.HeaderTransform;

        // A body farther than the draw distance from this camera is in the world and in the truth,
        // and not in this image: each record says where it stood against the distance, seen from the
        // pose the pixels were taken from, so none beyond it is read as a vehicle the image shows.
        if (drawDistance is not null)
        {
            Interlocked.Increment(ref _drawDistanceCaptures);
            recs = DrawDistanceCheck.Mark(recs, cameraPose.Location, drawDistance, out int beyond,
                                          out int partly);
            Interlocked.Add(ref _beyondDrawDistance, beyond);
            Interlocked.Add(ref _partlyBeyondDrawDistance, partly);
        }

        // Where each vehicle's box fell against this picture, and how large it appears in it, from the
        // pose the pixels were taken from and the picture's own size and field of view: a fact for
        // every record, whether or not a depth camera is attached, so a record with no occlusion is
        // never read as a vehicle the image shows unhidden. The field of view is the platform's where
        // one was given, so the projection agrees with the intrinsics the sidecar writes. A vehicle
        // whose box fell in the picture gets its box too -- the pixel rectangles, the share outside the
        // picture, its range and tilt, and the box's corners converted as its own point was -- and no
        // other vehicle does, as the owner ruled.
        var picture = new PinholeCamera(cameraPose, arrival.Width, arrival.Height,
                                        _platform?.HFovDeg ?? arrival.HeaderHFovDeg);
        recs = _haveOrigin ? BoxProjector.Mark(recs, picture, _origin) : BoxProjector.Mark(recs, picture);

        // How much of each vehicle this camera can actually see. Occlusion belongs to the
        // (vehicle, camera) pair, so it is measured against the depth capture of THIS frame from THIS
        // pose; when none matches, the capture carries no occlusion rather than a stale one, and every
        // record says why it carries none.
        if (_occlusion is null)
        {
            recs = UnmeasuredOcclusion.Mark(recs, OcclusionUnmeasured.NoDepthCamera);
        }
        else if (recs.Count > 0)
        {
            try { recs = MeasureOcclusion(recs, arrival.Frame, arrival.SimTimeSeconds, cameraPose); }
            catch { }
        }

        // The collection platform, at the pose the pixels were taken from — same pixels, same tick.
        // Course/speed come from the delta to the previous captured frame's pose.
        SensorPose? sensor = null;
        if (_haveOrigin && _platform is not null)
        {
            double dt = _prevSensorTf is null ? 0.0 : (arrival.SimTimeSeconds - _prevSensorSimTime);
            try
            {
                sensor = _telemetry.ComputeSensorPose(_origin, cameraPose, _prevSensorTf, dt,
                                                      _platform, arrival.Width, arrival.Height);
            }
            catch { }
            _prevSensorTf = cameraPose;
            _prevSensorSimTime = arrival.SimTimeSeconds;
        }

        // Tick and simulation time come from the very frame that produced these pixels, so the still,
        // its truth sidecar and the simulation instant are bound together rather than correlated after
        // the fact by wall clock.
        var capture = new CaptureIdentity(arrival.Frame, arrival.SimTimeSeconds, _runId, _scenarioId, _seed);
        return new Job(arrival.CapturedUtc, arrival.Width, arrival.Height, arrival.Bgra, recs, arrival.Solar,
                       sensor, capture, vehicles, drawDistance, supervision);
    }

    /// <summary>
    /// Every record with its occlusion against the depth capture of the frame, or with why it has none:
    /// the pairing's failure for all of them where no capture paired, and otherwise, per vehicle, what
    /// its own geometry or its sampling says. The records are already marked with where their boxes
    /// fell against the picture and their apparent sizes, which the depth sampling does not change.
    /// </summary>
    private IReadOnlyList<VehicleTelemetry> MeasureOcclusion(
        IReadOnlyList<VehicleTelemetry> recs, ulong tick, double simTime, Transform cameraTransform)
    {
        var depth = _occlusion!.MatchTo(tick, simTime, cameraTransform, out OcclusionUnmeasured? unpaired);
        if (depth is null) return UnmeasuredOcclusion.Mark(recs, unpaired!.Value);

        var merged = new List<VehicleTelemetry>(recs.Count);
        foreach (var r in recs)
        {
            // A box behind the lens or outside the picture is sampled by nothing, and a vehicle wholly
            // beyond the draw distance is drawn in neither this image nor the depth capture, which
            // would read the ground behind it as an unobstructed view of it.
            if (UnmeasuredOcclusion.FromGeometry(r) is { } why)
            {
                merged.Add(r with { OcclusionUnmeasured = why });
                continue;
            }
            var measured = _occlusion.Sample(depth, new VehicleBox(r.Id, r.ActorTransform, r.BoundingBox),
                                             out OcclusionUnmeasured? unsampled);
            merged.Add(measured is { } m
                ? r with
                {
                    Occlusion = m.Fraction,
                    OcclusionLevel = m.Level,
                    OcclusionSamples = m.Samples,
                    OcclusionUnmeasured = null,
                }
                : r with { OcclusionUnmeasured = unsampled });
        }
        return merged;
    }

    private async Task WorkerLoopAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var job))
            {
                try
                {
                    IlluminationDeclaration? illumination = await DeclarationForAsync(job.Capture.Tick)
                        .ConfigureAwait(false);
                    string stem = CameraName.StillStem(_name, job.CapturedUtc);
                    // The still carries its sun, its declaration, its pose and its identity, and never
                    // any supervision: truth stays out of the observation artifact (doc 04 D4.20).
                    PngEncoder.WriteBgraToFile(job.Bgra, job.Width, job.Height,
                                               Path.Combine(_dir, stem + ".png"),
                                               SolarMetadata.PngTextChunks(job.Solar)
                                                   .Concat(illumination?.PngTextChunks() ?? [])
                                                   .Concat(SensorMetadata.PngTextChunks(job.Sensor))
                                                   .Concat(job.Capture.PngTextChunks()));
                    CotWriter.WriteToFile(Path.Combine(_dir, stem + ".xml"),
                                          job.CapturedUtc, job.Telemetry, _affiliation, _stale,
                                          job.Solar, job.Sensor, job.Capture, illumination, job.Vehicles,
                                          job.DrawDistance, job.Supervision);
                    // Both writers leave the sun out of a capture whose block holds none, which is
                    // right for the frame and wrong for the run: counted, so it is never silent.
                    if (!SolarMetadata.HasData(job.Solar))
                        Interlocked.Increment(ref _solarBlockMissing);
                    // Likewise a capture whose vehicles in the picture go without what the frame's
                    // snapshot did not carry: right for the frame, and counted for the run.
                    if (CotWriter.LightsUnknown(job.Telemetry))
                        Interlocked.Increment(ref _lightsUnknown);
                    if (CotWriter.PoseSourceUnknown(job.Telemetry))
                        Interlocked.Increment(ref _poseSourceUnknown);
                    Interlocked.Increment(ref _saved);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Recorder] write failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The declaration of a capture's own frame, waiting a bounded moment for a source that has not
    /// reached that frame yet, and counting a capture that goes without.
    /// </summary>
    private async Task<IlluminationDeclaration?> DeclarationForAsync(ulong frame)
    {
        if (_illumination is not { } source)
        {
            return null;
        }

        DateTime giveUp = DateTime.UtcNow + IlluminationWait;
        while (true)
        {
            if (source.TryGetDeclaration(frame, out IlluminationDeclaration declaration))
            {
                Interlocked.Increment(ref _illuminationPaired);
                return declaration;
            }

            // A source already past this frame without an answer for it will not have one later.
            if ((source.NewestFrame is { } newest && newest >= frame) || DateTime.UtcNow >= giveUp)
            {
                Interlocked.Increment(ref _illuminationUnpaired);
                return null;
            }

            await Task.Delay(2).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        try { _subscription.Dispose(); } catch { /* already gone */ }
        // Every frame already handed on is prepared and written: the preparation task completes the
        // encoding channel when it has drained the arrivals, and the workers finish on that. The
        // snapshots are held until then, so the last arrivals still find their frames.
        _arrivals.Writer.TryComplete();
        try { _preparation.Wait(TimeSpan.FromSeconds(10)); } catch { /* best-effort flush */ }
        _snapshots.Dispose();
        _channel.Writer.TryComplete();
        _occlusion?.Dispose();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(10)); } catch { /* best-effort flush */ }
        // Given back once the stills written under it are flushed, so a recorder started next under the
        // same name does not write while this one still is.
        _nameHeld.Dispose();
    }
}
