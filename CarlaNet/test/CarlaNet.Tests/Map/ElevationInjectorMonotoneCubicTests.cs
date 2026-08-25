// Tests for ElevationFitMode.MonotoneCubicHermite — the C1 fit and the junction height/slope
// resolution that rides with it.
//
// Offline (no engine). Two fixtures: the straight road exercises the fit itself, and the
// road -> connector -> road chain mirrors what netconvert emits at an intersection, where every
// road-to-road link is between a junction connector and a road it joins.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.Map;

public class ElevationInjectorMonotoneCubicTests
{
    private const string Header =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0"" vendor=""test"">
    <geoReference><![CDATA[+proj=tmerc +lat_0=41.94813 +lon_0=-87.65593 +k=1 +x_0=0 +y_0=0 +datum=WGS84 +units=m +no_defs]]></geoReference>
  </header>";

    private static string Lanes =>
@"    <lanes>
      <laneSection s=""0.0"">
        <center><lane id=""0"" type=""driving"" level=""false""/></center>
        <right>
          <lane id=""-1"" type=""driving"" level=""false"">
            <width sOffset=""0.0"" a=""3.5"" b=""0.0"" c=""0.0"" d=""0.0""/>
          </lane>
        </right>
      </laneSection>
    </lanes>";

    private static string StraightXodr =>
$@"{Header}
  <road name=""r1"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
</OpenDRIVE>";

    // Road 1 runs east to x=100, connector 2 carries the next ten metres inside junction 100, and
    // road 3 continues east. Roads link to the junction; only the connector carries road-to-road
    // links, which is the structure the resolution keys off.
    private static string JunctionXodr =>
$@"{Header}
  <road name=""r1"" length=""100.0"" id=""1"" junction=""-1"">
    <link><successor elementType=""junction"" elementId=""100""/></link>
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
  <road name=""c2"" length=""10.0"" id=""2"" junction=""100"">
    <link>
      <predecessor elementType=""road"" elementId=""1"" contactPoint=""end""/>
      <successor elementType=""road"" elementId=""3"" contactPoint=""start""/>
    </link>
    <planView>
      <geometry s=""0.0"" x=""100.0"" y=""0.0"" hdg=""0.0"" length=""10.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
  <road name=""r3"" length=""100.0"" id=""3"" junction=""-1"">
    <link><predecessor elementType=""junction"" elementId=""100""/></link>
    <planView>
      <geometry s=""0.0"" x=""110.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
  <junction id=""100"" name=""j100"">
    <connection id=""0"" incomingRoad=""1"" connectingRoad=""2"" contactPoint=""start"">
      <laneLink from=""-1"" to=""-1""/>
    </connection>
  </junction>
</OpenDRIVE>";

    private static RoadMap Load(string xodr)
        => OpenDriveParser.Load(xodr) ?? throw new Exception(".xodr failed to parse");

    private readonly record struct Record(double S, double A, double B, double C, double D)
    {
        public double Evaluate(double s)
        {
            double ds = s - S;
            return A + B * ds + C * ds * ds + D * ds * ds * ds;
        }

        public double Tangent(double s)
        {
            double ds = s - S;
            return B + 2.0 * C * ds + 3.0 * D * ds * ds;
        }
    }

    private static List<Record> RecordsOf(string xodr, string roadId)
    {
        var road = XDocument.Parse(xodr).Root!.Elements("road")
            .Single(r => r.Attribute("id")?.Value == roadId);
        double Value(XElement e, string name) =>
            double.Parse(e.Attribute(name)!.Value, CultureInfo.InvariantCulture);
        return road.Element("elevationProfile")!.Elements("elevation")
            .Select(e => new Record(Value(e, "s"), Value(e, "a"), Value(e, "b"),
                                    Value(e, "c"), Value(e, "d")))
            .OrderBy(r => r.S)
            .ToList();
    }

    /// <summary>Injects heights produced by <paramref name="height"/> from (roadId, station).</summary>
    private static string Inject(string xodr, Func<uint, double, double> height,
        ElevationFitMode mode = ElevationFitMode.MonotoneCubicHermite,
        Func<uint, double, bool>? raised = null, double step = 10.0)
    {
        var samples = ElevationInjector.ExtractCenterlineSamples(Load(xodr), step);
        var heights = samples.Select(s => height(s.RoadId, s.S)).ToArray();
        var flags = raised is null
            ? null
            : samples.Select(s => raised(s.RoadId, s.S)).ToArray();
        return ElevationInjector.InjectElevation(xodr, samples, heights, 0.0, mode, 4.0, flags);
    }

    // ── the fit ──────────────────────────────────────────────────────────────

    [Fact]
    public void SlopeIsContinuousAtEveryRecordBoundary()
    {
        // A rolling profile, so consecutive spans genuinely carry different grades.
        var elevated = Inject(StraightXodr, (_, s) => 5.0 * Math.Sin(s / 25.0));
        var records = RecordsOf(elevated, "1");

        Assert.True(records.Count > 3);
        for (int i = 1; i < records.Count; ++i)
        {
            double step = Math.Abs(records[i].B - records[i - 1].Tangent(records[i].S));
            Assert.True(step < 1e-9, $"slope step {step} at s={records[i].S}");
        }
    }

    [Fact]
    public void HeightIsExactAtEverySample()
    {
        var elevated = Inject(StraightXodr, (_, s) => 5.0 * Math.Sin(s / 25.0));
        foreach (var record in RecordsOf(elevated, "1"))
            Assert.Equal(5.0 * Math.Sin(record.S / 25.0), record.A, 9);
    }

    [Fact]
    public void EmitsCurvatureRatherThanStraightRamps()
    {
        var records = RecordsOf(Inject(StraightXodr, (_, s) => 5.0 * Math.Sin(s / 25.0)), "1");
        Assert.Contains(records, r => r.C != 0.0);
        Assert.Contains(records, r => r.D != 0.0);
    }

    [Fact]
    public void LastRecordCarriesTheApproachGradeRatherThanZero()
    {
        // A steady 5 % climb: the road must hand that grade on, not arrive flat.
        var records = RecordsOf(Inject(StraightXodr, (_, s) => 0.05 * s), "1");
        Assert.Equal(0.05, records[^1].B, 6);
    }

    [Fact]
    public void PiecewiseLinearStillEndsFlat()
    {
        // The older modes are deliberately untouched, so a caller pinning one keeps the old output.
        var records = RecordsOf(
            Inject(StraightXodr, (_, s) => 0.05 * s, ElevationFitMode.PiecewiseLinear), "1");
        Assert.Equal(0.0, records[^1].B);
        Assert.All(records, r => Assert.Equal(0.0, r.C));
        Assert.All(records, r => Assert.Equal(0.0, r.D));
    }

    [Fact]
    public void DoesNotOvershootBracketingSamples()
    {
        // A plateau, a step, then a plateau — the shape an unconstrained spline rings on.
        var elevated = Inject(StraightXodr, (_, s) => s < 45.0 ? 0.0 : 10.0);
        var records = RecordsOf(elevated, "1");

        for (int i = 0; i < records.Count - 1; ++i)
        {
            double low = Math.Min(records[i].A, records[i + 1].A);
            double high = Math.Max(records[i].A, records[i + 1].A);
            for (int k = 1; k < 20; ++k)
            {
                double s = records[i].S + (records[i + 1].S - records[i].S) * k / 20.0;
                double z = records[i].Evaluate(s);
                Assert.InRange(z, low - 1e-9, high + 1e-9);
            }
        }
    }

    [Fact]
    public void FlatRoadStaysFlat()
    {
        var records = RecordsOf(Inject(StraightXodr, (_, _) => 7.0), "1");
        Assert.All(records, r =>
        {
            Assert.Equal(7.0, r.A, 9);
            Assert.Equal(0.0, r.B, 9);
            Assert.Equal(0.0, r.C, 9);
            Assert.Equal(0.0, r.D, 9);
        });
    }

    [Fact]
    public void ShortTerminalSpanDoesNotExplodeCoefficients()
    {
        // 100.0 / 33.3 leaves a 0.1 m remainder as the final span. Dividing a step's worth of
        // height change by it would otherwise blow the cubic coefficients up.
        var elevated = Inject(StraightXodr, (_, s) => 0.05 * s, step: 33.3);
        var records = RecordsOf(elevated, "1");

        Assert.All(records, r =>
        {
            Assert.True(Math.Abs(r.C) < 1.0, $"c={r.C} at s={r.S}");
            Assert.True(Math.Abs(r.D) < 1.0, $"d={r.D} at s={r.S}");
        });
        // The road end is still hit exactly; only interior stations may be absorbed.
        Assert.Equal(100.0, records[^1].S, 6);
        Assert.Equal(5.0, records[^1].A, 6);
    }

    // ── junction continuity ──────────────────────────────────────────────────

    [Fact]
    public void LinkedRoadEndsAgreeInHeightAndSlope()
    {
        // The connector is sampled a few centimetres off the roads it joins and climbs harder than
        // either — the ordinary case, where the measured height disagreement at a node is small.
        var elevated = Inject(JunctionXodr, (road, s) => road switch
        {
            1u => 0.05 * s,
            2u => 5.04 + 0.09 * s,
            _ => 5.98 + 0.05 * s,
        });

        var road1 = RecordsOf(elevated, "1");
        var connector = RecordsOf(elevated, "2");
        var road3 = RecordsOf(elevated, "3");

        Assert.Equal(road1[^1].A, connector[0].A, 6);
        Assert.Equal(connector[^1].A, road3[0].A, 6);
        Assert.Equal(road1[^1].B, connector[0].B, 6);
        Assert.Equal(connector[^1].B, road3[0].B, 6);
    }

    [Fact]
    public void MonotonicityWinsWhereAResolvedGradeWouldOvershoot()
    {
        // Here the connector is sampled a full metre above the roads, so resolving the node moves
        // road 3's start up and leaves its first span nearly flat. The grade carried through the
        // junction no longer fits inside that span, and the tangent limiting pulls it back: a road
        // that cannot carry the shared slope without overshooting its own samples gives up the
        // slope rather than the surface. Heights still resolve exactly.
        var elevated = Inject(JunctionXodr, (road, s) => road switch
        {
            1u => 0.05 * s,
            2u => 6.0 + 0.30 * s,
            _ => 5.5 + 0.05 * s,
        });

        var connector = RecordsOf(elevated, "2");
        var road3 = RecordsOf(elevated, "3");

        Assert.Equal(connector[^1].A, road3[0].A, 6);
        Assert.True(road3[0].B < connector[^1].B,
            $"expected the limiter to pull {connector[^1].B} back, got {road3[0].B}");

        // Whatever the limiter did, the surface stays inside its own samples.
        for (int k = 1; k < 20; ++k)
        {
            double s = road3[0].S + (road3[1].S - road3[0].S) * k / 20.0;
            Assert.InRange(road3[0].Evaluate(s),
                Math.Min(road3[0].A, road3[1].A) - 1e-9,
                Math.Max(road3[0].A, road3[1].A) + 1e-9);
        }
    }

    [Fact]
    public void TheLongerRoadSetsTheGradeThroughAJunction()
    {
        // Length-weighted, so a 10 m connector climbing at 30 % does not tilt a 100 m road that is
        // climbing at 5 %.
        var elevated = Inject(JunctionXodr, (road, s) => road switch
        {
            1u => 0.05 * s,
            2u => 5.0 + 0.30 * s,
            _ => 8.0 + 0.05 * s,
        });

        double throughGrade = RecordsOf(elevated, "1")[^1].B;
        Assert.InRange(throughGrade, 0.04, 0.10);
    }

    [Fact]
    public void PiecewiseLinearLeavesJunctionsAlone()
    {
        // Resolution rides with the C1 fit only, so the older modes produce what they always did:
        // the connector keeps its own sampled start height and the road still ends flat.
        var elevated = Inject(JunctionXodr, (road, s) => road switch
        {
            1u => 0.05 * s,
            2u => 6.0 + 0.30 * s,
            _ => 5.5 + 0.05 * s,
        }, ElevationFitMode.PiecewiseLinear);

        Assert.Equal(6.0, RecordsOf(elevated, "2")[0].A, 6);
        Assert.Equal(0.0, RecordsOf(elevated, "1")[^1].B);
    }

    [Fact]
    public void ADeckMeetingTheGroundIsNotAveragedAway()
    {
        // The connector is a raised deck and the roads it joins are not. Merging their heights
        // would drag the deck down into the road beneath it — the defect the layer routing exists
        // to prevent — so a node whose ends disagree about being raised is left alone.
        var elevated = Inject(JunctionXodr,
            (road, s) => road switch
            {
                1u => 0.05 * s,
                2u => 20.0,
                _ => 5.0 + 0.05 * s,
            },
            raised: (road, _) => road == 2u);

        Assert.Equal(20.0, RecordsOf(elevated, "2")[0].A, 6);
        Assert.Equal(5.0, RecordsOf(elevated, "1")[^1].A, 6);
    }
}
