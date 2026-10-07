using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarlaNet.Recording;
using CarlaNet.Sumo;
using CarlaNet.Types.Illumination;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

/// <summary>
/// The run manifest: one JSON object per line, appended as the run goes and closed by a terminal row --
/// what the run is, every admission to and release from the render set, every event that changes the
/// population SUMO simulates, the sun at the capture window's opening and end, and why the run ended.
/// </summary>
/// <remarks>
/// <para><b>Rows, not a document</b> (doc 04 §12.7, W2, D4.36). Each row is one self-contained JSON object
/// on a line of its own, carrying its kind in <c>row</c>, appended and flushed before the next is composed.
/// A reader keeps every line that ends in a line break, so a manifest cut off at any instant is the rows
/// already written, each still valid; the last row of a manifest whose run reached its end is
/// <c>manifest_closed</c>, and one without it is a run that was interrupted -- a vehicle admitted and
/// never released was still in the render set then, and a collision begun and never ended was still
/// going on.</para>
///
/// <para><b>The rows.</b> An admission or an event is stamped (<c>sim_time_s</c>) with TraCI's clock for the
/// SUMO frame it describes, never SUMO's own stamps, which are a step earlier; a release with the end of the
/// session's interval, the instant of the first frame that no longer draws the vehicle -- for a vehicle SUMO
/// removed, the frame after the one of its last SUMO step -- so an interval holds the instants of exactly the
/// frames that drew its vehicle; a solar row with the instant of the frame whose sun it reads, to the
/// microsecond.</para>
/// <list type="table">
/// <item><term><c>manifest_opened</c></term><description>First, before anything is rendered: the header
/// the caller handed over verbatim (<c>run</c>) -- the run's and the session's identity, the channels --
/// beside what the session established itself: the scenario's files and digests from its compile lock,
/// the supervision plan and its vocabulary, the SUMO settings the session checked and runs under, the
/// clock, the render set, the rule the vehicle lights follow (<c>vehicle_lights</c>: whether they are
/// driven, the sun elevations the headlights switch at and which elevation they read, and that brake
/// lights and turn signals follow SUMO's signals; doc 11 D11.9), and the epoch and illumination declared
/// (doc 04 C9 §11.8).</description></item>
/// <item><term><c>instance</c>, <c>series</c>, <c>cohort</c></term><description>Next, where the compile
/// lock binds a supervision plan: each of its pattern instances, recurring series and cohorts as declared
/// -- identity, supervision state, labels and parameters, an instance's participants with their roles and
/// the intervals it declares with their anchors, a series' slots.</description></item>
/// <item><term><c>interval_opened</c>, <c>interval_closed</c></term><description>One of the plan's
/// intervals as the supervision binder opens and closes it, named by its <c>(instance_id, participant,
/// phase)</c> triple: the declared onsets, the committed onset by TraCI's clock, the rendered frame that
/// showed it where one did, and whether it began before the window; on closing, why
/// (<c>closed_by</c>, in the core vocabulary's words), when SUMO committed its end, and the spans its
/// participant was not drawn. An interval SUMO never inserted a participant for closes without having
/// opened.</description></item>
/// <item><term><c>supervision_defect</c></term><description>A seam defect the binder found -- what it
/// found wrong with the seam rather than the scenario (<see cref="SupervisionBinder.Defects"/>) -- in its
/// own words, written as the binder next opens or closes an interval, at the next frame, or at the
/// close, whichever comes first.</description></item>
/// <item><term><c>sensor_placed</c></term><description>A camera the caller placed and named, with the
/// exposure it was given where it carries one: its post-process profile, method, ISO, shutter, aperture,
/// compensation and EV100 (<see cref="PlaceSensor(string, uint, CameraExposure)"/>).</description></item>
/// <item><term><c>render_admitted</c>, <c>render_released</c></term><description>A vehicle taking up a
/// place in the render set and giving it up (doc 04 C2 §4.1): admitted at the pass that admitted it, with
/// why -- <c>rendering_began</c>, <c>inserted</c> or <c>entered_limit</c> -- and the frame and body that
/// first drew it, written once that frame has rendered; released with the span and the reason the session
/// hands <see cref="SumoDriveSessionOptions.OnRelease"/>, written as the session hands it out -- for a vehicle
/// SUMO removed, once the frame of its last step has rendered, or at the close where the run ended before
/// that frame. A vehicle still in the render set when the run ends is not released, and has an admission row
/// alone.</description></item>
/// <item><term><c>collision_began</c>, <c>collision_ended</c></term><description>A collision SUMO
/// reported, written as it begins and again, as a span, when it is over.</description></item>
/// <item><term><c>vehicle_not_inserted</c>, <c>emergency_stop</c>, <c>teleport</c></term><description>The
/// other events that change the population the scenario authored.</description></item>
/// <item><term><c>solar_window_open</c>, <c>solar_window_end</c></term><description>The sun the world
/// reported at the window's first and last capture tick, and at the end the audit's worst residual and
/// whether the sun the world held matched the declaration throughout (<c>sun_matched_declaration</c>:
/// an epoch declared, the policy binding the sun, a sun present, and the audit within its tolerance;
/// doc 04 C9 §11.8.1, §11.8.2).</description></item>
/// <item><term><c>manifest_closed</c></term><description>Last: why the run ended --
/// <c>scenario_finished</c>, <c>caller_stopped</c> or <c>run_stopped</c> with its stage and cause -- with
/// the caller's own reason where it gave one, what the manifest holds, the intervals still open
/// (<c>open_intervals</c>) and the word the binder closes them with after this row
/// (<c>open_intervals_close_as</c>), the plan's intervals nothing in the run opened or closed
/// (<c>never_opened</c>), and how far the world departed from what the bridge commanded over the whole
/// run (<c>bridge_divergence</c>, doc 06 §4.3).</description></item>
/// </list>
///
/// <para><b>The bridge's divergence is a run-level figure.</b> The session compares every pose and
/// velocity it writes against what the world reports for the body on the same tick
/// (<see cref="PoseDivergence"/>); the terminal row carries the run's totals -- the comparisons taken,
/// the vehicle-ticks nothing read back, the worst and mean position in metres, the worst yaw, pitch and
/// roll in degrees, the worst and mean velocity in metres per second against the mean commanded speed,
/// and the vehicle and instant of the worst position and the worst velocity. A capture's sidecar carries
/// none of it, as the owner ruled (2026-10-05): a convention that is wrong is wrong on every vehicle of
/// every frame, and one figure for the run says so.</para>
///
/// <para><b>Closed by its caller, or by the session's end.</b> A caller that reads the run's closing gates
/// before it disposes the session closes the manifest first (<see cref="Close"/>), so the gate reads the
/// terminal row; a manifest still open when the session ends is closed then.</para>
///
/// <para><b>Every planned interval, once, in either case.</b> The terminal row comes before the binder
/// closes the intervals the session's end leaves open, so those are listed on it rather than written as
/// closed; with the opened and closed rows and <c>never_opened</c>, a closed manifest names every
/// <c>(instance_id, participant, phase)</c> triple the plan declares, which two runs of one scenario share
/// (06 D6.8). A manifest with no terminal row writes no <c>open_at_interruption</c>: a reader infers it,
/// for each triple opened and never closed in the rows already written.</para>
///
/// <para>Built and registered by the session (<see cref="SumoDriveSessionOptions.RunManifestPath"/>).
/// Every call comes from the thread that advances the session.</para>
/// </remarks>
public sealed class RunManifestWriter : ISumoStepObserver, ISupervisionIntervalSink, IDisposable
{
    /// <summary>The manifest's format, written on its opening row.</summary>
    public const int FormatVersion = 1;

    /// <summary>The first row.</summary>
    public const string OpenedRow = "manifest_opened";

    /// <summary>The terminal row: present in a manifest whose run reached its end, and only there.</summary>
    public const string ClosedRow = "manifest_closed";

    private const string SensorPlacedRow = "sensor_placed";
    private const string AdmittedRow = "render_admitted";
    private const string ReleasedRow = "render_released";
    private const string CollisionBeganRow = "collision_began";
    private const string CollisionEndedRow = "collision_ended";
    private const string NotInsertedRow = "vehicle_not_inserted";
    private const string EmergencyStopRow = "emergency_stop";
    private const string TeleportRow = "teleport";
    private const string WindowOpenRow = "solar_window_open";
    private const string WindowEndRow = "solar_window_end";
    private const string InstanceRow = "instance";
    private const string SeriesRow = "series";
    private const string CohortRow = "cohort";
    private const string IntervalOpenedRow = "interval_opened";
    private const string IntervalClosedRow = "interval_closed";
    private const string DefectRow = "supervision_defect";

    /// <summary>What the opening row names as the source of a vehicle's brake lights and turn signals.</summary>
    private const string SumoSignalsSource = "sumo_signals";

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        // A civil time's offset is written as it reads, +03:30, rather than escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FileStream _file;
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private readonly Utf8JsonWriter _json;
    private readonly SumoDriveSessionOptions _options;
    private readonly CoSimRunReport _report;
    private readonly Func<bool> _scenarioFinished;
    private readonly Func<IReadOnlyList<RenderedVehicleInterval>> _leftAtAClose;
    private readonly double _pairingTolerance;
    private readonly List<Admission> _pending = [];
    private readonly Dictionary<string, string> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RenderedVehicle> _drawn = new(StringComparer.Ordinal);
    private readonly HashSet<(string Collider, string Victim)> _collisionsOpen = [];
    private readonly List<string> _admittedAtThisPass = [];
    private readonly Dictionary<(string Instance, string? Participant, string Phase), SupervisionIntervalRecord> _openIntervals = [];
    private readonly HashSet<(string Instance, string? Participant, string Phase)> _bound = [];
    private SupervisionBinder? _binder;
    private int _defectsWritten;
    private int _collisionSpansWritten;
    private double? _lastSumoFrameSeconds;
    private RenderedFrameRecord? _lastFrame;
    private RenderedFrameRecord? _firstWindowCapture;
    private RenderedFrameRecord? _lastWindowCapture;
    private long _windowCaptureTicks;
    private bool _manifestClosed;
    private bool _fileClosed;

    private RunManifestWriter(string path, FileStream file, SumoDriveSessionOptions options,
                              CoSimRunReport report, Func<bool> scenarioFinished,
                              Func<IReadOnlyList<RenderedVehicleInterval>> leftAtAClose)
    {
        Path = path;
        _file = file;
        _json = new Utf8JsonWriter(_buffer, JsonOptions);
        _options = options;
        _report = report;
        _scenarioFinished = scenarioFinished;
        _leftAtAClose = leftAtAClose;
        // Ticks are a running sum of the world's delta, so a frame reaches a SUMO frame's instant to within
        // rounding; the frames either side are a whole delta away.
        _pairingTolerance = report.Clock.WorldDeltaSeconds / 2.0;
    }

    /// <summary>Where the manifest is written.</summary>
    public string Path { get; }

    /// <summary>Rows written so far, the opening and closing rows included.</summary>
    public long Rows { get; private set; }

    /// <summary>Whether the terminal row has been written.</summary>
    public bool Closed => _manifestClosed;

    /// <summary>Admission rows written.</summary>
    public long Admissions { get; private set; }

    /// <summary>Release rows written.</summary>
    public long Releases { get; private set; }

    /// <summary>Rows for the events that change the population: collisions, vehicles not inserted, emergency stops, teleports.</summary>
    public long Events { get; private set; }

    /// <summary>Interval openings written.</summary>
    public long IntervalsOpened { get; private set; }

    /// <summary>Interval closings written.</summary>
    public long IntervalsClosed { get; private set; }

    /// <summary>Seam defects written, as the supervision binder found them.</summary>
    public long SupervisionDefects { get; private set; }

    /// <summary>
    /// Refuse a header that is not a JSON object: the opening row carries it verbatim, and anything else
    /// would make the row something other than one.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The header is not JSON, or not an object.</exception>
    internal static void RequireAHeaderObject(string? header)
    {
        if (header is null)
        {
            return;
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(header);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                return;
            }
        }
        catch (JsonException failed)
        {
            throw new CoSimSessionRefusedException(
                $"The run manifest's header is not JSON ({failed.Message}). It is written verbatim into the "
                + "manifest's opening row, so it has to be one JSON object.", failed);
        }

        throw new CoSimSessionRefusedException(
            "The run manifest's header is JSON and not an object. It is written verbatim into the manifest's "
            + "opening row, so it has to be one JSON object.");
    }

    /// <summary>
    /// Refuse a manifest already on disk: a run writes a manifest of its own and never over another run's.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The file exists.</exception>
    internal static void RefuseAnExistingManifest(string path)
    {
        if (File.Exists(path))
        {
            throw new CoSimSessionRefusedException(
                $"A run manifest is already written at {path}. A run writes a manifest of its own and never "
                + "over another run's: name a path no run has written to.");
        }
    }

    /// <summary>
    /// Create the manifest and write its opening row, refusing a path that cannot be written or already
    /// holds one.
    /// </summary>
    /// <param name="path">Where the manifest is written.</param>
    /// <param name="options">The session's options.</param>
    /// <param name="report">The session's report.</param>
    /// <param name="scenarioFinished">Whether SUMO had nothing left to simulate at the last advance.</param>
    /// <param name="leftAtAClose">
    /// The vehicles SUMO removed at the last SUMO frame read whose last step has not rendered, as the
    /// session's end would release them now, for a close its caller makes before the session ends.
    /// </param>
    /// <exception cref="CoSimSessionRefusedException">The file exists already, or cannot be written.</exception>
    internal static RunManifestWriter Open(string path, SumoDriveSessionOptions options, CoSimRunReport report,
                                           Func<bool> scenarioFinished,
                                           Func<IReadOnlyList<RenderedVehicleInterval>> leftAtAClose)
    {
        RefuseAnExistingManifest(path);
        FileStream? file = null;
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Shared for reading, so a run is watched by reading the manifest as it grows.
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var writer = new RunManifestWriter(path, file, options, report, scenarioFinished, leftAtAClose);
            writer.WriteOpened();
            writer.WritePlan();
            return writer;
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            if (file is not null)
            {
                // Created here and nowhere else, so nothing of another run's is taken with it.
                file.Dispose();
                try
                {
                    File.Delete(path);
                }
                catch (Exception left) when (left is IOException or UnauthorizedAccessException)
                {
                    // The refusal says why the manifest could not be written; a file that cannot be
                    // deleted either is left as the refusal found it.
                }
            }

            throw new CoSimSessionRefusedException(
                $"The run manifest cannot be written at {path}: {failed.Message}", failed);
        }
    }

    /// <summary>
    /// Record a camera the caller has placed: its name, which its captures and its platform track carry,
    /// and its actor.
    /// </summary>
    /// <param name="sensorId">The camera's name.</param>
    /// <param name="cameraActorId">The camera's actor id.</param>
    public void PlaceSensor(string sensorId, uint cameraActorId) => PlaceSensor(sensorId, cameraActorId, null);

    /// <summary>
    /// Record a camera the caller has placed: its name, which its captures and its platform track carry,
    /// its actor, and the exposure it was given -- the post-process profile it names, the method, ISO,
    /// shutter, aperture and compensation its attributes set over it, and the EV100 they make under
    /// manual -- in <c>exposure</c>, under the names its captures' <c>&lt;_carla_exposure&gt;</c> carries,
    /// read from the camera's attributes (<see cref="CameraExposure.Of"/>). A camera that carries none has
    /// no <c>exposure</c>.
    /// </summary>
    /// <param name="sensorId">The camera's name.</param>
    /// <param name="cameraActorId">The camera's actor id.</param>
    /// <param name="exposure">The exposure the camera was given, or null where it carries none.</param>
    public void PlaceSensor(string sensorId, uint cameraActorId, CameraExposure? exposure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);
        if (_manifestClosed)
        {
            return;
        }

        Guard(() => WriteRow(SensorPlacedRow, json =>
        {
            json.WriteString("sensor_id", sensorId);
            json.WriteNumber("camera_actor_id", cameraActorId);
            WriteNumberOrNull(json, "after_frame", _lastFrame?.Frame);
            if (exposure is not null)
            {
                json.WriteStartObject("exposure");
                exposure.WriteJsonProperties(json);
                json.WriteEndObject();
            }
        }));
    }

    /// <summary>Write what the session made of a SUMO frame: releases, admissions and events.</summary>
    /// <exception cref="CoSimSessionRefusedException">The manifest could not be written.</exception>
    public void OnSumoStep(SumoStepRecord step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (_manifestClosed)
        {
            return;
        }

        Guard(() =>
        {
            _lastSumoFrameSeconds = step.FrameSeconds;
            WriteDefects();

            // Released before admitted, as the pass releases before it admits.
            foreach (RenderedVehicleInterval released in step.Released)
            {
                WriteRelease(released);
            }

            _admittedAtThisPass.Clear();
            foreach (string vehicleId in step.RenderedVehicleIds)
            {
                if (!_held.ContainsKey(vehicleId))
                {
                    _admittedAtThisPass.Add(vehicleId);
                }
            }

            _admittedAtThisPass.Sort(StringComparer.Ordinal);
            foreach (string vehicleId in _admittedAtThisPass)
            {
                string typeId = step.Frames.TryGetValue(vehicleId, out CoSimVehicleFrame frame) ? frame.TypeId : string.Empty;
                _held[vehicleId] = typeId;
                string reason = step.AfterFastForward ? "rendering_began"
                    : step.Events.Departed.Contains(vehicleId) ? "inserted"
                    : "entered_limit";
                _pending.Add(new Admission(vehicleId, typeId, step.FrameSeconds, reason));
            }

            WriteCollisionsEnded();
            foreach (SumoCollision collision in step.Collisions)
            {
                if (_collisionsOpen.Add((collision.ColliderId, collision.VictimId)))
                {
                    WriteCollisionBegan(collision, step.FrameSeconds);
                }
            }

            foreach (VehicleNotInserted dropped in step.NotInserted)
            {
                WriteEvent(NotInsertedRow, dropped.GoneAtSeconds, dropped.VehicleId,
                           json => json.WriteNumber("waiting_at_s", dropped.WaitingAtSeconds));
            }

            foreach (string vehicleId in step.Events.EmergencyStops)
            {
                WriteEvent(EmergencyStopRow, step.FrameSeconds, vehicleId, null);
            }

            foreach (string vehicleId in step.Events.TeleportsStarted)
            {
                WriteEvent(TeleportRow, step.FrameSeconds, vehicleId, null);
            }
        });
    }

    /// <summary>
    /// Write the admissions this frame resolves -- the vehicles its bodies first drew, and those admitted
    /// at its instant that no body drew -- and the sun at the window's first capture tick.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The manifest could not be written.</exception>
    public void OnFrameRendered(RenderedFrameRecord frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_manifestClosed)
        {
            return;
        }

        Guard(() =>
        {
            _lastFrame = frame;
            WriteDefects();
            if (frame.InWindow && frame.IsCaptureTick)
            {
                _windowCaptureTicks++;
                _lastWindowCapture = frame;
                if (_firstWindowCapture is null)
                {
                    _firstWindowCapture = frame;
                    WriteWindowOpen(frame);
                }
            }

            if (_pending.Count == 0)
            {
                return;
            }

            _drawn.Clear();
            if (frame.RenderSet is { } set)
            {
                foreach (RenderedVehicle vehicle in set.ByActor.Values)
                {
                    _drawn[vehicle.SumoId] = vehicle;
                }
            }

            int kept = 0;
            for (int index = 0; index < _pending.Count; index++)
            {
                Admission admission = _pending[index];
                if (_drawn.TryGetValue(admission.VehicleId, out RenderedVehicle? drawn))
                {
                    WriteAdmission(admission, drawn.AdmittedTick, drawn.ActorId);
                }
                else if (admission.AdmittedAtSeconds <= frame.SimulatedTimeSeconds + _pairingTolerance)
                {
                    // The frame stamped with its admission drew no body for it: a type with no measured
                    // body, no ground under it, or no world.
                    WriteAdmission(admission, frame.Frame, null);
                }
                else
                {
                    _pending[kept++] = admission;
                }
            }

            _pending.RemoveRange(kept, _pending.Count - kept);
        });
    }

    /// <summary>
    /// Write an interval as the supervision binder opens it: its row's identity, its declared onsets and
    /// what the run committed of it so far.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The manifest could not be written.</exception>
    public void OnIntervalOpened(SupervisionIntervalRecord interval)
    {
        ArgumentNullException.ThrowIfNull(interval);
        if (_manifestClosed)
        {
            return;
        }

        Guard(() =>
        {
            WriteDefects();
            (string, string?, string) key = KeyOf(interval);
            _openIntervals[key] = interval;
            _bound.Add(key);
            WriteRow(IntervalOpenedRow, json =>
            {
                // The instant it opened: committed by an event, or failing that declared; none where neither
                // is known, as for a phase entered before the window, whose instant cannot be recovered.
                WriteNumberOrNull(json, "sim_time_s", interval.CommittedStartSeconds ?? interval.DeclaredStartSeconds);
                WriteIntervalRow(json, interval);
                json.WriteString("role", interval.Role);
                WriteOnsets(json, interval);
            });
            IntervalsOpened++;
        });
    }

    /// <summary>
    /// Write an interval as the supervision binder closes it: why, when, its onsets as the run bound them,
    /// and the spans its participant was not drawn.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The manifest could not be written.</exception>
    public void OnIntervalClosed(SupervisionIntervalRecord interval)
    {
        ArgumentNullException.ThrowIfNull(interval);
        if (_manifestClosed)
        {
            return;
        }

        Guard(() =>
        {
            WriteDefects();
            (string, string?, string) key = KeyOf(interval);
            _openIntervals.Remove(key);
            _bound.Add(key);
            WriteRow(IntervalClosedRow, json =>
            {
                WriteNumberOrNull(json, "sim_time_s", interval.ClosedAtSeconds);
                WriteIntervalRow(json, interval);
                json.WriteString("closed_by", interval.ClosedBy is { } closedBy ? CoreVocabulary.Name(closedBy) : null);
                WriteOnsets(json, interval);
                WriteNumberOrNull(json, "committed_end_s", interval.CommittedEndSeconds);
                json.WriteStartArray("not_drawn");
                foreach (NotDrawnSpan span in interval.NotDrawn)
                {
                    json.WriteStartObject();
                    json.WriteNumber("from_s", span.FromSeconds);
                    json.WriteNumber("to_s", span.ToSeconds);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            });
            IntervalsClosed++;
        });
    }

    /// <summary>
    /// Write the seam defects the session's supervision binder finds, as it finds them: told once the
    /// session has built it, after the manifest.
    /// </summary>
    internal void Supervise(SupervisionBinder binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        _binder = binder;
    }

    /// <summary>
    /// Close the manifest at the session's end, where its caller has not: the releases of the vehicles SUMO
    /// removed at the last SUMO frame read, which the end released (<see cref="SessionEndRecord.Released"/>),
    /// the sun at the window's end, and the terminal row.
    /// </summary>
    public void OnSessionEnded(SessionEndRecord end)
    {
        ArgumentNullException.ThrowIfNull(end);
        if (_manifestClosed)
        {
            return;
        }

        string ended = end.Stopped is not null ? "run_stopped"
            : end.ScenarioFinished ? "scenario_finished"
            : "caller_stopped";
        WriteClosing(ended, end.Stopped, null, end.LastFrameSeconds,
                     end.LastRenderedSeconds is { } rendered ? Rendered(rendered) : null, end.LastRenderedFrame,
                     end.Released);
        CloseFile();
    }

    /// <summary>
    /// Close the manifest now, saying why the run ended: the releases of the vehicles SUMO removed at the
    /// last SUMO frame read, as the session's end releases them, the admissions still waiting on a frame,
    /// the sun at the window's end, and the terminal row. Nothing is written after it.
    /// </summary>
    /// <param name="reason">
    /// The caller's own word for why it stopped -- its window closed, an operator stopped it -- written
    /// beside the session's; null for none.
    /// </param>
    /// <remarks>
    /// For a caller that reads the run's closing gates before it disposes the session, so the gate reads
    /// the terminal row. From the thread that advances the session, between advances. A second call does
    /// nothing.
    /// </remarks>
    public void Close(string? reason)
    {
        if (_manifestClosed)
        {
            return;
        }

        string ended = _report.Stopped is not null ? "run_stopped"
            : _scenarioFinished() ? "scenario_finished"
            : "caller_stopped";
        WriteClosing(ended, _report.Stopped, reason, _lastSumoFrameSeconds,
                     _lastFrame is null ? null : Rendered(_lastFrame.SimulatedTimeSeconds),
                     _lastFrame?.Frame, _leftAtAClose());
        CloseFile();
    }

    /// <summary>Close the file where it stands; a manifest closed this way has no terminal row.</summary>
    public void Dispose() => CloseFile();

    /// <summary>Close the file and delete it: a session that never started writes no manifest.</summary>
    internal void Discard()
    {
        CloseFile();
        File.Delete(Path);
    }

    private void CloseFile()
    {
        if (_fileClosed)
        {
            return;
        }

        _fileClosed = true;
        _manifestClosed = true;
        _json.Dispose();
        _file.Dispose();
    }

    /// <summary>Run a write, turning a failure to write into the refusal that stops the run.</summary>
    private void Guard(Action write)
    {
        try
        {
            write();
        }
        catch (IOException failed)
        {
            throw new CoSimSessionRefusedException(
                $"The run manifest could not be written at {Path}: {failed.Message}. A run whose record stops "
                + "while its frames go on has frames no record accounts for, so the run stops here.", failed);
        }
    }

    private void WriteOpened()
    {
        CoSimRunReport report = _report;
        ScenarioLockCheck compile = report.CompileLock;
        ScenarioLock? locked = compile.Lock;
        SupervisionPlan? plan = compile.Plan;
        SumoDistributionEditCheck edits = report.DistributionEdits;
        CoSimClock clock = report.Clock;
        SolarEpoch? epoch = _options.Epoch;
        IlluminationPolicy? policy = _options.Illumination;
        WriteRow(OpenedRow, json =>
        {
            json.WriteNumber("manifest_version", FormatVersion);
            json.WriteString("opened_wall_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            json.WritePropertyName("run");
            if (_options.RunManifestHeader is { } header)
            {
                using JsonDocument given = JsonDocument.Parse(header);
                given.RootElement.WriteTo(json);
            }
            else
            {
                json.WriteNullValue();
            }

            json.WriteStartObject("scenario");
            json.WriteString("config_path", _options.ScenarioPath);
            json.WriteString("world_package", _options.WorldPackagePath);
            json.WriteBoolean("compiled", compile.Compiled);
            json.WriteString("lock_path", compile.ExpectedLockPath);
            json.WriteString("scenario_id", locked?.ScenarioId);
            json.WriteString("specification_sha256", locked?.SpecificationSha256);
            json.WriteString("config_sha256", locked?.Config.Sha256);
            json.WriteString("routes_sha256", locked?.Routes.Sha256);
            json.WriteString("network_sha256", locked?.Network.Sha256);
            json.WriteString("additional_sha256", locked?.Additional?.Sha256);
            json.WriteString("catalogue_digest", report.CatalogueDigest);
            json.WriteString("epoch_digest", locked?.EpochDigest);
            json.WriteString("world_opendrive_sha256", locked?.WorldOpenDriveSha256);
            json.WriteString("world_network_fingerprint", locked?.WorldNetworkFingerprint);
            // Whether the compiler ran the scenario in SUMO alone before writing it; null where the lock
            // records no run, or there is no lock. A run that skipped it starts only if accepted.
            json.WritePropertyName("dry_run_ran");
            if (locked?.DryRun is { } dryRun)
            {
                json.WriteBooleanValue(dryRun.Ran);
            }
            else
            {
                json.WriteNullValue();
            }

            json.WriteBoolean("skipped_dry_run_accepted", compile.SkippedDryRunAccepted);
            json.WriteEndObject();

            json.WritePropertyName("plan");
            if (plan is null)
            {
                json.WriteNullValue();
            }
            else
            {
                json.WriteStartObject();
                json.WriteString("path", plan.Path);
                json.WriteString("sha256", plan.Sha256);
                json.WriteString("plan_id", plan.PlanId);
                json.WriteNumber("spec_version", plan.SpecVersion);
                json.WriteString("scenario_id", plan.ScenarioId);
                json.WriteNumber("instances", plan.Instances.Length);
                json.WriteNumber("series", plan.Series.Length);
                json.WriteNumber("cohorts", plan.Cohorts.Length);
                json.WriteNumber("entities", plan.Entities.Length);
                json.WriteEndObject();
            }

            json.WritePropertyName("vocabulary");
            if (plan is null)
            {
                json.WriteNullValue();
            }
            else
            {
                json.WriteStartObject();
                json.WriteNumber("vocabulary_version", plan.Vocabulary.CoreVersion);
                json.WriteString("vocabulary_digest", plan.Vocabulary.Digest);
                json.WriteStartArray("namespaces");
                foreach (AuthorNamespace space in plan.Vocabulary.Namespaces)
                {
                    json.WriteStartObject();
                    json.WriteString("namespace", space.Namespace);
                    json.WriteNumber("version", space.Version);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteStartObject("sumo");
            json.WriteString("release", report.Sumo.Release);
            json.WriteString("binary", report.Sumo.Binary);
            json.WriteNumber("seed", report.SumoSeed);
            json.WriteNumber("step_s", clock.SumoStepSeconds);
            WriteNumberOrNull(json, "step_override_s", report.SumoStepOverrideSeconds);
            json.WriteString("collision_action", edits.CollisionActionInForce);
            json.WriteString("collision_action_declared", edits.CollisionActionDeclared);
            json.WriteStartArray("teleport_triggers");
            foreach (SumoTeleportTrigger trigger in edits.TeleportTriggers)
            {
                json.WriteStartObject();
                json.WriteString("name", trigger.Name);
                json.WriteString("declared", trigger.Declared);
                json.WriteNumber("seconds", trigger.Seconds);
                json.WriteBoolean("enabled", trigger.Enabled);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteBoolean("teleporting_accepted", edits.TeleportingAccepted);
            json.WriteString("random_depart_offset_declared", edits.RandomDepartOffsetDeclared);
            json.WriteString("random_declared", edits.RandomDeclared);
            json.WriteNumber("scale", edits.Scale);
            json.WriteNumber("max_num_vehicles", edits.MaxNumVehicles);
            json.WriteNumber("max_depart_delay_s", edits.MaxDepartDelaySeconds);
            json.WriteNumber("lanechange_duration_s", report.LaneChanges.Seconds);
            json.WriteEndObject();

            json.WriteStartObject("clock");
            json.WriteNumber("sumo_step_s", clock.SumoStepSeconds);
            json.WriteNumber("world_delta_s", clock.WorldDeltaSeconds);
            json.WriteNumber("capture_hz", clock.CaptureRateHz);
            json.WriteNumber("world_ticks_per_sumo_step", clock.WorldTicksPerSumoStep);
            json.WriteNumber("world_ticks_per_capture", clock.WorldTicksPerCapture);
            json.WriteEndObject();

            json.WriteStartObject("window");
            json.WriteNumber("rendered_from_s", _options.WarmUpToSimulatedSecond);
            WriteNumberOrNull(json, "opens_at_s", _options.WindowOpensAtSimulatedSecond);
            json.WriteEndObject();

            json.WriteStartObject("render_set");
            json.WriteString("policy", report.RenderSetPolicy);
            json.WriteBoolean("limits", report.RenderSetLimits);
            WriteNumberOrNull(json, "capacity", report.RenderSetCapacity);
            WriteNumberOrNull(json, "draw_distance_m", _options.DrawDistanceMetres);
            json.WriteEndObject();

            WriteVehicleLights(json);

            json.WriteStartObject("solar");
            json.WritePropertyName("epoch");
            if (epoch is null)
            {
                json.WriteNullValue();
            }
            else
            {
                using JsonDocument declared = JsonDocument.Parse(epoch.CanonicalJson);
                declared.RootElement.WriteTo(json);
            }

            json.WriteBoolean("epoch_declared", epoch is not null);
            json.WriteString("epoch_block_sha256", epoch?.Digest);
            json.WritePropertyName("illumination_declared");
            WriteIllumination(json, policy);
            json.WritePropertyName("illumination_in_force");
            WriteIllumination(json, policy);
            json.WriteBoolean("epoch_honoured", epoch is not null && policy is { HonoursTheEpoch: true });
            json.WriteString("advance_mechanism", "per_tick_write");
            json.WriteEndObject();

            json.WriteString("world_truth_track", _options.WorldTruthTrackPath);
        });
    }

    /// <summary>
    /// The rule the vehicle lights follow for the whole run: whether the session drives them at all; the
    /// sun elevations the headlights come on below and go off above, read against the geometric elevation
    /// the world reports (<see cref="HeadlightRule"/>, doc 11 D11.9); and that brake lights and turn signals
    /// follow the signals SUMO reports for each vehicle (<see cref="VehicleLampMapping"/>, D11.8).
    /// </summary>
    /// <remarks>
    /// Written from the options the session runs under, which are the values its headlight rule was built
    /// from before the manifest was opened. The rule runs only on a sun the session bound and audits
    /// (<see cref="SumoDriveSession"/>, <c>HeadlightsForThisTick</c>): under a policy that leaves the sun
    /// alone <c>headlights_follow_sun</c> is false and every headlight stays off, while brake lights and
    /// turn signals still follow SUMO. Where the lights are not driven at all every body keeps the lights it
    /// was spawned with, and the elevations and the signal source are written null. Per-vehicle light state
    /// is not in the truth record; this row is where a reader learns what rule the imagery's lights follow.
    /// </remarks>
    private void WriteVehicleLights(Utf8JsonWriter json)
    {
        bool driven = _options.VehicleLampsDriven;
        bool followSun = driven && _options.Illumination is { BindsTheSun: true };
        json.WriteStartObject("vehicle_lights");
        json.WriteBoolean("driven", driven);
        json.WriteBoolean("headlights_follow_sun", followSun);
        WriteNumberOrNull(json, "headlights_on_below_deg", driven ? _options.HeadlightOnBelowDegrees : null);
        WriteNumberOrNull(json, "headlights_off_above_deg", driven ? _options.HeadlightOffAboveDegrees : null);
        json.WriteString("headlights_elevation", driven ? SolarElevationKinds.Name(SolarElevationKind.Geometric) : null);
        json.WriteString("brake_lights", driven ? SumoSignalsSource : null);
        json.WriteString("turn_signals", driven ? SumoSignalsSource : null);
        json.WriteEndObject();
    }

    private static void WriteIllumination(Utf8JsonWriter json, IlluminationPolicy? policy)
    {
        if (policy is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteString("policy", policy.Name);
        if (policy.Advances)
        {
            json.WriteNumber("rate_sun_s_per_sim_s", policy.Rate);
        }

        if (policy.FreezeAtCivilTimeOfDay is { } freezeAt)
        {
            json.WriteString("freeze_at_civil_time", freezeAt.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture));
        }

        json.WriteBoolean("freeze_date_advances", policy.FreezeDateAdvances);
        json.WriteBoolean("require_sun", policy.RequireSun);
        json.WriteString("note", policy.Note);
        json.WriteEndObject();
    }

    private void WriteAdmission(Admission admission, ulong? frame, uint? actor)
    {
        WriteRow(AdmittedRow, json =>
        {
            json.WriteNumber("sim_time_s", admission.AdmittedAtSeconds);
            json.WriteString("sumo_id", admission.VehicleId);
            json.WriteString("vtype_id", admission.TypeId);
            json.WriteString("reason", admission.Reason);
            WriteNumberOrNull(json, "frame", frame);
            WriteNumberOrNull(json, "actor_id", actor);
        });
        Admissions++;
    }

    private void WriteRelease(RenderedVehicleInterval released)
    {
        // A vehicle released before any frame rendered its admission -- one SUMO had only at frames read
        // after the last frame the run rendered -- is admitted first, so its rows read in order.
        int waiting = _pending.FindIndex(admission => admission.VehicleId == released.VehicleId);
        if (waiting >= 0)
        {
            Admission admission = _pending[waiting];
            _pending.RemoveAt(waiting);
            WriteAdmission(admission, null, released.Actor == 0 ? null : released.Actor);
        }

        string typeId = _held.Remove(released.VehicleId, out string? held) ? held : string.Empty;
        WriteRow(ReleasedRow, json =>
        {
            json.WriteNumber("sim_time_s", released.ReleasedAtSeconds);
            json.WriteString("sumo_id", released.VehicleId);
            json.WriteString("vtype_id", typeId);
            WriteNumberOrNull(json, "actor_id", released.Actor == 0 ? null : released.Actor);
            json.WriteNumber("admitted_s", released.AdmittedAtSeconds);
            json.WriteString("reason", ReasonName(released.ReleaseReason));
        });
        Releases++;
    }

    /// <summary>A release reason as the manifest writes it.</summary>
    private static string ReasonName(RenderSetReleaseReason reason) => reason switch
    {
        RenderSetReleaseReason.LeftTheSimulation => "left_the_simulation",
        RenderSetReleaseReason.SessionEnded => "session_ended",
        RenderSetReleaseReason.Vanished => "vanished",
        RenderSetReleaseReason.LeftTheRegion => "left_the_region",
        RenderSetReleaseReason.Capacity => "capacity",
        _ => reason.ToString(),
    };

    private void WriteCollisionBegan(SumoCollision collision, double frameSeconds)
    {
        WriteRow(CollisionBeganRow, json =>
        {
            json.WriteNumber("sim_time_s", frameSeconds);
            WriteCollision(json, collision);
        });
        Events++;
    }

    /// <summary>The spans the session closed since the last frame: every one it added to its report.</summary>
    private void WriteCollisionsEnded()
    {
        IReadOnlyList<CollisionSpan> spans = _report.CollisionSpans;
        for (; _collisionSpansWritten < spans.Count; _collisionSpansWritten++)
        {
            CollisionSpan span = spans[_collisionSpansWritten];
            _collisionsOpen.Remove((span.Collision.ColliderId, span.Collision.VictimId));
            WriteRow(CollisionEndedRow, json =>
            {
                json.WriteNumber("sim_time_s", span.EndedAtSeconds);
                WriteCollision(json, span.Collision);
                json.WriteNumber("began_s", span.BeganAtSeconds);
                json.WriteNumber("ended_s", span.EndedAtSeconds);
                WriteNumberOrNull(json, "collider_actor_id", span.ColliderActor == 0 ? null : span.ColliderActor);
                WriteNumberOrNull(json, "victim_actor_id", span.VictimActor == 0 ? null : span.VictimActor);
            });
            Events++;
        }
    }

    private static void WriteCollision(Utf8JsonWriter json, SumoCollision collision)
    {
        json.WriteString("collider", collision.ColliderId);
        json.WriteString("victim", collision.VictimId);
        json.WriteString("collider_vtype_id", collision.ColliderTypeId);
        json.WriteString("victim_vtype_id", collision.VictimTypeId);
        json.WriteString("kind", collision.Kind);
        json.WriteString("lane", collision.LaneId);
        json.WriteNumber("lane_pos_m", collision.LanePositionMetres);
    }

    private void WriteEvent(string row, double frameSeconds, string vehicleId, Action<Utf8JsonWriter>? more)
    {
        WriteRow(row, json =>
        {
            json.WriteNumber("sim_time_s", frameSeconds);
            json.WriteString("sumo_id", vehicleId);
            more?.Invoke(json);
        });
        Events++;
    }

    private void WriteWindowOpen(RenderedFrameRecord frame)
    {
        SolarLease? lease = _report.Sun;
        SolarEpoch? epoch = _options.Epoch;
        double begin = Rendered(frame.SimulatedTimeSeconds);
        WriteRow(WindowOpenRow, json =>
        {
            json.WriteNumber("window_index", 0);
            json.WriteNumber("sim_time_s", begin);
            json.WriteNumber("frame", frame.Frame);
            json.WriteNumber("begin_s", begin);
            json.WriteString("civil_begin", epoch is null ? null : SolarEpoch.FormatCivil(epoch.CivilInstantAt(begin)));
            WriteSun(json, frame.Sun, "begin");
            WriteNumberOrNull(json, "sun_time_zone_hours", lease?.AsFound?.TimeZoneHours);
            json.WriteBoolean("no_sun", lease is null ? frame.Sun is null : lease.NoSun);
            double? delta = lease is { } bound && epoch is not null
                ? (bound.Declared.SunAt(begin) - epoch.CivilInstantAt(begin).DateTime).TotalHours
                : null;
            WriteNumberOrNull(json, "declared_civil_vs_solar_delta_h", delta);
        });
    }

    /// <summary>
    /// The sun a frame's reading carries, under C9's names for the window's first or last capture tick, with
    /// its band; every field null where the frame carries none.
    /// </summary>
    private static void WriteSun(Utf8JsonWriter json, SolarReading? sun, string end)
    {
        json.WriteString($"solar_date_{end}", sun is { } read
            ? string.Create(CultureInfo.InvariantCulture, $"{read.Year:0000}-{read.Month:00}-{read.Day:00}")
            : null);
        WriteNumberOrNull(json, $"solar_time_{end}", sun?.SolarTimeHours);
        WriteNumberOrNull(json, $"sun_elevation_{end}_deg", sun?.ElevationDegrees);
        WriteNumberOrNull(json, $"sun_corrected_elevation_{end}_deg", sun?.CorrectedElevationDegrees);
        WriteNumberOrNull(json, $"sun_azimuth_{end}_deg", sun?.AzimuthDegrees);
        // Cut by the rule a capture's band is cut by, so the window's band and its captures' are one band.
        if (sun is { } reading
            && IlluminationBands.TryOfReported(reading.ElevationDegrees, reading.CorrectedElevationDegrees,
                                               out IlluminationBand band, out SolarElevationKind cutFrom))
        {
            json.WriteString($"illumination_band_{end}", IlluminationBands.Name(band));
            json.WriteString($"illumination_band_{end}_elevation", SolarElevationKinds.Name(cutFrom));
        }
        else
        {
            json.WriteNull($"illumination_band_{end}");
            json.WriteNull($"illumination_band_{end}_elevation");
        }

        if (sun is { } state)
        {
            json.WriteBoolean("advancing", state.Advancing);
            json.WriteNumber("rate", state.Rate);
        }
        else
        {
            json.WriteNull("advancing");
            json.WriteNull("rate");
        }
    }

    private void WriteClosing(string ended, CoSimRunStop? stopped, string? reason, double? lastSumoFrameSeconds,
                              double? lastRenderedSeconds, ulong? lastRenderedFrame,
                              IReadOnlyList<RenderedVehicleInterval> left)
    {
        Guard(() =>
        {
            // Removed by SUMO at the last SUMO frame read, and released by the run's end before the frame of
            // their last step rendered.
            foreach (RenderedVehicleInterval released in left)
            {
                WriteRelease(released);
            }

            // Admitted at the SUMO frames read after the last frame rendered, which no frame drew.
            foreach (Admission admission in _pending)
            {
                WriteAdmission(admission, null, null);
            }

            _pending.Clear();
            WriteDefects();
            WriteWindowEnd();
            WriteRow(ClosedRow, json =>
            {
                json.WriteString("ended", ended);
                json.WriteString("stage", stopped?.Stage.ToString());
                json.WriteString("cause", stopped?.Cause.Code());
                json.WriteString("caller_reason", reason);
                WriteNumberOrNull(json, "last_sumo_frame_s", lastSumoFrameSeconds);
                WriteNumberOrNull(json, "last_rendered_s", lastRenderedSeconds);
                WriteNumberOrNull(json, "last_rendered_frame", lastRenderedFrame);
                json.WriteNumber("render_admitted", Admissions);
                json.WriteNumber("render_released", Releases);
                json.WriteNumber("still_in_render_set", _held.Count);
                json.WriteNumber("events", Events);
                WriteSupervisionAtClose(json, ended);
                WriteDivergenceAtClose(json);
                json.WriteNumber("rows_before", Rows);
                json.WriteString("closed_wall_utc",
                                 DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            });
            _manifestClosed = true;
        });
    }

    /// <summary>
    /// What the terminal row says of the plan's intervals: how many opened and closed, the defects, every
    /// interval still open as the run ended and how the binder closes it once the session ends, and every
    /// one nothing in the run opened or closed.
    /// </summary>
    /// <remarks>
    /// The binder closes the intervals still open when the session ends -- <c>scenario_end</c> where SUMO had
    /// nothing left, <c>capture_window_end</c> otherwise -- after this row, which is the manifest's last, so
    /// they are listed here as open rather than written as closed. A manifest with no terminal row is a run
    /// that was interrupted, and an interval opened in it and never closed was open at the interruption.
    /// </remarks>
    private void WriteSupervisionAtClose(Utf8JsonWriter json, string ended)
    {
        json.WriteNumber("intervals_opened", IntervalsOpened);
        json.WriteNumber("intervals_closed", IntervalsClosed);
        json.WriteNumber("supervision_defects", SupervisionDefects);
        json.WriteStartArray("open_intervals");
        foreach (SupervisionIntervalRecord open in _openIntervals.Values)
        {
            json.WriteStartObject();
            WriteIntervalRow(json, open);
            WriteNumberOrNull(json, "opened_s", open.CommittedStartSeconds ?? open.DeclaredStartSeconds);
            json.WriteBoolean("begun_before_window", open.BegunBeforeWindow);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteString("open_intervals_close_as", CoreVocabulary.Name(
            ended == "scenario_finished" ? ClosedBy.ScenarioEnd : ClosedBy.CaptureWindowEnd));
        json.WriteStartArray("never_opened");
        if (_report.CompileLock.Plan is { } plan)
        {
            foreach (PatternInstance instance in plan.Instances)
            {
                foreach (PlannedInterval planned in instance.Intervals)
                {
                    if (!_bound.Contains((instance.InstanceId, planned.EntityId, planned.Phase)))
                    {
                        json.WriteStartObject();
                        json.WriteString("instance_id", instance.InstanceId);
                        json.WriteString("participant", planned.EntityId);
                        json.WriteString("phase", planned.Phase);
                        json.WriteEndObject();
                    }
                }
            }
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// What the terminal row says of the difference between what the bridge commanded and what the world
    /// applied, over the whole run (06 §4.3): the comparisons taken and the vehicle-ticks nothing read back;
    /// the worst and mean position, metres; the worst yaw, pitch and roll, degrees; the worst and mean
    /// velocity, metres per second, with the mean commanded speed they are read against; and the SUMO
    /// vehicle, instant, tick and body of the worst position and of the worst velocity.
    /// </summary>
    /// <remarks>
    /// Read from the session's report, which accumulates every comparison the session takes
    /// (<see cref="CoSimRunReport.AddDivergence"/>), so the block is the same whether the caller closes the
    /// manifest or the session's end does. A run that compared nothing writes zero samples and null for
    /// both worst cases; with vehicle-ticks that nothing read back beside them, that is a run that measured
    /// nothing while writing poses, which a reader must not take for a run that measured zero.
    /// </remarks>
    private void WriteDivergenceAtClose(Utf8JsonWriter json)
    {
        CoSimRunReport report = _report;
        json.WriteStartObject("bridge_divergence");
        json.WriteNumber("samples", report.DivergenceSamples);
        json.WriteNumber("vehicle_ticks_with_no_read_back", report.VehicleTicksWithNoReadBack);
        json.WriteNumber("worst_position_m", report.WorstPositionDivergenceMetres);
        json.WriteNumber("mean_position_m", report.MeanPositionDivergenceMetres);
        json.WriteNumber("worst_yaw_deg", report.WorstYawDivergenceDegrees);
        json.WriteNumber("worst_pitch_deg", report.WorstPitchDivergenceDegrees);
        json.WriteNumber("worst_roll_deg", report.WorstRollDivergenceDegrees);
        json.WriteNumber("worst_velocity_m_per_s", report.WorstVelocityDivergenceMetresPerSecond);
        json.WriteNumber("mean_velocity_m_per_s", report.MeanVelocityDivergenceMetresPerSecond);
        json.WriteNumber("mean_commanded_speed_m_per_s", report.MeanCommandedSpeedMetresPerSecond);
        WriteWorstDivergence(json, "worst_position_on", report.WorstDivergence);
        WriteWorstDivergence(json, "worst_velocity_on", report.WorstVelocityDivergence);
        json.WriteEndObject();
    }

    /// <summary>
    /// The vehicle and instant one worst figure was measured on: SUMO's id for the vehicle, the simulated
    /// instant on TraCI's clock the pose was computed for, the session's tick and the body it was written to.
    /// </summary>
    private static void WriteWorstDivergence(Utf8JsonWriter json, string name, PoseDivergence? worst)
    {
        json.WritePropertyName(name);
        if (worst is not { } sample)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteString("sumo_id", sample.VehicleId);
        json.WriteNumber("sim_time_s", Rendered(sample.SimulatedTimeSeconds));
        json.WriteNumber("tick", sample.TickIndex);
        json.WriteNumber("actor_id", sample.Actor);
        json.WriteEndObject();
    }

    /// <summary>The row an interval is: its instance, its participant and its phase (06 D6.8).</summary>
    private static (string Instance, string? Participant, string Phase) KeyOf(SupervisionIntervalRecord interval) =>
        (interval.InstanceId, interval.EntityId, interval.Phase);

    private static void WriteIntervalRow(Utf8JsonWriter json, SupervisionIntervalRecord interval)
    {
        json.WriteString("instance_id", interval.InstanceId);
        json.WriteString("participant", interval.EntityId);
        json.WriteString("phase", interval.Phase);
    }

    /// <summary>
    /// An interval's onsets: what its author declared, what SUMO committed by the bridge's clock, and the
    /// rendered frame that showed it, each null where it did not happen.
    /// </summary>
    private static void WriteOnsets(Utf8JsonWriter json, SupervisionIntervalRecord interval)
    {
        WriteNumberOrNull(json, "declared_start_s", interval.DeclaredStartSeconds);
        WriteNumberOrNull(json, "declared_end_s", interval.DeclaredEndSeconds);
        WriteNumberOrNull(json, "declared_duration_s", interval.DeclaredDurationSeconds);
        WriteNumberOrNull(json, "committed_start_s", interval.CommittedStartSeconds);
        WriteNumberOrNull(json, "observed_start_s", interval.ObservedStartSeconds is { } observed ? Rendered(observed) : null);
        WriteNumberOrNull(json, "observed_start_frame", interval.ObservedStartFrame);
        json.WriteBoolean("begun_before_window", interval.BegunBeforeWindow);
    }

    /// <summary>The supervision binder's seam defects found since the last were written.</summary>
    private void WriteDefects()
    {
        if (_binder is not { } binder)
        {
            return;
        }

        IReadOnlyList<string> defects = binder.Defects;
        for (; _defectsWritten < defects.Count; _defectsWritten++)
        {
            string defect = defects[_defectsWritten];
            WriteRow(DefectRow, json =>
            {
                // Found at or before the SUMO frame last read; the defect names its own instants.
                WriteNumberOrNull(json, "found_by_s", _lastSumoFrameSeconds);
                json.WriteString("defect", defect);
            });
            SupervisionDefects++;
        }
    }

    /// <summary>
    /// The supervision plan the compile lock bound, row by row: every instance with its participants and
    /// the intervals it declares, every recurring series with its slots, and every cohort, each as declared.
    /// </summary>
    private void WritePlan()
    {
        if (_report.CompileLock.Plan is not { } plan)
        {
            return;
        }

        foreach (PatternInstance instance in plan.Instances)
        {
            WriteRow(InstanceRow, json =>
            {
                json.WriteString("instance_id", instance.InstanceId);
                json.WriteString("supervision", CoreVocabulary.Name(instance.Supervision));
                WriteStrings(json, "labels", instance.Labels);
                WriteParameters(json, instance.Parameters);
                WriteStringsOrNull(json, "hard_negative_for", instance.HardNegativeFor);
                WriteStrings(json, "aoi_refs", instance.AoiRefs);
                json.WriteStartArray("participants");
                foreach (InstanceParticipant participant in instance.Participants)
                {
                    json.WriteStartObject();
                    json.WriteString("participant", participant.EntityId);
                    json.WriteString("sumo_id", participant.SumoId);
                    json.WriteString("role", participant.Role);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("intervals");
                foreach (PlannedInterval planned in instance.Intervals)
                {
                    json.WriteStartObject();
                    json.WriteString("participant", planned.EntityId);
                    json.WriteString("phase", planned.Phase);
                    json.WriteString("anchor_start", planned.Anchor?.Start.Spelled);
                    json.WriteString("anchor_end", planned.Anchor?.End?.Spelled);
                    WriteNumberOrNull(json, "declared_start_s", planned.DeclaredStartSeconds);
                    json.WriteString("declared_start_civil", planned.DeclaredStartCivil);
                    WriteNumberOrNull(json, "declared_end_s", planned.DeclaredEndSeconds);
                    json.WriteString("declared_end_civil", planned.DeclaredEndCivil);
                    WriteNumberOrNull(json, "declared_duration_s", planned.DeclaredDurationSeconds);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            });
        }

        foreach (RecurringSeries series in plan.Series)
        {
            WriteRow(SeriesRow, json =>
            {
                json.WriteString("series_id", series.SeriesId);
                json.WriteString("rota_ref", series.RotaRef);
                json.WriteString("cadence", CoreVocabulary.Name(series.Cadence));
                json.WriteString("member_role", series.MemberRole);
                json.WriteString("supervision", CoreVocabulary.Name(series.Supervision));
                WriteStrings(json, "labels", series.Labels);
                WriteParameters(json, series.Parameters);
                WriteStringsOrNull(json, "hard_negative_for", series.HardNegativeFor);
                json.WriteStartArray("slots");
                foreach (SeriesSlot slot in series.Slots)
                {
                    json.WriteStartObject();
                    json.WriteString("slot_key", slot.SlotKey);
                    json.WriteString("aoi_ref", slot.AoiRef);
                    json.WriteNumber("declared_start_s", slot.DeclaredStartSeconds);
                    WriteNumberOrNull(json, "declared_end_s", slot.DeclaredEndSeconds);
                    json.WriteString("entity_id", slot.EntityId);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            });
        }

        foreach (CohortSupervision cohort in plan.Cohorts)
        {
            WriteRow(CohortRow, json =>
            {
                json.WriteString("flow_id", cohort.FlowId);
                json.WriteString("supervision", CoreVocabulary.Name(cohort.Supervision));
                WriteStrings(json, "labels", cohort.Labels);
                WriteParameters(json, cohort.Parameters);
            });
        }
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (string value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    private static void WriteStringsOrNull(Utf8JsonWriter json, string name, ImmutableArray<string>? values)
    {
        if (values is { } present)
        {
            WriteStrings(json, name, present);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteParameters(Utf8JsonWriter json, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        json.WriteStartObject("parameters");
        foreach ((string key, JsonElement value) in parameters)
        {
            json.WritePropertyName(key);
            value.WriteTo(json);
        }

        json.WriteEndObject();
    }

    private void WriteWindowEnd()
    {
        RenderedFrameRecord? last = _lastWindowCapture;
        SolarAudit? audit = _report.SunAudit;
        SolarLease? lease = _report.Sun;
        SolarEpoch? epoch = _options.Epoch;
        IlluminationPolicy? policy = _options.Illumination;
        bool noSun = lease is null ? last?.Sun is null : lease.NoSun;
        bool withinTolerance = audit is not null && audit.Failure is null;
        WriteRow(WindowEndRow, json =>
        {
            json.WriteNumber("window_index", 0);
            double? endSeconds = last is null ? null : Rendered(last.SimulatedTimeSeconds);
            WriteNumberOrNull(json, "sim_time_s", endSeconds);
            WriteNumberOrNull(json, "frame", last?.Frame);
            WriteNumberOrNull(json, "end_s", endSeconds);
            json.WriteString("civil_end", epoch is not null && last is not null
                ? SolarEpoch.FormatCivil(epoch.CivilInstantAt(endSeconds!.Value))
                : null);
            WriteSun(json, last?.Sun, "end");
            json.WriteNumber("capture_ticks", _windowCaptureTicks);

            json.WriteStartObject("solar_residual");
            WriteNumberOrNull(json, "max_delta_solar_s",
                              audit?.WorstClock is { } clock ? Math.Abs(clock.ClockResidualSeconds) : null);
            WriteNumberOrNull(json, "max_delta_elev_deg", audit?.WorstAngle?.AngleResidualDegrees);
            WriteNumberOrNull(json, "max_delta_corrected_deg",
                              audit?.WorstCorrected?.CorrectedResidualDegrees is { } corrected ? Math.Abs(corrected) : null);
            WriteNumberOrNull(json, "max_at_tick", audit?.WorstAngle?.TickIndex);
            WriteNumberOrNull(json, "max_at_sim_time_s", audit?.WorstAngle?.SimulatedSeconds);
            WriteNumberOrNull(json, "tolerance_s", audit?.ToleranceSeconds);
            WriteNumberOrNull(json, "tolerance_elev_deg", audit?.ToleranceDegrees);
            json.WriteNumber("audited_ticks", audit?.AuditedTicks ?? 0);
            json.WriteNumber("capture_ticks", _windowCaptureTicks);
            json.WriteBoolean("within_tolerance", withinTolerance);
            json.WriteBoolean("audit_skipped", audit is null);
            json.WriteEndObject();

            json.WriteBoolean("no_sun", noSun);
            // A fact, not a verdict: an epoch was declared, the policy bound the sun, the world held one,
            // and the audit held within its tolerance over the window. What a consumer makes of it is theirs.
            json.WriteBoolean("sun_matched_declaration", epoch is not null && policy is { BindsTheSun: true } && !noSun
                                                         && withinTolerance);
        });
    }

    /// <summary>Compose one row, append it as one line and flush it, before anything else is composed.</summary>
    private void WriteRow(string row, Action<Utf8JsonWriter> body)
    {
        _buffer.Clear();
        _json.Reset(_buffer);
        _json.WriteStartObject();
        _json.WriteString("row", row);
        body(_json);
        _json.WriteEndObject();
        _json.Flush();
        _file.Write(_buffer.WrittenSpan);
        _file.WriteByte((byte)'\n');
        _file.Flush();
        Rows++;
    }

    /// <summary>
    /// A rendered frame's instant as the manifest writes it, to the microsecond: the rendered clock is a
    /// running sum of the world's delta, so it reaches an instant to within rounding rather than exactly.
    /// </summary>
    private static double Rendered(double seconds) => Math.Round(seconds, 6);

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, long? value)
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

    /// <summary>A vehicle admitted to the render set whose admission row waits for a frame to draw it.</summary>
    private sealed record Admission(string VehicleId, string TypeId, double AdmittedAtSeconds, string Reason);
}
