// Ported from Eclipse SUMO's reference TraCI client, tools/traci/_simulation.py -- getTime,
// getDeltaT, getEndTime, getDepartedIDList, getArrivedIDList, getMinExpectedNumber,
// getPendingVehicles and getCollisions (_readCollisions).
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2011-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// TraCI's simulation domain: the clock, and what changed in the population this step.
/// </summary>
/// <remarks>
/// Each getter here is a round trip. A caller that reads these every step subscribes them instead
/// (<see cref="Subscription"/>), and they then arrive with the step's own answer.
/// </remarks>
public sealed class SumoSimulationDomain
{
    private readonly TraCIConnection _connection;

    internal SumoSimulationDomain(TraCIConnection connection)
    {
        _connection = connection;
        Subscription = new SumoSimulationSubscription(connection);
    }

    /// <summary>
    /// The simulation variables SUMO delivers with every step, once subscribed: the clock, the
    /// departures and arrivals, and the step's event lists.
    /// </summary>
    public SumoSimulationSubscription Subscription { get; }

    /// <summary>Simulated seconds since the configuration's begin.</summary>
    public double Time => Read(TraCIConstants.VAR_TIME).AsDouble;

    /// <summary>
    /// Seconds of simulated time in one step, as the configuration's <c>step-length</c> declared it.
    /// Fixed for the life of a simulation.
    /// </summary>
    public double StepLength => Read(TraCIConstants.VAR_DELTA_T).AsDouble;

    /// <summary>The configured end time in seconds, or <c>-1</c> where none was configured.</summary>
    public double EndTime => Read(TraCIConstants.VAR_END).AsDouble;

    /// <summary>
    /// The vehicles SUMO inserted during the step just taken.
    /// </summary>
    /// <remarks>
    /// This is the only account of an insertion. A vehicle that departs and arrives inside one step
    /// appears in this list and in <see cref="ArrivedVehicleIds"/>, and never in a vehicle list at
    /// all.
    /// </remarks>
    public IReadOnlyList<string> DepartedVehicleIds =>
        Read(TraCIConstants.VAR_DEPARTED_VEHICLES_IDS).AsStringList;

    /// <summary>
    /// The vehicles SUMO removed during the step just taken, whether they reached their destination
    /// or were taken out.
    /// </summary>
    public IReadOnlyList<string> ArrivedVehicleIds =>
        Read(TraCIConstants.VAR_ARRIVED_VEHICLES_IDS).AsStringList;

    /// <summary>
    /// How many vehicles SUMO still has running or still expects to insert. Zero means the scenario
    /// has nothing left to do, however much time remains on the clock.
    /// </summary>
    public int ExpectedVehicleCount => Read(TraCIConstants.VAR_MIN_EXPECTED_VEHICLES).AsInt;

    /// <summary>
    /// The vehicles whose departure time has come and that SUMO has so far failed to insert, which it
    /// retries every step.
    /// </summary>
    /// <remarks>
    /// A vehicle leaves this list by being inserted, which puts it in
    /// <see cref="DepartedVehicleIds"/>, or by being dropped: once it has waited longer than
    /// <c>max-depart-delay</c>, or when its edge is being vaporised. SUMO drops it without a warning
    /// or a state change of any kind (<c>MSInsertionControl::tryInsert</c>), so leaving this list
    /// without departing is the only trace a dropped vehicle leaves. A vehicle refused on its very first
    /// attempt for a start lane it may not use is dropped before it is ever listed here.
    /// </remarks>
    public IReadOnlyList<string> PendingVehicleIds =>
        Read(TraCIConstants.VAR_PENDING_VEHICLES).AsStringList;

    /// <summary>
    /// The collisions SUMO registered in the step just taken, including any registered earlier whose
    /// vehicles are still in contact.
    /// </summary>
    /// <remarks>
    /// SUMO writes these as a compound whose declared member count is not the number of members that
    /// follow -- one plus four per collision, over one plus nine -- so the generic decoder would stop
    /// part-way through the first collision. This reads them as SUMO's own client does, field by field.
    /// </remarks>
    public IReadOnlyList<SumoCollision> Collisions
    {
        get
        {
            TraCIReader reader = _connection.GetVariableForDedicatedDecoder(
                TraCIConstants.CMD_GET_SIM_VARIABLE, TraCIConstants.VAR_COLLISIONS, string.Empty);
            return ReadCollisions(reader);
        }
    }

    /// <summary>One simulation variable, with the type the wire gave it.</summary>
    public TraCIValue Read(int variableId) =>
        _connection.GetVariable(TraCIConstants.CMD_GET_SIM_VARIABLE, variableId, string.Empty);

    /// <summary>
    /// Decode the collisions from a reader positioned at the value's type byte: a compound, SUMO's
    /// member count, the number of collisions as a typed integer, then nine typed fields for each.
    /// </summary>
    internal static IReadOnlyList<SumoCollision> ReadCollisions(TraCIReader reader)
    {
        int type = reader.ReadUnsignedByte();
        if (type != TraCIConstants.TYPE_COMPOUND)
        {
            throw new FatalTraCIError(
                $"SUMO answered the collisions with value type 0x{type:x2} rather than a compound.");
        }

        // SUMO's declared member count, which counts four per collision where nine follow.
        reader.ReadInt();
        int count = Field(reader, TraCIValueKind.Integer).AsInt;
        if (count < 0)
        {
            throw new FatalTraCIError($"SUMO reported {count} collisions.");
        }

        var collisions = new List<SumoCollision>(count);
        for (int index = 0; index < count; index++)
        {
            collisions.Add(new SumoCollision(
                Field(reader, TraCIValueKind.String).AsString,
                Field(reader, TraCIValueKind.String).AsString,
                Field(reader, TraCIValueKind.String).AsString,
                Field(reader, TraCIValueKind.String).AsString,
                Field(reader, TraCIValueKind.Double).AsDouble,
                Field(reader, TraCIValueKind.Double).AsDouble,
                Field(reader, TraCIValueKind.String).AsString,
                Field(reader, TraCIValueKind.String).AsString,
                Field(reader, TraCIValueKind.Double).AsDouble));
        }

        return collisions;
    }

    private static TraCIValue Field(TraCIReader reader, TraCIValueKind expected)
    {
        TraCIValue value = reader.ReadTypedValue(TraCIConstants.VAR_COLLISIONS);
        if (value.Kind != expected)
        {
            throw new FatalTraCIError(
                $"A field of SUMO's collisions arrived as {value.Kind} where {expected} belongs, so the "
                + "value is being read at the wrong offset.");
        }

        return value;
    }
}
