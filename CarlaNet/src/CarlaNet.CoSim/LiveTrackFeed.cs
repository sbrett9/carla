using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarlaNet.Recording;

namespace CarlaNet.CoSim;

/// <summary>
/// A session's live track: on each sampled frame, one Cursor-on-Target event per body the frame drew,
/// built from the truth a capture of that frame records, for a caller to send on.
/// </summary>
/// <remarks>
/// <para><b>The sidecar's event, from the sidecar's truth.</b> A sampled frame's records are read as a
/// recorder reads a capture's (<see cref="ICarlaWorld.VehicleTruthAt"/>), cut to the bodies the frame
/// drew, each named by its SUMO vehicle (<see cref="RenderSet.Select"/>, the frame's own render set),
/// and written by the sidecar's own writer (<see cref="CotWriter.StreamEvent"/>). So each event is the
/// one a capture of the frame writes for the vehicle, without the fields the feed withholds, with the
/// frame's civil instant in UTC as its <c>time</c> (<see cref="IlluminationDeclaration.CivilInstant"/>)
/// and its <c>stale</c> that instant plus <see cref="LiveTrackOptions.StaleSeconds"/>. Its type carries
/// the affiliation of the vehicle's population (<see cref="LiveTrackOptions.AffiliationOf"/>), asked of
/// SUMO once per vehicle type and only where the options map any population. A vehicle with no body on
/// the frame is in no sample.</para>
///
/// <para><b>Which frames.</b> The newest frame of each interval of <see cref="TicksPerSample"/> world
/// ticks, counted from the session's first tick: tick index <c>i</c> where <c>(i + 1)</c> is a multiple
/// of it. Frames of the prewarm are sampled as well, and say they are not the window's.</para>
///
/// <para><b>Where the work runs.</b> In <see cref="OnFrameRendered"/>, on the tick thread, for the frame
/// the tick has just produced: the client holds it, so its truth is read with no round trip but a
/// description for a body seen for the first time. A sampled frame whose truth, render set or civil
/// instant is not to be had makes no sample and is counted (<see cref="FramesUnpaired"/>).</para>
///
/// <para><b>Taking the samples.</b> Completed samples wait, up to <see cref="Capacity"/>, until the caller
/// takes them (<see cref="TakeSamplesJson"/>); beyond that the oldest is dropped and counted
/// (<see cref="SamplesDropped"/>). Taken between advances, from the thread that advances the session, as
/// every other call on it is.</para>
/// </remarks>
public sealed class LiveTrackFeed : ISumoStepObserver
{
    /// <summary>How many completed samples wait to be taken before the oldest is dropped.</summary>
    public const int Capacity = 64;

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly LiveTrackOptions _options;
    private readonly ICarlaWorld _world;
    private readonly Func<string, string> _classOf;
    private readonly Dictionary<string, string> _populationOf = new(StringComparer.Ordinal);
    private readonly Queue<Sample> _waiting = new();
    private readonly object _gate = new();
    private long _samples, _framesUnpaired, _bodiesMissing, _samplesDropped;

    /// <param name="options">What the feed sends.</param>
    /// <param name="world">The world whose frames are sampled.</param>
    /// <param name="worldDeltaSeconds">The world's fixed delta, which the rate is counted in.</param>
    /// <param name="classOf">
    /// The catalogue class a SUMO vehicle type names (<see cref="VehicleCatalogue.ClassParameter"/>), or
    /// an empty string where it names none.
    /// </param>
    internal LiveTrackFeed(LiveTrackOptions options, ICarlaWorld world, double worldDeltaSeconds,
                           Func<string, string> classOf)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(classOf);
        _options = options;
        _world = world;
        _classOf = classOf;
        TicksPerSample = TicksPerSampleAt(options.RateHz, worldDeltaSeconds);
        SampleIntervalSeconds = TicksPerSample * worldDeltaSeconds;
    }

    /// <summary>What the feed sends.</summary>
    public LiveTrackOptions Options => _options;

    /// <summary>World ticks from one sampled frame to the next.</summary>
    public long TicksPerSample { get; }

    /// <summary>Simulated seconds from one sampled frame to the next.</summary>
    public double SampleIntervalSeconds { get; }

    /// <summary>Samples completed, whether taken since, still waiting or dropped.</summary>
    public long Samples
    {
        get
        {
            lock (_gate)
            {
                return _samples;
            }
        }
    }

    /// <summary>
    /// Sampled frames that made no sample, because the client no longer held the frame's truth, the
    /// truth could not be read, or the session recorded no render set or civil instant for the frame.
    /// </summary>
    public long FramesUnpaired
    {
        get
        {
            lock (_gate)
            {
                return _framesUnpaired;
            }
        }
    }

    /// <summary>
    /// Bodies a sampled frame drew that its truth held no record of, summed over the samples: vehicles in
    /// the picture with no event.
    /// </summary>
    public long BodiesMissing
    {
        get
        {
            lock (_gate)
            {
                return _bodiesMissing;
            }
        }
    }

    /// <summary>Samples dropped untaken because <see cref="Capacity"/> others were already waiting.</summary>
    public long SamplesDropped
    {
        get
        {
            lock (_gate)
            {
                return _samplesDropped;
            }
        }
    }

    /// <summary>
    /// World ticks per sample at a rate: <c>max(1, round(1 / (rate × delta)))</c>, rounded half to even.
    /// </summary>
    /// <param name="rateHz">Samples per simulated second.</param>
    /// <param name="worldDeltaSeconds">The world's fixed delta, seconds.</param>
    public static long TicksPerSampleAt(double rateHz, double worldDeltaSeconds)
    {
        double ticks = Math.Round(1.0 / (rateHz * worldDeltaSeconds), MidpointRounding.ToEven);
        // A rate so low that its interval overflows the count samples no frame of any run.
        return ticks < 1.0 ? 1 : ticks < 9e18 ? (long)ticks : long.MaxValue;
    }

    /// <summary>
    /// Every sample completed since the last call, oldest first, as a JSON array, and none left waiting.
    /// </summary>
    /// <remarks>
    /// <para>Each sample is an object:</para>
    /// <list type="bullet">
    /// <item><c>frame</c>: the world frame sampled.</item>
    /// <item><c>sim_time_s</c>: its simulated second on the scenario's clock, as a sidecar's
    /// <c>sim_time_s</c>.</item>
    /// <item><c>time_utc</c>: its civil instant in UTC, spelled as each event's <c>time</c>.</item>
    /// <item><c>in_window</c>: false for a frame of the prewarm, before the window opens.</item>
    /// <item><c>camera</c>: <c>none</c>, no camera being followed.</item>
    /// <item><c>camera_id</c>: null.</item>
    /// <item><c>events</c>: one object per body the frame drew, in the order of the frame's truth records:
    /// <c>sumo_id</c>, the vehicle; <c>in_view</c>, null; and <c>xml</c>, its event, one line with no
    /// XML declaration, to send as one UDP datagram.</item>
    /// </list>
    /// </remarks>
    public string TakeSamplesJson()
    {
        Sample[] taken;
        lock (_gate)
        {
            taken = [.. _waiting];
            _waiting.Clear();
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, JsonOptions))
        {
            json.WriteStartArray();
            foreach (Sample sample in taken)
            {
                json.WriteStartObject();
                json.WriteNumber("frame", sample.Frame);
                json.WritePropertyName("sim_time_s");
                json.WriteRawValue(sample.SimulatedSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                                   skipInputValidation: true);
                json.WriteString("time_utc", CotWriter.Timestamp(sample.StampedUtc));
                json.WriteBoolean("in_window", sample.InWindow);
                json.WriteString("camera", "none");
                json.WriteNull("camera_id");
                json.WriteStartArray("events");
                foreach (Event vehicle in sample.Events)
                {
                    json.WriteStartObject();
                    json.WriteString("sumo_id", vehicle.SumoId);
                    json.WriteNull("in_view");
                    json.WriteString("xml", vehicle.Xml);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc/>
    public void OnSumoStep(SumoStepRecord step)
    {
    }

    /// <inheritdoc/>
    public void OnFrameRendered(RenderedFrameRecord frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if ((frame.TickIndex + 1) % TicksPerSample != 0)
        {
            return;
        }

        Sample? sample = Take(frame, out int missing);
        lock (_gate)
        {
            _bodiesMissing += missing;
            if (sample is null)
            {
                _framesUnpaired++;
                return;
            }

            _samples++;
            if (_waiting.Count == Capacity)
            {
                _waiting.Dequeue();
                _samplesDropped++;
            }

            _waiting.Enqueue(sample);
        }
    }

    /// <inheritdoc/>
    public void OnSessionEnded(SessionEndRecord end)
    {
    }

    /// <summary>The sample a frame makes, or null where its truth is not to be had.</summary>
    private Sample? Take(RenderedFrameRecord frame, out int missing)
    {
        missing = 0;
        if (frame.RenderSet is not { } renderSet || frame.Illumination?.CivilInstant is not { } civil)
        {
            return null;
        }

        IReadOnlyList<VehicleTelemetry>? truth;
        try
        {
            truth = _world.VehicleTruthAt(frame.Frame);
        }
        catch (Exception failed) when (failed is not WorldConnectionLostException)
        {
            // As a recorder treats a capture whose records could not be built: no sample, and counted.
            // A lost connection stops the run where it is found.
            truth = null;
        }

        if (truth is null)
        {
            return null;
        }

        IReadOnlyList<VehicleTelemetry> records = renderSet.Select(truth, out missing);
        DateTime stamped = civil.UtcDateTime;
        var events = new List<Event>(records.Count);
        foreach (VehicleTelemetry record in records)
        {
            RenderedVehicle vehicle = record.Rendered!;
            string affiliation = _options.AffiliationByPopulation.Count == 0
                ? _options.Affiliation
                : _options.AffiliationOf(PopulationOf(vehicle.VehicleTypeId));
            events.Add(new Event(vehicle.SumoId,
                                 CotWriter.StreamEvent(record, affiliation, _options.UidPrefix, stamped,
                                                       _options.StaleSeconds)));
        }

        return new Sample(frame.Frame, frame.SimulatedTimeSeconds, stamped, frame.InWindow, events);
    }

    /// <summary>
    /// A vehicle type's population: the class it names, or the type itself where it names none. Asked of
    /// SUMO once per type, since a type's parameters do not change during a run.
    /// </summary>
    private string PopulationOf(string typeId)
    {
        if (typeId.Length == 0)
        {
            return typeId;
        }

        if (!_populationOf.TryGetValue(typeId, out string? population))
        {
            string named = _classOf(typeId);
            population = named.Length > 0 ? named : typeId;
            _populationOf[typeId] = population;
        }

        return population;
    }

    private sealed record Event(string SumoId, string Xml);

    private sealed record Sample(ulong Frame, double SimulatedSeconds, DateTime StampedUtc, bool InWindow,
                                 IReadOnlyList<Event> Events);
}
