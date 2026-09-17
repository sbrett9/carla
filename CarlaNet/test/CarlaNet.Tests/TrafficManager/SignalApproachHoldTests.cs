// Offline (no engine, no server): a vehicle stopping for a red light must keep stopping for it
// after the light stops being visible to the waypoint it is standing on.
//
// The road graph records, per waypoint, the next signal a vehicle in that lane will REACH — so a
// waypoint at or past a stop line names no signal at all, which SignalVisibilityTests fixes as the
// map's correct behaviour. The approach decision then has to distinguish "no light applies here"
// from "the light I am stopping for is now behind my leading waypoint". It did not, and released
// the hold at the stop line; because junction commitment is granted to any vehicle nothing is
// holding back, the release immediately handed the vehicle a commitment that exempted it from the
// light. Measured on a hundred-vehicle run: 69 of 137 approach holds ended inside three seconds
// against a twenty-second signal cycle, and 43 of the 46 commitments granted against a red or
// yellow followed a release within three seconds.
#nullable enable

using CarlaNet.TrafficManager.Stages;
using Xunit;

namespace CarlaNet.Tests.TrafficManager;

public class SignalApproachHoldTests
{
    private const string Approaching = "sig_A";
    private const string Elsewhere = "sig_B";

    [Fact]
    public void The_signal_the_leading_waypoint_names_governs_the_approach()
    {
        Assert.Equal(Approaching, TrafficLightStage.GoverningSignalForApproach(
            namedByBufferHead: Approaching, alreadyHeldFor: null, headingIntoJunction: true));
    }

    [Fact]
    public void A_vehicle_at_the_stop_line_keeps_the_signal_it_is_being_held_for()
    {
        // The leading waypoint has passed the stop line and so names nothing. The vehicle is still
        // pointed into the junction, and the light has not turned green — it must keep stopping.
        Assert.Equal(Approaching, TrafficLightStage.GoverningSignalForApproach(
            namedByBufferHead: null, alreadyHeldFor: Approaching, headingIntoJunction: true));
    }

    [Fact]
    public void A_vehicle_rerouted_away_from_the_approach_is_not_held_by_its_old_signal()
    {
        // No junction ahead any more: the vehicle turned off before reaching this one. Keeping the
        // hold here would park it on an open road waiting for a light it no longer faces.
        Assert.Null(TrafficLightStage.GoverningSignalForApproach(
            namedByBufferHead: null, alreadyHeldFor: Approaching, headingIntoJunction: false));
    }

    [Fact]
    public void A_new_approach_supersedes_the_signal_still_recorded_against_the_vehicle()
    {
        // Reaching a second junction while the first is still recorded must hand the vehicle the
        // signal in front of it, so the braking distance is decided afresh for the new approach.
        Assert.Equal(Elsewhere, TrafficLightStage.GoverningSignalForApproach(
            namedByBufferHead: Elsewhere, alreadyHeldFor: Approaching, headingIntoJunction: true));
    }

    [Theory]
    // Still short of the line: the leading waypoint names the very signal the vehicle is held
    // for, so the position test must not be trusted however near the boundary it has snapped.
    [InlineData(Approaching, Approaching, true)]
    // Past the line, or approaching a different signal: the position test means what it says.
    [InlineData(null, Approaching, false)]
    [InlineData(Elsewhere, Approaching, false)]
    // Not held for anything, so there is no approach for the position test to be wrong about.
    [InlineData(Approaching, null, false)]
    [InlineData(null, null, false)]
    public void A_vehicle_short_of_its_own_stop_line_has_not_entered_the_junction(
        string? namedByBufferHead, string? alreadyHeldFor, bool expected)
    {
        Assert.Equal(expected, TrafficLightStage.IsShortOfItsSignal(namedByBufferHead, alreadyHeldFor));
    }

    [Fact]
    public void A_vehicle_holding_for_nothing_is_governed_by_nothing()
    {
        Assert.Null(TrafficLightStage.GoverningSignalForApproach(
            namedByBufferHead: null, alreadyHeldFor: null, headingIntoJunction: true));
        Assert.Null(TrafficLightStage.GoverningSignalForApproach(
            namedByBufferHead: null, alreadyHeldFor: null, headingIntoJunction: false));
    }
}
