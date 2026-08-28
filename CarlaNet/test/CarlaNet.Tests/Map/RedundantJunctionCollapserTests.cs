// Offline tests for CarlaNet.Map.OpenDrive.RedundantJunctionCollapser.
//
// The fixtures mimic what netconvert emits at a node that offers no choice of route: an
// ordinary road, a short connector owned by a one-connection junction, and another ordinary
// road, with the connector's lane links naming the lane ids either side of it.
using System;
using System.Linq;
using System.Globalization;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;

namespace CarlaNet.Tests.Map;

public class RedundantJunctionCollapserTests
{
    private static string Road(string id, double length, string junction, string lanes,
                               string pre, string suc, string extra = "")
        => $@"<road name=""r{id}"" length=""{length.ToString(CultureInfo.InvariantCulture)}"" id=""{id}"" junction=""{junction}"">
    <link>{pre}{suc}</link>
    <type s=""0"" type=""town""/>
    <planView><geometry s=""0"" x=""0"" y=""0"" hdg=""0"" length=""{length.ToString(CultureInfo.InvariantCulture)}""><line/></geometry></planView>
    <elevationProfile><elevation s=""0"" a=""1"" b=""0"" c=""0"" d=""0""/></elevationProfile>
    <lateralProfile/>
    <lanes><laneSection s=""0"">{lanes}</laneSection></lanes>
    <objects/><signals/>{extra}
  </road>";

    private static string Lane(string id, string pre = "", string suc = "")
    {
        string link = pre == "" && suc == ""
            ? "<link/>"
            : $@"<link>{(pre == "" ? "" : $@"<predecessor id=""{pre}""/>")}{(suc == "" ? "" : $@"<successor id=""{suc}""/>")}</link>";
        return $@"<lane id=""{id}"" type=""driving"" level=""false"">{link}<width sOffset=""0"" a=""3.5"" b=""0"" c=""0"" d=""0""/></lane>";
    }

    private static string Right(params string[] lanes)
        => @"<center><lane id=""0"" type=""none""/></center><right>" + string.Join("", lanes) + "</right>";

    /// Road 1 -(junction 900)- road 3, joined by connector 2. Road 1 has two lanes, road 3 one,
    /// so the connector drops a lane the way a real lane-count change does.
    private static string PassThroughMap => $@"<?xml version=""1.0""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0""/>
  {Road("1", 100.0, "-1", Right(Lane("-1"), Lane("-2")),
        @"<predecessor elementType=""junction"" elementId=""800""/>",
        @"<successor elementType=""junction"" elementId=""900""/>")}
  {Road("2", 10.0, "900", Right(Lane("-1", "-1", "-1")),
        @"<predecessor elementType=""road"" elementId=""1"" contactPoint=""end""/>",
        @"<successor elementType=""road"" elementId=""3"" contactPoint=""start""/>")}
  {Road("3", 50.0, "-1", Right(Lane("-1")),
        @"<predecessor elementType=""junction"" elementId=""900""/>",
        @"<successor elementType=""junction"" elementId=""910""/>")}
  <junction name=""upstream"" id=""800""/>
  <junction name=""n"" id=""900"">
    <connection id=""0"" incomingRoad=""1"" connectingRoad=""2"" contactPoint=""start""><laneLink from=""-1"" to=""-1""/></connection>
  </junction>
  <junction name=""downstream"" id=""910"">
    <connection id=""0"" incomingRoad=""3"" connectingRoad=""3"" contactPoint=""start""><laneLink from=""-1"" to=""-1""/></connection>
  </junction>
</OpenDRIVE>";

    private static XElement Parse(string xml) => XDocument.Parse(xml).Root!;

    [Fact]
    public void PassThroughJunction_BecomesOneRoad()
    {
        var root = Parse(RedundantJunctionCollapser.Collapse(PassThroughMap, out var summary));

        Assert.Equal(1, summary.Collapsed);
        Assert.Equal(new[] { "1" }, root.Elements("road").Select(r => (string)r.Attribute("id")!));
        Assert.Equal(new[] { "800", "910" }, root.Elements("junction").Select(j => (string)j.Attribute("id")!));

        var road = root.Elements("road").Single();
        Assert.Equal(160.0, double.Parse((string)road.Attribute("length")!, CultureInfo.InvariantCulture), 6);
        // The merged road keeps its own start and takes over where the absorbed one ended.
        Assert.Equal("800", (string)road.Element("link")!.Element("predecessor")!.Attribute("elementId")!);
        Assert.Equal("910", (string)road.Element("link")!.Element("successor")!.Attribute("elementId")!);
    }

    [Fact]
    public void RecordsAreConcatenatedAtTheirNewStations()
    {
        var road = Parse(RedundantJunctionCollapser.Collapse(PassThroughMap)).Elements("road").Single();
        double[] S(string container, string child) => road.Element(container)!.Elements(child)
            .Select(e => double.Parse((string)e.Attribute("s")!, CultureInfo.InvariantCulture)).ToArray();

        Assert.Equal(new[] { 0.0, 100.0, 110.0 }, S("planView", "geometry"));
        Assert.Equal(new[] { 0.0, 100.0, 110.0 }, S("elevationProfile", "elevation"));
        Assert.Equal(new[] { 0.0, 100.0, 110.0 }, S("lanes", "laneSection"));
        Assert.Equal(new[] { 0.0, 100.0, 110.0 },
            road.Elements("type").Select(e => double.Parse((string)e.Attribute("s")!, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void LaneSectionsAreLinkedThroughTheConnectorsOwnCorrespondence()
    {
        var road = Parse(RedundantJunctionCollapser.Collapse(PassThroughMap)).Elements("road").Single();
        var sections = road.Element("lanes")!.Elements("laneSection").ToList();

        string? Succ(XElement section, string laneId) => (string?)section.Element("right")!.Elements("lane")
            .Single(l => (string?)l.Attribute("id") == laneId).Element("link")?.Element("successor")?.Attribute("id");
        string? Pred(XElement section, string laneId) => (string?)section.Element("right")!.Elements("lane")
            .Single(l => (string?)l.Attribute("id") == laneId).Element("link")?.Element("predecessor")?.Attribute("id");

        // The lane the connector carries is linked through; the one it drops gains no successor.
        Assert.Equal("-1", Succ(sections[0], "-1"));
        Assert.Null(Succ(sections[0], "-2"));
        Assert.Equal("-1", Pred(sections[1], "-1"));
        Assert.Equal("-1", Succ(sections[1], "-1"));
        Assert.Equal("-1", Pred(sections[2], "-1"));
    }

    [Fact]
    public void ASignalisedPassThroughJunctionIsKept()
    {
        // Shape-wise this junction is collapsible, but it drives traffic lights. Removing it would
        // take the phase program with it and orphan the heads.
        string signalised = PassThroughMap.Replace(
            @"<junction name=""n"" id=""900"">",
            @"<junction name=""n"" id=""900"">
    <controller id=""900_p0"" type=""0""/>");

        var result = RedundantJunctionCollapser.Collapse(signalised, out var summary);
        Assert.Equal(0, summary.Collapsed);
        Assert.Equal(1, summary.SkippedSignalised);
        var root = Parse(result);
        Assert.Equal(3, root.Elements("road").Count());
        Assert.Single(root.Elements("junction")
            .Single(j => (string?)j.Attribute("id") == "900").Elements("controller"));
    }

    [Fact]
    public void SignalHeadsRideOntoTheMergedRoad()
    {
        // A signal on the road that gets absorbed must reappear on the merged road, at the station
        // the merge moved it to, or the junction it belongs to loses a head.
        var doc = XDocument.Parse(PassThroughMap);
        var absorbed = doc.Root!.Elements("road").Single(r => (string)r.Attribute("id")! == "3");
        absorbed.Element("signals")!.Add(new XElement("signal",
            new XAttribute("s", "20"), new XAttribute("t", "-5"), new XAttribute("id", "sig1"),
            new XAttribute("dynamic", "yes"), new XAttribute("orientation", "-")));

        var root = Parse(RedundantJunctionCollapser.Collapse(doc.ToString(), out var summary));
        Assert.Equal(1, summary.Collapsed);

        var merged = root.Elements("road").Single();
        var signal = Assert.Single(merged.Element("signals")!.Elements("signal"));
        Assert.Equal("sig1", (string)signal.Attribute("id")!);
        // Road 3 began 110 m along the merged road, so its 20 m mark is now at 130 m.
        Assert.Equal(130.0, double.Parse((string)signal.Attribute("s")!, CultureInfo.InvariantCulture), 6);
    }


    [Fact]
    public void AJunctionOfferingAChoiceIsLeftAlone()
    {
        // A second connector out of the same junction makes it a real fork.
        string forked = PassThroughMap.Replace(
            @"<connection id=""0"" incomingRoad=""1"" connectingRoad=""2"" contactPoint=""start""><laneLink from=""-1"" to=""-1""/></connection>",
            @"<connection id=""0"" incomingRoad=""1"" connectingRoad=""2"" contactPoint=""start""><laneLink from=""-1"" to=""-1""/></connection>
     <connection id=""1"" incomingRoad=""1"" connectingRoad=""4"" contactPoint=""start""><laneLink from=""-2"" to=""-1""/></connection>");

        var result = RedundantJunctionCollapser.Collapse(forked, out var summary);
        Assert.Equal(0, summary.Collapsed);
        Assert.Equal(3, Parse(result).Elements("road").Count());
    }

    [Fact]
    public void ReferencesFromElsewhereFollowTheMergedRoad()
    {
        var root = Parse(RedundantJunctionCollapser.Collapse(PassThroughMap));
        // The downstream junction named road 3, which no longer exists.
        var downstream = root.Elements("junction").Single(j => (string?)j.Attribute("id") == "910");
        Assert.All(downstream.Elements("connection"),
            c => Assert.Equal("1", (string)c.Attribute("incomingRoad")!));
    }

    [Fact]
    public void ChainedPassThroughJunctionsCollapseToASingleRoad()
    {
        // road 1 -(900)- road 3 -(910)- road 5, every junction a plain continuation.
        string chain = $@"<?xml version=""1.0""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0""/>
  {Road("1", 100.0, "-1", Right(Lane("-1")), "", @"<successor elementType=""junction"" elementId=""900""/>")}
  {Road("2", 10.0, "900", Right(Lane("-1", "-1", "-1")),
        @"<predecessor elementType=""road"" elementId=""1"" contactPoint=""end""/>",
        @"<successor elementType=""road"" elementId=""3"" contactPoint=""start""/>")}
  {Road("3", 50.0, "-1", Right(Lane("-1")),
        @"<predecessor elementType=""junction"" elementId=""900""/>",
        @"<successor elementType=""junction"" elementId=""910""/>")}
  {Road("4", 5.0, "910", Right(Lane("-1", "-1", "-1")),
        @"<predecessor elementType=""road"" elementId=""3"" contactPoint=""end""/>",
        @"<successor elementType=""road"" elementId=""5"" contactPoint=""start""/>")}
  {Road("5", 20.0, "-1", Right(Lane("-1")), @"<predecessor elementType=""junction"" elementId=""910""/>", "")}
  <junction name=""a"" id=""900""><connection id=""0"" incomingRoad=""1"" connectingRoad=""2"" contactPoint=""start""><laneLink from=""-1"" to=""-1""/></connection></junction>
  <junction name=""b"" id=""910""><connection id=""0"" incomingRoad=""3"" connectingRoad=""4"" contactPoint=""start""><laneLink from=""-1"" to=""-1""/></connection></junction>
</OpenDRIVE>";

        var root = Parse(RedundantJunctionCollapser.Collapse(chain, out var summary));
        Assert.Equal(2, summary.Collapsed);
        var road = Assert.Single(root.Elements("road"));
        Assert.Equal(185.0, double.Parse((string)road.Attribute("length")!, CultureInfo.InvariantCulture), 6);
        Assert.Empty(root.Elements("junction"));
        Assert.Equal(5, road.Element("lanes")!.Elements("laneSection").Count());
    }

    [Fact]
    public void AMapWithNothingToCollapseIsReturnedUnchanged()
    {
        string plain = $@"<?xml version=""1.0""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" date="""" north=""0"" south=""0"" east=""0"" west=""0""/>
  {Road("1", 100.0, "-1", Right(Lane("-1")), "", "")}
</OpenDRIVE>";
        RedundantJunctionCollapser.Collapse(plain, out var summary);
        Assert.Equal(0, summary.Collapsed);
    }
}
