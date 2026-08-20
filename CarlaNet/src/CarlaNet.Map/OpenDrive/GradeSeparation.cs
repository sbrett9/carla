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

    /// <summary>Half-width of the corridor a structure sweeps over the ground. Inside it, a terrain
    /// model has no sight of the roadway below, so a road known to pass under the structure spans it
    /// rather than following the terrain. Should match the footprint the drape masks
    /// (<see cref="DrapeTerrain.Despike"/>), since both describe the same deck.</summary>
    public double StructureFootprintHalfWidthMeters { get; init; } = 15.0;

    /// <summary>Whether a run of lifted samples must reach <see cref="MinStructureMeters"/>
    /// somewhere along it to be kept.
    ///
    /// A structure is already defined as standing at least that tall, but the test was applied to
    /// samples rather than to the run they form, so a taper or a stray match could leave a lift
    /// far below it. Measured on Arapahoe_I25: seven runs peak between 4.99 and 7.50 m over 80 to
    /// 120 m, and seventy-three others peak at 1.19 m or less, fifty of them a single station,
    /// putting a step of a quarter to one metre into a road crossing flat ground.
    ///
    /// The height is used rather than the length or the ground beneath, because neither of those
    /// holds in general: a short bridge is still a bridge, and an overpass crossing a road at
    /// grade has flat ground under it.</summary>
    public bool RequireRunToReachStructureHeight { get; init; } = true;

    /// <summary>How close a centreline sample must be to a structure's end node for that node to
    /// seed an approach ramp. A node further than this from every sample of its own way is not
    /// covered by the road network there, and the nearest sample is then somewhere out along the
    /// span — seeding from it would start the approach at the height of the middle of the deck
    /// instead of at its end. Must comfortably exceed the centreline sampling step.</summary>
    public double MaxRampSourceDistanceMeters { get; init; } = 20.0;
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

    /// <summary>Index into the way list of the OSM way each sample was matched to, or -1 where none
    /// was close enough and compatible in bearing. Diagnostic: it is what
    /// <c>probe_grade_separation.py</c> reads to explain why a given road got the height it did.</summary>
    public required int[] MatchedWayIndex { get; init; }

    public required int SamplesMatched { get; init; }
    public required int SamplesLifted { get; init; }

    /// <summary>Samples on a road passing under a structure whose elevation was spanned across the
    /// footprint instead of taken from the terrain, because no terrain model sees under a deck.</summary>
    public required int SamplesSpannedUnderStructures { get; init; }
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
    /// <param name="atGradeHeights">The at-grade surface the caller will actually add the lift to,
    /// per sample. Only the footprint span needs it, to join a road to its own neighbours rather
    /// than to bare earth: the draped surface follows the photoreal on open ground, which sits a
    /// little off bare earth. Null means the at-grade surface IS bare earth plus
    /// <paramref name="atGradeOffsetMeters"/>, which is exactly true of the constant-offset modes.</param>
    public static GradeSeparationResult Compute(
        Road.Map map,
        IReadOnlyList<CenterlineSample> samples,
        OsmRoadLayers layers,
        IReadOnlyList<double> surfaceHeights,
        IReadOnlyList<double> groundHeights,
        double atGradeOffsetMeters,
        GradeSeparationOptions? options = null,
        IReadOnlyList<double>? atGradeHeights = null)
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
            var unmatched = new int[samples.Count];
            Array.Fill(unmatched, -1);
            return new GradeSeparationResult
            {
                Lift = lift, ElevatedWays = elevated, WaysPassingUnder = underWays,
                MatchedWayIndex = unmatched,
                SamplesMatched = 0, SamplesLifted = 0, SamplesSpannedUnderStructures = 0,
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

        // 5) Span each structure's footprint on the roads running beneath it. Bare earth is the right
        //    surface for them, but no terrain model can see the ground under a deck, so inside the
        //    footprint it reports the structure instead of the roadway.
        int spanned = SpanStructureFootprints(
            samples, matchedWay, matchedStation, underIndices, elevated, surfaceHeights,
            groundHeights, atGradeHeights, atGradeOffsetMeters, opt, lift);

        // 6) Carry a deck's lift into the roads it joins, across the .xodr road links. The
        //    approach ramps above walk the OSM node graph, which cannot reach a junction
        //    connector: netconvert synthesises those roads and no OSM way lies behind them. A deck
        //    ending at a junction therefore had nothing to ramp into and simply stopped, and since
        //    linked road ends are resolved to one height, the drop landed inside the deck's own
        //    last station -- 5.48 m in 10 m on road 2117 of Arapahoe_I25.
        int carried = CarryLiftAcrossRoadLinks(map, samples, opt, lift);

        // 7) Drop runs of lift that never reach the height of a structure. A taper or a stray
        //    match leaves a lift too small to be anything real, and on the ground it is a step in
        //    a road that should have stayed where it was.
        int unspanned = DropLiftsBelowStructureHeight(samples, opt, lift);

        int lifted = 0;
        double maxLift = 0.0;
        for (int i = 0; i < lift.Length; ++i)
        {
            if (lift[i] != 0.0) lifted++;
            if (Math.Abs(lift[i]) > Math.Abs(maxLift)) maxLift = lift[i];
        }

        if (carried > 0)
        {
            Console.WriteLine(
                $"[GradeSeparation] carried a deck's lift into {carried} sample(s) on the roads it "
                + "joins, so an approach ramps down instead of stepping off");
        }

        if (unspanned > 0)
        {
            Console.WriteLine(
                $"[GradeSeparation] dropped {unspanned} lifted sample(s) in runs never reaching "
                + $"{opt.MinStructureMeters:F1} m, too small to be a structure");
        }

        Console.WriteLine(
            $"[GradeSeparation] OSM drivable ways={ways.Count} elevated={elevated.Count} " +
            $"grade crossings={layers.Crossings.Count} ways passing under={underWays.Count} | " +
            $"structures from photoreal={fromSurface} from fixed separation={fromFallback} | " +
            $"samples matched={matchedCount}/{samples.Count} lifted={lifted} max lift={maxLift:F2} m | " +
            $"spanned under structures={spanned}");

        return new GradeSeparationResult
        {
            SamplesSpannedUnderStructures = spanned,
            Lift = lift,
            ElevatedWays = elevated,
            WaysPassingUnder = underWays,
            MatchedWayIndex = matchedWay,
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

        // Seed each vertex of a layered way with that way's lift there, read from the sample
        // physically nearest the vertex among those matched to that same way. Nearest-in-space, not
        // interpolated along the way: several .xodr roads can match one OSM way (the two
        // carriageways of a divided road), and their stations interleave into a profile that is not
        // a function of distance along the way.
        var nodeLift = new Dictionary<string, (double Lift, double Distance)>(StringComparer.Ordinal);
        var perWaySamples = new Dictionary<int, List<int>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            int w = matchedWay[i];
            if (w < 0 || wayLiftSource[w] == LiftSource.AtGrade) continue;
            if (!perWaySamples.TryGetValue(w, out var list))
                perWaySamples[w] = list = new List<int>();
            list.Add(i);
        }

        foreach (var (w, indices) in perWaySamples)
        {
            var way = ways[w];
            for (int v = 0; v < way.VertexCount; ++v)
            {
                double bestSq = double.MaxValue, value = 0.0;
                foreach (int i in indices)
                {
                    double dx = samples[i].X - way.X[v], dy = samples[i].Y - way.Y[v];
                    double distanceSq = dx * dx + dy * dy;
                    if (distanceSq >= bestSq) continue;
                    bestSq = distanceSq;
                    value = lift[i];
                }

                // A vertex the road network does not reach seeds nothing at all. Borrowing the
                // nearest sample regardless of range is what let a deck whose end node is 39 m from
                // any sample start its approach ramp at mid-span height.
                double distance = Math.Sqrt(bestSq);
                if (distance > opt.MaxRampSourceDistanceMeters) continue;

                // Where two structures meet at one node, the better-supported reading wins — the
                // closest sample, not the largest lift.
                if (nodeLift.TryGetValue(way.NodeIds[v], out var current)
                    && (current.Distance < distance
                        || (current.Distance <= distance && Math.Abs(current.Lift) >= Math.Abs(value))))
                {
                    continue;
                }
                nodeLift[way.NodeIds[v]] = (value, distance);
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

        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, double>();
        foreach (var (node, seed) in nodeLift)
        {
            best[node] = seed.Lift;
            queue.Enqueue(node, -Math.Abs(seed.Lift));
        }

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

        // Read the ramp back onto the at-grade samples. A sample lies between two vertices, so its
        // lift is whichever bracketing vertex can still deliver more after paying the gradient over
        // the distance to it — the same cone the relaxation above propagated. Interpolating between
        // the two vertices instead would stretch the ramp across the whole gap between them, which
        // on an OSM way described by two distant nodes smears a metre of lift over its entire length
        // rather than fading it out within lift/grade metres.
        for (int i = 0; i < samples.Count; ++i)
        {
            int w = matchedWay[i];
            if (w < 0 || wayLiftSource[w] != LiftSource.AtGrade || waysPassingUnder.Contains(w))
                continue;
            var way = ways[w];
            int v = UpperVertex(way.NodeStation, matchedStation[i]);
            double fromBelow = Reach(best.GetValueOrDefault(way.NodeIds[v - 1]),
                matchedStation[i] - way.NodeStation[v - 1], opt.ApproachRampGrade);
            double fromAbove = Reach(best.GetValueOrDefault(way.NodeIds[v]),
                way.NodeStation[v] - matchedStation[i], opt.ApproachRampGrade);
            lift[i] = Math.Abs(fromBelow) >= Math.Abs(fromAbove) ? fromBelow : fromAbove;
        }
    }

    // ── spanning a structure's footprint ─────────────────────────────────────

    // A terrain model has no sight of the ground beneath a bridge, so inside a deck's footprint it
    // reports the structure and its embankment rather than the roadway threading under them. Measured
    // at Arapahoe Ave / I-25: on 5 of the 7 ways crossing under a deck the bare-earth tileset domes up
    // inside the footprint — by 3.6 m on the ways carrying Arapahoe itself — where the real road runs
    // level underneath. Following it faithfully is what makes a road rise and fall under an overpass,
    // and it also swallows the clearance, reading ~4.4 m where the structure really stands ~8.4 m
    // above the roadway.
    //
    // Where a way is KNOWN to cross beneath a structure, the terrain inside the footprint is therefore
    // not evidence of that road's height. The road's own grades on either side are, so the span is
    // replaced by the chord between them. The correction is expressed as a lift so the caller's
    // at-grade surface remains the single source of the base elevation. This uses only what the OSM
    // already told us — that there is a structure overhead — and invents no elevation data.
    private static int SpanStructureFootprints(
        IReadOnlyList<CenterlineSample> samples,
        int[] matchedWay,
        double[] matchedStation,
        HashSet<int> waysPassingUnder,
        IReadOnlyList<OsmRoadWay> elevatedWays,
        IReadOnlyList<double> surfaceHeights,
        IReadOnlyList<double> groundHeights,
        IReadOnlyList<double>? atGradeHeights,
        double atGradeOffsetMeters,
        GradeSeparationOptions opt,
        double[] lift)
    {
        if (elevatedWays.Count == 0 || waysPassingUnder.Count == 0) return 0;

        // The chord joins ROAD elevations, so it is measured on the at-grade surface the caller will
        // add the lift to. Inside the footprint that surface is bare earth plus the systematic
        // offset, because the drape anchors the whole footprint there; outside it the draped surface
        // is free to follow the photoreal, and joining to bare earth instead would leave a step at
        // the footprint edge the size of the photoreal-vs-bare-earth gap.
        double AtGrade(int i) =>
            atGradeHeights is not null && double.IsFinite(atGradeHeights[i])
                ? atGradeHeights[i]
                : groundHeights[i] + atGradeOffsetMeters;
        double Anchored(int i) => groundHeights[i] + atGradeOffsetMeters;

        double halfWidth = opt.StructureFootprintHalfWidthMeters;
        var footprint = new SegmentIndex(elevatedWays, halfWidth);
        double halfWidthSq = halfWidth * halfWidth;

        // Samples on a road that crosses under a structure, where that structure stands over them.
        // The nominal footprint is a corridor about the deck's centreline, but OSM does not record
        // how wide a deck is, so a reading that still shows the structure overhead just outside the
        // corridor is taken as under it too — bounded to twice the corridor so this cannot run away
        // along a road that merely passes a building somewhere else.
        double reachSq = 4.0 * halfWidthSq;
        var beneath = new bool[samples.Count];
        var roadsAffected = new HashSet<RoadId>();
        for (int i = 0; i < samples.Count; ++i)
        {
            int w = matchedWay[i];
            if (w < 0 || !waysPassingUnder.Contains(w)) continue;
            double distanceSq = NearestWayDistanceSquared(
                footprint, elevatedWays, samples[i].X, samples[i].Y);
            if (distanceSq > halfWidthSq)
            {
                if (distanceSq > reachSq) continue;
                double overhead = surfaceHeights[i] - groundHeights[i] - atGradeOffsetMeters;
                if (!double.IsFinite(overhead) || overhead <= opt.MinStructureMeters) continue;
            }
            beneath[i] = true;
            roadsAffected.Add(samples[i].RoadId);
        }
        if (roadsAffected.Count == 0) return 0;

        // Walk each OSM way, not each .xodr road. The corrected height has to be a function of
        // POSITION: two roads meeting at a junction must be given the same elevation there or the
        // mesh tears at the joint. netconvert cuts one way into several roads — including short
        // connecting roads inside a junction that can lie wholly within a footprint, with no end
        // outside it to anchor a chord to — so anything computed per road is discontinuous across
        // them by construction.
        var perWay = new Dictionary<int, List<int>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            int w = matchedWay[i];
            if (w < 0 || !waysPassingUnder.Contains(w)) continue;
            if (!roadsAffected.Contains(samples[i].RoadId)) continue;
            if (!perWay.TryGetValue(w, out var list))
                perWay[w] = list = new List<int>();
            list.Add(i);
        }

        int corrected = 0;
        foreach (var list in perWay.Values)
        {
            list.Sort((a, b) => matchedStation[a].CompareTo(matchedStation[b]));

            for (int start = 0; start < list.Count;)
            {
                if (!beneath[list[start]]) { ++start; continue; }
                int end = start;
                while (end + 1 < list.Count && beneath[list[end + 1]]) ++end;

                // The nearest usable terrain reading clear of the footprint on each side.
                int before = start - 1;
                while (before >= 0 && !double.IsFinite(groundHeights[list[before]])) --before;
                int after = end + 1;
                while (after < list.Count && !double.IsFinite(groundHeights[list[after]])) ++after;

                bool haveBefore = before >= 0, haveAfter = after < list.Count;
                if (haveBefore || haveAfter)
                {
                    double s0 = haveBefore ? matchedStation[list[before]] : 0.0;
                    double g0 = haveBefore ? AtGrade(list[before]) : 0.0;
                    double s1 = haveAfter ? matchedStation[list[after]] : 0.0;
                    double g1 = haveAfter ? AtGrade(list[after]) : 0.0;

                    for (int k = start; k <= end; ++k)
                    {
                        int i = list[k];
                        if (!double.IsFinite(groundHeights[i])) continue;

                        double chord;
                        if (haveBefore && haveAfter)
                        {
                            double span = s1 - s0;
                            double t = span > 1e-9 ? (matchedStation[i] - s0) / span : 0.0;
                            chord = g0 + (g1 - g0) * Math.Clamp(t, 0.0, 1.0);
                        }
                        else
                        {
                            // The road enters or leaves the map inside the footprint; the one grade
                            // in hand is still better evidence than the structure overhead.
                            chord = haveBefore ? g0 : g1;
                        }

                        double correction = Math.Clamp(chord - Anchored(i),
                            -opt.MaxStructureMeters, opt.MaxStructureMeters);
                        if (correction == 0.0) continue;
                        lift[i] += correction;
                        ++corrected;
                    }
                }
                start = end + 1;
            }
        }
        return corrected;
    }

    private static double NearestWayDistanceSquared(
        SegmentIndex index, IReadOnlyList<OsmRoadWay> ways, double x, double y)
    {
        double best = double.MaxValue;
        foreach (var (w, seg) in index.Near(x, y))
        {
            var way = ways[w];
            double x0 = way.X[seg], y0 = way.Y[seg];
            double dx = way.X[seg + 1] - x0, dy = way.Y[seg + 1] - y0;
            double segLenSq = dx * dx + dy * dy;
            if (segLenSq <= 1e-12) continue;
            double t = Math.Clamp(((x - x0) * dx + (y - y0) * dy) / segLenSq, 0.0, 1.0);
            double px = x0 + dx * t, py = y0 + dy * t;
            double d = (x - px) * (x - px) + (y - py) * (y - py);
            if (d < best) best = d;
        }
        return best;
    }

    // What is left of a lift after travelling `distance` at `grade`, keeping its sign; 0 once spent.
    /// <summary>Spreads each road end's lift into the roads linked to it, decaying at the ramp
    /// gradient, so a deck descends to grade instead of stepping off its own last station.
    ///
    /// This walks the .xodr road links rather than the OSM node graph, which is what
    /// ApplyApproachRamps uses. The two are not the same network: a junction connecting-road is
    /// synthesised by netconvert and has no OSM way behind it, so a deck that ends at a junction
    /// is invisible to the node walk however well connected it is on the ground.
    ///
    /// A road end is reached with whatever lift survives the distance travelled, and the largest
    /// arrival wins, so a ramp is never steeper than the gradient however the network branches.
    /// Existing lift is never reduced. Returns how many samples were raised.</summary>
    private static int CarryLiftAcrossRoadLinks(
        Road.Map map,
        IReadOnlyList<CenterlineSample> samples,
        GradeSeparationOptions opt,
        double[] lift)
    {
        if (opt.ApproachRampGrade <= 0.0 || samples.Count == 0) return 0;

        var stations = new Dictionary<RoadId, List<int>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            if (!stations.TryGetValue(samples[i].RoadId, out var list))
            {
                stations[samples[i].RoadId] = list = [];
            }
            list.Add(i);
        }
        foreach (var list in stations.Values)
        {
            list.Sort((a, b) => samples[a].S.CompareTo(samples[b].S));
        }

        // Arriving at a road end with a budget of lift; the end is the road's start when
        // fromStart, otherwise its far end. Best arrival wins, so each end is walked once per
        // improvement rather than once per path.
        var best = new Dictionary<(RoadId Road, bool FromStart), double>();
        var queue = new PriorityQueue<(RoadId Road, bool FromStart, double Budget), double>();

        void Offer(RoadId road, bool fromStart, double budget)
        {
            if (budget <= 0.0 || !stations.ContainsKey(road)) return;
            var key = (road, fromStart);
            if (best.TryGetValue(key, out double had) && had >= budget) return;
            best[key] = budget;
            queue.Enqueue((road, fromStart, budget), -budget);
        }

        void OfferNeighbours(RoadId from, bool atRoadStart, double budget)
        {
            if (budget <= 0.0 || !map.Roads.TryGetValue(from, out var road)) return;
            foreach (var neighbour in atRoadStart ? road.Prevs : road.Nexts)
            {
                if (neighbour is null || neighbour.Id == from) continue;
                // Enter the neighbour at whichever of its ends touches this road.
                if (neighbour.PredecessorRoadId == from) Offer(neighbour.Id, true, budget);
                if (neighbour.SuccessorRoadId == from) Offer(neighbour.Id, false, budget);
            }
        }

        // Every road end that already carries lift is a source.
        foreach (var (roadId, list) in stations)
        {
            if (list.Count == 0) continue;
            OfferNeighbours(roadId, atRoadStart: true, budget: lift[list[0]]);
            OfferNeighbours(roadId, atRoadStart: false, budget: lift[list[^1]]);
        }

        int raised = 0;
        while (queue.TryDequeue(out var entry, out _))
        {
            if (!best.TryGetValue((entry.Road, entry.FromStart), out double current)
                || current > entry.Budget)
            {
                continue;   // a better arrival superseded this one
            }
            var list = stations[entry.Road];
            double length = map.Roads.TryGetValue(entry.Road, out var road) ? road.Length : 0.0;
            double entryStation = entry.FromStart ? 0.0 : length;

            foreach (int i in list)
            {
                double travelled = Math.Abs(samples[i].S - entryStation);
                double reach = entry.Budget - opt.ApproachRampGrade * travelled;
                if (reach <= lift[i]) continue;
                if (lift[i] == 0.0) ++raised;
                lift[i] = reach;
            }

            double residual = entry.Budget - opt.ApproachRampGrade * length;
            if (residual > 0.0)
            {
                OfferNeighbours(entry.Road, atRoadStart: !entry.FromStart, budget: residual);
            }
        }
        return raised;
    }

    /// <summary>Zeroes any run of lifted samples that never reaches the height of a structure.
    ///
    /// A run is contiguous along one road, so the taper at the end of a real deck is kept: it
    /// belongs to a run that reaches the deck's own height. What goes is a run that never gets
    /// there at all. Returns how many samples were dropped.</summary>
    private static int DropLiftsBelowStructureHeight(
        IReadOnlyList<CenterlineSample> samples,
        GradeSeparationOptions opt,
        double[] lift)
    {
        if (!opt.RequireRunToReachStructureHeight) return 0;

        int dropped = 0;
        int i = 0;
        while (i < lift.Length)
        {
            if (lift[i] == 0.0)
            {
                ++i;
                continue;
            }
            int j = i;
            double peak = Math.Abs(lift[i]);
            while (j + 1 < lift.Length && lift[j + 1] != 0.0
                   && samples[j + 1].RoadId == samples[i].RoadId)
            {
                ++j;
                peak = Math.Max(peak, Math.Abs(lift[j]));
            }
            if (peak < opt.MinStructureMeters)
            {
                for (int k = i; k <= j; ++k)
                {
                    if (lift[k] != 0.0) ++dropped;
                    lift[k] = 0.0;
                }
            }
            i = j + 1;
        }
        return dropped;
    }

    private static double Reach(double lift, double distance, double grade)
    {
        double remaining = Math.Abs(lift) - grade * Math.Max(0.0, distance);
        return remaining <= 0.0 ? 0.0 : Math.Sign(lift) * remaining;
    }

    // Index of the first vertex at or beyond `station` (never 0, so [v-1, v] always brackets it).
    private static int UpperVertex(double[] stations, double station)
    {
        for (int v = 1; v < stations.Length; ++v)
            if (stations[v] >= station) return v;
        return stations.Length - 1;
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
