using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// A body the session has lent to a SUMO vehicle, as the server is told of it.
/// </summary>
/// <param name="Actor">The body.</param>
/// <param name="VehicleId">The SUMO vehicle it is drawn for.</param>
/// <param name="VehicleTypeId">The vehicle type the scenario's <c>vType</c> declared for that vehicle.</param>
public readonly record struct LentBody(ActorId Actor, string VehicleId, string VehicleTypeId);
