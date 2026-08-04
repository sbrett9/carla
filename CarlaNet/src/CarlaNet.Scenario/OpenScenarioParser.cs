using System.Globalization;
using System.Xml.Linq;

namespace CarlaNet.Scenario;

/// <summary>
/// Reads an ASAM OpenSCENARIO 1.x storyboard into <see cref="ScenarioDefinition"/>.
///
/// Unsupported constructs are refused rather than ignored. A scenario that silently drops a trigger or
/// an action would execute as something other than what was authored, and the discrepancy would surface
/// much later as unexplained behaviour in training data. Failing at load, by contrast, is immediate and
/// names what is missing.
/// </summary>
public sealed class OpenScenarioParser
{
    private readonly Dictionary<string, string> _parameters = new();

    private OpenScenarioParser() { }

    public static ScenarioDefinition LoadFile(string path)
        => LoadFile(path, null);

    /// <param name="overrides">Values replacing the storyboard's own parameter declarations. This is
    /// what turns one authored storyboard into a family of runs.</param>
    public static ScenarioDefinition LoadFile(string path, IReadOnlyDictionary<string, string>? overrides)
        => Load(File.ReadAllText(path), System.IO.Path.GetFileNameWithoutExtension(path), overrides);

    public static ScenarioDefinition Load(string xml, string name = "scenario",
                                          IReadOnlyDictionary<string, string>? overrides = null)
        => new OpenScenarioParser().Parse(xml, name, overrides);

    private ScenarioDefinition Parse(string xml, string name, IReadOnlyDictionary<string, string>? overrides)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new ScenarioParseException($"not well-formed XML: {ex.Message}"); }

        XElement root = doc.Root ?? throw new ScenarioParseException("empty document");
        if (root.Name.LocalName != "OpenSCENARIO")
            throw new ScenarioParseException($"root element is <{root.Name.LocalName}>, expected <OpenSCENARIO>");

        ReadParameters(root, overrides);

        XElement? header = root.Element("FileHeader");
        string version = header is null
            ? "unknown"
            : $"{Attr(header, "revMajor") ?? "1"}.{Attr(header, "revMinor") ?? "0"}";

        var entities = ParseEntities(root);
        var acts = ParseActs(root);

        XElement? stop = root.Element("Storyboard")?.Element("StopTrigger");

        return new ScenarioDefinition
        {
            Name = name,
            Version = version,
            RoadNetworkFile = Attr(root.Element("RoadNetwork")?.Element("LogicFile"), "filepath"),
            Parameters = _parameters,
            Entities = entities,
            Acts = acts,
            StopTrigger = stop is null ? null : ParseTrigger(stop, "storyboard stop trigger"),
        };
    }

    // ── parameters ────────────────────────────────────────────────────────────

    private void ReadParameters(XElement root, IReadOnlyDictionary<string, string>? overrides)
    {
        foreach (XElement d in root.Element("ParameterDeclarations")?.Elements("ParameterDeclaration")
                               ?? Enumerable.Empty<XElement>())
        {
            string? key = d.Attribute("name")?.Value;
            string? value = d.Attribute("value")?.Value;
            if (key is not null && value is not null) _parameters[key] = value;
        }

        if (overrides is null) return;
        foreach (var (key, value) in overrides)
        {
            if (!_parameters.ContainsKey(key))
                throw new ScenarioParseException(
                    $"cannot override '{key}': the storyboard declares no such parameter");
            _parameters[key] = value;
        }
    }

    /// <summary>
    /// Substitutes a parameter reference. An undeclared reference is refused rather than passed through:
    /// a literal "$Speed" reaching a numeric field would fail confusingly far from its cause, and one
    /// reaching a name field would silently mismatch.
    /// </summary>
    private string Resolve(string raw, string where)
    {
        if (raw.Length < 2 || raw[0] != '$') return raw;
        string key = raw[1..];
        if (_parameters.TryGetValue(key, out string? value)) return value;
        throw new ScenarioParseException($"'{where}': parameter {raw} is used but never declared");
    }

    // ── entities ──────────────────────────────────────────────────────────────

    private IReadOnlyList<ScenarioEntity> ParseEntities(XElement root)
    {
        var init = root.Element("Storyboard")?.Element("Init")?.Element("Actions");
        var entities = new List<ScenarioEntity>();

        foreach (XElement obj in root.Element("Entities")?.Elements("ScenarioObject") ?? Enumerable.Empty<XElement>())
        {
            string entityName = Attr(obj, "name")
                ?? throw new ScenarioParseException("a <ScenarioObject> has no name");

            XElement vehicle = obj.Element("Vehicle")
                ?? throw new ScenarioParseException(
                    $"entity '{entityName}' is not a <Vehicle>; only vehicles are supported");

            XElement? priv = init?.Elements("Private")
                .FirstOrDefault(p => Attr(p, "entityRef") == entityName);
            if (priv is null)
                throw new ScenarioParseException($"entity '{entityName}' has no initial position");

            XElement? lane = priv.Descendants("LanePosition").FirstOrDefault();
            if (lane is null)
            {
                string kind = priv.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName.EndsWith("Position") && e.Name.LocalName != "Position")
                    ?.Name.LocalName ?? "none";
                throw new ScenarioParseException(
                    $"entity '{entityName}' is placed by {kind}; only LanePosition is supported");
            }

            double? initialSpeed = null;
            if (priv.Descendants("AbsoluteTargetSpeed").FirstOrDefault() is { } target)
                initialSpeed = Number(target, "value", entityName);

            IReadOnlyList<LanePosition>? initialRoute = null;
            if (priv.Descendants("AssignRouteAction").FirstOrDefault() is { } route)
                initialRoute = ParseRouteAction(route, entityName).Waypoints;

            RefuseUnknownInitActions(priv, entityName);

            entities.Add(new ScenarioEntity
            {
                Name = entityName,
                Category = Attr(vehicle, "vehicleCategory") ?? "car",
                TemplateHint = Property(vehicle, "drawtonomy:template"),
                Colour = Property(vehicle, "drawtonomy:color"),
                InitialSpeedMps = initialSpeed,
                InitialRoute = initialRoute,
                InitialPosition = ReadLanePosition(lane, entityName),
            });
        }

        if (entities.Count == 0) throw new ScenarioParseException("the scenario places no entities");
        return entities;
    }

    /// Initialisation actions this reader understands. Anything else is refused: an entity placed
    /// without an instruction the storyboard gave would behave differently from the one authored, and
    /// silently dropping a route is exactly that.
    private static readonly string[] KnownInitActions = ["TeleportAction", "LongitudinalAction", "RoutingAction"];

    private static void RefuseUnknownInitActions(XElement priv, string entityName)
    {
        foreach (XElement action in priv.Elements("PrivateAction"))
            foreach (XElement kind in action.Elements())
                if (!KnownInitActions.Contains(kind.Name.LocalName))
                    throw new ScenarioParseException(
                        $"entity '{entityName}' is initialised with {kind.Name.LocalName}, " +
                        "which is not supported");
    }

    private LanePosition ReadLanePosition(XElement lane, string where) => new(
        (int)Number(lane, "roadId", where),
        (int)Number(lane, "laneId", where),
        Number(lane, "s", where),
        lane.Attribute("offset") is null ? 0.0 : Number(lane, "offset", where));

    // ── acts, events, actions ─────────────────────────────────────────────────

    private IReadOnlyList<ScenarioAct> ParseActs(XElement root)
    {
        var acts = new List<ScenarioAct>();
        foreach (XElement act in root.Element("Storyboard")?.Elements("Story").Elements("Act")
                                 ?? Enumerable.Empty<XElement>())
        {
            string actName = Attr(act, "name") ?? $"act{acts.Count + 1}";

            var actors = new List<string>();
            var events = new List<ScenarioEvent>();

            foreach (XElement group in act.Elements("ManeuverGroup"))
            {
                foreach (XElement r in group.Element("Actors")?.Elements("EntityRef") ?? Enumerable.Empty<XElement>())
                    if (Attr(r, "entityRef") is { } who) actors.Add(who);

                foreach (XElement ev in group.Elements("Maneuver").Elements("Event"))
                {
                    string evName = Attr(ev, "name") ?? $"{actName}_event{events.Count + 1}";
                    events.Add(new ScenarioEvent
                    {
                        Name = evName,
                        StartTrigger = ev.Element("StartTrigger") is { } t
                            ? ParseTrigger(t, evName)
                            : ScenarioTrigger.Immediate(),
                        Actions = ParseActions(ev, evName),
                    });
                }
            }

            acts.Add(new ScenarioAct
            {
                Name = actName,
                StartTrigger = act.Element("StartTrigger") is { } s
                    ? ParseTrigger(s, actName)
                    : ScenarioTrigger.Immediate(),
                ActorNames = actors,
                Events = events,
            });
        }

        if (acts.Count == 0) throw new ScenarioParseException("the scenario contains no acts");
        return acts;
    }

    private IReadOnlyList<ScenarioAction> ParseActions(XElement ev, string where)
    {
        var actions = new List<ScenarioAction>();

        foreach (XElement speed in ev.Descendants("SpeedAction"))
            actions.Add(ParseSpeedAction(speed, where));

        foreach (XElement route in ev.Descendants("AssignRouteAction"))
            actions.Add(ParseRouteAction(route, where));

        foreach (XElement entityAction in ev.Descendants("EntityAction"))
        {
            if (entityAction.Element("DeleteEntityAction") is null)
            {
                string kind = entityAction.Elements().FirstOrDefault()?.Name.LocalName ?? "none";
                throw new ScenarioParseException(
                    $"event '{where}' applies {kind} to an entity; only DeleteEntityAction is supported");
            }
            actions.Add(new DeleteEntityAction
            {
                TargetEntity = Attr(entityAction, "entityRef")
                    ?? throw new ScenarioParseException($"event '{where}': a delete names no entity"),
            });
        }

        if (actions.Count == 0)
        {
            string kind = ev.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.EndsWith("Action") && !IsActionWrapper(e.Name.LocalName))
                ?.Name.LocalName ?? "none";
            throw new ScenarioParseException(
                $"event '{where}' applies {kind}, which is not supported");
        }

        return actions;
    }

    private SpeedAction ParseSpeedAction(XElement speed, string where)
    {
        XElement absolute = speed.Descendants("AbsoluteTargetSpeed").FirstOrDefault()
            ?? throw new ScenarioParseException(
                $"event '{where}' uses a relative speed target; only AbsoluteTargetSpeed is supported");

        double seconds = 0.0;
        if (speed.Element("SpeedActionDynamics") is { } dyn)
        {
            // "time" gives the transition duration directly. The other dimensions describe the same
            // transition in distance or rate and would need converting against a speed not known here.
            string dimension = Attr(dyn, "dynamicsDimension") ?? "time";
            if (dimension != "time")
                throw new ScenarioParseException(
                    $"event '{where}' uses dynamicsDimension '{dimension}'; only 'time' is supported");
            seconds = dyn.Attribute("value") is null ? 0.0 : Number(dyn, "value", where);
        }

        return new SpeedAction
        {
            TargetSpeedMps = Number(absolute, "value", where),
            TransitionSeconds = seconds,
        };
    }

    private AssignRouteAction ParseRouteAction(XElement route, string where)
    {
        var waypoints = new List<LanePosition>();
        foreach (XElement wp in route.Descendants("Waypoint"))
        {
            XElement lane = wp.Descendants("LanePosition").FirstOrDefault()
                ?? throw new ScenarioParseException(
                    $"event '{where}': a route waypoint is not a LanePosition, which is the only " +
                    "supported form");
            waypoints.Add(ReadLanePosition(lane, where));
        }

        if (waypoints.Count == 0)
            throw new ScenarioParseException($"event '{where}': the route has no waypoints");

        return new AssignRouteAction { Waypoints = waypoints };
    }

    // ── triggers ──────────────────────────────────────────────────────────────

    private ScenarioTrigger ParseTrigger(XElement trigger, string where)
    {
        XElement? condition = trigger.Descendants("Condition").FirstOrDefault();
        if (condition is null) return ScenarioTrigger.Immediate();

        if (condition.Descendants("SimulationTimeCondition").FirstOrDefault() is { } simTime)
        {
            double v = Number(simTime, "value", where);
            // A threshold of zero is how the authoring tool writes "as soon as this becomes active";
            // treating it as a time comparison would delay it by one evaluation for no reason.
            return v <= 0.0
                ? ScenarioTrigger.Immediate()
                : new ScenarioTrigger { Kind = TriggerKind.SimulationTime, Value = v };
        }

        if (condition.Descendants("StoryboardElementStateCondition").FirstOrDefault() is { } element)
        {
            string type = Attr(element, "storyboardElementType") ?? "act";
            if (type != "act")
                throw new ScenarioParseException(
                    $"trigger on '{where}' waits on a '{type}'; only act state is supported");
            return new ScenarioTrigger
            {
                Kind = TriggerKind.StoryboardElementState,
                ElementRef = Attr(element, "storyboardElementRef")
                    ?? throw new ScenarioParseException($"trigger on '{where}' names no act"),
            };
        }

        if (condition.Descendants("StandStillCondition").FirstOrDefault() is { } still)
            return new ScenarioTrigger
            {
                Kind = TriggerKind.StandStill,
                Value = Number(still, "duration", where),
                EntityRef = TriggeringEntity(condition),
            };

        if (condition.Descendants("ReachPositionCondition").FirstOrDefault() is { } reach)
        {
            XElement lane = reach.Descendants("LanePosition").FirstOrDefault()
                ?? throw new ScenarioParseException(
                    $"trigger on '{where}' waits on a position that is not a LanePosition, which is " +
                    "the only supported form");
            return new ScenarioTrigger
            {
                Kind = TriggerKind.ReachPosition,
                Value = reach.Attribute("tolerance") is null ? 1.0 : Number(reach, "tolerance", where),
                Position = ReadLanePosition(lane, where),
                EntityRef = TriggeringEntity(condition),
            };
        }

        string found = condition.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith("Condition") && !IsConditionWrapper(e.Name.LocalName))
            ?.Name.LocalName ?? "unknown";
        throw new ScenarioParseException($"trigger on '{where}' uses {found}, which is not supported");
    }

    private static string? TriggeringEntity(XElement condition)
        => condition.Descendants("EntityRef").FirstOrDefault()?.Attribute("entityRef")?.Value;

    // ── helpers ───────────────────────────────────────────────────────────────

    /// Elements that group a condition rather than being one; naming these in an error would point the
    /// reader at the wrapper instead of at the construct that is unsupported.
    private static bool IsConditionWrapper(string name)
        => name is "ByValueCondition" or "ByEntityCondition" or "EntityCondition";

    /// Likewise for the elements that group an action.
    private static bool IsActionWrapper(string name)
        => name is "Action" or "PrivateAction" or "GlobalAction" or "UserDefinedAction"
                or "LongitudinalAction" or "LateralAction" or "RoutingAction" or "EntityAction";

    private string? Attr(XElement? e, string name)
    {
        string? raw = e?.Attribute(name)?.Value;
        return raw is null ? null : Resolve(raw, e!.Name.LocalName);
    }

    private string? Property(XElement vehicle, string name)
    {
        string? raw = vehicle.Element("Properties")?.Elements("Property")
            .FirstOrDefault(p => p.Attribute("name")?.Value == name)?.Attribute("value")?.Value;
        return raw is null ? null : Resolve(raw, name);
    }

    private double Number(XElement e, string attribute, string where)
    {
        string? raw = e.Attribute(attribute)?.Value;
        if (raw is null)
            throw new ScenarioParseException($"'{where}': <{e.Name.LocalName}> has no {attribute}");
        raw = Resolve(raw, where);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new ScenarioParseException($"'{where}': {attribute}='{raw}' is not a number");
        return v;
    }
}

/// <summary>Raised when a scenario cannot be executed as authored.</summary>
public sealed class ScenarioParseException : Exception
{
    public ScenarioParseException(string message) : base(message) { }
}
