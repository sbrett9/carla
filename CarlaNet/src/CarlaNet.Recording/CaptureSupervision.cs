using System.Globalization;
using System.Xml;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;

namespace CarlaNet.Recording;

/// <summary>
/// What a truth sidecar says of the supervision in force on its capture's frame.
/// </summary>
public enum SidecarSupervision
{
    /// <summary>
    /// No supervision plan was in force on the frame: the sidecar carries no supervision and says
    /// nothing extra, exactly as before supervision existed -- traffic-manager traffic, or a drive
    /// that bound no plan.
    /// </summary>
    NotInForce,

    /// <summary>
    /// A plan was in force and the frame's supervision is written: the plan and vocabulary on the
    /// container, the world-scoped supervision with its absences, and every drawn SUMO vehicle's state.
    /// </summary>
    InForce,

    /// <summary>
    /// A plan was in force, and the supervision of the capture's own frame is not to be had: the client
    /// no longer held the frame's snapshot, or could not read its block. Nothing is written in its
    /// place -- a neighbouring frame's would be a guess -- and the container says
    /// <c>supervision="unknown"</c>, so no vehicle's missing state reads as an omission.
    /// </summary>
    Unknown,
}

/// <summary>
/// The supervision a capture's sidecar is written with: whether it was in force, unknown or neither,
/// and where it was in force, what the server carried on the capture's own frame.
/// </summary>
/// <remarks>
/// <para><b>From the server, never from this process.</b> The supervision in force is held on the CARLA
/// server and carried on every world-observer snapshot (doc 06 D6.41), so a recorder in any process
/// writes the same thing for one frame. It is read from the snapshot of the capture's own frame, in the
/// same read as the frame's vehicles and their render set, so a body's supervision is always the one it
/// carried for the vehicle it drew on that frame.</para>
///
/// <para><b>The capture's own frame, or unknown.</b> Where the client no longer held the image's frame,
/// its vehicles are read from the nearest frame it did hold, and the sidecar names that frame in
/// <c>telemetry_tick</c>; the supervision is not, because an interval can open or close between two
/// frames and a body can change hands, so the neighbour's would assert something of this image nobody
/// asserted.</para>
/// </remarks>
public sealed class CaptureSupervision
{
    private CaptureSupervision(SidecarSupervision state, ObservedSupervision observed)
    {
        State = state;
        Observed = observed;
    }

    /// <summary>No plan was in force on the frame: nothing is written.</summary>
    public static CaptureSupervision NotInForce { get; } = new(SidecarSupervision.NotInForce, ObservedSupervision.None);

    /// <summary>A plan was in force and the frame's supervision is not to be had.</summary>
    public static CaptureSupervision Unknown { get; } = new(SidecarSupervision.Unknown, ObservedSupervision.None);

    /// <summary>Which of the three the sidecar is.</summary>
    public SidecarSupervision State { get; }

    /// <summary>What the frame's snapshot carried, where the supervision is in force.</summary>
    public ObservedSupervision Observed { get; }

    /// <summary>
    /// The supervision a capture's sidecar is written with, from the snapshot its vehicles were read
    /// from.
    /// </summary>
    /// <param name="captureFrame">The frame the capture's image was rendered on.</param>
    /// <param name="servedFrame">
    /// The frame whose snapshot was read, or null where no snapshot was held at all.
    /// </param>
    /// <param name="served">The supervision that snapshot carried.</param>
    /// <param name="newest">
    /// The supervision of the newest snapshot, which says whether a plan was in force where no snapshot
    /// of the capture's frame was held.
    /// </param>
    public static CaptureSupervision For(ulong captureFrame, ulong? servedFrame, ObservedSupervision served,
                                         ObservedSupervision newest)
    {
        ArgumentNullException.ThrowIfNull(served);
        ArgumentNullException.ThrowIfNull(newest);
        if (servedFrame == captureFrame)
        {
            return served.IsCarried ? new CaptureSupervision(SidecarSupervision.InForce, served)
                : served.IsUnreadable ? Unknown
                : NotInForce;
        }

        // Not the capture's own frame: unknown where a plan was in force around it, and otherwise none.
        ObservedSupervision around = servedFrame is null ? newest : served;
        return around.IsCarried || around.IsUnreadable ? Unknown : NotInForce;
    }

    /// <summary>
    /// The container's attributes: the plan, the vocabulary's version and digest where the supervision is
    /// in force; <c>supervision="unknown"</c> where it is unknown; nothing where no plan was in force.
    /// </summary>
    internal void WriteContainerAttributes(XmlWriter w)
    {
        if (State == SidecarSupervision.Unknown)
        {
            w.WriteAttributeString("supervision", "unknown");
            return;
        }

        if (State == SidecarSupervision.InForce && Observed.Plan is { } plan)
        {
            w.WriteAttributeString("plan_id", plan.PlanId);
            WriteVocabulary(w, plan);
        }
    }

    /// <summary>
    /// The world-scoped supervision, where it is in force: one <c>&lt;absence&gt;</c> per absence in force,
    /// none where there is none, so an empty element says no absence was in force rather than nothing
    /// being known. An absence is never a CoT event, because there is no vehicle (doc 06 D6.6).
    /// </summary>
    internal void WriteWorld(XmlWriter w)
    {
        if (State != SidecarSupervision.InForce || Observed.Plan is not { } plan)
        {
            return;
        }

        w.WriteStartElement("_supervision");
        w.WriteAttributeString("scope", "world");
        WriteVocabulary(w, plan);
        foreach (AbsenceInForce absence in Observed.Absences)
        {
            w.WriteStartElement("absence");
            w.WriteAttributeString("instance", absence.InstanceId);
            WriteList(w, "labels", absence.Labels);
            WriteList(w, "areas", absence.Areas);
            if (absence.Phase.Length > 0)
            {
                w.WriteAttributeString("phase", absence.Phase);
            }

            w.WriteEndElement(); // absence
        }

        w.WriteEndElement(); // _supervision
    }

    /// <summary>
    /// A drawn SUMO vehicle's supervision, where it is in force: its state, always, <c>unlabelled</c>
    /// included, and one <c>&lt;annotation&gt;</c> per pattern instance in force for it -- which a nominal
    /// vehicle may carry as an annotated one does (doc 06 D6.31), and an unlabelled one never does.
    /// </summary>
    /// <param name="w">The writer, inside the vehicle's <c>&lt;detail&gt;</c>.</param>
    /// <param name="actorId">The body that drew the vehicle on the frame.</param>
    internal void WriteVehicle(XmlWriter w, uint actorId)
    {
        if (State != SidecarSupervision.InForce || Observed.Plan is not { } plan
            || Observed.Of(actorId) is not { } supervision)
        {
            return;
        }

        w.WriteStartElement("_supervision");
        w.WriteAttributeString("state", CoreVocabulary.Name(supervision.State));
        WriteVocabulary(w, plan);
        foreach (AnnotationInForce annotation in supervision.Annotations)
        {
            w.WriteStartElement("annotation");
            w.WriteAttributeString("instance", annotation.InstanceId);
            WriteList(w, "labels", annotation.Labels);
            if (annotation.Phase.Length > 0)
            {
                w.WriteAttributeString("phase", annotation.Phase);
            }

            if (annotation.Role.Length > 0)
            {
                w.WriteAttributeString("role", annotation.Role);
            }

            w.WriteEndElement(); // annotation
        }

        w.WriteEndElement(); // _supervision
    }

    /// <summary>
    /// The vocabulary's core version and the plan's digest over its resolved terms, which pin what every
    /// label beside them means (doc 06 D6.30).
    /// </summary>
    private static void WriteVocabulary(XmlWriter w, SupervisionPlanIdentity plan)
    {
        w.WriteAttributeString("vocabulary", plan.VocabularyVersion.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("vocabulary_digest", plan.VocabularyDigest);
    }

    /// <summary>
    /// A set of terms or areas, space-separated: neither grammar admits a space, a term being
    /// <c>namespace:name</c> in lower snake case and an area id lower snake case.
    /// </summary>
    private static void WriteList(XmlWriter w, string name, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            w.WriteAttributeString(name, string.Join(' ', values));
        }
    }
}
