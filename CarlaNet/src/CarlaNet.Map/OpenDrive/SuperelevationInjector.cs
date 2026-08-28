// Cross-section counterpart to ElevationInjector.
//
// ElevationInjector gives every road an <elevationProfile> sampled along its reference
// line, and leaves <lateralProfile> empty. A consumer therefore renders each road dead flat
// across its width, so two roads that overlap in plan disagree in height wherever their
// reference lines are laterally apart — which at a junction is every connecting road, offset
// by whole lane widths from the road it serves.
//
// That height difference is not an error in the elevation data: it is the terrain, sampled
// correctly at two different places. What is missing is the road's crossfall. This class
// measures it, using the same engine round-trip ElevationInjector already uses:
//
//   ExtractCrossSectionSamples(map, step, probesPerSide)  -> reference line PLUS lateral probes
//   ToGeo(samples, origin)                                -> (roadId, s, t, lat, lon)
//   -- engine boundary: ACesium3DTileset.SampleHeightMostDetailed gives ellipsoidal heights --
//   FitCrossSections(samples, heights)                    -> per-station roll angle + residual
//   InjectSuperelevation(xodr, fits)                       -> .xodr carrying <superelevation>
//
// Deriving the crossfall from the .xodr instead of measuring it does not work: netconvert
// roads are one-way carriageways whose lanes all sit on one side, so the only observations
// available are on that side, at offsets quantised to a lane width, roughly two per road.
// Probing the surface directly removes all four limits at once.
//
// Sign convention: t is the OpenDRIVE lateral coordinate, positive to the LEFT of the
// driving direction, and a road's surface height at offset t is z_ref + t*tan(theta). A
// positive superelevation therefore rolls the left side up and drains to the right.
// DirectedPoint.ApplyLateralOffset moves to the RIGHT for a positive argument, so it is
// always called with -t.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.Road;
using CarlaNet.Map.Road.Element;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.OpenDrive;

/// <summary>A probe on a road's cross-section: a station plus a lateral offset.
/// X/Y are in the CARLA world frame (+X=East, -Y=North), as ElevationInjector emits.</summary>
public readonly record struct CrossSectionSample(RoadId RoadId, double S, double T, double X, double Y);

/// <summary>The crossfall measured at one station.</summary>
/// <param name="SuperelevationRadians">Roll angle about the s-axis, left side up positive.</param>
/// <param name="ResidualMeters">RMS height error of the straight-line fit. A crowned or
/// broken cross-section shows up here as a large residual and is rejected.</param>
public readonly record struct CrossSectionFit(
    RoadId RoadId, double S, double SuperelevationRadians, double ResidualMeters, int ProbeCount);

public static class SuperelevationInjector
{
    // ── P2: probe points across each road, not just along it ────────────────

    /// <summary>
    /// Walks each road's reference line every <paramref name="stepMeters"/> and, at each
    /// station, emits the reference line plus <paramref name="probesPerSide"/> probes on
    /// whichever sides carry driving lanes. The outermost probe sits
    /// <paramref name="edgeMarginMeters"/> inside the pavement edge so it cannot land on the
    /// verge, and a side narrower than <paramref name="edgeMarginMeters"/> * 2 is skipped
    /// rather than probed at a meaningless offset.
    /// </summary>
    public static IReadOnlyList<CrossSectionSample> ExtractCrossSectionSamples(
        Road.Map map,
        double stepMeters,
        int probesPerSide = 2,
        double edgeMarginMeters = 0.5,
        double maxProbeSpacingMeters = 4.0)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (stepMeters <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(stepMeters), "step must be positive");
        if (probesPerSide < 1)
            throw new ArgumentOutOfRangeException(nameof(probesPerSide), "need at least one probe per side");

        var samples = new List<CrossSectionSample>();

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
                var (leftExtent, rightExtent) = DrivingExtent(road, s);
                foreach (var t in ProbeOffsets(leftExtent, rightExtent, probesPerSide, edgeMarginMeters, maxProbeSpacingMeters))
                {
                    var dp = Road.Map.GetDirectedPointInNoLaneOffset(road, s);
                    dp.ApplyLateralOffset((float)(-t)); // t is left-positive; the helper moves right
                    // planView is +Y=North; CARLA world is -Y=North -> flip Y.
                    samples.Add(new CrossSectionSample(roadId, s, t, dp.Location.X, -dp.Location.Y));
                }
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

    /// <summary>The lane section covering s: the last one that starts at or before it.</summary>
    private static LaneSection? SectionAt(Road.Road road, double s)
    {
        LaneSection? found = null;
        foreach (var section in road.LaneSections)
        {
            if (section.S <= s + 1e-9)
                found = section;
            else
                break;
        }
        return found ?? road.LaneSections.FirstOrDefault();
    }

    /// <summary>
    /// How far the driving surface reaches either side of the reference line at s. Only
    /// driving lanes count: probing a sidewalk or verge would measure the kerb, not the road.
    /// </summary>
    public static (double Left, double Right) DrivingExtent(Road.Road road, double s)
    {
        var section = SectionAt(road, s);
        if (section == null)
            return (0.0, 0.0);

        double left = 0.0, right = 0.0;
        // Widths accumulate outward from the centre, so a non-driving lane cuts the run off:
        // anything beyond it is separated from the carriageway by that lane.
        foreach (var lane in section.Lanes.Where(kv => kv.Key < 0).OrderByDescending(kv => kv.Key).Select(kv => kv.Value))
        {
            if (lane.Type != LaneType.Driving) break;
            right += LaneWidthAt(lane, s);
        }
        foreach (var lane in section.Lanes.Where(kv => kv.Key > 0).OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (lane.Type != LaneType.Driving) break;
            left += LaneWidthAt(lane, s);
        }
        return (left, right);
    }

    private static double LaneWidthAt(Lane lane, double s)
    {
        var width = lane.GetInfoAt<RoadInfoLaneWidth>(s);
        if (width != null)
            return Math.Max(0.0, width.Polynomial.Evaluate(s));
        var border = lane.GetInfoAt<RoadInfoLaneBorder>(s);
        return border == null ? 0.0 : Math.Max(0.0, border.Polynomial.Evaluate(s));
    }

    /// <summary>Reference line, then probes spread out to just inside each pavement edge.</summary>
    public static IEnumerable<double> ProbeOffsets(
        double leftExtent, double rightExtent, int probesPerSide, double edgeMargin,
        double maxProbeSpacingMeters = 4.0)
    {
        yield return 0.0;
        foreach (var (extent, sign) in new[] { (leftExtent, 1.0), (rightExtent, -1.0) })
        {
            if (extent < edgeMargin * 2.0)
                continue; // too narrow to say anything about a slope
            double outer = extent - edgeMargin;
            // A fixed probe count leaves a wide carriageway resting on as few points as a narrow
            // connector, so one probe landing on a kerb or a drape artefact tilts the whole fit.
            // Holding the spacing instead keeps the fit as well supported on a six-lane road as on
            // a single lane.
            int perSide = Math.Max(probesPerSide, (int)Math.Ceiling(outer / maxProbeSpacingMeters));
            for (int k = 1; k <= perSide; ++k)
                yield return sign * outer * k / perSide;
        }
    }

    // ── P3: reproject to WGS84 with the same transform ElevationInjector uses ─

    /// <inheritdoc cref="ElevationInjector.ToGeo"/>
    public static IReadOnlyList<GeoSample> ToGeo(IReadOnlyList<CrossSectionSample> samples, GeoLocation origin)
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

    // ── P4: fit a roll angle per station ────────────────────────────────────

    /// <summary>Why stations were kept or dropped, for diagnosing a fit that came out thin.</summary>
    public readonly record struct CrossSectionFitSummary(
        int StationsSeen, int Fitted, int TooFewProbes, int SpanTooShort, int NotPlanar, int Clamped);

    /// <inheritdoc cref="FitCrossSections(IReadOnlyList{CrossSectionSample}, IReadOnlyList{double}, out CrossSectionFitSummary, double, double, double, double)"/>
    public static IReadOnlyList<CrossSectionFit> FitCrossSections(
        IReadOnlyList<CrossSectionSample> samples,
        IReadOnlyList<double> ellipsoidalHeights,
        double maxSlope = 0.10,
        double residualFloorMeters = 0.05,
        double residualFractionOfSpan = 0.01,
        double minSpanMeters = 2.0)
        => FitCrossSections(samples, ellipsoidalHeights, out _,
            maxSlope, residualFloorMeters, residualFractionOfSpan, minSpanMeters);

    /// <summary>
    /// Least-squares fits height against lateral offset at each station.
    /// <paramref name="samples"/> and <paramref name="ellipsoidalHeights"/> are index-aligned,
    /// in the order <see cref="ExtractCrossSectionSamples"/> produced. Stations are dropped
    /// when they have too few usable probes, when the probes are too close together to
    /// resolve a slope, or when the residual says the cross-section is not a straight line —
    /// a crown, a kerb or a bridge edge is better left flat than modelled as a roll.
    ///
    /// The planarity tolerance scales with the probe span rather than being a fixed distance.
    /// An absolute threshold is not comparable across roads: the same crossfall produces a few
    /// centimetres of relief across a 3 m connector and tens of centimetres across a 20 m
    /// carriageway, so a fixed tolerance is permissive on narrow roads and punishing on wide
    /// ones — which are the roads where crossfall matters most. The threshold is
    /// <paramref name="residualFractionOfSpan"/> of the span, with
    /// <paramref name="residualFloorMeters"/> as a floor so short spans keep a usable margin.
    /// The floor is set where a fixed threshold used to sit, so narrow roads are judged exactly
    /// as before and only wide ones are given the room their span warrants.
    /// </summary>
    public static IReadOnlyList<CrossSectionFit> FitCrossSections(
        IReadOnlyList<CrossSectionSample> samples,
        IReadOnlyList<double> ellipsoidalHeights,
        out CrossSectionFitSummary summary,
        double maxSlope = 0.10,
        double residualFloorMeters = 0.05,
        double residualFractionOfSpan = 0.01,
        double minSpanMeters = 2.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(ellipsoidalHeights);
        if (samples.Count != ellipsoidalHeights.Count)
            throw new ArgumentException(
                $"samples ({samples.Count}) and heights ({ellipsoidalHeights.Count}) must be index-aligned",
                nameof(ellipsoidalHeights));

        var fits = new List<CrossSectionFit>();
        int seen = 0, tooFew = 0, shortSpan = 0, notPlanar = 0, clamped = 0;
        int i = 0;
        while (i < samples.Count)
        {
            ++seen;
            var roadId = samples[i].RoadId;
            double s = samples[i].S;
            int start = i;
            while (i < samples.Count && samples[i].RoadId == roadId && samples[i].S == s)
                ++i;

            var ts = new List<double>();
            var zs = new List<double>();
            for (int k = start; k < i; ++k)
            {
                double z = ellipsoidalHeights[k];
                if (double.IsNaN(z) || double.IsInfinity(z))
                    continue; // the engine failed this probe
                ts.Add(samples[k].T);
                zs.Add(z);
            }

            if (ts.Count < 3)
            {
                ++tooFew;
                continue;
            }
            double span = ts.Max() - ts.Min();
            if (span < minSpanMeters)
            {
                ++shortSpan;
                continue;
            }

            var (slope, residual) = LineFit(ts, zs);
            if (residual > Math.Max(residualFloorMeters, residualFractionOfSpan * span))
            {
                ++notPlanar;
                continue;
            }

            if (Math.Abs(slope) > maxSlope)
                ++clamped;
            slope = Math.Clamp(slope, -maxSlope, maxSlope);
            fits.Add(new CrossSectionFit(roadId, s, Math.Atan(slope), residual, ts.Count));
        }
        summary = new CrossSectionFitSummary(seen, fits.Count, tooFew, shortSpan, notPlanar, clamped);
        return fits;
    }

    /// <summary>Ordinary least squares of z against t. Returns the slope and the RMS residual.</summary>
    private static (double Slope, double Residual) LineFit(IReadOnlyList<double> ts, IReadOnlyList<double> zs)
    {
        int n = ts.Count;
        double meanT = ts.Average();
        double meanZ = zs.Average();
        double sxx = 0.0, sxy = 0.0;
        for (int k = 0; k < n; ++k)
        {
            double dt = ts[k] - meanT;
            sxx += dt * dt;
            sxy += dt * (zs[k] - meanZ);
        }
        if (sxx <= 1e-9)
            return (0.0, double.PositiveInfinity);

        double slope = sxy / sxx;
        double intercept = meanZ - slope * meanT;
        double sum = 0.0;
        for (int k = 0; k < n; ++k)
        {
            double err = zs[k] - (intercept + slope * ts[k]);
            sum += err * err;
        }
        return (slope, Math.Sqrt(sum / n));
    }

    // ── P5: write <superelevation> into the .xodr ───────────────────────────

    /// <summary>
    /// Adds a &lt;superelevation&gt; record per fitted station to each road's
    /// &lt;lateralProfile&gt;, interpolating linearly between stations so the roll is
    /// continuous. A road is skipped unless at least <paramref name="minCoverage"/> of its
    /// stations produced a usable fit — a road measured in only a couple of places would
    /// otherwise get a crossfall extrapolated far beyond the evidence. Roads with no fits are
    /// left exactly as they were.
    /// </summary>
    public static string InjectSuperelevation(
        string openDriveXml,
        IReadOnlyList<CrossSectionFit> fits,
        IReadOnlyList<CrossSectionSample>? samples = null,
        double minCoverage = 0.5)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        ArgumentNullException.ThrowIfNull(fits);

        var doc = XDocument.Parse(openDriveXml, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new ArgumentException("not an OpenDRIVE document", nameof(openDriveXml));

        // How many stations were attempted per road, so coverage is measured against the
        // sampling that was requested rather than against the fits that survived.
        var attempted = new Dictionary<RoadId, int>();
        if (samples != null)
        {
            foreach (var group in samples.GroupBy(x => x.RoadId))
                attempted[group.Key] = group.Select(x => x.S).Distinct().Count();
        }

        foreach (var group in fits.GroupBy(f => f.RoadId))
        {
            var ordered = group.OrderBy(f => f.S).ToList();
            if (attempted.TryGetValue(group.Key, out int total) && total > 0
                && (double)ordered.Count / total < minCoverage)
                continue;

            var road = root.Elements("road")
                .FirstOrDefault(r => (string?)r.Attribute("id")
                                     == group.Key.ToString(CultureInfo.InvariantCulture));
            if (road == null)
                continue;

            var profile = new XElement("lateralProfile");
            for (int k = 0; k < ordered.Count; ++k)
            {
                double b = 0.0;
                if (k + 1 < ordered.Count)
                {
                    double ds = ordered[k + 1].S - ordered[k].S;
                    if (ds > 1e-6)
                        b = (ordered[k + 1].SuperelevationRadians - ordered[k].SuperelevationRadians) / ds;
                }
                profile.Add(new XElement("superelevation",
                    new XAttribute("s", Str(ordered[k].S)),
                    new XAttribute("a", Str(ordered[k].SuperelevationRadians)),
                    new XAttribute("b", Str(b)),
                    new XAttribute("c", "0"),
                    new XAttribute("d", "0")));
            }

            var existing = road.Element("lateralProfile");
            if (existing != null)
                existing.ReplaceWith(profile);
            else
                road.Add(profile);
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string Str(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
