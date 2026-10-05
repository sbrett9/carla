using System.Globalization;
using CarlaNet.Recording;
using CarlaNet.Sumo;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

/// <summary>
/// Binds the supervision plan to what SUMO does in a run: opens and closes each of the plan's intervals
/// on the events that commit it, at the bridge's clock, and states what is in force for each vehicle,
/// and for the world, as it changes, for the session to put on the server.
/// </summary>
/// <remarks>
/// <para><b>It binds rows; it never makes one</b> (<c>06_Truth_And_Annotation.md</c> §3.6, D6.8). It is
/// built from the plan and the step events and from nothing else: every interval it holds is one the plan
/// declares, every annotation it states is a copy of a plan row's instance, labels, phase and role, and
/// what the run fills in -- committed and observed onsets, the close and why, the gaps in drawing -- sits
/// beside the row (<see cref="SupervisionIntervalRecord"/>). It holds no solar type and reads nothing of
/// the sun (D6.21).</para>
///
/// <para><b>What commits an interval</b> (§3.3). An anchored interval opens and closes on the events its
/// anchor names, at the TraCI clock of the step whose event list announced them, never at SUMO's own stamp,
/// which is a step earlier: <c>depart</c> on the vehicle's insertion; <c>stop:i</c> and <c>stop_end:i</c>
/// on its starting and ending a stop, the index being how many stops SUMO says the vehicle had completed
/// (<see cref="SumoVehicleQueries.CompletedStops"/>, asked only then and only for a vehicle with a stop
/// anchor); <c>phase:i</c> when SUMO first reports the vehicle at or past the route index the phase is
/// entered at (<see cref="SumoVehicleQueries.RouteIndex"/>, asked when such a vehicle's edge changes). An
/// unanchored interval has no committing event: it opens and closes on its declared seconds, its committed
/// onset null, as the owner ruled.</para>
///
/// <para><b>What is observed</b>, inside the window and only where a world renders: a departure's onset is
/// the first frame that draws the vehicle -- since the session draws a vehicle from the frame SUMO first
/// reports it in (03 D3.6), it equals the committed onset, and any gap is recorded as a defect -- and a
/// stop's is the first frame whose applied speed holds at or below 0.15 m/s, the predicate and threshold of
/// §3.3. The body is interpolated into the stop over the step that ends at it, so a stop's observed onset
/// may come up to one SUMO step before its committed one. An onset that did not happen is null, never
/// substituted.</para>
///
/// <para><b>Why an interval closes</b> (§3.4): <c>trigger</c> on the event or the declared second that
/// ends it -- or <c>physical_predicate_never_held</c> where a stop it opened on ended while the body was
/// drawn and never stood still; <c>entity_arrived</c> where SUMO lists the vehicle among its arrivals;
/// <c>sumo_removed</c> where it took the vehicle out otherwise -- a teleport off the network, or gone between
/// two steps; <c>never_inserted</c> where SUMO gave up inserting it; <c>slot_unrealised</c> at an absence's
/// declared end; <c>capture_window_end</c> or <c>scenario_end</c> where the session ends with it open. A
/// body under an optional render-set limit is lost and regained without closing anything: the annotation
/// is the SUMO vehicle's for its life, and a span with no body is recorded as not drawn, as the owner
/// ruled. <c>render_released</c>, a body the CARLA side lost, has no producer yet.</para>
///
/// <para><b>Nothing is invented for the time before the window</b>, as the owner ruled. The binder sees
/// SUMO from the first frame the session reads. A plan subject already in the simulation there is read
/// back once -- its departure, the stops it has completed and the one under way, its route index -- and
/// each event SUMO stamped becomes the committed onset of an interval that began before the window, at
/// the stamp plus one SUMO step, which is the clock of the step that would have listed it. A phase entered
/// before then has no recoverable instant: its interval is open with no committed onset. An interval that
/// began before the window has no observed onset, and nothing is put in force for the frames before the
/// window: the session's table holds the state as of each frame from the window's first.</para>
///
/// <para><b>What it states</b> (<see cref="DriveSupervision"/>), per SUMO vehicle while SUMO has it: the
/// annotations of every interval open for it, of every instance it participates in with no interval of
/// its own (in force for its whole life), of every series one of whose slots it realises (named
/// <c>series:&lt;series_id&gt;</c>, as the plan's entities refer to it), and of its flow's cohort if the
/// flow is annotated (named <c>cohort:&lt;flow_id&gt;</c>, for every <c>&lt;flow_id&gt;.&lt;n&gt;</c>). The state
/// is annotated if any of them is, otherwise nominal if any is, and the annotations are those of that
/// state. An unlabelled vehicle is never stated. Absences open and close on their declared seconds. Each
/// change takes effect from the frame at its instant: it is put in force once the next tick to be rendered
/// has reached it.</para>
///
/// <para><b>A plan subject SUMO never inserts fails the run</b>, as the owner ruled: its intervals are
/// closed <c>never_inserted</c>, and the step that showed it refuses, naming the vehicle.</para>
///
/// <para>Every interval it opens and closes is told, as it happens, to each
/// <see cref="ISupervisionIntervalSink"/> it was given.</para>
/// </remarks>
public sealed class SupervisionBinder : ISumoStepObserver
{
    /// <summary>
    /// The applied speed at or below which a rendered body stands still: the threshold of the executor's
    /// stop predicate, kept (06 §3.3).
    /// </summary>
    public const double StandstillMetresPerSecond = 0.15;

    private const double Tolerance = 1e-6;

    private readonly SupervisionPlan _plan;
    private readonly DriveSupervision _supervision;
    private readonly double _sumoStepSeconds;
    private readonly double _worldDeltaSeconds;
    private readonly Func<double> _windowOpensAt;
    private readonly ISupervisionIntervalSink[] _sinks;
    private readonly List<BoundInterval> _intervals = [];
    private readonly Dictionary<string, Subject> _subjects = new(StringComparer.Ordinal);
    private readonly List<Subject> _phaseSubjects = [];
    private readonly HashSet<string> _readBack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CohortSupervision> _cohorts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cohortMembers = new(StringComparer.Ordinal);
    private readonly List<(double At, BoundInterval Interval, bool Opens)> _timed = [];
    private readonly PriorityQueue<Change, (double At, long Order)> _pending = new();
    private readonly HashSet<BoundInterval> _watchingEntry = [];
    private readonly HashSet<BoundInterval> _watchingStandstill = [];
    private readonly HashSet<string> _drawn = new(StringComparer.Ordinal);
    private readonly List<string> _defects = [];
    private readonly List<(Subject Subject, VehicleNotInserted Gone)> _dropped = [];
    private int _nextTimed;
    private long _order;
    private bool _started;
    private bool _ended;
    private double _nextTickSeconds;

    /// <summary>Bind <paramref name="plan"/> to a run, stating what is in force on <paramref name="supervision"/>.</summary>
    /// <param name="plan">The plan the scenario's compile lock binds.</param>
    /// <param name="supervision">
    /// Where the session takes what is in force from. Bound to the plan here, which starts it afresh.
    /// </param>
    /// <param name="clock">The session's clock: SUMO's step, and the world's delta between rendered frames.</param>
    /// <param name="windowOpensAtSeconds">
    /// The simulated instant the capture window opens, read when it is first needed: the session knows it
    /// once SUMO has been fast-forwarded, before it tells anyone of the first frame.
    /// </param>
    /// <param name="sinks">What is told every interval opened and closed.</param>
    public SupervisionBinder(SupervisionPlan plan, DriveSupervision supervision, CoSimClock clock,
                             Func<double> windowOpensAtSeconds, IEnumerable<ISupervisionIntervalSink>? sinks = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(supervision);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(windowOpensAtSeconds);
        _plan = plan;
        _supervision = supervision;
        _sumoStepSeconds = clock.SumoStepSeconds;
        _worldDeltaSeconds = clock.WorldDeltaSeconds;
        _windowOpensAt = windowOpensAtSeconds;
        _sinks = sinks is null ? [] : [.. sinks];
        Index(plan);
        supervision.Bind(new SupervisionPlanIdentity(plan.PlanId, plan.Vocabulary.CoreVersion,
                                                     plan.Vocabulary.Digest));
    }

    /// <summary>The plan bound.</summary>
    public SupervisionPlan Plan => _plan;

    /// <summary>
    /// Every interval the plan declares, in its order, as the run has bound it so far: one record per row,
    /// whatever happened.
    /// </summary>
    public IReadOnlyList<SupervisionIntervalRecord> Intervals => [.. _intervals.Select(interval => interval.Record())];

    /// <summary>
    /// What the binder found wrong with the seam rather than the scenario: a departure observed on another
    /// frame than the one SUMO committed it at, a stop the body never stood still for, a stop SUMO made on
    /// another lane than the anchor names. Each in one line.
    /// </summary>
    public IReadOnlyList<string> Defects => _defects;

    /// <inheritdoc/>
    public void OnSumoStep(SumoStepRecord step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Observe(step, step.Vehicles is { } queries ? new SessionAnswers(queries) : NoAnswers.Instance);
    }

    /// <inheritdoc/>
    public void OnFrameRendered(RenderedFrameRecord frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_ended)
        {
            return;
        }

        if (frame.InWindow)
        {
            ObserveEntries(frame);
            ObserveStandstills(frame);
        }

        _nextTickSeconds = frame.SimulatedTimeSeconds + _worldDeltaSeconds;
        PutInForce();
    }

    /// <inheritdoc/>
    public void OnSessionEnded(SessionEndRecord end)
    {
        ArgumentNullException.ThrowIfNull(end);
        if (_ended)
        {
            return;
        }

        _ended = true;
        double at = end.LastRenderedSeconds ?? end.LastFrameSeconds;
        ClosedBy reason = end.ScenarioFinished ? ClosedBy.ScenarioEnd : ClosedBy.CaptureWindowEnd;
        foreach (BoundInterval interval in _intervals.Where(interval => interval.Status == SupervisionIntervalStatus.Open))
        {
            Close(interval, reason, at, committedEnd: null);
        }
    }

    /// <summary>One SUMO frame, with the answers to the questions the binder puts about it.</summary>
    internal void Observe(SumoStepRecord step, IVehicleAnswers answers)
    {
        if (_ended)
        {
            return;
        }

        double at = step.FrameSeconds;
        HashSet<string> readBack = _readBack;
        readBack.Clear();
        AdvanceDeclared(at);
        if (!_started)
        {
            _started = true;
            _nextTickSeconds = at;
            ReadBack(step, answers, at, readBack);
        }

        SumoStepEvents events = step.Events;
        foreach (string id in events.Departed)
        {
            if (!readBack.Contains(id))
            {
                Departed(id, at, committed: at);
            }
        }

        foreach (string id in events.StopsStarted)
        {
            if (!readBack.Contains(id) && StopWatcher(id) is { } subject
                && answers.CompletedStops(id) is { } completed)
            {
                StopStarted(subject, completed.Count, at, laneId: Lane(step, id));
            }
        }

        foreach (string id in events.StopsEnded)
        {
            if (!readBack.Contains(id) && StopWatcher(id) is { } subject
                && answers.CompletedStops(id) is { Count: > 0 } completed)
            {
                StopEnded(subject, completed.Count - 1, at, completed[^1].LaneId, committed: at);
            }
        }

        WatchPhases(step, answers, at, readBack);

        foreach (string id in events.Arrived)
        {
            Removed(id, at, events.TeleportsStarted.Contains(id) ? ClosedBy.SumoRemoved : ClosedBy.EntityArrived);
        }

        foreach (string id in step.Vanished)
        {
            Removed(id, at, ClosedBy.SumoRemoved);
        }

        foreach (VehicleNotInserted gone in step.NotInserted)
        {
            if (_subjects.TryGetValue(gone.VehicleId, out Subject? subject))
            {
                NeverInserted(subject, gone);
            }
        }

        TrackDrawing(step, at);
        PutInForce();
        RefuseWhatWasDropped();
    }

    // -- the plan ----------------------------------------------------------------------------------------

    private void Index(SupervisionPlan plan)
    {
        foreach (PatternInstance instance in plan.Instances)
        {
            if (instance.Realisation == Realisation.Absent)
            {
                foreach (PlannedInterval vacancy in instance.Intervals)
                {
                    var interval = new BoundInterval(instance, vacancy, string.Empty, null);
                    _intervals.Add(interval);
                    Declare(interval);
                }

                continue;
            }

            Dictionary<string, (Subject Subject, string Role)> byEntity = new(StringComparer.Ordinal);
            foreach (InstanceParticipant participant in instance.Participants)
            {
                Subject subject = SubjectOf(participant.SumoId);
                byEntity[participant.EntityId] = (subject, participant.Role);
            }

            foreach (InstanceParticipant participant in instance.Participants)
            {
                (Subject subject, string role) = byEntity[participant.EntityId];
                List<BoundInterval> own = [];
                foreach (PlannedInterval planned in instance.Intervals.Where(
                             planned => planned.EntityId == participant.EntityId))
                {
                    var interval = new BoundInterval(instance, planned, role, subject);
                    _intervals.Add(interval);
                    subject.Intervals.Add(interval);
                    own.Add(interval);
                    if (planned.Anchor is null)
                    {
                        Declare(interval);
                    }
                }

                subject.Participations.Add(new Participation(instance, role, own));
            }
        }

        foreach (RecurringSeries series in plan.Series.Where(series => series.Supervision != SupervisionState.Unlabelled))
        {
            foreach (SeriesSlot slot in series.Slots)
            {
                if (slot.RealisedBy is { } member)
                {
                    SubjectOf(member).Series.Add(series);
                }
            }
        }

        foreach (CohortSupervision cohort in plan.Cohorts.Where(cohort => cohort.Supervision == SupervisionState.Annotated))
        {
            _cohorts[cohort.FlowId] = cohort;
        }

        _phaseSubjects.AddRange(_subjects.Values.Where(subject => subject.Anchors(AnchorEvent.Phase)));

        // In the order their instants come, an opening before a closing at the same instant, so an
        // interval declared with no length opens and closes rather than the other way round.
        _timed.Sort((left, right) =>
        {
            int order = left.At.CompareTo(right.At);
            return order != 0 ? order : right.Opens.CompareTo(left.Opens);
        });
    }

    private Subject SubjectOf(string id)
    {
        if (!_subjects.TryGetValue(id, out Subject? subject))
        {
            subject = new Subject(id);
            _subjects[id] = subject;
        }

        return subject;
    }

    /// <summary>An interval that opens and closes on its declared seconds: unanchored, or an absence's vacancy.</summary>
    private void Declare(BoundInterval interval)
    {
        if (interval.Planned.DeclaredStartSeconds is { } start)
        {
            _timed.Add((start, interval, true));
        }

        if (interval.Planned.DeclaredEndSeconds is { } end)
        {
            _timed.Add((end, interval, false));
        }
    }

    // -- the run ------------------------------------------------------------------------------------------

    /// <summary>Open and close every declared interval whose instant the frame has reached.</summary>
    private void AdvanceDeclared(double frameSeconds)
    {
        while (_nextTimed < _timed.Count && _timed[_nextTimed].At <= frameSeconds + Tolerance)
        {
            (double at, BoundInterval interval, bool opens) = _timed[_nextTimed++];
            bool absence = interval.Subject is null;
            if (opens && interval.Status == SupervisionIntervalStatus.Planned)
            {
                Open(interval, committed: null, startedAt: at);
                if (absence)
                {
                    Schedule(new Change(at, Opened: Absence(interval)));
                }
                else
                {
                    Restate(interval.Subject!.Id, at);
                }
            }
            else if (!opens && interval.Status == SupervisionIntervalStatus.Open)
            {
                Close(interval, absence ? ClosedBy.SlotUnrealised : ClosedBy.Trigger, at, committedEnd: null);
                if (absence)
                {
                    Schedule(new Change(at, Closed: interval.Instance.InstanceId));
                }
                else
                {
                    Restate(interval.Subject!.Id, at);
                }
            }
        }
    }

    /// <summary>
    /// The first frame: read back from SUMO what each plan subject already in the simulation did before
    /// it, and every annotated flow's members already driving.
    /// </summary>
    private void ReadBack(SumoStepRecord step, IVehicleAnswers answers, double at, HashSet<string> readBack)
    {
        foreach (Subject subject in _subjects.Values)
        {
            if (!step.Frames.TryGetValue(subject.Id, out CoSimVehicleFrame frame))
            {
                continue;
            }

            readBack.Add(subject.Id);
            double? departed = subject.Anchors(AnchorEvent.Depart) && answers.Departure(subject.Id) is { } stamp
                ? stamp + _sumoStepSeconds
                : null;
            Departed(subject.Id, at, departed);

            if (subject.Anchors(AnchorEvent.Phase))
            {
                if (answers.RouteIndex(subject.Id) is { } routeIndex)
                {
                    // Entered before the window at an instant nothing recorded: open, with no time.
                    PhaseReached(subject, routeIndex, at, committed: null);
                }

                subject.LastEdge = frame.EdgeId;
            }

            if (subject.Anchors(AnchorEvent.Stop) || subject.Anchors(AnchorEvent.StopEnd))
            {
                IReadOnlyList<SumoStop> completed = answers.CompletedStops(subject.Id) ?? [];
                for (int index = 0; index < completed.Count; index++)
                {
                    StopStarted(subject, index, at, completed[index].LaneId,
                                committed: Bridged(completed[index].ArrivalSeconds));
                    StopEnded(subject, index, at, completed[index].LaneId,
                              committed: Bridged(completed[index].DepartureSeconds));
                }

                if (answers.UpcomingStops(subject.Id) is [{ ArrivalSeconds: { } arrived, DepartureSeconds: null } underWay, ..])
                {
                    StopStarted(subject, completed.Count, at, underWay.LaneId, committed: arrived + _sumoStepSeconds);
                }
            }
        }

        foreach (string id in step.Frames.Keys)
        {
            if (CohortOf(id) is not null && _cohortMembers.Add(id))
            {
                Restate(id, at);
            }
        }
    }

    /// <summary>SUMO's own stamp as the bridge's clock: one step later, the step that listed it.</summary>
    private double? Bridged(double? stamp) => stamp is { } seconds ? seconds + _sumoStepSeconds : null;

    private void Departed(string id, double at, double? committed)
    {
        if (_subjects.TryGetValue(id, out Subject? subject) && !subject.Alive)
        {
            subject.Alive = true;
            foreach (BoundInterval interval in subject.Planned(AnchorEvent.Depart))
            {
                Open(interval, committed, startedAt: committed);
            }

            Restate(id, at);
        }

        if (CohortOf(id) is not null && _cohortMembers.Add(id))
        {
            Restate(id, at);
        }
    }

    /// <summary>Overload for a stop SUMO listed as started at this frame.</summary>
    private void StopStarted(Subject subject, int index, double at, string? laneId) =>
        StopStarted(subject, index, at, laneId, committed: at);

    private void StopStarted(Subject subject, int index, double at, string? laneId, double? committed)
    {
        bool changed = false;
        foreach (BoundInterval interval in subject.Planned(AnchorEvent.Stop, index))
        {
            CheckLane(subject, interval.Planned.Anchor!.Start, laneId);
            Open(interval, committed, startedAt: committed);
            changed = true;
        }

        foreach (BoundInterval interval in subject.Open(AnchorEvent.Stop, index))
        {
            CheckLane(subject, interval.Planned.Anchor!.End!, laneId);
            Close(interval, ClosedBy.Trigger, committed, committedEnd: committed);
            changed = true;
        }

        if (changed)
        {
            Restate(subject.Id, at);
        }
    }

    private void StopEnded(Subject subject, int index, double at, string? laneId, double? committed)
    {
        bool changed = false;
        foreach (BoundInterval interval in subject.Planned(AnchorEvent.StopEnd, index))
        {
            CheckLane(subject, interval.Planned.Anchor!.Start, laneId);
            Open(interval, committed, startedAt: committed);
            changed = true;
        }

        foreach (BoundInterval interval in subject.Open(AnchorEvent.StopEnd, index))
        {
            CheckLane(subject, interval.Planned.Anchor!.End!, laneId);
            bool neverStood = interval.Planned.Anchor.Start.Event == AnchorEvent.Stop && !interval.BegunBeforeWindow
                              && interval.SawAppliedPose && interval.ObservedStartSeconds is null;
            if (neverStood)
            {
                _defects.Add($"'{subject.Id}' ({interval.Instance.InstanceId}, {interval.Planned.Phase}): SUMO "
                             + $"committed stop {index} at {Seconds(interval.CommittedStartSeconds)} s and ended it "
                             + $"at {Seconds(committed)} s, and its drawn body never stood still at or below "
                             + $"{StandstillMetresPerSecond.ToString(CultureInfo.InvariantCulture)} m/s");
            }

            Close(interval, neverStood ? ClosedBy.PhysicalPredicateNeverHeld : ClosedBy.Trigger, committed,
                  committedEnd: committed);
            changed = true;
        }

        if (changed)
        {
            Restate(subject.Id, at);
        }
    }

    /// <summary>
    /// Every phase-anchored subject whose edge changed: ask SUMO where along its route it is, and commit
    /// each phase it has reached.
    /// </summary>
    private void WatchPhases(SumoStepRecord step, IVehicleAnswers answers, double at, HashSet<string> readBack)
    {
        foreach (Subject subject in _phaseSubjects)
        {
            if (!subject.Alive || readBack.Contains(subject.Id) || !subject.AwaitsPhase()
                || !step.Frames.TryGetValue(subject.Id, out CoSimVehicleFrame frame)
                || frame.EdgeId == subject.LastEdge)
            {
                continue;
            }

            subject.LastEdge = frame.EdgeId;
            if (!frame.IsOnInternalEdge && answers.RouteIndex(subject.Id) is { } routeIndex)
            {
                PhaseReached(subject, routeIndex, at, committed: at);
            }
        }
    }

    private void PhaseReached(Subject subject, int routeIndex, double at, double? committed)
    {
        bool changed = false;
        foreach (BoundInterval interval in subject.Intervals.Where(interval =>
                     interval.Status == SupervisionIntervalStatus.Planned
                     && interval.Planned.Anchor?.Start is { Event: AnchorEvent.Phase, RouteIndex: { } entered }
                     && entered <= routeIndex))
        {
            Open(interval, committed, startedAt: committed);
            changed = true;
        }

        foreach (BoundInterval interval in subject.Intervals.Where(interval =>
                     interval.Status == SupervisionIntervalStatus.Open
                     && interval.Planned.Anchor?.End is { Event: AnchorEvent.Phase, RouteIndex: { } entered }
                     && entered <= routeIndex).ToList())
        {
            Close(interval, ClosedBy.Trigger, committed, committedEnd: committed);
            changed = true;
        }

        if (changed)
        {
            Restate(subject.Id, at);
        }
    }

    private void Removed(string id, double at, ClosedBy reason)
    {
        if (_subjects.TryGetValue(id, out Subject? subject) && subject.Alive)
        {
            subject.Alive = false;
            foreach (BoundInterval interval in subject.Intervals.Where(
                         interval => interval.Status == SupervisionIntervalStatus.Open).ToList())
            {
                Close(interval, reason, at, committedEnd: at);
            }

            Restate(id, at);
        }

        if (_cohortMembers.Remove(id))
        {
            Restate(id, at);
        }
    }

    private void NeverInserted(Subject subject, VehicleNotInserted gone)
    {
        foreach (BoundInterval interval in subject.Intervals.Where(
                     interval => interval.Status != SupervisionIntervalStatus.Closed).ToList())
        {
            Close(interval, ClosedBy.NeverInserted, gone.GoneAtSeconds, committedEnd: null);
        }

        _dropped.Add((subject, gone));
    }

    private void RefuseWhatWasDropped()
    {
        if (_dropped.Count == 0)
        {
            return;
        }

        string named = string.Join("; ", _dropped.Select(dropped =>
            $"'{dropped.Subject.Id}', given up between {Seconds(dropped.Gone.WaitingAtSeconds)} s and "
            + $"{Seconds(dropped.Gone.GoneAtSeconds)} s, a subject of "
            + string.Join(", ", dropped.Subject.Participations.Select(participation => participation.Instance.InstanceId)
                .Concat(dropped.Subject.Series.Select(series => $"series:{series.SeriesId}"))
                .Distinct())));
        _dropped.Clear();
        throw new CoSimSessionRefusedException(
            CoSimSessionStage.Window,
            $"SUMO never inserted a subject of the supervision plan {_plan.PlanId}: {named}. Its intervals are "
            + "closed never_inserted, and a plan subject SUMO drops fails the run: the rows its author declared "
            + "for it can bind nothing (06 D6.12). Make room for it to depart -- a later departure, another lane "
            + "-- or a longer max-depart-delay, and recompile.");
    }

    /// <summary>The spans inside the window over which an open interval's vehicle had no place in the render set.</summary>
    private void TrackDrawing(SumoStepRecord step, double at)
    {
        if (at < _windowOpensAt() - Tolerance)
        {
            return;
        }

        foreach (BoundInterval interval in _intervals)
        {
            if (interval.Status != SupervisionIntervalStatus.Open || interval.Subject is not { Alive: true } subject)
            {
                continue;
            }

            bool drawn = step.RenderedVehicleIds.Contains(subject.Id);
            if (!drawn && interval.GapFrom is null)
            {
                interval.GapFrom = at;
            }
            else if (drawn && interval.GapFrom is { } from)
            {
                interval.NotDrawn.Add(new NotDrawnSpan(from, at));
                interval.GapFrom = null;
            }
        }
    }

    // -- observation ----------------------------------------------------------------------------------------

    private void ObserveEntries(RenderedFrameRecord frame)
    {
        if (_watchingEntry.Count == 0 || frame.RenderSet is not { } set)
        {
            return;
        }

        _drawn.Clear();
        foreach (RenderedVehicle vehicle in set.ByActor.Values)
        {
            _drawn.Add(vehicle.SumoId);
        }

        foreach (BoundInterval interval in _watchingEntry.Where(interval => _drawn.Contains(interval.Subject!.Id)).ToList())
        {
            interval.ObservedStartSeconds = frame.SimulatedTimeSeconds;
            interval.ObservedStartFrame = frame.Frame;
            _watchingEntry.Remove(interval);
            if (interval.CommittedStartSeconds is { } committed
                && Math.Abs(frame.SimulatedTimeSeconds - committed) > _worldDeltaSeconds / 2.0)
            {
                _defects.Add($"'{interval.Subject!.Id}' ({interval.Instance.InstanceId}, {interval.Planned.Phase}): "
                             + $"SUMO inserted it at {Seconds(committed)} s and the first frame drew it at "
                             + $"{Seconds(frame.SimulatedTimeSeconds)} s; a vehicle is drawn from the frame SUMO "
                             + "first reports it in, so the two are one frame (03 D3.6)");
            }
        }
    }

    private void ObserveStandstills(RenderedFrameRecord frame)
    {
        if (_watchingStandstill.Count == 0 || frame.AppliedPoses.Count == 0)
        {
            return;
        }

        foreach (VehiclePose pose in frame.AppliedPoses)
        {
            foreach (BoundInterval interval in _watchingStandstill.Where(
                         interval => interval.Subject!.Id == pose.VehicleId).ToList())
            {
                interval.SawAppliedPose = true;
                double speed = Math.Sqrt((pose.VelocityX * pose.VelocityX) + (pose.VelocityY * pose.VelocityY)
                                         + (pose.VelocityZ * pose.VelocityZ));
                if (speed <= StandstillMetresPerSecond)
                {
                    interval.ObservedStartSeconds = frame.SimulatedTimeSeconds;
                    interval.ObservedStartFrame = frame.Frame;
                    _watchingStandstill.Remove(interval);
                }
            }
        }
    }

    // -- opening, closing and stating -----------------------------------------------------------------------

    /// <param name="interval">The interval.</param>
    /// <param name="committed">Its committed onset; null where none is known or none is defined.</param>
    /// <param name="startedAt">The instant it started, committed or declared, where one is known.</param>
    private void Open(BoundInterval interval, double? committed, double? startedAt)
    {
        interval.Status = SupervisionIntervalStatus.Open;
        interval.CommittedStartSeconds = interval.Planned.Anchor is null ? null : committed;
        interval.BegunBeforeWindow = startedAt is not { } started || started < _windowOpensAt() - Tolerance;
        if (!interval.BegunBeforeWindow && interval.Subject is not null)
        {
            switch (interval.Planned.Anchor?.Start.Event)
            {
                case AnchorEvent.Depart:
                    _watchingEntry.Add(interval);
                    break;
                case AnchorEvent.Stop:
                    _watchingStandstill.Add(interval);
                    break;
            }
        }

        SupervisionIntervalRecord record = interval.Record();
        foreach (ISupervisionIntervalSink sink in _sinks)
        {
            sink.OnIntervalOpened(record);
        }
    }

    /// <param name="interval">The interval.</param>
    /// <param name="reason">Why it closed.</param>
    /// <param name="at">The instant it closed; null for an end read back from SUMO with no instant.</param>
    /// <param name="committedEnd">The instant SUMO committed its end, where an event of SUMO's ended it.</param>
    private void Close(BoundInterval interval, ClosedBy reason, double? at, double? committedEnd)
    {
        interval.Status = SupervisionIntervalStatus.Closed;
        interval.ClosedBy = reason;
        interval.ClosedAtSeconds = at;
        interval.CommittedEndSeconds = interval.Planned.Anchor is null ? null : committedEnd;
        if (interval.GapFrom is { } from)
        {
            interval.NotDrawn.Add(new NotDrawnSpan(from, Math.Max(from, at ?? from)));
            interval.GapFrom = null;
        }

        _watchingEntry.Remove(interval);
        _watchingStandstill.Remove(interval);
        SupervisionIntervalRecord record = interval.Record();
        foreach (ISupervisionIntervalSink sink in _sinks)
        {
            sink.OnIntervalClosed(record);
        }
    }

    /// <summary>State a vehicle's supervision as of an instant, to be put in force from the frame at it.</summary>
    private void Restate(string vehicleId, double at) =>
        Schedule(new Change(at, VehicleId: vehicleId, Supervision: InForce(vehicleId)));

    private void Schedule(Change change) => _pending.Enqueue(change, (change.At, _order++));

    /// <summary>
    /// Put in force, in order, every change whose instant the next tick to be rendered has reached --
    /// nothing before the window opens.
    /// </summary>
    private void PutInForce()
    {
        if (_nextTickSeconds < _windowOpensAt() - Tolerance)
        {
            return;
        }

        while (_pending.TryPeek(out Change change, out (double At, long Order) due)
               && due.At <= _nextTickSeconds + Tolerance)
        {
            _pending.Dequeue();
            if (change.VehicleId is { } vehicle)
            {
                _supervision.Set(vehicle, change.Supervision!);
            }
            else if (change.Opened is { } absence)
            {
                _supervision.Open(absence);
            }
            else if (change.Closed is { } closed)
            {
                _supervision.Close(closed);
            }
        }
    }

    /// <summary>What the author asserts of a vehicle now, from the plan's rows and the intervals open.</summary>
    private SupervisionInForce InForce(string vehicleId)
    {
        List<(SupervisionState State, AnnotationInForce Annotation)> found = [];
        if (_subjects.TryGetValue(vehicleId, out Subject? subject) && subject.Alive)
        {
            foreach (Participation participation in subject.Participations)
            {
                PatternInstance instance = participation.Instance;
                if (participation.Intervals.Count == 0)
                {
                    found.Add((instance.Supervision,
                               new AnnotationInForce(instance.InstanceId, instance.Labels, string.Empty, participation.Role)));
                    continue;
                }

                foreach (BoundInterval interval in participation.Intervals.Where(
                             interval => interval.Status == SupervisionIntervalStatus.Open))
                {
                    found.Add((instance.Supervision,
                               new AnnotationInForce(instance.InstanceId, instance.Labels, interval.Planned.Phase,
                                                     participation.Role)));
                }
            }

            foreach (RecurringSeries series in subject.Series)
            {
                found.Add((series.Supervision,
                           new AnnotationInForce($"series:{series.SeriesId}", series.Labels, string.Empty, series.MemberRole)));
            }
        }

        if (_cohortMembers.Contains(vehicleId) && CohortOf(vehicleId) is { } cohort)
        {
            found.Add((cohort.Supervision,
                       new AnnotationInForce($"cohort:{cohort.FlowId}", cohort.Labels, string.Empty, string.Empty)));
        }

        SupervisionState state = found.Any(entry => entry.State == SupervisionState.Annotated) ? SupervisionState.Annotated
            : found.Any(entry => entry.State == SupervisionState.Nominal) ? SupervisionState.Nominal
            : SupervisionState.Unlabelled;
        return state == SupervisionState.Unlabelled
            ? SupervisionInForce.Unlabelled
            : new SupervisionInForce(state, [.. found.Where(entry => entry.State == state).Select(entry => entry.Annotation)]);
    }

    /// <summary>The annotated cohort a flow member's id names, <c>&lt;flow_id&gt;.&lt;n&gt;</c>, or null.</summary>
    private CohortSupervision? CohortOf(string vehicleId)
    {
        if (_cohorts.Count == 0)
        {
            return null;
        }

        int dot = vehicleId.LastIndexOf('.');
        return dot > 0 && dot < vehicleId.Length - 1 && vehicleId.AsSpan(dot + 1).IndexOfAnyExceptInRange('0', '9') < 0
               && _cohorts.TryGetValue(vehicleId[..dot], out CohortSupervision? cohort)
            ? cohort
            : null;
    }

    private static AbsenceInForce Absence(BoundInterval interval) =>
        new(interval.Instance.InstanceId, interval.Instance.Labels, interval.Instance.AoiRefs, interval.Planned.Phase);

    /// <summary>A plan subject one of whose intervals is anchored to a stop, or null.</summary>
    private Subject? StopWatcher(string id) =>
        _subjects.TryGetValue(id, out Subject? subject)
        && (subject.Anchors(AnchorEvent.Stop) || subject.Anchors(AnchorEvent.StopEnd))
            ? subject
            : null;

    private static string? Lane(SumoStepRecord step, string id) =>
        step.Frames.TryGetValue(id, out CoSimVehicleFrame frame) ? frame.LaneId : null;

    private void CheckLane(Subject subject, AnchorPoint anchor, string? laneId)
    {
        if (laneId is { Length: > 0 } && anchor.Lane is { } declared && !string.Equals(declared, laneId, StringComparison.Ordinal))
        {
            _defects.Add($"'{subject.Id}': {anchor.Spelled} is anchored on lane {declared}, and SUMO made that stop "
                         + $"on {laneId}");
        }
    }

    private static string Seconds(double? value) =>
        value is { } seconds ? seconds.ToString("0.###", CultureInfo.InvariantCulture) : "(unknown)";

    /// <summary>One plan subject: a participant of an instance, or a series' realising vehicle.</summary>
    private sealed class Subject(string id)
    {
        public string Id { get; } = id;

        public List<Participation> Participations { get; } = [];

        public List<RecurringSeries> Series { get; } = [];

        public List<BoundInterval> Intervals { get; } = [];

        public bool Alive { get; set; }

        public string? LastEdge { get; set; }

        /// <summary>Whether any of its intervals is anchored, at either end, to an event of this kind.</summary>
        public bool Anchors(AnchorEvent kind) =>
            Intervals.Exists(interval => interval.Planned.Anchor is { } anchor
                                         && (anchor.Start.Event == kind || anchor.End?.Event == kind));

        /// <summary>Whether a phase it has not reached yet would open or close one of its intervals.</summary>
        public bool AwaitsPhase() =>
            Intervals.Exists(interval =>
                (interval.Status == SupervisionIntervalStatus.Planned && interval.Planned.Anchor?.Start.Event == AnchorEvent.Phase)
                || (interval.Status == SupervisionIntervalStatus.Open && interval.Planned.Anchor?.End?.Event == AnchorEvent.Phase));

        /// <summary>Its planned intervals whose start is anchored to an event of this kind, and index where given.</summary>
        public List<BoundInterval> Planned(AnchorEvent kind, int? index = null) =>
            [.. Intervals.Where(interval => interval.Status == SupervisionIntervalStatus.Planned
                                            && interval.Planned.Anchor?.Start is { } point
                                            && point.Event == kind && (index is null || point.Index == index))];

        /// <summary>Its open intervals whose end is anchored to an event of this kind and index.</summary>
        public List<BoundInterval> Open(AnchorEvent kind, int index) =>
            [.. Intervals.Where(interval => interval.Status == SupervisionIntervalStatus.Open
                                            && interval.Planned.Anchor?.End is { } point
                                            && point.Event == kind && point.Index == index)];
    }

    private sealed record Participation(PatternInstance Instance, string Role, List<BoundInterval> Intervals);

    /// <summary>One change to what is in force, to take effect from the frame at its instant.</summary>
    private readonly record struct Change(
        double At,
        string? VehicleId = null,
        SupervisionInForce? Supervision = null,
        AbsenceInForce? Opened = null,
        string? Closed = null);

    /// <summary>One of the plan's intervals, and what the run has bound of it.</summary>
    private sealed class BoundInterval(PatternInstance instance, PlannedInterval planned, string role, Subject? subject)
    {
        public PatternInstance Instance { get; } = instance;

        public PlannedInterval Planned { get; } = planned;

        public string Role { get; } = role;

        /// <summary>Its participant; null for an absence's vacancy.</summary>
        public Subject? Subject { get; } = subject;

        public SupervisionIntervalStatus Status { get; set; }

        public double? CommittedStartSeconds { get; set; }

        public double? CommittedEndSeconds { get; set; }

        public double? ObservedStartSeconds { get; set; }

        public ulong? ObservedStartFrame { get; set; }

        public bool BegunBeforeWindow { get; set; }

        public ClosedBy? ClosedBy { get; set; }

        public double? ClosedAtSeconds { get; set; }

        public bool SawAppliedPose { get; set; }

        public double? GapFrom { get; set; }

        public List<NotDrawnSpan> NotDrawn { get; } = [];

        public SupervisionIntervalRecord Record() => new()
        {
            InstanceId = Instance.InstanceId,
            EntityId = Planned.EntityId,
            Phase = Planned.Phase,
            Role = Role,
            Supervision = Instance.Supervision,
            Realisation = Instance.Realisation,
            Labels = Instance.Labels,
            Anchor = Planned.Anchor,
            DeclaredStartSeconds = Planned.DeclaredStartSeconds,
            DeclaredEndSeconds = Planned.DeclaredEndSeconds,
            DeclaredDurationSeconds = Planned.DeclaredDurationSeconds,
            Status = Status,
            CommittedStartSeconds = CommittedStartSeconds,
            CommittedEndSeconds = CommittedEndSeconds,
            ObservedStartSeconds = ObservedStartSeconds,
            ObservedStartFrame = ObservedStartFrame,
            BegunBeforeWindow = BegunBeforeWindow,
            ClosedBy = ClosedBy,
            ClosedAtSeconds = ClosedAtSeconds,
            NotDrawn = [.. NotDrawn],
        };
    }

    /// <summary>The questions the binder puts to SUMO about a frame's vehicles.</summary>
    internal interface IVehicleAnswers
    {
        double? Departure(string vehicleId);

        IReadOnlyList<SumoStop>? CompletedStops(string vehicleId);

        IReadOnlyList<SumoStop>? UpcomingStops(string vehicleId);

        int? RouteIndex(string vehicleId);
    }

    private sealed class SessionAnswers(SumoVehicleQueries queries) : IVehicleAnswers
    {
        public double? Departure(string vehicleId) => queries.Departure(vehicleId);

        public IReadOnlyList<SumoStop>? CompletedStops(string vehicleId) => queries.CompletedStops(vehicleId);

        public IReadOnlyList<SumoStop>? UpcomingStops(string vehicleId) => queries.UpcomingStops(vehicleId);

        public int? RouteIndex(string vehicleId) => queries.RouteIndex(vehicleId);
    }

    private sealed class NoAnswers : IVehicleAnswers
    {
        public static NoAnswers Instance { get; } = new();

        public double? Departure(string vehicleId) => null;

        public IReadOnlyList<SumoStop>? CompletedStops(string vehicleId) => null;

        public IReadOnlyList<SumoStop>? UpcomingStops(string vehicleId) => null;

        public int? RouteIndex(string vehicleId) => null;
    }
}
