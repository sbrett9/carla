// Gives a bridge the shape a bridge has: a ramp up, a deck, a ramp down.
//
// GradeSeparation raises a deck by lifting its terrain samples and then fading that lift back
// into the connected network at its approach-ramp grade. The fade is algebraic rather than
// structural, so it does not coincide with the structure. Measured on Arapahoe_I25, road 2044's
// deck runs 0-69 m while its lift runs 5-133 m, carrying an artificial ramp 64 m past the
// abutment onto ground that already contains the real embankment and counting the approach
// twice; on roads 2117 and 2196 the reverse happens and the lift covers only the middle of the
// span, leaving the ends of the deck unraised.
//
// Underneath that, a surveyed surface cannot describe a deck at all. Over road 2044 the draped
// surface plunges from -13.2 m to -20.5 m across the span, because what a vertical sample hits
// there is the ground beneath the bridge. Elsewhere it reconstructs deck, parapet and whatever
// traffic was crossing into a single blob: the seven deck spans in that map run at 0.5-2.8% end
// to end while their sampled profiles bulge by up to 3.25 m and reach 18.2% instantaneous grade.
//
// So neither the surface nor the fade is worth following across a structure, while both ends of
// it are sound: at the point where the lift reaches zero the road is on ground the survey can
// see. This replaces everything between those points with the three straight runs a bridge is
// actually built from, anchored at the ends and passing through the deck at its own height.
//
// Dropping the lift instead does not work, and is worth recording because it looks reasonable:
// with the lift removed, the carried deck meets a raw surface that has fallen away beneath it,
// and peak grades rise rather than fall - 19.9% to 31.7% on road 2044, 14.9% to 37.7% on 2047.
// The lift is the only thing that knows the deck is there.
using System;
using System.Collections.Generic;

namespace CarlaNet.Map.OpenDrive;

public static class BridgeProfileShaper
{
    /// <summary>
    /// Rewrites <paramref name="heights"/> in place for every road carrying a deck.
    /// <paramref name="samples"/>, <paramref name="heights"/>, <paramref name="lift"/> and
    /// <paramref name="matchedWayIndex"/> are index-aligned, as
    /// <see cref="GradeSeparation.Compute"/> produces them. Returns the number of roads shaped.
    /// </summary>
    /// <param name="minSpanMeters">A deck shorter than this is left alone: too little of the road
    /// is structure for the three-segment shape to describe it better than the terrain does.</param>
    /// <param name="minLiftMeters">How much lift counts as being part of the structure, which sets
    /// where the approach ramps are anchored.</param>
    public static int Shape(
        IReadOnlyList<CenterlineSample> samples,
        double[] heights,
        IReadOnlyList<double> lift,
        IReadOnlyList<int> matchedWayIndex,
        IReadOnlyList<OsmRoadWay> ways,
        double minSpanMeters = 20.0,
        double minLiftMeters = 0.10)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(heights);
        ArgumentNullException.ThrowIfNull(lift);
        ArgumentNullException.ThrowIfNull(matchedWayIndex);
        ArgumentNullException.ThrowIfNull(ways);
        if (heights.Length != samples.Count || lift.Count != samples.Count
            || matchedWayIndex.Count != samples.Count)
            throw new ArgumentException("samples, heights, lift and matches must be index-aligned");

        int shaped = 0;
        int start = 0;
        while (start < samples.Count)
        {
            int end = start;
            while (end + 1 < samples.Count && samples[end + 1].RoadId == samples[start].RoadId)
                ++end;
            if (ShapeRoad(samples, heights, lift, matchedWayIndex, ways, start, end,
                          minSpanMeters, minLiftMeters))
                ++shaped;
            start = end + 1;
        }
        return shaped;
    }

    private static bool ShapeRoad(
        IReadOnlyList<CenterlineSample> samples, double[] heights, IReadOnlyList<double> lift,
        IReadOnlyList<int> matchedWayIndex, IReadOnlyList<OsmRoadWay> ways,
        int first, int last, double minSpan, double minLift)
    {
        int deckFirst = -1, deckLast = -1;
        for (int i = first; i <= last; ++i)
        {
            int w = matchedWayIndex[i];
            if (w < 0 || w >= ways.Count || ways[w].Layer <= 0)
                continue;
            if (deckFirst < 0)
                deckFirst = i;
            deckLast = i;
        }
        if (deckFirst < 0 || samples[deckLast].S - samples[deckFirst].S < minSpan)
            return false;

        // The structure reaches as far as its lift does: that is where it rejoins ground the
        // survey can see, and so where an approach ramp must start and finish.
        int anchorFirst = deckFirst, anchorLast = deckLast;
        for (int i = first; i <= last; ++i)
        {
            if (Math.Abs(lift[i]) < minLift)
                continue;
            if (i < anchorFirst) anchorFirst = i;
            if (i > anchorLast) anchorLast = i;
        }

        double sA0 = samples[anchorFirst].S, sA1 = samples[anchorLast].S;
        double sD0 = samples[deckFirst].S, sD1 = samples[deckLast].S;
        double zA0 = heights[anchorFirst], zA1 = heights[anchorLast];
        double zD0 = heights[deckFirst], zD1 = heights[deckLast];

        for (int i = anchorFirst; i <= anchorLast; ++i)
        {
            double s = samples[i].S;
            if (s < sD0)
                heights[i] = Lerp(zA0, zD0, s, sA0, sD0);   // ramp up to the abutment
            else if (s <= sD1)
                heights[i] = Lerp(zD0, zD1, s, sD0, sD1);   // the deck itself
            else
                heights[i] = Lerp(zD1, zA1, s, sD1, sA1);   // ramp down off the far abutment
        }
        return true;
    }

    private static double Lerp(double z0, double z1, double s, double s0, double s1)
        => s1 - s0 <= 1e-9 ? z0 : z0 + (z1 - z0) * (s - s0) / (s1 - s0);
}
