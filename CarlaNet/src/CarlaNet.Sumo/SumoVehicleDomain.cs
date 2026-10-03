// Ported from Eclipse SUMO's reference TraCI client, tools/traci/_vehicle.py -- getIDList,
// getIDCount, the scalar getters, getDeparture and getDepartDelay (:333-345), getStops (:785-804)
// with its reader _readStopData (:103-127), remove (_vehicle.py:1479-1482) and moveToXY (:1485-1499).
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

    /// <summary>
    /// SUMO's own stamp for when it inserted the vehicle, in simulated seconds, or null for a vehicle
    /// not yet inserted.
    /// </summary>
    /// <remarks>
    /// One step before the TraCI clock of the step that listed the vehicle as departed
    /// (<see cref="SumoStepEvents"/>): SUMO stamps the insertion with the step's opening second and
    /// reports its clock once it has advanced. Measured on SUMO 1.27.0: a vehicle listed at 1.0 s
    /// answers 0.0 s here. A question asked once per vehicle, not per step.
    /// </remarks>
    public double? Departure(string vehicleId) =>
        SumoStop.Seconds(Read(vehicleId, TraCIConstants.VAR_DEPARTURE).AsDouble);

    /// <summary>
    /// How many simulated seconds after the time the route declared SUMO inserted the vehicle: zero for
    /// one inserted on time, the length of the wait for one SUMO had to hold back. For a vehicle not yet
    /// inserted, how long it has waited so far.
    /// </summary>
    /// <remarks>
    /// The gap between the declared and the committed entry, which is congestion and so is part of what
    /// a run captures. A question asked once per vehicle, not per step.
    /// </remarks>
    public double DepartDelay(string vehicleId) => Read(vehicleId, TraCIConstants.VAR_DEPART_DELAY).AsDouble;

    /// <summary>
    /// A vehicle's stops: those still ahead of it, or those it has made.
    /// </summary>
    /// <param name="vehicleId">The vehicle.</param>
    /// <param name="limit">
    /// SUMO's own argument. Zero for every stop still ahead, the one under way included; a positive
    /// number for at most that many of them, nearest first; a negative number for at most that many of
    /// the stops already made, oldest first, ending with the one most recently left.
    /// </param>
    /// <remarks>
    /// One round trip. SUMO writes the answer as a compound whose declared member count is not the number
    /// of members that follow -- one plus four per stop, over one plus sixteen -- so it is read field by
    /// field, as SUMO's own client reads it (<see cref="ReadStops"/>). For a question asked when an event
    /// says the answer has changed, not on every step.
    /// </remarks>
    public IReadOnlyList<SumoStop> Stops(string vehicleId, int limit = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        TraCIReader reader = _connection.GetVariableForDedicatedDecoder(
            TraCIConstants.CMD_GET_VEHICLE_VARIABLE, TraCIConstants.VAR_NEXT_STOPS2, vehicleId, limit);
        return ReadStops(reader);
    }

    /// <summary>
    /// Every stop a vehicle has made, oldest first: the one most recently left last.
    /// </summary>
    /// <remarks>
    /// <see cref="Stops"/> with the largest negative limit an integer holds, which SUMO answers with all
    /// of them (<c>libsumo::Vehicle::getStops</c>). A stop under way is not among them until it ends. One
    /// round trip.
    /// </remarks>
    public IReadOnlyList<SumoStop> CompletedStops(string vehicleId) => Stops(vehicleId, -int.MaxValue);

    /// <summary>One variable of one vehicle, with the type the wire gave it.</summary>
    public TraCIValue Read(string vehicleId, int variableId) =>
        _connection.GetVariable(TraCIConstants.CMD_GET_VEHICLE_VARIABLE, variableId, vehicleId);

    /// <summary>
    /// Decode a vehicle's stops from a reader positioned at the value's type byte: a compound, SUMO's
    /// member count, the number of stops as a typed integer, then sixteen typed fields for each.
    /// </summary>
    /// <remarks>
    /// The field order is the server's (<c>TraCIServer::wrapNextStopDataVector</c>) and its client's
    /// (<c>_readStopData</c>): lane, end position, stopping place, flags, duration, until, start
    /// position, intended arrival, arrival, departure, split, join, act type, trip id, line, speed.
    /// </remarks>
    internal static IReadOnlyList<SumoStop> ReadStops(TraCIReader reader)
    {
        int type = reader.ReadUnsignedByte();
        if (type != TraCIConstants.TYPE_COMPOUND)
        {
            throw new FatalTraCIError(
                $"SUMO answered a vehicle's stops with value type 0x{type:x2} rather than a compound.");
        }

        // SUMO's declared member count, which counts four per stop where sixteen follow.
        reader.ReadInt();
        int count = StopField(reader, TraCIValueKind.Integer).AsInt;
        if (count < 0)
        {
            throw new FatalTraCIError($"SUMO reported {count} stops.");
        }

        var stops = new List<SumoStop>(count);
        for (int index = 0; index < count; index++)
        {
            string lane = StopField(reader, TraCIValueKind.String).AsString;
            double endPosition = StopField(reader, TraCIValueKind.Double).AsDouble;
            string stoppingPlace = StopField(reader, TraCIValueKind.String).AsString;
            int flags = StopField(reader, TraCIValueKind.Integer).AsInt;
            double duration = StopField(reader, TraCIValueKind.Double).AsDouble;
            double until = StopField(reader, TraCIValueKind.Double).AsDouble;
            double startPosition = StopField(reader, TraCIValueKind.Double).AsDouble;
            double intendedArrival = StopField(reader, TraCIValueKind.Double).AsDouble;
            double arrival = StopField(reader, TraCIValueKind.Double).AsDouble;
            double departure = StopField(reader, TraCIValueKind.Double).AsDouble;
            stops.Add(new SumoStop(
                lane,
                startPosition,
                endPosition,
                stoppingPlace,
                (SumoStopFlags)flags,
                SumoStop.Seconds(duration),
                SumoStop.Seconds(until),
                SumoStop.Seconds(intendedArrival),
                SumoStop.Seconds(arrival),
                SumoStop.Seconds(departure),
                StopField(reader, TraCIValueKind.String).AsString,
                StopField(reader, TraCIValueKind.String).AsString,
                StopField(reader, TraCIValueKind.String).AsString,
                StopField(reader, TraCIValueKind.String).AsString,
                StopField(reader, TraCIValueKind.String).AsString,
                StopField(reader, TraCIValueKind.Double).AsDouble));
        }

        return stops;
    }

    private static TraCIValue StopField(TraCIReader reader, TraCIValueKind expected)
    {
        TraCIValue value = reader.ReadTypedValue(TraCIConstants.VAR_NEXT_STOPS2);
        if (value.Kind != expected)
        {
            throw new FatalTraCIError(
                $"A field of a vehicle's stops arrived as {value.Kind} where {expected} belongs, so the "
                + "value is being read at the wrong offset.");
        }

        return value;
    }

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
