using System.Globalization;
using System.Threading.Channels;
using CarlaNet.Sensors;
using CarlaNet.Transport;
using CarlaNet.Transport.Streaming;
using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>
/// Native capture-to-disk recorder. Subscribes to a camera's sensor stream, decimates to a target rate,
/// and for each captured frame writes a lossless PNG of the imagery plus a CoT-XML telemetry sidecar
/// (paired by filename stem). All decoding/encoding/IO happens on the .NET thread pool — the frame
/// buffer never crosses to Python and the GIL is never held, so the viewer stays smooth while recording.
///
/// Construction starts recording; <see cref="Dispose"/> stops it (flushes pending captures).
/// </summary>
/// <remarks>
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
    /// <summary>A decimated frame as the stream thread hands it on: nothing asked of the server yet.</summary>
    private sealed record Arrival(DateTime CapturedUtc, ulong Frame, double SimTimeSeconds,
                                  Transform SensorTransform, int Width, int Height,
                                  ReadOnlyMemory<byte> Bgra, IReadOnlyList<double> Solar);

    private sealed record Job(DateTime CapturedUtc, int Width, int Height,
                              ReadOnlyMemory<byte> Bgra, IReadOnlyList<VehicleTelemetry> Telemetry,
                              IReadOnlyList<double> Solar, SensorPose? Sensor,
                              CaptureIdentity Capture, SidecarVehicles Vehicles);

    private readonly CarlaClient _client;
    private readonly string _dir;
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

    private readonly Channel<Arrival> _arrivals;
    private readonly Task _preparation;
    private readonly Channel<Job> _channel;
    private readonly Task[] _workers;
    private readonly IDisposable _subscription;
    private readonly CaptureInstantClock _captureClock = new();

    private double _lastCaptureSimTime = double.NegativeInfinity;
    private Transform? _prevSensorTf;
    private double _prevSensorSimTime = double.NegativeInfinity;
    private long _saved, _dropped;
    private long _telemetryExact, _telemetryOffset, _telemetryWorstOffset;
    private long _illuminationPaired, _illuminationUnpaired;

    /// <summary>
    /// How long a capture waits for the declaration of its own frame when it arrives before the
    /// source has audited that frame. The image is read back from the GPU after the tick's snapshot
    /// is published, so it is not expected to wait at all; the bound is what keeps a frame the source
    /// will never answer for from holding a worker.
    /// </summary>
    private static readonly TimeSpan IlluminationWait = TimeSpan.FromMilliseconds(500);

    public long Saved => Interlocked.Read(ref _saved);
    public long Dropped => Interlocked.Read(ref _dropped);
    public bool HaveTelemetryOrigin => _haveOrigin;
    public string Directory => _dir;

    /// <summary>Captures whose truth records came from the very frame that produced the pixels.</summary>
    public long TelemetryTickExact => Interlocked.Read(ref _telemetryExact);

    /// <summary>Captures whose truth records came from a neighbouring frame, because the client no longer
    /// held the image's own frame when the image arrived. Each such sidecar says which frame it got.</summary>
    public long TelemetryTickOffset => Interlocked.Read(ref _telemetryOffset);

    /// <summary>The largest distance, in frames, between a capture's pixels and its truth records.</summary>
    public long TelemetryTickWorstOffset => Interlocked.Read(ref _telemetryWorstOffset);

    /// <summary>Captures written with the illumination declaration of their own frame.</summary>
    public long IlluminationPaired => Interlocked.Read(ref _illuminationPaired);

    /// <summary>
    /// Captures written without one, although an illumination source was given: frames the source
    /// did not answer for. Every one is a still whose sun cannot be traced to a declaration.
    /// </summary>
    public long IlluminationUnpaired => Interlocked.Read(ref _illuminationUnpaired);

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

    /// <summary>Whether captures carry a per-vehicle occlusion measurement.</summary>
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

    /// <param name="streamToken">The camera actor's StreamToken (24-byte sensor stream token).</param>
    /// <param name="hz">Captures per second (may be fractional). Decimated against sim time.</param>
    /// <param name="platform">Collection-platform options; when supplied (and a georeference origin is
    /// available) each capture records the camera as a CoT air track. Null disables the platform track.</param>
    /// <param name="runId">Identifier grouping every artifact produced by this execution. Recorded on
    /// each capture so stills and sidecars can be gathered back into a run after the fact.</param>
    /// <param name="scenarioId">The scenario driving this run, where there is one.</param>
    /// <param name="seed">Seed the run was started with, recorded so it can be reproduced.</param>
    /// <param name="depthStreamToken">StreamToken of a depth camera held at the recorded camera's
    /// pose and field of view. Supplying it adds a per-vehicle occlusion measurement to each capture,
    /// at the cost of a second subscription to that camera. Null leaves occlusion unmeasured.</param>
    /// <param name="occlusion">Tuning for that measurement; defaults when null.</param>
    /// <param name="illumination">What declared the sun this run is lit by. Supplying it writes, beside
    /// each capture's sun, the declaration for that capture's frame and the audit's residual on it,
    /// so the still's illumination is traceable to what the run said it should be. Null writes the
    /// sun alone.</param>
    /// <param name="renderSet">What lent the vehicle bodies this run renders, where they are lent from
    /// a pool. Supplying it lists, in each capture, only the bodies its frame rendered, each named by
    /// the vehicle it rendered (<c>sumo_id</c>), and leaves out every body parked between loans; a
    /// frame whose set is no longer held lists no vehicle and says so. Null lists every vehicle actor,
    /// which is right wherever each actor is its own vehicle.</param>
    public FrameRecorder(CarlaClient client, byte[] streamToken, string dir, double hz,
                         string affiliation = "n", double staleSeconds = 3.0,
                         SensorPlatformOptions? platform = null, int workers = 0,
                         string? runId = null, string? scenarioId = null, long? seed = null,
                         byte[]? depthStreamToken = null, OcclusionOptions? occlusion = null,
                         IIlluminationSource? illumination = null, IRenderSetSource? renderSet = null)
    {
        if (streamToken is not { Length: 24 })
            throw new ArgumentException("streamToken must be a 24-byte sensor stream token", nameof(streamToken));

        _client = client;
        _dir = dir;
        _periodSeconds = 1.0 / Math.Max(0.01, hz);
        _affiliation = affiliation;
        _stale = staleSeconds;
        _platform = platform;
        // A run identifier is always present, so captures can be gathered back into a run even when the
        // caller supplied nothing. Derived from the start instant, which is unique enough per recorder
        // and reads plainly in a directory listing.
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
            _occlusion = new OcclusionEstimator(client, depthStreamToken, occlusion);
        _illumination = illumination;
        _renderSet = renderSet is null ? null : new RenderSetPairing(renderSet);

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

        // Independent subscription to the camera stream (does not disturb the display listener).
        _subscription = client.SubscribeToStream(streamToken, OnFrame);
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

        // Solar state read lock-free from the world-observer cache (no RPC, no poll), now rather than on
        // the preparation task, so it is the sun of the tick nearest the pixels.
        IReadOnlyList<double> solar = _client.GetCachedSolarState();

        // RawBgra is already a private copy produced by Deserialize, so it can be handed on without
        // copying again.
        var arrival = new Arrival(_captureClock.Next(DateTime.UtcNow), frame.Header.Frame, t,
                                  frame.SensorTransform, w, h, img.RawBgra, solar);
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
                Job job = Prepare(arrival);
                if (!_channel.Writer.TryWrite(job))
                    Interlocked.Increment(ref _dropped);
            }
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private Job Prepare(Arrival arrival)
    {
        IReadOnlyList<VehicleTelemetry> recs = Array.Empty<VehicleTelemetry>();
        ulong? telemetryTick = null;
        if (_haveOrigin)
        {
            // The truth is read as of the frame named in this image's header, not as of whatever the
            // observer delivered last: the image is read back from the GPU asynchronously and arrives on
            // its own stream, so by now the newest snapshot is usually a tick or more past the pixels,
            // and it can be behind them when the observer thread was held up. Descriptions are cached,
            // so this is fast once every actor has been seen.
            try
            {
                recs = _telemetry.Compute(_origin, arrival.Frame, out ulong served);
                telemetryTick = served;
                if (served == arrival.Frame)
                    Interlocked.Increment(ref _telemetryExact);
                else
                {
                    Interlocked.Increment(ref _telemetryOffset);
                    long gap = (long)(served > arrival.Frame ? served - arrival.Frame : arrival.Frame - served);
                    long worst;
                    while (gap > (worst = Interlocked.Read(ref _telemetryWorstOffset))
                           && Interlocked.CompareExchange(ref _telemetryWorstOffset, gap, worst) != worst) { }
                }
            }
            catch { }
        }

        // Where the bodies are lent from a pool, the world's vehicle actors are not the scene's
        // vehicles: a body between loans stands parked out of sight below the ground, and a lent one
        // renders whichever vehicle borrowed it. So the records are cut to the bodies the records'
        // own frame rendered, each named by its vehicle, before anything is measured against the
        // imagery -- a parked body is neither reported nor measured.
        SidecarVehicles vehicles = SidecarVehicles.World;
        if (_renderSet is not null && telemetryTick is { } described)
        {
            PairedTruth paired = _renderSet.Pair(recs, described);
            recs = paired.Records;
            vehicles = paired.Vehicles;
        }

        // How much of each vehicle this camera can actually see. Occlusion belongs to the
        // (vehicle, camera) pair, so it is measured against the depth capture of THIS frame from THIS
        // pose; when none matches, the capture simply carries no occlusion rather than a stale one.
        if (_occlusion is not null && recs.Count > 0)
        {
            try { recs = MeasureOcclusion(recs, arrival.Frame, arrival.SimTimeSeconds, arrival.SensorTransform); }
            catch { }
        }

        // The collection platform, derived from THIS frame's header transform — same pixels, same tick.
        // Course/speed come from the delta to the previous captured frame's pose.
        SensorPose? sensor = null;
        if (_haveOrigin && _platform is not null)
        {
            double dt = _prevSensorTf is null ? 0.0 : (arrival.SimTimeSeconds - _prevSensorSimTime);
            try
            {
                sensor = _telemetry.ComputeSensorPose(_origin, arrival.SensorTransform, _prevSensorTf, dt,
                                                      _platform, arrival.Width, arrival.Height);
            }
            catch { }
            _prevSensorTf = arrival.SensorTransform;
            _prevSensorSimTime = arrival.SimTimeSeconds;
        }

        // Tick and simulation time come from the very frame that produced these pixels, so the still,
        // its truth sidecar and the simulation instant are bound together rather than correlated after
        // the fact by wall clock.
        var capture = new CaptureIdentity(arrival.Frame, arrival.SimTimeSeconds, _runId, _scenarioId, _seed,
                                          telemetryTick);
        return new Job(arrival.CapturedUtc, arrival.Width, arrival.Height, arrival.Bgra, recs, arrival.Solar,
                       sensor, capture, vehicles);
    }

    private IReadOnlyList<VehicleTelemetry> MeasureOcclusion(
        IReadOnlyList<VehicleTelemetry> recs, ulong tick, double simTime, Transform cameraTransform)
    {
        var depth = _occlusion!.MatchTo(tick, simTime, cameraTransform);
        if (depth is null) return recs;

        var boxes = new List<VehicleBox>(recs.Count);
        foreach (var r in recs) boxes.Add(new VehicleBox(r.Id, r.ActorTransform, r.BoundingBox));
        var measured = _occlusion.Estimate(depth, boxes);
        if (measured.Count == 0) return recs;

        var merged = new List<VehicleTelemetry>(recs.Count);
        foreach (var r in recs)
            merged.Add(measured.TryGetValue(r.Id, out var m)
                ? r with
                {
                    Occlusion = m.Fraction,
                    OcclusionLevel = m.Level,
                    OcclusionSamples = m.Samples,
                    ApparentWidthPx = m.ApparentWidthPx,
                    ApparentHeightPx = m.ApparentHeightPx,
                }
                : r);
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
                    string stem = "SCTMV_" + job.CapturedUtc.ToLocalTime()
                        .ToString("yyyy.MM.dd_HH.mm.ss.fff", CultureInfo.InvariantCulture);
                    PngEncoder.WriteBgraToFile(job.Bgra, job.Width, job.Height,
                                               Path.Combine(_dir, stem + ".png"),
                                               SolarMetadata.PngTextChunks(job.Solar)
                                                   .Concat(illumination?.PngTextChunks() ?? [])
                                                   .Concat(SensorMetadata.PngTextChunks(job.Sensor))
                                                   .Concat(job.Capture.PngTextChunks()));
                    CotWriter.WriteToFile(Path.Combine(_dir, stem + ".xml"),
                                          job.CapturedUtc, job.Telemetry, _affiliation, _stale,
                                          job.Solar, job.Sensor, job.Capture, illumination, job.Vehicles);
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
        // encoding channel when it has drained the arrivals, and the workers finish on that.
        _arrivals.Writer.TryComplete();
        try { _preparation.Wait(TimeSpan.FromSeconds(10)); } catch { /* best-effort flush */ }
        _channel.Writer.TryComplete();
        _occlusion?.Dispose();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(10)); } catch { /* best-effort flush */ }
    }
}
