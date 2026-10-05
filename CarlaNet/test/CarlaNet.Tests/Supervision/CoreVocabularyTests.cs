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
    public void The_Core_Is_Version_2_And_Names_Its_Source()
    {
        Assert.Equal(2, CoreVocabulary.Version);
        Assert.Equal("06_Truth_And_Annotation.md §3.7", CoreVocabulary.Source);
    }

    [Fact]
    public void Every_Family_Is_Published_With_Its_Terms_In_Order()
    {
        Dictionary<string, IReadOnlyList<string>> families =
            CoreVocabulary.Families.ToDictionary(family => family.Family, family => family.Terms);
        Assert.Equal(["supervision_state", "subject_kind", "realisation", "interval_onset", "closed_by",
                      "observability_outcome", "illumination_band", "cadence", "reserved_role",
                      "reserved_phase", "interval_anchor", "render_state", "render_reason"],
                     CoreVocabulary.Families.Select(family => family.Family));
        Assert.Equal(["annotated", "nominal", "unlabelled"], families["supervision_state"]);
        Assert.Equal(["entity", "cohort", "slot"], families["subject_kind"]);
        Assert.Equal(["present", "absent"], families["realisation"]);
        Assert.Equal(["declared", "committed", "observed"], families["interval_onset"]);
        Assert.Equal(["trigger", "entity_arrived", "sumo_removed", "never_inserted", "slot_unrealised",
                      "physical_predicate_never_held", "render_released", "capture_window_end",
                      "scenario_end"], families["closed_by"]);
        Assert.Equal(["observed", "out_of_frame", "occluded", "not_rendered", "site_unobserved",
                      "beyond_draw_distance"], families["observability_outcome"]);
        Assert.Equal(IlluminationBands.Names, families["illumination_band"]);
        Assert.Equal(["enumerated", "period_s + offsets_s[] + span"], families["cadence"]);
        Assert.Equal(["subject"], families["reserved_role"]);
        Assert.Equal(["vacancy"], families["reserved_phase"]);
        Assert.Equal(["depart", "stop", "stop_end", "phase"], families["interval_anchor"]);
        Assert.Equal(["rendered", "simulated_only"], families["render_state"]);
        Assert.Equal(["no_world", "left_the_simulation", "vanished", "outside_limit", "no_blueprint",
                      "unknown_extent", "no_ground", "not_drawn"], families["render_reason"]);
    }

    [Fact]
    public void A_Term_Is_Its_Member_s_Name_And_The_Reserved_Words_Are_The_Core_s()
    {
        Assert.Equal("physical_predicate_never_held", CoreVocabulary.Name(ClosedBy.PhysicalPredicateNeverHeld));
        Assert.Equal("beyond_draw_distance", CoreVocabulary.Name(ObservabilityOutcome.BeyondDrawDistance));
        Assert.Equal("stop_end", CoreVocabulary.Name(AnchorEvent.StopEnd));
        Assert.Equal("period_s + offsets_s[] + span", CoreVocabulary.Name(CadenceForm.Periodic));
        Assert.Equal("subject", CoreVocabulary.SubjectRole);
        Assert.Equal("vacancy", CoreVocabulary.VacancyPhase);
        // Every family holds one term per member, so no member can go unpublished.
        Assert.Equal(Enum.GetValues<ObservabilityOutcome>().Length,
                     CoreVocabulary.Families.Single(f => f.Family == "observability_outcome").Terms.Count);
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
