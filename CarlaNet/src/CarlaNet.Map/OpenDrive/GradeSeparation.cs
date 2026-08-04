// Routes each road's elevation to the surface its OSM layer selects, so a bridge deck and the road
// beneath it stop sharing one height.
//
// Sampling a surface yields ONE height per plan position, so no per-cell rule — raw threshold,
// offset-corrected residual, percentile — can hand two roads at the same (x,y) different heights.
// The distinguishing information is not in the terrain at all; it is in the OSM layer tags. So:
//
//   layer > 0 (deck)   -> the photoreal surface, where the deck IS the topmost thing a vertical
//                         sample hits, giving that structure's real clearance with no constant
//   layer = 0          -> bare earth, which nothing overhead can contaminate
//   layer < 0 / tunnel -> bare earth less a fixed depth; the photoreal cannot see into a bore
//
// Both surfaces are already sampled at every road point, so this file adds only the missing piece:
// which way each .xodr road sample belongs to, and hence which surface it should read.
//
// The .xodr carries no OSM way ids, so the correlation is geometric — the same approach SignInjector
// uses to place signs. Two properties keep it honest at exactly the place it matters, a crossing:
//   * a candidate segment must run roughly PARALLEL to the road sample's tangent, and a road passing
//     under a deck crosses it at an angle;
//   * a .xodr road may only draw its layer from OSM ways CONNECTED to the one it mostly follows.
//     netconvert merges ways into one road only along shared nodes, and the way passing underneath
//     shares none — that is the very definition of the grade separation.
//
// Output is one lift per centreline sample: metres to add to the at-grade surface that height-align
// mode already produces. At-grade roads get 0, so every mode keeps its existing behaviour wherever
// the OSM records no vertical structure.
using System;
using System.Collections.Generic;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.OpenDrive;

/// <summary>Tuning for <see cref="GradeSeparation.Compute"/>. The defaults were chosen against the
/// Arapahoe Ave / I-25 interchange, whose 16 grade-separated crossings span 2.9–6.7 m of real
/// clearance.</summary>
public sealed record GradeSeparationOptions
{
    /// <summary>A centreline sample further than this from every OSM way is left at grade.</summary>
    public double MaxSnapMeters { get; init; } = 12.0;

    /// <summary>A candidate OSM segment whose bearing differs from the road sample's by more than
    /// this (measured without regard to direction of travel) is rejected. This is what stops a road
    /// passing under a deck from adopting the deck's layer.</summary>
    public double MaxBearingDifferenceDegrees { get; init; } = 45.0;

    /// <summary>How far the photoreal must rise above bare earth before it counts as a structure
    /// rather than surface noise. The systematic photoreal-vs-bare-earth offset is already removed
    /// before this test, so it only has to clear the residual spread (0.24 m at Arapahoe).</summary>
    public double MinStructureMeters { get; init; } = 1.5;

    /// <summary>Upper bound on a lift read from the photoreal. Above this the sample is far more
    /// likely to be a building or canopy over the road than a deck, so it is capped rather than
    /// trusted.</summary>
    public double MaxStructureMeters { get; init; } = 15.0;

    /// <summary>Vertical separation assumed per layer step where the surface offers no measurement:
    /// a structure the photogrammetry did not reconstruct, or a tunnel, which it cannot see into.</summary>
    public double FallbackLayerSeparationMeters { get; init; } = 5.0;

    /// <summary>Steepest gradient used to bring an approach road up to a deck end, as a rise/run
    /// fraction. It bounds how far a lift reaches into the connected at-grade network: a 1 m deck
    /// end fades out within 1/grade metres.</summary>
    public double ApproachRampGrade { get; init; } = 0.06;
}

/// <summary>Per-sample elevation lift plus the structures it came from.</summary>
public sealed class GradeSeparationResult
{
    /// <summary>Metres to add to the at-grade surface at each centreline sample, index-aligned with
    /// the samples passed in. 0 wherever the OSM records nothing above or below grade.</summary>
    public required double[] Lift { get; init; }

    /// <summary>The ways carrying a deck, for masking the collision heightfield: one height per
    /// grid cell cannot represent a deck and the road under it, so the heightfield must be anchored
    /// to the lower surface and the deck must carry its own road-mesh collision.</summary>
    public required IReadOnlyList<OsmRoadWay> ElevatedWays { get; init; }

    /// <summary>Ways that pass beneath a higher-layer way at a crossing. Their elevation is pinned
    /// to bare earth and no approach ramp may reach them.</summary>
    public required IReadOnlyList<OsmRoadWay> WaysPassingUnder { get; init; }

    public required int SamplesMatched { get; init; }
    public required int SamplesLifted { get; init; }
    public required int StructuresFromSurface { get; init; }
    public required int StructuresFromFallback { get; init; }
    public required double MaxLiftMeters { get; init; }

    /// <summary>True when nothing in the extract is above or below grade, so every mode's existing
    /// at-grade behaviour is reproduced exactly.</summary>
    public bool IsEmpty => SamplesLifted == 0;
}

public static class GradeSeparation
{
    // How a way's lift is obtained.
    private enum LiftSource
    {
        AtGrade,          // layer 0
        Surface,          // measured from the photoreal, per sample
        FixedSeparation,  // layer x the fallback separation, uniform along the way
    }

    /// <summary>
    /// Works out how far above (or below) the at-grade surface each centreline sample belongs.
    /// </summary>
    /// <param name="map">The parsed flat .xodr the samples were taken from; supplies each sample's
    /// tangent, which disambiguates a deck from the road crossing under it.</param>
    /// <param name="samples">Centreline samples in the CARLA world frame, as
    /// <see cref="ElevationInjector.ExtractCenterlineSamples"/> produced them.</param>
    /// <param name="layers">The OSM ways and their plan crossings, projected against the same origin.</param>
    /// <param name="surfaceHeights">Photoreal (topmost-surface) ellipsoidal height at each sample.</param>
    /// <param name="groundHeights">Bare-earth ellipsoidal height at each sample.</param>
    /// <param name="atGradeOffsetMeters">The height the at-grade road already sits above bare earth
    /// in the caller's height-align mode — the systematic photoreal-vs-bare-earth offset. Removing it
    /// makes a lift the height above the road's own at-grade surface, so a deck ends up at exactly
    /// the photoreal height whichever mode is in use.</param>
    public static GradeSeparationResult Compute(
        Road.Map map,
        IReadOnlyList<CenterlineSample> samples,
        OsmRoadLayers layers,
        IReadOnlyList<double> surfaceHeights,
        IReadOnlyList<double> groundHeights,
        double atGradeOffsetMeters,
        GradeSeparationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(surfaceHeights);
        ArgumentNullException.ThrowIfNull(groundHeights);
        if (surfaceHeights.Count != samples.Count || groundHeights.Count != samples.Count)
            throw new ArgumentException(
                $"height arrays ({surfaceHeights.Count}/{groundHeights.Count}) must match samples ({samples.Count})");

        var opt = options ?? new GradeSeparationOptions();
        var lift = new double[samples.Count];
        var ways = layers.Ways;

        var elevated = new List<OsmRoadWay>();
        var underWays = new List<OsmRoadWay>();
        var underIndices = layers.WaysPassingUnder();
        foreach (int i in underIndices) underWays.Add(ways[i]);

        bool anyLayered = false;
        foreach (var w in ways) if (w.Layer != 0) { anyLayered = true; break; }
        if (!anyLayered || samples.Count == 0)
        {
            return new GradeSeparationResult
            {
                Lift = lift, ElevatedWays = elevated, WaysPassingUnder = underWays,
                SamplesMatched = 0, SamplesLifted = 0,
                StructuresFromSurface = 0, StructuresFromFallback = 0, MaxLiftMeters = 0.0,
            };
        }

        // 1) Snap every sample to the OSM way it runs along.
        var matchedWay = new int[samples.Count];
        var matchedStation = new double[samples.Count];
        MatchSamplesToWays(map, samples, ways, opt, matchedWay, matchedStation);
        RestrictToConnectedWays(samples, ways, matchedWay);

        int matchedCount = 0;
        foreach (int w in matchedWay) if (w >= 0) matchedCount++;

        // 2) Decide, per layered way, where its lift comes from. A deck the photogrammetry
        //    reconstructed carries its own measured clearance; one it missed falls back to a fixed
        //    separation, but only where a crossing proves there is something to clear.
        var wayLiftSource = new LiftSource[ways.Count];
        var wayMeasuredLift = new double[ways.Count];
        for (int i = 0; i < ways.Count; ++i) wayLiftSource[i] = LiftSource.AtGrade;

        var rawLift = new double[samples.Count];
        for (int i = 0; i < samples.Count; ++i)
        {
            double surface = surfaceHeights[i], ground = groundHeights[i];
            rawLift[i] = double.IsFinite(surface) && double.IsFinite(ground)
                ? surface - ground - atGradeOffsetMeters
                : double.NaN;
        }

        var wayHasCrossing = new bool[ways.Count];
        foreach (var c in layers.Crossings)
        {
            wayHasCrossing[c.WayIndexA] = true;
            wayHasCrossing[c.WayIndexB] = true;
        }

        for (int w = 0; w < ways.Count; ++w)
        {
            int layer = ways[w].Layer;
            if (layer == 0) continue;
            if (layer < 0)
            {
                // Nothing above ground records the depth of a bore, so a fixed separation is the
                // only available answer.
                wayLiftSource[w] = LiftSource.FixedSeparation;
                wayMeasuredLift[w] = layer * opt.FallbackLayerSeparationMeters;
                continue;
            }

            double peak = double.NegativeInfinity;
            for (int i = 0; i < samples.Count; ++i)
                if (matchedWay[i] == w && double.IsFinite(rawLift[i]) && rawLift[i] > peak)
                    peak = rawLift[i];

            if (peak >= opt.MinStructureMeters)
            {
                wayLiftSource[w] = LiftSource.Surface;
            }
            else if (wayHasCrossing[w])
            {
                wayLiftSource[w] = LiftSource.FixedSeparation;
                wayMeasuredLift[w] = layer * opt.FallbackLayerSeparationMeters;
            }
            // Otherwise the OSM marks the way as layered but neither the photoreal nor a crossing
            // shows anything to clear, so it stays at grade rather than being lifted on faith.
        }

        // 3) Lift the layered samples.
        int fromSurface = 0, fromFallback = 0;
        for (int w = 0; w < ways.Count; ++w)
        {
            if (wayLiftSource[w] == LiftSource.Surface) fromSurface++;
            else if (wayLiftSource[w] == LiftSource.FixedSeparation) fromFallback++;
            else continue;
            if (ways[w].Layer > 0) elevated.Add(ways[w]);
        }

        for (int i = 0; i < samples.Count; ++i)
        {
            int w = matchedWay[i];
            if (w < 0) continue;
            switch (wayLiftSource[w])
            {
                case LiftSource.Surface:
                    lift[i] = double.IsFinite(rawLift[i])
                        ? Math.Clamp(rawLift[i], 0.0, opt.MaxStructureMeters)
                        : 0.0;
                    break;
                case LiftSource.FixedSeparation:
                    lift[i] = wayMeasuredLift[w];
                    break;
            }
        }

        // 4) Ramp the connected at-grade approaches up to each deck end, so the structure reads as a
        //    hump rather than a step. The reach is bounded by the ramp gradient, and roads that pass
        //    under a deck are excluded outright — they must stay at grade whatever adjoins them.
        ApplyApproachRamps(samples, ways, matchedWay, matchedStation, wayLiftSource, underIndices, opt, lift);

        int lifted = 0;
        double maxLift = 0.0;
        for (int i = 0; i < lift.Length; ++i)
        {
            if (lift[i] != 0.0) lifted++;
            if (Math.Abs(lift[i]) > Math.Abs(maxLift)) maxLift = lift[i];
        }

        Console.WriteLine(
            $"[GradeSeparation] OSM drivable ways={ways.Count} elevated={elevated.Count} " +
            $"grade crossings={layers.Crossings.Count} ways passing under={underWays.Count} | " +
            $"structures from photoreal={fromSurface} from fixed separation={fromFallback} | " +
            $"samples matched={matchedCount}/{samples.Count} lifted={lifted} max lift={maxLift:F2} m");

        return new GradeSeparationResult
        {
            Lift = lift,
            ElevatedWays = elevated,
            WaysPassingUnder = underWays,
            SamplesMatched = matchedCount,
            SamplesLifted = lifted,
            StructuresFromSurface = fromSurface,
            StructuresFromFallback = fromFallback,
            MaxLiftMeters = maxLift,
        };
    }

    // ── sample -> OSM way ────────────────────────────────────────────────────

    private static void MatchSamplesToWays(
        Road.Map map,
        IReadOnlyList<CenterlineSample> samples,
        IReadOnlyList<OsmRoadWay> ways,
        GradeSeparationOptions opt,
        int[] matchedWay,
        double[] matchedStation)
    {
        var index = new SegmentIndex(ways, opt.MaxSnapMeters);
        double maxBearing = opt.MaxBearingDifferenceDegrees * Math.PI / 180.0;
        double maxSnapSq = opt.MaxSnapMeters * opt.MaxSnapMeters;

        for (int i = 0; i < samples.Count; ++i)
        {
            matchedWay[i] = -1;
            var s = samples[i];
            if (!map.Roads.TryGetValue(s.RoadId, out var road) || road.Length <= 0.0)
                continue;

            // planView is +Y=North and the sample frame is -Y=North, so the tangent flips sign.
            double sampleBearing = -Road.Map.GetDirectedPointInNoLaneOffset(road, s.S).Tangent;

            double bestSq = maxSnapSq;
            foreach (var (w, seg) in index.Near(s.X, s.Y))
            {
                var way = ways[w];
                double x0 = way.X[seg], y0 = way.Y[seg];
                double dx = way.X[seg + 1] - x0, dy = way.Y[seg + 1] - y0;
                double segLenSq = dx * dx + dy * dy;
                if (segLenSq <= 1e-12) continue;

                if (BearingDifference(sampleBearing, Math.Atan2(dy, dx)) > maxBearing) continue;

                double t = Math.Clamp(((s.X - x0) * dx + (s.Y - y0) * dy) / segLenSq, 0.0, 1.0);
                double px = x0 + dx * t, py = y0 + dy * t;
                double distSq = (s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py);
                if (distSq >= bestSq) continue;

                bestSq = distSq;
                matchedWay[i] = w;
                matchedStation[i] = way.NodeStation[seg] + Math.Sqrt(segLenSq) * t;
            }
        }
    }

    /// Smallest angle between two bearings ignoring direction of travel, in radians [0, pi/2].
    private static double BearingDifference(double a, double b)
    {
        double d = Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));
        return d > Math.PI / 2.0 ? Math.PI - d : d;
    }

    // A .xodr road is one continuous run of the OSM network, so every way it touches must be
    // reachable from the way it mostly follows through shared nodes. Anything else near it — above
    // all the way it crosses at a grade separation, which shares no node with it by definition — is
    // dropped, so no road can inherit the layer of a structure it merely passes beneath.
    private static void RestrictToConnectedWays(
        IReadOnlyList<CenterlineSample> samples,
        IReadOnlyList<OsmRoadWay> ways,
        int[] matchedWay)
    {
        var perRoad = new Dictionary<RoadId, Dictionary<int, int>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            if (matchedWay[i] < 0) continue;
            if (!perRoad.TryGetValue(samples[i].RoadId, out var hits))
                perRoad[samples[i].RoadId] = hits = new Dictionary<int, int>();
            hits[matchedWay[i]] = hits.GetValueOrDefault(matchedWay[i]) + 1;
        }

        var keep = new Dictionary<RoadId, HashSet<int>>();
        foreach (var (roadId, hits) in perRoad)
        {
            int primary = -1, bestHits = 0;
            foreach (var (w, n) in hits)
                if (n > bestHits) { bestHits = n; primary = w; }

            var reachable = new HashSet<int> { primary };
            var frontier = new Queue<int>();
            frontier.Enqueue(primary);
            while (frontier.Count > 0)
            {
                var from = ways[frontier.Dequeue()];
                foreach (var (candidate, _) in hits)
                {
                    if (reachable.Contains(candidate)) continue;
                    if (!SharesNode(from, ways[candidate])) continue;
                    reachable.Add(candidate);
                    frontier.Enqueue(candidate);
                }
            }
            keep[roadId] = reachable;
        }

        for (int i = 0; i < samples.Count; ++i)
            if (matchedWay[i] >= 0 && !keep[samples[i].RoadId].Contains(matchedWay[i]))
                matchedWay[i] = -1;
    }

    private static bool SharesNode(OsmRoadWay a, OsmRoadWay b)
    {
        foreach (var id in a.NodeIds)
            foreach (var other in b.NodeIds)
                if (string.Equals(id, other, StringComparison.Ordinal))
                    return true;
        return false;
    }

    // ── approach ramps ───────────────────────────────────────────────────────

    // Spread each deck end's lift into the at-grade ways that share the node, decaying at the ramp
    // gradient, so an approach climbs to meet the deck instead of stepping up to it. The decay is a
    // shortest-path relaxation over the OSM node graph: a node's lift is the largest any source can
    // still deliver after paying the gradient over the distance travelled, which guarantees the
    // resulting ramp is never steeper than the gradient however the network branches.
    private static void ApplyApproachRamps(
        IReadOnlyList<CenterlineSample> samples,
        IReadOnlyList<OsmRoadWay> ways,
        int[] matchedWay,
        double[] matchedStation,
        LiftSource[] wayLiftSource,
        HashSet<int> waysPassingUnder,
        GradeSeparationOptions opt,
        double[] lift)
    {
        if (opt.ApproachRampGrade <= 0.0) return;

        // Per layered way, the lift at each of its vertices, interpolated from its own samples.
        var nodeLift = new Dictionary<string, double>(StringComparer.Ordinal);
        var perWaySamples = new Dictionary<int, List<(double Station, double Lift)>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            int w = matchedWay[i];
            if (w < 0 || wayLiftSource[w] == LiftSource.AtGrade) continue;
            if (!perWaySamples.TryGetValue(w, out var list))
                perWaySamples[w] = list = new List<(double, double)>();
            list.Add((matchedStation[i], lift[i]));
        }

        foreach (var (w, list) in perWaySamples)
        {
            list.Sort((a, b) => a.Station.CompareTo(b.Station));
            var way = ways[w];
            for (int v = 0; v < way.VertexCount; ++v)
            {
                double value = InterpolateByStation(list, way.NodeStation[v]);
                if (Math.Abs(value) <= Math.Abs(nodeLift.GetValueOrDefault(way.NodeIds[v])))
                    continue;
                nodeLift[way.NodeIds[v]] = value;
            }
        }
        if (nodeLift.Count == 0) return;

        // The at-grade network the ramp may travel over. A way passing under a deck is excluded so
        // neither its own samples nor anything routed through it can be lifted.
        var adjacency = new Dictionary<string, List<(string Node, double Length)>>(StringComparer.Ordinal);
        for (int w = 0; w < ways.Count; ++w)
        {
            if (wayLiftSource[w] != LiftSource.AtGrade || waysPassingUnder.Contains(w)) continue;
            var way = ways[w];
            for (int v = 0; v + 1 < way.VertexCount; ++v)
            {
                double len = way.NodeStation[v + 1] - way.NodeStation[v];
                Link(way.NodeIds[v], way.NodeIds[v + 1], len);
                Link(way.NodeIds[v + 1], way.NodeIds[v], len);
            }
        }

        void Link(string from, string to, double length)
        {
            if (!adjacency.TryGetValue(from, out var list))
                adjacency[from] = list = new List<(string, double)>();
            list.Add((to, length));
        }

        var best = new Dictionary<string, double>(nodeLift, StringComparer.Ordinal);
        var queue = new PriorityQueue<string, double>();
        foreach (var (node, value) in nodeLift)
            queue.Enqueue(node, -Math.Abs(value));

        while (queue.TryDequeue(out var node, out double negMagnitude))
        {
            double value = best.GetValueOrDefault(node);
            if (Math.Abs(value) < -negMagnitude - 1e-9) continue;   // superseded by a stronger source
            if (!adjacency.TryGetValue(node, out var neighbours)) continue;

            foreach (var (next, length) in neighbours)
            {
                double magnitude = Math.Abs(value) - opt.ApproachRampGrade * length;
                if (magnitude <= 0.0) continue;
                double candidate = Math.Sign(value) * magnitude;
                if (magnitude <= Math.Abs(best.GetValueOrDefault(next)) + 1e-9) continue;
                best[next] = candidate;
                queue.Enqueue(next, -magnitude);
            }
        }

        // Read the ramp back onto the at-grade samples.
        for (int i = 0; i < samples.Count; ++i)
        {
            int w = matchedWay[i];
            if (w < 0 || wayLiftSource[w] != LiftSource.AtGrade || waysPassingUnder.Contains(w))
                continue;
            var way = ways[w];
            int v = UpperVertex(way.NodeStation, matchedStation[i]);
            double a = best.GetValueOrDefault(way.NodeIds[v - 1]);
            double b = best.GetValueOrDefault(way.NodeIds[v]);
            double span = way.NodeStation[v] - way.NodeStation[v - 1];
            double t = span > 1e-9 ? (matchedStation[i] - way.NodeStation[v - 1]) / span : 0.0;
            lift[i] = a + (b - a) * Math.Clamp(t, 0.0, 1.0);
        }
    }

    // Index of the first vertex at or beyond `station` (never 0, so [v-1, v] always brackets it).
    private static int UpperVertex(double[] stations, double station)
    {
        for (int v = 1; v < stations.Length; ++v)
            if (stations[v] >= station) return v;
        return stations.Length - 1;
    }

    private static double InterpolateByStation(List<(double Station, double Lift)> ordered, double station)
    {
        if (ordered.Count == 0) return 0.0;
        if (station <= ordered[0].Station) return ordered[0].Lift;
        if (station >= ordered[^1].Station) return ordered[^1].Lift;
        for (int i = 1; i < ordered.Count; ++i)
        {
            if (ordered[i].Station < station) continue;
            double span = ordered[i].Station - ordered[i - 1].Station;
            double t = span > 1e-9 ? (station - ordered[i - 1].Station) / span : 0.0;
            return ordered[i - 1].Lift + (ordered[i].Lift - ordered[i - 1].Lift) * t;
        }
        return ordered[^1].Lift;
    }

    // ── segment lookup ───────────────────────────────────────────────────────

    // Uniform bucket grid over the way segments so a sample only tests the handful of segments that
    // could be within snapping distance. Segments are walked rather than boxed, so a long straight
    // way costs cells proportional to its length instead of its bounding box.
    private sealed class SegmentIndex
    {
        private readonly double _cell;
        private readonly Dictionary<(int, int), List<(int Way, int Segment)>> _buckets = new();

        public SegmentIndex(IReadOnlyList<OsmRoadWay> ways, double cellSize)
        {
            _cell = Math.Max(1.0, cellSize);
            for (int w = 0; w < ways.Count; ++w)
            {
                var way = ways[w];
                for (int v = 0; v + 1 < way.VertexCount; ++v)
                {
                    double len = way.NodeStation[v + 1] - way.NodeStation[v];
                    int steps = Math.Max(1, (int)Math.Ceiling(len / (_cell * 0.5)));
                    for (int s = 0; s <= steps; ++s)
                    {
                        double t = (double)s / steps;
                        Add(way.X[v] + (way.X[v + 1] - way.X[v]) * t,
                            way.Y[v] + (way.Y[v + 1] - way.Y[v]) * t, w, v);
                    }
                }
            }
        }

        private void Add(double x, double y, int way, int segment)
        {
            var key = ((int)Math.Floor(x / _cell), (int)Math.Floor(y / _cell));
            if (!_buckets.TryGetValue(key, out var list))
                _buckets[key] = list = new List<(int, int)>();
            if (list.Count == 0 || list[^1] != (way, segment))
                list.Add((way, segment));
        }

        public IEnumerable<(int Way, int Segment)> Near(double x, double y)
        {
            int cx = (int)Math.Floor(x / _cell), cy = (int)Math.Floor(y / _cell);
            var seen = new HashSet<(int, int)>();
            for (int dx = -1; dx <= 1; ++dx)
            {
                for (int dy = -1; dy <= 1; ++dy)
                {
                    if (!_buckets.TryGetValue((cx + dx, cy + dy), out var list)) continue;
                    foreach (var entry in list)
                        if (seen.Add(entry))
                            yield return entry;
                }
            }
        }
    }
}
