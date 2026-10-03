namespace CarlaNet.Sumo;

/// <summary>
/// What kind of stop a vehicle made or will make, as TraCI's <c>getStops</c> reports it.
/// </summary>
/// <remarks>
/// SUMO's own bits (<c>SUMOVehicleParameter::Stop::getFlags</c>), the same as the <c>STOP_*</c>
/// constants a client sets a stop with. <c>getNextStops</c> reports a different, older layout shifted
/// left by one with a "reached" bit in front; this is not that one.
/// </remarks>
[Flags]
public enum SumoStopFlags
{
    /// <summary>An ordinary stop on the lane, for a duration or until a time.</summary>
    None = 0,

    /// <summary>The vehicle leaves its lane for the stay, and comes back onto it to resume its route.</summary>
    Parking = TraCIConstants.STOP_PARKING,

    /// <summary>The stop ends when a person boards or leaves, not at a time.</summary>
    Triggered = TraCIConstants.STOP_TRIGGERED,

    /// <summary>The stop ends when a container is loaded or unloaded.</summary>
    ContainerTriggered = TraCIConstants.STOP_CONTAINER_TRIGGERED,

    /// <summary>The stop is at a bus stop the network declares.</summary>
    BusStop = TraCIConstants.STOP_BUS_STOP,

    /// <summary>The stop is at a container stop.</summary>
    ContainerStop = TraCIConstants.STOP_CONTAINER_STOP,

    /// <summary>The stop is at a charging station.</summary>
    ChargingStation = TraCIConstants.STOP_CHARGING_STATION,

    /// <summary>The stop is in a parking area the network declares.</summary>
    ParkingArea = TraCIConstants.STOP_PARKING_AREA,

    /// <summary>The stop is at an overhead-wire segment.</summary>
    OverheadWire = TraCIConstants.STOP_OVERHEAD_WIRE,
}
