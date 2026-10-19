namespace CarlaNet.Types.Supervision;

// The supervision in force on a frame (06_Truth_And_Annotation.md §3.1-§3.4, §8.2): what the scenario's
// author asserts of each vehicle. One shape for every side of it -- what a drive session puts in force,
// what the server carries on each world-observer snapshot, and what a reader decodes -- so the truth a
// recorder writes is the truth the session named, field for field. Every row is a vehicle's: nothing is
// held for the world apart from the plan it is all bound from (06 §3.5).

/// <summary>
/// The plan supervision is bound from, by the identity every record of it carries: the plan, and the
/// vocabulary version and digest that pin what its terms mean (06 D6.30).
/// </summary>
/// <param name="PlanId">The supervision plan's <c>plan_id</c>.</param>
/// <param name="VocabularyVersion">The version of the vocabulary's core the plan's terms were resolved against.</param>
/// <param name="VocabularyDigest">The plan's <c>vocabulary_digest</c>, over its resolved term set.</param>
public sealed record SupervisionPlanIdentity(string PlanId, int VocabularyVersion, string VocabularyDigest);

/// <summary>One pattern instance in force for a vehicle (06 §3.4, §8.2's <c>&lt;annotation&gt;</c>).</summary>
/// <param name="InstanceId">The instance, as the plan names it: <c>&lt;scenario_id&gt;/&lt;name&gt;</c>.</param>
/// <param name="Labels">The terms it is labelled with, each spelled as the plan's vocabulary spells it.</param>
/// <param name="Phase">The phase of its interval in force; empty where the plan declares none.</param>
/// <param name="Role">The role the vehicle plays in it; empty where the plan declares none.</param>
public sealed record AnnotationInForce(string InstanceId, IReadOnlyList<string> Labels, string Phase, string Role)
{
    /// <inheritdoc/>
    public bool Equals(AnnotationInForce? other) =>
        other is not null
        && InstanceId == other.InstanceId
        && Phase == other.Phase
        && Role == other.Role
        && Labels.SequenceEqual(other.Labels);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(InstanceId, Phase, Role, Labels.Count);
}

/// <summary>
/// What the author asserts of one vehicle on a frame: its state, and the pattern instances in force
/// (06 §3.1).
/// </summary>
/// <param name="State">
/// <see cref="SupervisionState.Annotated"/>, <see cref="SupervisionState.Nominal"/> or
/// <see cref="SupervisionState.Unlabelled"/>. The assertion lives in the state alone.
/// </param>
/// <param name="Annotations">
/// The instances in force, which say which pattern, or which authored ordinary behaviour, the state is
/// about. At least one when annotated, any number when nominal, none when unlabelled.
/// </param>
public sealed record SupervisionInForce(SupervisionState State, IReadOnlyList<AnnotationInForce> Annotations)
{
    /// <summary>No assertion either way: never a negative, and what a vehicle is when nothing is held for it.</summary>
    public static SupervisionInForce Unlabelled { get; } = new(SupervisionState.Unlabelled, []);

    /// <summary>
    /// Why this is not a state a vehicle can be in, or <see langword="null"/> where it is: unlabelled
    /// names nothing, annotated names at least one instance, and every instance is named.
    /// </summary>
    public string? Problem()
    {
        if (!Enum.IsDefined(State))
        {
            return $"{State} is not a supervision state: a vehicle is annotated, nominal or unlabelled.";
        }

        if (State == SupervisionState.Unlabelled && Annotations.Count > 0)
        {
            return "An unlabelled vehicle carries no annotation: there is nothing to name.";
        }

        if (State == SupervisionState.Annotated && Annotations.Count == 0)
        {
            return "An annotated vehicle names the instance it executes.";
        }

        return Annotations.Any(annotation => string.IsNullOrEmpty(annotation.InstanceId))
            ? "Every annotation names its instance."
            : null;
    }

    /// <inheritdoc/>
    public bool Equals(SupervisionInForce? other) =>
        other is not null && State == other.State && Annotations.SequenceEqual(other.Annotations);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(State, Annotations.Count);
}
