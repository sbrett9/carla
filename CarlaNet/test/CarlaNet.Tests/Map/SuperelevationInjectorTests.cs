// Offline tests for CarlaNet.Map.OpenDrive.SuperelevationInjector — no engine required.
//
// The engine boundary is a pure function of the samples (heights in, heights out), so the
// probe placement, the fit and the injection can all be exercised with synthetic heights.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.Map;

public class SuperelevationInjectorTests
{
    // One straight 100 m road along +X with a single 3.5 m lane on the right.
    private const string RightOnlyXodr =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0"" vendor=""test"">
    <geoReference><![CDATA[+proj=tmerc +lat_0=41.94813 +lon_0=-87.65593 +k=1 +x_0=0 +y_0=0 +datum=WGS84 +units=m +no_defs]]></geoReference>
  </header>
  <road name=""r1"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
    <lateralProfile/>
    <lanes>
      <laneSection s=""0.0"">
        <center><lane id=""0"" type=""driving"" level=""false""/></center>
        <right>
          <lane id=""-1"" type=""driving"" level=""false"">
            <width sOffset=""0.0"" a=""3.5"" b=""0.0"" c=""0.0"" d=""0.0""/>
          </lane>
        </right>
      </laneSection>
    </lanes>
  </road>
</OpenDRIVE>";

    // Same road with a 3.5 m driving lane each side, then a sidewalk further out on the
    // right that must not be probed.
    private const string TwoSidedXodr =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0"" vendor=""test"">
    <geoReference><![CDATA[+proj=tmerc +lat_0=41.94813 +lon_0=-87.65593 +k=1 +x_0=0 +y_0=0 +datum=WGS84 +units=m +no_defs]]></geoReference>
  </header>
  <road name=""r1"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
    <lateralProfile/>
    <lanes>
      <laneSection s=""0.0"">
        <left>
          <lane id=""1"" type=""driving"" level=""false"">
            <width sOffset=""0.0"" a=""3.5"" b=""0.0"" c=""0.0"" d=""0.0""/>
          </lane>
        </left>
        <center><lane id=""0"" type=""driving"" level=""false""/></center>
        <right>
          <lane id=""-1"" type=""driving"" level=""false"">
            <width sOffset=""0.0"" a=""3.5"" b=""0.0"" c=""0.0"" d=""0.0""/>
          </lane>
          <lane id=""-2"" type=""sidewalk"" level=""false"">
            <width sOffset=""0.0"" a=""2.8"" b=""0.0"" c=""0.0"" d=""0.0""/>
          </lane>
        </right>
      </laneSection>
    </lanes>
  </road>
</OpenDRIVE>";

    private static RoadMap Load(string xodr)
        => OpenDriveParser.Load(xodr) ?? throw new Exception("test .xodr failed to parse");

    [Fact]
    public void Probes_LandPerpendicularToHeading_InCarlaWorldFrame()
    {
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(Load(RightOnlyXodr), 50.0);

        // 3 stations (0, 50, 100) x 3 probes (reference line + 2 on the right) = 9.
        Assert.Equal(9, samples.Count);

        var atZero = samples.Where(s => s.S == 0.0).OrderByDescending(s => s.T).ToList();
        Assert.Equal(new[] { 0.0, -1.5, -3.0 }, atZero.Select(s => s.T));

        // Heading is due East, so a lateral offset moves purely in Y. t is left-positive
        // (North), and the CARLA world frame has -Y = North, so right-side probes are +Y.
        foreach (var probe in atZero)
        {
            Assert.Equal(0.0, probe.X, 6);
            Assert.Equal(-probe.T, probe.Y, 6);
        }
    }

    [Fact]
    public void Probes_SkipNonDrivingLanes_AndStayInsideThePavement()
    {
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(Load(TwoSidedXodr), 100.0);
        var atZero = samples.Where(s => s.S == 0.0).Select(s => s.T).OrderBy(t => t).ToList();

        // One driving lane each side: extent 3.5, outermost probe 0.5 inside the edge.
        // The 2.8 m sidewalk beyond lane -1 must not extend the right-hand reach.
        Assert.Equal(new[] { -3.0, -1.5, 0.0, 1.5, 3.0 }, atZero);
    }

    [Fact]
    public void NarrowSide_IsNotProbedAtAMeaninglessOffset()
    {
        // A side thinner than twice the edge margin cannot support a probe inside its edge.
        var offsets = SuperelevationInjector.ProbeOffsets(0.4, 3.5, probesPerSide: 2, edgeMargin: 0.5).ToList();
        Assert.DoesNotContain(offsets, t => t > 0.0);
        Assert.Contains(0.0, offsets);
        Assert.Equal(2, offsets.Count(t => t < 0.0));
    }

    [Fact]
    public void WideCarriagewayGetsMoreProbes_SoOneBadSampleCannotTiltTheFit()
    {
        // A single lane: two probes a side is already denser than the spacing limit.
        Assert.Equal(3, SuperelevationInjector.ProbeOffsets(0.0, 3.5, 2, 0.5).Count());

        // A six-lane carriageway: 19.6 m of pavement held to 4 m spacing needs five a side.
        var wide = SuperelevationInjector.ProbeOffsets(0.0, 20.1, 2, 0.5).ToList();
        Assert.Equal(6, wide.Count);
        var gaps = wide.OrderBy(t => t).Zip(wide.OrderBy(t => t).Skip(1), (a, b) => b - a);
        Assert.All(gaps, g => Assert.True(g <= 4.0 + 1e-9, $"probe spacing {g:F2} m exceeds the limit"));
    }

    [Fact]
    public void Fit_RecoversAKnownCrossfall()
    {
        var map = Load(TwoSidedXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 50.0);

        // Surface rolls at exactly 2%: z = 100 + t * 0.02, left side up.
        var heights = samples.Select(s => 100.0 + s.T * 0.02).ToList();
        var fits = SuperelevationInjector.FitCrossSections(samples, heights);

        Assert.Equal(3, fits.Count); // one per station
        foreach (var fit in fits)
        {
            Assert.Equal(Math.Atan(0.02), fit.SuperelevationRadians, 9);
            Assert.Equal(0.0, fit.ResidualMeters, 9);
            Assert.Equal(5, fit.ProbeCount);
        }
    }

    [Fact]
    public void Fit_RejectsACrossSectionThatIsNotAStraightLine()
    {
        var map = Load(TwoSidedXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 100.0);

        // A kerb-like step rather than a roll: no straight line represents this.
        var heights = samples.Select(s => s.T < -2.0 ? 101.0 : 100.0).ToList();
        Assert.Empty(SuperelevationInjector.FitCrossSections(samples, heights));

        // Both stations are accepted once the surface really is planar.
        var planar = samples.Select(s => 100.0 + s.T * 0.02).ToList();
        var fits = SuperelevationInjector.FitCrossSections(samples, planar);
        Assert.Equal(new[] { 0.0, 100.0 }, fits.Select(f => f.S));
    }

    [Fact]
    public void Fit_DropsFailedProbes_AndTheStationWithThemIfTooFewRemain()
    {
        var map = Load(TwoSidedXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 100.0);

        // Engine returns NaN where it could not sample. The first station loses two of its
        // five probes and is still fitted from the remaining three.
        var heights = samples.Select((s, i) => i < 2 ? double.NaN : 100.0 + s.T * 0.02).ToList();
        var fits = SuperelevationInjector.FitCrossSections(samples, heights);
        Assert.Equal(new[] { 0.0, 100.0 }, fits.Select(f => f.S));
        Assert.Equal(3, fits.Single(f => f.S == 0.0).ProbeCount);
        Assert.Equal(Math.Atan(0.02), fits.Single(f => f.S == 0.0).SuperelevationRadians, 9);

        // Two good probes is not enough to trust a slope, so that station is dropped while
        // the intact one survives.
        var mostlyFailed = samples.Select((s, i) => i < 3 ? double.NaN : 100.0 + s.T * 0.02).ToList();
        var sparse = SuperelevationInjector.FitCrossSections(samples, mostlyFailed);
        Assert.Equal(100.0, Assert.Single(sparse).S);
    }

    [Fact]
    public void Fit_ClampsAnImplausibleSlope()
    {
        var map = Load(TwoSidedXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 100.0);
        var heights = samples.Select(s => 100.0 + s.T * 0.5).ToList(); // 50% crossfall
        var fits = SuperelevationInjector.FitCrossSections(samples, heights, out var summary, maxSlope: 0.10);
        Assert.Equal(Math.Atan(0.10), fits[0].SuperelevationRadians, 9);
        Assert.Equal(fits.Count, summary.Clamped);
    }

    /// <summary>Probes straight out from the reference line, as a one-way carriageway gets.</summary>
    private static List<CrossSectionSample> Probes(params double[] offsets)
        => offsets.Select(t => new CrossSectionSample(1u, 0.0, t, 0.0, 0.0)).ToList();

    [Fact]
    public void PlanarityTolerance_ScalesWithSpan_SoWideRoadsAreNotHeldToATighterStandard()
    {
        // The same shape at two widths: a 1% sag relative to the span. Physically the same
        // departure from planar, so both should be judged the same way.
        double Sag(double t, double span) => 100.0 + t * 0.02 + 0.01 * span * (1.0 - Math.Pow(2.0 * t / span + 1.0, 2));

        var narrow = Probes(0.0, -1.5, -3.0);
        var wide = Probes(0.0, -6.45, -12.9);
        var narrowZ = narrow.Select(p => Sag(p.T, 3.0)).ToList();
        var wideZ = wide.Select(p => Sag(p.T, 12.9)).ToList();

        // Span-scaled: both accepted.
        Assert.Single(SuperelevationInjector.FitCrossSections(narrow, narrowZ));
        Assert.Single(SuperelevationInjector.FitCrossSections(wide, wideZ));

        // A fixed absolute tolerance accepts the narrow one and rejects the wide one, which is
        // the bias this scaling exists to remove.
        Assert.Single(SuperelevationInjector.FitCrossSections(
            narrow, narrowZ, residualFloorMeters: 0.05, residualFractionOfSpan: 0.0));
        Assert.Empty(SuperelevationInjector.FitCrossSections(
            wide, wideZ, residualFloorMeters: 0.05, residualFractionOfSpan: 0.0));
    }

    [Fact]
    public void Summary_AccountsForEveryStation()
    {
        var map = Load(TwoSidedXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 50.0);
        var heights = samples.Select(s => s.T < -2.0 ? 101.0 : 100.0).ToList();
        SuperelevationInjector.FitCrossSections(samples, heights, out var summary);

        Assert.Equal(3, summary.StationsSeen);
        Assert.Equal(0, summary.Fitted);
        Assert.Equal(3, summary.NotPlanar);
        Assert.Equal(summary.StationsSeen,
            summary.Fitted + summary.TooFewProbes + summary.SpanTooShort + summary.NotPlanar);
    }

    [Fact]
    public void Inject_WritesContinuousSuperelevationRecords()
    {
        var map = Load(RightOnlyXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 50.0);
        // Crossfall increases along the road so the interpolation slope is non-zero.
        var heights = samples.Select(s => 100.0 + s.T * (0.01 + 0.0002 * s.S)).ToList();
        var fits = SuperelevationInjector.FitCrossSections(samples, heights);

        var result = SuperelevationInjector.InjectSuperelevation(RightOnlyXodr, fits, samples);
        var road = XDocument.Parse(result).Root!.Elements("road").Single();
        var records = road.Element("lateralProfile")!.Elements("superelevation").ToList();

        Assert.Equal(3, records.Count);
        Assert.Single(road.Elements("lateralProfile")); // the empty one was replaced, not duplicated

        double A(int i) => double.Parse((string)records[i].Attribute("a")!, System.Globalization.CultureInfo.InvariantCulture);
        double B(int i) => double.Parse((string)records[i].Attribute("b")!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(Math.Atan(0.01), A(0), 9);
        Assert.Equal(Math.Atan(0.02), A(1), 9);
        // b carries the roll from this station to the next, so the profile is continuous.
        Assert.Equal((A(1) - A(0)) / 50.0, B(0), 12);
        Assert.Equal(0.0, B(2)); // last record has nothing to interpolate towards
    }

    [Fact]
    public void CrossfallFallsBackToFlatWhereAStationWasRejected()
    {
        var map = Load(RightOnlyXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 50.0); // s = 0, 50, 100
        // Planar at the first two stations; a kerb-like step at the road end, which is rejected.
        var heights = samples.Select(s => s.S >= 99.0
            ? (s.T < -2.0 ? 101.0 : 100.0)
            : 100.0 + s.T * 0.02).ToList();
        var fits = SuperelevationInjector.FitCrossSections(samples, heights);
        Assert.Equal(new[] { 0.0, 50.0 }, fits.Select(f => f.S));

        var road = XDocument.Parse(SuperelevationInjector.InjectSuperelevation(RightOnlyXodr, fits, samples))
            .Root!.Elements("road").Single();
        var records = road.Element("lateralProfile")!.Elements("superelevation").ToList();
        double A(int i) => double.Parse((string)records[i].Attribute("a")!, System.Globalization.CultureInfo.InvariantCulture);

        // A record is added at the rejected station so the roll returns to flat rather than
        // holding the last measured value across a stretch that was deliberately not trusted.
        Assert.Equal(new[] { "0", "50", "100" }, records.Select(r => (string)r.Attribute("s")!));
        Assert.Equal(Math.Atan(0.02), A(0), 9);
        Assert.Equal(0.0, A(2));
    }

    [Fact]
    public void Inject_SkipsARoadMeasuredInTooFewPlaces()
    {
        var map = Load(RightOnlyXodr);
        var samples = SuperelevationInjector.ExtractCrossSectionSamples(map, 25.0); // 5 stations
        var heights = samples.Select(s => 100.0 + s.T * 0.02).ToList();
        var fits = SuperelevationInjector.FitCrossSections(samples, heights);

        // Only one station survived: below the coverage floor, so the road is left alone.
        var sparse = fits.Take(1).ToList();
        var result = SuperelevationInjector.InjectSuperelevation(RightOnlyXodr, sparse, samples, minCoverage: 0.5);
        var road = XDocument.Parse(result).Root!.Elements("road").Single();
        Assert.Empty(road.Element("lateralProfile")!.Elements("superelevation"));
    }

    [Fact]
    public void Inject_LeavesRoadsWithoutFitsUntouched()
    {
        var result = SuperelevationInjector.InjectSuperelevation(
            RightOnlyXodr, Array.Empty<CrossSectionFit>());
        var road = XDocument.Parse(result).Root!.Elements("road").Single();
        Assert.Empty(road.Element("lateralProfile")!.Elements("superelevation"));
        // The geoReference must survive the round-trip.
        Assert.Contains("+proj=tmerc", XDocument.Parse(result).Root!.Element("header")!.Value);
    }
}
