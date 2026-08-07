using CarlaNet.Scenario;

namespace CarlaNet.Tests.Scenario;

/// <summary>
/// Covers the OpenSCENARIO subset an authored pattern uses, against storyboards shaped the way the
/// authoring tool emits them: one act per authored phase, timing carried on the act's start trigger,
/// and the events inside firing immediately.
/// </summary>
public class OpenScenarioParserTests
{
    /// A drive / stop / resume storyboard in the emitted shape. The third phase waits on the entity
    /// standing still, which is how a dwell of any length is expressed.
    private const string DriveStopResume = """
        <?xml version="1.0" encoding="UTF-8"?>
        <OpenSCENARIO>
          <FileHeader revMajor="1" revMinor="1" date="2026-01-01T00:00:00" description="test" author="test"/>
          <RoadNetwork><LogicFile filepath="world.xodr"/></RoadNetwork>
          <Entities>
            <ScenarioObject name="Vehicle1">
              <Vehicle name="car_white" vehicleCategory="car">
                <Properties>
                  <Property name="drawtonomy:template" value="sedan"/>
                  <Property name="drawtonomy:color" value="black"/>
                </Properties>
              </Vehicle>
            </ScenarioObject>
          </Entities>
          <Storyboard>
            <Init><Actions>
              <Private entityRef="Vehicle1">
                <PrivateAction><TeleportAction><Position>
                  <LanePosition roadId="243" laneId="-1" offset="0" s="70"/>
                </Position></TeleportAction></PrivateAction>
              </Private>
            </Actions></Init>
            <Story name="Phase1Story"><Act name="Phase1Act">
              <ManeuverGroup maximumExecutionCount="1" name="g1">
                <Actors selectTriggeringEntities="false"><EntityRef entityRef="Vehicle1"/></Actors>
                <Maneuver name="m1"><Event name="Event_1" priority="overwrite">
                  <Action name="a1"><PrivateAction><LongitudinalAction><SpeedAction>
                    <SpeedActionDynamics dynamicsShape="linear" value="2" dynamicsDimension="time"/>
                    <SpeedActionTarget><AbsoluteTargetSpeed value="8.333333"/></SpeedActionTarget>
                  </SpeedAction></LongitudinalAction></PrivateAction></Action>
                  <StartTrigger><ConditionGroup><Condition name="c" delay="0" conditionEdge="none">
                    <ByValueCondition><SimulationTimeCondition value="0" rule="greaterThan"/></ByValueCondition>
                  </Condition></ConditionGroup></StartTrigger>
                </Event></Maneuver>
              </ManeuverGroup>
              <StartTrigger><ConditionGroup><Condition name="s1" delay="0" conditionEdge="none">
                <ByValueCondition><SimulationTimeCondition value="0" rule="greaterThan"/></ByValueCondition>
              </Condition></ConditionGroup></StartTrigger>
            </Act></Story>
            <Story name="Phase2Story"><Act name="Phase2Act">
              <ManeuverGroup maximumExecutionCount="1" name="g2">
                <Actors selectTriggeringEntities="false"><EntityRef entityRef="Vehicle1"/></Actors>
                <Maneuver name="m2"><Event name="Event_2" priority="overwrite">
                  <Action name="a2"><PrivateAction><LongitudinalAction><SpeedAction>
                    <SpeedActionDynamics dynamicsShape="linear" value="2" dynamicsDimension="time"/>
                    <SpeedActionTarget><AbsoluteTargetSpeed value="0"/></SpeedActionTarget>
                  </SpeedAction></LongitudinalAction></PrivateAction></Action>
                </Event></Maneuver>
              </ManeuverGroup>
              <StartTrigger><ConditionGroup><Condition name="s2" delay="0" conditionEdge="rising">
                <ByValueCondition><SimulationTimeCondition value="5" rule="greaterThan"/></ByValueCondition>
              </Condition></ConditionGroup></StartTrigger>
            </Act></Story>
            <Story name="Phase3Story"><Act name="Phase3Act">
              <ManeuverGroup maximumExecutionCount="1" name="g3">
                <Actors selectTriggeringEntities="false"><EntityRef entityRef="Vehicle1"/></Actors>
                <Maneuver name="m3"><Event name="Event_3" priority="overwrite">
                  <Action name="a3"><PrivateAction><LongitudinalAction><SpeedAction>
                    <SpeedActionDynamics dynamicsShape="linear" value="2" dynamicsDimension="time"/>
                    <SpeedActionTarget><AbsoluteTargetSpeed value="8.333333"/></SpeedActionTarget>
                  </SpeedAction></LongitudinalAction></PrivateAction></Action>
                </Event></Maneuver>
              </ManeuverGroup>
              <StartTrigger><ConditionGroup><Condition name="s3" delay="0" conditionEdge="none">
                <ByEntityCondition>
                  <TriggeringEntities triggeringEntitiesRule="any"><EntityRef entityRef="Vehicle1"/></TriggeringEntities>
                  <EntityCondition><StandStillCondition duration="2700"/></EntityCondition>
                </ByEntityCondition>
              </Condition></ConditionGroup></StartTrigger>
            </Act></Story>
            <StopTrigger><ConditionGroup><Condition name="stop" delay="0" conditionEdge="rising">
              <ByValueCondition><SimulationTimeCondition value="3600" rule="greaterThan"/></ByValueCondition>
            </Condition></ConditionGroup></StopTrigger>
          </Storyboard>
        </OpenSCENARIO>
        """;

    [Fact]
    public void ReadsEntityPlacementAndAppearance()
    {
        var s = OpenScenarioParser.Load(DriveStopResume);

        Assert.Equal("1.1", s.Version);
        Assert.Equal("world.xodr", s.RoadNetworkFile);
        ScenarioEntity e = Assert.Single(s.Entities);
        Assert.Equal("Vehicle1", e.Name);
        Assert.Equal("car", e.Category);
        Assert.Equal("sedan", e.TemplateHint);
        Assert.Equal("black", e.Colour);
        Assert.Equal(new LanePosition(243, -1, 70.0, 0.0), e.InitialPosition);
    }

    [Fact]
    public void CarriesTimingOnActTriggersAndFiresInnerEventsImmediately()
    {
        var s = OpenScenarioParser.Load(DriveStopResume);
        Assert.Equal(3, s.Acts.Count);

        // A threshold of zero means "as soon as the act is active", not a delay of one evaluation.
        Assert.Equal(TriggerKind.Immediately, s.Acts[0].StartTrigger.Kind);
        Assert.Equal(TriggerKind.SimulationTime, s.Acts[1].StartTrigger.Kind);
        Assert.Equal(5.0, s.Acts[1].StartTrigger.Value);

        foreach (var act in s.Acts)
            foreach (var ev in act.Events)
                Assert.Equal(TriggerKind.Immediately, ev.StartTrigger.Kind);
    }

    [Fact]
    public void ReadsADwellAsAStandStillDuration()
    {
        var s = OpenScenarioParser.Load(DriveStopResume);
        ScenarioTrigger t = s.Acts[2].StartTrigger;

        Assert.Equal(TriggerKind.StandStill, t.Kind);
        Assert.Equal(2700.0, t.Value);
        Assert.Equal("Vehicle1", t.EntityRef);
    }

    [Fact]
    public void ReadsSpeedTargetsAndTransitionTimes()
    {
        var s = OpenScenarioParser.Load(DriveStopResume);

        var drive = Assert.IsType<SpeedAction>(Assert.Single(s.Acts[0].Events[0].Actions));
        Assert.Equal(8.333333, drive.TargetSpeedMps, 6);
        Assert.Equal(2.0, drive.TransitionSeconds);

        var stop = Assert.IsType<SpeedAction>(Assert.Single(s.Acts[1].Events[0].Actions));
        Assert.Equal(0.0, stop.TargetSpeedMps);
    }

    [Fact]
    public void ReadsTheStoryboardStopTrigger()
    {
        var s = OpenScenarioParser.Load(DriveStopResume);
        Assert.NotNull(s.StopTrigger);
        Assert.Equal(TriggerKind.SimulationTime, s.StopTrigger!.Kind);
        Assert.Equal(3600.0, s.StopTrigger.Value);
    }

    // A scenario that quietly loses a construct executes as something other than what was authored, and
    // the discrepancy would surface much later as unexplained behaviour in training data. Each of these
    // asserts the parser refuses rather than ignores.

    [Fact]
    public void RefusesAPositionItCannotResolve()
    {
        string xml = DriveStopResume.Replace(
            """<LanePosition roadId="243" laneId="-1" offset="0" s="70"/>""",
            """<WorldPosition x="10" y="20" z="0" h="0"/>""");
        var ex = Assert.Throws<ScenarioParseException>(() => OpenScenarioParser.Load(xml));
        Assert.Contains("WorldPosition", ex.Message);
    }

    [Fact]
    public void RefusesATriggerItCannotEvaluate()
    {
        string xml = DriveStopResume.Replace(
            """<SimulationTimeCondition value="5" rule="greaterThan"/>""",
            """<TimeHeadwayCondition entityRef="Vehicle1" value="2" rule="lessThan" freespace="false" alongRoute="true"/>""");
        var ex = Assert.Throws<ScenarioParseException>(() => OpenScenarioParser.Load(xml));
        Assert.Contains("TimeHeadwayCondition", ex.Message);
    }

    [Fact]
    public void RefusesADynamicsDimensionItCannotConvert()
    {
        string xml = DriveStopResume.Replace(
            """dynamicsShape="linear" value="2" dynamicsDimension="time" """.TrimEnd(),
            """dynamicsShape="linear" value="2" dynamicsDimension="distance" """.TrimEnd());
        var ex = Assert.Throws<ScenarioParseException>(() => OpenScenarioParser.Load(xml));
        Assert.Contains("distance", ex.Message);
    }

    [Fact]
    public void RefusesAMalformedDocument()
        => Assert.Throws<ScenarioParseException>(() => OpenScenarioParser.Load("<OpenSCENARIO><oops"));

    [Fact]
    public void RefusesADocumentThatIsNotAScenario()
        => Assert.Throws<ScenarioParseException>(() => OpenScenarioParser.Load("<OpenDRIVE/>"));
}
