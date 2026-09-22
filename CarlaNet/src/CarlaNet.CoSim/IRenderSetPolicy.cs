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
/// <para>The policy itself is not the bridge's to design -- the render-set contract owns the
/// predicate and the performance section owns the capacity. This is the interface both have to fit
/// into.</para>
/// </remarks>
public interface IRenderSetPolicy
{
    /// <summary>How many vehicles may hold a rendered actor at once.</summary>
    int Capacity { get; }

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
    /// <remarks>
    /// Must be a pure function of the frame and of session-fixed inputs. Two runs of one seed that
    /// admit different sets are two runs nobody can compare, and nothing downstream would report the
    /// difference.
    /// </remarks>
    double Rank(in CoSimVehicleFrame frame);
}
