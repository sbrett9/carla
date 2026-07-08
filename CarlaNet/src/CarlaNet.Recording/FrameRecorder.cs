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
                              IReadOnlyList<double> Solar);

    private readonly CarlaClient _client;
    private readonly string _dir;
    private readonly double _periodSeconds;
    private readonly string _affiliation;
    private readonly double _stale;
    private readonly VehicleTelemetryService _telemetry;
    private readonly bool _haveOrigin;
    private readonly GeoLocation _origin;

    private readonly Channel<Job> _channel;
    private readonly Task[] _workers;
    private readonly IDisposable _subscription;

    private double _lastCaptureSimTime = double.NegativeInfinity;
    private long _saved, _dropped;

    public long Saved => Interlocked.Read(ref _saved);
    public long Dropped => Interlocked.Read(ref _dropped);
    public bool HaveTelemetryOrigin => _haveOrigin;
    public string Directory => _dir;

    /// <param name="streamToken">The camera actor's StreamToken (24-byte sensor stream token).</param>
    /// <param name="hz">Captures per second (may be fractional). Decimated against sim time.</param>
    public FrameRecorder(CarlaClient client, byte[] streamToken, string dir, double hz,
                         string affiliation = "n", double staleSeconds = 3.0, int workers = 0)
    {
        if (streamToken is not { Length: 24 })
            throw new ArgumentException("streamToken must be a 24-byte sensor stream token", nameof(streamToken));

        _client = client;
        _dir = dir;
        _periodSeconds = 1.0 / Math.Max(0.01, hz);
        _affiliation = affiliation;
        _stale = staleSeconds;
        System.IO.Directory.CreateDirectory(dir);

        _telemetry = new VehicleTelemetryService(client);
        try { _origin = _telemetry.GetOrigin(); _haveOrigin = true; }
        catch { _haveOrigin = false; }

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

        // Solar state paired to this tick, read lock-free from the world-observer cache (no RPC, no
        // poll) — the same snapshot the telemetry above came from.
        IReadOnlyList<double> solar = _client.GetCachedSolarState();

        // RawBgra is already a private copy produced by Deserialize, so we can hand it to the worker
        // without copying again.
        var job = new Job(captured, w, h, img.RawBgra, recs, solar);
        if (!_channel.Writer.TryWrite(job))
            Interlocked.Increment(ref _dropped);
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
                                               SolarMetadata.PngTextChunks(job.Solar));
                    CotWriter.WriteToFile(Path.Combine(_dir, stem + ".xml"),
                                          job.CapturedUtc, job.Telemetry, _affiliation, _stale, job.Solar);
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
        _channel.Writer.TryComplete();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(10)); } catch { /* best-effort flush */ }
    }
}
