using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// One thing SUMO did to one vehicle, at TraCI's clock for the step it was listed in.
/// </summary>
/// <param name="VehicleId">SUMO's vehicle id.</param>
/// <param name="AtSeconds">TraCI's clock for the step whose event list named the vehicle.</param>
public readonly record struct SumoVehicleEvent(string VehicleId, double AtSeconds)
{
    /// <summary>The event in one line.</summary>
    public override string ToString() =>
        $"'{VehicleId}' at t={AtSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";
}
