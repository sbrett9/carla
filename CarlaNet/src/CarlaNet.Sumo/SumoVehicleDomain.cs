// Ported from Eclipse SUMO's reference TraCI client, tools/traci/_vehicle.py -- getIDList,
// getIDCount, the scalar getters, remove (_vehicle.py:1479-1482) and moveToXY (:1485-1499).
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2011-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// TraCI's vehicle domain: what SUMO's vehicles are doing, and the two things a bridge does to
/// them.
/// </summary>
/// <remarks>
/// The per-vehicle getters here are the fallback, not the read path. Every one is a round trip, so
/// reading a population through them is linear in round trips where
/// <see cref="Subscription"/> is one. They are here for a question asked once -- inspecting a single
/// vehicle, or checking a subscription's answer against a direct one, which is what the agreement
/// check against SUMO's own client does.
/// </remarks>
public sealed class SumoVehicleDomain
{
    private readonly TraCIConnection _connection;

    internal SumoVehicleDomain(TraCIConnection connection, IReadOnlyList<int> subscribedVariables)
    {
        _connection = connection;
        Subscription = new SumoVehicleSubscription(connection, subscribedVariables);
    }

    /// <summary>Which vehicles SUMO delivers state for with every step.</summary>
    public SumoVehicleSubscription Subscription { get; }

    /// <summary>
    /// Every vehicle in the simulation right now. A whole-population question, and one round trip
    /// however large the population -- but inside a step loop the subscription and the departure and
    /// arrival lists say the same thing without it.
    /// </summary>
    public IReadOnlyList<string> Ids =>
        _connection.GetVariable(TraCIConstants.CMD_GET_VEHICLE_VARIABLE,
                                TraCIConstants.TRACI_ID_LIST, string.Empty).AsStringList;

    /// <summary>How many vehicles are in the simulation right now.</summary>
    public int Count =>
        _connection.GetVariable(TraCIConstants.CMD_GET_VEHICLE_VARIABLE,
                                TraCIConstants.ID_COUNT, string.Empty).AsInt;

    /// <summary>
    /// Position of the centre of the vehicle's front bumper, in projected metres.
    /// </summary>
    public (double X, double Y) Position(string vehicleId)
    {
        (double x, double y, _) = Read(vehicleId, TraCIVariables.Position).AsPosition;
        return (x, y);
    }

    /// <summary>Heading in degrees clockwise from north.</summary>
    public double Angle(string vehicleId) => Read(vehicleId, TraCIVariables.Angle).AsDouble;

    /// <summary>Speed along the lane, in metres per second.</summary>
    public double Speed(string vehicleId) => Read(vehicleId, TraCIVariables.Speed).AsDouble;

    /// <summary>The edge the vehicle is on, with a leading colon for one inside a junction.</summary>
    public string EdgeId(string vehicleId) => Read(vehicleId, TraCIVariables.RoadId).AsString;

    /// <summary>The lane the vehicle is on.</summary>
    public string LaneId(string vehicleId) => Read(vehicleId, TraCIVariables.LaneId).AsString;

    /// <summary>The vehicle type, which is what the scenario's <c>vType</c> declared.</summary>
    public string TypeId(string vehicleId) => Read(vehicleId, TraCIVariables.TypeId).AsString;

    /// <summary>The signal word: indicators and brake light.</summary>
    public SumoVehicleSignals Signals(string vehicleId) =>
        (SumoVehicleSignals)Read(vehicleId, TraCIVariables.Signals).AsInt;

    /// <summary>One variable of one vehicle, with the type the wire gave it.</summary>
    public TraCIValue Read(string vehicleId, int variableId) =>
        _connection.GetVariable(TraCIConstants.CMD_GET_VEHICLE_VARIABLE, variableId, vehicleId);

    /// <summary>
    /// Ask SUMO for one vehicle's whole state directly, without a subscription.
    /// </summary>
    /// <remarks>
    /// Seven round trips for one vehicle. Correct, and the wrong shape for a step loop: used per
    /// vehicle per step this is the path <see cref="SumoVehicleSubscription"/> exists to avoid.
    /// </remarks>
    public SumoVehicleState ReadStateWithoutSubscription(string vehicleId)
    {
        (double x, double y) = Position(vehicleId);
        return new SumoVehicleState(
            vehicleId,
            x,
            y,
            Angle(vehicleId),
            Speed(vehicleId),
            EdgeId(vehicleId),
            LaneId(vehicleId),
            TypeId(vehicleId),
            Signals(vehicleId));
    }

    /// <summary>
    /// Take a vehicle out of the simulation.
    /// </summary>
    /// <param name="vehicleId">The vehicle to remove.</param>
    /// <param name="reason">
    /// What SUMO records the removal as, which decides whether it counts as an arrival and what its
    /// trip statistics say. The default, <see cref="TraCIConstants.REMOVE_VAPORIZED"/>, is SUMO's
    /// own for a vehicle taken out by a client.
    /// </param>
    public void Remove(string vehicleId, int reason = TraCIConstants.REMOVE_VAPORIZED)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        TraCIWriter writer = _connection.BeginSetCommand(
            TraCIConstants.CMD_SET_VEHICLE_VARIABLE, TraCIConstants.REMOVE, vehicleId);
        writer.WriteByte((sbyte)reason);
        _connection.SendPreparedCommand(TraCIConstants.CMD_SET_VEHICLE_VARIABLE);
    }

    /// <summary>
    /// Place a vehicle at a position and heading of the caller's choosing.
    /// </summary>
    /// <param name="vehicleId">The vehicle to move.</param>
    /// <param name="edgeId">A placement hint for resolving which edge is meant, or empty for none.</param>
    /// <param name="laneIndex">A placement hint for which lane of that edge.</param>
    /// <param name="x">Projected easting in metres.</param>
    /// <param name="y">Projected northing in metres.</param>
    /// <param name="angle">Heading in degrees clockwise from north, or
    /// <see cref="TraCIConstants.INVALID_DOUBLE_VALUE"/> to take the edge's own.</param>
    /// <param name="keepRoute">
    /// <c>1</c> keeps the vehicle on its existing route and snaps to the nearest point on it;
    /// <c>0</c> lets it move to any edge, replacing its route with that one edge; <c>2</c> lets it
    /// leave the network entirely.
    /// </param>
    /// <param name="matchThreshold">How far SUMO may search for a position to snap to, in metres.</param>
    /// <remarks>
    /// The direction a playback bridge does <b>not</b> need: under SUMO-drives-CARLA-renders, poses
    /// travel the other way. It is here because it is the one place the write path carries a
    /// compound of mixed types, so it is what exercises that half of the codec.
    /// </remarks>
    public void MoveToXY(string vehicleId,
                         string edgeId,
                         int laneIndex,
                         double x,
                         double y,
                         double angle = TraCIConstants.INVALID_DOUBLE_VALUE,
                         int keepRoute = 1,
                         double matchThreshold = 100.0)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        ArgumentNullException.ThrowIfNull(edgeId);

        TraCIWriter writer = _connection.BeginSetCommand(
            TraCIConstants.CMD_SET_VEHICLE_VARIABLE, TraCIConstants.MOVE_TO_XY, vehicleId);
        writer.WriteCompoundHeader(7);
        writer.WriteString(edgeId);
        writer.WriteInt(laneIndex);
        writer.WriteDouble(x);
        writer.WriteDouble(y);
        writer.WriteDouble(angle);
        writer.WriteByte((sbyte)keepRoute);
        writer.WriteDouble(matchThreshold);
        _connection.SendPreparedCommand(TraCIConstants.CMD_SET_VEHICLE_VARIABLE);
    }
}
