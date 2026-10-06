using CarlaNet.Types.Illumination;

namespace CarlaNet.Types.Supervision;

/// <summary>One family of the closed core: its name and its terms, in their published order.</summary>
/// <param name="Family">The family's name, as the published vocabulary keys it.</param>
/// <param name="Terms">Every term of the family, each spelled as every record writes it.</param>
public sealed record CoreTermFamily(string Family, IReadOnlyList<string> Terms);

/// <summary>
/// The annotation vocabulary's closed core: the terms this pipeline's own code branches on, generated
/// from the enumerations that code switches on.
/// </summary>
/// <remarks>
/// <para><b>One test decides membership</b> (<c>06_Truth_And_Annotation.md</c> §3.7, D6.27): does the
/// pipeline's own code branch on the term? If it does, a misspelling makes the pipeline behave
/// differently, so the term is closed, versioned and published here; every other term is an author's,
/// declared in the scenario and carried opaquely.</para>
///
/// <para><b>Generated, never copied</b> (D6.30). The scenario compiler publishes this table into every
/// supervision plan through <c>carlanet</c> (<c>carlacontrol.AnnotationVocabulary</c>), so a value added
/// to one of these enumerations reaches every vocabulary compiled after it without anyone restating it.
/// Adding a value bumps <see cref="Version"/>; removing or renaming one bumps it and breaks every
/// consumer that read the old spelling, which is why no rename is offered.</para>
///
/// <para><b>Spelling.</b> A term is its enumeration member's name in lower snake case --
/// <see cref="ClosedBy.EntityArrived"/> is <c>entity_arrived</c> -- except the periodic cadence, whose
/// published spelling names its three fields, and the illumination bands, which are their own table's
/// (<see cref="IlluminationBands"/>).</para>
/// </remarks>
public static class CoreVocabulary
{
    /// <summary>
    /// The core's version. 2 added the interval anchor, render state and render reason families and
    /// generates the core from here. 3 removed the <c>realisation</c>, <c>observability_outcome</c> and
    /// <c>reserved_phase</c> families, the subject kind <c>slot</c> and the close <c>slot_unrealised</c>:
    /// every label follows a vehicle, and the core carries no outcome that rests on a pass mark (06 §3.5,
    /// the owner's rulings of 2026-10-05).
    /// </summary>
    public const int Version = 3;

    /// <summary>Where the core is defined.</summary>
    public const string Source = "06_Truth_And_Annotation.md §3.7";

    /// <summary>The published spelling of <see cref="CadenceForm.Periodic"/>: the fields it is given by.</summary>
    public const string PeriodicCadence = "period_s + offsets_s[] + span";

    /// <summary>Every family, in the order §3.7 tabulates them, then those added since.</summary>
    public static IReadOnlyList<CoreTermFamily> Families { get; } =
    [
        Family<SupervisionState>("supervision_state"),
        Family<SubjectKind>("subject_kind"),
        Family<IntervalOnset>("interval_onset"),
        Family<ClosedBy>("closed_by"),
        new("illumination_band", IlluminationBands.Names),
        Family<CadenceForm>("cadence"),
        Family<ReservedRole>("reserved_role"),
        Family<AnchorEvent>("interval_anchor"),
        Family<RenderState>("render_state"),
        Family<RenderReason>("render_reason"),
    ];

    /// <summary>The role a one-participant instance names its participant.</summary>
    public static string SubjectRole { get; } = Name(ReservedRole.Subject);

    /// <summary>A supervision state's name.</summary>
    public static string Name(SupervisionState value) => Snake(value);

    /// <summary>A subject kind's name.</summary>
    public static string Name(SubjectKind value) => Snake(value);

    /// <summary>An interval onset's name.</summary>
    public static string Name(IntervalOnset value) => Snake(value);

    /// <summary>A <c>closed_by</c> value's name.</summary>
    public static string Name(ClosedBy value) => Snake(value);

    /// <summary>A cadence form's name.</summary>
    public static string Name(CadenceForm value) =>
        value == CadenceForm.Periodic ? PeriodicCadence : Snake(value);

    /// <summary>The reserved role's name.</summary>
    public static string Name(ReservedRole value) => Snake(value);

    /// <summary>An anchor event's name, without the index a stop or a phase carries.</summary>
    public static string Name(AnchorEvent value) => Snake(value);

    /// <summary>A render state's name.</summary>
    public static string Name(RenderState value) => Snake(value);

    /// <summary>A render reason's name.</summary>
    public static string Name(RenderReason value) => Snake(value);

    private static CoreTermFamily Family<TEnum>(string family) where TEnum : struct, Enum =>
        new(family, [.. Enum.GetValues<TEnum>().Select(value => value switch
        {
            CadenceForm cadence => Name(cadence),
            _ => Snake(value),
        })]);

    /// <summary>A member's name in lower snake case: <c>EntityArrived</c> is <c>entity_arrived</c>.</summary>
    private static string Snake<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        string name = value.ToString();
        var spelled = new System.Text.StringBuilder(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char letter = name[index];
            if (char.IsUpper(letter) && index > 0)
            {
                spelled.Append('_');
            }

            spelled.Append(char.ToLowerInvariant(letter));
        }

        return spelled.ToString();
    }
}
