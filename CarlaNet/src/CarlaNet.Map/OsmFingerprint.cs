// A canonical identity for an OpenStreetMap extract, computed over the parsed document.
//
// A world package records which extract the world was built from. A digest of the file's bytes
// cannot do that job: the extract a world is built from is not the file a person downloaded but the
// clipped one the pipeline writes, and two clips that hold the same graph are free to serialise it
// differently. That was literally true here -- the clipper emitted its nodes in set-iteration
// order, so one extract clipped in four processes gave four files and one graph, and every
// SourceOsmSha256 ever recorded named a file that no longer existed anywhere.
//
// So the digest is taken over what the extract says: the bounds, the nodes with their coordinates
// and tags, the ways with their node references and tags, and the relations with their members and
// tags. Rows are sorted, so element order cannot matter.
//
// Excluded, by reading only the attributes named above: OSM's edit bookkeeping -- `version`,
// `timestamp`, `changeset`, `uid`, `user`, `visible` -- and the `generator` attribute on the root.
// None of them can change a road graph, and two exports of identical geometry differ in them.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CarlaNet.Map;

/// <summary>
/// Computes the canonical fingerprint of an OSM extract: a SHA-256 over the parsed document,
/// stable against the order its elements happen to be written in.
/// </summary>
public static class OsmFingerprint
{
    /// <summary>The fingerprint of an OSM document, as lowercase hexadecimal SHA-256.</summary>
    public static string Compute(string osmXml)
    {
        ArgumentNullException.ThrowIfNull(osmXml);
        XElement root = XDocument.Parse(osmXml).Root
            ?? throw new InvalidDataException("the OSM document is empty");

        var rows = new List<byte[]>();

        XElement? bounds = root.Element("bounds");
        rows.Add(CanonicalDigest.Row("B",
                                     bounds?.Attribute("minlat")?.Value,
                                     bounds?.Attribute("minlon")?.Value,
                                     bounds?.Attribute("maxlat")?.Value,
                                     bounds?.Attribute("maxlon")?.Value));

        var elements = new List<byte[]>();
        foreach (XElement node in root.Elements("node"))
        {
            var fields = new List<string?>
            {
                "N", node.Attribute("id")?.Value,
                node.Attribute("lat")?.Value, node.Attribute("lon")?.Value,
            };
            fields.AddRange(Tags(node));
            elements.Add(CanonicalDigest.Row([.. fields]));
        }
        foreach (XElement way in root.Elements("way"))
        {
            var fields = new List<string?> { "W", way.Attribute("id")?.Value };
            // Node references are in document order: a way is a sequence, and reversing it reverses
            // the road.
            fields.AddRange(way.Elements("nd").Select(n => "nd=" + n.Attribute("ref")?.Value));
            fields.AddRange(Tags(way));
            elements.Add(CanonicalDigest.Row([.. fields]));
        }
        foreach (XElement relation in root.Elements("relation"))
        {
            var fields = new List<string?> { "R", relation.Attribute("id")?.Value };
            // Members are in document order too: which way is `from` and which is `to` in a turn
            // restriction is carried by the roles, but the order is part of the record.
            fields.AddRange(relation.Elements("member").Select(
                m => $"member={m.Attribute("type")?.Value}:{m.Attribute("ref")?.Value}"
                     + $":{m.Attribute("role")?.Value}"));
            fields.AddRange(Tags(relation));
            elements.Add(CanonicalDigest.Row([.. fields]));
        }

        // Sorted by the encoded row so the order elements were written in cannot matter. Sorting on
        // the row rather than on a parsed id also means an element with no id is handled rather
        // than throwing.
        elements.Sort(CanonicalDigest.ByteOrder.Instance.Compare);
        rows.AddRange(elements);
        return CanonicalDigest.Of(rows);
    }

    /// <summary>The fingerprint of an OSM file on disk, or an empty string when it cannot be read.</summary>
    /// <remarks>
    /// Empty rather than throwing: this is provenance, recorded after a world has already been
    /// built, and losing the record is a lesser harm than discarding the world.
    /// </remarks>
    public static string ComputeFile(string path)
    {
        try
        {
            return Compute(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    /// <summary>An element's tags as <c>tag=key=value</c> fields, in key order.</summary>
    private static IEnumerable<string> Tags(XElement element)
        => element.Elements("tag")
            .Select(t => $"tag={t.Attribute("k")?.Value}={t.Attribute("v")?.Value}")
            .OrderBy(CanonicalDigest.Utf8.GetBytes, CanonicalDigest.ByteOrder.Instance);
}
