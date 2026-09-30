namespace CarlaNet.CoSim;

/// <summary>
/// Which SUMO vehicles are worth a full state subscription, and which of those hold a rendered
/// actor.
/// </summary>
/// <remarks>
/// <para><b>Two sets, not one, and they cost different things.</b> A rendered vehicle costs a CARLA
/// actor and a batch entry per world tick. A <i>subscribed</i> vehicle costs SUMO's own step time,
/// charged whether or not anything reads the result: measured at 388 live vehicles on the Arapahoe
/// network, 3.73 ms per step with nothing subscribed against 9.26 ms with the bridge's variable set
/// subscribed and never read. So the subscribed set is a budget in its own right, and it is
/// governed by the render set with a wider margin rather than by the whole population.</para>
///
/// <para><b>Both predicates take what the caller already knows about the vehicle</b>, because
/// without it there is no hysteresis, and without hysteresis a vehicle on a boundary flickers in and
/// out on consecutive ticks. With no dissolve on entry or exit that is the most visible failure this
/// design has, so the shape of the interface makes the implementer answer the question.</para>
///
/// <para><b>Each pass begins by telling the policy what the session holds</b> (<see cref="BeginPass"/>):
/// the instant, SUMO's step, the seed, the bodies' reach and the cameras. A policy that decides from a
/// fixed region ignores all of it; one that follows the cameras decides from nothing else. The time a
/// released vehicle is held for (<see cref="ReleaseLagSeconds"/>) is the render-set manager's to keep,
/// so that every policy's releases are timed and recorded the same way.</para>
/// </remarks>
public interface IRenderSetPolicy
{
    /// <summary>How many vehicles may hold a rendered actor at once.</summary>
    int Capacity { get; }

    /// <summary>
    /// Simulated seconds of travel ahead of a camera's footprint a vehicle is admitted at, under the rule
    /// in force at this pass; zero for a rule with no cameras.
    /// </summary>
    double AdmitLeadSeconds { get; }

    /// <summary>
    /// Simulated seconds a rendered vehicle that has stopped passing <see cref="ShouldRender"/> keeps its
    /// place for, under the rule in force at this pass, before it is released; zero releases it at the
    /// first pass it fails.
    /// </summary>
    double ReleaseLagSeconds { get; }

    /// <summary>The rule the last <see cref="BeginPass"/> put in force.</summary>
    RenderSetRule ActiveRule { get; }

    /// <summary>The footprint of every camera the last pass decided from; empty under the circle.</summary>
    IReadOnlyList<CameraFootprint> Footprints { get; }

    /// <summary>What the policy is, in the report's words.</summary>
    string Description { get; }

    /// <summary>
    /// Take the session's state for the pass about to be made, before either predicate is asked about
    /// any vehicle.
    /// </summary>
    void BeginPass(RenderSetPass pass);

    /// <summary>
    /// Whether a vehicle at this projected position is worth its full state subscription.
    /// </summary>
    /// <param name="x">SUMO easting, metres.</param>
    /// <param name="y">SUMO northing, metres.</param>
    /// <param name="alreadySubscribed">
    /// Whether the vehicle already holds one. An implementation that answers the same for both
    /// values has no hysteresis and will oscillate.
    /// </param>
    bool ShouldSubscribe(double x, double y, bool alreadySubscribed);

    /// <summary>Whether a subscribed vehicle should hold a rendered actor.</summary>
    /// <param name="frame">The vehicle's state for the SUMO step just read.</param>
    /// <param name="alreadyRendered">Whether it holds one now.</param>
    bool ShouldRender(in CoSimVehicleFrame frame, bool alreadyRendered);

    /// <summary>
    /// The order to admit in when more vehicles pass <see cref="ShouldRender"/> than
    /// <see cref="Capacity"/> allows. Lowest first.
    /// </summary>
    /// <param name="frame">The vehicle's state for the SUMO step just read.</param>
    /// <param name="alreadyRendered">
    /// Whether it holds a place now, which a policy may prefer so that a newcomer does not take the
    /// place of a vehicle already in view.
    /// </param>
    /// <remarks>
    /// Must be a pure function of the frame, of whether the vehicle is rendered and of session-fixed
    /// inputs -- the seed among them. Two runs of one seed that admit different sets are two runs
    /// nobody can compare, and nothing downstream would report the difference.
    /// </remarks>
    double Rank(in CoSimVehicleFrame frame, bool alreadyRendered);
}
