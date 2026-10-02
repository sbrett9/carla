// The metering cycle the world build gives every ramp meter.
//
// netconvert gives a ramp meter the programme it would give a junction: on the Arapahoe extract, an
// actuated 80 s green (10-50 s), 5 s amber, 5 s red, which barely meters anything. A real meter
// releases about one vehicle per green on a cycle of a few seconds, and that is what this writes:
//
//   * GREEN 2 s, RED AT LEAST 4 s, ON A 6 s CYCLE PER LANE. Two seconds lets the vehicle waiting at
//     the stop line clear it -- it stands about a metre short, and even a lorry covers that in well
//     under two seconds -- while the vehicle behind it, a car length further back, cannot reach the
//     line before the red. So one vehicle leaves per green, and a lane releases at most 600 vehicles
//     an hour: inside the 240-900 an hour that FHWA's Ramp Management and Control Handbook gives for
//     single-lane, one-vehicle-per-green metering, and high in it, because a fixed programme cannot
//     slow down for a congested freeway the way a traffic-responsive meter does, and a low fixed rate
//     would queue a ramp a real meter would be releasing faster. Measured on Arapahoe: no green
//     released more than one vehicle.
//
//   * RED AND GREEN ONLY. Colorado's meters show no amber, so neither does this. The green is SUMO's
//     's' state: a vehicle stops at the line before it goes, and goes only while the light is green.
//     That is a meter's one-vehicle-per-green release enforced by the simulation rather than left to
//     chance, and it is what keeps a red with no amber before it from catching a vehicle at speed.
//     Measured on Arapahoe over 45 minutes: with 'g' greens 23 vehicles reached the line as the red
//     came on and stopped dead at it, at 9-16 m/s2; with 's' one did, and it had crept a few
//     centimetres. An 's' to 'r' change is also not the green-to-red change SUMO warns about, so a
//     metered world loads without a missing-amber warning per meter.
//
//   * A TWO-LANE METER RELEASES ITS LANES ALTERNATELY, one lane's green starting 3 s after the
//     other's, so the two lanes together release a vehicle every 3 s and up to 1 200 an hour. Two
//     vehicles released side by side reach the merge together, and every two-lane meter on the
//     Arapahoe extract narrows to one lane within 16-168 m of its stop line, so a simultaneous
//     release would deliver pairs into a lane drop. More lanes extend the same rotation, 3 s apart.
//
//   * STATIC, not actuated. A fixed cycle is the metering rate itself; netconvert's actuated default
//     (--tls.default-type actuated) would stretch a green while vehicles keep arriving, which is the
//     opposite of metering.
//
// Only a meter whose signal controls a single approach is metered: every link of its programme must
// leave one non-internal edge, which is the ramp. A signal node that is also a junction of several
// roads, or that carries a pedestrian crossing, keeps netconvert's programme, because metering every
// approach of a junction is not what the meter does; the conversion reports which and why.
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CarlaNet.Map;

/// <summary>One phase of a metering programme: how long it lasts and the state of every link.</summary>
public sealed record MeterPhase(int DurationSeconds, string State);

/// <summary>A ramp meter given the metering programme.</summary>
/// <param name="Id">The signal's id, which is the OSM node id of the meter.</param>
/// <param name="ProgramId">The programme it replaces.</param>
/// <param name="Lanes">How many lanes of the ramp it meters.</param>
/// <param name="Links">Each link index's ramp lane, in link-index order.</param>
/// <param name="Phases">The programme.</param>
public sealed record MeteredSignal(
    string Id, string ProgramId, int Lanes, IReadOnlyList<(string FromEdge, int FromLane)> Links,
    IReadOnlyList<MeterPhase> Phases)
{
    /// <summary>The cycle length, the sum of the phases.</summary>
    public int CycleSeconds => Phases.Sum(p => p.DurationSeconds);
}

/// <summary>
/// The programmes a conversion gives its ramp meters, and the meters it left alone with the reason.
/// </summary>
/// <param name="Metered">The meters given the metering programme, in id order.</param>
/// <param name="NotMetered">The meters that keep netconvert's programme, each with why.</param>
/// <param name="ProgramFile">The <c>--tllogic-files</c> document netconvert reads, or empty when no
/// meter is metered.</param>
public sealed record RampMeterPlan(
    IReadOnlyList<MeteredSignal> Metered,
    IReadOnlyList<(string Id, string Reason)> NotMetered,
    string ProgramFile);

/// <summary>
/// Builds the metering programme of every ramp meter from the network netconvert built for it, and
/// checks that a later network carries it.
/// </summary>
public static class RampMeterProgram
{
    /// <summary>How long a lane's green lasts: long enough for one vehicle to leave.</summary>
    public const int GreenSeconds = 2;

    /// <summary>The shortest cycle a lane is given, so that its red lasts at least 4 s.</summary>
    public const int MinimumCycleSeconds = 6;

    /// <summary>The interval between the start of one lane's green and the next lane's.</summary>
    public const int LaneSpacingSeconds = 3;

    /// <summary>The state of a released lane: stop at the line, then go while it lasts.</summary>
    public const char GreenState = 's';

    /// <summary>The state of a held lane.</summary>
    public const char RedState = 'r';

    /// <summary>The control type of a metering programme.</summary>
    public const string ProgramType = "static";

    /// <summary>
    /// The phases of a meter whose links leave the given ramp lanes, in link-index order: each lane
    /// released in turn, lowest lane index first, <see cref="LaneSpacingSeconds"/> apart.
    /// </summary>
    public static IReadOnlyList<MeterPhase> Phases(IReadOnlyList<int> laneOfLink)
    {
        ArgumentNullException.ThrowIfNull(laneOfLink);
        if (laneOfLink.Count == 0)
            throw new ArgumentException("a meter must control at least one link", nameof(laneOfLink));

        var lanes = laneOfLink.Distinct().Order().ToList();
        // One lane: 2 s green in a 6 s cycle. Two: each lane's green 3 s after the other's, which is
        // the same 6 s cycle per lane. More: 3 s apart, so the cycle grows with the lanes.
        int spacing = Math.Max(LaneSpacingSeconds, MinimumCycleSeconds / lanes.Count);
        var phases = new List<MeterPhase>(2 * lanes.Count);
        string allRed = new(RedState, laneOfLink.Count);
        foreach (int lane in lanes)
        {
            var state = new StringBuilder(laneOfLink.Count);
            foreach (int linkLane in laneOfLink)
                state.Append(linkLane == lane ? GreenState : RedState);
            phases.Add(new MeterPhase(GreenSeconds, state.ToString()));
            phases.Add(new MeterPhase(spacing - GreenSeconds, allRed));
        }
        return phases;
    }

    /// <summary>
    /// The programme of every ramp meter that controls a single approach in <paramref name="networkXml"/>,
    /// a network netconvert built with the meters kept out of junction joining.
    /// </summary>
    public static RampMeterPlan Plan(string networkXml, IReadOnlyList<string> meterIds)
    {
        ArgumentNullException.ThrowIfNull(networkXml);
        ArgumentNullException.ThrowIfNull(meterIds);

        XElement root = XDocument.Parse(networkXml).Root
            ?? throw new InvalidDataException("the SUMO network document is empty");
        var programs = ProgramsById(root);
        var links = LinksBySignal(root);

        var metered = new List<MeteredSignal>();
        var notMetered = new List<(string, string)>();
        foreach (string id in meterIds)
        {
            if (!programs.TryGetValue(id, out var program))
            {
                notMetered.Add((id, "the network has no signal of that id: the meter was removed with "
                                    + "its road, or was not built as a signal"));
                continue;
            }
            links.TryGetValue(id, out var byIndex);
            byIndex ??= [];
            int stateLength = program.States.Count == 0 ? 0 : program.States[0].Length;
            int vehicleLinks = Enumerable.Range(0, stateLength)
                .Count(k => byIndex.TryGetValue(k, out var l) && !l.FromEdge.StartsWith(':'));
            if (stateLength == 0 || vehicleLinks != stateLength)
            {
                notMetered.Add((id, $"{vehicleLinks} of the {stateLength} links of its programme are "
                                    + "vehicle movements off a road, so it also controls something "
                                    + "else, such as a pedestrian crossing"));
                continue;
            }
            var ordered = Enumerable.Range(0, stateLength).Select(k => byIndex[k]).ToList();
            var approaches = ordered.Select(l => l.FromEdge).Distinct(StringComparer.Ordinal).ToList();
            if (approaches.Count != 1)
            {
                notMetered.Add((id, $"it controls {approaches.Count} approaches "
                                    + $"({string.Join(", ", approaches)}), so it is a junction rather "
                                    + "than a meter on one ramp"));
                continue;
            }
            var phases = Phases([.. ordered.Select(l => l.FromLane)]);
            metered.Add(new MeteredSignal(id, program.ProgramId,
                                          ordered.Select(l => l.FromLane).Distinct().Count(),
                                          ordered, phases));
        }
        return new RampMeterPlan(metered, notMetered, metered.Count == 0 ? "" : ProgramFile(metered));
    }

    /// <summary>
    /// What is wrong with <paramref name="networkXml"/> as the network <paramref name="plan"/> was meant
    /// to produce: a metered signal missing, of another type, with other phases, or with its links
    /// leaving other lanes than the plan was built for. Empty when it is right.
    /// </summary>
    public static IReadOnlyList<string> Verify(string networkXml, RampMeterPlan plan)
    {
        ArgumentNullException.ThrowIfNull(networkXml);
        ArgumentNullException.ThrowIfNull(plan);

        XElement root = XDocument.Parse(networkXml).Root
            ?? throw new InvalidDataException("the SUMO network document is empty");
        var programs = ProgramsById(root);
        var links = LinksBySignal(root);
        var problems = new List<string>();
        foreach (MeteredSignal meter in plan.Metered)
        {
            if (!programs.TryGetValue(meter.Id, out var program))
            {
                problems.Add($"{meter.Id}: no programme in the network");
                continue;
            }
            if (program.Type != ProgramType || program.ProgramId != meter.ProgramId)
                problems.Add($"{meter.Id}: programme '{program.ProgramId}' is {program.Type}, "
                             + $"expected '{meter.ProgramId}' {ProgramType}");
            var expected = meter.Phases.Select(p => (p.DurationSeconds * 1.0, p.State)).ToList();
            var actual = program.States.Zip(program.Durations, (s, d) => (d, s)).ToList();
            if (!expected.SequenceEqual(actual))
                problems.Add($"{meter.Id}: phases {Describe(actual)}, expected {Describe(expected)}");
            links.TryGetValue(meter.Id, out var byIndex);
            byIndex ??= [];
            var built = Enumerable.Range(0, byIndex.Count)
                .Select(k => byIndex.TryGetValue(k, out var l) ? l : (FromEdge: "?", FromLane: -1)).ToList();
            if (!built.SequenceEqual(meter.Links))
                problems.Add($"{meter.Id}: its links leave {string.Join(" ", built)}, "
                             + $"the programme was built for {string.Join(" ", meter.Links)}");
        }
        return problems;
    }

    private static string Describe(IEnumerable<(double Duration, string State)> phases)
        => string.Join(" ", phases.Select(p => $"{p.State}:{p.Duration.ToString(CultureInfo.InvariantCulture)}s"));

    private static string ProgramFile(IReadOnlyList<MeteredSignal> metered)
    {
        var document = new XElement("tlLogics");
        foreach (MeteredSignal meter in metered)
        {
            var logic = new XElement("tlLogic",
                new XAttribute("id", meter.Id),
                new XAttribute("type", ProgramType),
                new XAttribute("programID", meter.ProgramId),
                new XAttribute("offset", "0"));
            foreach (MeterPhase phase in meter.Phases)
                logic.Add(new XElement("phase",
                    new XAttribute("duration", phase.DurationSeconds.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("state", phase.State)));
            document.Add(logic);
        }

        // Written with fixed line endings, so one extract gives one file on every platform.
        var text = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            NewLineChars = "\n",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
        };
        using (var writer = XmlWriter.Create(new StringWriterUtf8(text), settings))
        {
            writer.WriteStartDocument();
            writer.WriteComment(" The ramp-meter programmes the world build gives every OSM "
                                + "traffic_signals=ramp_meter signal: CarlaNet.Map.RampMeterProgram. ");
            document.WriteTo(writer);
            writer.WriteEndDocument();
        }
        return text.Append('\n').ToString();
    }

    private sealed record Program(string Type, string ProgramId, List<string> States, List<double> Durations);

    private static Dictionary<string, Program> ProgramsById(XElement root)
    {
        var programs = new Dictionary<string, Program>(StringComparer.Ordinal);
        foreach (XElement logic in root.Elements("tlLogic"))
        {
            if (logic.Attribute("id")?.Value is not string id)
                continue;
            var phases = logic.Elements("phase").ToList();
            programs[id] = new Program(
                logic.Attribute("type")?.Value ?? "",
                logic.Attribute("programID")?.Value ?? "",
                [.. phases.Select(p => p.Attribute("state")?.Value ?? "")],
                [.. phases.Select(p => double.TryParse(p.Attribute("duration")?.Value, NumberStyles.Float,
                                                       CultureInfo.InvariantCulture, out var d) ? d : double.NaN)]);
        }
        return programs;
    }

    // signal id -> link index -> the edge and lane the link leaves. A link leaving an internal edge
    // (an id starting ':') is not a movement off a road -- a pedestrian crossing is one.
    private static Dictionary<string, Dictionary<int, (string FromEdge, int FromLane)>> LinksBySignal(XElement root)
    {
        var links = new Dictionary<string, Dictionary<int, (string, int)>>(StringComparer.Ordinal);
        foreach (XElement connection in root.Elements("connection"))
        {
            if (connection.Attribute("tl")?.Value is not string tl
                || !int.TryParse(connection.Attribute("linkIndex")?.Value, NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int index)
                || connection.Attribute("from")?.Value is not string from
                || !int.TryParse(connection.Attribute("fromLane")?.Value, NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int lane))
                continue;
            if (!links.TryGetValue(tl, out var byIndex))
                links[tl] = byIndex = [];
            byIndex[index] = (from, lane);
        }
        return links;
    }

    // A StringWriter reports UTF-16 as its encoding, which XmlWriter would then declare.
    private sealed class StringWriterUtf8(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
