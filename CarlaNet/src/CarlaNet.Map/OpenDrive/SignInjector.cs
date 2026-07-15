// The flat .xodr that netconvert/OsmConverter produces carries NO <signal> elements:
// netconvert never emits stop/yield/speed-limit signs (only dynamic traffic-light codes,
// and even those only when traffic-light generation is enabled). So digital-twin worlds
// spawn no stop/yield signs even though the source OSM has highway=stop / highway=give_way
// nodes. This class post-processes the .xodr to inject the missing sign <signal> elements,
// which the native ATrafficLightManager::SpawnSignals then turns into real, sensor-detectable
// sign actors (BP_Stop01 / BP_Yield01) that drive vehicle behaviour through the existing
// UStopSignComponent / UYieldSignComponent path.
//
// It is a pure string -> string rewrite inserted after netconvert and before the world is
// loaded, mirroring ElevationInjector (which injects <elevationProfile> the same way).
//
// Placement (the geometric part): each OSM sign node is a lat/lon. We reproject it into the
// CARLA world frame with the SAME ellipsoidal transform the elevation/drape paths use
// (Geodesy.GeodeticToCarlaLocal against the .xodr geoReference), snap it to the nearest road
// reference-line sample to get (roadId, s), and place the sign at a fixed shoulder offset t
// on whichever side of the road the node actually sits. The server's OpenDRIVE parser is the
// placement authority (the C# Road.Signal transform is stubbed), so injected signs are
// validated by observing the spawned actors, not by the C# map.
//
// Coordinate frames: ExtractCenterlineSamples emits CARLA-world samples (+X=East, -Y=North);
// Geodesy.GeodeticToCarlaLocal returns the same frame — so the nearest-sample search is a
// like-for-like comparison. Road.GetDirectedPointInNoLaneOffset, however, returns the raw
// OpenDRIVE planView point (+Y=North) and the ref-line tangent, so the side-of-road test is
// done in the planView frame (flip the node's Y back) where +t means "left of travel".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CarlaNet.Map.Geom;
using CarlaNet.Map.Road;
using CarlaNet.Map.Road.Element;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.OpenDrive;

/// <summary>A static sign parsed from the OSM source, before snapping to the road network.</summary>
public readonly record struct OsmSign(double Latitude, double Longitude, string TypeCode, string NamePrefix);

/// <summary>A sign after snapping, ready to be written as an OpenDRIVE &lt;signal&gt;.</summary>
public readonly record struct PlacedSign(RoadId RoadId, double S, double T, string TypeCode, string Id, string Name);

public static class SignInjector
{
    // OpenDRIVE sign type codes CARLA matches in SpawnSignals (see carla::road::SignalType).
    private const string StopType = "206";   // SignalType::StopSign
    private const string YieldType = "205";  // SignalType::YieldSign

    /// <summary>
    /// Rewrites <paramref name="openDriveXml"/> so each road that an OSM stop/give-way node
    /// snaps to carries a &lt;signal&gt; for it. <paramref name="osmPath"/> is the same .osm that
    /// was fed to netconvert (already clipped, if the pipeline clips), read directly for the
    /// node lat/lon + tags that netconvert discards. <paramref name="map"/> is the parsed flat
    /// .xodr, whose geoReference is the projection origin (the digital-twin path pins it). Roads
    /// with no snapped sign are untouched. Returns the original text unchanged if the OSM has no
    /// stop/give-way nodes.
    /// </summary>
    /// <param name="sampleStepMeters">Reference-line sampling step for the nearest-point snap.</param>
    /// <param name="shoulderOffsetMeters">Lateral distance from the road centre to place the sign.</param>
    /// <param name="maxSnapMeters">A node farther than this from every road is dropped (not on our network).</param>
    public static string InjectSigns(
        string openDriveXml,
        string osmPath,
        Road.Map map,
        double sampleStepMeters = 2.0,
        double shoulderOffsetMeters = 3.5,
        double maxSnapMeters = 25.0)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        ArgumentNullException.ThrowIfNull(osmPath);
        ArgumentNullException.ThrowIfNull(map);

        var osmSigns = ParseOsmSigns(osmPath);
        if (osmSigns.Count == 0)
            return openDriveXml;

        var samples = ElevationInjector.ExtractCenterlineSamples(map, sampleStepMeters);
        if (samples.Count == 0)
            return openDriveXml;

        var origin = map.GeoReference;

        // Snap every node to (roadId, s, t); collect per road.
        var perRoad = new Dictionary<RoadId, List<PlacedSign>>();
        int counter = 0;
        int droppedFar = 0;
        double nearestSum = 0.0, nearestMax = 0.0;
        foreach (var sign in osmSigns)
        {
            var local = Geodesy.GeodeticToCarlaLocal(
                origin, new GeoLocation(sign.Latitude, sign.Longitude, 0.0));

            NearestStation(samples, local.X, local.Y, out var roadId, out var s, out double dist);
            nearestSum += dist;
            if (dist > nearestMax) nearestMax = dist;
            if (dist > maxSnapMeters)
            {
                droppedFar++;
                continue; // nearest road is too far — node isn't on our generated network
            }

            if (!map.Roads.TryGetValue(roadId, out var road))
                continue;

            DirectedPoint dp = Road.Map.GetDirectedPointInNoLaneOffset(road, s);
            double t = ShoulderOffsetSigned(dp, local, shoulderOffsetMeters);
            string id = $"osm_{sign.TypeCode}_{counter++}";
            var placed = new PlacedSign(roadId, s, t, sign.TypeCode, id, $"{sign.NamePrefix}_{counter}");

            if (!perRoad.TryGetValue(roadId, out var list))
                perRoad[roadId] = list = new List<PlacedSign>();
            list.Add(placed);
        }

        Console.WriteLine(
            $"[SignInjector] OSM stop/yield nodes={osmSigns.Count} injected={counter} " +
            $"dropped_beyond_{maxSnapMeters:F0}m={droppedFar} nearest-road avg=" +
            $"{(osmSigns.Count > 0 ? nearestSum / osmSigns.Count : 0):F0}m max={nearestMax:F0}m");

        if (perRoad.Count == 0)
            return openDriveXml;

        return RewriteXodr(openDriveXml, perRoad);
    }

    // ── OSM parse ────────────────────────────────────────────────────────────

    // Read <node lat lon> elements whose child <tag k="highway" v="stop"|"give_way"> marks a
    // sign. Streamed so a large (unclipped) .osm doesn't have to be held whole in memory.
    private static List<OsmSign> ParseOsmSigns(string osmPath)
    {
        var signs = new List<OsmSign>();
        var readerSettings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(osmPath, readerSettings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "node")
                continue;

            string? latStr = reader.GetAttribute("lat");
            string? lonStr = reader.GetAttribute("lon");
            bool empty = reader.IsEmptyElement;
            if (latStr is null || lonStr is null ||
                !double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
                !double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                continue;
            }
            if (empty)
                continue; // no children -> no tags -> not a sign

            // Read this node's <tag> children. Stop/give-way signs are tagged either
            // highway=stop|give_way or traffic_sign=stop|give_way.
            string? typeCode = null;
            using var sub = reader.ReadSubtree();
            sub.Read(); // position on <node>
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element || sub.Name != "tag")
                    continue;
                string? k = sub.GetAttribute("k");
                if (k != "highway" && k != "traffic_sign")
                    continue;
                string? v = sub.GetAttribute("v");
                if (v == "stop") typeCode = StopType;
                else if (v == "give_way") typeCode = YieldType;
            }

            if (typeCode is not null)
                signs.Add(new OsmSign(lat, lon, typeCode,
                    typeCode == StopType ? "OSM_Stop" : "OSM_Yield"));
        }

        return signs;
    }

    // ── snapping ─────────────────────────────────────────────────────────────

    // Nearest reference-line sample to (x, y) in the CARLA world frame, with its distance.
    private static void NearestStation(
        IReadOnlyList<CenterlineSample> samples, double x, double y,
        out RoadId roadId, out double s, out double dist)
    {
        roadId = default;
        s = 0.0;
        double bestSq = double.MaxValue;
        for (int i = 0; i < samples.Count; ++i)
        {
            double dx = samples[i].X - x;
            double dy = samples[i].Y - y;
            double dsq = dx * dx + dy * dy;
            if (dsq < bestSq)
            {
                bestSq = dsq;
                roadId = samples[i].RoadId;
                s = samples[i].S;
            }
        }
        dist = Math.Sqrt(bestSq);
    }

    // Signed lateral offset t placing the sign on the side of the road the node sits on.
    // OpenDRIVE +t is left of the direction of travel; the server applies -t then flips Y
    // (MapBuilder::ComputeSignalTransform), which nets out to +t = left. We work in the planView
    // frame: flip the node's CARLA -Y back to +Y, take the ref point + tangent there, and test
    // which side of the tangent the node lies on.
    private static double ShoulderOffsetSigned(DirectedPoint dp, Location nodeLocalCarla, double magnitude)
    {
        double nodePlanViewY = -nodeLocalCarla.Y; // CARLA -Y=North -> planView +Y=North
        double vx = nodeLocalCarla.X - dp.Location.X;
        double vy = nodePlanViewY - dp.Location.Y;
        // Left-of-travel normal for a heading tangent: (-sin, cos).
        double leftDot = vx * -Math.Sin(dp.Tangent) + vy * Math.Cos(dp.Tangent);
        return (leftDot >= 0.0 ? +1.0 : -1.0) * magnitude;
    }

    // ── xodr rewrite ─────────────────────────────────────────────────────────

    private static string RewriteXodr(string openDriveXml, Dictionary<RoadId, List<PlacedSign>> perRoad)
    {
        var doc = XDocument.Parse(openDriveXml);
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document (no root)");

        foreach (var roadNode in root.Elements("road"))
        {
            if (!uint.TryParse(roadNode.Attribute("id")?.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id))
                continue;
            if (!perRoad.TryGetValue(id, out var signs) || signs.Count == 0)
                continue;

            var signalsEl = roadNode.Element("signals") ?? CreateSignalsContainer(roadNode);
            foreach (var sign in signs)
                signalsEl.Add(BuildSignalElement(sign));
        }

        return doc.ToString(SaveOptions.None);
    }

    // Per the OpenDRIVE <road> child order, <signals> comes after <lanes> (and after <objects>
    // if present). netconvert emits neither <signals> nor <objects>, so we normally insert right
    // after <lanes>.
    private static XElement CreateSignalsContainer(XElement roadNode)
    {
        var signalsEl = new XElement("signals");
        var anchor = roadNode.Element("objects") ?? roadNode.Element("lanes");
        if (anchor != null)
            anchor.AddAfterSelf(signalsEl);
        else
            roadNode.Add(signalsEl);
        return signalsEl;
    }

    private static XElement BuildSignalElement(PlacedSign sign) =>
        new XElement("signal",
            new XAttribute("s", F(sign.S)),
            new XAttribute("t", F(sign.T)),
            new XAttribute("id", sign.Id),
            new XAttribute("name", sign.Name),   // must NOT be "Stencil_STOP" (SpawnSignals skips that)
            new XAttribute("dynamic", "no"),
            new XAttribute("orientation", "+"),  // does not affect placement; facing comes from the tangent
            new XAttribute("zOffset", "0"),
            new XAttribute("country", "OpenDRIVE"),
            new XAttribute("type", sign.TypeCode),
            new XAttribute("subtype", "-1"),
            new XAttribute("value", "-1"),
            new XAttribute("hOffset", "0"));

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
