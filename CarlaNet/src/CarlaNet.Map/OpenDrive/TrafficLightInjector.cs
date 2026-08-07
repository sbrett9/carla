// netconvert emits traffic-light <signal>s and one all-heads <controller> per signalized
// junction, but it does NOT emit the <junction><controller> links that OpenDRIVE (and CARLA)
// use to tie a controller to its junction, and it does NOT split a junction's heads into the
// separate signal PHASES that actually cycle. Two consequences in CARLA:
//
//   1. With no junction link, CARLA's SolveControllerAndJuntionReferences never associates a
//      signal with its controller, so every light falls into ATrafficLightManager's orphan
//      "no controller" branch -> one isolated group per light + per-frame log spam (issue #1).
//   2. A single all-heads controller would drive every approach of a junction green together.
//
// This class fixes both in the .xodr (no engine change): for each SUMO <tlLogic> it reads the
// phase program, emits ONE <controller> per distinct green phase (listing exactly the heads that
// are green together), and adds the <junction><controller> links. CARLA then takes its normal
// grouping path — one ATrafficLightGroup per junction, one UTrafficLightController per phase,
// cycled round-robin so conflicting approaches never share green.
//
// The correspondence that makes this exact: netconvert names the xodr signal for tlLogic J's
// link k "J_k", the xodr <controller> id and the junction <name> both equal J, and each
// <connection tl="J" linkIndex="k"> ties link k to a movement. So the green characters of a
// phase state string map position-for-position onto signals J_0, J_1, ...
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace CarlaNet.Map.OpenDrive;

public static class TrafficLightInjector
{
    // Cap on how far a pole is pushed to the far side of its junction — a guard against degenerate
    // connecting-road geometry (a real intersection crossing is well under this).
    private const double MaxFarSideMeters = 35.0;

    // Extra step past where the approach exits the junction, to clear the far crosswalk onto the corner.
    private const double FarSideClearanceMeters = 2.5;

    // How far past the drivable edge to seat a pole, onto the shoulder/sidewalk. netconvert heads sit
    // in a driving lane, so placing the pole at its own t leaves it in the roadway / crosswalk box.
    private const double SidewalkMarginMeters = 2.0;

    // Reference-line sampling step used to find the road surface under a relocated pole. The roads
    // searched are a junction's internal ones, which are short.
    private const double ElevationSampleStepMeters = 2.0;

    // A pole farther than this from every road of its junction keeps the elevation of its own stop
    // line rather than adopting a distant road's. A curbside pole is a lane or two off the nearest
    // centreline, plus the crosswalk clearance, so this is generous.
    private const double ElevationSearchRadiusMeters = 25.0;


    /// <summary>
    /// Rewrites <paramref name="openDriveXml"/> so every netconvert traffic-light junction is
    /// grouped per phase and linked to its junction, using the phase programs in the SUMO
    /// <paramref name="netXml"/> (netconvert's <c>--output-file</c>). Returns the input unchanged
    /// if the net has no <c>&lt;tlLogic&gt;</c> programs.
    /// </summary>
    public static string InjectTrafficLights(string openDriveXml, string netXml)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        ArgumentNullException.ThrowIfNull(netXml);

        // tlLogic id -> ordered list of phases; each phase is the set of link indices green together.
        var programs = ParseGreenPhases(netXml);
        if (programs.Count == 0)
            return openDriveXml;

        var doc = XDocument.Parse(openDriveXml);
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document (no root)");

        // Parsed geometry of the (elevated) input — junction centres + head world positions, for
        // relocating each surviving pole to the far side of its junction. Null if it fails to parse;
        // the far-side step is then skipped and poles keep their near-side road-relative placement.
        var geoMap = OpenDriveParser.Load(openDriveXml);

        // junction name -> junction element (netconvert sets <junction name="{tlLogicId}">).
        var junctionByName = new Dictionary<string, XElement>();
        foreach (var j in root.Elements("junction"))
            if (j.Attribute("name")?.Value is string nm)
                junctionByName[nm] = j;

        // netconvert names clustered junctions "cluster_<id>_<id>..._#Nmore", so the signal and
        // controller ids we derive from them ("{junction}_{k}") blow past CARLA's 32-char SignId
        // limit and get truncated (per-frame log spam + possible id collisions). Alias any junction
        // id that would overflow to a short unique stand-in, and rewrite its signals' ids to match.
        var shortByTl = new Dictionary<string, string>();
        int aliasCounter = 0;
        foreach (var tlId in programs.Keys)
            shortByTl[tlId] = tlId.Length > 24 ? "t" + aliasCounter++ : tlId;

        // signal ids actually present (after aliasing), so we only reference heads that exist; plus,
        // per traffic-light head, its (road, station, lateral offset, element) so we can collapse an
        // approach's several heads down to one pole below.
        var signalIds = new HashSet<string>();
        var tlHeads = new Dictionary<string,
            (string RoadId, double S, double T, XElement El, int LaneMin, int LaneMax)>();
        foreach (var road in root.Elements("road"))
        {
            string roadId = road.Attribute("id")?.Value ?? "";
            foreach (var s in road.Elements("signals").Elements("signal"))
            {
                if (s.Attribute("id") is not XAttribute idAttr)
                    continue;
                string sid = idAttr.Value;
                int cut = sid.LastIndexOf('_');
                if (cut > 0 && shortByTl.TryGetValue(sid[..cut], out var alias))
                {
                    // A traffic-light head. Rename it if its junction id was aliased, and zero its
                    // zOffset: netconvert lifts light signals ~5 m (a mast height), but the CARLA
                    // traffic-light Blueprint already models the pole + arm up from its base, so the
                    // offset floats the whole pole above the road.
                    if (alias != sid[..cut])
                        sid = idAttr.Value = alias + sid[cut..]; // "{longJunction}_{k}" -> "{alias}_{k}"
                    s.SetAttributeValue("zOffset", "0");
                    // netconvert leaves the light facing along the road tangent (orientation "+", no
                    // hOffset), which points its face the way traffic travels — its back to the
                    // drivers who must obey it. Rotate 180 deg so it faces the oncoming approach.
                    s.SetAttributeValue("hOffset", "3.14159265");
                    // netconvert gives each head a single-lane <validity>; keep its lane span so the
                    // approach's surviving pole can inherit the coverage of the heads we drop below.
                    var val = s.Element("validity");
                    int laneA = (int)ParseNum(val?.Attribute("fromLane")?.Value);
                    int laneB = (int)ParseNum(val?.Attribute("toLane")?.Value);
                    tlHeads[sid] = (roadId, ParseNum(s.Attribute("s")?.Value),
                        ParseNum(s.Attribute("t")?.Value), s,
                        Math.Min(laneA, laneB), Math.Max(laneA, laneB));
                }
                signalIds.Add(sid);
            }
        }

        // One pole per approach. netconvert emits one <signal> per head and CARLA spawns a full
        // mast-arm assembly per signal, so an approach's several heads stack as several poles across
        // the road. Group heads by approach (road + stop-line station) and keep only the roadside-most
        // (largest |t|) as the representative pole — its mast arm then reaches back over the lanes.
        var approachRep = new Dictionary<string, (string Head, double AbsT, int LaneMin, int LaneMax)>();
        foreach (var (head, pos) in tlHeads)
        {
            string key = pos.RoadId + ":" + Math.Round(pos.S).ToString(CultureInfo.InvariantCulture);
            double absT = Math.Abs(pos.T);
            if (!approachRep.TryGetValue(key, out var cur))
            {
                approachRep[key] = (head, absT, pos.LaneMin, pos.LaneMax);
                continue;
            }
            // Keep the roadside-most head as the pole, but accumulate every head's lane coverage.
            int laneMin = Math.Min(cur.LaneMin, pos.LaneMin);
            int laneMax = Math.Max(cur.LaneMax, pos.LaneMax);
            approachRep[key] = absT > cur.AbsT
                ? (head, absT, laneMin, laneMax)
                : (cur.Head, cur.AbsT, laneMin, laneMax);
        }
        var keptHeads = new HashSet<string>(approachRep.Values.Select(v => v.Head));

        // The surviving pole must govern EVERY lane of its approach. CARLA builds one stop-line
        // trigger box per lane listed in a signal's <validity> (TrafficLightComponent::InitializeSign),
        // and netconvert scopes each head's validity to the single lane that head hangs over. Dropping
        // the other heads without merging their validity would leave their lanes with no trigger box,
        // so vehicles in them would never register the light and would drive straight through it.
        foreach (var rep in approachRep.Values)
        {
            if (!tlHeads.TryGetValue(rep.Head, out var pos))
                continue;
            var validity = pos.El.Element("validity");
            if (validity == null)
            {
                validity = new XElement("validity");
                pos.El.Add(validity);
            }
            validity.SetAttributeValue("fromLane", rep.LaneMin.ToString(CultureInfo.InvariantCulture));
            validity.SetAttributeValue("toLane", rep.LaneMax.ToString(CultureInfo.InvariantCulture));
        }

        // Move each surviving pole to the FAR side of its junction, at the roadside, facing back toward
        // the approach. A real signal is across the intersection from the driver who obeys it; netconvert
        // places it near the stop line facing along the road. Two components:
        //   - forward: measure how far this approach's own paths through the junction reach. Each junction
        //     <connection> is an internal "connecting road" tracing one movement (this approach -> an
        //     exit); the farthest any of them extends along the travel direction is where the approach
        //     leaves the intersection — the true far side. Using only THIS approach's connecting roads
        //     keeps it correct on clustered junctions (several merged OSM nodes), where a whole-junction
        //     centroid would place the far side across the entire blob. Falls back to a capped reflection
        //     through the junction centroid when the connecting roads aren't resolvable.
        //   - lateral: seat the pole just beyond the drivable edge (summed lane widths) on the head's
        //     side. GetDirectedPointInNoLaneOffset returns the CENTRELINE point, and the head's own t is
        //     mid-lane, so without this the pole sits in the roadway / crosswalk.
        // Falls back to the near-side road-relative placement when the geometry isn't available.
        if (geoMap != null)
        {
            var elevatedRoads = ElevatedRoadIds(root);
            var centreByAlias = new Dictionary<string, (double X, double Y)>();
            // alias -> (incoming road id -> its connecting-road ids), for the per-approach far-side measure.
            var connByAlias = new Dictionary<string, Dictionary<string, List<uint>>>();
            // alias -> every connecting-road id of the junction, in document order. A relocated pole
            // stands at a corner the whole junction shares, so the ground under it is resampled
            // against all of them, not only its own approach's.
            var junctionRoadsByAlias = new Dictionary<string, List<uint>>();
            foreach (var (tlId, alias) in shortByTl)
            {
                if (!junctionByName.TryGetValue(tlId, out var jn)) continue;
                if (TryJunctionCentre(jn, geoMap, out var centre)) centreByAlias[alias] = centre;
                var byIncoming = new Dictionary<string, List<uint>>();
                var allConnecting = new List<uint>();
                var seenConnecting = new HashSet<uint>();
                foreach (var conn in jn.Elements("connection"))
                {
                    string inc = conn.Attribute("incomingRoad")?.Value ?? "";
                    if (inc.Length == 0 || !uint.TryParse(conn.Attribute("connectingRoad")?.Value, out var crid))
                        continue;
                    if (!byIncoming.TryGetValue(inc, out var lst)) byIncoming[inc] = lst = new List<uint>();
                    lst.Add(crid);
                    if (seenConnecting.Add(crid)) allConnecting.Add(crid);
                }
                connByAlias[alias] = byIncoming;
                junctionRoadsByAlias[alias] = allConnecting;
            }

            foreach (var head in keptHeads)
            {
                if (!tlHeads.TryGetValue(head, out var pos)) continue;
                int u = head.LastIndexOf('_');
                if (u <= 0 || !centreByAlias.TryGetValue(head[..u], out var c)) continue;
                if (!uint.TryParse(pos.RoadId, out var rid) || !geoMap.Roads.TryGetValue(rid, out var rd)) continue;

                var dp = Road.Map.GetDirectedPointInNoLaneOffset(rd, pos.S); // centreline point + tangent
                double cx0 = dp.Location.X, cy0 = dp.Location.Y, z = dp.Location.Z;
                double tanX = Math.Cos(dp.Tangent), tanY = Math.Sin(dp.Tangent);
                double nX = Math.Sin(dp.Tangent), nY = -Math.Cos(dp.Tangent); // ApplyLateralOffset normal

                // Orient the tangent toward the junction (so "forward" crosses it).
                double toCx = c.X - cx0, toCy = c.Y - cy0;
                double distToCentre = Math.Sqrt(toCx * toCx + toCy * toCy);
                if (distToCentre < 1e-3) continue;             // stop line ~ at the centre: leave near-side
                if (tanX * toCx + tanY * toCy < 0) { tanX = -tanX; tanY = -tanY; }

                // Forward distance: how far this approach's connecting roads reach along the travel
                // direction (its exit from the junction), else the capped centroid reflection.
                double cross;
                if (connByAlias.TryGetValue(head[..u], out var byInc)
                    && byInc.TryGetValue(pos.RoadId, out var connIds)
                    && FarExitDistance(geoMap, connIds, cx0, cy0, tanX, tanY) is { } dFar && dFar > 1.0)
                    cross = Math.Min(dFar + FarSideClearanceMeters, MaxFarSideMeters);
                else
                    cross = Math.Min(2.0 * distToCentre, MaxFarSideMeters);

                double edge = RoadEdgeDistance(pos.El.Parent?.Parent, pos.T, pos.S) + SidewalkMarginMeters;
                double lateral = pos.T == 0 ? 0 : Math.Sign(-pos.T) * edge;
                double fx = cx0 + tanX * cross + lateral * nX; // far side + roadside curb offset
                double fy = cy0 + tanY * cross + lateral * nY;

                // The pole has moved metres across the junction, so the ground beneath it is no longer
                // the ground at the stop line whose elevation z was read from. On a graded approach
                // keeping that elevation buries the mast — taking with it the clearance a tall vehicle
                // needs under its arm — or leaves it floating. Resample against the junction's own
                // roads; they are the surface the pole now stands beside.
                if (junctionRoadsByAlias.TryGetValue(head[..u], out var junctionRoads)
                    && NearestCentrelineElevation(geoMap, junctionRoads, elevatedRoads, fx, fy) is { } farZ)
                    z = farZ;

                double hdg = Math.Atan2(tanY, tanX);            // face back toward the oncoming approach
                pos.El.SetAttributeValue("hOffset", null);      // positionInertial carries the full yaw
                pos.El.Add(new XElement("positionInertial",
                    new XAttribute("x", F(fx)), new XAttribute("y", F(fy)), new XAttribute("z", F(z)),
                    new XAttribute("hdg", F(hdg)),
                    new XAttribute("pitch", "0"), new XAttribute("roll", "0")));
            }
        }

        // Drop netconvert's single all-heads controllers (ids equal to a tlLogic id); we rebuild
        // them per phase below.
        foreach (var c in root.Elements("controller").ToList())
            if (c.Attribute("id")?.Value is string cid && programs.ContainsKey(cid))
                c.Remove();

        var newControllers = new List<XElement>();
        int junctionsWired = 0, controllersEmitted = 0, unmatched = 0;

        // A representative pole belongs to exactly one controller (the phase it is first green in);
        // guard against a head that is green in more than one phase (permissive movements).
        var placedHeads = new HashSet<string>();

        foreach (var (tlId, phases) in programs)
        {
            if (!junctionByName.TryGetValue(tlId, out var junction))
            {
                unmatched++;
                continue; // no junction to attach to — skip rather than orphan
            }

            string alias = shortByTl[tlId];
            int phaseIndex = 0;
            foreach (var greenLinks in phases)
            {
                var heads = new List<string>();
                foreach (var k in greenLinks)
                {
                    string head = $"{alias}_{k}";
                    if (signalIds.Contains(head) && keptHeads.Contains(head) && placedHeads.Add(head))
                        heads.Add(head);
                }
                if (heads.Count == 0)
                {
                    phaseIndex++;
                    continue;
                }

                string controllerId = $"{alias}_p{phaseIndex}";
                var controller = new XElement("controller", new XAttribute("id", controllerId));
                foreach (var head in heads)
                    controller.Add(new XElement("control", new XAttribute("signalId", head)));
                newControllers.Add(controller);
                controllersEmitted++;

                junction.Add(new XElement("controller",
                    new XAttribute("id", controllerId),
                    new XAttribute("type", "0"),
                    new XAttribute("sequence", phaseIndex.ToString())));
                phaseIndex++;
            }
            junctionsWired++;
        }

        // Remove the redundant heads of each approach (everything but the representative pole).
        int dropped = 0;
        foreach (var (id, pos) in tlHeads)
            if (!keptHeads.Contains(id))
            {
                pos.El.Remove();
                dropped++;
            }

        // Top-level <controller>s live after the roads and before the junctions.
        var firstJunction = root.Elements("junction").FirstOrDefault();
        if (firstJunction != null)
            firstJunction.AddBeforeSelf(newControllers);
        else
            root.Add(newControllers);

        Console.WriteLine(
            $"[TrafficLightInjector] tlLogic programs={programs.Count} junctions wired={junctionsWired} " +
            $"phase-controllers emitted={controllersEmitted} unmatched-programs={unmatched} " +
            $"poles kept={keptHeads.Count} heads dropped={dropped}");

        return doc.ToString(SaveOptions.None);
    }

    private static double ParseNum(string? v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

    // Distance from the road centreline to the outer edge of the drivable surface on the head's side
    // (t &lt; 0 = right / negative lane ids), by summing lane widths at station <paramref name="s"/>.
    // Falls back to |t| when the road/lane data isn't usable. Reads the .xodr lane widths directly
    // (a + b·ds + c·ds² + d·ds³) rather than the parsed model, since only the signal element is in hand.
    private static double RoadEdgeDistance(XElement? roadEl, double t, double s)
    {
        if (roadEl == null) return Math.Abs(t);

        // Lane section active at s = the one with the greatest sOffset &le; s.
        XElement? section = null;
        double sectionS = double.NegativeInfinity;
        foreach (var sec in roadEl.Elements("lanes").Elements("laneSection"))
        {
            double ss = ParseNum(sec.Attribute("s")?.Value);
            if (ss <= s + 1e-6 && ss > sectionS) { sectionS = ss; section = sec; }
        }
        if (section?.Element(t < 0 ? "right" : "left") is not XElement grp)
            return Math.Abs(t);

        double ds = s - sectionS, sum = 0;
        foreach (var w in grp.Elements("lane").Elements("width"))
            sum += ParseNum(w.Attribute("a")?.Value) + ParseNum(w.Attribute("b")?.Value) * ds
                 + ParseNum(w.Attribute("c")?.Value) * ds * ds
                 + ParseNum(w.Attribute("d")?.Value) * ds * ds * ds;
        return sum > 0 ? sum : Math.Abs(t);
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // Farthest that any of the given connecting roads reaches along the (unit) travel direction
    // (dx, dy) from the approach point (ox, oy) — i.e. where this approach's paths exit the junction.
    // Returns null when none of the roads resolve to usable geometry.
    private static double? FarExitDistance(
        Road.Map map, List<uint> connRoadIds, double ox, double oy, double dx, double dy)
    {
        double maxProj = double.NegativeInfinity;
        foreach (var rid in connRoadIds)
        {
            if (!map.Roads.TryGetValue(rid, out var road) || road.Length <= 0.0)
                continue;
            const int samples = 6;
            for (int i = 0; i <= samples; i++)
            {
                var loc = Road.Map.GetDirectedPointInNoLaneOffset(road, road.Length * i / samples).Location;
                double proj = (loc.X - ox) * dx + (loc.Y - oy) * dy;
                if (proj > maxProj) maxProj = proj;
            }
        }
        return double.IsNegativeInfinity(maxProj) ? null : maxProj;
    }

    // Road ids that carry an actual &lt;elevationProfile&gt;. The parser hands every road a default zero
    // elevation record when the .xodr gives it none, so the parsed model cannot tell "at the datum"
    // from "no data" — and on an elevated map that zero sits tens of metres under the road.
    private static HashSet<uint> ElevatedRoadIds(XElement root)
    {
        var ids = new HashSet<uint>();
        foreach (var roadEl in root.Elements("road"))
            if (uint.TryParse(roadEl.Attribute("id")?.Value, out var rid)
                && roadEl.Element("elevationProfile")?.Elements("elevation").Any() == true)
                ids.Add(rid);
        return ids;
    }

    // Reference-line elevation of <paramref name="roadIds"/> at the sample nearest (x, y), or null when
    // none of them has elevation data within ElevationSearchRadiusMeters of it. Roads outside
    // <paramref name="elevatedRoads"/> are skipped rather than read as zero, which would bury the pole.
    private static double? NearestCentrelineElevation(
        Road.Map map, IReadOnlyList<uint> roadIds, IReadOnlySet<uint> elevatedRoads, double x, double y)
    {
        double nearestSq = ElevationSearchRadiusMeters * ElevationSearchRadiusMeters;
        double? nearestZ = null;
        foreach (var rid in roadIds)
        {
            if (!elevatedRoads.Contains(rid))
                continue;
            if (!map.Roads.TryGetValue(rid, out var road) || road.Length <= 0.0)
                continue;
            int steps = Math.Max(1, (int)Math.Ceiling(road.Length / ElevationSampleStepMeters));
            for (int i = 0; i <= steps; i++)
            {
                var loc = Road.Map.GetDirectedPointInNoLaneOffset(road, road.Length * i / steps).Location;
                double dx = loc.X - x, dy = loc.Y - y;
                double distSq = dx * dx + dy * dy;
                if (distSq < nearestSq) { nearestSq = distSq; nearestZ = loc.Z; }
            }
        }
        return nearestZ;
    }

    // Junction centre = the average of its connecting (internal) roads' midpoints, in the planView
    // frame. Returns false when the junction has no usable connecting-road geometry.
    private static bool TryJunctionCentre(XElement junction, Road.Map geoMap, out (double X, double Y) centre)
    {
        double sx = 0, sy = 0;
        int n = 0;
        foreach (var conn in junction.Elements("connection"))
        {
            if (!uint.TryParse(conn.Attribute("connectingRoad")?.Value, out var rid))
                continue;
            if (!geoMap.Roads.TryGetValue(rid, out var road) || road.Length <= 0.0)
                continue;
            var loc = Road.Map.GetDirectedPointInNoLaneOffset(road, road.Length / 2.0).Location;
            sx += loc.X;
            sy += loc.Y;
            n++;
        }
        centre = n > 0 ? (sx / n, sy / n) : default;
        return n > 0;
    }

    // Parse each <tlLogic> into its distinct green phases (a phase = the set of link indices that
    // are green together). A "green" character is 'G' or 'g'; duplicate green state strings (the
    // same movement group recurring in the cycle) are collapsed so we emit one controller per
    // distinct movement group.
    private static Dictionary<string, List<List<int>>> ParseGreenPhases(string netXml)
    {
        var result = new Dictionary<string, List<List<int>>>();
        var net = XDocument.Parse(netXml).Root;
        if (net is null)
            return result;

        foreach (var tl in net.Elements("tlLogic"))
        {
            if (tl.Attribute("id")?.Value is not string id)
                continue;

            var phases = new List<List<int>>();
            var seenGreenStates = new HashSet<string>();
            foreach (var ph in tl.Elements("phase"))
            {
                string state = ph.Attribute("state")?.Value ?? "";
                if (!state.Any(c => c is 'G' or 'g'))
                    continue; // yellow/all-red transition phase, not a movement group
                if (!seenGreenStates.Add(state))
                    continue; // this movement group already recorded

                var greenLinks = new List<int>();
                for (int k = 0; k < state.Length; k++)
                    if (state[k] is 'G' or 'g')
                        greenLinks.Add(k);
                phases.Add(greenLinks);
            }

            if (phases.Count > 0)
                result[id] = phases;
        }
        return result;
    }
}
