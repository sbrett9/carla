// Offline tests for CarlaNet.Map.OpenDrive.BridgeProfileShaper.
//
// The fixture is the shape the pass exists for: a road that approaches a deck over ground the
// survey can see, crosses it, and comes down the other side, with a sampled surface that sags
// across the span and a lift that reaches further than the structure does.
using System;
using System.Collections.Generic;
using System.Linq;
using CarlaNet.Map.OpenDrive;

namespace CarlaNet.Tests.Map;

public class BridgeProfileShaperTests
{
    private const int Ground = 0;   // a way at grade
    private const int Deck = 1;     // a way on a layer above grade

    private static OsmRoadWay Way(string id, int layer) => new()
    {
        Id = id, Layer = layer, IsBridge = layer > 0, IsTunnel = false,
        NodeIds = [], X = [], Y = [], NodeStation = [],
        MinX = 0.0, MinY = 0.0, MaxX = 0.0, MaxY = 0.0,
    };

    private static readonly IReadOnlyList<OsmRoadWay> Ways = [Way("at-grade", 0), Way("deck", 1)];

    /// 200 m road sampled every 10 m; the deck occupies 80-120 m.
    private static (CenterlineSample[] Samples, double[] Heights, double[] Lift, int[] Match) Fixture()
    {
        var samples = new List<CenterlineSample>();
        var heights = new List<double>();
        var lift = new List<double>();
        var match = new List<int>();
        for (int i = 0; i <= 20; ++i)
        {
            double s = i * 10.0;
            samples.Add(new CenterlineSample(7u, s, s, 0.0));
            bool onDeck = s >= 80.0 && s <= 120.0;
            match.Add(onDeck ? Deck : Ground);
            // The lift reaches from 40 m to 160 m: well past the structure at both ends.
            double l = (s >= 40.0 && s <= 160.0) ? 3.0 : 0.0;
            lift.Add(l);
            // A surface that sags across the span, plus a wobble the fit should discard.
            double sag = onDeck ? -4.0 : 0.0;
            heights.Add(100.0 + l + sag + (i % 2 == 0 ? 0.3 : -0.3));
        }
        return (samples.ToArray(), heights.ToArray(), lift.ToArray(), match.ToArray());
    }

    [Fact]
    public void TheDeckBecomesOneStraightRun()
    {
        var (samples, heights, lift, match) = Fixture();
        Assert.Equal(1, BridgeProfileShaper.Shape(samples, heights, lift, match, Ways));

        var deck = Enumerable.Range(0, samples.Length)
            .Where(i => samples[i].S >= 80.0 && samples[i].S <= 120.0).ToList();
        double first = heights[deck[0]], last = heights[deck[^1]];
        foreach (int i in deck)
        {
            double expected = first + (last - first) * (samples[i].S - 80.0) / 40.0;
            Assert.Equal(expected, heights[i], 9);
        }
    }

    [Fact]
    public void EachApproachIsOneStraightRunFromWhereTheLiftBegins()
    {
        var (samples, heights, lift, match) = Fixture();
        BridgeProfileShaper.Shape(samples, heights, lift, match, Ways);

        // 40-80 m is the ramp up; every interval along it must share one gradient.
        var up = Enumerable.Range(0, samples.Length)
            .Where(i => samples[i].S >= 40.0 && samples[i].S <= 80.0).ToList();
        var gradients = up.Zip(up.Skip(1), (a, b) =>
            (heights[b] - heights[a]) / (samples[b].S - samples[a].S)).ToList();
        Assert.All(gradients, g => Assert.Equal(gradients[0], g, 9));
        Assert.NotEqual(0.0, gradients[0], 6);
    }

    [Fact]
    public void RoadOutsideTheStructureIsUntouched()
    {
        var (samples, heights, lift, match) = Fixture();
        var before = (double[])heights.Clone();
        BridgeProfileShaper.Shape(samples, heights, lift, match, Ways);

        foreach (int i in Enumerable.Range(0, samples.Length))
            if (samples[i].S < 40.0 || samples[i].S > 160.0)
                Assert.Equal(before[i], heights[i], 12);
    }

    [Fact]
    public void ARoadWithNoDeckIsLeftAlone()
    {
        var (samples, heights, lift, match) = Fixture();
        for (int i = 0; i < match.Length; ++i)
            match[i] = Ground;
        var before = (double[])heights.Clone();

        Assert.Equal(0, BridgeProfileShaper.Shape(samples, heights, lift, match, Ways));
        Assert.Equal(before, heights);
    }

    [Fact]
    public void ADeckTooShortToDescribeIsLeftAlone()
    {
        var (samples, heights, lift, match) = Fixture();
        for (int i = 0; i < match.Length; ++i)
            match[i] = samples[i].S == 100.0 ? Deck : Ground;   // a single 0 m "span"
        var before = (double[])heights.Clone();

        Assert.Equal(0, BridgeProfileShaper.Shape(samples, heights, lift, match, Ways));
        Assert.Equal(before, heights);
    }

    [Fact]
    public void MisalignedArraysAreRejected()
    {
        var (samples, heights, lift, match) = Fixture();
        Assert.Throws<ArgumentException>(() =>
            BridgeProfileShaper.Shape(samples, heights.Take(5).ToArray(), lift, match, Ways));
    }
}
