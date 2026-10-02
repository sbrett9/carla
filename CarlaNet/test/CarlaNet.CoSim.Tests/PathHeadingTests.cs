namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A body's heading from the path its bumper takes: the rear axle follows the bumper, turned only by
/// forward travel, held across a move no vehicle drives, and started from SUMO's angle.
/// </summary>
public sealed class PathHeadingTests
{
    private const double Tick = 0.05;
    private const double RearAxle = 3.5;

    [Fact]
    public void ABodySFirstPoseTakesSumoSAngleAndNoPathVelocity()
    {
        var headings = new PathHeading();
        PathHeading.Step first = headings.Advance("v", 0, 10.0, 20.0, 12.0, Tick, RearAxle, 359.5, restart: false);

        Assert.Equal(359.5, first.HeadingDegrees, 9);
        Assert.Null(first.Velocity);
        Assert.False(first.Held);
    }

    [Fact]
    public void TheStepIsTheTractrixSExactSolution()
    {
        // tan((phi - theta') / 2) = tan((phi - theta) / 2) e^(-d / L), for a bumper moving due east (90)
        // with the body pointing 30 degrees off it.
        double after = PathHeading.Follow(60.0, 90.0, 2.0, RearAxle);
        double expected = 90.0 - (2.0 * Math.Atan(Math.Tan(15.0 * Math.PI / 180.0) * Math.Exp(-2.0 / RearAxle))
                                  * (180.0 / Math.PI));
        Assert.Equal(expected, after, 9);

        // The shorter way round, across north.
        Assert.Equal(PathHeading.Follow(10.0, 40.0, 2.0, RearAxle) - 20.0, PathHeading.Follow(350.0, 20.0, 2.0, RearAxle) % 360.0, 9);
    }

    [Fact]
    public void OnAStraightPathTheHeadingSettlesOnItsDirection()
    {
        var headings = new PathHeading();
        headings.Advance("v", 0, 0.0, 0.0, 10.0, Tick, RearAxle, 10.0, restart: false);
        PathHeading.Step last = default;
        for (int tick = 1; tick <= 60; tick++)
        {
            last = headings.Advance("v", tick, 0.0, 0.5 * tick, 10.0, Tick, RearAxle, 10.0, restart: false);
        }

        // Thirty metres of travel against a 3.5 m rear axle: the offset is e^(-30/3.5) of what it was.
        Assert.InRange(last.HeadingDegrees, 0.0, 10.0 * Math.Exp(-30.0 / RearAxle) * 1.01);
        Assert.Equal(0.0, last.Velocity!.Value.X, 9);
        Assert.Equal(10.0, last.Velocity!.Value.Y, 9);
    }

    [Fact]
    public void NoTickTurnsTheBodyFasterThanItsForwardTravelOverItsRearAxleAllows()
    {
        // Round a 90-degree corner of a polyline at 8 m/s: the path's direction steps by 90 degrees in
        // one tick, and the body turns by no more than d / L radians in any tick.
        var headings = new PathHeading();
        double x = 0.0;
        double y = 0.0;
        double heading = headings.Advance("v", 0, x, y, 8.0, Tick, RearAxle, 0.0, restart: false).HeadingDegrees;
        double worst = 0.0;
        for (int tick = 1; tick <= 200; tick++)
        {
            if (tick <= 50)
            {
                y += 0.4;
            }
            else
            {
                x += 0.4;
            }

            double next = headings.Advance("v", tick, x, y, 8.0, Tick, RearAxle, 0.0, restart: false).HeadingDegrees;
            worst = Math.Max(worst, Math.Abs(Math.IEEERemainder(next - heading, 360.0)));
            heading = next;
        }

        Assert.True(worst <= (0.4 / RearAxle * (180.0 / Math.PI)) + 1e-9, $"turned {worst:0.000} degrees in one tick");
        Assert.Equal(90.0, heading, 3);
    }

    [Fact]
    public void ABodySumoMovesSidewaysWhileStandingSlidesWithoutTurning()
    {
        // The lane-change model moves a vehicle across at 1 m/s at a standstill: 5 cm a tick, no speed.
        var headings = new PathHeading();
        headings.Advance("v", 0, 0.0, 0.0, 0.0, Tick, RearAxle, 0.0, restart: false);
        PathHeading.Step moved = headings.Advance("v", 1, -0.05, 0.0, 0.0, Tick, RearAxle, 0.0, restart: false);

        Assert.Equal(0.0, moved.HeadingDegrees, 9);
        Assert.False(moved.Held);
        Assert.Equal(-1.0, moved.Velocity!.Value.X, 9);
    }

    [Fact]
    public void AMoveNoVehicleDrivesHoldsTheHeadingAndGivesNoPathVelocity()
    {
        // Measured on Arapahoe: SUMO switching a changing vehicle onto a lane that does not run parallel
        // moved it 15.9 m in one step at 14 m/s.
        var headings = new PathHeading();
        headings.Advance("v", 0, 0.0, 0.0, 14.0, Tick, RearAxle, 45.0, restart: false);
        PathHeading.Step jumped = headings.Advance("v", 1, 15.0, 5.0, 14.0, Tick, RearAxle, 45.0, restart: false);

        Assert.True(jumped.Held);
        Assert.Equal(45.0, jumped.HeadingDegrees, 9);
        Assert.Null(jumped.Velocity);
    }

    [Fact]
    public void ADiscontinuityStartsAgainFromSumoSAngleAndForgettingABodyStartsItAfresh()
    {
        var headings = new PathHeading();
        headings.Advance("v", 0, 0.0, 0.0, 10.0, Tick, RearAxle, 0.0, restart: false);
        Assert.Equal(270.0, headings.Advance("v", 1, 0.0, 0.5, 10.0, Tick, RearAxle, 270.0, restart: true).HeadingDegrees, 9);

        headings.Forget("v");
        Assert.Equal(123.0, headings.Advance("v", 2, 0.0, 1.0, 10.0, Tick, RearAxle, 123.0, restart: false).HeadingDegrees, 9);
    }

    [Fact]
    public void ALaneChangeTurnsTheBodyTowardsItsSidewaysMovementAndBack()
    {
        // North at 10 m/s while moving 3.35 m left over 3 s at a steady rate, as SUMO moves it.
        var headings = new PathHeading();
        double lateral = 3.35 / 3.0 * Tick;
        headings.Advance("v", 0, 0.0, 0.0, 10.0, Tick, RearAxle, 0.0, restart: false);
        double most = 0.0;
        PathHeading.Step step = default;
        for (int tick = 1; tick <= 120; tick++)
        {
            double x = -lateral * Math.Min(tick, 60);
            step = headings.Advance("v", tick, x, 0.5 * tick, 10.0, Tick, RearAxle, 0.0, restart: false);
            most = Math.Max(most, 360.0 - step.HeadingDegrees);
        }

        // atan(1.117 / 10) is 6.4 degrees, which the body reaches within a few metres of travel.
        Assert.InRange(most, 6.0, 6.5);
        Assert.True(step.HeadingDegrees < 0.01 || step.HeadingDegrees > 359.99,
                    $"the body still pointed {step.HeadingDegrees:0.000} once the change was over");
    }
}
