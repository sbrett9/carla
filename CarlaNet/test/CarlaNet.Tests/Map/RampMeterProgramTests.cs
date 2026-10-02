// The ramp meters' programmes, and which meters get one.
//
// Offline: the networks here are the parts of a netconvert network the planner reads -- the signal
// programmes and the connections carrying a link index -- written as netconvert writes them. The
// conversion that produces a real one is in RampMeterConversionTests, which needs netconvert.
using System.Xml.Linq;
using CarlaNet.Map;

namespace CarlaNet.Tests.Map;

public class RampMeterProgramTests
{
    // Meter 202 on a two-lane ramp, the way netconvert leaves one kept out of joining: one link per
    // lane, both off the ramp edge. J1 is an ordinary two-road junction.
    private const string Network =
@"<net>
  <tlLogic id=""202"" type=""actuated"" programID=""0"" offset=""0"">
    <phase duration=""80"" state=""GG"" minDur=""10"" maxDur=""50""/>
    <phase duration=""5"" state=""yy""/>
    <phase duration=""5"" state=""rr""/>
  </tlLogic>
  <tlLogic id=""J1"" type=""actuated"" programID=""0"" offset=""0"">
    <phase duration=""37"" state=""GGrr""/>
    <phase duration=""8"" state=""yyrr""/>
    <phase duration=""37"" state=""rrGG""/>
    <phase duration=""8"" state=""rryy""/>
  </tlLogic>
  <connection from=""910#0"" to=""910#1"" fromLane=""0"" toLane=""0"" tl=""202"" linkIndex=""0"" dir=""s"" state=""O""/>
  <connection from=""910#0"" to=""910#1"" fromLane=""1"" toLane=""1"" tl=""202"" linkIndex=""1"" dir=""s"" state=""O""/>
  <connection from=""a"" to=""c"" fromLane=""0"" toLane=""0"" tl=""J1"" linkIndex=""0"" dir=""s"" state=""O""/>
  <connection from=""a"" to=""c"" fromLane=""1"" toLane=""1"" tl=""J1"" linkIndex=""1"" dir=""s"" state=""O""/>
  <connection from=""b"" to=""c"" fromLane=""0"" toLane=""0"" tl=""J1"" linkIndex=""2"" dir=""r"" state=""O""/>
  <connection from=""b"" to=""c"" fromLane=""0"" toLane=""1"" tl=""J1"" linkIndex=""3"" dir=""r"" state=""O""/>
</net>";

    [Fact]
    public void AOneLaneMeterReleasesOneVehicleEverySixSeconds()
    {
        var phases = RampMeterProgram.Phases([0]);

        Assert.Equal([new MeterPhase(2, "s"), new MeterPhase(4, "r")], phases);
    }

    [Fact]
    public void ATwoLaneMeterReleasesItsLanesAlternatelyThreeSecondsApart()
    {
        // Each lane: 2 s green, 4 s red, on a 6 s cycle; together, a vehicle every 3 s.
        var phases = RampMeterProgram.Phases([0, 1]);

        Assert.Equal([new MeterPhase(2, "sr"), new MeterPhase(1, "rr"),
                      new MeterPhase(2, "rs"), new MeterPhase(1, "rr")], phases);
    }

    [Fact]
    public void ALaneWithTwoLinksReleasesThemTogether()
    {
        // A lane that fans out at the meter has a link per outgoing lane; they are one lane's release.
        var phases = RampMeterProgram.Phases([0, 1, 1]);

        Assert.Equal([new MeterPhase(2, "srr"), new MeterPhase(1, "rrr"),
                      new MeterPhase(2, "rss"), new MeterPhase(1, "rrr")], phases);
    }

    [Fact]
    public void MoreLanesExtendTheRotationAndTheCycle()
    {
        var phases = RampMeterProgram.Phases([0, 1, 2]);

        Assert.Equal(9, phases.Sum(p => p.DurationSeconds));
        Assert.Equal(["srr", "rrr", "rsr", "rrr", "rrs", "rrr"], phases.Select(p => p.State));
        Assert.All(phases.Where(p => p.State.Contains('s')), p => Assert.Equal(2, p.DurationSeconds));
    }

    [Fact]
    public void EveryLaneIsRedForAtLeastFourSecondsOfItsCycle()
    {
        foreach (int[] lanes in new[] { new[] { 0 }, new[] { 0, 1 }, new[] { 0, 1, 2 }, new[] { 0, 1, 2, 3 } })
        {
            var phases = RampMeterProgram.Phases(lanes);
            for (int link = 0; link < lanes.Length; link++)
            {
                int red = phases.Where(p => p.State[link] == 'r').Sum(p => p.DurationSeconds);
                int green = phases.Where(p => p.State[link] == 's').Sum(p => p.DurationSeconds);
                Assert.Equal(RampMeterProgram.GreenSeconds, green);
                Assert.True(red >= 4, $"{lanes.Length} lanes: link {link} red for {red} s");
            }
        }
    }

    [Fact]
    public void AMeterOnOneRampIsMeteredAndAJunctionIsNot()
    {
        var plan = RampMeterProgram.Plan(Network, ["202", "J1"]);

        var meter = Assert.Single(plan.Metered);
        Assert.Equal("202", meter.Id);
        Assert.Equal(2, meter.Lanes);
        Assert.Equal(6, meter.CycleSeconds);
        Assert.Equal([("910#0", 0), ("910#0", 1)], meter.Links);
        var (id, reason) = Assert.Single(plan.NotMetered);
        Assert.Equal("J1", id);
        Assert.Contains("2 approaches", reason);
    }

    [Fact]
    public void AMeterTheNetworkDoesNotHaveIsReportedNotMetered()
    {
        var plan = RampMeterProgram.Plan(Network, ["999"]);

        Assert.Empty(plan.Metered);
        Assert.Equal("", plan.ProgramFile);
        Assert.Contains("no signal of that id", Assert.Single(plan.NotMetered).Reason);
    }

    [Fact]
    public void AMeterThatAlsoControlsACrossingIsNotMetered()
    {
        string network = Network.Replace(
            "</net>",
            @"<connection from="":202_w0"" to="":202_c0"" fromLane=""0"" toLane=""0"" tl=""202"" linkIndex=""2"" dir=""s"" state=""O""/></net>")
            .Replace(@"state=""GG"" minDur", @"state=""GGr"" minDur")
            .Replace(@"state=""yy""/>", @"state=""yyr""/>")
            .Replace(@"state=""rr""/>", @"state=""rrr""/>");

        var plan = RampMeterProgram.Plan(network, ["202"]);

        Assert.Empty(plan.Metered);
        Assert.Contains("pedestrian crossing", Assert.Single(plan.NotMetered).Reason);
    }

    [Fact]
    public void TheProgrammeFileReplacesTheMetersProgrammeAsAStaticCycle()
    {
        var plan = RampMeterProgram.Plan(Network, ["202"]);
        var logic = Assert.Single(XDocument.Parse(plan.ProgramFile).Root!.Elements("tlLogic"));

        Assert.Equal("202", logic.Attribute("id")?.Value);
        Assert.Equal("static", logic.Attribute("type")?.Value);
        // The programme it replaces, so netconvert swaps it rather than adding a second one.
        Assert.Equal("0", logic.Attribute("programID")?.Value);
        Assert.Equal(["2:sr", "1:rr", "2:rs", "1:rr"],
                     logic.Elements("phase").Select(p => $"{p.Attribute("duration")?.Value}:{p.Attribute("state")?.Value}"));
        Assert.DoesNotContain("\r", plan.ProgramFile);
    }

    [Fact]
    public void ANetworkCarryingTheProgrammeVerifies()
    {
        var plan = RampMeterProgram.Plan(Network, ["202"]);
        string built = Network.Replace(
            @"<tlLogic id=""202"" type=""actuated"" programID=""0"" offset=""0"">
    <phase duration=""80"" state=""GG"" minDur=""10"" maxDur=""50""/>
    <phase duration=""5"" state=""yy""/>
    <phase duration=""5"" state=""rr""/>",
            @"<tlLogic id=""202"" type=""static"" programID=""0"" offset=""0"">
    <phase duration=""2"" state=""sr""/>
    <phase duration=""1"" state=""rr""/>
    <phase duration=""2"" state=""rs""/>
    <phase duration=""1"" state=""rr""/>");

        Assert.Empty(RampMeterProgram.Verify(built, plan));
    }

    [Fact]
    public void ANetworkStillCarryingTheGuessedProgrammeFailsVerification()
    {
        var plan = RampMeterProgram.Plan(Network, ["202"]);

        var problems = RampMeterProgram.Verify(Network, plan);

        Assert.Contains(problems, p => p.Contains("actuated"));
        Assert.Contains(problems, p => p.Contains("phases"));
    }

    [Fact]
    public void ANetworkWhoseMeterLinksMovedFailsVerification()
    {
        var plan = RampMeterProgram.Plan(Network, ["202"]);
        string moved = Network.Replace(@"from=""910#0"" to=""910#1"" fromLane=""1""", @"from=""910#0"" to=""910#1"" fromLane=""0""");

        Assert.Contains(RampMeterProgram.Verify(moved, plan), p => p.Contains("links leave"));
    }
}
