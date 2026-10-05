using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// One SUMO frame the session read, as every observer is told of it: TraCI's clock for it, what SUMO
/// did to its vehicles in the step that produced it, and what the session made of it.
/// </summary>
/// <param name="WorldTick">World ticks the session had rendered when it read the frame.</param>
/// <param name="FrameSeconds">
/// TraCI's clock for the frame, which is the instant of everything in this record: the simulated second
/// SUMO reports once it has advanced, and the instant the frame that renders this state is stamped
/// with. The same as <see cref="SumoStepEvents.TimeSeconds"/>, read from the same answer.
/// </param>
/// <param name="AfterFastForward">
/// Whether this is the frame SUMO was fast-forwarded to, the first the session reads. Its events are
/// only those of the fast-forward's last step: whatever SUMO did before it happened while nothing was
/// watching, and is read back per vehicle where it is needed -- a departure, the stops already made --
/// through <see cref="Vehicles"/>. Before a session that starts at zero has taken any step, they are
/// none.
/// </param>
/// <param name="Events">
/// What SUMO reported happening to its vehicles in the step that produced the frame: insertions,
/// removals, stops and parking stays begun and ended, teleports begun and emergency stops, list by list.
/// </param>
/// <param name="Collisions">
/// The collisions SUMO reported at the frame, each one again on every step its two vehicles stay in
/// contact. SUMO is asked for them only on a frame where one began or one is still going on; on any other
/// the list is empty, and so is this (<see cref="CoSimRunReport.CollisionListReads"/>). Each is handed out
/// once more, as a span, when it is over (<see cref="SumoDriveSessionOptions.OnCollision"/>).
/// </param>
/// <param name="NotInserted">
/// The vehicles SUMO gave up trying to insert between the frame before and this one, which it does
/// without a word; always none on the first frame, which has no frame before it to compare against.
/// </param>
/// <param name="Vanished">
/// The vehicles SUMO no longer had at the frame without having listed them among its arrivals: taken
/// out between two steps by something other than SUMO's own step. Lent from the session: valid during
/// the call.
/// </param>
/// <param name="Frames">
/// Every vehicle SUMO has at the frame, and its state, by SUMO id. Lent from the session: valid during
/// the call, and refilled by the next frame, so copy what is kept.
/// </param>
/// <param name="RenderedVehicleIds">
/// The vehicles holding a place in the render set as decided at the frame: every vehicle SUMO has, or
/// under an optional limit the ones it admits. Lent from the session: valid during the call.
/// </param>
/// <param name="Pass">The admission pass made at the frame, as the report and its writer have it.</param>
/// <param name="Vehicles">
/// The per-vehicle questions an observer may put to SUMO about the frame, each one round trip, asked
/// only when an event says the answer is needed.
/// </param>
/// <remarks>
/// Built once per frame and only where an observer is registered, so a session nobody observes pays
/// nothing for it. Its lists from SUMO are the step's own and may be kept.
/// </remarks>
public sealed record SumoStepRecord(
    long WorldTick,
    double FrameSeconds,
    bool AfterFastForward,
    SumoStepEvents Events,
    IReadOnlyList<SumoCollision> Collisions,
    IReadOnlyList<VehicleNotInserted> NotInserted,
    IReadOnlyCollection<string> Vanished,
    IReadOnlyDictionary<string, CoSimVehicleFrame> Frames,
    IReadOnlyCollection<string> RenderedVehicleIds,
    AdmissionPass Pass,
    SumoVehicleQueries Vehicles)
{
    /// <summary>
    /// The vehicles that left the render set at the frame's pass, each with the span it held a place
    /// over, the body that drew it and why it left -- the intervals handed to
    /// <see cref="SumoDriveSessionOptions.OnRelease"/>, in the order they were released. A vehicle still
    /// in the render set when the session ends is released by the end, not by a frame, and is in none.
    /// </summary>
    public IReadOnlyList<RenderedVehicleInterval> Released { get; init; } = [];
}
