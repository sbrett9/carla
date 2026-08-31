// Offline tests for CarlaNet.Map.OpenDrive.JunctionSurfaceReconciler.
//
// The fixture is the shape the pass exists for: two connecting roads that cross inside a junction
// without linking to each other, so nothing in the document says their surfaces meet, carrying
// heights that differ where they overlap.
using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;

namespace CarlaNet.Tests.Map;

public class JunctionSurfaceReconcilerTests
{
    /// A crossroads: a 40 m connector running east and a 40 m connector running north, crossing
    /// at their midpoints. Each is flat, at the height given, so where they cross they disagree.
    private static string Crossing(double eastHeight, double northHeight, string junction = "1")
        => $"""
        <OpenDRIVE>
          <road name="east" length="40.0" id="10" junction="{junction}">
            <planView><geometry s="0.0" x="-20.0" y="0.0" hdg="0.0" length="40.0"><line/></geometry></planView>
            <elevationProfile><elevation s="0.0" a="{N(eastHeight)}" b="0.0" c="0.0" d="0.0"/></elevationProfile>
            <lanes><laneSection s="0.0">
              <right><lane id="-1" type="driving" level="false"><width sOffset="0.0" a="3.5" b="0.0" c="0.0" d="0.0"/></lane></right>
            </laneSection></lanes>
          </road>
          <road name="north" length="40.0" id="11" junction="{junction}">
            <planView><geometry s="0.0" x="0.0" y="-20.0" hdg="1.5707963267948966" length="40.0"><line/></geometry></planView>
            <elevationProfile><elevation s="0.0" a="{N(northHeight)}" b="0.0" c="0.0" d="0.0"/></elevationProfile>
            <lanes><laneSection s="0.0">
              <right><lane id="-1" type="driving" level="false"><width sOffset="0.0" a="3.5" b="0.0" c="0.0" d="0.0"/></lane></right>
            </laneSection></lanes>
          </road>
          <junction id="{junction}" name="j"/>
        </OpenDRIVE>
        """;

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static CarlaNet.Map.Road.Map Parse(string xml) => OpenDriveParser.Load(xml)!;

    private static double HeightAt(string xml, string roadId, double s)
    {
        var road = XDocument.Parse(xml).Root!.Elements("road")
            .First(r => (string?)r.Attribute("id") == roadId);
        var records = road.Elements("elevationProfile").Elements("elevation")
            .Select(e => (S: Num(e, "s"), A: Num(e, "a"), B: Num(e, "b"), C: Num(e, "c"), D: Num(e, "d")))
            .OrderBy(r => r.S).ToList();
        var chosen = records[0];
        foreach (var record in records)
        {
            if (record.S <= s + 1e-9) chosen = record; else break;
        }
        double ds = s - chosen.S;
        return chosen.A + chosen.B * ds + chosen.C * ds * ds + chosen.D * ds * ds * ds;
    }

    private static int Records(string xml, string roadId)
        => XDocument.Parse(xml).Root!.Elements("road")
            .First(r => (string?)r.Attribute("id") == roadId)
            .Elements("elevationProfile").Elements("elevation").Count();

    private static double Num(XElement e, string name)
        => double.Parse((string?)e.Attribute(name) ?? "0", CultureInfo.InvariantCulture);

    [Fact]
    public void CrossingConnectorsAreBroughtToTheSameHeightWhereTheyMeet()
    {
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.6);
        string result = JunctionSurfaceReconciler.Reconcile(xml, Parse(xml), out var summary);

        Assert.True(summary.Overlaps > 0, "the two connectors must be seen to overlap");
        Assert.Equal(0.6, summary.MaxBeforeMeters, 2);
        Assert.True(summary.MaxAfterMeters < 0.05,
            $"crossing surfaces still disagree by {summary.MaxAfterMeters:F3} m");
        // Both connectors move most of the way to a shared height; what is left is the width of
        // the crossing, over which a flat ribbon cannot follow a surface that is going somewhere.
        Assert.True(Math.Abs(HeightAt(result, "10", 20.0) - HeightAt(result, "11", 20.0)) < 0.05);
        Assert.InRange(HeightAt(result, "10", 20.0), 10.2, 10.4);
        Assert.InRange(HeightAt(result, "11", 20.0), 10.2, 10.4);
    }

    [Fact]
    public void TheJunctionBoundaryDoesNotMove()
    {
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.6);
        string result = JunctionSurfaceReconciler.Reconcile(xml, Parse(xml), out var summary);

        // Connector ends are where reconciled contacts live; the pass must not trade one seam
        // for another.
        Assert.True(summary.MaxBoundaryShiftMeters <= 0.02,
            $"junction boundary moved {summary.MaxBoundaryShiftMeters:F4} m");
        foreach (var (road, height) in new[] { ("10", 10.0), ("11", 10.6) })
        {
            Assert.Equal(height, HeightAt(result, road, 0.0), 2);
            Assert.Equal(height, HeightAt(result, road, 40.0), 2);
        }
    }

    [Fact]
    public void SurfacesThatAlreadyAgreeAreLeftWhereTheyAre()
    {
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.0);
        string result = JunctionSurfaceReconciler.Reconcile(xml, Parse(xml), out var summary);

        Assert.Equal(0.0, summary.MaxBeforeMeters, 6);
        foreach (double s in new[] { 0.0, 10.0, 20.0, 30.0, 40.0 })
        {
            Assert.Equal(10.0, HeightAt(result, "10", s), 4);
            Assert.Equal(10.0, HeightAt(result, "11", s), 4);
        }
    }

    [Fact]
    public void RoadsOutsideAJunctionAreNotTouched()
    {
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.6, junction: "-1");
        string result = JunctionSurfaceReconciler.Reconcile(xml, Parse(xml), out var summary);

        Assert.Equal(0, summary.Junctions);
        Assert.Equal(0, summary.Overlaps);
        Assert.Equal(10.0, HeightAt(result, "10", 20.0), 6);
        Assert.Equal(10.6, HeightAt(result, "11", 20.0), 6);
    }

    [Fact]
    public void ConnectorsThatDoNotOverlapAreNotDraggedTogether()
    {
        // The same two connectors, moved apart so their surfaces never cover the same ground.
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.6)
            .Replace("x=\"0.0\" y=\"-20.0\"", "x=\"500.0\" y=\"-20.0\"", StringComparison.Ordinal);
        string result = JunctionSurfaceReconciler.Reconcile(xml, Parse(xml), out var summary);

        Assert.Equal(0, summary.Overlaps);
        Assert.Equal(10.0, HeightAt(result, "10", 20.0), 6);
        Assert.Equal(10.6, HeightAt(result, "11", 20.0), 6);
    }

    [Fact]
    public void AJunctionIsLeftAloneWhenItsBoundaryWouldHaveToMove()
    {
        // Held to no movement at all, the pass must decline the junction rather than shift an
        // end that a reconciled contact depends on, and leave the document exactly as it was.
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.6);
        string result = JunctionSurfaceReconciler.Reconcile(
            xml, Parse(xml), out var summary, maxBoundaryShiftMeters: 0.0);

        Assert.Equal(1, summary.JunctionsHeld);
        Assert.Equal(summary.MaxBeforeMeters, summary.MaxAfterMeters, 6);
        Assert.Equal(10.0, HeightAt(result, "10", 20.0), 6);
        Assert.Equal(10.6, HeightAt(result, "11", 20.0), 6);
    }

    [Fact]
    public void AConnectorThatCameOutStraightIsDescribedByItsEndsAlone()
    {
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.0);
        string result = JunctionSurfaceReconciler.Reconcile(xml, Parse(xml), out _);

        // One record per kept station, and a straight connector keeps only its two ends.
        Assert.Equal(2, Records(result, "10"));
        Assert.Equal(2, Records(result, "11"));
    }

    [Fact]
    public void AllowingTheProfileToStrayFurtherCostsFewerRecords()
    {
        string xml = Crossing(eastHeight: 10.0, northHeight: 10.6);
        int tight = Records(JunctionSurfaceReconciler.Reconcile(
            xml, Parse(xml), out _, simplifyToleranceMeters: 0.002), "10");
        int loose = Records(JunctionSurfaceReconciler.Reconcile(
            xml, Parse(xml), out _, simplifyToleranceMeters: 0.05), "10");

        Assert.True(loose < tight, $"loosening the tolerance kept {loose} records against {tight}");
        Assert.True(tight <= 27, "a profile must never cost more records than it has stations");
    }

    [Fact]
    public void ANullDocumentIsRejected()
        => Assert.Throws<ArgumentNullException>(
            () => JunctionSurfaceReconciler.Reconcile(null!, Parse(Crossing(10.0, 10.0))));
}
