using System.Text.Json;
using System.Text.Json.Nodes;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The rows of a supervision plan in the shape the scenario compiler writes them, for a test that needs a
/// plan of its own: instances with their participants and intervals, anchors, absences, series, cohorts
/// and entities.
/// </summary>
/// <remarks>
/// A plan is the shipped Gardnerville plan's identity and vocabulary -- digested by the compiler, no
/// author namespace -- with its rows replaced. The reader does not resolve a label against the
/// vocabulary (the compiler does), so a test's labels are spelled freely.
/// </remarks>
internal static class PlanRows
{
    public const string ScenarioId = "Supervised";

    /// <summary>A plan document with the rows given, everything else the shipped Gardnerville plan's.</summary>
    public static JsonObject Plan(JsonArray instances, JsonArray? series = null, JsonArray? cohorts = null,
                                  JsonArray? entities = null)
    {
        JsonObject plan = JsonNode.Parse(File.ReadAllText(SupervisionPlanTests.ShippedPlan(
            "Gardnerville_Centerville_Lane_NeighborhoodOrbit")))!.AsObject();
        plan["plan_id"] = ScenarioId;
        plan["scenario_id"] = ScenarioId;
        plan["instances"] = instances;
        plan["series"] = series ?? [];
        plan["cohorts"] = cohorts ?? [];
        plan["entities"] = entities ?? [];
        return plan;
    }

    /// <summary>A plan document with its rows replaced by those given, its identity and digests kept.</summary>
    public static JsonObject WithRows(JsonObject plan, JsonArray instances, JsonArray? series = null,
                                      JsonArray? cohorts = null, JsonArray? entities = null)
    {
        plan["instances"] = instances;
        plan["series"] = series ?? [];
        plan["cohorts"] = cohorts ?? [];
        plan["entities"] = entities ?? [];
        return plan;
    }

    /// <summary>Write a plan document to a temporary file and read it as a session would.</summary>
    public static SupervisionPlan Read(JsonObject plan)
    {
        string path = Path.Combine(Path.GetTempPath(), $"carlanet-binder-{Guid.NewGuid():n}.supervision.json");
        File.WriteAllText(path, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            return SupervisionPlan.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A pattern instance a vehicle realises.</summary>
    public static JsonObject Instance(string name, string supervision, string[] labels,
                                      (string Entity, string Role)[] participants, params JsonObject[] intervals) => new()
    {
        ["instance_id"] = $"{ScenarioId}/{name}",
        ["supervision"] = supervision,
        ["realisation"] = "present",
        ["labels"] = Texts(labels),
        ["parameters"] = new JsonObject(),
        ["hard_negative_for"] = null,
        ["counterfactual"] = null,
        ["series_ref"] = null,
        ["slot_ref"] = null,
        ["aoi_refs"] = new JsonArray(),
        ["participants"] = new JsonArray(participants.Select(participant => (JsonNode?)new JsonObject
        {
            ["entity_id"] = participant.Entity,
            ["role"] = participant.Role,
            ["sumo_id"] = participant.Entity,
        }).ToArray()),
        ["intervals"] = new JsonArray(intervals.Select(interval => (JsonNode?)interval).ToArray()),
    };

    /// <summary>An interval of a participant, anchored or declared in seconds.</summary>
    public static JsonObject Interval(string entity, string phase, JsonObject? anchor = null,
                                      double? declaredStart = null, double? declaredEnd = null,
                                      double? declaredDuration = null) => new()
    {
        ["entity_id"] = entity,
        ["phase"] = phase,
        ["anchor"] = anchor,
        ["declared_start_s"] = declaredStart,
        ["declared_start_civil"] = declaredStart is null ? null : "2026-09-29T07:00:00+03:30",
        ["declared_end_s"] = declaredEnd,
        ["declared_end_civil"] = declaredEnd is null ? null : "2026-09-29T07:00:00+03:30",
        ["declared_duration_s"] = declaredDuration ?? (declaredStart is { } start && declaredEnd is { } end
            ? end - start
            : null),
    };

    /// <summary>An anchor from one event to another, or open-ended.</summary>
    public static JsonObject Anchor(JsonObject start, JsonObject? end = null) => new() { ["start"] = start, ["end"] = end };

    public static JsonObject Depart() => new() { ["event"] = "depart" };

    public static JsonObject Stop(int index, string lane, double endPositionMetres) =>
        new() { ["event"] = $"stop:{index}", ["lane"] = lane, ["end_pos_m"] = endPositionMetres };

    public static JsonObject StopEnd(int index, string lane, double endPositionMetres) =>
        new() { ["event"] = $"stop_end:{index}", ["lane"] = lane, ["end_pos_m"] = endPositionMetres };

    public static JsonObject Phase(int index, int routeIndex, string edge) =>
        new() { ["event"] = $"phase:{index}", ["route_index"] = routeIndex, ["edge"] = edge };

    /// <summary>An absence over a series' unrealised slot, with its vacancy over the declared seconds.</summary>
    public static JsonObject Absence(string name, string series, string slot, string expectedEntity, string[] labels,
                                     string area, double from, double to) => new()
    {
        ["instance_id"] = $"{ScenarioId}/{name}",
        ["supervision"] = "annotated",
        ["realisation"] = "absent",
        ["labels"] = Texts(labels),
        ["parameters"] = new JsonObject(),
        ["hard_negative_for"] = null,
        ["counterfactual"] = new JsonObject { ["kind"] = "series", ["ref"] = series },
        ["series_ref"] = series,
        ["slot_ref"] = slot,
        ["aoi_refs"] = new JsonArray(area),
        ["participants"] = new JsonArray(),
        ["expected"] = new JsonObject
        {
            ["role"] = "test:guard",
            ["expected_entity_id"] = expectedEntity,
            ["route"] = new JsonObject { ["from"] = "approach", ["to"] = "turn_east", ["via"] = new JsonArray() },
            ["site_lane"] = "turn_east_0",
            ["site_pos_m"] = 50.0,
            ["declared_start_s"] = from,
            ["declared_end_s"] = to,
        },
        ["intervals"] = new JsonArray(new JsonObject
        {
            ["entity_id"] = null,
            ["phase"] = "vacancy",
            ["anchor"] = null,
            ["declared_start_s"] = from,
            ["declared_start_civil"] = "2026-09-29T07:00:00+03:30",
            ["declared_end_s"] = to,
            ["declared_end_civil"] = "2026-09-29T07:00:00+03:30",
            ["declared_duration_s"] = to - from,
        }),
        ["counter_evidence"] = new JsonObject { ["series_slots_total"] = 2, ["series_slots_realised"] = 1 },
    };

    /// <summary>A series with a slot per member given, realised by it or, where null, unrealised.</summary>
    public static JsonObject Series(string id, string supervision, string[] labels,
                                    params (string Slot, string? RealisedBy, double From, double To)[] slots) => new()
    {
        ["series_id"] = id,
        ["rota_ref"] = id,
        ["cadence"] = "enumerated",
        ["member_role"] = "test:guard",
        ["supervision"] = supervision,
        ["labels"] = Texts(labels),
        ["parameters"] = new JsonObject(),
        ["hard_negative_for"] = null,
        ["slots"] = new JsonArray(slots.Select(slot => (JsonNode?)new JsonObject
        {
            ["slot_key"] = slot.Slot,
            ["aoi_ref"] = "site",
            ["declared_start_s"] = slot.From,
            ["declared_start_civil"] = "2026-09-29T07:00:00+03:30",
            ["declared_end_s"] = slot.To,
            ["declared_end_civil"] = "2026-09-29T07:00:00+03:30",
            ["expected_entity_id"] = slot.Slot,
            ["realised_by"] = slot.RealisedBy,
        }).ToArray()),
    };

    public static JsonObject Cohort(string flow, string supervision, params string[] labels) => new()
    {
        ["flow_id"] = flow,
        ["supervision"] = supervision,
        ["labels"] = Texts(labels),
        ["parameters"] = new JsonObject(),
    };

    public static JsonObject Entity(string id, string state, params string[] refs) => new()
    {
        ["entity_id"] = id,
        ["supervision"] = new JsonArray(state),
        ["refs"] = Texts(refs),
    };

    private static JsonArray Texts(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
