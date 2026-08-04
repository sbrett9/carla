// Offline tests for the grade-separation elevation routing — CarlaNet.Map.OpenDrive.OsmRoadLayers
// and GradeSeparation.
//
// The defect they pin down: a vertical sample of a photogrammetric surface returns the SAME height
// for a bridge deck and for the road passing beneath it, so a single-surface pipeline either lifts
// the lower road onto the deck or drops the deck onto the ground. The fixture below is the smallest
// map that reproduces it — one road crossing another with no shared node, and a surface that reads
// high over BOTH of them — so a test that passes cannot be passing by accident.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CarlaNet.Map.OpenDrive;
using CarlaNet.Types.Geom;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.Map;

public class GradeSeparationTests
{
    // Two straight roads crossing at the origin: road 1 runs East over 100 m, road 2 runs North over
    // 100 m. planView is +Y=North, so road 2's heading is pi/2 and its CARLA Y is negated.
    private const string CrossingXodr =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0"" vendor=""test"">
    <geoReference><![CDATA[+proj=tmerc +lat_0=41.94813 +lon_0=-87.65593 +k=1 +x_0=0 +y_0=0 +datum=WGS84 +units=m +no_defs]]></geoReference>
  </header>
  <road name=""deck"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""-50.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
    <lanes><laneSection s=""0.0"">
      <center><lane id=""0"" type=""driving"" level=""false""/></center>
      <right><lane id=""-1"" type=""driving"" level=""false""><width sOffset=""0.0"" a=""3.5"" b=""0.0"" c=""0.0"" d=""0.0""/></lane></right>
    </laneSection></lanes>
  </road>
  <road name=""under"" length=""100.0"" id=""2"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""-50.0"" hdg=""1.5707963267948966"" length=""100.0""><line/></geometry>
    </planView>
    <lanes><laneSection s=""0.0"">
      <center><lane id=""0"" type=""driving"" level=""false""/></center>
      <right><lane id=""-1"" type=""driving"" level=""false""><width sOffset=""0.0"" a=""3.5"" b=""0.0"" c=""0.0"" d=""0.0""/></lane></right>
    </laneSection></lanes>
  </road>
</OpenDRIVE>";

    private const double GroundHeight = 100.0;

    // The photoreal sits slightly BELOW bare earth on open ground — measured at -0.82 m over 4218
    // road samples at Arapahoe Ave / I-25. Using the real sign here keeps the tests honest about
    // clearance being measured against that baseline rather than against zero.
    private const double SystematicOffset = -0.8;

    private static RoadMap LoadCrossing()
        => OpenDriveParser.Load(CrossingXodr) ?? throw new Exception(".xodr failed to parse");

    // ── OSM fixture ──────────────────────────────────────────────────────────

    private sealed record WaySpec(string Id, (double X, double Y)[] Points, params (string K, string V)[] Tags);

    // Writes a temporary .osm whose way vertices land on the given CARLA world coordinates.
    private static string WriteOsm(GeoLocation origin, params WaySpec[] ways)
    {
        var xml = new StringBuilder();
        xml.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        xml.AppendLine(@"<osm version=""0.6"">");
        xml.AppendLine(@"  <bounds minlat=""41.9"" minlon=""-87.7"" maxlat=""42.0"" maxlon=""-87.6""/>");

        int nodeId = 1000;
        var nodeIdsPerWay = new List<List<int>>();
        foreach (var way in ways)
        {
            var ids = new List<int>();
            foreach (var (x, y) in way.Points)
            {
                var g = Geodesy.CarlaLocalToGeodetic(origin, x, y, 0.0);
                xml.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    @"  <node id=""{0}"" lat=""{1:R}"" lon=""{2:R}"" version=""1""/>",
                    nodeId, g.Latitude, g.Longitude));
                ids.Add(nodeId++);
            }
            nodeIdsPerWay.Add(ids);
        }

        for (int i = 0; i < ways.Length; ++i)
        {
            xml.AppendLine($@"  <way id=""{ways[i].Id}"" version=""1"">");
            foreach (int id in nodeIdsPerWay[i])
                xml.AppendLine($@"    <nd ref=""{id}""/>");
            foreach (var (k, v) in ways[i].Tags)
                xml.AppendLine($@"    <tag k=""{k}"" v=""{v}""/>");
            xml.AppendLine(@"  </way>");
        }
        xml.AppendLine("</osm>");

        var path = Path.Combine(Path.GetTempPath(), $"carlanet_layers_{Guid.NewGuid():N}.osm");
        File.WriteAllText(path, xml.ToString());
        return path;
    }

    private static T WithOsm<T>(GeoLocation origin, WaySpec[] ways, Func<string, T> body)
    {
        string path = WriteOsm(origin, ways);
        try { return body(path); }
        finally { try { File.Delete(path); } catch { /* temp cleanup is best effort */ } }
    }

    // Way A runs East through the origin (the deck); way B runs North through it (the road under).
    // They share no node, which is exactly how OSM records a grade separation.
    private static WaySpec[] CrossingWays(params (string K, string V)[] deckTags) =>
    [
        new WaySpec("100", [(-60.0, 0.0), (60.0, 0.0)], deckTags),
        new WaySpec("200", [(0.0, -60.0), (0.0, 60.0)], ("highway", "primary")),
    ];

    // ── layer classification ─────────────────────────────────────────────────

    [Fact]
    public void Layer_ComesFromTags_WithBridgeAndTunnelSupplyingDirection()
    {
        var origin = new GeoLocation(41.94813, -87.65593, 0.0);
        var ways = new[]
        {
            new WaySpec("1", [(0.0, 0.0), (50.0, 0.0)], ("highway", "primary"), ("bridge", "yes")),
            new WaySpec("2", [(0.0, 20.0), (50.0, 20.0)], ("highway", "primary"), ("layer", "2")),
            new WaySpec("3", [(0.0, 40.0), (50.0, 40.0)], ("highway", "primary"), ("tunnel", "yes")),
            new WaySpec("4", [(0.0, 60.0), (50.0, 60.0)], ("highway", "primary"), ("bridge", "no")),
            new WaySpec("5", [(0.0, 80.0), (50.0, 80.0)], ("highway", "primary")),
            // An explicit layer outranks the bridge tag: that is the only tag that can order more
            // than two levels, which is what a stacked interchange needs.
            new WaySpec("6", [(0.0, 100.0), (50.0, 100.0)], ("highway", "primary"), ("bridge", "yes"), ("layer", "3")),
        };

        var parsed = WithOsm(origin, ways, p => OsmRoadLayers.ParseRoadWays(p, origin));
        var byId = parsed.ToDictionary(w => w.Id, w => w);

        Assert.Equal(1, byId["1"].Layer);
        Assert.True(byId["1"].IsBridge);
        Assert.Equal(2, byId["2"].Layer);
        Assert.Equal(-1, byId["3"].Layer);
        Assert.True(byId["3"].IsTunnel);
        Assert.Equal(0, byId["4"].Layer);
        Assert.False(byId["4"].IsBridge);
        Assert.Equal(0, byId["5"].Layer);
        Assert.Equal(3, byId["6"].Layer);
    }

    [Fact]
    public void Layer_BuildingPassageIsNotATunnel()
    {
        // A way threading under a building is still on the ground, so sinking it by a tunnel depth
        // would be wrong.
        var origin = new GeoLocation(41.94813, -87.65593, 0.0);
        var ways = new[]
        {
            new WaySpec("1", [(0.0, 0.0), (50.0, 0.0)],
                ("highway", "service"), ("tunnel", "building_passage")),
        };

        var parsed = WithOsm(origin, ways, p => OsmRoadLayers.ParseRoadWays(p, origin));
        Assert.Equal(0, parsed[0].Layer);
        Assert.False(parsed[0].IsTunnel);
    }

    [Fact]
    public void ParseRoadWays_KeepsOnlyDrivableWays_AndProjectsThem()
    {
        var origin = new GeoLocation(41.94813, -87.65593, 0.0);
        var ways = new[]
        {
            new WaySpec("1", [(0.0, 0.0), (100.0, 0.0)], ("highway", "residential")),
            new WaySpec("2", [(0.0, 10.0), (100.0, 10.0)], ("highway", "footway")),
            new WaySpec("3", [(0.0, 20.0), (100.0, 20.0)], ("railway", "rail")),
        };

        var parsed = WithOsm(origin, ways, p => OsmRoadLayers.ParseRoadWays(p, origin));

        Assert.Single(parsed);
        Assert.Equal("1", parsed[0].Id);
        // Round-tripping through WGS84 and back must land on the coordinates the way was written at.
        Assert.Equal(0.0, parsed[0].X[0], 2);
        Assert.Equal(100.0, parsed[0].X[1], 2);
        Assert.Equal(0.0, parsed[0].Y[0], 2);
        Assert.Equal(100.0, parsed[0].Length, 2);
    }

    // ── crossing detection ───────────────────────────────────────────────────

    [Fact]
    public void Crossing_IsFound_WhenWaysIntersectWithNoSharedNode()
    {
        var origin = new GeoLocation(41.94813, -87.65593, 0.0);
        var layers = WithOsm(origin, CrossingWays(("highway", "motorway"), ("bridge", "yes")),
            p => OsmRoadLayers.Read(p, origin));

        Assert.Single(layers.Crossings);
        var crossing = layers.Crossings[0];
        Assert.Equal(0.0, crossing.X, 2);
        Assert.Equal(0.0, crossing.Y, 2);

        // The lower-layer way is the one that must stay on bare earth.
        var under = layers.WaysPassingUnder();
        Assert.Single(under);
        Assert.Equal("200", layers.Ways[under.Single()].Id);
    }

    [Fact]
    public void Crossing_IsNotReported_WhenTheWaysShareANode()
    {
        // A shared node is a junction: the two roads meet at grade, so this must not be read as a
        // grade separation however the ways are tagged.
        var origin = new GeoLocation(41.94813, -87.65593, 0.0);
        string path = Path.Combine(Path.GetTempPath(), $"carlanet_layers_{Guid.NewGuid():N}.osm");
        var mid = Geodesy.CarlaLocalToGeodetic(origin, 0.0, 0.0, 0.0);
        var west = Geodesy.CarlaLocalToGeodetic(origin, -60.0, 0.0, 0.0);
        var east = Geodesy.CarlaLocalToGeodetic(origin, 60.0, 0.0, 0.0);
        var south = Geodesy.CarlaLocalToGeodetic(origin, 0.0, 60.0, 0.0);
        var north = Geodesy.CarlaLocalToGeodetic(origin, 0.0, -60.0, 0.0);

        string N(int id, GeoLocation g) => string.Format(CultureInfo.InvariantCulture,
            @"<node id=""{0}"" lat=""{1:R}"" lon=""{2:R}"" version=""1""/>", id, g.Latitude, g.Longitude);
        File.WriteAllText(path,
            $@"<?xml version=""1.0""?><osm version=""0.6"">
              <bounds minlat=""41.9"" minlon=""-87.7"" maxlat=""42.0"" maxlon=""-87.6""/>
              {N(1, west)}{N(2, mid)}{N(3, east)}{N(4, south)}{N(5, north)}
              <way id=""100"" version=""1""><nd ref=""1""/><nd ref=""2""/><nd ref=""3""/>
                <tag k=""highway"" v=""primary""/></way>
              <way id=""200"" version=""1""><nd ref=""4""/><nd ref=""2""/><nd ref=""5""/>
                <tag k=""highway"" v=""primary""/></way>
            </osm>");
        try
        {
            var layers = OsmRoadLayers.Read(path, origin);
            Assert.Equal(2, layers.Ways.Count);
            Assert.Empty(layers.Crossings);
        }
        finally { try { File.Delete(path); } catch { /* temp cleanup is best effort */ } }
    }

    [Fact]
    public void Crossing_IsNotReported_WhenBoundsOverlapButTheWaysDoNotMeet()
    {
        var origin = new GeoLocation(41.94813, -87.65593, 0.0);
        var ways = new[]
        {
            new WaySpec("100", [(-60.0, 0.0), (-10.0, 0.0)], ("highway", "primary")),
            new WaySpec("200", [(-30.0, -60.0), (-30.0, -5.0)], ("highway", "primary")),
        };

        var layers = WithOsm(origin, ways, p => OsmRoadLayers.Read(p, origin));
        Assert.Empty(layers.Crossings);
    }

    // ── elevation routing ────────────────────────────────────────────────────

    // Bare earth is flat; the photoreal reads 5 m higher over a 60 m square centred on the crossing,
    // which is what a deck does to a top-down sample — it raises the reading for the road passing
    // underneath just as much as for the deck itself.
    private static void BuildSurfaces(
        IReadOnlyList<CenterlineSample> samples, out double[] surface, out double[] ground)
    {
        surface = new double[samples.Count];
        ground = new double[samples.Count];
        for (int i = 0; i < samples.Count; ++i)
        {
            ground[i] = GroundHeight;
            bool overStructure = Math.Abs(samples[i].X) <= 30.0 && Math.Abs(samples[i].Y) <= 30.0;
            surface[i] = overStructure ? GroundHeight + 5.0 : GroundHeight + SystematicOffset;
        }
    }

    [Fact]
    public void Deck_TakesItsClearanceFromTheSurface_AndTheRoadUnderneathStaysAtGrade()
    {
        var map = LoadCrossing();
        var origin = map.GeoReference;
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 10.0);
        BuildSurfaces(samples, out var surface, out var ground);

        var result = WithOsm(origin, CrossingWays(("highway", "motorway"), ("bridge", "yes")),
            p => GradeSeparation.Compute(map, samples, OsmRoadLayers.Read(p, origin),
                surface, ground, SystematicOffset));

        Assert.Single(result.ElevatedWays);
        Assert.Equal(1, result.StructuresFromSurface);
        Assert.Equal(0, result.StructuresFromFallback);

        // Clearance = surface - bare earth - the systematic offset = 5.0 + 0.8.
        const double expectedLift = 5.0 - SystematicOffset;
        for (int i = 0; i < samples.Count; ++i)
        {
            bool nearCrossing = Math.Abs(samples[i].X) <= 30.0 && Math.Abs(samples[i].Y) <= 30.0;
            if (samples[i].RoadId == 1u && nearCrossing)
                Assert.Equal(expectedLift, result.Lift[i], 3);   // the deck rides the structure
            else if (samples[i].RoadId == 2u)
                Assert.Equal(0.0, result.Lift[i], 6);            // the road under it does not
        }
    }

    [Fact]
    public void NoLayeredWays_LeavesEveryRoadAtGrade()
    {
        // Without a layer tag the extract records no vertical structure, so the surface reading over
        // the crossing must be ignored rather than guessed at. This is what keeps every existing
        // generated map byte-for-byte unchanged.
        var map = LoadCrossing();
        var origin = map.GeoReference;
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 10.0);
        BuildSurfaces(samples, out var surface, out var ground);

        var result = WithOsm(origin, CrossingWays(("highway", "motorway")),
            p => GradeSeparation.Compute(map, samples, OsmRoadLayers.Read(p, origin),
                surface, ground, SystematicOffset));

        Assert.True(result.IsEmpty);
        Assert.All(result.Lift, v => Assert.Equal(0.0, v, 9));
    }

    [Fact]
    public void UnreconstructedStructure_FallsBackToTheFixedSeparation_OnlyWhereSomethingCrosses()
    {
        // The photoreal is flat everywhere, so there is nothing to measure. A deck over a crossing
        // still has to clear the road beneath it; a deck with nothing under it is left alone rather
        // than lifted on faith.
        var map = LoadCrossing();
        var origin = map.GeoReference;
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 10.0);
        var flatSurface = new double[samples.Count];
        var ground = new double[samples.Count];
        for (int i = 0; i < samples.Count; ++i)
        {
            ground[i] = GroundHeight;
            flatSurface[i] = GroundHeight + SystematicOffset;
        }

        var crossed = WithOsm(origin, CrossingWays(("highway", "motorway"), ("bridge", "yes")),
            p => GradeSeparation.Compute(map, samples, OsmRoadLayers.Read(p, origin),
                flatSurface, ground, SystematicOffset));

        Assert.Equal(1, crossed.StructuresFromFallback);
        Assert.Equal(0, crossed.StructuresFromSurface);
        Assert.Equal(5.0, crossed.MaxLiftMeters, 3);

        // Same deck, but the way it used to cross is moved clear of it.
        var isolated = new WaySpec[]
        {
            new("100", [(-60.0, 0.0), (60.0, 0.0)], ("highway", "motorway"), ("bridge", "yes")),
            new("200", [(-200.0, -60.0), (-200.0, 60.0)], ("highway", "primary")),
        };
        var alone = WithOsm(origin, isolated,
            p => GradeSeparation.Compute(map, samples, OsmRoadLayers.Read(p, origin),
                flatSurface, ground, SystematicOffset));

        Assert.Equal(0, alone.StructuresFromFallback);
        Assert.All(alone.Lift, v => Assert.Equal(0.0, v, 9));
    }

    [Fact]
    public void Tunnel_IsSunkBelowGrade()
    {
        var map = LoadCrossing();
        var origin = map.GeoReference;
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 10.0);
        BuildSurfaces(samples, out var surface, out var ground);

        // Road 2 runs through a bore under road 1.
        var ways = new WaySpec[]
        {
            new("100", [(-60.0, 0.0), (60.0, 0.0)], ("highway", "motorway")),
            new("200", [(0.0, -60.0), (0.0, 60.0)], ("highway", "primary"), ("tunnel", "yes")),
        };

        var result = WithOsm(origin, ways,
            p => GradeSeparation.Compute(map, samples, OsmRoadLayers.Read(p, origin),
                surface, ground, SystematicOffset));

        for (int i = 0; i < samples.Count; ++i)
            if (samples[i].RoadId == 2u)
                Assert.Equal(-5.0, result.Lift[i], 3);
    }

    // ── collision heightfield ────────────────────────────────────────────────

    [Fact]
    public void SystematicOffset_IsTheMedianGapIgnoringStructures()
    {
        // Two cells hold a building; the median must not move because of them.
        var dsm = new[] { 99.2, 99.3, 99.1, 99.2, 130.0, 128.0 };
        var dtm = new[] { 100.0, 100.0, 100.0, 100.0, 100.0, 100.0 };
        Assert.Equal(-0.8, DrapeTerrain.SystematicOffset(dsm, dtm, 5.0), 2);
    }

    [Fact]
    public void DrapedSurface_UnderADeck_IsTheGroundNotTheDeck()
    {
        // 41x41 cells at 2 m spanning [-40, 40] in both axes. Bare earth is flat; the photoreal
        // carries a deck 5 m up along the line y = 0 for |x| <= 20.
        var spec = new DrapeGridSpec(default, -40.0, -40.0, 2.0, 41, 41);
        int n = spec.NodeCount;
        var dtm = new double[n];
        var dsm = new double[n];
        for (int r = 0; r < spec.NumRows; ++r)
        {
            for (int c = 0; c < spec.NumCols; ++c)
            {
                double x = spec.MinX + c * spec.CellSize, y = spec.MinY + r * spec.CellSize;
                int i = r * spec.NumCols + c;
                dtm[i] = GroundHeight;
                bool onDeck = Math.Abs(y) <= 4.0 && Math.Abs(x) <= 20.0;
                dsm[i] = onDeck ? GroundHeight + 5.0 : GroundHeight + SystematicOffset;
            }
        }

        var deck = new OsmRoadWay
        {
            Id = "100", Layer = 1, IsBridge = true, IsTunnel = false,
            NodeIds = ["1", "2"],
            X = [-20.0, 20.0], Y = [0.0, 0.0], NodeStation = [0.0, 40.0],
            MinX = -20.0, MinY = 0.0, MaxX = 20.0, MaxY = 0.0,
        };

        int centre = (spec.NumRows / 2) * spec.NumCols + spec.NumCols / 2;

        // Without the structure the deck is inside the de-spike threshold, so the surface climbs
        // onto it and the road passing underneath has nowhere at grade to sit — the defect.
        var unmasked = DrapeTerrain.Despike(dsm, dtm, spec, maxDrapeMeters: 5.0, smoothPasses: 0);
        Assert.Equal(GroundHeight + 5.0, unmasked.DrapedZ[centre], 3);

        var masked = DrapeTerrain.Despike(dsm, dtm, spec, maxDrapeMeters: 5.0, smoothPasses: 0,
            elevatedStructures: [deck], atGradeOffsetMeters: SystematicOffset);
        Assert.Equal(GroundHeight + SystematicOffset, masked.DrapedZ[centre], 3);
        Assert.Equal(SystematicOffset, masked.SystematicOffsetMeters, 6);

        // Open ground well away from the deck keeps the photoreal detail it had.
        int corner = 2 * spec.NumCols + 2;
        Assert.Equal(unmasked.DrapedZ[corner], masked.DrapedZ[corner], 6);
    }

    // ── outlier rejection ────────────────────────────────────────────────────

    [Fact]
    public void OutlierRejection_SpareTheDeliberatelyRaisedSamples()
    {
        var map = LoadCrossing();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 10.0);
        var heights = new double[samples.Count];
        for (int i = 0; i < samples.Count; ++i) heights[i] = 100.0;

        // One short deck: a single sample of road 1 raised well beyond the rejection threshold.
        int spike = samples.Select((s, i) => (s, i)).First(p => p.s.RoadId == 1u && p.s.S == 50.0).i;
        heights[spike] = 106.0;

        string Inject(bool raised)
        {
            var flags = new bool[samples.Count];
            flags[spike] = raised;
            return ElevationInjector.InjectElevation(CrossingXodr, samples, heights, 100.0,
                ElevationFitMode.PiecewiseLinear, 4.0, flags);
        }

        Assert.Equal(0.0, ElevationAt(Inject(false), roadId: "1", s: 50.0), 3);
        Assert.Equal(6.0, ElevationAt(Inject(true), roadId: "1", s: 50.0), 3);
    }

    private static double ElevationAt(string xodr, string roadId, double s)
    {
        var road = System.Xml.Linq.XDocument.Parse(xodr).Root!
            .Elements("road").Single(r => r.Attribute("id")?.Value == roadId);
        var record = road.Element("elevationProfile")!.Elements("elevation")
            .Single(e => Math.Abs(double.Parse(e.Attribute("s")!.Value,
                CultureInfo.InvariantCulture) - s) < 1e-6);
        return double.Parse(record.Attribute("a")!.Value, CultureInfo.InvariantCulture);
    }
}
