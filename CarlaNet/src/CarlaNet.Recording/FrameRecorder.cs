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
public sealed class FrameRecorder : IDisposable
{
    private sealed record Job(DateTime CapturedUtc, int Width, int Height,
                              ReadOnlyMemory<byte> Bgra, IReadOnlyList<VehicleTelemetry> Telemetry,
                              IReadOnlyList<double> Solar, SensorPose? Sensor,
                              CaptureIdentity Capture);

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

    private readonly Channel<Job> _channel;
    private readonly Task[] _workers;
    private readonly IDisposable _subscription;

    private double _lastCaptureSimTime = double.NegativeInfinity;
    private Transform? _prevSensorTf;
    private double _prevSensorSimTime = double.NegativeInfinity;
    private long _saved, _dropped;

    public long Saved => Interlocked.Read(ref _saved);
    public long Dropped => Interlocked.Read(ref _dropped);
    public bool HaveTelemetryOrigin => _haveOrigin;
    public string Directory => _dir;

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
    public FrameRecorder(CarlaClient client, byte[] streamToken, string dir, double hz,
                         string affiliation = "n", double staleSeconds = 3.0,
                         SensorPlatformOptions? platform = null, int workers = 0,
                         string? runId = null, string? scenarioId = null, long? seed = null,
                         byte[]? depthStreamToken = null, OcclusionOptions? occlusion = null)
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

        int n = workers > 0 ? workers : Math.Max(2, Environment.ProcessorCount / 2);
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(Math.Max(4, n * 2))
        {
            FullMode = BoundedChannelFullMode.DropWrite,   // never block the stream-reader thread
            SingleReader = false,
            SingleWriter = true,
        });
        _workers = new Task[n];
        for (int i = 0; i < n; i++) _workers[i] = Task.Run(WorkerLoopAsync);

        // Independent subscription to the camera stream (does not disturb the display listener).
        _subscription = client.SubscribeToStream(streamToken, OnFrame);
    }

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

        var captured = DateTime.UtcNow;
        IReadOnlyList<VehicleTelemetry> recs = Array.Empty<VehicleTelemetry>();
        if (_haveOrigin)
        {
            // Cache reads (transforms/velocities) are fresh for this frame; descriptions are cached, so
            // this is fast and keeps the still and its truth labels paired.
            try { recs = _telemetry.Compute(_origin); } catch { }
        }

        // How much of each vehicle this camera can actually see. Occlusion belongs to the
        // (vehicle, camera) pair, so it is measured against the depth capture of THIS frame from THIS
        // pose; when none matches, the capture simply carries no occlusion rather than a stale one.
        if (_occlusion is not null && recs.Count > 0)
        {
            try { recs = MeasureOcclusion(recs, frame.Header.Frame, t, frame.SensorTransform); }
            catch { }
        }

        // Solar state paired to this tick, read lock-free from the world-observer cache (no RPC, no
        // poll) — the same snapshot the telemetry above came from.
        IReadOnlyList<double> solar = _client.GetCachedSolarState();

        // The collection platform, derived from THIS frame's header transform — same pixels, same tick.
        // Course/speed come from the delta to the previous captured frame's pose.
        SensorPose? sensor = null;
        if (_haveOrigin && _platform is not null)
        {
            double dt = _prevSensorTf is null ? 0.0 : (t - _prevSensorSimTime);
            try { sensor = _telemetry.ComputeSensorPose(_origin, frame.SensorTransform, _prevSensorTf, dt, _platform, w, h); }
            catch { }
            _prevSensorTf = frame.SensorTransform;
            _prevSensorSimTime = t;
        }

        // Tick and simulation time come from the very frame that produced these pixels, so the still,
        // its truth sidecar and the simulation instant are bound together rather than correlated after
        // the fact by wall clock.
        var capture = new CaptureIdentity(frame.Header.Frame, t, _runId, _scenarioId, _seed);

        // RawBgra is already a private copy produced by Deserialize, so we can hand it to the worker
        // without copying again.
        var job = new Job(captured, w, h, img.RawBgra, recs, solar, sensor, capture);
        if (!_channel.Writer.TryWrite(job))
            Interlocked.Increment(ref _dropped);
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
                    string stem = "SCTMV_" + job.CapturedUtc.ToLocalTime()
                        .ToString("yyyy.MM.dd_HH.mm.ss.fff", CultureInfo.InvariantCulture);
                    PngEncoder.WriteBgraToFile(job.Bgra, job.Width, job.Height,
                                               Path.Combine(_dir, stem + ".png"),
                                               SolarMetadata.PngTextChunks(job.Solar)
                                                   .Concat(SensorMetadata.PngTextChunks(job.Sensor))
                                                   .Concat(job.Capture.PngTextChunks()));
                    CotWriter.WriteToFile(Path.Combine(_dir, stem + ".xml"),
                                          job.CapturedUtc, job.Telemetry, _affiliation, _stale,
                                          job.Solar, job.Sensor, job.Capture);
                    Interlocked.Increment(ref _saved);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Recorder] write failed: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        try { _subscription.Dispose(); } catch { /* already gone */ }
        _occlusion?.Dispose();
        _channel.Writer.TryComplete();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(10)); } catch { /* best-effort flush */ }
    }
}
