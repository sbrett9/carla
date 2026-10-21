using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarlaNet.Recording;
using CarlaNet.Types.Geom;

using ActorId = uint;

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
/// <para><b>In view of a followed camera.</b> While a camera is followed (<see cref="Follow"/>), each
/// sample's records are marked against that camera as a recorder marks a capture's: against the draw
/// distance the frame's render set carries (<see cref="DrawDistanceCheck.Mark"/>), then where each box
/// falls in the camera's picture (<see cref="BoxProjector.Mark(IReadOnlyList{VehicleTelemetry}, in PinholeCamera)"/>),
/// from the camera's pose in the frame's own snapshot and the image size and field of view it was
/// spawned with. A vehicle is in view where its box falls wholly or partly in the picture and it is not
/// wholly beyond the draw distance; a vehicle hidden behind another is in view. The marking decides each
/// event's in-view flag and adds nothing to the event. A frame whose snapshot does not hold the followed
/// camera is sampled with no flags and counted (<see cref="SamplesCameraMissing"/>). The camera is never
/// registered with the session, so following it changes nothing about which vehicles get a body.</para>
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
    private long _samples, _framesUnpaired, _bodiesMissing, _samplesCameraMissing, _samplesDropped;
    private (ActorId Camera, CameraOptics Optics)? _followed;

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

    /// <summary>
    /// Samples taken while a camera was followed whose frame's snapshot did not hold it -- the camera was
    /// destroyed, or had not yet reached the client -- each sent with no in-view flags.
    /// </summary>
    public long SamplesCameraMissing
    {
        get
        {
            lock (_gate)
            {
                return _samplesCameraMissing;
            }
        }
    }

    /// <summary>The camera followed now, or null where none is.</summary>
    public ActorId? FollowedCamera
    {
        get
        {
            lock (_gate)
            {
                return _followed?.Camera;
            }
        }
    }

    /// <summary>The image size and field of view of the camera followed now, or null where none is.</summary>
    public CameraOptics? FollowedOptics
    {
        get
        {
            lock (_gate)
            {
                return _followed?.Optics;
            }
        }
    }

    /// <summary>
    /// Follow a camera from the next sampled frame on: each sample says which of its vehicles are in the
    /// camera's picture. Following another camera stops following the last.
    /// </summary>
    /// <param name="camera">The camera's actor id, spawned by any client.</param>
    /// <remarks>
    /// The camera's image size and field of view are read from its attributes now, in one round trip, and
    /// never again: a camera's attributes are fixed at its spawn. Its pose is read on each sampled frame
    /// from that frame's snapshot, so a camera flown or orbited between frames is followed wherever it
    /// goes. Called between advances, from the thread that advances the session, as every other call on
    /// it is. Unlike <see cref="SumoDriveSession.AddCamera"/>, it changes nothing about which vehicles get
    /// a body.
    /// </remarks>
    /// <exception cref="ArgumentException">The world has no such actor, or the actor is not a camera.</exception>
    public void Follow(ActorId camera)
    {
        CameraOptics optics = _world.DescribeCamera(camera)
                              ?? throw new ArgumentException(
                                  $"Actor {camera} is not a camera the world knows: it has no image_size_x, "
                                  + "image_size_y and fov to project a picture with.", nameof(camera));
        lock (_gate)
        {
            _followed = (camera, optics);
        }
    }

    /// <summary>
    /// Stop following a camera, from the next sampled frame on: samples carry no in-view flags again.
    /// Answers whether a camera was followed.
    /// </summary>
    public bool Unfollow()
    {
        lock (_gate)
        {
            bool was = _followed is not null;
            _followed = null;
            return was;
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
    /// <item><c>camera</c>: <c>none</c> where no camera was followed, <c>followed</c> where the followed
    /// camera's pose was read from the frame's snapshot, and <c>missing</c> where the snapshot did not hold
    /// it.</item>
    /// <item><c>camera_id</c>: the followed camera's actor id, or null where none was followed.</item>
    /// <item><c>events</c>: one object per body the frame drew, in the order of the frame's truth records:
    /// <c>sumo_id</c>, the vehicle; <c>in_view</c>, true or false where <c>camera</c> is
    /// <c>followed</c>, and null otherwise; and <c>xml</c>, its event, one line with no XML declaration,
    /// to send as one UDP datagram.</item>
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
                json.WriteString("camera", sample.Camera);
                if (sample.CameraId is { } cameraId)
                {
                    json.WriteNumber("camera_id", cameraId);
                }
                else
                {
                    json.WriteNull("camera_id");
                }

                json.WriteStartArray("events");
                foreach (Event vehicle in sample.Events)
                {
                    json.WriteStartObject();
                    json.WriteString("sumo_id", vehicle.SumoId);
                    if (vehicle.InView is { } inView)
                    {
                        json.WriteBoolean("in_view", inView);
                    }
                    else
                    {
                        json.WriteNull("in_view");
                    }

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

        (ActorId Camera, CameraOptics Optics)? followed;
        lock (_gate)
        {
            followed = _followed;
        }

        Sample? sample = Take(frame, followed, out int missing);
        lock (_gate)
        {
            _bodiesMissing += missing;
            if (sample is null)
            {
                _framesUnpaired++;
                return;
            }

            _samples++;
            if (sample.Camera == CameraMissing)
            {
                _samplesCameraMissing++;
            }

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
    private Sample? Take(RenderedFrameRecord frame, (ActorId Camera, CameraOptics Optics)? followed, out int missing)
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

        // Marked as a recorder marks a capture of the camera: the draw distance from the camera's pose
        // first, then the picture. The box itself is not sent, so the records are projected without the
        // georeference its corners are converted with.
        string camera = CameraNone;
        bool marked = false;
        if (followed is { } watched)
        {
            camera = CameraMissing;
            if (_world.ObservedTransformAt(watched.Camera, frame.Frame) is { } pose)
            {
                camera = CameraFollowed;
                marked = true;
                records = DrawDistanceCheck.Mark(records, pose.Location, renderSet.DrawDistanceMetres, out _, out _);
                records = BoxProjector.Mark(records, new PinholeCamera(pose, watched.Optics.ImageWidth,
                                                                       watched.Optics.ImageHeight,
                                                                       watched.Optics.HorizontalFovDegrees));
            }
        }

        DateTime stamped = civil.UtcDateTime;
        var events = new List<Event>(records.Count);
        foreach (VehicleTelemetry record in records)
        {
            RenderedVehicle vehicle = record.Rendered!;
            string affiliation = _options.AffiliationByPopulation.Count == 0
                ? _options.Affiliation
                : _options.AffiliationOf(PopulationOf(vehicle.VehicleTypeId));
            bool? inView = marked
                ? (record.InFrame is InFrame.Wholly or InFrame.Partly) && record.DrawDistance != DrawDistanceReach.Beyond
                : null;
            events.Add(new Event(vehicle.SumoId, inView,
                                 CotWriter.StreamEvent(record, affiliation, _options.UidPrefix, stamped,
                                                       _options.StaleSeconds)));
        }

        return new Sample(frame.Frame, frame.SimulatedTimeSeconds, stamped, frame.InWindow, camera,
                          followed?.Camera, events);
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

    private const string CameraNone = "none";
    private const string CameraFollowed = "followed";
    private const string CameraMissing = "missing";

    private sealed record Event(string SumoId, bool? InView, string Xml);

    private sealed record Sample(ulong Frame, double SimulatedSeconds, DateTime StampedUtc, bool InWindow,
                                 string Camera, ActorId? CameraId, IReadOnlyList<Event> Events);
}
