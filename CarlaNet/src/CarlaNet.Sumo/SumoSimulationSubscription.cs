// Ported from Eclipse SUMO's reference TraCI client: tools/traci/_simulation.py's subscribe, which
// subscribes the simulation domain under the empty object id, and the subscribe /
// getSubscriptionResults path in tools/traci/domain.py:188-223.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// The simulation domain's variables, delivered with every step: the clock, and what changed in the
/// population.
/// </summary>
/// <remarks>
/// <para><b>One subscribe, then nothing per step.</b> Read through <see cref="SumoSimulationDomain"/>'s
/// getters, each variable is a round trip after every step; subscribed, all of them arrive inside the
/// step's own answer and reading them costs only the decode. The subscription is made once, with one
/// round trip, and lasts as long as the simulation.</para>
///
/// <para><b>The same values as the getters, from the same moment.</b> SUMO fills a subscription after
/// the step by calling the getter itself (<c>TraCIServer::postProcessSimulationStep</c>), before any
/// client has sent its next command, so every list here is the one a direct get would have answered
/// and <see cref="Time"/> is the clock that goes with them. Measured on SUMO 1.27.0 over a run with
/// departures, arrivals, a stop and a parking stay: every list equal to its direct get on every
/// step.</para>
///
/// <para>A subscribe is answered with the current values, so they are readable from the moment
/// <see cref="Subscribe"/> returns, not only after the next step; the lists then hold the last step's
/// events, which before any step is none.</para>
/// </remarks>
public sealed class SumoSimulationSubscription
{
    /// <summary>
    /// What a co-simulation bridge reads every step: the clock, how much is left, who departed and
    /// arrived, who is still waiting to be inserted, every event list a vehicle's interval is opened or
    /// closed by, and how many vehicles a collision began for.
    /// </summary>
    /// <remarks>
    /// Every one is a whole-population value a step delivers once, so adding one costs bytes in the
    /// step's answer and never a round trip; none of them is per vehicle.
    /// </remarks>
    public static readonly int[] StepVariables =
    [
        TraCIConstants.VAR_TIME,
        TraCIConstants.VAR_MIN_EXPECTED_VEHICLES,
        TraCIConstants.VAR_DEPARTED_VEHICLES_IDS,
        TraCIConstants.VAR_ARRIVED_VEHICLES_IDS,
        TraCIConstants.VAR_PENDING_VEHICLES,
        TraCIConstants.VAR_STOP_STARTING_VEHICLES_IDS,
        TraCIConstants.VAR_STOP_ENDING_VEHICLES_IDS,
        TraCIConstants.VAR_PARKING_STARTING_VEHICLES_IDS,
        TraCIConstants.VAR_PARKING_ENDING_VEHICLES_IDS,
        TraCIConstants.VAR_TELEPORT_STARTING_VEHICLES_IDS,
        TraCIConstants.VAR_EMERGENCYSTOPPING_VEHICLES_IDS,
        TraCIConstants.VAR_COLLIDING_VEHICLES_NUMBER,
    ];

    private readonly TraCIConnection _connection;
    private readonly TraCISubscriptionResults _results;
    private int[] _variables = [];

    internal SumoSimulationSubscription(TraCIConnection connection)
    {
        _connection = connection;
        _results = connection.SubscriptionResults(TraCIConstants.RESPONSE_SUBSCRIBE_SIM_VARIABLE);
    }

    /// <summary>Whether the simulation domain has been subscribed.</summary>
    public bool IsSubscribed => _variables.Length > 0;

    /// <summary>The variables subscribed, in the order they were; empty before <see cref="Subscribe"/>.</summary>
    public IReadOnlyList<int> Variables => _variables;

    /// <summary>
    /// Have SUMO deliver these simulation variables with every step from now on, replacing any set
    /// subscribed before. One round trip, and the current values arrive with its answer.
    /// </summary>
    /// <param name="variables">The variables; <see cref="StepVariables"/> where none are given.</param>
    /// <exception cref="ArgumentException">
    /// None were given, or one of them is written as a compound this client has no decoder for.
    /// </exception>
    public void Subscribe(IReadOnlyList<int>? variables = null)
    {
        int[] wanted = [.. variables ?? StepVariables];
        if (wanted.Length == 0)
        {
            throw new ArgumentException(
                "Subscribing no variables removes the subscription; give at least one.", nameof(variables));
        }

        _connection.Subscribe(TraCIConstants.CMD_SUBSCRIBE_SIM_VARIABLE, string.Empty, wanted);
        _variables = wanted;
    }

    /// <summary>TraCI's clock: simulated seconds since the configuration's begin, as of the last step.</summary>
    public double Time => Require(TraCIConstants.VAR_TIME).AsDouble;

    /// <summary>
    /// How many vehicles SUMO still has running or still expects to insert, as of the last step; see
    /// <see cref="SumoSimulationDomain.ExpectedVehicleCount"/>.
    /// </summary>
    public int ExpectedVehicleCount => Require(TraCIConstants.VAR_MIN_EXPECTED_VEHICLES).AsInt;

    /// <summary>The vehicles SUMO inserted during the last step; see <see cref="SumoSimulationDomain.DepartedVehicleIds"/>.</summary>
    public IReadOnlyList<string> DepartedVehicleIds => Require(TraCIConstants.VAR_DEPARTED_VEHICLES_IDS).AsStringList;

    /// <summary>The vehicles SUMO removed during the last step; see <see cref="SumoSimulationDomain.ArrivedVehicleIds"/>.</summary>
    public IReadOnlyList<string> ArrivedVehicleIds => Require(TraCIConstants.VAR_ARRIVED_VEHICLES_IDS).AsStringList;

    /// <summary>
    /// The vehicles whose departure time has come and that SUMO has so far failed to insert; see
    /// <see cref="SumoSimulationDomain.PendingVehicleIds"/>.
    /// </summary>
    public IReadOnlyList<string> PendingVehicleIds => Require(TraCIConstants.VAR_PENDING_VEHICLES).AsStringList;

    /// <summary>
    /// How many vehicles a collision began for in the last step, each counted for every collision that
    /// began for it: zero on a step in which none began, including every later step of one still going
    /// on.
    /// </summary>
    /// <remarks>
    /// <para>SUMO counts a vehicle here only when it registers a collision for the first time; one it
    /// registers again on a later step because its vehicles are still in contact is kept in
    /// <see cref="SumoSimulationDomain.Collisions"/> and not counted (<c>MSNet::registerCollision</c>, which
    /// answers whether the collision is new, and <c>MSLane::handleCollisionBetween</c>, which counts only a
    /// new one). And SUMO drops a collision its vehicles were not in contact for at the end of the step
    /// (<c>MSNet::removeOutdatedCollisions</c>). So the collision list is empty on a step where this is
    /// zero, unless the list was not empty on the step before.</para>
    ///
    /// <para>A vehicle in a collision with a person is counted, the person is not.</para>
    /// </remarks>
    public int CollidingVehicleCount => Require(TraCIConstants.VAR_COLLIDING_VEHICLES_NUMBER).AsInt;

    /// <summary>
    /// The last step's event lists with the clock they go with, each read from the step's answer.
    /// </summary>
    /// <exception cref="TraCIException">
    /// The subscription does not carry the clock or one of the lists, which <see cref="StepVariables"/>
    /// does.
    /// </exception>
    public SumoStepEvents ReadEvents() => new(
        Time,
        DepartedVehicleIds,
        ArrivedVehicleIds,
        Require(TraCIConstants.VAR_STOP_STARTING_VEHICLES_IDS).AsStringList,
        Require(TraCIConstants.VAR_STOP_ENDING_VEHICLES_IDS).AsStringList,
        Require(TraCIConstants.VAR_PARKING_STARTING_VEHICLES_IDS).AsStringList,
        Require(TraCIConstants.VAR_PARKING_ENDING_VEHICLES_IDS).AsStringList,
        Require(TraCIConstants.VAR_TELEPORT_STARTING_VEHICLES_IDS).AsStringList,
        Require(TraCIConstants.VAR_EMERGENCYSTOPPING_VEHICLES_IDS).AsStringList);

    /// <summary>One subscribed variable from the last step, with the type the wire gave it.</summary>
    public bool TryReadVariable(int variableId, out TraCIValue value) =>
        _results.TryGetValue(string.Empty, variableId, out value);

    private TraCIValue Require(int variableId)
    {
        if (_results.TryGetValue(string.Empty, variableId, out TraCIValue value))
        {
            return value;
        }

        throw new TraCIException(
            $"The simulation domain delivered no value for TraCI variable 0x{variableId:x2} in the last "
            + (IsSubscribed
                ? "step. It is not among the subscribed variables, or SUMO refused it; see "
                  + $"{nameof(TraCIConnection)}.{nameof(TraCIConnection.LastStepFailures)}."
                : $"step, because the simulation domain has not been subscribed; see {nameof(Subscribe)}."));
    }
}
