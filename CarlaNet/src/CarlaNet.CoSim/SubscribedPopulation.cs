using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// Every SUMO vehicle, subscribed to the state the bridge reads, and delivering it with every step.
/// </summary>
/// <remarks>
/// <para><b>Every vehicle SUMO has is subscribed, because by default every one of them is rendered</b>
/// -- and under an optional render-set limit too, so the limit decides from each vehicle's full state
/// and a vehicle it admits part-way through its drive has the frame before its admission. A
/// subscription is charged inside SUMO's step whether or not anyone reads it -- measured at 388 live
/// vehicles on the Arapahoe network, 3.73 ms per step with nothing subscribed and 9.26 ms with the
/// eight-variable bridge set subscribed and never read (before the lateral offset was added to it) --
/// so a heavier scenario costs SUMO more time per step. That is the price of drawing what the scenario holds, and it is paid in wall clock, never
/// in content.</para>
///
/// <para>A subscription delivers its results immediately: SUMO answers a subscribe command with the
/// current values of everything subscribed, and those are written into the same per-step store the
/// step response fills. So a vehicle subscribed on the step it departed has full state on that step,
/// not the next one.</para>
///
/// <para><b>An arrived vehicle is forgotten, never unsubscribed.</b> SUMO drops a subscription with
/// the vehicle it is on, so unsubscribing afterwards is refused -- and SUMO writes a line to its own
/// console for every refusal, which on a real scenario is several a second.</para>
/// </remarks>
public sealed class SubscribedPopulation
{
    /// <summary>
    /// What every vehicle delivers: the client's seven-variable state plus the lane position the
    /// lane-geometry interpolation is evaluated at and the lateral offset from the lane's centre line,
    /// which puts the vehicle where SUMO has it while a lane change is spread over time.
    /// </summary>
    public static readonly int[] StateVariables =
    [
        TraCIVariables.Position,
        TraCIVariables.Angle,
        TraCIVariables.Speed,
        TraCIVariables.RoadId,
        TraCIVariables.LaneId,
        TraCIVariables.LanePosition,
        TraCIVariables.LateralLanePosition,
        TraCIVariables.TypeId,
        TraCIVariables.Signals,
    ];

    private readonly TraCIConnection _traci;
    private readonly TraCISubscriptionResults _results;
    private readonly HashSet<string> _subscribed = [];
    private readonly List<string> _scratch = [];
    private readonly HashSet<string> _vanished = [];

    /// <param name="connection">The session's one TraCI connection.</param>
    public SubscribedPopulation(TraCIConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _traci = connection;
        _results = connection.SubscriptionResults(TraCIConstants.RESPONSE_SUBSCRIBE_VEHICLE_VARIABLE);
    }

    /// <summary>Every vehicle subscribed.</summary>
    public IReadOnlyCollection<string> SubscribedVehicleIds => _subscribed;

    /// <summary>
    /// At the last <see cref="ReadFrames"/>: the vehicles that were subscribed, had not been listed
    /// among the step's arrivals, and delivered nothing -- so SUMO no longer has them.
    /// </summary>
    /// <remarks>
    /// SUMO drops a vehicle's subscription with the vehicle, and reports as arrivals only the vehicles
    /// it removed during the step. One taken out between two steps -- by a TraCI command, which clears
    /// its state change before the next step's arrivals are collected -- is in neither list, and this is
    /// where it shows.
    /// </remarks>
    public IReadOnlyCollection<string> LastVanished => _vanished;

    /// <summary>Subscribe commands sent since the session started: one per vehicle, for its whole life.</summary>
    public long Subscribes { get; private set; }

    /// <summary>
    /// Subscribe the vehicles SUMO inserted this step, and drop what it removed.
    /// </summary>
    /// <remarks>
    /// A vehicle that departs and arrives inside one step appears in both lists and is dropped
    /// without ever having been subscribed, which is why the removal runs second.
    /// </remarks>
    public void Reconcile(IReadOnlyList<string> departed, IReadOnlyList<string> arrived)
    {
        ArgumentNullException.ThrowIfNull(departed);
        ArgumentNullException.ThrowIfNull(arrived);

        foreach (string vehicleId in departed)
        {
            if (_subscribed.Add(vehicleId))
            {
                _traci.Subscribe(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, vehicleId, StateVariables);
                Subscribes++;
            }
        }

        foreach (string vehicleId in arrived)
        {
            Forget(vehicleId);
        }
    }

    /// <summary>
    /// Subscribe every vehicle already in the simulation.
    /// </summary>
    /// <remarks>
    /// The one whole-population question a session asks, and it asks it once. The departure list
    /// covers only the step just taken, so a session that starts part-way into a scenario -- which
    /// is what a capture window is -- would otherwise never subscribe the vehicles that departed
    /// during the fast-forward, and they would be invisible to it for the rest of their lives.
    /// </remarks>
    public void Seed(IReadOnlyList<string> vehicleIds)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        Reconcile(vehicleIds, []);
    }

    /// <summary>
    /// Release a vehicle SUMO has already removed, without telling SUMO -- which would refuse the
    /// command and log a line for it.
    /// </summary>
    public void Forget(string vehicleId)
    {
        _subscribed.Remove(vehicleId);
        _results.Forget(vehicleId);
    }

    /// <summary>
    /// Read the full state of every subscribed vehicle into <paramref name="into"/>, which is cleared
    /// first, and return how many delivered it.
    /// </summary>
    /// <remarks>
    /// A subscribed vehicle that delivered nothing at all has left the simulation without being listed
    /// among the step's arrivals -- which does not happen on the normal path, but does to a vehicle
    /// taken out between two steps. It is dropped here rather than being carried as a stale state, and
    /// named in <see cref="LastVanished"/>.
    /// </remarks>
    public int ReadFrames(IDictionary<string, CoSimVehicleFrame> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        _scratch.Clear();
        _vanished.Clear();

        foreach (string vehicleId in _subscribed)
        {
            if (TryReadFrame(vehicleId, out CoSimVehicleFrame frame))
            {
                into[vehicleId] = frame;
            }
            else if (_results.ValuesFor(vehicleId).Count == 0)
            {
                _scratch.Add(vehicleId);
            }
        }

        foreach (string vehicleId in _scratch)
        {
            Forget(vehicleId);
            _vanished.Add(vehicleId);
        }

        return into.Count;
    }

    /// <summary>
    /// SUMO's lateral lane position as an offset from the lane's centre line: zero where SUMO reports
    /// none, which it does for a vehicle on no lane -- parked off it at a stop.
    /// </summary>
    private static double LateralOffset(double reported) =>
        double.IsFinite(reported) && Math.Abs(reported) < InvalidLateralOffset ? reported : 0.0;

    /// <summary>
    /// SUMO's <c>INVALID_DOUBLE_VALUE</c> is -2^30; anything this far from a lane is not an offset across
    /// it.
    /// </summary>
    private const double InvalidLateralOffset = 1.0e6;

    /// <summary>One vehicle's state from the last step, where it delivered all of it.</summary>
    public bool TryReadFrame(string vehicleId, out CoSimVehicleFrame frame)
    {
        IReadOnlyDictionary<int, TraCIValue> values = _results.ValuesFor(vehicleId);
        if (values.Count < StateVariables.Length)
        {
            frame = default;
            return false;
        }

        (double x, double y, _) = values[TraCIVariables.Position].AsPosition;
        frame = new CoSimVehicleFrame(
            vehicleId,
            x,
            y,
            values[TraCIVariables.Angle].AsDouble,
            values[TraCIVariables.Speed].AsDouble,
            values[TraCIVariables.RoadId].AsString,
            values[TraCIVariables.LaneId].AsString,
            values[TraCIVariables.LanePosition].AsDouble,
            values[TraCIVariables.TypeId].AsString,
            (SumoVehicleSignals)values[TraCIVariables.Signals].AsInt,
            LateralOffset(values[TraCIVariables.LateralLanePosition].AsDouble));
        return true;
    }
}
