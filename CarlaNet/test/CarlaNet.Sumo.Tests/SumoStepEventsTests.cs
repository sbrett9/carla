using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// One step's events: counted across every list, and described with the clock they go with.
/// </summary>
public sealed class SumoStepEventsTests
{
    [Fact]
    public void AStepInWhichNothingHappenedHasNothingInAnyList()
    {
        SumoStepEvents none = SumoStepEvents.None(12.5);

        Assert.Equal(12.5, none.TimeSeconds);
        Assert.Equal(0, none.Count);
        Assert.Equal("t=12.5 s: nothing", none.ToString());
    }

    [Fact]
    public void EveryListIsCountedAndNamed()
    {
        var events = new SumoStepEvents(
            15.0,
            Departed: ["a"],
            Arrived: ["b", "c"],
            StopsStarted: ["d"],
            StopsEnded: ["e"],
            ParkingStarted: ["d"],
            ParkingEnded: ["f"],
            TeleportsStarted: ["g"],
            EmergencyStops: ["h"]);

        Assert.Equal(9, events.Count);
        Assert.Equal(
            "t=15 s: departed 'a'; arrived 'b', 'c'; stop started 'd'; stop ended 'e'; parking started 'd'; "
            + "parking ended 'f'; teleport started 'g'; emergency stop 'h'",
            events.ToString());
    }

    /// <summary>
    /// The step's variables are whole-population values the step delivers once: none of them is one a
    /// generic decode would misread, which is what lets them be subscribed at all.
    /// </summary>
    [Fact]
    public void EveryStepVariableIsReadableFromItsTypeByteAlone()
    {
        Assert.All(SumoSimulationSubscription.StepVariables, variable =>
            Assert.True(TraCIVariables.IsDecodable(TraCIConstants.CMD_GET_SIM_VARIABLE, variable),
                        $"simulation variable 0x{variable:x2} needs a decoder of its own"));
        Assert.Equal(SumoSimulationSubscription.StepVariables.Length,
                     SumoSimulationSubscription.StepVariables.Distinct().Count());
        Assert.Contains(TraCIConstants.VAR_TIME, SumoSimulationSubscription.StepVariables);
    }
}
