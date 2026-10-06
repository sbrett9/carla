// The annotation vocabulary's closed core of Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/
// 06_Truth_And_Annotation.md §3.7, pinned term by term. This table is the only definition in the tree:
// the scenario compiler publishes it into every supervision plan through carlanet
// (CarlaControl/test/test_scenario_compiler.py pins the same families there), and the world truth track
// writes its render state and reasons from it.
using CarlaNet.Types.Illumination;
using CarlaNet.Types.Supervision;

namespace CarlaNet.Tests.Supervision;

public class CoreVocabularyTests
{
    [Fact]
    public void The_Core_Is_Version_3_And_Names_Its_Source()
    {
        Assert.Equal(3, CoreVocabulary.Version);
        Assert.Equal("06_Truth_And_Annotation.md §3.7", CoreVocabulary.Source);
    }

    [Fact]
    public void Every_Family_Is_Published_With_Its_Terms_In_Order()
    {
        Dictionary<string, IReadOnlyList<string>> families =
            CoreVocabulary.Families.ToDictionary(family => family.Family, family => family.Terms);
        Assert.Equal(["supervision_state", "subject_kind", "interval_onset", "closed_by", "illumination_band",
                      "cadence", "reserved_role", "interval_anchor", "render_state", "render_reason"],
                     CoreVocabulary.Families.Select(family => family.Family));
        Assert.Equal(["annotated", "nominal", "unlabelled"], families["supervision_state"]);
        Assert.Equal(["entity", "cohort"], families["subject_kind"]);
        Assert.Equal(["declared", "committed", "observed"], families["interval_onset"]);
        Assert.Equal(["trigger", "entity_arrived", "sumo_removed", "never_inserted",
                      "physical_predicate_never_held", "render_released", "capture_window_end",
                      "scenario_end"], families["closed_by"]);
        Assert.Equal(IlluminationBands.Names, families["illumination_band"]);
        Assert.Equal(["enumerated", "period_s + offsets_s[] + span"], families["cadence"]);
        Assert.Equal(["subject"], families["reserved_role"]);
        Assert.Equal(["depart", "stop", "stop_end", "phase"], families["interval_anchor"]);
        Assert.Equal(["rendered", "simulated_only"], families["render_state"]);
        Assert.Equal(["no_world", "left_the_simulation", "vanished", "outside_limit", "no_blueprint",
                      "unknown_extent", "no_ground", "not_drawn"], families["render_reason"]);
    }

    [Fact]
    public void The_Absence_Shape_Is_Gone_From_The_Core()
    {
        // The owner's ruling of 2026-10-05 (06 §3.5): a label follows a vehicle, so the core publishes no
        // place-shaped subject, no realisation, no reserved phase for an empty occasion, no close for an
        // occasion no vehicle came to, and no outcome that rests on a pass mark.
        Assert.DoesNotContain("realisation", CoreVocabulary.Families.Select(family => family.Family));
        Assert.DoesNotContain("observability_outcome", CoreVocabulary.Families.Select(family => family.Family));
        Assert.DoesNotContain("reserved_phase", CoreVocabulary.Families.Select(family => family.Family));
        Assert.DoesNotContain("slot", CoreVocabulary.Families.Single(f => f.Family == "subject_kind").Terms);
        Assert.DoesNotContain("slot_unrealised", CoreVocabulary.Families.Single(f => f.Family == "closed_by").Terms);
        Assert.DoesNotContain("vacancy", CoreVocabulary.Families.SelectMany(family => family.Terms));
    }

    [Fact]
    public void A_Term_Is_Its_Member_s_Name_And_The_Reserved_Word_Is_The_Core_s()
    {
        Assert.Equal("physical_predicate_never_held", CoreVocabulary.Name(ClosedBy.PhysicalPredicateNeverHeld));
        Assert.Equal("left_the_simulation", CoreVocabulary.Name(RenderReason.LeftTheSimulation));
        Assert.Equal("stop_end", CoreVocabulary.Name(AnchorEvent.StopEnd));
        Assert.Equal("period_s + offsets_s[] + span", CoreVocabulary.Name(CadenceForm.Periodic));
        Assert.Equal("subject", CoreVocabulary.SubjectRole);
        // Every family holds one term per member, so no member can go unpublished.
        Assert.Equal(Enum.GetValues<ClosedBy>().Length,
                     CoreVocabulary.Families.Single(f => f.Family == "closed_by").Terms.Count);
        Assert.Equal(Enum.GetValues<RenderReason>().Length,
                     CoreVocabulary.Families.Single(f => f.Family == "render_reason").Terms.Count);
    }

    [Fact]
    public void No_Term_Is_Spelled_Twice_Within_A_Family()
    {
        Assert.All(CoreVocabulary.Families,
                   family => Assert.Equal(family.Terms.Count, family.Terms.Distinct().Count()));
    }
}
