using System.Globalization;
using System.Text;
using System.Text.Json;
using CarlaNet.Recording;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Illumination;

namespace CarlaNet.CoSim;

/// <summary>
/// The world truth track: every vehicle SUMO has, at each sampled SUMO frame inside the capture window,
/// drawn or not -- one CSV row per vehicle per sample, appended as the frame that renders it completes.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> A capture sidecar lists the vehicles its frame drew. The base rate of a
/// behaviour is a count over the vehicles the world contained, and taken over the sidecars it is taken
/// over the drawn ones only, leaving out every vehicle with no body (doc 06 §8.3, D6.17). This is the
/// record of what the world contained, so the denominator is taken from here.</para>
///
/// <para><b>Every value is SUMO's, read from the state the session already holds.</b> Each vehicle's
/// row is its state at the frame (<see cref="SumoStepRecord.Frames"/>), with no question put to SUMO
/// per vehicle; what a vehicle type declares -- its class, dimensions and colour -- is asked once per
/// type (<see cref="WorldTruthVehicleType"/>). Position is converted on the world's own georeference,
/// height is the bare-earth grid's under the vehicle, and course and speed are SUMO's: the reconciled
/// record of a vehicle drawn, with the body's pose and box, is the capture sidecar's, joined to these
/// rows by <c>sumo_id</c> and <c>frame</c>. Every instant is TraCI's clock for the SUMO frame the row
/// describes, never SUMO's own stamps, which are one step earlier.</para>
///
/// <para><b>Each sample waits for the frame that renders it.</b> A SUMO frame is told to observers a
/// step ahead of the frame that renders it, so its rows are written when that frame completes: the one
/// stamped with the same instant, the first of the ticks that interpolate from it. That frame says
/// whether the instant is the capture window's, which body drew each vehicle, and the sun the world
/// reported. Which vehicles it drew was decided at the SUMO frame after it, the one the ticks
/// interpolate towards, and that frame says why a vehicle not drawn was not.</para>
///
/// <para><b>Inside the capture window only.</b> A frame of the prewarm writes nothing. A track at a
/// reduced rate outside every window is not built; the summary records it as not written.</para>
///
/// <para><b>Columns.</b> The standalone producer's CSV (<c>SumoCotBridge.py</c>) without
/// <c>marked</c> -- an author's marking never travels in this track; labels join from the supervision
/// plan by vehicle id (D6.18) -- and then <c>sumo_id</c>, <c>entity_id</c> (the SUMO id, D6.13),
/// <c>frame</c>, <c>render_state</c>, <c>render_reason</c>, <c>actor_id</c>, <c>in_window</c>, and the
/// sun in the order a capture's <c>_solar</c> block writes it: <c>sun_elevation_deg</c>, the geometric
/// elevation the world reported on the frame's tick; <c>sun_corrected_elevation_deg</c>, the
/// refraction-corrected one, where the world's reading carries it; <c>illumination_band</c>, cut from the
/// corrected one by the bands' table, or from the geometric one where the reading carries no other; and
/// <c>illumination_band_elevation</c>, naming which. The uid is the one a capture sidecar gives the same
/// vehicle, <c>CARLA-TRUTH-SUMO-&lt;sumo_id&gt;</c>. <c>render_state</c> is <c>rendered</c> where a body drew the
/// vehicle on the frame, with its <c>actor_id</c>, and <c>simulated_only</c> where none did, with the
/// reason, the first of these that holds:</para>
/// <list type="table">
/// <item><term><c>no_world</c></term><description>The session renders no world.</description></item>
/// <item><term><c>left_the_simulation</c>, <c>vanished</c></term><description>SUMO no longer had it at
/// its next frame -- listed among the arrivals, or not -- so no frame from this instant draws it: the
/// ticks after a vehicle's last SUMO frame have nothing to carry it towards.</description></item>
/// <item><term><c>outside_limit</c></term><description>An optional render-set limit left it no
/// body.</description></item>
/// <item><term><c>no_blueprint</c>, <c>unknown_extent</c></term><description>Its type has no measured
/// body.</description></item>
/// <item><term><c>no_ground</c></term><description>It stands off the world's ground grid, where no body
/// can be seated.</description></item>
/// <item><term><c>not_drawn</c></term><description>None of those: the frame drew no body for it, for a
/// reason the track cannot name.</description></item>
/// </list>
///
/// <para><b>Written to survive a kill</b> (doc 04 C10 §12.7, W2). The header is the first line, and each
/// row is appended and flushed as one line before the next is composed, so a file cut off at any
/// instant is the rows already written and at most one line without its line break, which a reader
/// leaves off. The summary beside it (<see cref="SummaryPath"/>) is written whole under a temporary
/// name and renamed into place (W1): at the start with the rate and the columns, and again when the
/// session ends with what the track holds and why it ended. A summary whose <c>ended</c> is null is a
/// track still being written, or one whose run was killed.</para>
///
/// <para>Built and registered by the session (<see cref="SumoDriveSessionOptions.WorldTruthTrackPath"/>),
/// ahead of every observer the caller registered. Every call comes from the tick thread.</para>
/// </remarks>
public sealed class WorldTruthTrackWriter : ISumoStepObserver, IDisposable
{
    /// <summary>The format of the track and its summary, written in the summary.</summary>
    public const int FormatVersion = 2;

    /// <summary>A body drew the vehicle on the frame.</summary>
    public const string RenderedState = "rendered";

    /// <summary>No body drew it: SUMO simulated it, and no frame shows it.</summary>
    public const string SimulatedOnlyState = "simulated_only";

    private const string NoWorld = "no_world";
    private const string LeftTheSimulation = "left_the_simulation";
    private const string Vanished = "vanished";
    private const string OutsideLimit = "outside_limit";
    private const string NoBlueprint = "no_blueprint";
    private const string UnknownExtent = "unknown_extent";
    private const string NoGround = "no_ground";
    private const string NotDrawn = "not_drawn";

    /// <summary>
    /// A sun elevation to a millionth of a degree, the solar block's precision, so a band read back from
    /// the written elevation is the band written beside it everywhere but within that of an edge.
    /// </summary>
    private const string ElevationFormat = "0.######";

    private static readonly string[] ColumnNames =
    [
        "time_utc", "sim_time_s", "uid", "callsign", "cot_type", "how",
        "lat", "lon", "hae_m", "ce_m", "le_m",
        "course_deg", "speed_mps", "vx", "vy", "vz",
        "base_type", "type_id", "special_type", "length_m", "width_m", "height_m", "color",
        "role_name", "edge", "lane", "sumo_x", "sumo_y", "carla_x", "carla_y",
        "sumo_id", "entity_id", "frame", "render_state", "render_reason", "actor_id", "in_window",
        "sun_elevation_deg", "sun_corrected_elevation_deg", "illumination_band", "illumination_band_elevation",
    ];

    private readonly FileStream _file;
    private readonly StreamWriter _writer;
    private readonly double _sumoStepSeconds;
    private readonly int _ticksPerSumoStep;
    private readonly double _pairingTolerance;
    private readonly Func<string, WorldTruthVehicleType> _describe;
    private readonly Dictionary<string, WorldTruthVehicleType> _types = new(StringComparer.Ordinal);
    private readonly GroundSurface _ground;
    private readonly GeoLocation _origin;
    private readonly SolarEpoch? _epoch;
    private readonly Queue<Sample> _waiting = new();
    private readonly Stack<Sample> _spare = new();
    private readonly Dictionary<string, uint> _actorOf = new(StringComparer.Ordinal);
    private readonly StringBuilder _row = new();
    private long _windowFrames;
    private bool _closed;

    private WorldTruthTrackWriter(string path, FileStream file, int sumoStepsPerSample, CoSimClock clock,
                                  Func<string, WorldTruthVehicleType> describe, GroundSurface ground,
                                  (double Latitude, double Longitude) origin, SolarEpoch? epoch)
    {
        Path = path;
        SummaryPath = SummaryPathFor(path);
        _file = file;
        _writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
        SumoStepsPerSample = sumoStepsPerSample;
        _sumoStepSeconds = clock.SumoStepSeconds;
        _ticksPerSumoStep = clock.WorldTicksPerSumoStep;
        // Ticks are a running sum of the world's delta, so a frame reaches a SUMO frame's instant to
        // within rounding; the frames either side are a whole delta away.
        _pairingTolerance = clock.WorldDeltaSeconds / 2.0;
        _describe = describe;
        _ground = ground;
        _origin = new GeoLocation(origin.Latitude, origin.Longitude, ground.OriginHeightMetres);
        _epoch = epoch;
    }

    /// <summary>The columns of every row, in order: the header line.</summary>
    public static IReadOnlyList<string> Columns => ColumnNames;

    /// <summary>Where the track is written.</summary>
    public string Path { get; }

    /// <summary>Where its summary is written (<see cref="SummaryPathFor"/>).</summary>
    public string SummaryPath { get; }

    /// <summary>How many SUMO frames inside the window each sample is apart: one samples every frame.</summary>
    public int SumoStepsPerSample { get; }

    /// <summary>Simulated seconds between samples.</summary>
    public double IntervalSeconds => Math.Round(SumoStepsPerSample * _sumoStepSeconds, 9);

    /// <summary>Samples written: the SUMO frames whose every vehicle has a row.</summary>
    public long Samples { get; private set; }

    /// <summary>Rows written, one per vehicle per sample.</summary>
    public long Rows { get; private set; }

    /// <summary>TraCI's clock for the first sample written, or null before one is.</summary>
    public double? FirstSampleSeconds { get; private set; }

    /// <summary>TraCI's clock for the last sample written, or null before one is.</summary>
    public double? LastSampleSeconds { get; private set; }

    /// <summary>
    /// Why the track ended -- <c>scenario_finished</c>, <c>caller_stopped</c> or <c>run_stopped</c> -- or
    /// null while it is being written.
    /// </summary>
    public string? Ended { get; private set; }

    /// <summary>The summary written beside a track: its name with <c>.summary.json</c> for its extension.</summary>
    public static string SummaryPathFor(string trackPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackPath);
        return System.IO.Path.ChangeExtension(trackPath, ".summary.json");
    }

    /// <summary>
    /// How many SUMO steps apart samples are taken at an interval, or every step for none.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The interval is not a whole number of SUMO steps.</exception>
    internal static int SumoStepsPerSampleAt(double? intervalSeconds, double sumoStepSeconds)
    {
        if (intervalSeconds is not { } interval)
        {
            return 1;
        }

        double steps = interval / sumoStepSeconds;
        double whole = Math.Round(steps);
        if (whole < 1.0 || Math.Abs(steps - whole) > 1e-6)
        {
            throw new CoSimSessionRefusedException(
                $"The world truth track is to be sampled every {Seconds(interval)} s and SUMO steps every "
                + $"{Seconds(sumoStepSeconds)} s. A sample is a SUMO frame, so the interval has to be a whole "
                + "number of SUMO steps; a sample between two frames would be a state nobody simulated.");
        }

        return (int)whole;
    }

    /// <summary>
    /// Create the track and its summary, refusing a path that cannot be written or already holds one.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">Either file exists already, or cannot be written.</exception>
    internal static WorldTruthTrackWriter Open(string path, int sumoStepsPerSample, CoSimClock clock,
                                               Func<string, WorldTruthVehicleType> describe,
                                               GroundSurface ground, (double Latitude, double Longitude) origin,
                                               SolarEpoch? epoch)
    {
        RefuseAnExistingTrack(path);
        FileStream? file = null;
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Shared for reading, so a run is watched by reading the track as it grows.
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var writer = new WorldTruthTrackWriter(path, file, sumoStepsPerSample, clock, describe, ground,
                                                   origin, epoch);
            writer.WriteHeader();
            writer.WriteSummary(null);
            return writer;
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            if (file is not null)
            {
                // Created here and nowhere else, so nothing of another run's is taken with it.
                file.Dispose();
                DeleteIfPossible(path);
                DeleteIfPossible(SummaryPathFor(path));
            }

            throw new CoSimSessionRefusedException(
                $"The world truth track cannot be written at {path}: {failed.Message}", failed);
        }
    }

    /// <summary>Delete a file this writer created, where the failure that ended it allows.</summary>
    private static void DeleteIfPossible(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            // The refusal says why the track could not be written; a file that cannot be deleted
            // either is left as the refusal found it.
        }
    }

    /// <summary>
    /// Refuse a track, or its summary, already on disk: a run writes a track of its own and never over
    /// another run's.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">Either file exists.</exception>
    internal static void RefuseAnExistingTrack(string path)
    {
        foreach (string held in (string[])[path, SummaryPathFor(path)])
        {
            if (File.Exists(held))
            {
                throw new CoSimSessionRefusedException(
                    $"A world truth track is already written at {held}. A run writes a track of its own and "
                    + "never over another run's: name a path no run has written to.");
            }
        }
    }

    /// <summary>Keep the frame's every vehicle until the frame that renders it completes.</summary>
    public void OnSumoStep(SumoStepRecord step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (_closed)
        {
            return;
        }

        Sample sample = _spare.Count > 0 ? _spare.Pop() : new Sample();
        sample.FrameSeconds = step.FrameSeconds;
        sample.Limited = step.Pass.Limited;
        foreach (CoSimVehicleFrame vehicle in step.Frames.Values)
        {
            sample.Vehicles.Add(vehicle);
            sample.Present.Add(vehicle.Id);
            if (!_types.ContainsKey(vehicle.TypeId))
            {
                _types[vehicle.TypeId] = _describe(vehicle.TypeId);
            }
        }

        sample.Admitted.UnionWith(step.RenderedVehicleIds);
        sample.Vanished.UnionWith(step.Vanished);
        _waiting.Enqueue(sample);
    }

    /// <summary>
    /// Write the rows of the SUMO frame this frame renders, where it is the window's and is sampled.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The track could not be written.</exception>
    public void OnFrameRendered(RenderedFrameRecord frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_closed || frame.TickIndex % _ticksPerSumoStep != 0)
        {
            return;
        }

        if (!_waiting.TryDequeue(out Sample? sample))
        {
            throw new InvalidOperationException(
                $"Frame {frame.Frame} renders a SUMO frame at t={Seconds(frame.SimulatedTimeSeconds)} s, and the "
                + "world truth track was told of none to write for it.");
        }

        try
        {
            if (Math.Abs(sample.FrameSeconds - frame.SimulatedTimeSeconds) > _pairingTolerance)
            {
                throw new InvalidOperationException(
                    $"Frame {frame.Frame} renders t={Seconds(frame.SimulatedTimeSeconds)} s, and the SUMO frame "
                    + $"waiting for it is at t={Seconds(sample.FrameSeconds)} s: the world truth track would "
                    + "stamp its rows with an instant the frame did not render.");
            }

            if (!frame.InWindow || _windowFrames++ % SumoStepsPerSample != 0)
            {
                return;
            }

            // The SUMO frame read before this one rendered: the one its ticks interpolate towards, whose
            // render set this frame drew.
            _waiting.TryPeek(out Sample? next);
            Write(sample, next, frame);
        }
        catch (IOException failed)
        {
            throw new CoSimSessionRefusedException(
                $"The world truth track could not be written at {Path}: {failed.Message}. A track that stops "
                + "while the frames go on records a world emptier than the one they show, so the run stops "
                + "here.", failed);
        }
        finally
        {
            Recycle(sample);
        }
    }

    /// <summary>Close the track, and say in its summary what it holds and why it ended.</summary>
    public void OnSessionEnded(SessionEndRecord end)
    {
        ArgumentNullException.ThrowIfNull(end);
        if (_closed)
        {
            return;
        }

        Ended = end.Stopped is not null ? "run_stopped"
            : end.ScenarioFinished ? "scenario_finished"
            : "caller_stopped";
        Close();
        WriteSummary(end);
    }

    /// <summary>Close the track where it stands; its summary says it did not end.</summary>
    public void Dispose() => Close();

    /// <summary>
    /// Close the track and delete it and its summary: a session that never started writes no track.
    /// </summary>
    internal void Discard()
    {
        Close();
        File.Delete(Path);
        File.Delete(SummaryPath);
    }

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _writer.Dispose();
    }

    private void WriteHeader()
    {
        _row.Clear();
        for (int index = 0; index < ColumnNames.Length; index++)
        {
            Field(ColumnNames[index], first: index == 0);
        }

        WriteRow();
    }

    private void Write(Sample sample, Sample? next, RenderedFrameRecord frame)
    {
        _actorOf.Clear();
        if (frame.RenderSet is { } drawn)
        {
            foreach (RenderedVehicle vehicle in drawn.ByActor.Values)
            {
                _actorOf[vehicle.SumoId] = vehicle.ActorId;
            }
        }

        var instant = new Instant(
            _epoch is { } epoch ? Iso(epoch.CivilInstantAt(sample.FrameSeconds)) : string.Empty,
            F(sample.FrameSeconds, "0.###"),
            frame.Frame.ToString(CultureInfo.InvariantCulture),
            frame.RenderSet is not null,
            Sun.Of(frame));

        // In the order of their ids, so two runs of one seed write their rows alike: a frame's vehicles
        // arrive in a hash table's order.
        sample.Vehicles.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        foreach (CoSimVehicleFrame vehicle in sample.Vehicles)
        {
            WriteVehicle(vehicle, next, instant);
        }

        Samples++;
        FirstSampleSeconds ??= sample.FrameSeconds;
        LastSampleSeconds = sample.FrameSeconds;
    }

    private void WriteVehicle(in CoSimVehicleFrame vehicle, Sample? next, Instant instant)
    {
        WorldTruthVehicleType type = _types[vehicle.TypeId];
        double course = ((vehicle.HeadingDegrees % 360.0) + 360.0) % 360.0;
        double radians = course * Math.PI / 180.0;
        double speed = vehicle.SpeedMetresPerSecond;
        double? bareEarth = _ground.SampleBareEarthForSumoPosition(vehicle.X, vehicle.Y);
        // The geodetic position of the point on the ground under the bumper: the tangent plane is
        // converted at the ground's height where it is known.
        GeoLocation where = Geodesy.CarlaLocalToGeodetic(
            _origin, vehicle.X, -vehicle.Y, bareEarth is { } ground ? ground - _origin.Altitude : 0.0);
        (string state, string reason, string actor) = RenderStateOf(vehicle, type, bareEarth, next,
                                                                    instant.DrawsAWorld);
        int flowEnd = vehicle.Id.LastIndexOf('.');

        _row.Clear();
        Field(instant.TimeUtc, first: true);
        Field(instant.SimTime);
        Field("CARLA-TRUTH-SUMO-" + vehicle.Id);
        Field(type.BaseType + "-" + vehicle.Id);
        Field("a-n-G-E-V");
        Field("m-g");
        Field(F(where.Latitude, "0.0000000"));
        Field(F(where.Longitude, "0.0000000"));
        Field(bareEarth is { } height ? F(height, "0.00") : string.Empty);
        Field("0.0");
        Field("0.0");
        Field(F(course, "0.0"));
        Field(F(speed, "0.00"));
        // The contract's frame is CARLA's, east and south, so atan2(vx, -vy) is the course.
        Field(F(speed * Math.Sin(radians), "0.00"));
        Field(F(-speed * Math.Cos(radians), "0.00"));
        Field("0.00");
        Field(type.BaseType);
        Field(vehicle.TypeId);
        Field(type.SpecialType);
        Field(F(type.LengthMetres, "0.00"));
        Field(F(type.WidthMetres, "0.00"));
        Field(F(type.HeightMetres, "0.00"));
        Field(type.Color);
        // SUMO names a flow's vehicles <flow id>.<n>, so the flow is the nearest thing to a role.
        Field(flowEnd > 0 ? vehicle.Id[..flowEnd] : vehicle.Id);
        Field(vehicle.EdgeId);
        Field(vehicle.LaneId);
        Field(F(vehicle.X, "0.00"));
        Field(F(vehicle.Y, "0.00"));
        Field(F(vehicle.X, "0.00"));
        Field(F(-vehicle.Y, "0.00"));
        Field(vehicle.Id);
        Field(vehicle.Id);
        Field(instant.Frame);
        Field(state);
        Field(reason);
        Field(actor);
        Field("1");
        Field(instant.Sun.Elevation);
        Field(instant.Sun.CorrectedElevation);
        Field(instant.Sun.Band);
        Field(instant.Sun.BandElevation);
        WriteRow();
        Rows++;
    }

    /// <summary>
    /// Whether a body drew the vehicle on the frame, and if not, why: the first reason that holds.
    /// </summary>
    private (string State, string Reason, string Actor) RenderStateOf(
        in CoSimVehicleFrame vehicle, WorldTruthVehicleType type, double? bareEarth, Sample? next, bool drawsAWorld)
    {
        if (!drawsAWorld)
        {
            return (SimulatedOnlyState, NoWorld, string.Empty);
        }

        if (_actorOf.TryGetValue(vehicle.Id, out uint actor))
        {
            return (RenderedState, string.Empty, actor.ToString(CultureInfo.InvariantCulture));
        }

        string reason = next is null ? NotDrawn
            : !next.Present.Contains(vehicle.Id) ? (next.Vanished.Contains(vehicle.Id) ? Vanished : LeftTheSimulation)
            : next.Limited && !next.Admitted.Contains(vehicle.Id) ? OutsideLimit
            : type.Unrenderable is UnrenderableReason.NoBlueprint ? NoBlueprint
            : type.Unrenderable is UnrenderableReason.UnknownExtent ? UnknownExtent
            : bareEarth is null ? NoGround
            : NotDrawn;
        return (SimulatedOnlyState, reason, string.Empty);
    }

    /// <summary>Append a field to the row, quoted where it holds a separator, a quote or a line break.</summary>
    private void Field(string value, bool first = false)
    {
        if (!first)
        {
            _row.Append(',');
        }

        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            _row.Append(value);
            return;
        }

        _row.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    /// <summary>Append the row as one line and flush it, before anything else is composed.</summary>
    private void WriteRow()
    {
        _row.Append('\n');
        _writer.Write(_row);
        _writer.Flush();
    }

    /// <summary>
    /// The summary, written whole under a temporary name and renamed into place: the rate and the
    /// columns, what the track holds so far, and how it ended, or null where it has not.
    /// </summary>
    private void WriteSummary(SessionEndRecord? end)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("world_truth_track_version", FormatVersion);
            json.WriteString("track", System.IO.Path.GetFileName(Path));
            json.WriteStartArray("columns");
            foreach (string column in ColumnNames)
            {
                json.WriteStringValue(column);
            }

            json.WriteEndArray();
            json.WriteString("instant", "traci_clock");
            json.WriteNumber("sumo_step_s", _sumoStepSeconds);
            json.WriteNumber("interval_s", IntervalSeconds);
            json.WriteNumber("every_sumo_steps", SumoStepsPerSample);
            // Outside every capture window nothing is written; a reduced rate there is not built.
            json.WriteNull("outside_window_interval_s");
            json.WriteNumber("samples", Samples);
            json.WriteNumber("rows", Rows);
            WriteNumberOrNull(json, "first_sample_s", FirstSampleSeconds);
            WriteNumberOrNull(json, "last_sample_s", LastSampleSeconds);
            if (end is null)
            {
                json.WriteNull("ended");
            }
            else
            {
                json.WriteStartObject("ended");
                json.WriteString("reason", Ended);
                if (end.Stopped is { } stopped)
                {
                    json.WriteString("stage", stopped.Stage.ToString());
                    json.WriteString("cause", stopped.Cause.Code());
                }

                json.WriteNumber("last_sumo_frame_s", end.LastFrameSeconds);
                WriteNumberOrNull(json, "last_rendered_s", end.LastRenderedSeconds);
                WriteNumberOrNull(json, "last_rendered_frame", end.LastRenderedFrame);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        string partial = SummaryPath + ".partial";
        File.WriteAllBytes(partial, buffer.ToArray());
        File.Move(partial, SummaryPath, overwrite: true);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number)
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, ulong? value)
    {
        if (value is { } number)
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private void Recycle(Sample sample)
    {
        sample.Vehicles.Clear();
        sample.Present.Clear();
        sample.Admitted.Clear();
        sample.Vanished.Clear();
        _spare.Push(sample);
    }

    /// <summary>The Cursor-on-Target timestamp: UTC, to the millisecond, with a trailing <c>Z</c>.</summary>
    private static string Iso(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";

    /// <summary>A number in invariant form, with a negative zero written as zero.</summary>
    private static string F(double value, string format) =>
        (value == 0.0 ? 0.0 : value).ToString(format, CultureInfo.InvariantCulture);

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>What every row of one sample shares: its two clocks, its frame and its sun.</summary>
    private readonly record struct Instant(string TimeUtc, string SimTime, string Frame, bool DrawsAWorld, Sun Sun);

    /// <summary>
    /// The sun the world reported on a frame's tick, as the track writes it: both elevations, and the
    /// band with the elevation it was cut from, each empty where the frame carries none.
    /// </summary>
    private readonly record struct Sun(string Elevation, string CorrectedElevation, string Band, string BandElevation)
    {
        /// <summary>
        /// The frame's sun, its band cut by the rule every record that writes a band beside a reported sun
        /// follows (<see cref="IlluminationBands.TryOfReported"/>): from the refraction-corrected
        /// elevation, from the geometric one only where the world's reading carries no other, and none for
        /// a sun the engine could not compute.
        /// </summary>
        public static Sun Of(RenderedFrameRecord frame)
        {
            if (frame.SunElevationDegrees is not { } geometric)
            {
                return new Sun(string.Empty, string.Empty, string.Empty, string.Empty);
            }

            string elevation = F(geometric, ElevationFormat);
            string corrected = frame.SunCorrectedElevationDegrees is { } refracted
                ? F(refracted, ElevationFormat)
                : string.Empty;
            return IlluminationBands.TryOfReported(geometric, frame.SunCorrectedElevationDegrees,
                                                   out IlluminationBand band, out SolarElevationKind cutFrom)
                ? new Sun(elevation, corrected, IlluminationBands.Name(band), SolarElevationKinds.Name(cutFrom))
                : new Sun(elevation, corrected, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// One SUMO frame as the track keeps it until the frame that renders it: every vehicle's state, and
    /// what the frame decided about the render set.
    /// </summary>
    private sealed class Sample
    {
        public double FrameSeconds { get; set; }

        /// <summary>Whether an optional limit could leave a vehicle out at the frame's pass.</summary>
        public bool Limited { get; set; }

        public List<CoSimVehicleFrame> Vehicles { get; } = [];

        public HashSet<string> Present { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Admitted { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Vanished { get; } = new(StringComparer.Ordinal);
    }
}
