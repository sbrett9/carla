// The .xodr netconvert produces is a planar road network: each road carries its own
// <elevationProfile>, but nothing in it records which road passes OVER which other. Sampling a
// terrain surface therefore hands a bridge deck and the road beneath it the same height and the
// two merge into a phantom at-grade crossing.
//
// OSM does record the vertical order. `layer` ranks overlapping ways, `bridge`/`tunnel` name the
// structure, and — independent of any tag — two ways that cross in plan while sharing NO node are
// grade separated by construction, because OSM only creates a junction where ways share a node.
//
// This file is the pure OSM half: read the car-drivable ways with their layer tags, project them
// into the CARLA world frame with the SAME ellipsoidal transform the elevation, sign and drape
// paths use (Geodesy.GeodeticToCarlaLocal against the .xodr geoReference), and find the plan
// crossings. GradeSeparation turns that into a per-sample elevation lift.
//
// Coordinate frame: CARLA world metres, +X=East, -Y=North — matching
// ElevationInjector.ExtractCenterlineSamples, so the two can be compared directly.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.OpenDrive;

/// <summary>One car-drivable OSM way, projected into the CARLA world frame, with the tags that
/// place it vertically relative to the ways it crosses.</summary>
public sealed class OsmRoadWay
{
    /// <summary>OSM way id, as it appears in the source file.</summary>
    public required string Id { get; init; }

    /// <summary>Vertical rank among the ways this one overlaps. Taken from the OSM <c>layer</c>
    /// tag; where that is absent, <c>bridge</c> implies +1 and <c>tunnel</c> implies -1. 0 means
    /// at grade.</summary>
    public required int Layer { get; init; }

    /// <summary>The way carries a <c>bridge</c> tag with a value other than "no".</summary>
    public required bool IsBridge { get; init; }

    /// <summary>The way carries a <c>tunnel</c> tag denoting a bore. <c>building_passage</c> is
    /// deliberately excluded: a way threading under a building is still at ground level.</summary>
    public required bool IsTunnel { get; init; }

    /// <summary>Node ids in order. Two ways sharing an id meet at a junction; two ways sharing
    /// none cannot, which is what makes a plan crossing a grade separation.</summary>
    public required IReadOnlyList<string> NodeIds { get; init; }

    /// <summary>Vertex X in CARLA world metres (+East), one per entry of <see cref="NodeIds"/>.</summary>
    public required double[] X { get; init; }

    /// <summary>Vertex Y in CARLA world metres (-North), one per entry of <see cref="NodeIds"/>.</summary>
    public required double[] Y { get; init; }

    /// <summary>Distance along the way to each vertex, metres; <c>NodeStation[0]</c> is 0.</summary>
    public required double[] NodeStation { get; init; }

    public required double MinX { get; init; }
    public required double MinY { get; init; }
    public required double MaxX { get; init; }
    public required double MaxY { get; init; }

    public int VertexCount => X.Length;

    /// <summary>Total length of the projected polyline, metres.</summary>
    public double Length => NodeStation.Length > 0 ? NodeStation[^1] : 0.0;
}

/// <summary>Two ways crossing in plan while sharing no node — a grade separation, whether or not
/// either way is tagged. Indices address the way list the crossing was found in; the crossing
/// point is in CARLA world metres, where the clearance between the two surfaces is measurable.</summary>
public readonly record struct GradeCrossing(int WayIndexA, int WayIndexB, double X, double Y);

/// <summary>The car-drivable OSM ways of one extract, projected and cross-referenced.</summary>
public sealed class OsmRoadLayers
{
    /// <summary>The <c>highway</c> values netconvert turns into driveable roads. Footways,
    /// cycleways and rail are excluded so a centreline never snaps to a pavement running beside
    /// the road it belongs to.</summary>
    private static readonly HashSet<string> DrivableHighwayValues = new(StringComparer.Ordinal)
    {
        "motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link",
        "secondary", "secondary_link", "tertiary", "tertiary_link", "unclassified",
        "residential", "living_street", "service", "road",
    };

    // Tag values that mean "this key is not actually set".
    private static readonly HashSet<string> FalseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "false", "0", "",
    };

    public IReadOnlyList<OsmRoadWay> Ways { get; }
    public IReadOnlyList<GradeCrossing> Crossings { get; }

    private OsmRoadLayers(IReadOnlyList<OsmRoadWay> ways, IReadOnlyList<GradeCrossing> crossings)
    {
        Ways = ways;
        Crossings = crossings;
    }

    /// <summary>Read <paramref name="osmPath"/> (the same file that was fed to netconvert, already
    /// clipped if the pipeline clips), project every car-drivable way against
    /// <paramref name="origin"/>, and find the plan crossings that share no node.</summary>
    public static OsmRoadLayers Read(string osmPath, GeoLocation origin)
    {
        ArgumentNullException.ThrowIfNull(osmPath);
        var ways = ParseRoadWays(osmPath, origin);
        return new OsmRoadLayers(ways, FindGradeCrossings(ways));
    }

    /// <summary>Ways whose <see cref="OsmRoadWay.Layer"/> places them above grade.</summary>
    public IReadOnlyList<int> ElevatedWayIndices()
    {
        var result = new List<int>();
        for (int i = 0; i < Ways.Count; ++i)
            if (Ways[i].Layer > 0) result.Add(i);
        return result;
    }

    /// <summary>Ways that pass beneath a way of a higher layer at one of the crossings — the
    /// population whose elevation must stay on bare earth however high the surface reads there.</summary>
    public HashSet<int> WaysPassingUnder()
    {
        var under = new HashSet<int>();
        foreach (var c in Crossings)
        {
            int layerA = Ways[c.WayIndexA].Layer, layerB = Ways[c.WayIndexB].Layer;
            if (layerA > layerB) under.Add(c.WayIndexB);
            else if (layerB > layerA) under.Add(c.WayIndexA);
        }
        return under;
    }

    // ── OSM parse ────────────────────────────────────────────────────────────

    /// <summary>Stream the .osm and return the car-drivable ways projected into the CARLA world
    /// frame. Ways referencing a node the file does not contain keep the vertices it does.</summary>
    public static IReadOnlyList<OsmRoadWay> ParseRoadWays(string osmPath, GeoLocation origin)
    {
        ArgumentNullException.ThrowIfNull(osmPath);

        var nodeLatLon = new Dictionary<string, (double Lat, double Lon)>(StringComparer.Ordinal);
        var pending = new List<(string Id, List<string> Refs, Dictionary<string, string> Tags)>();

        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
        using (var reader = XmlReader.Create(osmPath, settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.Name == "node")
                {
                    string? id = reader.GetAttribute("id");
                    if (id is null) continue;
                    if (TryParseDouble(reader.GetAttribute("lat"), out double lat)
                        && TryParseDouble(reader.GetAttribute("lon"), out double lon))
                    {
                        nodeLatLon[id] = (lat, lon);
                    }
                    continue;
                }

                if (reader.Name != "way")
                    continue;

                string? wayId = reader.GetAttribute("id");
                if (wayId is null || reader.IsEmptyElement)
                    continue;

                var refs = new List<string>();
                var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                using var sub = reader.ReadSubtree();
                sub.Read(); // position on <way>
                while (sub.Read())
                {
                    if (sub.NodeType != XmlNodeType.Element) continue;
                    if (sub.Name == "nd")
                    {
                        if (sub.GetAttribute("ref") is string r) refs.Add(r);
                    }
                    else if (sub.Name == "tag")
                    {
                        if (sub.GetAttribute("k") is string k && sub.GetAttribute("v") is string v)
                            tags[k] = v;
                    }
                }

                if (refs.Count >= 2 && tags.TryGetValue("highway", out var highway)
                    && DrivableHighwayValues.Contains(highway))
                {
                    pending.Add((wayId, refs, tags));
                }
            }
        }

        var ways = new List<OsmRoadWay>(pending.Count);
        foreach (var (id, refs, tags) in pending)
        {
            var keptRefs = new List<string>(refs.Count);
            var xs = new List<double>(refs.Count);
            var ys = new List<double>(refs.Count);
            foreach (var r in refs)
            {
                if (!nodeLatLon.TryGetValue(r, out var ll)) continue;
                var p = Geodesy.GeodeticToCarlaLocal(origin, new GeoLocation(ll.Lat, ll.Lon, 0.0));
                keptRefs.Add(r);
                xs.Add(p.X);
                ys.Add(p.Y);
            }
            if (keptRefs.Count < 2) continue;

            var station = new double[xs.Count];
            double minX = xs[0], maxX = xs[0], minY = ys[0], maxY = ys[0];
            for (int i = 1; i < xs.Count; ++i)
            {
                station[i] = station[i - 1] + Math.Sqrt(
                    (xs[i] - xs[i - 1]) * (xs[i] - xs[i - 1]) + (ys[i] - ys[i - 1]) * (ys[i] - ys[i - 1]));
                minX = Math.Min(minX, xs[i]); maxX = Math.Max(maxX, xs[i]);
                minY = Math.Min(minY, ys[i]); maxY = Math.Max(maxY, ys[i]);
            }

            bool isBridge = IsTagSet(tags, "bridge");
            // A building passage runs under a roof at ground level, not through a bore.
            bool isTunnel = IsTagSet(tags, "tunnel")
                && !string.Equals(tags.GetValueOrDefault("tunnel"), "building_passage", StringComparison.OrdinalIgnoreCase);

            ways.Add(new OsmRoadWay
            {
                Id = id,
                Layer = ResolveLayer(tags, isBridge, isTunnel),
                IsBridge = isBridge,
                IsTunnel = isTunnel,
                NodeIds = keptRefs,
                X = xs.ToArray(),
                Y = ys.ToArray(),
                NodeStation = station,
                MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
            });
        }
        return ways;
    }

    // An explicit `layer` wins — it is the only tag that orders more than two levels, which is what
    // a stacked interchange needs. bridge/tunnel supply the direction when layer is absent.
    private static int ResolveLayer(Dictionary<string, string> tags, bool isBridge, bool isTunnel)
    {
        if (tags.TryGetValue("layer", out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return (int)Math.Round(v);
        }
        if (isBridge) return 1;
        if (isTunnel) return -1;
        return 0;
    }

    private static bool IsTagSet(Dictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var v) && !FalseValues.Contains(v);

    private static bool TryParseDouble(string? s, out double value)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ── crossings ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every pair of ways that intersect in plan while sharing no node. OSM builds a junction only
    /// where ways share a node, so such an intersection can only be a grade separation — the test
    /// needs no <c>bridge</c> or <c>layer</c> tag on either side and so carries into under-tagged
    /// areas. The returned point is where the two centrelines cross, which is where the vertical
    /// clearance between the two surfaces is measurable.
    /// </summary>
    public static IReadOnlyList<GradeCrossing> FindGradeCrossings(IReadOnlyList<OsmRoadWay> ways)
    {
        ArgumentNullException.ThrowIfNull(ways);
        var crossings = new List<GradeCrossing>();

        // Node sets are only needed for pairs that survive the bounding-box test.
        var nodeSets = new HashSet<string>?[ways.Count];

        for (int a = 0; a < ways.Count; ++a)
        {
            for (int b = a + 1; b < ways.Count; ++b)
            {
                if (!BoundsOverlap(ways[a], ways[b])) continue;

                nodeSets[a] ??= new HashSet<string>(ways[a].NodeIds, StringComparer.Ordinal);
                bool sharesNode = false;
                foreach (var id in ways[b].NodeIds)
                {
                    if (nodeSets[a]!.Contains(id)) { sharesNode = true; break; }
                }
                if (sharesNode) continue;   // they meet at a junction, so they are at grade

                if (TryFirstIntersection(ways[a], ways[b], out double x, out double y))
                    crossings.Add(new GradeCrossing(a, b, x, y));
            }
        }
        return crossings;
    }

    private static bool BoundsOverlap(OsmRoadWay a, OsmRoadWay b)
        => !(a.MaxX < b.MinX || b.MaxX < a.MinX || a.MaxY < b.MinY || b.MaxY < a.MinY);

    // First proper intersection between the two polylines. Touching endpoints are excluded by the
    // caller's node-disjoint test, so a strict straddle on both segments is the right predicate.
    private static bool TryFirstIntersection(OsmRoadWay a, OsmRoadWay b, out double x, out double y)
    {
        for (int i = 0; i + 1 < a.VertexCount; ++i)
        {
            for (int j = 0; j + 1 < b.VertexCount; ++j)
            {
                double d1 = Orient(b.X[j], b.Y[j], b.X[j + 1], b.Y[j + 1], a.X[i], a.Y[i]);
                double d2 = Orient(b.X[j], b.Y[j], b.X[j + 1], b.Y[j + 1], a.X[i + 1], a.Y[i + 1]);
                double d3 = Orient(a.X[i], a.Y[i], a.X[i + 1], a.Y[i + 1], b.X[j], b.Y[j]);
                double d4 = Orient(a.X[i], a.Y[i], a.X[i + 1], a.Y[i + 1], b.X[j + 1], b.Y[j + 1]);
                if ((d1 > 0) == (d2 > 0) || (d3 > 0) == (d4 > 0)) continue;

                double denom = d1 - d2;
                if (denom == 0.0) continue;
                double t = d1 / denom;
                x = a.X[i] + (a.X[i + 1] - a.X[i]) * t;
                y = a.Y[i] + (a.Y[i + 1] - a.Y[i]) * t;
                return true;
            }
        }
        x = y = 0.0;
        return false;
    }

    private static double Orient(double ax, double ay, double bx, double by, double cx, double cy)
        => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
}
