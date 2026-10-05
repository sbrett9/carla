using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

/// <summary>
/// The supervision in force for a drive, as the interval binder states it: what the scenario's author
/// asserts of each SUMO vehicle, and the absences in force for the world as a whole. The session puts it
/// on the server, onto the bodies that draw those vehicles, before the tick cue of each frame it changes
/// in, and every reader takes it from there (<c>CarlaClient.GetSnapshotFrame</c>).
/// </summary>
/// <remarks>
/// <para><b>Held on the server, never here.</b> This table is what the binder hands over, not a source
/// any recorder reads: a recorder beside the session reads the same server-held truth as one in another
/// process, so no two clients of the world can ever hold different supervision for the same frame (doc 06
/// §8.2, the ruling of 2026-10-05).</para>
///
/// <para><b>Stated per SUMO vehicle, carried per body.</b> The binder knows the plan's subjects by their
/// SUMO ids and nothing of the pool; the session knows which body draws which vehicle on each frame. So
/// the binder states a vehicle's supervision here once, when it changes, and the session names it to the
/// server for whichever body draws the vehicle -- again whenever the vehicle is lent a body anew, because
/// the server drops a body's supervision when the body is given back or handed on. A vehicle no body
/// draws is on no snapshot, because no frame shows it.</para>
///
/// <para><b>Bound to one plan, and it never mints a row.</b> Nothing is held until a plan is bound, and
/// binding one starts afresh: every vehicle unlabelled and no absence open. What is held is only what the
/// binder copies from the plan's rows (doc 06 D6.8). This table checks that each state is one a vehicle can
/// be in, never that the plan has the row.</para>
///
/// <para><b>On the session's thread.</b> Written between ticks -- from a step observer, or by whatever
/// drives the session between two advances -- and read by the session before each tick's cue.</para>
/// </remarks>
public sealed class DriveSupervision
{
    private readonly Dictionary<string, SupervisionInForce> _vehicles = new(StringComparer.Ordinal);
    private readonly List<AbsenceInForce> _absences = [];

    /// <summary>The plan bound, or <see langword="null"/> where none is, or it was withdrawn.</summary>
    public SupervisionPlanIdentity? Plan { get; private set; }

    /// <summary>
    /// Counts every change to what is held, so the session can tell a tick whose supervision changed from
    /// one whose did not without comparing the whole table.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>Every vehicle the author asserts something of, by SUMO vehicle id; any other is unlabelled.</summary>
    public IReadOnlyDictionary<string, SupervisionInForce> Vehicles => _vehicles;

    /// <summary>The absences in force, in the order they opened.</summary>
    public IReadOnlyList<AbsenceInForce> Absences => _absences;

    /// <summary>
    /// Bind the plan every row is copied from, and start afresh: every vehicle unlabelled and no absence
    /// open.
    /// </summary>
    public void Bind(SupervisionPlanIdentity plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(plan.PlanId);
        ArgumentNullException.ThrowIfNull(plan.VocabularyDigest);
        Plan = plan;
        _vehicles.Clear();
        _absences.Clear();
        Revision++;
    }

    /// <summary>
    /// Put in force what the author asserts of one SUMO vehicle from now on, replacing what it had;
    /// unlabelled withdraws the assertion.
    /// </summary>
    /// <exception cref="InvalidOperationException">No plan is bound.</exception>
    /// <exception cref="ArgumentException">The state is not one a vehicle can be in
    /// (<see cref="SupervisionInForce.Problem"/>).</exception>
    public void Set(string vehicleId, SupervisionInForce supervision)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        ArgumentNullException.ThrowIfNull(supervision);
        Bound();
        if (supervision.Problem() is { } problem)
        {
            throw new ArgumentException($"Vehicle {vehicleId}: {problem}", nameof(supervision));
        }

        if (supervision.State == SupervisionState.Unlabelled)
        {
            if (_vehicles.Remove(vehicleId))
            {
                Revision++;
            }

            return;
        }

        if (!_vehicles.TryGetValue(vehicleId, out SupervisionInForce? held) || !held.Equals(supervision))
        {
            _vehicles[vehicleId] = supervision;
            Revision++;
        }
    }

    /// <summary>What the author asserts of a SUMO vehicle now: its supervision, or unlabelled.</summary>
    public SupervisionInForce Of(string vehicleId) =>
        _vehicles.TryGetValue(vehicleId, out SupervisionInForce? held) ? held : SupervisionInForce.Unlabelled;

    /// <summary>Open an absence, or replace one open under the same instance.</summary>
    /// <exception cref="InvalidOperationException">No plan is bound.</exception>
    public void Open(AbsenceInForce absence)
    {
        ArgumentNullException.ThrowIfNull(absence);
        ArgumentException.ThrowIfNullOrEmpty(absence.InstanceId);
        Bound();
        int held = _absences.FindIndex(open => open.InstanceId == absence.InstanceId);
        if (held < 0)
        {
            _absences.Add(absence);
        }
        else if (!_absences[held].Equals(absence))
        {
            _absences[held] = absence;
        }
        else
        {
            return;
        }

        Revision++;
    }

    /// <summary>Close the absence open under an instance; nothing where none is.</summary>
    public void Close(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        if (_absences.RemoveAll(open => open.InstanceId == instanceId) > 0)
        {
            Revision++;
        }
    }

    /// <summary>Whether an absence is open under an instance.</summary>
    public bool IsOpen(string instanceId) => _absences.Exists(open => open.InstanceId == instanceId);

    /// <summary>
    /// Withdraw everything: no plan, no vehicle's supervision, no absence, so the world carries no
    /// supervision from the next frame on.
    /// </summary>
    public void Withdraw()
    {
        if (Plan is null && _vehicles.Count == 0 && _absences.Count == 0)
        {
            return;
        }

        Plan = null;
        _vehicles.Clear();
        _absences.Clear();
        Revision++;
    }

    private void Bound()
    {
        if (Plan is null)
        {
            throw new InvalidOperationException(
                "No supervision plan is bound: every row is copied from a plan, so a plan is bound first.");
        }
    }
}
