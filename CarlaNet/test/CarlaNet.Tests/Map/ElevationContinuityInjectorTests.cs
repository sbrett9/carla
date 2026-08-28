// Offline tests for CarlaNet.Map.OpenDrive.ElevationContinuityInjector.
//
// The fixture is the shape the pass exists for: a junction connector whose sampled height at each
// end disagrees with the road it links, because netconvert put its reference line a lane over and
// the terrain there is not the same height.
using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;

namespace CarlaNet.Tests.Map;

public class ElevationContinuityInjectorTests
{
    private static string Road(string id, double length, string junction, string pre, string suc,
                              params (double S, double A, double B)[] elevation)
    {
        string records = string.Join("", elevation.Select(e =>
            $@"<elevation s=""{e.S.ToString(CultureInfo.InvariantCulture)}"" a=""{e.A.ToString(CultureInfo.InvariantCulture)}"" b=""{e.B.ToString(CultureInfo.InvariantCulture)}"" c=""0"" d=""0""/>"));
        return $@"<road name=""r{id}"" length=""{length.ToString(CultureInfo.InvariantCulture)}"" id=""{id}"" junction=""{junction}"">
    <link>{pre}{suc}</link>
    <planView><geometry s=""0"" x=""0"" y=""0"" hdg=""0"" length=""{length.ToString(CultureInfo.InvariantCulture)}""><line/></geometry></planView>
    <elevationProfile>{records}</elevationProfile>
    <lateralProfile/>
    <lanes><laneSection s=""0""><center><lane id=""0"" type=""none""/></center><right><lane id=""-1"" type=""driving""><link/><width sOffset=""0"" a=""3.5"" b=""0"" c=""0"" d=""0""/></lane></right></laneSection></lanes>
  </road>";
    }

    /// Road 1 ends at 10.0 m; connector 2 was sampled a lane over and reads 10.6 at its start and
    /// 12.4 at its end, while road 3 begins at 12.0. Both ends of the connector are wrong.
    private static string Map =>
$@"<?xml version=""1.0""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0""/>
  {Road("1", 200.0, "-1", "", @"<successor elementType=""junction"" elementId=""900""/>", (0.0, 0.0, 0.05))}
  {Road("2", 20.0, "900",
        @"<predecessor elementType=""road"" elementId=""1"" contactPoint=""end""/>",
        @"<successor elementType=""road"" elementId=""3"" contactPoint=""start""/>", (0.0, 10.6, 0.09))}
  {Road("3", 100.0, "-1", @"<predecessor elementType=""junction"" elementId=""900""/>", "", (0.0, 12.0, 0.0))}
  <junction name=""n"" id=""900"">
    <connection id=""0"" incomingRoad=""1"" connectingRoad=""2"" contactPoint=""start""><laneLink from=""-1"" to=""-1""/></connection>
  </junction>
</OpenDRIVE>";

    private static double Height(XElement road, double s)
    {
        XElement? chosen = null;
        foreach (var e in road.Element("elevationProfile")!.Elements("elevation"))
        {
            double rs = double.Parse((string)e.Attribute("s")!, CultureInfo.InvariantCulture);
            if (rs <= s + 1e-9 && (chosen == null ||
                rs > double.Parse((string)chosen.Attribute("s")!, CultureInfo.InvariantCulture)))
                chosen = e;
        }
        double ds = s - double.Parse((string)chosen!.Attribute("s")!, CultureInfo.InvariantCulture);
        double N(string k) => double.Parse((string)chosen.Attribute(k)!, CultureInfo.InvariantCulture);
        return N("a") + N("b") * ds + N("c") * ds * ds + N("d") * ds * ds * ds;
    }

    private static XElement RoadOf(string xml, string id)
        => XDocument.Parse(xml).Root!.Elements("road")
            .Single(r => (string)r.Attribute("id")! == id);

    [Fact]
    public void ConnectorIsBentOntoBothRoadsItLinks()
    {
        var result = ElevationContinuityInjector.Reconcile(Map, out var summary);
        var one = RoadOf(result, "1");
        var connector = RoadOf(result, "2");
        var three = RoadOf(result, "3");

        Assert.Equal(2, summary.Constraints);
        Assert.True(summary.MaxResidualMeters < 0.01,
            $"largest remaining disagreement {summary.MaxResidualMeters}");
        Assert.Equal(Height(one, 200.0), Height(connector, 0.0), 6);
        Assert.Equal(Height(three, 0.0), Height(connector, 20.0), 6);
    }

    [Fact]
    public void TheRoadsThemselvesKeepTheTerrainTheyWereSampledFrom()
    {
        var before = RoadOf(Map, "1");
        var after = RoadOf(ElevationContinuityInjector.Reconcile(Map), "1");
        foreach (var s in new[] { 0.0, 50.0, 120.0, 200.0 })
            Assert.Equal(Height(before, s), Height(after, s), 9);
        ElevationContinuityInjector.Reconcile(Map, out var summary);
        Assert.Equal(1, summary.RoadsBent); // only the connector
    }

    [Fact]
    public void ALongRoadIsCorrectedNearItsEndRatherThanTiltedAlongItsLength()
    {
        // With a connector on both sides of the contact the correction is split, so road 1 moves
        // too — and that is the case where confining it to a transition matters.
        string bothConnectors = Map.Replace(@"id=""1"" junction=""-1""", @"id=""1"" junction=""901""");
        var after = RoadOf(ElevationContinuityInjector.Reconcile(bothConnectors), "1");
        var before = RoadOf(Map, "1");
        // Well inside the road, beyond the transition, nothing has moved.
        Assert.Equal(Height(before, 100.0), Height(after, 100.0), 9);
        Assert.NotEqual(Height(before, 200.0), Height(after, 200.0));
    }

    [Fact]
    public void AMapAlreadyContinuousIsLeftAlone()
    {
        string aligned = Map
            .Replace(@"a=""10.6"" b=""0.09""", @"a=""10"" b=""0.1""")
            .Replace(@"a=""12"" b=""0""", @"a=""12"" b=""0""");
        ElevationContinuityInjector.Reconcile(aligned, out var summary);
        Assert.Equal(0, summary.RoadsBent);
    }
}
