// Ported from Eclipse SUMO's reference TraCI client, tools/traci/_simulation.py -- getTime,
// getDeltaT, getEndTime, getDepartedIDList, getArrivedIDList and getMinExpectedNumber.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2011-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// TraCI's simulation domain: the clock, and what changed in the population this step.
/// </summary>
public sealed class SumoSimulationDomain
{
    private readonly TraCIConnection _connection;

    internal SumoSimulationDomain(TraCIConnection connection)
    {
        _connection = connection;
    }

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

    /// <summary>One simulation variable, with the type the wire gave it.</summary>
    public TraCIValue Read(int variableId) =>
        _connection.GetVariable(TraCIConstants.CMD_GET_SIM_VARIABLE, variableId, string.Empty);
}
