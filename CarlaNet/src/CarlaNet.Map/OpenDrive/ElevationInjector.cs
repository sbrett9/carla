// Phase B of the dynamic georeferenced-world pipeline (see
// carla/Docs/CAT_Research/DYNAMIC_WORLD_PIPELINE_PLAN.md).
//
// The flat .xodr that netconvert/OsmConverter produces has no <elevationProfile>,
// so CARLA builds dead-flat roads that float above / sink below the 3D Cesium
// terrain. This class post-processes the .xodr to make roads conform to Cesium:
//
//   ExtractCenterlineSamples(map, step)  -> per-road (roadId, s, x, y) reference-line points
//   ToGeo(samples, origin)               -> (roadId, s, lat, lon)   [via Track-B Geodesy]
//   -- engine boundary: ACesium3DTileset.SampleHeightMostDetailed gives ellipsoidal heights --
//   InjectElevation(xodr, samples, heights, originHeight) -> elevated .xodr
//
// Coordinate frames (the subtle bit): the OpenDRIVE planView is +X=East, +Y=North.
// CARLA's world frame is +X=East, -Y=North (the "Unreal Y-axis hack"). We emit
// samples in the CARLA world frame and convert with Geodesy.CarlaLocalToGeodetic so
// the elevation hand-off shares ONE projection with Track-B telemetry truth (this is
// what kills the tmerc-vs-spherical-Mercator drift class — plan §3).
//
// Nothing in CARLA's road model or mesh code changes: both the road mesh and the
// waypoint z already follow OpenDRIVE <elevation> (see Map.GetDirectedPointIn). The
// entire fix is producing the <elevationProfile> text.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.Road;
using CarlaNet.Map.Road.Element;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.OpenDrive;

/// <summary>A reference-line sample in the CARLA world frame (metres, +X=East, -Y=North).</summary>
public readonly record struct CenterlineSample(RoadId RoadId, double S, double X, double Y);

/// <summary>A reference-line sample reprojected to WGS84 (degrees).</summary>
public readonly record struct GeoSample(RoadId RoadId, double S, double Latitude, double Longitude);

/// <summary>How the per-road &lt;elevation&gt; cubic records are fitted between samples.</summary>
public enum ElevationFitMode
{
    /// <summary>Each record is a flat step (a=z, b=c=d=0). Discontinuous but trivially safe.</summary>
    PiecewiseConstant,

    /// <summary>Each record ramps linearly to the next sample (a=z, b=slope, c=d=0). Continuous.</summary>
    PiecewiseLinear,

    /// <summary>
    /// Monotone cubic Hermite (PCHIP) with Fritsch-Carlson tangent limiting: real c and d, so
    /// consecutive records agree in slope as well as height and the surface has no crease at a
    /// sample boundary. The limiting keeps the curve inside the bracketing sample values on
    /// monotone runs, which an unconstrained spline does not — on a noisy photogrammetric height
    /// series that would invent humps and dips that were never sampled.
    ///
    /// This mode also carries slope through a junction. Roads linked by an
    /// <c>elementType="road"</c> link — in generated maps, always a junction connector and the
    /// road it joins — have their shared endpoint height and slope resolved to one value, and the
    /// last record on every road carries a real tangent instead of the zero the other modes leave.
    /// </summary>
    MonotoneCubicHermite
}

public static class ElevationInjector
{
    // ── P2: extract reference-line sample points from the flat map ───────────

    /// <summary>
    /// Walks each road's reference line and emits points every <paramref name="stepMeters"/>
    /// (plus the exact road end). Output is in the CARLA world frame. Roads without any
    /// planView geometry are skipped. No server / world load required.
    /// </summary>
    public static IReadOnlyList<CenterlineSample> ExtractCenterlineSamples(Road.Map map, double stepMeters)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (stepMeters <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(stepMeters), "step must be positive");

        var samples = new List<CenterlineSample>();

        // Deterministic road order so sample indices are stable across runs.
        foreach (var roadId in map.Roads.Keys.OrderBy(k => k))
        {
            var road = map.Roads[roadId];
            if (road.Length <= 0.0)
                continue;
            if (!road.Info.All.OfType<RoadInfoGeometry>().Any())
                continue; // no planView geometry -> can't place it

            foreach (var s in StationsAlong(road.Length, stepMeters))
            {
                var dp = Road.Map.GetDirectedPointInNoLaneOffset(road, s);
                // planView is +Y=North; CARLA world is -Y=North -> flip Y.
                samples.Add(new CenterlineSample(roadId, s, dp.Location.X, -dp.Location.Y));
            }
        }

        return samples;
    }

    /// <summary>s = 0, step, 2·step, … (strictly &lt; length), then exactly length.</summary>
    private static IEnumerable<double> StationsAlong(double length, double step)
    {
        const double eps = 1e-6;
        for (double s = 0.0; s < length - eps; s += step)
            yield return s;
        yield return length;
    }

    // ── P3: reproject to WGS84 via the Track-B ellipsoidal transform ─────────

    /// <summary>
    /// Reprojects samples to WGS84 using the shared ENU-tangent ellipsoidal transform
    /// (<see cref="Geodesy.CarlaLocalToGeodetic(GeoLocation,double,double,double)"/>), the
    /// SAME projection telemetry truth uses. <paramref name="origin"/> is the georeference
    /// pin (its altitude is irrelevant — height sampling ignores the input Z).
    /// </summary>
    public static IReadOnlyList<GeoSample> ToGeo(IReadOnlyList<CenterlineSample> samples, GeoLocation origin)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var result = new List<GeoSample>(samples.Count);
        foreach (var s in samples)
        {
            var g = Geodesy.CarlaLocalToGeodetic(origin, s.X, s.Y, 0.0);
            result.Add(new GeoSample(s.RoadId, s.S, g.Latitude, g.Longitude));
        }
        return result;
    }

    // ── P5: inject the sampled heights back into the .xodr ───────────────────

    /// <summary>
    /// Rewrites <paramref name="openDriveXml"/> so each sampled road carries an
    /// &lt;elevationProfile&gt; that conforms to the supplied heights. <paramref name="samples"/>
    /// and <paramref name="ellipsoidalHeights"/> are index-aligned (same order
    /// <see cref="ExtractCenterlineSamples"/> / <see cref="ToGeo"/> produced). Injected
    /// z = ellipsoidalHeight − <paramref name="originHeight"/>, so the georeference origin sits
    /// at z=0 and matches <c>CesiumGeoreference.OriginHeight</c>. Failed samples (NaN height)
    /// are filled by linear interpolation / nearest-hold within their road. Existing
    /// &lt;elevationProfile&gt; elements on touched roads are replaced. Other roads are untouched.
    ///
    /// <paramref name="deliberatelyRaisedSamples"/> marks samples whose height came from a known
    /// vertical structure (a bridge deck routed to the photoreal surface by
    /// <see cref="GradeSeparation"/>) rather than from a raw terrain sample. Those are exempt from
    /// outlier rejection, which would otherwise flatten a short deck back into the road under it —
    /// the very defect the layer routing exists to fix.
    /// </summary>
    public static string InjectElevation(
        string openDriveXml,
        IReadOnlyList<CenterlineSample> samples,
        IReadOnlyList<double> ellipsoidalHeights,
        double originHeight,
        ElevationFitMode mode = ElevationFitMode.PiecewiseLinear,
        double outlierThresholdMeters = 4.0,
        IReadOnlyList<bool>? deliberatelyRaisedSamples = null)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(ellipsoidalHeights);
        if (samples.Count != ellipsoidalHeights.Count)
            throw new ArgumentException(
                $"samples ({samples.Count}) and heights ({ellipsoidalHeights.Count}) must be the same length");
        if (deliberatelyRaisedSamples is not null && deliberatelyRaisedSamples.Count != samples.Count)
            throw new ArgumentException(
                $"raised-sample flags ({deliberatelyRaisedSamples.Count}) must match samples ({samples.Count})");

        // Group (s, z) by road, preserving ascending-s order, z relative to origin.
        var perRoad = new Dictionary<RoadId, List<(double S, double Z, bool Raised)>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            double rawZ = ellipsoidalHeights[i] - originHeight;
            var list = perRoad.TryGetValue(samples[i].RoadId, out var existing)
                ? existing
                : (perRoad[samples[i].RoadId] = new List<(double, double, bool)>());
            list.Add((samples[i].S, rawZ, deliberatelyRaisedSamples?[i] ?? false));
        }

        foreach (var list in perRoad.Values)
        {
            list.Sort((a, b) => a.S.CompareTo(b.S));
            FillGaps(list);                                  // failed samples (NaN) first
            RejectOutliers(list, outlierThresholdMeters);    // L-tracks/trees/awnings/roofs -> NaN
            FillGaps(list);                                  // interpolate the rejected spikes
        }

        var doc = XDocument.Parse(openDriveXml);
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document (no root)");

        // Sharing a height series between the two directions of a street, and carrying slope
        // through a junction, both ride with the C1 fit; the piecewise modes stay exactly as they
        // were, so selecting one of them reproduces the previous output byte for byte.
        Dictionary<RoadEnd, double> endTangents;
        if (mode == ElevationFitMode.MonotoneCubicHermite)
        {
            // Before the junction pass, so resolved node heights see the shared series.
            var carriageways = MergeOpposingCarriageways(openDriveXml, perRoad);
            endTangents = ResolveLinkedRoadEnds(root, perRoad, carriageways);
        }
        else
        {
            endTangents = new Dictionary<RoadEnd, double>();
        }

        foreach (var roadNode in root.Elements("road"))
        {
            if (!uint.TryParse(roadNode.Attribute("id")?.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id))
                continue;
            if (!perRoad.TryGetValue(id, out var profile) || profile.Count == 0)
                continue;

            endTangents.TryGetValue(new RoadEnd(id, true), out double atStart);
            endTangents.TryGetValue(new RoadEnd(id, false), out double atEnd);
            roadNode.Elements("elevationProfile").Remove();
            var elevationProfile = BuildElevationProfile(
                profile, mode,
                endTangents.ContainsKey(new RoadEnd(id, true)) ? atStart : null,
                endTangents.ContainsKey(new RoadEnd(id, false)) ? atEnd : null);
            InsertElevationProfile(roadNode, elevationProfile);
        }

        return doc.ToString(SaveOptions.None);
    }

    /// <summary>Convenience: extract → (caller samples heights) is external, so this is for tests/symmetry.</summary>
    private static XElement BuildElevationProfile(
        List<(double S, double Z, bool Raised)> profile,
        ElevationFitMode mode,
        double? tangentAtStart = null,
        double? tangentAtEnd = null)
    {
        if (mode == ElevationFitMode.MonotoneCubicHermite)
            return BuildMonotoneCubicProfile(profile, tangentAtStart, tangentAtEnd);

        var elevationProfile = new XElement("elevationProfile");
        for (int i = 0; i < profile.Count; ++i)
        {
            double s = profile[i].S;
            double a = profile[i].Z;
            double b = 0.0;
            if (mode == ElevationFitMode.PiecewiseLinear && i + 1 < profile.Count)
            {
                double ds = profile[i + 1].S - s;
                if (ds > 1e-9)
                    b = (profile[i + 1].Z - a) / ds;
            }
            elevationProfile.Add(new XElement("elevation",
                new XAttribute("s", F(s)),
                new XAttribute("a", F(a)),
                new XAttribute("b", F(b)),
                new XAttribute("c", F(0.0)),
                new XAttribute("d", F(0.0))));
        }
        return elevationProfile;
    }

    // ── monotone cubic Hermite (PCHIP) ───────────────────────────────────────

    /// <summary>Stations closer together than this are merged before fitting.</summary>
    /// <remarks>
    /// The sampler walks 0, step, 2·step, … and then jumps to the road length, so the last span is
    /// whatever the length leaves over — millimetres on some roads. Dividing a step's worth of
    /// sampling noise by such a span makes the cubic coefficients explode, so the remainder is
    /// absorbed into the span before it rather than fitted through.
    /// </remarks>
    private const double MinFitSpanMeters = 1.0;

    /// <summary>Distance over which a road-end grade is estimated, one sample step.</summary>
    private const double EndGradeWindowMeters = 10.0;

    private static XElement BuildMonotoneCubicProfile(
        List<(double S, double Z, bool Raised)> profile, double? tangentAtStart, double? tangentAtEnd)
    {
        var (s, z) = MergeShortSpans(profile);
        var elevationProfile = new XElement("elevationProfile");
        int n = s.Count;
        if (n == 0)
            return elevationProfile;
        if (n == 1)
        {
            elevationProfile.Add(Record(s[0], z[0], 0.0, 0.0, 0.0));
            return elevationProfile;
        }

        var spans = new double[n - 1];
        var secants = new double[n - 1];
        for (int i = 0; i < n - 1; ++i)
        {
            spans[i] = s[i + 1] - s[i];
            secants[i] = (z[i + 1] - z[i]) / spans[i];
        }

        var tangents = new double[n];
        for (int i = 1; i < n - 1; ++i)
            tangents[i] = (spans[i] * secants[i - 1] + spans[i - 1] * secants[i])
                          / (spans[i - 1] + spans[i]);
        tangents[0] = tangentAtStart ?? EndGrade(s, z, atStart: true);
        tangents[n - 1] = tangentAtEnd ?? EndGrade(s, z, atStart: false);
        LimitTangents(tangents, secants);

        for (int i = 0; i < n - 1; ++i)
        {
            double h = spans[i], delta = secants[i];
            double m0 = tangents[i], m1 = tangents[i + 1];
            elevationProfile.Add(Record(
                s: s[i],
                a: z[i],
                b: m0,
                c: (3.0 * delta - 2.0 * m0 - m1) / h,
                d: (m0 + m1 - 2.0 * delta) / (h * h)));
        }
        // The record at road end governs the pitch reported there and the grade handed to the
        // successor road, so it extends the curve at its own tangent rather than flattening.
        elevationProfile.Add(Record(s[^1], z[^1], tangents[n - 1], 0.0, 0.0));
        return elevationProfile;
    }

    private static XElement Record(double s, double a, double b, double c, double d) =>
        new("elevation",
            new XAttribute("s", F(s)),
            new XAttribute("a", F(a)),
            new XAttribute("b", F(b)),
            new XAttribute("c", F(c)),
            new XAttribute("d", F(d)));

    /// <summary>Drops interior stations closer than <see cref="MinFitSpanMeters"/> to the one kept
    /// before them. The first and last are always kept: the last is the road end, whose height the
    /// next road meets.</summary>
    private static (List<double> S, List<double> Z) MergeShortSpans(
        List<(double S, double Z, bool Raised)> profile)
    {
        var s = new List<double>(profile.Count);
        var z = new List<double>(profile.Count);
        if (profile.Count == 0)
            return (s, z);

        s.Add(profile[0].S);
        z.Add(profile[0].Z);
        if (profile.Count < 3)
        {
            for (int i = 1; i < profile.Count; ++i) { s.Add(profile[i].S); z.Add(profile[i].Z); }
            return (s, z);
        }

        for (int i = 1; i < profile.Count - 1; ++i)
        {
            if (profile[i].S - s[^1] < MinFitSpanMeters)
                continue;
            s.Add(profile[i].S);
            z.Add(profile[i].Z);
        }
        if (s.Count > 1 && profile[^1].S - s[^1] < MinFitSpanMeters)
        {
            s.RemoveAt(s.Count - 1);
            z.RemoveAt(z.Count - 1);
        }
        s.Add(profile[^1].S);
        z.Add(profile[^1].Z);
        return (s, z);
    }

    /// <summary>Least-squares grade over the samples within one step of a road end.</summary>
    /// <remarks>
    /// Taking the slope of the end span alone is what manufactures artificial grades: that span is
    /// a remainder of the road length and can be millimetres long, so a step's worth of sampling
    /// noise across it reads as an arbitrarily steep road.
    /// </remarks>
    private static double EndGrade(List<double> s, List<double> z, bool atStart)
    {
        double anchor = atStart ? s[0] : s[^1];
        double sumS = 0.0, sumZ = 0.0, sumSS = 0.0, sumSZ = 0.0;
        int count = 0;
        for (int i = 0; i < s.Count; ++i)
        {
            if (Math.Abs(s[i] - anchor) > EndGradeWindowMeters)
                continue;
            sumS += s[i]; sumZ += z[i]; sumSS += s[i] * s[i]; sumSZ += s[i] * z[i];
            ++count;
        }
        if (count < 2)
        {
            int a = atStart ? 0 : s.Count - 2;
            int b = atStart ? 1 : s.Count - 1;
            double span = s[b] - s[a];
            return span > 1e-9 ? (z[b] - z[a]) / span : 0.0;
        }
        double denominator = count * sumSS - sumS * sumS;
        return Math.Abs(denominator) < 1e-12 ? 0.0 : (count * sumSZ - sumS * sumZ) / denominator;
    }

    /// <summary>Fritsch-Carlson limiting, in place — the guarantee against overshoot.</summary>
    /// <remarks>
    /// On each span the tangent pair is pulled back inside the circle of radius 3 in
    /// (α, β) = (m_i/Δ, m_{i+1}/Δ), the sufficient condition for the interpolant to stay monotone
    /// there. Flat spans and reversals against the local secant are flattened outright. A tangent
    /// imposed for junction continuity is limited too: a road that cannot carry that grade without
    /// overshooting its own samples gives up the shared slope rather than the surface.
    /// </remarks>
    private static void LimitTangents(double[] tangents, double[] secants)
    {
        for (int i = 0; i < secants.Length; ++i)
        {
            double delta = secants[i];
            if (Math.Abs(delta) < 1e-12)
            {
                tangents[i] = 0.0;
                tangents[i + 1] = 0.0;
                continue;
            }
            if (tangents[i] * delta < 0.0) tangents[i] = 0.0;
            if (tangents[i + 1] * delta < 0.0) tangents[i + 1] = 0.0;
            double alpha = tangents[i] / delta, beta = tangents[i + 1] / delta;
            double magnitude = alpha * alpha + beta * beta;
            if (magnitude > 9.0)
            {
                double tau = 3.0 / Math.Sqrt(magnitude);
                tangents[i] = tau * alpha * delta;
                tangents[i + 1] = tau * beta * delta;
            }
        }
    }

    // ── opposing carriageways of one street ──────────────────────────────────

    /// <summary>How far apart two reference lines may be and still be the same centreline.</summary>
    private const double CarriagewayCoincidenceMeters = 0.5;

    /// <summary>
    /// Gives the two directions of one street a single height series.
    ///
    /// netconvert models each direction of a two-way street as its own edge, so one physical
    /// street arrives as two &lt;road&gt; records sharing a centreline, each carrying its lane on its
    /// own side. Sampled and fitted independently, the two disagree — and because road A's station
    /// <c>s</c> is road B's station <c>length − s</c>, their sample grids land at different
    /// physical points, so the disagreement is largest exactly where the grade is steepest. The
    /// two halves of one street then meet along its centre line at different heights.
    ///
    /// Where the two disagree the lower is taken, not the average. Both sample the same ground
    /// through a surface model that includes whatever stands on it, so the error is one-sided: a
    /// sample can land on a canopy or a deck above the road, never below it. Pairs carrying
    /// deliberately raised samples are left alone — there the height difference is a real grade
    /// separation rather than a sampling error.
    /// </summary>
    private static List<(RoadId Left, RoadId Right)> MergeOpposingCarriageways(
        string openDriveXml, Dictionary<RoadId, List<(double S, double Z, bool Raised)>> perRoad)
    {
        var merged = new List<(RoadId, RoadId)>();
        var map = OpenDriveParser.Load(openDriveXml);
        if (map is null)
            return merged;

        foreach (var (left, right) in FindOpposingCarriageways(map, perRoad))
        {
            var a = perRoad[left];
            var b = perRoad[right];
            if (a.Any(p => p.Raised) || b.Any(p => p.Raised))
                continue;

            double length = a[^1].S;
            // Both series expressed on the same physical axis: road B runs the other way, so its
            // station s sits at length − s along road A.
            var mirrored = b.Select(p => (S: length - p.S, p.Z)).OrderBy(p => p.S).ToList();

            // One series, sampled once. Both roads then carry records at the same physical points
            // — B's mirrored — so their fitted curves are reflections of each other and agree
            // everywhere rather than merely closely.
            var shared = new List<(double S, double Z)>(a.Count);
            foreach (var (s, z, _) in a)
                shared.Add((s, Math.Min(z, InterpolateHeight(mirrored, s))));

            a.Clear();
            foreach (var (s, z) in shared)
                a.Add((s, z, false));

            b.Clear();
            for (int i = shared.Count - 1; i >= 0; --i)
                b.Add((length - shared[i].S, shared[i].Z, false));

            merged.Add((left, right));
        }
        return merged;
    }

    /// <summary>
    /// Road pairs of equal length whose reference lines are coincident when one is traversed
    /// backwards, and which point in opposite directions where they meet.
    /// </summary>
    private static List<(RoadId Left, RoadId Right)> FindOpposingCarriageways(
        Road.Map map, Dictionary<RoadId, List<(double S, double Z, bool Raised)>> perRoad)
    {
        var byLength = new Dictionary<long, List<RoadId>>();
        foreach (var (id, profile) in perRoad)
        {
            if (profile.Count < 2 || !map.Roads.TryGetValue(id, out var road) || road.Length <= 0.0)
                continue;
            long key = (long)Math.Round(road.Length * 100.0);
            if (!byLength.TryGetValue(key, out var list))
                byLength[key] = list = new List<RoadId>();
            list.Add(id);
        }

        var pairs = new List<(RoadId, RoadId)>();
        var taken = new HashSet<RoadId>();
        foreach (var group in byLength.Values)
        {
            group.Sort();
            for (int i = 0; i < group.Count; ++i)
            {
                if (taken.Contains(group[i])) continue;
                for (int j = i + 1; j < group.Count; ++j)
                {
                    if (taken.Contains(group[j])) continue;
                    if (!IsOpposingCarriageway(map, group[i], group[j])) continue;
                    pairs.Add((group[i], group[j]));
                    taken.Add(group[i]);
                    taken.Add(group[j]);
                    break;
                }
            }
        }
        return pairs;
    }

    private static bool IsOpposingCarriageway(Road.Map map, RoadId leftId, RoadId rightId)
    {
        var left = map.Roads[leftId];
        var right = map.Roads[rightId];
        double length = left.Length;
        const int probes = 9;
        for (int k = 0; k <= probes; ++k)
        {
            double s = length * k / probes;
            var here = Road.Map.GetDirectedPointInNoLaneOffset(left, s);
            var there = Road.Map.GetDirectedPointInNoLaneOffset(right, length - s);
            double dx = here.Location.X - there.Location.X;
            double dy = here.Location.Y - there.Location.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > CarriagewayCoincidenceMeters)
                return false;
            // Traversed in opposite directions, so the headings must be roughly antiparallel.
            double delta = Math.Abs(NormalizeAngle(here.Tangent - there.Tangent + Math.PI));
            if (delta > 0.35)
                return false;
        }
        return true;
    }

    private static double NormalizeAngle(double radians)
    {
        while (radians > Math.PI) radians -= 2.0 * Math.PI;
        while (radians < -Math.PI) radians += 2.0 * Math.PI;
        return radians;
    }

    /// <summary>Height of a station-ordered series at <paramref name="s"/>, held flat past its ends.</summary>
    private static double InterpolateHeight(List<(double S, double Z)> series, double s)
    {
        if (series.Count == 0) return double.NaN;
        if (s <= series[0].S) return series[0].Z;
        if (s >= series[^1].S) return series[^1].Z;
        int low = 0, high = series.Count - 1;
        while (high - low > 1)
        {
            int mid = (low + high) / 2;
            if (series[mid].S <= s) low = mid; else high = mid;
        }
        double span = series[high].S - series[low].S;
        if (span <= 1e-9) return series[low].Z;
        double t = (s - series[low].S) / span;
        return series[low].Z + (series[high].Z - series[low].Z) * t;
    }

    // ── junction height and slope continuity ─────────────────────────────────

    /// <summary>One end of one road — the unit a junction node is assembled from.</summary>
    private readonly record struct RoadEnd(RoadId RoadId, bool AtStart);

    /// <summary>
    /// Makes roads that meet agree on the height and the slope where they meet.
    ///
    /// A road-to-road <c>&lt;link&gt;</c> joins two road ends at one physical point. In generated
    /// maps every such link is between a junction connector and a road it joins — roads that meet
    /// at a junction link to the junction, not to each other — so this resolves intersections and
    /// nothing else. Heights are written back into <paramref name="perRoad"/>; the returned
    /// tangents are imposed on the fit.
    ///
    /// Both are weighted by road length, so a 400 m road sets the grade through the node and the
    /// 10 m connector adapts to it rather than the other way round. A node whose ends disagree
    /// about being deliberately raised is left alone: a bridge deck meeting the ground beneath it
    /// is a real step, not something to average away.
    /// </summary>
    private static Dictionary<RoadEnd, double> ResolveLinkedRoadEnds(
        XElement root, Dictionary<RoadId, List<(double S, double Z, bool Raised)>> perRoad,
        List<(RoadId Left, RoadId Right)> carriageways)
    {
        var nodes = GroupLinkedRoadEnds(root, perRoad, carriageways);
        var tangents = new Dictionary<RoadEnd, double>();

        foreach (var node in nodes)
        {
            if (node.Count < 2)
                continue;
            var members = node
                .Where(end => perRoad.TryGetValue(end.End.RoadId, out var p) && p.Count >= 2)
                .ToList();
            if (members.Count < 2)
                continue;

            // A deck meeting the ground is a genuine step; only merge ends that agree.
            bool firstRaised = SampleAt(perRoad, members[0].End).Raised;
            if (members.Any(m => SampleAt(perRoad, m.End).Raised != firstRaised))
                continue;

            double weightSum = 0.0, heightSum = 0.0, tangentSum = 0.0;
            foreach (var (end, parity) in members)
            {
                var profile = perRoad[end.RoadId];
                double weight = Math.Max(profile[^1].S - profile[0].S, 1e-3);
                weightSum += weight;
                heightSum += weight * SampleAt(perRoad, end).Z;
                tangentSum += weight * parity * RoadEndGrade(profile, end.AtStart);
            }
            double height = heightSum / weightSum;
            double tangent = tangentSum / weightSum;

            foreach (var (end, parity) in members)
            {
                var profile = perRoad[end.RoadId];
                int index = end.AtStart ? 0 : profile.Count - 1;
                profile[index] = (profile[index].S, height, profile[index].Raised);
                tangents[end] = parity * tangent;
            }
        }
        return tangents;
    }

    /// <summary>
    /// Collects road ends joined by road-to-road links into one group per physical node, each end
    /// carrying the sign that puts its grade in a common direction of travel.
    /// </summary>
    /// <remarks>
    /// Travel runs continuously through a node when one road arrives at it and the other leaves —
    /// an end meeting a start. Two ends meeting, or two starts, means the second road is traversed
    /// backwards, so its grade enters the average negated.
    /// </remarks>
    private static List<List<(RoadEnd End, double Parity)>> GroupLinkedRoadEnds(
        XElement root, Dictionary<RoadId, List<(double S, double Z, bool Raised)>> perRoad,
        List<(RoadId Left, RoadId Right)> carriageways)
    {
        var parent = new Dictionary<RoadEnd, RoadEnd>();
        var parity = new Dictionary<RoadEnd, double>();

        RoadEnd Find(RoadEnd end)
        {
            if (!parent.TryGetValue(end, out var up) || up.Equals(end))
                return end;
            var root_ = Find(up);
            parity[end] = parity[end] * parity[up];
            parent[end] = root_;
            return root_;
        }

        void Add(RoadEnd end)
        {
            if (parent.ContainsKey(end)) return;
            parent[end] = end;
            parity[end] = 1.0;
        }

        foreach (var roadNode in root.Elements("road"))
        {
            if (!uint.TryParse(roadNode.Attribute("id")?.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id) || !perRoad.ContainsKey(id))
                continue;
            var link = roadNode.Element("link");
            if (link is null) continue;

            foreach (var (role, mineAtStart) in new[] { ("predecessor", true), ("successor", false) })
            {
                var element = link.Element(role);
                if (element is null || element.Attribute("elementType")?.Value != "road")
                    continue;
                if (!uint.TryParse(element.Attribute("elementId")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var otherId) || !perRoad.ContainsKey(otherId))
                    continue;

                bool theirsAtStart = element.Attribute("contactPoint")?.Value != "end";
                var mine = new RoadEnd(id, mineAtStart);
                var theirs = new RoadEnd(otherId, theirsAtStart);
                Add(mine);
                Add(theirs);

                // Same kind of end on both sides means one road is traversed backwards.
                double relative = mineAtStart != theirsAtStart ? 1.0 : -1.0;
                var mineRoot = Find(mine);
                var theirsRoot = Find(theirs);
                if (mineRoot.Equals(theirsRoot))
                    continue;
                parent[theirsRoot] = mineRoot;
                parity[theirsRoot] = parity[mine] * relative * parity[theirs];
            }
        }

        // The two directions of one street are separate roads that never link to each other, yet
        // their mirrored ends sit on the same corner of a junction. Without this they land in
        // different node groups and get resolved to different heights, which reopens along the
        // street's centre line the very disagreement the shared height series just closed.
        foreach (var (left, right) in carriageways)
        {
            foreach (var (mineAtStart, theirsAtStart) in new[] { (true, false), (false, true) })
            {
                var mine = new RoadEnd(left, mineAtStart);
                var theirs = new RoadEnd(right, theirsAtStart);
                Add(mine);
                Add(theirs);
                var mineRoot = Find(mine);
                var theirsRoot = Find(theirs);
                if (mineRoot.Equals(theirsRoot))
                    continue;
                parent[theirsRoot] = mineRoot;
                parity[theirsRoot] = parity[mine] * 1.0 * parity[theirs];
            }
        }

        var grouped = new Dictionary<RoadEnd, List<(RoadEnd, double)>>();
        foreach (var end in parent.Keys.ToList())
        {
            var representative = Find(end);
            if (!grouped.TryGetValue(representative, out var list))
                grouped[representative] = list = new List<(RoadEnd, double)>();
            list.Add((end, parity[end]));
        }
        return grouped.Values.ToList();
    }

    private static (double S, double Z, bool Raised) SampleAt(
        Dictionary<RoadId, List<(double S, double Z, bool Raised)>> perRoad, RoadEnd end)
    {
        var profile = perRoad[end.RoadId];
        return end.AtStart ? profile[0] : profile[^1];
    }

    /// <summary>Grade at one end of a road, measured over a step rather than over the end span.</summary>
    private static double RoadEndGrade(List<(double S, double Z, bool Raised)> profile, bool atStart)
    {
        var s = new List<double>(profile.Count);
        var z = new List<double>(profile.Count);
        foreach (var point in profile) { s.Add(point.S); z.Add(point.Z); }
        return EndGrade(s, z, atStart);
    }

    // <road> child order per OpenDRIVE: link, type, planView, elevationProfile, …
    // Insert right after planView (or after type/link) so the document stays schema-valid.
    private static void InsertElevationProfile(XElement roadNode, XElement elevationProfile)
    {
        var planView = roadNode.Element("planView");
        if (planView != null)
        {
            planView.AddAfterSelf(elevationProfile);
            return;
        }
        var anchor = roadNode.Element("type") ?? roadNode.Element("link");
        if (anchor != null)
            anchor.AddAfterSelf(elevationProfile);
        else
            roadNode.AddFirst(elevationProfile);
    }

    // Replace NaN z-values (failed height samples) by linear interpolation between the
    // nearest valid neighbours; hold the nearest valid value at the ends; all-NaN -> 0.
    private static void FillGaps(List<(double S, double Z, bool Raised)> list)
    {
        int n = list.Count;
        bool anyValid = list.Any(p => !double.IsNaN(p.Z));
        if (!anyValid)
        {
            for (int i = 0; i < n; ++i) list[i] = (list[i].S, 0.0, list[i].Raised);
            return;
        }

        for (int i = 0; i < n; ++i)
        {
            if (!double.IsNaN(list[i].Z)) continue;

            int prev = i - 1;
            while (prev >= 0 && double.IsNaN(list[prev].Z)) prev--;
            int next = i + 1;
            while (next < n && double.IsNaN(list[next].Z)) next++;

            double z;
            if (prev >= 0 && next < n)
            {
                double t = (list[i].S - list[prev].S) / (list[next].S - list[prev].S);
                z = list[prev].Z + t * (list[next].Z - list[prev].Z);
            }
            else if (prev >= 0)
            {
                z = list[prev].Z;
            }
            else
            {
                z = list[next].Z;
            }
            list[i] = (list[i].S, z, list[i].Raised);
        }
    }

    // Reject points that sit more than thresholdMeters above OR below BOTH of their nearest
    // valid neighbours — i.e. isolated peaks/pits, where the road centerline passed under an
    // over-street structure the photogrammetry surface mesh captured (CTA "L" tracks, tree
    // canopies, awnings, building overhangs) so the sampled height is that structure, not the
    // street. This is slope-robust: on a real grade a point lies BETWEEN its neighbours, so it
    // is never beyond both. Rejected points become NaN; FillGaps then interpolates the street
    // level back in. Points flagged as deliberately raised — a deck whose height was routed to the
    // photoreal surface on purpose — are never rejected, since flattening one back into the road
    // beneath it is the defect the layer routing exists to remove. Disabled when
    // thresholdMeters <= 0.
    private static void RejectOutliers(List<(double S, double Z, bool Raised)> list, double thresholdMeters)
    {
        int n = list.Count;
        if (thresholdMeters <= 0.0 || n < 3) return;

        var z = new double[n];
        for (int i = 0; i < n; ++i) z[i] = list[i].Z;

        var reject = new bool[n];
        for (int i = 0; i < n; ++i)
        {
            if (double.IsNaN(z[i]) || list[i].Raised) continue;
            int p = i - 1; while (p >= 0 && (double.IsNaN(z[p]) || reject[p])) --p;
            int q = i + 1; while (q < n && double.IsNaN(z[q])) ++q;
            if (p < 0 || q >= n) continue; // road ends — nothing to bracket against

            double hi = Math.Max(z[p], z[q]);
            double lo = Math.Min(z[p], z[q]);
            if (z[i] - hi > thresholdMeters || lo - z[i] > thresholdMeters)
                reject[i] = true;
        }

        for (int i = 0; i < n; ++i)
            if (reject[i]) list[i] = (list[i].S, double.NaN, list[i].Raised);
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
