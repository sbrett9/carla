using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// The per-vehicle questions an observer may put to SUMO between two steps: when a vehicle was
/// inserted, how late, and the stops it has made or has ahead of it. Each is one round trip.
/// </summary>
/// <remarks>
/// <para><b>Asked when an event says the answer is needed, never on every step.</b> None of these can be
/// subscribed for the population without charging every vehicle's step for a value that changes a
/// handful of times in its life, and a per-vehicle getter in the step loop is linear in round trips --
/// the cost the subscriptions exist to avoid. So a vehicle's departure is asked once, when it is listed
/// as departed or when a window opens on a vehicle already driving; its completed stops once, when it is
/// listed as ending one. Every question is counted on the report
/// (<see cref="CoSimRunReport.VehicleQueries"/>), so a run that asks too many says so.</para>
///
/// <para><b>An answer stamped by SUMO, not by the bridge's clock.</b> A departure, and a stop's arrival
/// and departure, are SUMO's own stamps, one step before the TraCI clock of the step whose event list
/// announced them (<see cref="SumoStepEvents"/>). The instant a record carries is the event's; these are
/// what SUMO itself holds, recorded beside it.</para>
///
/// <para><b>A vehicle SUMO does not know answers null.</b> One that has arrived, or was never loaded, is
/// refused by SUMO, and a refusal of one question is routine and leaves the connection usable; it must
/// not stop the run as a SUMO that failed does. A connection that fails while asking does stop it.</para>
///
/// <para>From the thread that advances the session, between its steps: inside an observer's call, or
/// between two advances.</para>
/// </remarks>
public sealed class SumoVehicleQueries
{
    private readonly SumoVehicleDomain _vehicles;
    private readonly CoSimRunReport _report;

    internal SumoVehicleQueries(SumoVehicleDomain vehicles, CoSimRunReport report)
    {
        _vehicles = vehicles;
        _report = report;
    }

    /// <summary>
    /// SUMO's stamp for when it inserted the vehicle, simulated seconds; null for a vehicle not yet
    /// inserted, or one SUMO does not know.
    /// </summary>
    public double? Departure(string vehicleId) => Ask(vehicleId, () => _vehicles.Departure(vehicleId));

    /// <summary>
    /// How many simulated seconds after its declared departure SUMO inserted the vehicle -- for one not yet
    /// inserted, how long it has waited so far -- or null for one SUMO does not know.
    /// </summary>
    public double? DepartDelay(string vehicleId) =>
        Ask(vehicleId, () => (double?)_vehicles.DepartDelay(vehicleId));

    /// <summary>
    /// Every stop the vehicle has made, oldest first, or null for one SUMO does not know. A stop under way
    /// is not among them until it ends.
    /// </summary>
    public IReadOnlyList<SumoStop>? CompletedStops(string vehicleId) =>
        Ask(vehicleId, () => _vehicles.CompletedStops(vehicleId));

    /// <summary>
    /// Every stop still ahead of the vehicle, the one under way first, or null for one SUMO does not know.
    /// </summary>
    public IReadOnlyList<SumoStop>? UpcomingStops(string vehicleId) =>
        Ask(vehicleId, () => _vehicles.Stops(vehicleId));

    /// <summary>
    /// The index, counted from 0, of the route edge the vehicle is on or last left, or null for one SUMO
    /// does not know: asked when the vehicle's edge changes and something waits on its progress.
    /// </summary>
    public int? RouteIndex(string vehicleId) => Ask(vehicleId, () => (int?)_vehicles.RouteIndex(vehicleId));

    private T? Ask<T>(string vehicleId, Func<T?> question)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        _report.VehicleQueries++;
        try
        {
            return question();
        }
        catch (TraCIException)
        {
            // SUMO refused this one question -- it does not know the vehicle -- and read the frame to its
            // end, so the connection is as usable as it was.
            return default;
        }
    }
}
