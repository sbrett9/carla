// Tests for the shared height series given to the two directions of one street.
//
// netconvert models each direction of a two-way street as its own edge, so one physical street
// arrives as two <road> records on a coincident centreline, traversed in opposite directions.
// Fitted independently they disagree, and the two halves of the street meet at different heights
// along its centre line.
using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.Map;

public class ElevationInjectorCarriagewayTests
{
    private const string Header =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0"" vendor=""test"">
    <geoReference><![CDATA[+proj=tmerc +lat_0=41.94813 +lon_0=-87.65593 +k=1 +x_0=0 +y_0=0 +datum=WGS84 +units=m +no_defs]]></geoReference>
  </header>";

    private const string Lanes =
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

    // Road 1 runs east from the origin; road 2 runs west from its far end along the same line.
    // Road 2's station s is road 1's station 100 - s.
    private static string PairedXodr =>
$@"{Header}
  <road name=""Main Street"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
  <road name=""Main Street"" length=""100.0"" id=""2"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""100.0"" y=""0.0"" hdg=""3.14159265358979"" length=""100.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
</OpenDRIVE>";

    // Same two roads, but the second runs the same way as the first — not a carriageway pair.
    private static string ParallelXodr =>
$@"{Header}
  <road name=""a"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
  <road name=""b"" length=""100.0"" id=""2"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
{Lanes}
  </road>
</OpenDRIVE>";

    private readonly record struct Record(double S, double A, double B, double C, double D)
    {
        public double Evaluate(double s)
        {
            double ds = s - S;
            return A + B * ds + C * ds * ds + D * ds * ds * ds;
        }
    }

    private static Record[] RecordsOf(string xodr, string roadId)
    {
        double Value(XElement e, string n) =>
            double.Parse(e.Attribute(n)!.Value, CultureInfo.InvariantCulture);
        return XDocument.Parse(xodr).Root!.Elements("road")
            .Single(r => r.Attribute("id")?.Value == roadId)
            .Element("elevationProfile")!.Elements("elevation")
            .Select(e => new Record(Value(e, "s"), Value(e, "a"), Value(e, "b"),
                                    Value(e, "c"), Value(e, "d")))
            .OrderBy(r => r.S).ToArray();
    }

    private static double HeightAt(Record[] records, double s)
    {
        var chosen = records[0];
        foreach (var r in records)
        {
            if (r.S <= s + 1e-9) chosen = r;
            else break;
        }
        return chosen.Evaluate(s);
    }

    private static string Inject(string xodr, Func<uint, double, double> height,
        Func<uint, double, bool>? raised = null,
        ElevationFitMode mode = ElevationFitMode.MonotoneCubicHermite)
    {
        var map = OpenDriveParser.Load(xodr) ?? throw new Exception("parse failed");
        var samples = ElevationInjector.ExtractCenterlineSamples(map, 10.0);
        var heights = samples.Select(s => height(s.RoadId, s.S)).ToArray();
        var flags = raised is null ? null : samples.Select(s => raised(s.RoadId, s.S)).ToArray();
        return ElevationInjector.InjectElevation(xodr, samples, heights, 0.0, mode, 4.0, flags);
    }

    [Fact]
    public void OpposingCarriagewaysAgreeAtMatchedStations()
    {
        // Each direction sampled independently off the same slope, with a little noise that lands
        // at different physical points because road 2's grid is the mirror of road 1's.
        var elevated = Inject(PairedXodr, (road, s) => road == 1u
            ? 0.04 * s + 0.15 * Math.Sin(s / 7.0)
            : 0.04 * (100.0 - s) + 0.15 * Math.Cos(s / 5.0));

        var left = RecordsOf(elevated, "1");
        var right = RecordsOf(elevated, "2");

        for (double s = 0.0; s <= 100.0; s += 2.5)
        {
            double a = HeightAt(left, s);
            double b = HeightAt(right, 100.0 - s);
            Assert.True(Math.Abs(a - b) < 1e-6,
                $"carriageways disagree by {Math.Abs(a - b):F6} m at s={s}");
        }
    }

    [Fact]
    public void TheLowerSurfaceIsTaken()
    {
        // Road 2's samples sit two metres above road 1's along the whole street, as they would if
        // they had landed on something standing over the road. The error is one-sided — a surface
        // model can put a sample above the ground, never below it — so the lower one wins.
        var elevated = Inject(PairedXodr, (road, _) => road == 1u ? 5.0 : 7.0);

        Assert.Equal(5.0, HeightAt(RecordsOf(elevated, "1"), 50.0), 6);
        Assert.Equal(5.0, HeightAt(RecordsOf(elevated, "2"), 50.0), 6);
    }

    [Fact]
    public void ADeliberatelyRaisedCarriagewayIsLeftAlone()
    {
        // Road 2 is a deck routed to the photoreal surface. Here the height difference is a real
        // grade separation, so taking the lower would drop the deck onto the road beneath it.
        var elevated = Inject(PairedXodr, (road, _) => road == 1u ? 5.0 : 12.0,
            raised: (road, _) => road == 2u);

        Assert.Equal(5.0, HeightAt(RecordsOf(elevated, "1"), 50.0), 6);
        Assert.Equal(12.0, HeightAt(RecordsOf(elevated, "2"), 50.0), 6);
    }

    [Fact]
    public void RoadsRunningTheSameWayAreNotAPair()
    {
        // Coincident but not opposing: two roads travelling the same direction are not the two
        // halves of one street, so their heights stay independent.
        var elevated = Inject(ParallelXodr, (road, _) => road == 1u ? 5.0 : 7.0);

        Assert.Equal(5.0, HeightAt(RecordsOf(elevated, "1"), 50.0), 6);
        Assert.Equal(7.0, HeightAt(RecordsOf(elevated, "2"), 50.0), 6);
    }

    [Fact]
    public void PiecewiseLinearLeavesCarriagewaysIndependent()
    {
        // The merge rides with the C1 fit only, so a caller pinning an older mode is unaffected.
        var elevated = Inject(PairedXodr, (road, _) => road == 1u ? 5.0 : 7.0,
            mode: ElevationFitMode.PiecewiseLinear);

        Assert.Equal(5.0, HeightAt(RecordsOf(elevated, "1"), 50.0), 6);
        Assert.Equal(7.0, HeightAt(RecordsOf(elevated, "2"), 50.0), 6);
    }
}
