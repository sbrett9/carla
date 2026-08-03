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
public static class OpenScenarioParser
{
    public static ScenarioDefinition LoadFile(string path)
        => Load(File.ReadAllText(path), System.IO.Path.GetFileNameWithoutExtension(path));

    public static ScenarioDefinition Load(string xml, string name = "scenario")
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new ScenarioParseException($"not well-formed XML: {ex.Message}"); }

        XElement root = doc.Root ?? throw new ScenarioParseException("empty document");
        if (root.Name.LocalName != "OpenSCENARIO")
            throw new ScenarioParseException($"root element is <{root.Name.LocalName}>, expected <OpenSCENARIO>");

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
            RoadNetworkFile = root.Element("RoadNetwork")?.Element("LogicFile")?.Attribute("filepath")?.Value,
            Entities = entities,
            Acts = acts,
            StopTrigger = stop is null ? null : ParseTrigger(stop, "storyboard stop trigger"),
        };
    }

    private static IReadOnlyList<ScenarioEntity> ParseEntities(XElement root)
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

            // Private init actions carry where the entity starts and how fast.
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
            XElement? target = priv.Descendants("AbsoluteTargetSpeed").FirstOrDefault();
            if (target is not null) initialSpeed = Number(target, "value", entityName);

            entities.Add(new ScenarioEntity
            {
                Name = entityName,
                Category = Attr(vehicle, "vehicleCategory") ?? "car",
                TemplateHint = Property(vehicle, "drawtonomy:template"),
                Colour = Property(vehicle, "drawtonomy:color"),
                InitialSpeedMps = initialSpeed,
                InitialPosition = new LanePosition(
                    (int)Number(lane, "roadId", entityName),
                    (int)Number(lane, "laneId", entityName),
                    Number(lane, "s", entityName),
                    lane.Attribute("offset") is null ? 0.0 : Number(lane, "offset", entityName)),
            });
        }

        if (entities.Count == 0) throw new ScenarioParseException("the scenario places no entities");
        return entities;
    }

    private static IReadOnlyList<ScenarioAct> ParseActs(XElement root)
    {
        var acts = new List<ScenarioAct>();
        foreach (XElement act in root.Element("Storyboard")?.Elements("Story").Elements("Act")
                                 ?? Enumerable.Empty<XElement>())
        {
            string actName = Attr(act, "name") ?? $"act{acts.Count + 1}";

            XElement? start = act.Element("StartTrigger");
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
                        Action = ParseAction(ev, evName),
                    });
                }
            }

            acts.Add(new ScenarioAct
            {
                Name = actName,
                StartTrigger = start is null ? ScenarioTrigger.Immediate() : ParseTrigger(start, actName),
                ActorNames = actors,
                Events = events,
            });
        }

        if (acts.Count == 0) throw new ScenarioParseException("the scenario contains no acts");
        return acts;
    }

    private static ScenarioAction ParseAction(XElement ev, string where)
    {
        XElement? speed = ev.Descendants("SpeedAction").FirstOrDefault();
        if (speed is null)
        {
            string kind = ev.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.EndsWith("Action") && e.Name.LocalName != "Action")
                ?.Name.LocalName ?? "none";
            throw new ScenarioParseException(
                $"event '{where}' applies {kind}; only SpeedAction is supported");
        }

        XElement? absolute = speed.Descendants("AbsoluteTargetSpeed").FirstOrDefault();
        if (absolute is null)
            throw new ScenarioParseException(
                $"event '{where}' uses a relative speed target; only AbsoluteTargetSpeed is supported");

        double seconds = 0.0;
        if (speed.Element("SpeedActionDynamics") is { } dyn)
        {
            // "time" gives the transition duration directly. Other dimensions describe the same
            // transition in distance or rate and would need converting against the current speed, which
            // is not known at parse time.
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

    private static ScenarioTrigger ParseTrigger(XElement trigger, string where)
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
        {
            return new ScenarioTrigger
            {
                Kind = TriggerKind.StandStill,
                Value = Number(still, "duration", where),
                EntityRef = condition.Descendants("EntityRef").FirstOrDefault()?.Attribute("entityRef")?.Value,
            };
        }

        string found = condition.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith("Condition") && !IsConditionWrapper(e.Name.LocalName))
            ?.Name.LocalName ?? "unknown";
        throw new ScenarioParseException($"trigger on '{where}' uses {found}, which is not supported");
    }

    /// Elements that group a condition rather than being one; naming these in an error would point the
    /// reader at the wrapper instead of at the construct that is unsupported.
    private static bool IsConditionWrapper(string name)
        => name is "ByValueCondition" or "ByEntityCondition" or "EntityCondition";

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static string? Property(XElement vehicle, string name)
        => vehicle.Element("Properties")?.Elements("Property")
            .FirstOrDefault(p => p.Attribute("name")?.Value == name)?.Attribute("value")?.Value;

    private static double Number(XElement e, string attribute, string where)
    {
        string? raw = e.Attribute(attribute)?.Value;
        if (raw is null)
            throw new ScenarioParseException($"'{where}': <{e.Name.LocalName}> has no {attribute}");
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
