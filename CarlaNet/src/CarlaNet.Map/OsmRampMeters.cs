// The ramp meters an OpenStreetMap extract declares.
//
// OpenStreetMap tags a ramp meter as a signal node like any other -- `highway=traffic_signals` -- with
// `traffic_signals=ramp_meter` saying what kind of signal it is. netconvert reads only the first tag,
// so to it a ramp meter is an ordinary signalised junction, and two things follow that are wrong for
// a meter:
//
//   * --junctions.join merges the meter with any junction within the join distance. A meter a few
//     metres short of the merge is joined with the merge and with the freeway node beside it, and
//     the joined junction's guessed programme then gives the freeway a red light every cycle.
//     Measured on Arapahoe I-25: meter 582785322 became cluster_432157733_432193589_582785322,
//     alternating the loop ramp with five lanes of I-25 on 10-50 s greens.
//   * the guessed programme is a junction's -- an 80 s green -- where a meter releases about one
//     vehicle per green on a cycle of a few seconds.
//
// This reads the nodes so the world build can keep them out of junction joining and give each its
// metering programme (OsmConverter, RampMeterProgram).
using System.Xml;

namespace CarlaNet.Map;

/// <summary>
/// Reads the ramp meters of an OSM extract: the nodes tagged both <c>highway=traffic_signals</c> and
/// <c>traffic_signals=ramp_meter</c>.
/// </summary>
public static class OsmRampMeters
{
    /// <summary>The value of <c>traffic_signals</c> that marks a signal as a ramp meter.</summary>
    public const string RampMeterValue = "ramp_meter";

    /// <summary>
    /// The node ids of every ramp meter in the extract, in ordinal order, so the same extract always
    /// gives the same argument list. Empty when the extract has none.
    /// </summary>
    /// <remarks>
    /// Both tags are required. netconvert builds a signal only from <c>highway=traffic_signals</c>, so a
    /// node carrying <c>traffic_signals=ramp_meter</c> alone is not a signal in the network and there is
    /// nothing to meter.
    /// </remarks>
    public static IReadOnlyList<string> Read(string osmPath)
    {
        ArgumentNullException.ThrowIfNull(osmPath);

        var meters = new SortedSet<string>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(osmPath, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "node" || reader.IsEmptyElement)
                continue;
            string? id = reader.GetAttribute("id");
            if (id is null)
                continue;

            bool signal = false, rampMeter = false;
            using (XmlReader tags = reader.ReadSubtree())
            {
                tags.Read(); // position on <node>
                while (tags.Read())
                {
                    if (tags.NodeType != XmlNodeType.Element || tags.Name != "tag")
                        continue;
                    string? key = tags.GetAttribute("k");
                    string? value = tags.GetAttribute("v");
                    if (key == "highway" && value == "traffic_signals")
                        signal = true;
                    else if (key == "traffic_signals" && value == RampMeterValue)
                        rampMeter = true;
                }
            }
            if (signal && rampMeter)
                meters.Add(id);
        }
        return [.. meters];
    }
}
