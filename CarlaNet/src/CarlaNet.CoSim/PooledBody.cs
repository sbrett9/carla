using CarlaNet.Types.Geom;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// One CARLA actor the bridge owns for the whole session, and the slot it stands in when nothing is
/// borrowing it.
/// </summary>
/// <param name="Actor">The actor the server created.</param>
/// <param name="BlueprintId">Which body it is. An actor's blueprint cannot change, so neither can this.</param>
/// <param name="Parking">Where it stands while free.</param>
public readonly record struct PooledBody(ActorId Actor, string BlueprintId, Transform Parking);
