// Phase B tests — CarlaNet.Map.OpenDrive.ElevationInjector.
//
// Offline (no engine): a synthetic flat .xodr (one straight 100 m road along +X,
// origin pinned at Wrigley home plate) exercises extract -> ToGeo -> inject. The
// strongest test is end-to-end: inject heights, reparse with OpenDriveParser, and
// confirm CARLA's own GetDirectedPointInNoLaneOffset reads back the injected z.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;
using CarlaNet.Types.Geom;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.Map;

public class ElevationInjectorTests
{
    // One straight road, id=1, length 100 m, heading 0 (runs due East). Origin pinned
    // to Wrigley home plate so ToGeo lands on the real testbed georeference.
    private const string FlatXodr =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0"" vendor=""test"">
    <geoReference><![CDATA[+proj=tmerc +lat_0=41.94813 +lon_0=-87.65593 +k=1 +x_0=0 +y_0=0 +datum=WGS84 +units=m +no_defs]]></geoReference>
  </header>
  <road name=""r1"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0"">
        <line/>
      </geometry>
    </planView>
    <lanes>
      <laneSection s=""0.0"">
        <center>
          <lane id=""0"" type=""driving"" level=""false""/>
        </center>
        <right>
          <lane id=""-1"" type=""driving"" level=""false"">
            <width sOffset=""0.0"" a=""3.5"" b=""0.0"" c=""0.0"" d=""0.0""/>
          </lane>
        </right>
      </laneSection>
    </lanes>
  </road>
</OpenDRIVE>";

    private static RoadMap LoadFlat()
        => OpenDriveParser.Load(FlatXodr) ?? throw new Exception("flat .xodr failed to parse");

    [Fact]
    public void Extract_StraightRoad_HasExpectedStationsAndCoords()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 25.0);

        // s = 0, 25, 50, 75, 100
        Assert.Equal(5, samples.Count);
        Assert.All(samples, s => Assert.Equal(1u, s.RoadId));

        double[] expectedS = { 0, 25, 50, 75, 100 };
        for (int i = 0; i < expectedS.Length; ++i)
        {
            Assert.Equal(expectedS[i], samples[i].S, 6);
            Assert.Equal(expectedS[i], samples[i].X, 2); // heading 0 -> x == s
            Assert.Equal(0.0, samples[i].Y, 2);          // on the reference line
        }
    }

    [Fact]
    public void Extract_AlwaysIncludesBothEndpoints()
    {
        var map = LoadFlat();
        // step that does not divide the length evenly
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 30.0);
        Assert.Equal(0.0, samples.First().S, 6);
        Assert.Equal(100.0, samples.Last().S, 6);
    }

    [Fact]
    public void ToGeo_RoundTripsThroughGeodesy()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 25.0);
        var geo = ElevationInjector.ToGeo(samples, map.GeoReference);

        Assert.Equal(samples.Count, geo.Count);
        for (int i = 0; i < samples.Count; ++i)
        {
            var back = Geodesy.GeodeticToCarlaLocal(
                map.GeoReference, new GeoLocation(geo[i].Latitude, geo[i].Longitude, 0.0));
            Assert.Equal(samples[i].X, back.X, 3);
            Assert.Equal(samples[i].Y, back.Y, 3);
        }

        // Sanity: the origin sample maps to the origin lat/lon, and moving East
        // increases longitude while latitude barely changes.
        Assert.Equal(map.GeoReference.Latitude, geo[0].Latitude, 6);
        Assert.Equal(map.GeoReference.Longitude, geo[0].Longitude, 6);
        Assert.True(geo[4].Longitude > geo[0].Longitude);
    }

    [Fact]
    public void Inject_RoundTrips_ConsumedByCarlaElevation()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 25.0);

        // Underlying truth: ellipsoidal height = originHeight + 0.1*s  =>  relative z = 0.1*s.
        const double originHeight = 146.508;
        var heights = samples.Select(s => originHeight + 0.1 * s.S).ToList();

        var elevated = ElevationInjector.InjectElevation(
            FlatXodr, samples, heights, originHeight, ElevationFitMode.PiecewiseLinear);

        var map2 = OpenDriveParser.Load(elevated) ?? throw new Exception("elevated .xodr failed to parse");
        var road = map2.Roads[1u];

        // At every sample station, CARLA's own elevation evaluation returns the injected z.
        foreach (var s in samples.Select(x => x.S))
        {
            double z = RoadMap.GetDirectedPointInNoLaneOffset(road, s).Location.Z;
            Assert.Equal(0.1 * s, z, 3);
        }

        // And the piecewise-linear fit interpolates exactly at a mid-segment station.
        double zMid = RoadMap.GetDirectedPointInNoLaneOffset(road, 12.5).Location.Z;
        Assert.Equal(1.25, zMid, 3);
    }

    [Fact]
    public void Inject_FillsFailedSampleGaps()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 25.0); // s = 0,25,50,75,100
        const double originHeight = 146.508;

        var heights = samples.Select(s => originHeight + 0.1 * s.S).ToList();
        int mid = samples.ToList().FindIndex(s => s.S == 50.0);
        heights[mid] = double.NaN; // simulate a failed height sample at s=50

        var elevated = ElevationInjector.InjectElevation(
            FlatXodr, samples, heights, originHeight, ElevationFitMode.PiecewiseLinear);

        var road = (OpenDriveParser.Load(elevated) ?? throw new Exception("parse")).Roads[1u];
        // Interpolated between s=25 (z=2.5) and s=75 (z=7.5) -> 5.0 at s=50.
        Assert.Equal(5.0, RoadMap.GetDirectedPointInNoLaneOffset(road, 50.0).Location.Z, 3);
    }

    [Fact]
    public void Inject_RejectsOverStreetStructureSpikes()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 25.0); // s = 0,25,50,75,100
        const double originHeight = 146.508;

        // Flat street at +1 m, with an L-track / canopy spike of +9 m at s=50.
        var heights = samples.Select(_ => originHeight + 1.0).ToList();
        int mid = samples.ToList().FindIndex(s => s.S == 50.0);
        heights[mid] = originHeight + 9.0;

        var elevated = ElevationInjector.InjectElevation(FlatXodr, samples, heights, originHeight);
        var road = (OpenDriveParser.Load(elevated) ?? throw new Exception("parse")).Roads[1u];
        // The spike is snapped back to street level (~1 m), not left at 9 m.
        Assert.Equal(1.0, RoadMap.GetDirectedPointInNoLaneOffset(road, 50.0).Location.Z, 1);
    }

    [Fact]
    public void Inject_KeepsGenuineSlope()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 25.0);
        const double originHeight = 146.508;
        // A real 3% grade must NOT be flagged as outliers (slope-robust rejection).
        var heights = samples.Select(s => originHeight + 0.03 * s.S).ToList();

        var elevated = ElevationInjector.InjectElevation(FlatXodr, samples, heights, originHeight);
        var road = (OpenDriveParser.Load(elevated) ?? throw new Exception("parse")).Roads[1u];
        Assert.Equal(1.5, RoadMap.GetDirectedPointInNoLaneOffset(road, 50.0).Location.Z, 1);
    }

    [Fact]
    public void Inject_ReplacesExistingProfile_NoDuplicates()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 50.0);
        const double originHeight = 146.508;
        var heights = samples.Select(_ => originHeight + 1.0).ToList();

        var once = ElevationInjector.InjectElevation(FlatXodr, samples, heights, originHeight);
        var twice = ElevationInjector.InjectElevation(once, samples, heights, originHeight);

        var road = XDocument.Parse(twice).Root!.Elements("road").Single();
        Assert.Single(road.Elements("elevationProfile"));
        // elevationProfile must sit immediately after planView (schema order).
        var children = road.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(children.IndexOf("planView") + 1, children.IndexOf("elevationProfile"));
    }

    [Fact]
    public void Inject_MismatchedLengths_Throws()
    {
        var map = LoadFlat();
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 25.0);
        var tooFew = new List<double> { 1.0, 2.0 };
        Assert.Throws<ArgumentException>(
            () => ElevationInjector.InjectElevation(FlatXodr, samples, tooFew, 0.0));
    }

    [Fact]
    public void Extract_RejectsNonPositiveStep()
    {
        var map = LoadFlat();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ElevationInjector.ExtractCenterlineSamples(map, 0.0));
    }
}
