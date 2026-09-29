using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// A vehicle SUMO was trying to insert and gave up on: it left SUMO's insertion queue between two
/// SUMO frames without departing.
/// </summary>
/// <param name="VehicleId">SUMO's vehicle id.</param>
/// <param name="WaitingAtSeconds">The simulated second of the last SUMO frame that still had it waiting.</param>
/// <param name="GoneAtSeconds">The simulated second of the first SUMO frame that no longer did.</param>
/// <remarks>
/// SUMO drops such a vehicle silently -- once it has waited longer than <c>max-depart-delay</c>, or
/// when its edge is being vaporised -- with no warning and no state change, so this record is the only
/// account of it. The vehicle never existed on the network, so it has no truth to record; what it
/// changes is the population the scenario authored, which is what a consumer comparing the two needs to
/// be told.
/// </remarks>
public readonly record struct VehicleNotInserted(string VehicleId, double WaitingAtSeconds, double GoneAtSeconds)
{
    /// <summary>The record in one line.</summary>
    public override string ToString() =>
        $"'{VehicleId}', waiting at t={WaitingAtSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s "
        + $"and gone by t={GoneAtSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";
}
