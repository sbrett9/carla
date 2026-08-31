// Makes a junction carry one surface instead of a pile of independently draped ribbons.
//
// ElevationContinuityInjector reconciles road CONTACTS: ends that a <link> says are shared. Inside
// a junction the connecting roads mostly do not link to each other, they CROSS, so no constraint
// ever related their heights. Each was draped along its own reference line and they meet in the
// middle of the intersection at whatever height the terrain happened to give them, while the mesh
// renders every connector's full width, so the same square metre of pavement is covered several
// times over at several heights. Measured on Arapahoe_I25 before this pass: of 14,698 places where
// two connectors' surfaces overlap, 44% disagreed by more than 5 cm and 10% by more than 20 cm,
// worst 1.49 m. Contact reconciliation scored perfectly throughout, because it was never looking
// at these places: 121 of 133 junctions were affected, so this is the ordinary case, not an edge.
//
// Fitting one plane or one quadratic per junction was measured and is not good enough - a plane
// leaves 30% of overlaps beyond 5 cm. A road is flat across its width (CARLA's parser reads
// <superelevation> into a struct and the map-builder call is commented out, so a connector's
// surface height at any lateral offset is its reference-line height), and two flat ribbons reading
// one sloped surface still differ by the surface gradient times their lateral separation. So this
// solves the heights directly rather than assuming a shape: per junction, the height of every
// connector at every station, wanting overlapping surfaces to agree, ends to stay where contact
// reconciliation put them, the result to stay near the terrain it was sampled from, and no
// connector to kink. That leaves 5.9% of overlaps beyond 5 cm and 0.3% beyond 20 cm, a median of
// 6 mm against 40 mm, and costs 2,000 elevation records across the map.
//
// Holding the ends is what makes this safe to run after contact reconciliation rather than instead
// of it: the junction boundary moves at most 1.7 mm, and all 1292 road contacts stay within the
// 2 mm they were already at. A junction that cannot be resolved without moving its boundary
// further than allowed is left exactly as it was.
//
// Two kinds of residual survive, both of them roads that are genuinely at different heights where
// they overlap, which no arrangement of flat ribbons resolves and which this must not force
// together. Junction 167 is two connectors leaving one point and diverging 1.3 m over 8.8 m: a
// grade separation beginning. Six junctions on the I-25 ramp system hold U-turn connectors, which
// start and finish on the same approach road and so are pinned flat at that road's height while
// sweeping across the middle of the junction, where they cross another U-turn pinned to a road
// arriving up to 0.74 m lower on sloping ground; each is a handful of overlaps out of hundreds
// (junction 25: 4 pairs of 235, median 19 mm). Letting the approach roads move was measured and
// buys little for what it costs - with through roads free to shift, the worst case improves only
// from 0.745 m to 0.588 m - so they are held.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace CarlaNet.Map.OpenDrive;

/// <summary>What a junction surface pass did, for the build log.</summary>
/// <param name="Overlaps">Places where two connectors' surfaces cover the same ground.</param>
/// <param name="JunctionsHeld">Junctions left untouched because the solve wanted to move the
/// junction boundary further than the pass is allowed to.</param>
public readonly record struct JunctionSurfaceSummary(
    int Junctions,
    int JunctionsHeld,
    int Connectors,
    int Overlaps,
    double MedianBeforeMeters,
    double MedianAfterMeters,
    double MaxBeforeMeters,
    double MaxAfterMeters,
    double MaxBoundaryShiftMeters);

public static class JunctionSurfaceReconciler
{
    /// <summary>One connecting road's pavement, as stations along it and probes across it.</summary>
    private sealed class Ribbon
    {
        public required string RoadId { get; init; }
        public required double[] Stations { get; init; }
        public required double[] Heights { get; init; }
        /// <summary>Plan positions of every probe, and the station each belongs to.</summary>
        public required (double X, double Y, int Station)[] Probes { get; init; }
    }

    /// <summary>One row of the least-squares problem: sum(coefficient * height) = value.</summary>
    private readonly record struct Constraint(int[] Columns, double[] Coefficients, double Value);

    /// <inheritdoc cref="Reconcile(string, Road.Map, out JunctionSurfaceSummary, double, double, double, double, double, double, double, double)"/>
    public static string Reconcile(string openDriveXml, Road.Map map)
        => Reconcile(openDriveXml, map, out _);

    /// <summary>
    /// Rewrites the elevation profile of every junction connecting road so that connectors whose
    /// surfaces overlap report the same height there.
    /// </summary>
    /// <param name="map">Parsed geometry, for placing each connector's pavement in plan. Only the
    /// plan view is read, so a map parsed before elevation was injected is fine.</param>
    /// <param name="stepMeters">Spacing of the stations solved for along each connector. This also
    /// sets where agreement is enforced and measured, so coarsening it both leaves more error
    /// between stations and hides it: on Arapahoe_I25, going from 1.5 m to 2.5 m saves 710 records
    /// and doubles the median disagreement, while reporting the same figure.</param>
    /// <param name="probeSpacingMeters">Spacing of the probes across each connector's width.</param>
    /// <param name="overlapRadiusMeters">How close two probes must be in plan to count as covering
    /// the same ground.</param>
    /// <param name="anchorWeight">How hard connector ends are held where contact reconciliation
    /// put them, relative to the weight of one overlap.</param>
    /// <param name="terrainWeight">How hard the result is pulled back to the sampled terrain.</param>
    /// <param name="smoothWeight">How hard a kink along a connector is penalised.</param>
    /// <param name="maxBoundaryShiftMeters">A junction whose solve would move any connector end
    /// further than this is left exactly as it was, rather than disturbing a reconciled contact.</param>
    /// <param name="simplifyToleranceMeters">How far the emitted cubics may sit from the solved
    /// heights. Stations are kept only while the profile strays further than this, so a connector
    /// that came out straight costs two records rather than one per station. Measured on
    /// Arapahoe_I25, tightening this to 2 mm buys 0.3 mm of median agreement for 980 more records,
    /// and loosening it to 10 mm gives back 0.9 mm for 640 fewer: the residual is set by the
    /// geometry rather than by how finely the profile is described.</param>
    public static string Reconcile(
        string openDriveXml,
        Road.Map map,
        out JunctionSurfaceSummary summary,
        double stepMeters = 1.5,
        double probeSpacingMeters = 1.5,
        double overlapRadiusMeters = 0.75,
        double anchorWeight = 30.0,
        double terrainWeight = 0.15,
        double smoothWeight = 1.0,
        double maxBoundaryShiftMeters = 0.02,
        double simplifyToleranceMeters = 0.005)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        ArgumentNullException.ThrowIfNull(map);
        if (stepMeters <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(stepMeters), "step must be positive");
        if (overlapRadiusMeters <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(overlapRadiusMeters), "radius must be positive");

        var doc = XDocument.Parse(openDriveXml.TrimStart('﻿'));
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document", nameof(openDriveXml));

        var byJunction = new SortedDictionary<string, List<XElement>>(StringComparer.Ordinal);
        foreach (var road in root.Elements("road"))
        {
            string junction = (string?)road.Attribute("junction") ?? "-1";
            if (junction == "-1")
                continue;
            if (!byJunction.TryGetValue(junction, out var list))
                byJunction[junction] = list = new List<XElement>();
            list.Add(road);
        }

        var before = new List<double>();
        var after = new List<double>();
        int junctions = 0, held = 0, connectors = 0;
        double maxShift = 0.0;

        foreach (var (_, roads) in byJunction)
        {
            var ribbons = roads.OrderBy(r => (string?)r.Attribute("id"), StringComparer.Ordinal)
                               .Select(r => BuildRibbon(r, map, stepMeters, probeSpacingMeters))
                               .Where(r => r != null)
                               .Select(r => r!)
                               .ToList();
            if (ribbons.Count < 2)
                continue;

            var (offsets, columns) = Layout(ribbons);
            var overlaps = FindOverlaps(ribbons, offsets, overlapRadiusMeters);
            if (overlaps.Count == 0)
                continue;

            var current = new double[columns];
            for (int i = 0; i < ribbons.Count; ++i)
                Array.Copy(ribbons[i].Heights, 0, current, offsets[i], ribbons[i].Heights.Length);

            var solved = Solve(ribbons, overlaps, offsets, columns, current,
                               anchorWeight, terrainWeight, smoothWeight);

            double shift = 0.0;
            for (int i = 0; i < ribbons.Count; ++i)
            {
                int last = ribbons[i].Stations.Length - 1;
                shift = Math.Max(shift, Math.Abs(solved[offsets[i]] - ribbons[i].Heights[0]));
                shift = Math.Max(shift, Math.Abs(solved[offsets[i] + last] - ribbons[i].Heights[last]));
            }

            var wasDisagreeing = overlaps.Select(o => Math.Abs(current[o.A] - current[o.B])).ToList();
            before.AddRange(wasDisagreeing);
            junctions++;
            connectors += ribbons.Count;

            if (shift > maxBoundaryShiftMeters)
            {
                // A reconciled contact is worth more than a junction interior: leave this one alone.
                after.AddRange(wasDisagreeing);
                held++;
                continue;
            }

            maxShift = Math.Max(maxShift, shift);

            // Report what the document ends up saying, not what the solve wanted: the emitted
            // cubics are simplified, so measuring the solution would flatter a loose tolerance.
            var emitted = new double[columns];
            for (int i = 0; i < ribbons.Count; ++i)
            {
                var heights = new double[ribbons[i].Stations.Length];
                Array.Copy(solved, offsets[i], heights, 0, heights.Length);
                var written = Write(roads.First(r => (string?)r.Attribute("id") == ribbons[i].RoadId),
                                    ribbons[i].Stations, heights, simplifyToleranceMeters);
                Array.Copy(written, 0, emitted, offsets[i], written.Length);
            }
            after.AddRange(overlaps.Select(o => Math.Abs(emitted[o.A] - emitted[o.B])));
        }

        summary = new JunctionSurfaceSummary(
            junctions, held, connectors, before.Count,
            Median(before), Median(after),
            before.Count > 0 ? before.Max() : 0.0,
            after.Count > 0 ? after.Max() : 0.0,
            maxShift);
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    // ── the junction's pavement ───────────────────────────────────────────────

    private static Ribbon? BuildRibbon(XElement roadNode, Road.Map map, double step, double probeSpacing)
    {
        if (!uint.TryParse((string?)roadNode.Attribute("id"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint id)
            || !map.Roads.TryGetValue(id, out var road) || road.Length <= 1e-6)
            return null;

        var records = ReadRecords(roadNode);
        if (records.Count == 0)
            return null;

        int intervals = Math.Max(2, (int)Math.Round(road.Length / step));
        var stations = new double[intervals + 1];
        var heights = new double[intervals + 1];
        var probes = new List<(double, double, int)>();

        for (int i = 0; i <= intervals; ++i)
        {
            double s = road.Length * i / intervals;
            stations[i] = s;
            heights[i] = HeightAt(records, s);

            var (left, right) = SuperelevationInjector.DrivingExtent(road, s);
            double width = left + right;
            if (width < 1e-6)
                continue;
            int across = Math.Max(2, (int)Math.Ceiling(width / probeSpacing));
            for (int k = 0; k <= across; ++k)
            {
                // t is left-positive; ApplyLateralOffset moves right, so it takes the negation.
                double t = left - width * k / across;
                var point = Road.Map.GetDirectedPointInNoLaneOffset(road, s);
                point.ApplyLateralOffset((float)(-t));
                probes.Add((point.Location.X, point.Location.Y, i));
            }
        }

        return probes.Count == 0 ? null : new Ribbon
        {
            RoadId = (string)roadNode.Attribute("id")!,
            Stations = stations,
            Heights = heights,
            Probes = probes.ToArray(),
        };
    }

    /// <summary>Pairs of stations on different connectors whose surfaces cover the same ground.</summary>
    private static List<(int A, int B)> FindOverlaps(List<Ribbon> ribbons, int[] offsets, double radius)
    {
        var cells = new Dictionary<(long, long), List<(double X, double Y, int Column, int Ribbon)>>();
        for (int i = 0; i < ribbons.Count; ++i)
        {
            foreach (var (x, y, station) in ribbons[i].Probes)
            {
                var key = ((long)Math.Floor(x / radius), (long)Math.Floor(y / radius));
                if (!cells.TryGetValue(key, out var bucket))
                    cells[key] = bucket = new List<(double, double, int, int)>();
                bucket.Add((x, y, offsets[i] + station, i));
            }
        }

        var found = new HashSet<(int, int)>();
        foreach (var ((cx, cy), bucket) in cells)
        {
            var near = new List<(double X, double Y, int Column, int Ribbon)>();
            for (long dx = -1; dx <= 1; ++dx)
                for (long dy = -1; dy <= 1; ++dy)
                    if (cells.TryGetValue((cx + dx, cy + dy), out var neighbour))
                        near.AddRange(neighbour);

            foreach (var a in bucket)
                foreach (var b in near)
                {
                    if (a.Ribbon >= b.Ribbon)
                        continue;
                    double ex = a.X - b.X, ey = a.Y - b.Y;
                    if (ex * ex + ey * ey <= radius * radius)
                        found.Add((a.Column, b.Column));
                }
        }

        return found.OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToList();
    }

    private static (int[] Offsets, int Columns) Layout(List<Ribbon> ribbons)
    {
        var offsets = new int[ribbons.Count];
        int total = 0;
        for (int i = 0; i < ribbons.Count; ++i)
        {
            offsets[i] = total;
            total += ribbons[i].Stations.Length;
        }
        return (offsets, total);
    }

    // ── the solve ─────────────────────────────────────────────────────────────

    private static double[] Solve(
        List<Ribbon> ribbons, List<(int A, int B)> overlaps, int[] offsets, int columns,
        double[] current, double anchorWeight, double terrainWeight, double smoothWeight)
    {
        var rows = new List<Constraint>(overlaps.Count + columns * 3);

        foreach (var (a, b) in overlaps)
            rows.Add(new Constraint([a, b], [1.0, -1.0], 0.0));

        for (int i = 0; i < ribbons.Count; ++i)
        {
            var ribbon = ribbons[i];
            int last = ribbon.Stations.Length - 1;

            // Hold the ends where contact reconciliation put them.
            foreach (int end in new[] { 0, last })
                rows.Add(new Constraint([offsets[i] + end], [anchorWeight],
                                        anchorWeight * ribbon.Heights[end]));

            // Stay near the ground the connector was sampled from.
            for (int k = 0; k <= last; ++k)
                rows.Add(new Constraint([offsets[i] + k], [terrainWeight],
                                        terrainWeight * ribbon.Heights[k]));

            // A connector may bend to meet its neighbours, but not kink.
            for (int k = 1; k < last; ++k)
                rows.Add(new Constraint(
                    [offsets[i] + k - 1, offsets[i] + k, offsets[i] + k + 1],
                    [smoothWeight, -2.0 * smoothWeight, smoothWeight], 0.0));
        }

        return SolveNormalEquations(rows, columns, current);
    }

    /// <summary>
    /// Conjugate gradient on the normal equations, Jacobi preconditioned. The problem is a few
    /// hundred heights with three or fewer terms per row, so an iterative solve costs nothing and
    /// avoids carrying a dense factorisation.
    /// </summary>
    private static double[] SolveNormalEquations(
        IReadOnlyList<Constraint> rows, int columns, double[] initial)
    {
        var x = (double[])initial.Clone();
        var inverseDiagonal = new double[columns];
        foreach (var row in rows)
            for (int k = 0; k < row.Columns.Length; ++k)
                inverseDiagonal[row.Columns[k]] += row.Coefficients[k] * row.Coefficients[k];
        for (int j = 0; j < columns; ++j)
            inverseDiagonal[j] = inverseDiagonal[j] > 1e-12 ? 1.0 / inverseDiagonal[j] : 1.0;

        double[] Residual(double[] at)
        {
            var result = new double[columns];
            foreach (var row in rows)
            {
                double dot = 0.0;
                for (int k = 0; k < row.Columns.Length; ++k)
                    dot += row.Coefficients[k] * at[row.Columns[k]];
                double e = row.Value - dot;
                for (int k = 0; k < row.Columns.Length; ++k)
                    result[row.Columns[k]] += row.Coefficients[k] * e;
            }
            return result;
        }

        double[] Normal(double[] direction)
        {
            var result = new double[columns];
            foreach (var row in rows)
            {
                double dot = 0.0;
                for (int k = 0; k < row.Columns.Length; ++k)
                    dot += row.Coefficients[k] * direction[row.Columns[k]];
                for (int k = 0; k < row.Columns.Length; ++k)
                    result[row.Columns[k]] += row.Coefficients[k] * dot;
            }
            return result;
        }

        var r = Residual(x);
        var z = new double[columns];
        for (int j = 0; j < columns; ++j)
            z[j] = r[j] * inverseDiagonal[j];
        var p = (double[])z.Clone();
        double rz = Dot(r, z);
        double target = Math.Sqrt(Dot(r, r)) * 1e-10 + 1e-12;

        for (int iteration = 0; iteration < 4 * columns + 100 && rz > 0.0; ++iteration)
        {
            var q = Normal(p);
            double pq = Dot(p, q);
            if (pq <= 1e-300)
                break;
            double alpha = rz / pq;
            for (int j = 0; j < columns; ++j)
            {
                x[j] += alpha * p[j];
                r[j] -= alpha * q[j];
            }
            if (Math.Sqrt(Dot(r, r)) <= target)
                break;
            for (int j = 0; j < columns; ++j)
                z[j] = r[j] * inverseDiagonal[j];
            double next = Dot(r, z);
            double beta = next / rz;
            for (int j = 0; j < columns; ++j)
                p[j] = z[j] + beta * p[j];
            rz = next;
        }
        return x;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0.0;
        for (int j = 0; j < a.Length; ++j)
            sum += a[j] * b[j];
        return sum;
    }

    // ── writing it back ───────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the road's elevation profile with cubics through the solved heights, keeping only
    /// the stations the profile needs to stay within <paramref name="tolerance"/> of them.
    /// Returns what the emitted profile reports at each station.
    /// </summary>
    private static double[] Write(XElement roadNode, double[] stations, double[] heights, double tolerance)
    {
        var kept = new SortedSet<int> { 0, stations.Length - 1 };
        XElement profile;
        List<Record> records;
        while (true)
        {
            profile = ElevationInjector.BuildElevationProfile(
                kept.Select(i => (S: stations[i], Z: heights[i], Raised: false)).ToList(),
                ElevationFitMode.ShapePreservingCubic);

            // Measure against the curve that will actually be written, not against a chord.
            records = ReadRecords(profile);
            int worst = -1;
            double worstError = tolerance;
            for (int i = 0; i < stations.Length; ++i)
            {
                if (kept.Contains(i))
                    continue;
                double error = Math.Abs(HeightAt(records, stations[i]) - heights[i]);
                if (error > worstError)
                {
                    worstError = error;
                    worst = i;
                }
            }
            if (worst < 0 || !kept.Add(worst))
                break;
        }

        ElevationInjector.ReplaceElevationProfile(roadNode, profile);
        return stations.Select(s => HeightAt(records, s)).ToArray();
    }

    // ── elevation records ─────────────────────────────────────────────────────

    private readonly record struct Record(double S, double A, double B, double C, double D);

    private static List<Record> ReadRecords(XElement roadOrProfile)
    {
        var profile = roadOrProfile.Name == "elevationProfile"
            ? roadOrProfile
            : roadOrProfile.Element("elevationProfile");
        if (profile == null)
            return new List<Record>();
        return profile.Elements("elevation")
            .Select(e => new Record(Num(e, "s"), Num(e, "a"), Num(e, "b"), Num(e, "c"), Num(e, "d")))
            .OrderBy(r => r.S)
            .ToList();
    }

    private static double Num(XElement e, string name)
        => double.Parse((string?)e.Attribute(name) ?? "0", CultureInfo.InvariantCulture);

    private static double HeightAt(List<Record> records, double s)
    {
        if (records.Count == 0)
            return 0.0;
        var chosen = records[0];
        foreach (var record in records)
        {
            if (record.S <= s + 1e-9)
                chosen = record;
            else
                break;
        }
        double ds = s - chosen.S;
        return chosen.A + chosen.B * ds + chosen.C * ds * ds + chosen.D * ds * ds * ds;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;
        var ordered = values.OrderBy(v => v).ToList();
        int middle = ordered.Count / 2;
        return ordered.Count % 2 == 1 ? ordered[middle] : 0.5 * (ordered[middle - 1] + ordered[middle]);
    }
}
