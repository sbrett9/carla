// Offline tests for CarlaNet.Map.OpenDrive.TrafficLightInjector.
//
// A synthetic .xodr for one signalized junction (four heads J1_0..J1_3, one netconvert-style
// all-heads <controller>, and NO <junction><controller> link) plus a SUMO net with a two-phase
// <tlLogic> exercises the whole rewrite: split into per-phase controllers, add the junction links,
// and drop the all-heads controller. This is the shape netconvert actually emits, so it catches
// both the grouping logic and the XML-surgery pitfalls (e.g. inserting relative to a removed node).
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;

namespace CarlaNet.Tests.Map;

public class TrafficLightInjectorTests
{
    // One road carrying the four heads, a netconvert all-heads controller id="J1", and a junction
    // named "J1" with no controller link — exactly what breaks CARLA's grouping (issue #1).
    private const string Xodr =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" vendor=""test""/>
  <road name=""r1"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
    <lanes>
      <laneSection s=""0.0"">
        <center><lane id=""0"" type=""driving"" level=""false""/></center>
      </laneSection>
    </lanes>
    <signals>
      <signal id=""J1_0"" s=""90"" t=""-5"" type=""1000001"" dynamic=""yes""><validity fromLane=""-2"" toLane=""-2""/></signal>
      <signal id=""J1_1"" s=""90"" t=""-2"" type=""1000001"" dynamic=""yes""><validity fromLane=""-1"" toLane=""-1""/></signal>
      <signal id=""J1_2"" s=""95"" t=""-5"" type=""1000001"" dynamic=""yes""><validity fromLane=""-2"" toLane=""-2""/></signal>
      <signal id=""J1_3"" s=""95"" t=""-2"" type=""1000001"" dynamic=""yes""><validity fromLane=""-1"" toLane=""-1""/></signal>
    </signals>
  </road>
  <controller id=""J1"">
    <control signalId=""J1_0""/>
    <control signalId=""J1_1""/>
    <control signalId=""J1_2""/>
    <control signalId=""J1_3""/>
  </controller>
  <junction name=""J1"" id=""5"">
    <connection id=""0"" incomingRoad=""1"" connectingRoad=""1"" contactPoint=""start""/>
  </junction>
</OpenDRIVE>";

    // Two opposing green phases: heads 0,1 together, then heads 2,3 together.
    private const string NetXml =
@"<net>
  <tlLogic id=""J1"" type=""static"" programID=""0"">
    <phase duration=""30"" state=""GGrr""/>
    <phase duration=""3""  state=""yyrr""/>
    <phase duration=""30"" state=""rrGG""/>
    <phase duration=""3""  state=""rryy""/>
  </tlLogic>
</net>";

    [Fact]
    public void Inject_TwoPhaseJunction_SplitsControllersAndLinksJunction()
    {
        string outXodr = TrafficLightInjector.InjectTrafficLights(Xodr, NetXml);

        var root = XDocument.Parse(outXodr).Root!;

        // The netconvert all-heads controller is gone; two per-phase controllers replace it.
        Assert.Null(root.Elements("controller").FirstOrDefault(c => c.Attribute("id")!.Value == "J1"));
        var controllers = root.Elements("controller").ToList();
        Assert.Equal(2, controllers.Count);

        var p0 = controllers.Single(c => c.Attribute("id")!.Value == "J1_p0");
        var p1 = controllers.Single(c => c.Attribute("id")!.Value == "J1_p1");
        // One pole per approach: only the roadside-most head (larger |t|) of each approach survives,
        // and each phase-controller drives its approach's single pole.
        Assert.Equal(new[] { "J1_0" }, p0.Elements("control").Select(x => x.Attribute("signalId")!.Value));
        Assert.Equal(new[] { "J1_2" }, p1.Elements("control").Select(x => x.Attribute("signalId")!.Value));
        var remainingSignals = root.Elements("road").Elements("signals").Elements("signal")
            .Select(s => s.Attribute("id")!.Value).ToHashSet();
        Assert.Equal(new HashSet<string> { "J1_0", "J1_2" }, remainingSignals);

        // The junction now links BOTH phase controllers (was zero links — the root of #1).
        var junction = root.Elements("junction").Single(j => j.Attribute("name")!.Value == "J1");
        var links = junction.Elements("controller").Select(c => c.Attribute("id")!.Value).ToList();
        Assert.Equal(new[] { "J1_p0", "J1_p1" }, links);
    }

    // Collapsing an approach's heads to one pole must not shrink the lanes the light governs: CARLA
    // builds a stop-line trigger box per lane in <validity>, so a lane whose head was dropped without
    // its validity being merged onto the survivor gets no trigger box, and traffic in that lane drives
    // through the light.
    [Fact]
    public void Inject_CollapsingHeads_KeepsValidityOverEveryLaneOfTheApproach()
    {
        var root = XDocument.Parse(TrafficLightInjector.InjectTrafficLights(Xodr, NetXml)).Root!;

        var signals = root.Elements("road").Elements("signals").Elements("signal")
            .ToDictionary(s => s.Attribute("id")!.Value);
        Assert.Equal(new HashSet<string> { "J1_0", "J1_2" }, signals.Keys.ToHashSet());

        // Each approach had heads over lanes -1 and -2; the surviving pole must cover both.
        foreach (var (id, signal) in signals)
        {
            var validity = signal.Element("validity");
            Assert.NotNull(validity);
            var lanes = new[] { int.Parse(validity!.Attribute("fromLane")!.Value),
                                int.Parse(validity.Attribute("toLane")!.Value) };
            Assert.Equal(-2, lanes.Min());
            Assert.Equal(-1, lanes.Max());
        }
    }

    [Fact]
    public void Inject_NoTlLogic_ReturnsInputUnchanged()
    {
        Assert.Equal(Xodr, TrafficLightInjector.InjectTrafficLights(Xodr, "<net/>"));
    }

    // A graded junction: the approach (road 1) sits at 10 m, the connecting road it crosses to reach
    // the far side (road 2) at 25 m. The pole is relocated ~27 m forward, so it stands on road 2's
    // ground, not road 1's.
    private const string GradedXodr =
@"<?xml version=""1.0"" standalone=""yes""?>
<OpenDRIVE>
  <header revMajor=""1"" revMinor=""4"" name="""" version=""1.00"" vendor=""test""/>
  <road name=""approach"" length=""100.0"" id=""1"" junction=""-1"">
    <planView>
      <geometry s=""0.0"" x=""0.0"" y=""0.0"" hdg=""0.0"" length=""100.0""><line/></geometry>
    </planView>
    <elevationProfile><elevation s=""0.0"" a=""10.0"" b=""0.0"" c=""0.0"" d=""0.0""/></elevationProfile>
    <lanes>
      <laneSection s=""0.0"">
        <center><lane id=""0"" type=""driving"" level=""false""/></center>
      </laneSection>
    </lanes>
    <signals>
      <signal id=""J1_0"" s=""95"" t=""-5"" type=""1000001"" dynamic=""yes""><validity fromLane=""-2"" toLane=""-2""/></signal>
      <signal id=""J1_1"" s=""95"" t=""-2"" type=""1000001"" dynamic=""yes""><validity fromLane=""-1"" toLane=""-1""/></signal>
    </signals>
  </road>
  <road name=""crossing"" length=""20.0"" id=""2"" junction=""5"">
    <planView>
      <geometry s=""0.0"" x=""100.0"" y=""0.0"" hdg=""0.0"" length=""20.0""><line/></geometry>
    </planView>
    <elevationProfile><elevation s=""0.0"" a=""25.0"" b=""0.0"" c=""0.0"" d=""0.0""/></elevationProfile>
    <lanes>
      <laneSection s=""0.0"">
        <center><lane id=""0"" type=""driving"" level=""false""/></center>
      </laneSection>
    </lanes>
  </road>
  <controller id=""J1"">
    <control signalId=""J1_0""/>
    <control signalId=""J1_1""/>
  </controller>
  <junction name=""J1"" id=""5"">
    <connection id=""0"" incomingRoad=""1"" connectingRoad=""2"" contactPoint=""start""/>
  </junction>
</OpenDRIVE>";

    private const string GradedNetXml =
@"<net>
  <tlLogic id=""J1"" type=""static"" programID=""0"">
    <phase duration=""30"" state=""GG""/>
    <phase duration=""3""  state=""yy""/>
  </tlLogic>
</net>";

    private static double PoleZ(string xodr, string netXml)
    {
        var root = XDocument.Parse(TrafficLightInjector.InjectTrafficLights(xodr, netXml)).Root!;
        var inertial = root.Elements("road").Elements("signals").Elements("signal")
            .Elements("positionInertial").Single();
        return double.Parse(inertial.Attribute("z")!.Value, CultureInfo.InvariantCulture);
    }

    // The pole is moved metres across the junction, so it must take the elevation of the ground it
    // ends up on. Carrying the stop line's elevation sinks the mast into a rising far side, and with
    // it the clearance a tall vehicle needs to pass under the arm.
    [Fact]
    public void Inject_FarSidePole_TakesTheElevationWhereItLands()
    {
        Assert.Equal(25.0, PoleZ(GradedXodr, GradedNetXml), precision: 3);
    }

    // The parser gives a road with no <elevationProfile> a default zero elevation record, so a
    // resample that trusted the parsed model would read "no data" as "at the datum" and drop the pole
    // tens of metres below an elevated map. Such a road must be left out of the search entirely.
    [Fact]
    public void Inject_FarSideRoadWithoutElevation_KeepsTheStopLineElevation()
    {
        string xodr = XDocument.Parse(GradedXodr) is var doc && doc.Root != null
            ? RemoveElevationOfRoad(doc, "2")
            : GradedXodr;

        Assert.Equal(10.0, PoleZ(xodr, GradedNetXml), precision: 3);
    }

    private static string RemoveElevationOfRoad(XDocument doc, string roadId)
    {
        doc.Root!.Elements("road").Single(r => r.Attribute("id")!.Value == roadId)
            .Elements("elevationProfile").Remove();
        return doc.ToString();
    }

    // netconvert's clustered junction ids ("cluster_<id>_<id>..._#Nmore") produce signal ids that
    // exceed CARLA's 32-char SignId limit; they must be aliased down, with all references rewritten.
    [Fact]
    public void Inject_LongClusterId_AliasesEverythingUnder32Chars()
    {
        const string longId = "cluster_4757430239_4757430240_4757430241_4757430243_#1more"; // 57 chars
        string xodr =
$@"<OpenDRIVE>
  <header/>
  <road id=""1"" junction=""-1"">
    <signals>
      <signal id=""{longId}_0"" type=""1000001""/>
      <signal id=""{longId}_1"" type=""1000001""/>
    </signals>
  </road>
  <controller id=""{longId}"">
    <control signalId=""{longId}_0""/>
    <control signalId=""{longId}_1""/>
  </controller>
  <junction name=""{longId}"" id=""7""><connection id=""0""/></junction>
</OpenDRIVE>";
        string net =
$@"<net><tlLogic id=""{longId}"" type=""static"">
  <phase duration=""30"" state=""GG""/><phase duration=""3"" state=""yy""/>
</tlLogic></net>";

        var root = XDocument.Parse(TrafficLightInjector.InjectTrafficLights(xodr, net)).Root!;

        var signalIds = root.Elements("road").Elements("signals").Elements("signal")
            .Select(s => s.Attribute("id")!.Value).ToHashSet();
        Assert.All(signalIds, id => Assert.True(id.Length <= 32, $"signal id too long: {id}"));
        Assert.All(root.Elements("controller"),
            c => Assert.True(c.Attribute("id")!.Value.Length <= 32, $"controller id too long: {c.Attribute("id")!.Value}"));

        // Every control reference still resolves to a (renamed) signal, and the junction is linked.
        Assert.All(root.Elements("controller").Elements("control"),
            ctl => Assert.Contains(ctl.Attribute("signalId")!.Value, signalIds));
        Assert.NotEmpty(root.Elements("junction").Single().Elements("controller"));
    }
}
