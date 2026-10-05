using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarlaNet.Types.Supervision;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A compiled supervision plan is read into records nothing can write, with every core value read
/// through the core's enumerations, and a plan this reader cannot bind is refused whole, naming each
/// problem.
/// </summary>
public sealed class SupervisionPlanTests
{
    private const string Bahonar = "Shahid_Bahonar_Port_PatternOfLife";
    private const string Arapahoe = "Arapahoe_I25_UnderpassDwell";
    private const string Gardnerville = "Gardnerville_Centerville_Lane_NeighborhoodOrbit";

    private readonly ITestOutputHelper _output;

    public SupervisionPlanTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TheShippedBahonarPlanExposesItsInstancesAnchoredIntervalsSeriesAndAbsence()
    {
        SupervisionPlan plan = SupervisionPlan.Read(ShippedPlan(Bahonar));
        ScenarioLock locked = ScenarioLock.Read(ShippedFile(Bahonar, ".lock.json"));

        _output.WriteLine(plan.ToString());
        Assert.Equal(Bahonar, plan.PlanId);
        Assert.Equal(Bahonar, plan.ScenarioId);
        Assert.Equal(locked.Supervision!.Sha256, plan.Sha256);
        Assert.Equal(locked.Routes.Sha256, plan.RoutesDigest);
        Assert.Equal(locked.Config.Sha256, plan.ConfigDigest);
        Assert.Equal(locked.WorldNetworkFingerprint, plan.NetworkDigest);
        Assert.Null(plan.AdditionalDigest);

        Assert.Equal(27, plan.Instances.Length);
        Assert.Equal(5, plan.Instances.Count(instance => instance.Supervision == SupervisionState.Annotated
                                                         && instance.Realisation == Realisation.Present));
        Assert.Equal(21, plan.Instances.Count(instance => instance.Supervision == SupervisionState.Nominal));

        // The escort: five participants, departure-anchored, the group's parameters as authored.
        PatternInstance escort = Instance(plan, "pi_escort_drydock_d3");
        Assert.Equal<string>(["bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"], escort.Labels);
        Assert.Equal(5, escort.Parameters["group_size"].GetInt32());
        Assert.Equal(16, escort.Parameters["departure_spread_s"].GetInt32());
        Assert.Equal(("term", "bahonar:routine_freight_haul"), (escort.Counterfactual!.Kind, escort.Counterfactual.Ref));
        Assert.Equal<string>(["drydock"], escort.AoiRefs);
        Assert.Equal<string>(["bahonar:lead", "bahonar:follower", "bahonar:follower", "bahonar:follower", "bahonar:follower"],
                     escort.Participants.Select(participant => participant.Role));
        Assert.All(escort.Intervals.Select((interval, index) => (interval, index)), pair =>
        {
            Assert.Equal($"escort_{pair.index}", pair.interval.EntityId);
            Assert.Equal(AnchorEvent.Depart, pair.interval.Anchor!.Start.Event);
            Assert.Null(pair.interval.Anchor.Start.Index);
            Assert.Null(pair.interval.Anchor.End);
            Assert.Equal(270000.0 + (4 * pair.index), pair.interval.DeclaredStartSeconds);
        });

        // The gate probe: a duration stop declares its length and no start (06 D6.4).
        PlannedInterval standoff = Assert.Single(Instance(plan, "pi_gate_probe_d2").Intervals);
        Assert.Equal("standoff", standoff.Phase);
        Assert.Equal((AnchorEvent.Stop, 0, "-431672573#2_0", 30.0),
                     (standoff.Anchor!.Start.Event, standoff.Anchor.Start.Index, standoff.Anchor.Start.Lane,
                      standoff.Anchor.Start.EndPositionMetres));
        Assert.Equal((AnchorEvent.StopEnd, 0, "stop_end:0"),
                     (standoff.Anchor.End!.Event, standoff.Anchor.End.Index, standoff.Anchor.End.Spelled));
        Assert.Null(standoff.DeclaredStartSeconds);
        Assert.Null(standoff.DeclaredEndSeconds);
        Assert.Equal(300.0, standoff.DeclaredDurationSeconds);
        Assert.Equal(300, Instance(plan, "pi_gate_probe_d2").Parameters["dwell_s"].GetInt32());

        // The stay-behind: an until stop, whose end declares the run's end and whose start declares none.
        PlannedInterval dwell = Assert.Single(Instance(plan, "pi_ferry_stay_behind_d1").Intervals);
        Assert.Equal((AnchorEvent.Stop, AnchorEvent.StopEnd), (dwell.Anchor!.Start.Event, dwell.Anchor.End!.Event));
        Assert.Null(dwell.DeclaredStartSeconds);
        Assert.Equal(604800.0, dwell.DeclaredEndSeconds);
        Assert.Null(dwell.DeclaredDurationSeconds);

        // A haul: nominal, carrying its term's matched negatives (06 §3.9(d)).
        PatternInstance haul = Instance(plan, "haul_d0_0");
        Assert.Equal(SupervisionState.Nominal, haul.Supervision);
        Assert.Equal<string>(["bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"], haul.HardNegativeFor!);
        Assert.Equal(CoreVocabulary.SubjectRole, Assert.Single(haul.Participants).Role);
        Assert.Empty(haul.Intervals);
        Assert.Null(Instance(plan, "pi_gate_probe_d2").HardNegativeFor);

        // The no-show: an absence over the series' one unrealised slot, sited at a lane position.
        PatternInstance absence = Assert.Single(plan.Instances, instance => instance.Realisation == Realisation.Absent);
        Assert.Equal($"{Bahonar}/pi_tower_relief_d4_h7_t3_unmanned", absence.InstanceId);
        Assert.Equal(SupervisionState.Annotated, absence.Supervision);
        Assert.Equal<string>(["bahonar:post_unmanned"], absence.Labels);
        Assert.Empty(absence.Participants);
        Assert.Equal(("tower_relief", "guard_d4_h7_t3"), (absence.SeriesRef, absence.SlotRef));
        Assert.Equal<string>(["tower_03"], absence.AoiRefs);
        AbsenceExpectation expected = absence.Expected!;
        Assert.Equal(("bahonar:guard", "guard_d4_h7_t3"), (expected.Role, expected.ExpectedEntityId));
        Assert.Equal(("26413459_0", 58.9), (expected.SiteLane, expected.SitePositionMetres));
        Assert.Equal(("26413425#5", "26413425#5"), (expected.Route!.From, expected.Route.To));
        Assert.Equal<string>(["26413459"], expected.Route.Via);
        Assert.Equal((345600.0, 374400.0), (expected.DeclaredStartSeconds, expected.DeclaredEndSeconds));
        PlannedInterval vacancy = Assert.Single(absence.Intervals);
        Assert.Equal((null, CoreVocabulary.VacancyPhase, null), (vacancy.EntityId, vacancy.Phase, vacancy.Anchor));
        Assert.Equal((345600.0, 374400.0, 28800.0),
                     (vacancy.DeclaredStartSeconds, vacancy.DeclaredEndSeconds, vacancy.DeclaredDurationSeconds));
        Assert.Equal((336, 335), (absence.CounterEvidence!.SeriesSlotsTotal, absence.CounterEvidence.SeriesSlotsRealised));

        // The guard rota as a series: 336 slots, the absence's the one left unrealised.
        RecurringSeries relief = Assert.Single(plan.Series);
        Assert.Equal(("tower_relief", "guard_posting", CadenceForm.Enumerated, "bahonar:guard", SupervisionState.Nominal),
                     (relief.SeriesId, relief.RotaRef, relief.Cadence, relief.MemberRole, relief.Supervision));
        Assert.Equal<string>(["bahonar:tower_posting"], relief.Labels);
        Assert.Equal<string>(["bahonar:standoff_dwell_at_access_point", "bahonar:arrival_without_departure"],
                     relief.HardNegativeFor!);
        Assert.Equal(336, relief.Slots.Length);
        SeriesSlot unrealised = Assert.Single(relief.Slots, slot => !slot.Realised);
        Assert.Equal(("guard_d4_h7_t3", "tower_03", 345600.0, 374400.0),
                     (unrealised.SlotKey, unrealised.AoiRef, unrealised.DeclaredStartSeconds, unrealised.DeclaredEndSeconds));
        Assert.Equal(("guard_d0_h7_t0", "guard_d0_h7_t0"), (relief.Slots[0].SlotKey, relief.Slots[0].RealisedBy));

        // Every flow and every authored vehicle, explicitly.
        Assert.Equal(248, plan.Cohorts.Length);
        Assert.Equal(98, plan.Cohorts.Count(cohort => cohort.Supervision == SupervisionState.Annotated));
        Assert.Equal<string>(["bahonar:cleared_gate_transit"],
                     plan.Cohorts.First(cohort => cohort.Supervision == SupervisionState.Annotated).Labels);
        Assert.Equal(365, plan.Entities.Length);
        EntitySupervision lead = plan.Entities.Single(entity => entity.EntityId == "escort_0");
        Assert.Equal<SupervisionState>([SupervisionState.Annotated], lead.Supervision);
        Assert.Equal<string>([escort.InstanceId], lead.Refs);
        EntitySupervision guard = plan.Entities.Single(entity => entity.EntityId == "guard_d0_h15_t0");
        Assert.Equal<SupervisionState>([SupervisionState.Nominal], guard.Supervision);
        Assert.Equal<string>(["series:tower_relief"], guard.Refs);

        Assert.StartsWith($"{Bahonar}: 27 instances (5 annotated, 21 nominal, 1 absent), 1 series of 336 slots "
                          + "(1 unrealised), 248 cohorts (98 annotated), 365 entities; vocabulary core 2, bahonar 1, "
                          + "digest e3571085", plan.ToString());
    }

    [Fact]
    public void TheShippedBahonarVocabularyIsReadWithEveryDeclaration()
    {
        PlanVocabulary vocabulary = SupervisionPlan.Read(ShippedPlan(Bahonar)).Vocabulary;

        Assert.Equal(CoreVocabulary.Version, vocabulary.CoreVersion);
        Assert.Equal(CoreVocabulary.Source, vocabulary.CoreSource);
        Assert.Equal("e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad", vocabulary.Digest);
        AuthorNamespace bahonar = Assert.Single(vocabulary.Namespaces);
        Assert.Equal(("bahonar", 1), (bahonar.Namespace, bahonar.Version));
        Assert.StartsWith("Shahid Bahonar Port pattern of life", bahonar.Authority);
        Assert.Equal(10, bahonar.Terms.Length);
        Assert.Equal<string>(["bahonar:lead", "bahonar:follower", "bahonar:guard"], bahonar.Roles.Select(role => role.Role));
        Assert.Equal<string>(["bahonar:guard_post", "bahonar:gate", "bahonar:drydock", "bahonar:ferry_terminal"],
                     bahonar.AreaKinds.Select(kind => kind.Kind));

        AuthorTerm unmanned = Term(bahonar, "bahonar:post_unmanned");
        Assert.Equal("bahonar:expected_arrival_absent", unmanned.Broader);
        Assert.Equal<SubjectKind>([SubjectKind.Slot], unmanned.AppliesTo);
        Assert.Equal<Realisation>([Realisation.Absent], unmanned.Realisation);
        Assert.Equal(("series", "tower_relief"), (unmanned.Counterfactual!.Kind, unmanned.Counterfactual.Ref));
        Assert.Equal(("active", 1), (unmanned.Status, unmanned.Since));
        Assert.Null(unmanned.SupersededBy);

        AuthorTerm group = Term(bahonar, "bahonar:coordinated_group_transit");
        Assert.Equal<string>(["departure_spread_s", "group_size"], group.Parameters.Keys);
        Assert.Equal(("integer", null, "vehicles in the group"),
                     (group.Parameters["group_size"].Type, group.Parameters["group_size"].Unit,
                      group.Parameters["group_size"].Definition));
        Assert.Equal(("number", "s"), (group.Parameters["departure_spread_s"].Type, group.Parameters["departure_spread_s"].Unit));

        Assert.Equal<string>(["bahonar:cleared_gate_transit"], Term(bahonar, "bahonar:standoff_dwell_at_access_point").ContrastWith);
        Assert.Equal<string>(["bahonar:standoff_dwell_at_access_point", "bahonar:arrival_without_departure"],
                     Term(bahonar, "bahonar:tower_posting").HardNegativeFor);
        Assert.Equal<SubjectKind>([SubjectKind.Cohort], Term(bahonar, "bahonar:cleared_gate_transit").AppliesTo);
        Assert.Empty(Term(bahonar, "bahonar:destination_off_pattern").HardNegativeFor);
    }

    [Theory]
    [InlineData(Arapahoe, 51, "marked", true)]
    [InlineData(Gardnerville, 30, "orbiter", false)]
    public void ThePlansThatAssertNothingSayEveryFlowAndVehicleIsUnlabelled(string scenario, int flows,
                                                                            string vehicle, bool closesLanes)
    {
        SupervisionPlan plan = SupervisionPlan.Read(ShippedPlan(scenario));
        ScenarioLock locked = ScenarioLock.Read(ShippedFile(scenario, ".lock.json"));

        _output.WriteLine(plan.ToString());
        Assert.Equal(scenario, plan.PlanId);
        Assert.Empty(plan.Instances);
        Assert.Empty(plan.Series);
        Assert.Empty(plan.Vocabulary.Namespaces);
        Assert.Equal(flows, plan.Cohorts.Length);
        Assert.All(plan.Cohorts, cohort =>
        {
            Assert.Equal(SupervisionState.Unlabelled, cohort.Supervision);
            Assert.Empty(cohort.Labels);
            Assert.Empty(cohort.Parameters);
        });
        EntitySupervision entity = Assert.Single(plan.Entities);
        Assert.Equal((vehicle, SupervisionState.Unlabelled), (entity.EntityId, Assert.Single(entity.Supervision)));
        Assert.Empty(entity.Refs);
        Assert.Equal(closesLanes ? locked.Additional!.Sha256 : null, plan.AdditionalDigest);
        Assert.Equal(locked.VocabularyDigest, plan.Vocabulary.Digest);
    }

    [Fact]
    public void EveryTypeAPlanReachesIsARecordNothingCanWriteOrMake()
    {
        // 06 D6.8 in the type graph: the runtime may only bind rows, so no row can be made, changed or
        // added to outside a reading of the plan. Walked from the plan, so a type added later is held
        // to it without anyone listing it here.
        List<Type> types = PlanTypes();
        _output.WriteLine(string.Join(", ", types.Select(type => type.Name)));
        Assert.Contains(typeof(AnchorPoint), types);
        Assert.Contains(typeof(TermParameter), types);
        Assert.Contains(typeof(AbsenceCounterEvidence), types);

        foreach (Type type in types)
        {
            Assert.True(type.IsSealed, $"{type.Name} is not sealed");
            Assert.True(type.GetMethod("<Clone>$") is not null, $"{type.Name} is not a record");

            // Made only by reading a plan: every constructor is private, the record's copy included.
            Assert.All(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                       constructor => Assert.True(constructor.IsPrivate, $"{type.Name} has a non-private constructor"));

            // No setter or init of any visibility, and no field anyone outside can reach.
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public
                                                                 | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Assert.True(property.SetMethod is null, $"{type.Name}.{property.Name} has a setter");
            }

            Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Public));
            Assert.All(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                       field => Assert.True(field.IsInitOnly, $"{type.Name}.{field.Name} is not read-only"));

            // Every collection is immutable in its type, not merely read-only behind an interface.
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.True(Immutable(property.PropertyType), $"{type.Name}.{property.Name} is a "
                                                              + $"{property.PropertyType} that can be changed");
            }

            // No mutation API: no public method that returns nothing.
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                                  | BindingFlags.DeclaredOnly),
                                  method => method.ReturnType == typeof(void));
        }
    }

    [Theory]
    [InlineData("instances[0].supervision", "\"anomalous\"",
                "plan.instances[0].supervision is 'anomalous', which is no supervision_state of the core vocabulary at "
                + "version 2 (annotated, nominal, unlabelled)")]
    [InlineData("instances[26].realisation", "\"missing\"",
                "plan.instances[26].realisation is 'missing', which is no realisation of the core vocabulary at version 2 "
                + "(present, absent)")]
    [InlineData("series[0].cadence", "\"weekly\"",
                "plan.series[0].cadence is 'weekly', which is no cadence of the core vocabulary at version 2 (enumerated, "
                + "period_s + offsets_s[] + span)")]
    [InlineData("entities[0].supervision", "[\"negative\"]",
                "plan.entities[0].supervision[0] is 'negative', which is no supervision_state")]
    [InlineData("cohorts[0].supervision", "\"Annotated\"",
                "plan.cohorts[0].supervision is 'Annotated', which is no supervision_state")]
    [InlineData("instances[1].intervals[0].anchor.start.event", "\"stop_0\"",
                "plan.instances[1].intervals[0].anchor.start.event is 'stop_0', which is none of depart, stop:<i>, "
                + "stop_end:<i>, phase:<i>, the core's interval anchors")]
    [InlineData("instances[1].intervals[0].anchor.end.event", "\"stop_end:-1\"",
                "plan.instances[1].intervals[0].anchor.end.event is 'stop_end:-1', which is none of")]
    [InlineData("instances[0].intervals[0].anchor.start.event", "\"depart:0\"",
                "plan.instances[0].intervals[0].anchor.start.event is 'depart:0', which is none of")]
    [InlineData("vocabulary.namespaces[0].terms[0].applies_to", "[\"vehicle\"]",
                "vocabulary.namespaces[0].terms[0].applies_to[0] is 'vehicle', which is no subject_kind of the core "
                + "vocabulary at version 2 (entity, cohort, slot)")]
    public void ACoreValueOutsideTheCoreIsRefusedNamingItsFamilyAndEveryValue(string at, string value, string expected)
    {
        using var copy = new PlanCopy(Bahonar, plan => Set(plan, at, JsonNode.Parse(value)));

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SupervisionPlan.Read(copy.Path));

        _output.WriteLine(refused.Message);
        Assert.StartsWith($"The supervision plan {copy.Path} cannot be bound: (1) ", refused.Message);
        Assert.Contains(expected, refused.Message);
        Assert.Contains("Recompile the scenario", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
    }

    [Fact]
    public void AVocabularyThatDoesNotDigestAsThePlanSaysIsRefused()
    {
        // One definition reworded after the compile: every label still resolves, and means something
        // its digest does not say.
        using var copy = new PlanCopy(Bahonar, plan =>
            plan["vocabulary"]!["namespaces"]![0]!["terms"]![1]!["definition"] = "A guard tower left unmanned.");

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SupervisionPlan.Read(copy.Path));

        _output.WriteLine(refused.Message);
        Assert.Contains("(1) plan.vocabulary_digest is e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad, "
                        + "and the vocabulary the plan carries digests as ", refused.Message);
        Assert.DoesNotContain("(2)", refused.Message);
    }

    [Fact]
    public void TheVocabularyIsDigestedByTheCompilersOwnCanonicalForm()
    {
        // The document and its digest from Python's json.dumps(sort_keys=True, indent=2, ensure_ascii=False),
        // as carlacontrol.AnnotationVocabulary digests a vocabulary: keys by code point -- the key above the
        // BMP sorts after U+FFFD, where UTF-16 order would put it first -- escapes and control characters,
        // text beyond ASCII written as it is, empty containers, and a number as Python spells it. Built from
        // code points, so this file carries none of the characters it tests.
        static string Of(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));
        var document = new JsonObject
        {
            ["namespaces"] = new JsonArray
            {
                new JsonObject
                {
                    ["zeta"] = new JsonArray(),
                    ["alpha"] = new JsonObject(),
                    [Of(0xFFFD) + " key"] = "replacement",
                    [Of(0x1F600) + " key"] = "emoji",
                    ["terms"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["term"] = "x:y",
                            ["definition"] = "quote \" backslash \\ newline \n tab \t bell " + Of(0x07)
                                             + " nul-ish " + Of(0x1F),
                            ["since"] = 1,
                        },
                    },
                    ["text"] = Of(0xA7) + " " + Of(0x2014) + " " + Of(0xE9) + " " + Of(0xFC) + " "
                               + Of(0x6F22, 0x5B57) + " " + Of(0x2028) + " " + Of(0x7F),
                    ["number"] = 0.45,
                    ["flag"] = true,
                    ["nothing"] = null,
                },
            },
            ["core"] = new JsonObject
            {
                ["vocabulary_version"] = 2,
                ["terms"] = new JsonObject { ["b"] = new JsonArray("two", "one"), ["a"] = new JsonArray() },
            },
        };
        using JsonDocument parsed = JsonDocument.Parse(document.ToJsonString());

        Assert.Equal("50c30d80f48dd45f76ec3d1549760f76dd61644f28fcd0496669dc1c98296aee",
                     SupervisionPlan.VocabularyDigestOf(parsed.RootElement));
    }

    [Fact]
    public void APlanCompiledAgainstAnotherCoreIsRefused()
    {
        using var older = new PlanCopy(Gardnerville, plan =>
        {
            plan["vocabulary_version"] = 1;
            plan["vocabulary"]!["core"]!["vocabulary_version"] = 1;
        });
        string message = Assert.Throws<CoSimSessionRefusedException>(() => SupervisionPlan.Read(older.Path)).Message;
        _output.WriteLine(message);
        Assert.Contains("vocabulary.core.vocabulary_version is 1, and this session branches on the core at version 2",
                        message);

        // The same version with a value gone: a core this session does not branch on.
        using var edited = new PlanCopy(Gardnerville, plan =>
            plan["vocabulary"]!["core"]!["terms"]!["observability_outcome"]!.AsArray().RemoveAt(5));
        message = Assert.Throws<CoSimSessionRefusedException>(() => SupervisionPlan.Read(edited.Path)).Message;
        _output.WriteLine(message);
        Assert.Contains("vocabulary.core.terms.observability_outcome is [observed, out_of_frame, occluded, not_rendered, "
                        + "site_unobserved], and the core at version 2 publishes [observed, out_of_frame, occluded, "
                        + "not_rendered, site_unobserved, beyond_draw_distance]", message);
    }

    [Fact]
    public void APlanOfAnotherShapeIsRefusedWholeAndEveryProblemNamed()
    {
        using var notJson = new PlanCopy(Gardnerville, _ => { });
        File.WriteAllText(notJson.Path, "{ not json");
        Assert.Contains("cannot be read as JSON", Refusal(notJson.Path));

        using var later = new PlanCopy(Gardnerville, plan => plan["supervision_plan_version"] = 2);
        Assert.Contains("declares supervision_plan_version 2, and version 1 is the only shape this session reads",
                        Refusal(later.Path));

        using var partial = new PlanCopy(Bahonar, plan =>
        {
            plan.Remove("routes_digest");
            plan["instances"]![1]!.AsObject().Remove("intervals");
            plan["instances"]![2]!["intervals"]![0]!["entity_id"] = "probe_d2";
            plan["instances"]![26]!["participants"]!.AsArray().Add(new JsonObject
            {
                ["entity_id"] = "guard_d4_h7_t3", ["role"] = "bahonar:guard", ["sumo_id"] = "guard_d4_h7_t3",
            });
            plan["cohorts"]![0]!["supervision"] = "nominal";
        });
        string message = Refusal(partial.Path);
        _output.WriteLine(message);
        Assert.Contains("(1) plan.routes_digest is missing", message);
        Assert.Contains("(2) plan.instances[1].intervals is missing", message);
        Assert.Contains("(3) plan.instances[2].intervals[0].entity_id is 'probe_d2', which is no participant of the "
                        + "instance", message);
        Assert.Contains("(4) plan.instances[26].participants names a participant on an absence", message);
        Assert.Contains("(5) plan.cohorts[0].supervision is nominal on a cohort", message);
    }

    [Fact]
    public void APhaseAnchorCarriesItsPlaceInTheRoute()
    {
        // No shipped plan anchors to a phase; the shadow's circuit, re-anchored, stands in for one.
        using var copy = new PlanCopy(Bahonar, plan => plan["instances"]![3]!["intervals"]![0]!["anchor"] = new JsonObject
        {
            ["start"] = new JsonObject { ["event"] = "phase:1", ["route_index"] = 4, ["edge"] = "26413425#5" },
            ["end"] = new JsonObject { ["event"] = "phase:2", ["route_index"] = 11, ["edge"] = "26413459" },
        });

        PlannedInterval circuit = Assert.Single(SupervisionPlan.Read(copy.Path).Instances[3].Intervals);

        Assert.Equal((AnchorEvent.Phase, 1, 4, "26413425#5"),
                     (circuit.Anchor!.Start.Event, circuit.Anchor.Start.Index, circuit.Anchor.Start.RouteIndex,
                      circuit.Anchor.Start.Edge));
        Assert.Equal((AnchorEvent.Phase, 2, 11, "26413459"),
                     (circuit.Anchor.End!.Event, circuit.Anchor.End.Index, circuit.Anchor.End.RouteIndex,
                      circuit.Anchor.End.Edge));
        Assert.Null(circuit.Anchor.Start.Lane);
        Assert.Null(circuit.Anchor.Start.EndPositionMetres);
    }

    private string Refusal(string path)
    {
        string message = Assert.Throws<CoSimSessionRefusedException>(() => SupervisionPlan.Read(path)).Message;
        _output.WriteLine(message);
        return message;
    }

    private static PatternInstance Instance(SupervisionPlan plan, string name) =>
        plan.Instances.Single(instance => instance.InstanceId == $"{plan.ScenarioId}/{name}");

    private static AuthorTerm Term(AuthorNamespace space, string term) => space.Terms.Single(t => t.Term == term);

    internal static string ShippedPlan(string scenario) => ShippedFile(scenario, ".supervision.json");

    private static string ShippedFile(string scenario, string extension) =>
        ScenarioLockCheckTests.RepositoryFile("Import", scenario + extension);

    /// <summary>Set the value at a dotted path with list indices -- <c>instances[1].intervals[0].phase</c>.</summary>
    private static void Set(JsonObject plan, string at, JsonNode? value)
    {
        JsonNode node = plan;
        string[] steps = at.Split('.');
        for (int index = 0; index < steps.Length; index++)
        {
            string step = steps[index];
            int bracket = step.IndexOf('[', StringComparison.Ordinal);
            string key = bracket < 0 ? step : step[..bracket];
            int? item = bracket < 0 ? null : int.Parse(step[(bracket + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
            bool last = index == steps.Length - 1;
            if (last && item is null)
            {
                node[key] = value;
                return;
            }

            node = node[key]!;
            if (item is { } position)
            {
                if (last)
                {
                    node[position] = value;
                    return;
                }

                node = node[position]!;
            }
        }
    }

    /// <summary>The types a plan reaches through its public properties, in the order they are met.</summary>
    private static List<Type> PlanTypes()
    {
        List<Type> found = [];
        Queue<Type> waiting = new([typeof(SupervisionPlan)]);
        while (waiting.TryDequeue(out Type? type))
        {
            if (found.Contains(type))
            {
                continue;
            }

            found.Add(type);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                foreach (Type reached in Reached(property.PropertyType))
                {
                    waiting.Enqueue(reached);
                }
            }
        }

        return found;
    }

    private static IEnumerable<Type> Reached(Type type)
    {
        if (type.Assembly == typeof(SupervisionPlan).Assembly)
        {
            yield return type;
        }

        foreach (Type argument in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            foreach (Type reached in Reached(argument))
            {
                yield return reached;
            }
        }
    }

    private static bool Immutable(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Immutable(underlying);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            return Immutable(type.GetGenericArguments()[0]);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableSortedDictionary<,>))
        {
            return type.GetGenericArguments().All(Immutable);
        }

        return type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(JsonElement)
               || type.Assembly == typeof(SupervisionPlan).Assembly;
    }

    /// <summary>A shipped plan copied to a temporary file with an edit, for a test of what reading it does.</summary>
    private sealed class PlanCopy : IDisposable
    {
        public PlanCopy(string scenario, Action<JsonObject> edit)
        {
            JsonObject plan = JsonNode.Parse(File.ReadAllText(ShippedPlan(scenario)))!.AsObject();
            edit(plan);
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                          $"carlanet-plan-{Guid.NewGuid():n}.supervision.json");
            File.WriteAllText(Path, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
