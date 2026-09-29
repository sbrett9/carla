using System.Globalization;
using CarlaNet.Sumo;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// One collision SUMO registered, over the span of simulated time it lasted, with the bodies that
/// rendered the two vehicles while it lasted.
/// </summary>
/// <param name="Collision">
/// What SUMO reported on the step the collision began: the collider, the victim, their types and
/// speeds, SUMO's own name for what happened, and the lane and position.
/// </param>
/// <param name="BeganAtSeconds">The simulated second of the first SUMO frame that reported it.</param>
/// <param name="EndedAtSeconds">
/// The simulated second of the first SUMO frame that no longer reported it, or the session's end.
/// </param>
/// <param name="ColliderActor">
/// The body that rendered the collider while the collision lasted -- the first it held, since a vehicle
/// admitted on the step its collision began takes up its body on the next tick -- or zero where it held
/// none.
/// </param>
/// <param name="VictimActor">The same for the victim.</param>
/// <remarks>
/// <para><b>A fact about the corpus, not a failure of the run.</b> The run records it and goes on: a
/// consumer that wants spans with collisions in them removed or kept filters on this record, and the
/// imagery of those spans is SUMO's traffic as SUMO simulated it. Under <c>collision.action warn</c>
/// the two vehicles carry on as SUMO moves them, and kinematic bodies pass through each other rather
/// than colliding.</para>
///
/// <para><b>One record per collision, not per step.</b> SUMO reports a collision again on every step
/// its two vehicles stay in contact, so the session keeps it open while it is reported and closes it
/// on the first frame it is not, at which point it is handed out.</para>
/// </remarks>
public readonly record struct CollisionSpan(
    SumoCollision Collision,
    double BeganAtSeconds,
    double EndedAtSeconds,
    ActorId ColliderActor,
    ActorId VictimActor)
{
    /// <summary>The span in one line: who, where, what SUMO called it, and when.</summary>
    public override string ToString() =>
        $"'{Collision.ColliderId}' into '{Collision.VictimId}' ({Collision.Kind}) on {Collision.LaneId} at "
        + $"{Collision.LanePositionMetres.ToString("0.00", CultureInfo.InvariantCulture)} m, t="
        + $"{BeganAtSeconds.ToString("0.###", CultureInfo.InvariantCulture)} to "
        + $"{EndedAtSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";
}
