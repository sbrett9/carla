// Makes roads meet at the height of the road they join.
//
// Elevation is sampled along each road's own reference line, and netconvert puts a junction
// connecting road's reference line on the lanes it serves rather than on the parent road's
// centreline. A contact that is topologically shared is therefore up to six lanes off to the
// side: measured on Arapahoe_I25 the separations are exact multiples of the default lane width,
// median 3.35 m and up to 20.10 m, and almost purely lateral. The two roads honestly report the
// ground at two different places, and their lanes tear where they abut.
//
// This bends each junction connector so its ends land exactly on the roads it links, blending
// the correction over a transition so a long road is corrected near its end rather than tilted
// along its length, and refitting each segment as a Hermite cubic so the gradient stays
// continuous where it already was. Through roads are left carrying the terrain they were
// sampled from; only the short connectors move.
//
// IMPORTANT: this is only correct while a road is flat across its width. With a flat
// cross-section a road's surface height at any lateral offset equals its reference-line height,
// so making the reference lines agree makes the surfaces agree — which is exactly why
// netconvert's own output is continuous. If <superelevation> is ever emitted, the reference-line
// difference at an offset contact stops being an error and becomes the cross-slope, and forcing
// it to zero then tilts the surfaces apart instead of together. Measured both ways on
// Arapahoe_I25: flat plus this pass leaves 0 of 1292 contacts disagreeing by more than 2 cm,
// crossfall without it leaves 224, and the two combined leave 346. Do not run both.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace CarlaNet.Map.OpenDrive;

/// <summary>What a continuity pass did, for the build log.</summary>
public readonly record struct ElevationContinuitySummary(
    int Constraints, int Iterations, int RoadsBent, double MaxResidualMeters);

public static class ElevationContinuityInjector
{
    private readonly record struct Record(double S, double A, double B, double C, double D)
    {
        public double Height(double s)
        {
            double ds = s - S;
            return A + B * ds + C * ds * ds + D * ds * ds * ds;
        }

        public double Gradient(double s)
        {
            double ds = s - S;
            return B + 2.0 * C * ds + 3.0 * D * ds * ds;
        }
    }

    /// <inheritdoc cref="Reconcile(string, out ElevationContinuitySummary, double, double, int)"/>
    public static string Reconcile(string openDriveXml) => Reconcile(openDriveXml, out _);

    /// <summary>
    /// Adjusts elevation profiles so every pair of linked road ends reports the same height.
    /// Junction connectors are bent in preference to the roads they join; where both sides are
    /// the same kind the correction is split between them. Iterates, so a network whose roads can
    /// disturb each other still settles.
    /// </summary>
    public static string Reconcile(
        string openDriveXml,
        out ElevationContinuitySummary summary,
        double transitionLengthMeters = 30.0,
        double minGapMeters = 0.002,
        int maxIterations = 8)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        var doc = XDocument.Parse(openDriveXml.TrimStart('﻿'));
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document", nameof(openDriveXml));

        var roads = root.Elements("road").ToDictionary(r => (string)r.Attribute("id")!);
        var lengths = roads.ToDictionary(kv => kv.Key,
            kv => double.Parse((string)kv.Value.Attribute("length")!, CultureInfo.InvariantCulture));
        var profiles = roads.ToDictionary(kv => kv.Key, kv => ReadRecords(kv.Value));
        var isConnector = roads.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.Attribute("junction") != "-1");

        var constraints = Constraints(root, roads, lengths);
        var touched = new HashSet<string>();
        double residual = 0.0;
        int iteration = 0;

        for (iteration = 1; iteration <= maxIterations; ++iteration)
        {
            var targets = new Dictionary<(string Road, bool AtEnd), List<double>>();
            residual = 0.0;

            foreach (var ((roadA, endA), (roadB, endB)) in constraints)
            {
                if (profiles[roadA].Count == 0 || profiles[roadB].Count == 0)
                    continue;
                double za = HeightAt(profiles[roadA], endA ? lengths[roadA] : 0.0);
                double zb = HeightAt(profiles[roadB], endB ? lengths[roadB] : 0.0);
                double gap = Math.Abs(za - zb);
                if (gap > residual)
                    residual = gap;
                if (gap < minGapMeters)
                    continue;

                if (isConnector[roadA] && !isConnector[roadB])
                    Add(targets, (roadA, endA), zb);
                else if (isConnector[roadB] && !isConnector[roadA])
                    Add(targets, (roadB, endB), za);
                else
                {
                    double middle = 0.5 * (za + zb);
                    Add(targets, (roadA, endA), middle);
                    Add(targets, (roadB, endB), middle);
                }
            }

            if (targets.Count == 0)
                break;

            foreach (var group in targets.GroupBy(t => t.Key.Road))
            {
                string rid = group.Key;
                double length = lengths[rid];
                if (length <= 1e-6 || profiles[rid].Count == 0)
                    continue;

                double startDelta = 0.0, endDelta = 0.0;
                foreach (var entry in group)
                {
                    double wanted = entry.Value.Average();
                    if (entry.Key.AtEnd)
                        endDelta = wanted - HeightAt(profiles[rid], length);
                    else
                        startDelta = wanted - HeightAt(profiles[rid], 0.0);
                }
                profiles[rid] = ApplyCorrection(profiles[rid], length, startDelta, endDelta, transitionLengthMeters);
                touched.Add(rid);
            }
        }

        foreach (var rid in touched)
            WriteRecords(roads[rid], profiles[rid]);

        summary = new ElevationContinuitySummary(constraints.Count, iteration - 1, touched.Count, residual);
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static void Add(Dictionary<(string, bool), List<double>> targets, (string, bool) key, double value)
    {
        if (!targets.TryGetValue(key, out var list))
            targets[key] = list = new List<double>();
        list.Add(value);
    }

    /// <summary>Every pair of road ends that a road-typed link says must share a height.</summary>
    private static List<((string Road, bool AtEnd), (string Road, bool AtEnd))> Constraints(
        XElement root, Dictionary<string, XElement> roads, Dictionary<string, double> lengths)
    {
        var seen = new HashSet<((string, bool), (string, bool))>();
        var list = new List<((string, bool), (string, bool))>();
        foreach (var road in root.Elements("road"))
        {
            string rid = (string)road.Attribute("id")!;
            foreach (var (which, atEnd) in new[] { ("predecessor", false), ("successor", true) })
            {
                var link = road.Element("link")?.Element(which);
                if (link == null || (string?)link.Attribute("elementType") != "road")
                    continue;
                string other = (string?)link.Attribute("elementId") ?? "";
                if (!roads.ContainsKey(other))
                    continue;
                var pair = new[] { (rid, atEnd), (other, (string?)link.Attribute("contactPoint") == "end") }
                    .OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToArray();
                var key = (pair[0], pair[1]);
                if (seen.Add(key))
                    list.Add(key);
            }
        }
        return list;
    }

    private static List<Record> ReadRecords(XElement road)
        => road.Elements("elevationProfile").Elements("elevation")
               .Select(e => new Record(
                   Num(e, "s"), Num(e, "a"), Num(e, "b"), Num(e, "c"), Num(e, "d")))
               .OrderBy(r => r.S)
               .ToList();

    private static double Num(XElement e, string name)
        => double.Parse((string?)e.Attribute(name) ?? "0", CultureInfo.InvariantCulture);

    private static Record Active(List<Record> records, double s)
    {
        var chosen = records[0];
        foreach (var record in records)
        {
            if (record.S <= s + 1e-9)
                chosen = record;
            else
                break;
        }
        return chosen;
    }

    private static double HeightAt(List<Record> records, double s) => Active(records, s).Height(s);

    private static double GradientAt(List<Record> records, double s) => Active(records, s).Gradient(s);

    /// <summary>The endpoint deltas blended to nothing over the transition. Returns value and slope.</summary>
    private static (double Value, double Slope) Blend(
        double s, double length, double startDelta, double endDelta, double transition)
    {
        double span = Math.Max(Math.Min(transition, length), 1e-6);
        double value = 0.0, slope = 0.0;
        if (startDelta != 0.0 && s < span)
        {
            value += startDelta * (1.0 - s / span);
            slope += -startDelta / span;
        }
        if (endDelta != 0.0 && s > length - span)
        {
            value += endDelta * (1.0 - (length - s) / span);
            slope += endDelta / span;
        }
        return (value, slope);
    }

    private static List<Record> ApplyCorrection(
        List<Record> records, double length, double startDelta, double endDelta, double transition)
    {
        double span = Math.Min(transition, length);
        var breakpoints = new SortedSet<double> { 0.0, length };
        foreach (var record in records)
            if (record.S >= 0.0 && record.S <= length)
                breakpoints.Add(record.S);
        if (startDelta != 0.0)
            breakpoints.Add(Math.Min(span, length));
        if (endDelta != 0.0)
            breakpoints.Add(Math.Max(0.0, length - span));

        var ordered = breakpoints.ToList();
        var output = new List<Record>();
        for (int i = 0; i + 1 < ordered.Count; ++i)
        {
            double left = ordered[i], right = ordered[i + 1], h = right - left;
            if (h <= 1e-6)
                continue;
            double z0 = HeightAt(records, left), g0 = GradientAt(records, left);
            double z1 = HeightAt(records, right), g1 = GradientAt(records, right);
            var (c0, cg0) = Blend(left, length, startDelta, endDelta, transition);
            var (c1, cg1) = Blend(right, length, startDelta, endDelta, transition);
            z0 += c0; g0 += cg0; z1 += c1; g1 += cg1;
            output.Add(new Record(left, z0, g0,
                (3.0 * (z1 - z0) / h - 2.0 * g0 - g1) / h,
                (2.0 * (z0 - z1) / h + g0 + g1) / (h * h)));
        }
        return output.Count > 0 ? output : records;
    }

    private static void WriteRecords(XElement road, List<Record> records)
    {
        var profile = road.Element("elevationProfile");
        if (profile == null)
        {
            profile = new XElement("elevationProfile");
            var planView = road.Element("planView");
            if (planView != null) planView.AddAfterSelf(profile); else road.Add(profile);
        }
        profile.RemoveNodes();
        foreach (var r in records)
            profile.Add(new XElement("elevation",
                new XAttribute("s", Str(r.S)), new XAttribute("a", Str(r.A)),
                new XAttribute("b", Str(r.B)), new XAttribute("c", Str(r.C)),
                new XAttribute("d", Str(r.D))));
    }

    private static string Str(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
