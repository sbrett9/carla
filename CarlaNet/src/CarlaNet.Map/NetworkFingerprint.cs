// A canonical identity for a SUMO road network, computed over the parsed graph rather than the
// bytes of the file that carries it.
//
// A byte digest of a .net.xml identifies nothing useful. netconvert stamps every output with the
// moment it ran and echoes its whole configuration -- including the temporary file paths this
// pipeline hands it -- into a leading comment, so two runs that produce the same road graph produce
// different files. Conversely a file can be re-serialised with its elements in another order, or
// carry street names it did not carry before, without the graph a scenario is authored against
// changing at all.
//
// So the fingerprint is taken over what a scenario actually binds to: the edges and their lanes,
// the junctions, every connection, the signal programmes, and the projection the coordinates are
// in. Each row is sorted, so element order in the document is irrelevant; each field is named, so
// an attribute that appears or disappears changes the answer.
//
// WHAT IS EXCLUDED, and why each one:
//   * the generation timestamp and the echoed configuration block -- they record the run, not the
//     graph, and carry temporary paths that differ on every invocation;
//   * <type> elements -- netconvert's own defaults table, identical for a given version and
//     flag set, and not something a scenario refers to;
//   * street names and original names (`--output.street-names`, `--output.original-names`) -- these
//     are annotation. NOTE that SETTING --output.street-names changes the graph, because
//     NBEdge::expandableBy refuses to merge two differently-named edges; that change shows up here
//     as different edges and lanes, which is the point. The names themselves do not.
//
// WHAT IS INCLUDED that might look surplus:
//   * edges with function="internal" -- the lanes inside a junction. A turn restriction that is
//     honoured and one that is dropped differ ONLY in the internal lanes and the connections that
//     use them, so leaving these out would make the clip's handling of turn restrictions invisible;
//   * <tlLogic> type and phase states -- the world build and the scenario now come out of one
//     netconvert invocation, so the signal programme is fixed when the world is built. A scenario
//     authored against a differently-phased network is a real mismatch and should be loud.
//
// The canonical encoding below is a cross-language contract: `carlacontrol.NetworkFingerprint`
// implements the same thing in Python so the scenario side can check a world package without a
// .NET runtime. Change one and the other must change with it, or a correct package starts being
// refused.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CarlaNet.Map;

/// <summary>
/// Computes the canonical fingerprint of a SUMO <c>.net.xml</c>: a SHA-256 over the parsed graph,
/// stable against re-serialisation, the generation timestamp, and the annotation netconvert's
/// naming options add.
/// </summary>
public static class NetworkFingerprint
{
    // Field and row separators, chosen from the control range so they cannot occur in XML attribute
    // values. The missing-attribute marker is distinct from an empty attribute, so `width=""` and no
    // width at all do not collide.
    private const byte FieldSeparator = 0x1F;
    private const byte RowSeparator = 0x1E;
    private const byte AttributeAbsent = 0x00;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The fingerprint of a network document, as lowercase hexadecimal SHA-256.</summary>
    public static string Compute(string netXml)
    {
        ArgumentNullException.ThrowIfNull(netXml);
        XElement root = XDocument.Parse(netXml).Root
            ?? throw new InvalidDataException("the SUMO network document is empty");
        return Compute(root);
    }

    /// <summary>The fingerprint of a network file on disk.</summary>
    public static string ComputeFile(string path)
        => Compute(File.ReadAllText(path));

    private static string Compute(XElement root)
    {
        var rows = new List<byte[]>();

        // Edges, each immediately followed by its own lanes, both in id order. The edge's `name`
        // (the street name) and the lanes' <param> children (origId) are deliberately not read.
        foreach (XElement edge in SortedBy(root.Elements("edge"), e => Attribute(e, "id")))
        {
            rows.Add(Row("E", Attribute(edge, "id"), Attribute(edge, "from"), Attribute(edge, "to"),
                         Attribute(edge, "function"), Attribute(edge, "priority")));
            foreach (XElement lane in SortedBy(edge.Elements("lane"), l => Attribute(l, "id")))
            {
                rows.Add(Row("L", Attribute(lane, "id"), Attribute(lane, "index"),
                             Attribute(lane, "speed"), Attribute(lane, "length"),
                             Attribute(lane, "width"), Attribute(lane, "allow"),
                             Attribute(lane, "disallow"), Attribute(lane, "shape")));
            }
        }

        foreach (XElement junction in SortedBy(root.Elements("junction"), j => Attribute(j, "id")))
        {
            rows.Add(Row("J", Attribute(junction, "id"), Attribute(junction, "type"),
                         Attribute(junction, "x"), Attribute(junction, "y"),
                         Attribute(junction, "incLanes"), Attribute(junction, "intLanes")));
        }

        // Connections carry no id, so every attribute is read and the rows are ordered by their own
        // encoded content. That also means a connection gaining an attribute in a later netconvert
        // is visible rather than silently ignored.
        var connections = new List<byte[]>();
        foreach (XElement connection in root.Elements("connection"))
        {
            var fields = new List<string?> { "C" };
            foreach (XAttribute attribute in connection.Attributes()
                         .OrderBy(a => Utf8.GetBytes(a.Name.LocalName), ByteOrder.Instance))
            {
                fields.Add(attribute.Name.LocalName + "=" + attribute.Value);
            }
            connections.Add(Row([.. fields]));
        }
        connections.Sort(ByteOrder.Instance.Compare);
        rows.AddRange(connections);

        // Signal programmes: the junction they control, the programme's name, the control type and
        // the ordered phase states. Phase durations are the timing of a programme rather than its
        // structure and are left out; a phase appearing, disappearing or changing which movements it
        // releases is not.
        var programmes = new List<byte[]>();
        foreach (XElement logic in root.Elements("tlLogic"))
        {
            var fields = new List<string?>
            {
                "T", Attribute(logic, "id"), Attribute(logic, "type"), Attribute(logic, "programID"),
            };
            foreach (XElement phase in logic.Elements("phase"))
            {
                fields.Add(Attribute(phase, "state"));
            }
            programmes.Add(Row([.. fields]));
        }
        programmes.Sort(ByteOrder.Instance.Compare);
        rows.AddRange(programmes);

        // The frame the coordinates are in. Two networks with the same topology in different
        // projections are not interchangeable, and a scenario placed by coordinate would land in
        // the wrong place without noticing.
        XElement? location = root.Element("location");
        rows.Add(Row("G",
                     location is null ? null : Attribute(location, "netOffset"),
                     location is null ? null : Attribute(location, "convBoundary"),
                     location is null ? null : Attribute(location, "origBoundary"),
                     location is null ? null : Attribute(location, "projParameter")));

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] row in rows)
        {
            sha.AppendData(row);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>An attribute's value, or null when the element does not carry it.</summary>
    private static string? Attribute(XElement element, string name)
        => element.Attribute(name)?.Value;

    private static IEnumerable<XElement> SortedBy(
        IEnumerable<XElement> elements, Func<XElement, string?> key)
        => elements.OrderBy(e => Utf8.GetBytes(key(e) ?? string.Empty), ByteOrder.Instance);

    /// <summary>One encoded row: the fields, separated, terminated by the row separator.</summary>
    private static byte[] Row(params string?[] fields)
    {
        using var buffer = new MemoryStream();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) { buffer.WriteByte(FieldSeparator); }
            if (fields[i] is null)
            {
                buffer.WriteByte(AttributeAbsent);
            }
            else
            {
                byte[] encoded = Utf8.GetBytes(fields[i]!);
                buffer.Write(encoded, 0, encoded.Length);
            }
        }
        buffer.WriteByte(RowSeparator);
        return buffer.ToArray();
    }

    /// <summary>
    /// Unsigned lexicographic order over raw bytes. Sorting the UTF-8 encoding rather than the
    /// string keeps the order identical in every language that computes this fingerprint; .NET's
    /// default string comparison is culture-aware and its ordinal comparison is over UTF-16 code
    /// units, neither of which Python reproduces for anything outside the ASCII range.
    /// </summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        internal static readonly ByteOrder Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (left is null) { return right is null ? 0 : -1; }
            if (right is null) { return 1; }
            int shared = Math.Min(left.Length, right.Length);
            for (int i = 0; i < shared; i++)
            {
                if (left[i] != right[i]) { return left[i] < right[i] ? -1 : 1; }
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
