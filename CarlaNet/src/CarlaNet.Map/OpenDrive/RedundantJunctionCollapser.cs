// Merges away junctions that offer no choice of route.
//
// netconvert wraps every surviving OSM node in a junction. Where a node exists only because
// two ways meet with differing attributes — a lane count changing, a name changing — the
// result is a "junction" with a single incoming road and a single connecting road: no turn,
// no choice, just one road continuing into the next through a short connector.
//
// Those cost accuracy rather than just tidiness. netconvert places a connector's reference
// line at the lanes it serves, so where the lane count changes the connector sits a whole
// lane width off the road it continues, and each of the three roads is draped independently
// along its own reference line. Two artificial seams appear where there is really one
// continuous carriageway, and on a cross-sloped surface the heights either side of them
// disagree. Collapsing the three roads into one removes the seams outright, and because it
// runs before the terrain is sampled, everything downstream sees the merged geometry.
//
// The merge is a concatenation: the connector's and the following road's records are appended
// to the first road with their s shifted, and the three lane sections become three sections of
// one road. The connector's lane links already name the lane ids of the roads either side, so
// they carry over unchanged and supply the links for the neighbouring sections.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace CarlaNet.Map.OpenDrive;

/// <summary>What a collapse pass did, for the build log.</summary>
public readonly record struct JunctionCollapseSummary(
    int JunctionsExamined, int Collapsed, int SkippedNotSimple, int SkippedLaneMismatch,
    int SkippedSignalised);

public static class RedundantJunctionCollapser
{
    /// <summary>Record elements carrying an s coordinate that must shift when roads are joined.</summary>
    private static readonly (string Container, string Child)[] SShifted =
    [
        ("planView", "geometry"),
        ("elevationProfile", "elevation"),
        ("lateralProfile", "superelevation"),
        ("lateralProfile", "crossfall"),
        ("lateralProfile", "shape"),
        ("lanes", "laneSection"),
        ("objects", "object"),
        ("objects", "objectReference"),
        ("objects", "tunnel"),
        ("objects", "bridge"),
        ("signals", "signal"),
        ("signals", "signalReference"),
    ];

    /// <inheritdoc cref="Collapse(string, out JunctionCollapseSummary)"/>
    public static string Collapse(string openDriveXml) => Collapse(openDriveXml, out _);

    /// <summary>
    /// Joins each road-connector-road triple that sits either side of a junction with one
    /// incoming road and one connecting road. Iterates, so a chain of such junctions collapses
    /// into a single road. Junctions that offer any choice of route are left alone.
    /// </summary>
    public static string Collapse(string openDriveXml, out JunctionCollapseSummary summary)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        // netconvert writes a byte-order mark, which survives any decode that does not strip
        // it and makes the parser reject the document at position 1.
        var doc = XDocument.Parse(openDriveXml.TrimStart('﻿'));
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document", nameof(openDriveXml));

        // Counted up front so the post-condition can prove nothing to do with traffic control was
        // lost. Signal heads ride onto the merged road with the rest of a road's records, and a
        // signalised junction is never collapsed, so both totals must come out unchanged.
        int signalsBefore = root.Descendants("signal").Count();
        int controllersBefore = root.Elements("controller").Count();
        int junctionControllersBefore = root.Elements("junction").Elements("controller").Count();

        int examined = 0, collapsed = 0, notSimple = 0, laneMismatch = 0, signalised = 0;
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            examined = 0;
            notSimple = 0;
            laneMismatch = 0;
            signalised = 0;
            var roads = root.Elements("road").ToDictionary(x => (string)x.Attribute("id")!);

            foreach (var junction in root.Elements("junction").ToList())
            {
                ++examined;
                // A junction that controls traffic lights must survive: its <controller> children
                // carry the phase programs and the link binding the signal heads to this junction,
                // and deleting it orphans every light it drives even though the heads themselves
                // move safely onto the merged road. On Arapahoe_I25 five of the shortest
                // pass-through junctions are signalised, so this is not a hypothetical.
                if (junction.Elements("controller").Any())
                {
                    ++signalised;
                    continue;
                }

                var connections = junction.Elements("connection").ToList();
                var incoming = connections.Select(c => (string?)c.Attribute("incomingRoad")).Distinct().ToList();
                var connecting = connections.Select(c => (string?)c.Attribute("connectingRoad")).Distinct().ToList();
                if (incoming.Count != 1 || connecting.Count != 1)
                {
                    ++notSimple;
                    continue;
                }

                if (!roads.TryGetValue(incoming[0] ?? "", out var before)
                    || !roads.TryGetValue(connecting[0] ?? "", out var link))
                {
                    ++notSimple;
                    continue;
                }

                var successor = link.Element("link")?.Element("successor");
                if (successor == null || (string?)successor.Attribute("elementType") != "road"
                    || !roads.TryGetValue((string?)successor.Attribute("elementId") ?? "", out var after))
                {
                    ++notSimple;
                    continue;
                }

                // Only a plain continuation: both outer roads ordinary, both meeting this
                // junction end-to-start, and not the same road looping back on itself.
                if (before == after
                    || (string?)before.Attribute("junction") != "-1"
                    || (string?)after.Attribute("junction") != "-1"
                    || !MeetsJunction(before, "successor", junction)
                    || !MeetsJunction(after, "predecessor", junction)
                    || (string?)successor.Attribute("contactPoint") != "start"
                    || (string?)link.Element("link")?.Element("predecessor")?.Attribute("contactPoint") != "end")
                {
                    ++notSimple;
                    continue;
                }

                if (!LaneLinksResolve(before, link, after))
                {
                    ++laneMismatch;
                    continue;
                }

                Merge(root, before, link, after, junction);
                ++collapsed;
                progressed = true;
                break; // ids and links have moved; rebuild the index before continuing
            }
        }

        summary = new JunctionCollapseSummary(examined, collapsed, notSimple, laneMismatch, signalised);
        if (collapsed > 0)
        {
            AssertReferencesResolve(root);
            AssertTrafficControlIntact(root, signalsBefore, controllersBefore, junctionControllersBefore);
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Nothing that drives a traffic light may be lost. Signal heads move with the road records
    /// they sit on, and a junction carrying a controller is never collapsed, so all three totals
    /// must survive a merge; a shortfall means a signalised junction was dropped and its lights
    /// orphaned, which parses cleanly and then flashes a whole intersection green.
    /// </summary>
    private static void AssertTrafficControlIntact(
        XElement root, int signalsBefore, int controllersBefore, int junctionControllersBefore)
    {
        int signals = root.Descendants("signal").Count();
        int controllers = root.Elements("controller").Count();
        int junctionControllers = root.Elements("junction").Elements("controller").Count();
        if (signals >= signalsBefore && controllers >= controllersBefore
            && junctionControllers >= junctionControllersBefore)
            return;
        throw new InvalidOperationException(
            $"collapsing redundant junctions lost traffic control: signals {signalsBefore} -> {signals}, "
            + $"controllers {controllersBefore} -> {controllers}, "
            + $"junction controller links {junctionControllersBefore} -> {junctionControllers}");
    }

    /// <summary>
    /// Every road and junction named by a link must still exist. Merging rewrites ids across
    /// the whole document, so a missed reference would leave a map that parses but routes into
    /// nothing; failing here is far better than shipping that.
    /// </summary>
    private static void AssertReferencesResolve(XElement root)
    {
        var roadIds = root.Elements("road").Select(x => (string?)x.Attribute("id")).ToHashSet();
        var junctionIds = root.Elements("junction").Select(x => (string?)x.Attribute("id")).ToHashSet();
        var dangling = new List<string>();

        foreach (var road in root.Elements("road"))
        {
            string id = (string?)road.Attribute("id") ?? "?";
            foreach (var which in new[] { "predecessor", "successor" })
            {
                var e = road.Element("link")?.Element(which);
                if (e == null) continue;
                var type = (string?)e.Attribute("elementType");
                var target = (string?)e.Attribute("elementId");
                bool ok = type == "road" ? roadIds.Contains(target)
                        : type == "junction" ? junctionIds.Contains(target)
                        : true;
                if (!ok) dangling.Add($"road {id} {which} -> {type} {target}");
            }
            var junction = (string?)road.Attribute("junction");
            if (junction != null && junction != "-1" && !junctionIds.Contains(junction))
                dangling.Add($"road {id} claims junction {junction}");
        }
        foreach (var junction in root.Elements("junction"))
        {
            string id = (string?)junction.Attribute("id") ?? "?";
            foreach (var connection in junction.Elements("connection"))
            {
                foreach (var attribute in new[] { "incomingRoad", "connectingRoad" })
                {
                    var target = (string?)connection.Attribute(attribute);
                    if (target != null && !roadIds.Contains(target))
                        dangling.Add($"junction {id} {attribute} -> road {target}");
                }
            }
        }

        if (dangling.Count > 0)
            throw new InvalidOperationException(
                "collapsing redundant junctions left " + dangling.Count + " dangling reference(s): "
                + string.Join("; ", dangling.Take(10)));
    }

    private static bool MeetsJunction(XElement road, string which, XElement junction)
    {
        var e = road.Element("link")?.Element(which);
        return e != null
            && (string?)e.Attribute("elementType") == "junction"
            && (string?)e.Attribute("elementId") == (string?)junction.Attribute("id");
    }

    private static double Length(XElement road)
        => double.Parse((string)road.Attribute("length")!, CultureInfo.InvariantCulture);

    private static IEnumerable<XElement> DrivingLanes(XElement road)
        => road.Elements("lanes").Elements("laneSection").Take(1)
               .Elements().Where(side => side.Name == "left" || side.Name == "right")
               .Elements("lane");

    /// <summary>
    /// Every lane of the connector must name a lane that exists on the road either side, so
    /// the merged road's sections can be linked without inventing a correspondence.
    /// </summary>
    private static bool LaneLinksResolve(XElement before, XElement link, XElement after)
    {
        var beforeIds = DrivingLanes(before).Select(l => (string?)l.Attribute("id")).ToHashSet();
        var afterIds = DrivingLanes(after).Select(l => (string?)l.Attribute("id")).ToHashSet();
        foreach (var lane in DrivingLanes(link))
        {
            var p = (string?)lane.Element("link")?.Element("predecessor")?.Attribute("id");
            var s = (string?)lane.Element("link")?.Element("successor")?.Attribute("id");
            if (p == null || s == null || !beforeIds.Contains(p) || !afterIds.Contains(s))
                return false;
        }
        return DrivingLanes(link).Any();
    }

    private static void Merge(XElement root, XElement before, XElement link, XElement after, XElement junction)
    {
        double lenBefore = Length(before), lenLink = Length(link), lenAfter = Length(after);

        // Link the outer roads' lanes to the connector's, using the correspondence the
        // connector already records. Done before the sections are appended, while the three
        // roads are still separate.
        var linkLanes = DrivingLanes(link).ToList();
        foreach (var lane in DrivingLanes(before))
        {
            var id = (string?)lane.Attribute("id");
            var match = linkLanes.FirstOrDefault(
                l => (string?)l.Element("link")?.Element("predecessor")?.Attribute("id") == id);
            if (match != null)
                SetLaneLink(lane, "successor", (string)match.Attribute("id")!);
        }
        foreach (var lane in DrivingLanes(after))
        {
            var id = (string?)lane.Attribute("id");
            var match = linkLanes.FirstOrDefault(
                l => (string?)l.Element("link")?.Element("successor")?.Attribute("id") == id);
            if (match != null)
                SetLaneLink(lane, "predecessor", (string)match.Attribute("id")!);
        }

        AppendShifted(before, link, lenBefore);
        AppendShifted(before, after, lenBefore + lenLink);

        before.SetAttributeValue("length",
            (lenBefore + lenLink + lenAfter).ToString("R", CultureInfo.InvariantCulture));
        if (string.IsNullOrEmpty((string?)before.Attribute("name")))
            before.SetAttributeValue("name", (string?)after.Attribute("name"));

        // The merged road now ends where the following road ended.
        var beforeLink = before.Element("link") ?? new XElement("link");
        beforeLink.Element("successor")?.Remove();
        var afterSuccessor = after.Element("link")?.Element("successor");
        if (afterSuccessor != null)
            beforeLink.Add(new XElement(afterSuccessor));

        string mergedId = (string)before.Attribute("id")!;
        string goneId = (string)after.Attribute("id")!;
        RewriteReferences(root, goneId, mergedId);

        link.Remove();
        after.Remove();
        junction.Remove();
    }

    private static void SetLaneLink(XElement lane, string which, string targetId)
    {
        var linkElement = lane.Element("link");
        if (linkElement == null)
        {
            linkElement = new XElement("link");
            lane.AddFirst(linkElement);
        }
        linkElement.Element(which)?.Remove();
        linkElement.Add(new XElement(which, new XAttribute("id", targetId)));
    }

    /// <summary>Copies one road's s-indexed records onto another, offset along it.</summary>
    private static void AppendShifted(XElement target, XElement source, double offset)
    {
        foreach (var (containerName, childName) in SShifted)
        {
            var sourceContainer = source.Element(containerName);
            if (sourceContainer == null)
                continue;
            var children = sourceContainer.Elements(childName).ToList();
            if (children.Count == 0)
                continue;

            var targetContainer = target.Element(containerName);
            if (targetContainer == null)
            {
                targetContainer = new XElement(containerName);
                target.Add(targetContainer);
            }
            foreach (var child in children)
            {
                var copy = new XElement(child);
                var s = copy.Attribute("s");
                if (s != null)
                {
                    double value = double.Parse(s.Value, CultureInfo.InvariantCulture) + offset;
                    copy.SetAttributeValue("s", value.ToString("R", CultureInfo.InvariantCulture));
                }
                targetContainer.Add(copy);
            }
        }

        // <type> is a direct child of <road> rather than living in a container.
        foreach (var type in source.Elements("type").ToList())
        {
            var copy = new XElement(type);
            var s = copy.Attribute("s");
            if (s != null)
            {
                double value = double.Parse(s.Value, CultureInfo.InvariantCulture) + offset;
                copy.SetAttributeValue("s", value.ToString("R", CultureInfo.InvariantCulture));
            }
            var last = target.Elements("type").LastOrDefault();
            if (last != null) last.AddAfterSelf(copy); else target.AddFirst(copy);
        }
    }

    /// <summary>
    /// Points everything that named the absorbed road at the merged one. Contact points are
    /// left alone: the absorbed road's end is now the merged road's end, and its start faced
    /// only the junction being removed.
    /// </summary>
    private static void RewriteReferences(XElement root, string goneId, string mergedId)
    {
        foreach (var road in root.Elements("road"))
        {
            foreach (var which in new[] { "predecessor", "successor" })
            {
                var e = road.Element("link")?.Element(which);
                if (e != null && (string?)e.Attribute("elementType") == "road"
                    && (string?)e.Attribute("elementId") == goneId)
                    e.SetAttributeValue("elementId", mergedId);
            }
        }
        foreach (var connection in root.Elements("junction").Elements("connection"))
        {
            foreach (var attribute in new[] { "incomingRoad", "connectingRoad", "linkedRoad" })
            {
                if ((string?)connection.Attribute(attribute) == goneId)
                    connection.SetAttributeValue(attribute, mergedId);
            }
        }
    }
}
