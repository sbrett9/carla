// Ported from Eclipse SUMO's reference TraCI client: the subscribe / unsubscribe /
// getAllSubscriptionResults path in tools/traci/domain.py:188-223.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// Which vehicles SUMO delivers state for with every step, and the state it delivered.
/// </summary>
/// <remarks>
/// <para><b>This is the read path, not one of two.</b> Asking SUMO for each variable of each
/// vehicle in turn is linear in round trips: measured on a 388-vehicle Arapahoe network, 116.0 ms
/// per step against 8.1 ms for the same variables delivered by subscription.
/// <see cref="SumoVehicleDomain.ReadStateWithoutSubscription"/> is there for a question asked once,
/// and is documented as the trap it is.</para>
///
/// <para><b>Which vehicles are subscribed is the caller's decision, and it is not free.</b> SUMO
/// fills subscription results while it advances, so the cost lands inside the step whether anything
/// reads them or not: 3.73 ms per step with nothing subscribed against 9.26 ms with the seven-
/// variable set subscribed and never read, on the same network. A bridge that renders a subset of
/// the population should subscribe that subset. This class therefore takes the vehicles one at a
/// time rather than subscribing everything SUMO has.</para>
/// </remarks>
public sealed class SumoVehicleSubscription
{
    private readonly TraCIConnection _connection;
    private readonly TraCISubscriptionResults _results;
    private readonly int[] _variables;
    private readonly HashSet<string> _subscribed = [];

    internal SumoVehicleSubscription(TraCIConnection connection, IReadOnlyList<int> variables)
    {
        _connection = connection;
        _variables = [.. variables];
        foreach (int variable in _variables)
        {
            TraCIVariables.EnsureDecodable(TraCIConstants.CMD_GET_VEHICLE_VARIABLE, variable);
        }

        _results = connection.SubscriptionResults(TraCIConstants.RESPONSE_SUBSCRIBE_VEHICLE_VARIABLE);
    }

    /// <summary>The variables every subscribed vehicle delivers, in the order they were subscribed.</summary>
    public IReadOnlyList<int> Variables => _variables;

    /// <summary>Every vehicle currently subscribed, whether or not it delivered results last step.</summary>
    public IReadOnlyCollection<string> SubscribedVehicleIds => _subscribed;

    /// <summary>The vehicles that delivered state in the last step.</summary>
    public IReadOnlyList<string> DeliveredVehicleIds => _results.ObjectIds;

    /// <summary>
    /// Start delivering this vehicle's state with every step response. Subscribing a vehicle that is
    /// already subscribed is harmless and re-sends the subscription.
    /// </summary>
    public void Add(string vehicleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        _connection.Subscribe(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, vehicleId, _variables);
        _subscribed.Add(vehicleId);
    }

    /// <summary>
    /// Stop delivering a vehicle's state, and drop what is held for it -- without telling SUMO.
    /// </summary>
    /// <remarks>
    /// <para><b>This is what the release path for a departed or arrived vehicle calls.</b> SUMO
    /// drops a subscription together with the vehicle it is on, so unsubscribing afterwards is not
    /// merely redundant: SUMO refuses it with "The subscription to remove was not found" <i>and</i>
    /// writes a line to its own console for every one. Measured on a 388-vehicle Arapahoe run that
    /// is one such line per arrival, several a second, which buries everything else SUMO has to
    /// say.</para>
    ///
    /// <para>It cannot raise, and there is nothing to catch around it. Releasing a vehicle that was
    /// never subscribed, or has already been released, does nothing.</para>
    /// </remarks>
    public void Release(string vehicleId)
    {
        _subscribed.Remove(vehicleId);
        _results.Forget(vehicleId);
    }

    /// <summary>
    /// Tell SUMO to stop delivering a vehicle's state, for a vehicle that is still in the
    /// simulation.
    /// </summary>
    /// <remarks>
    /// The right call for a vehicle that has left the render set but is still driving -- the point
    /// of it is to take the vehicle's cost back out of the step. For one SUMO has already removed,
    /// use <see cref="Release"/>: this sends a command SUMO refuses and logs.
    /// </remarks>
    public void Unsubscribe(string vehicleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        if (!_subscribed.Remove(vehicleId))
        {
            return;
        }

        _results.Forget(vehicleId);
        _connection.Unsubscribe(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE, vehicleId);
    }

    /// <summary>
    /// Read every subscribed vehicle's state for the step just taken into <paramref name="into"/>,
    /// which is cleared first, and return how many arrived.
    /// </summary>
    /// <remarks>
    /// <para>A subscribed vehicle SUMO has removed is simply absent from the results, which is how a
    /// caller learns it has gone without asking.</para>
    ///
    /// <para>The caller supplies the dictionary because this runs every step and reusing it keeps
    /// the read free of allocation beyond the strings the frame itself carries.</para>
    /// </remarks>
    public int Read(IDictionary<string, SumoVehicleState> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        foreach (string vehicleId in _results.ObjectIds)
        {
            into[vehicleId] = ReadState(vehicleId);
        }

        return into.Count;
    }

    /// <summary>
    /// Read every subscribed vehicle's state into a fresh dictionary. Convenient outside a step
    /// loop; inside one, use <see cref="Read(IDictionary{string, SumoVehicleState})"/> and keep the
    /// dictionary.
    /// </summary>
    public Dictionary<string, SumoVehicleState> Read()
    {
        Dictionary<string, SumoVehicleState> states = [];
        Read(states);
        return states;
    }

    /// <summary>
    /// One subscribed vehicle's state from the last step, or <see langword="null"/> where it
    /// delivered none -- which for a subscribed vehicle means SUMO has removed it.
    /// </summary>
    public SumoVehicleState? TryRead(string vehicleId) =>
        _results.ValuesFor(vehicleId).Count == 0 ? null : ReadState(vehicleId);

    /// <summary>
    /// One variable of one vehicle from the last step, with the type the wire gave it. For anything
    /// outside <see cref="SumoVehicleState"/>'s fixed set.
    /// </summary>
    public bool TryReadVariable(string vehicleId, int variableId, out TraCIValue value) =>
        _results.TryGetValue(vehicleId, variableId, out value);

    private SumoVehicleState ReadState(string vehicleId)
    {
        (double x, double y, _) = Require(vehicleId, TraCIVariables.Position).AsPosition;
        return new SumoVehicleState(
            vehicleId,
            x,
            y,
            Require(vehicleId, TraCIVariables.Angle).AsDouble,
            Require(vehicleId, TraCIVariables.Speed).AsDouble,
            Require(vehicleId, TraCIVariables.RoadId).AsString,
            Require(vehicleId, TraCIVariables.LaneId).AsString,
            Require(vehicleId, TraCIVariables.TypeId).AsString,
            (SumoVehicleSignals)Require(vehicleId, TraCIVariables.Signals).AsInt);
    }

    private TraCIValue Require(string vehicleId, int variableId)
    {
        if (_results.TryGetValue(vehicleId, variableId, out TraCIValue value))
        {
            return value;
        }

        throw new TraCIException(
            $"Vehicle '{vehicleId}' delivered no value for TraCI variable 0x{variableId:x2} in the "
            + "last step. Reading a whole state needs the whole variable set subscribed; see "
            + $"{nameof(TraCIVariables)}.{nameof(TraCIVariables.VehicleState)}.");
    }
}
