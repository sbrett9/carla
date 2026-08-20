// A road may sit below the photoreal surface only where something is built over it.
//
// The draped surface a road follows is de-spiked: where the photoreal stands well above bare
// earth the drape treats it as a structure — a building, a canopy, an awning — and falls back
// to the ground, so the road under it does too. On open terrain that decision is wrong, and it
// drops the road into the hillside it should be lying on. Measured on Arapahoe_I25, 146 of
// 5,235 elevation stations sit on bare earth rather than the photoreal for that reason, by
// between one and sixteen metres; on the Hormuz trunk highway a single station drops five
// metres and puts a visible notch in the carriageway.
//
// What separates those from a road that is legitimately below the surface is whether anything
// is over it. That test is geometric here, not a reading of the OSM `bridge` / `layer` /
// `tunnel` tags, because those tags are carried by the way doing the bridging and never by the
// road passing underneath: on Arapahoe_I25 all 24 stations under a real deck are untagged, so
// a tag-driven rule would lift every underpass on the map onto the deck above it.
//
// A sustained departure is left alone as well. A road in a cutting is genuinely below a
// photoreal mesh that spans the cut, and it shows as a run of stations rather than one or two;
// on Arapahoe_I25 six stations look like that, against 160 isolated ones.

namespace CarlaNet.Map.OpenDrive;

/// <summary>Tuning for <see cref="PhotorealClamp"/>.</summary>
public sealed class PhotorealClampOptions
{
    /// <summary>How far below the photoreal a station may sit before it is considered dropped.
    /// Below this the difference is sampling noise between the surface and the fitted road.</summary>
    public double ToleranceMeters { get; init; } = 1.0;

    /// <summary>How much road has to be overhead for a station to count as being under a deck.
    /// Measured clearances at real underpasses on Arapahoe_I25 run from 7.1 m to 7.9 m, and the
    /// shallowest grade separation the mesh work measured is 6.8 m, so this is far below any of
    /// them while still clearing the noise between two roads at the same grade.</summary>
    public double OverheadClearanceMeters { get; init; } = 2.0;

    /// <summary>How far away in plan that road may be. A deck wider than this is still caught,
    /// since the test runs at every station along the road beneath it.
    ///
    /// Erring wide. Lifting a road that is under a deck destroys a grade separation, while
    /// failing to lift a dropped station only leaves it as it is today, so the two mistakes are
    /// not equally bad. Measured on Arapahoe_I25, going from 10 m to 25 m spares two further
    /// stations and no more, and the five largest lifts have nothing above them even at 40 m --
    /// so this is clear of the real underpasses while still well inside the distance at which
    /// another road could plausibly be bridging this one.</summary>
    public double OverheadRadiusMeters { get; init; } = 15.0;

    /// <summary>How many stations in a row must be below the surface for the departure to be
    /// read as terrain rather than a dropped sample.</summary>
    public int SustainedRunStations { get; init; } = 4;
}

/// <summary>What the clamp did, for the build log.</summary>
public readonly record struct PhotorealClampResult(
    int Lifted, int UnderDeck, int Sustained, int Raised, double LargestLiftMeters);

/// <summary>Lifts road stations that were dropped off the photoreal back onto it.</summary>
public static class PhotorealClamp
{
    private const double PlanCellMeters = 8.0;

    /// <summary>
    /// Raises each station of <paramref name="roadHeights"/> that sits below
    /// <paramref name="photorealHeights"/> without cause, in place.
    /// </summary>
    /// <param name="samples">Reference-line samples, grouped by road and ascending in s.</param>
    /// <param name="roadHeights">Ellipsoidal road heights, modified in place.</param>
    /// <param name="photorealHeights">The photoreal surface at each sample. NaN where the
    /// tileset had no height, which leaves that station alone.</param>
    /// <param name="raised">Stations deliberately lifted onto a deck by the grade separation.
    /// Those are above the surface by design and are never touched.</param>
    public static PhotorealClampResult Apply(
        IReadOnlyList<CenterlineSample> samples,
        double[] roadHeights,
        IReadOnlyList<double> photorealHeights,
        IReadOnlyList<bool> raised,
        PhotorealClampOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(roadHeights);
        ArgumentNullException.ThrowIfNull(photorealHeights);
        ArgumentNullException.ThrowIfNull(raised);
        if (roadHeights.Length != samples.Count || photorealHeights.Count != samples.Count
            || raised.Count != samples.Count)
        {
            throw new ArgumentException(
                $"samples ({samples.Count}), heights ({roadHeights.Length}), photoreal "
                + $"({photorealHeights.Count}) and raised ({raised.Count}) must agree");
        }
        options ??= new PhotorealClampOptions();

        // Which stations are below the surface at all. Computed up front because the run length
        // through a station is what tells a dropped sample from a road in a cutting.
        var below = new bool[samples.Count];
        for (int i = 0; i < samples.Count; ++i)
        {
            double surface = photorealHeights[i];
            below[i] = !double.IsNaN(surface) && !raised[i]
                && surface - roadHeights[i] > options.ToleranceMeters;
        }

        // Every road surface, indexed by plan cell, so "is anything over this?" is a local
        // lookup. The heights are the ones being clamped, which is what a vehicle would meet.
        var index = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < samples.Count; ++i)
        {
            var key = ((int)Math.Floor(samples[i].X / PlanCellMeters),
                       (int)Math.Floor(samples[i].Y / PlanCellMeters));
            if (!index.TryGetValue(key, out var bucket))
            {
                index[key] = bucket = [];
            }
            bucket.Add(i);
        }

        int reach = (int)Math.Ceiling(options.OverheadRadiusMeters / PlanCellMeters);
        int lifted = 0, underDeck = 0, sustained = 0, raisedCount = 0;
        double largest = 0.0;

        for (int i = 0; i < samples.Count; ++i)
        {
            if (raised[i])
            {
                ++raisedCount;
                continue;
            }
            if (!below[i])
            {
                continue;
            }
            if (HasRoadOverhead(samples, roadHeights, index, i, reach, options))
            {
                ++underDeck;
                continue;
            }
            if (RunLengthThrough(samples, below, i) >= options.SustainedRunStations)
            {
                ++sustained;
                continue;
            }
            largest = Math.Max(largest, photorealHeights[i] - roadHeights[i]);
            roadHeights[i] = photorealHeights[i];
            ++lifted;
        }

        return new PhotorealClampResult(lifted, underDeck, sustained, raisedCount, largest);
    }

    private static bool HasRoadOverhead(
        IReadOnlyList<CenterlineSample> samples,
        double[] heights,
        Dictionary<(int, int), List<int>> index,
        int at,
        int reach,
        PhotorealClampOptions options)
    {
        CenterlineSample here = samples[at];
        int gx = (int)Math.Floor(here.X / PlanCellMeters);
        int gy = (int)Math.Floor(here.Y / PlanCellMeters);
        double radiusSquared = options.OverheadRadiusMeters * options.OverheadRadiusMeters;

        for (int dx = -reach; dx <= reach; ++dx)
        {
            for (int dy = -reach; dy <= reach; ++dy)
            {
                if (!index.TryGetValue((gx + dx, gy + dy), out var bucket))
                {
                    continue;
                }
                foreach (int other in bucket)
                {
                    if (samples[other].RoadId == here.RoadId)
                    {
                        continue;
                    }
                    double ox = samples[other].X - here.X;
                    double oy = samples[other].Y - here.Y;
                    if (ox * ox + oy * oy > radiusSquared)
                    {
                        continue;
                    }
                    if (heights[other] - heights[at] >= options.OverheadClearanceMeters)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>How many stations in an unbroken run of below-surface ones this station is part
    /// of, counted along its own road.</summary>
    private static int RunLengthThrough(
        IReadOnlyList<CenterlineSample> samples, bool[] below, int at)
    {
        RoadId road = samples[at].RoadId;
        int first = at;
        while (first > 0 && samples[first - 1].RoadId == road && below[first - 1])
        {
            --first;
        }
        int last = at;
        while (last + 1 < samples.Count && samples[last + 1].RoadId == road && below[last + 1])
        {
            ++last;
        }
        return last - first + 1;
    }
}
