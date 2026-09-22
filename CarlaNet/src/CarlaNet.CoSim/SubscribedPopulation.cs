using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// Which SUMO vehicles deliver state with every step, and how much of it each one delivers.
/// </summary>
/// <remarks>
/// <para><b>Two tiers, because a subscription is charged inside SUMO's step whether or not anyone
/// reads it.</b> Measured at 388 live vehicles on the Arapahoe network: 3.73 ms per step with
/// nothing subscribed, 9.26 ms with the eight-variable bridge set subscribed and never read. The
/// render-set contract says the subscribed set is governed by the render set rather than by the
/// population -- but a vehicle's position is exactly what decides that, and a vehicle nothing is
/// subscribed to has no position to decide on.</para>
///
/// <para>The way out is that the two tiers are not the same size. Every vehicle in the simulation
/// carries a <b>screening</b> subscription of one variable, its position, which is what the render
/// set is decided from. A vehicle the policy wants is then <b>promoted</b> to the full eight, and
/// demoted again when it leaves.</para>
///
/// <para>A promotion delivers its results immediately: SUMO answers a subscribe command with the
/// current values of everything subscribed, and those land in the same per-step store the step
/// response fills. So a vehicle promoted on the step it entered the margin has full state on that
/// step, not the next one.</para>
///
/// <para><b>A subscription only ever grows, which is why a demotion is two commands.</b> Measured
/// against SUMO's own client on the fixture network: subscribing a vehicle to one variable and then
/// to eight delivers eight, and subscribing it back to one still delivers eight, on that step and
/// on every step after. Only an explicit unsubscribe clears the set, and a subscribe after it then
/// takes effect from the following step. A demotion that is a plain re-subscribe therefore keeps
/// paying for the seven variables it meant to give back, invisibly -- the values keep arriving and
/// everything reads correctly.</para>
///
/// <para><b>An arrived vehicle is forgotten, never unsubscribed.</b> SUMO drops a subscription with
/// the vehicle it is on, so unsubscribing afterwards is refused -- and SUMO writes a line to its own
/// console for every refusal, which on a real scenario is several a second.</para>
/// </remarks>
public sealed class SubscribedPopulation
{
    /// <summary>
    /// The one variable every vehicle in the simulation carries: where it is. Everything the render
    /// set is decided from, and nothing else.
    /// </summary>
    public static readonly int[] ScreeningVariables = [TraCIVariables.Position];

    /// <summary>
    /// What a vehicle inside the subscription margin delivers: the client's seven-variable state
    /// plus the lane position the lane-geometry interpolation is evaluated at.
    /// </summary>
    public static readonly int[] StateVariables =
    [
        TraCIVariables.Position,
        TraCIVariables.Angle,
        TraCIVariables.Speed,
        TraCIVariables.RoadId,
        TraCIVariables.LaneId,
        TraCIVariables.LanePosition,
        TraCIVariables.TypeId,
        TraCIVariables.Signals,
    ];

    private readonly TraCIConnection _traci;
    private readonly TraCISubscriptionResults _results;
    private readonly HashSet<string> _screened = [];
    private readonly HashSet<string> _promoted = [];
    private readonly List<string> _scratch = [];

    /// <param name="connection">
    /// The session's one TraCI connection. The subscription is driven through it directly rather
    /// than through <see cref="SumoVehicleSubscription"/>, which subscribes every vehicle to one
    /// fixed variable list and so cannot express a tier.
    /// </param>
    public SubscribedPopulation(TraCIConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _traci = connection;
        _results = connection.SubscriptionResults(TraCIConstants.RESPONSE_SUBSCRIBE_VEHICLE_VARIABLE);
    }

    /// <summary>Every vehicle subscribed at either tier.</summary>
    public IReadOnlyCollection<string> ScreenedVehicleIds => _screened;

    /// <summary>Every vehicle subscribed to the full state set.</summary>
    public IReadOnlyCollection<string> PromotedVehicleIds => _promoted;

    /// <summary>Subscribe commands sent since the session started, by tier.</summary>
    public long ScreeningSubscribes { get; private set; }

    /// <summary>Promotions and demotions sent since the session started.</summary>
    public long TierChanges { get; private set; }

    /// <summary>
    /// Take up the vehicles SUMO inserted this step at the screening tier, and drop what it
    /// removed.
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
            if (_screened.Add(vehicleId))
            {
                _traci.Subscribe(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, vehicleId,
                                 ScreeningVariables);
                ScreeningSubscribes++;
            }
        }

        foreach (string vehicleId in arrived)
        {
            Forget(vehicleId);
        }
    }

    /// <summary>
    /// Take up every vehicle already in the simulation at the screening tier.
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
        _screened.Remove(vehicleId);
        _promoted.Remove(vehicleId);
        _results.Forget(vehicleId);
    }

    /// <summary>
    /// Read every subscribed vehicle's position into <paramref name="into"/>, which is cleared
    /// first, and return how many delivered one.
    /// </summary>
    /// <remarks>
    /// A subscribed vehicle absent from the results has left the simulation between this step's
    /// arrival list and now -- which does not happen on the normal path, but does after a route
    /// error. It is dropped here rather than being carried as a stale position.
    /// </remarks>
    public int ReadPositions(IDictionary<string, (double X, double Y)> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        _scratch.Clear();

        foreach (string vehicleId in _screened)
        {
            if (_results.TryGetValue(vehicleId, TraCIVariables.Position, out TraCIValue value))
            {
                (double x, double y, _) = value.AsPosition;
                into[vehicleId] = (x, y);
            }
            else
            {
                _scratch.Add(vehicleId);
            }
        }

        foreach (string vehicleId in _scratch)
        {
            Forget(vehicleId);
        }

        return into.Count;
    }

    /// <summary>
    /// Move a screened vehicle up to the full state set, delivering its state immediately.
    /// </summary>
    public void Promote(string vehicleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        if (!_screened.Contains(vehicleId) || !_promoted.Add(vehicleId))
        {
            return;
        }

        _traci.Subscribe(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, vehicleId, StateVariables);
        TierChanges++;
    }

    /// <summary>
    /// Move a vehicle back down to position alone, taking the other seven variables out of SUMO's
    /// step.
    /// </summary>
    /// <remarks>
    /// Two commands, and both are needed: a subscription grows and never shrinks, so subscribing
    /// the shorter list on its own leaves all eight variables arriving for the rest of the vehicle's
    /// life. The values keep reading correctly, which is what makes the mistake invisible. Only the
    /// vehicle's own subscription is cleared, so this is safe to call for a vehicle still in the
    /// simulation -- which is the only vehicle it is ever called for, because one SUMO has removed
    /// takes its subscription with it.
    /// </remarks>
    public void Demote(string vehicleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        if (!_promoted.Remove(vehicleId))
        {
            return;
        }

        _traci.Unsubscribe(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, vehicleId);
        _traci.Subscribe(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, vehicleId,
                         ScreeningVariables);
        TierChanges++;
    }

    /// <summary>
    /// Read the full state of every promoted vehicle into <paramref name="into"/>, which is cleared
    /// first, and return how many delivered it.
    /// </summary>
    public int ReadFrames(IDictionary<string, CoSimVehicleFrame> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        foreach (string vehicleId in _promoted)
        {
            if (TryReadFrame(vehicleId, out CoSimVehicleFrame frame))
            {
                into[vehicleId] = frame;
            }
        }

        return into.Count;
    }

    /// <summary>One promoted vehicle's state from the last step, where it delivered all of it.</summary>
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
            (SumoVehicleSignals)values[TraCIVariables.Signals].AsInt);
        return true;
    }
}
