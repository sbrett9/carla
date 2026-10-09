// Source: carla/rpc/SupervisionUpdate.h
// SupervisionUpdate           MSGPACK_DEFINE_ARRAY(fresh, plan_id, vocabulary_version, vocabulary_digest, actors)
// SupervisionUpdateActor      MSGPACK_DEFINE_ARRAY(actor_id, state, annotations)
// SupervisionUpdateAnnotation MSGPACK_DEFINE_ARRAY(instance_id, labels, phase, role)
namespace CarlaNet.Types.Rpc.Supervision;

/// <summary>
/// One change to the supervision a co-simulation session holds on the server (<c>update_supervision</c>):
/// the plan it is bound from and the bodies whose supervision changes. Every row is a vehicle's; nothing
/// is held for the world apart from the plan (06 §3.5).
/// </summary>
/// <param name="Fresh">
/// Drop every row the server holds before applying this change: a session's first change, and the first
/// after it binds another plan.
/// </param>
/// <param name="PlanId">The plan every row is bound from. Empty withdraws all supervision.</param>
/// <param name="VocabularyVersion">The version of the vocabulary's core the plan was resolved against.</param>
/// <param name="VocabularyDigest">The digest over the plan's resolved terms.</param>
/// <param name="Actors">The bodies whose supervision changes, each replaced whole.</param>
[MessagePackObject]
public record struct SupervisionUpdate(
    [property: Key(0)] bool Fresh,
    [property: Key(1)] string PlanId,
    [property: Key(2)] uint VocabularyVersion,
    [property: Key(3)] string VocabularyDigest,
    [property: Key(4)] IReadOnlyList<SupervisionUpdateActor> Actors);

/// <summary>
/// What the author asserts, from this change on, of the vehicle one lent body draws.
/// </summary>
/// <param name="ActorId">The body.</param>
/// <param name="State">
/// <c>annotated</c>, <c>nominal</c> or <c>unlabelled</c>, spelled as the vocabulary's core spells it
/// (<c>CoreVocabulary.Name(SupervisionState)</c>). Unlabelled clears the body.
/// </param>
/// <param name="Annotations">Every instance in force for the vehicle; none when unlabelled.</param>
[MessagePackObject]
public record struct SupervisionUpdateActor(
    [property: Key(0)] uint ActorId,
    [property: Key(1)] string State,
    [property: Key(2)] IReadOnlyList<SupervisionUpdateAnnotation> Annotations);

/// <summary>One pattern instance in force for the vehicle a body draws.</summary>
[MessagePackObject]
public record struct SupervisionUpdateAnnotation(
    [property: Key(0)] string InstanceId,
    [property: Key(1)] IReadOnlyList<string> Labels,
    [property: Key(2)] string Phase,
    [property: Key(3)] string Role);
