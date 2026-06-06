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
    PiecewiseLinear
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
    /// </summary>
    public static string InjectElevation(
        string openDriveXml,
        IReadOnlyList<CenterlineSample> samples,
        IReadOnlyList<double> ellipsoidalHeights,
        double originHeight,
        ElevationFitMode mode = ElevationFitMode.PiecewiseLinear)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(ellipsoidalHeights);
        if (samples.Count != ellipsoidalHeights.Count)
            throw new ArgumentException(
                $"samples ({samples.Count}) and heights ({ellipsoidalHeights.Count}) must be the same length");

        // Group (s, z) by road, preserving ascending-s order, z relative to origin.
        var perRoad = new Dictionary<RoadId, List<(double S, double Z)>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            double rawZ = ellipsoidalHeights[i] - originHeight;
            var list = perRoad.TryGetValue(samples[i].RoadId, out var existing)
                ? existing
                : (perRoad[samples[i].RoadId] = new List<(double, double)>());
            list.Add((samples[i].S, rawZ));
        }

        foreach (var list in perRoad.Values)
        {
            list.Sort((a, b) => a.S.CompareTo(b.S));
            FillGaps(list); // resolve NaNs left by failed height samples
        }

        var doc = XDocument.Parse(openDriveXml);
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document (no root)");

        foreach (var roadNode in root.Elements("road"))
        {
            if (!uint.TryParse(roadNode.Attribute("id")?.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id))
                continue;
            if (!perRoad.TryGetValue(id, out var profile) || profile.Count == 0)
                continue;

            roadNode.Elements("elevationProfile").Remove();
            var elevationProfile = BuildElevationProfile(profile, mode);
            InsertElevationProfile(roadNode, elevationProfile);
        }

        return doc.ToString(SaveOptions.None);
    }

    /// <summary>Convenience: extract → (caller samples heights) is external, so this is for tests/symmetry.</summary>
    private static XElement BuildElevationProfile(List<(double S, double Z)> profile, ElevationFitMode mode)
    {
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
    private static void FillGaps(List<(double S, double Z)> list)
    {
        int n = list.Count;
        bool anyValid = list.Any(p => !double.IsNaN(p.Z));
        if (!anyValid)
        {
            for (int i = 0; i < n; ++i) list[i] = (list[i].S, 0.0);
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
            list[i] = (list[i].S, z);
        }
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
