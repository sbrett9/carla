// Offline (no engine, no server): the PID has to behave the same way whatever rate its loop is
// actually running at.
//
// The gains are tuned at one period, Constants.PID.DT, and the discretisation used to assume that
// period rather than measure it. Where the loop runs slower -- a free-running server under render
// load, or a control loop queueing behind its own RPCs -- that leaves the integral and derivative
// terms scaled for a tick that never happened and the steering slew budget spent per tick rather
// than per second. Upstream measured the result on Town12: at 19 fps steer_std 0.11, at 11 fps the
// lateral loop diverges into growing weave and the recovery logic then cycles it full-lock in
// place. Ported here from carla-simulator 17068ab73.
#nullable enable

using CarlaNet.TrafficManager;
using CarlaNet.TrafficManager.Stages;
using Xunit;

namespace CarlaNet.Tests.TrafficManager;

public class AdaptiveControlPeriodTests
{
    private const float DT = Constants.PID.DT;
    private static readonly float[] Longitudinal = Constants.PID.LONGITUDINAL_PARAM;
    private static readonly float[] Lateral = Constants.PID.LATERAL_PARAM;

    private static ActuationSignal Step(float dt, float angularDeviation = 0.10f,
                                        float previousAngular = 0.02f,
                                        float velocityDeviation = 0.30f,
                                        float previousVelocity = 0.10f,
                                        float previousSteer = 0.0f)
        => PIDController.RunStep(
            new StateEntry(1.0, angularDeviation, velocityDeviation, 0f),
            new StateEntry(0.0, previousAngular, previousVelocity, previousSteer),
            Longitudinal, Lateral, dt);

    [Fact]
    public void At_the_nominal_period_the_controller_is_unchanged()
    {
        // The pre-change formula, written out. At dt == DT the compensation must vanish
        // identically: gain_scale is exactly 1, and the integral/derivative factors are the
        // constants they always were. This is the regression guard for synchronous runs.
        var present = new StateEntry(1.0, 0.10f, 0.30f, 0f);
        var previous = new StateEntry(0.0, 0.02f, 0.10f, 0.0f);

        float expectedV =
            Longitudinal[0] * present.VelocityDeviation
          + Longitudinal[1] * (present.VelocityDeviation + previous.VelocityDeviation) * DT
          + Longitudinal[2] * (present.VelocityDeviation - previous.VelocityDeviation) * Constants.PID.INV_DT;
        float expectedSteer =
            Lateral[0] * present.AngularDeviation
          + Lateral[1] * (present.AngularDeviation + previous.AngularDeviation) * DT
          + Lateral[2] * (present.AngularDeviation - previous.AngularDeviation) * Constants.PID.INV_DT;

        // Then the old clamps: slew against the previous steer, then the absolute limit.
        expectedSteer = MathF.Max(previous.Steer - Constants.PID.MAX_STEERING_DIFF,
                        MathF.Min(expectedSteer, previous.Steer + Constants.PID.MAX_STEERING_DIFF));
        expectedSteer = MathF.Max(-Constants.PID.MAX_STEERING,
                        MathF.Min(expectedSteer, Constants.PID.MAX_STEERING));

        ActuationSignal a = Step(DT);
        Assert.Equal(MathF.Min(expectedV, Constants.PID.MAX_THROTTLE), a.Throttle);
        // Exactly, not approximately. MAX_STEERING_RATE * DT round-trips to the same float as the
        // old MAX_STEERING_DIFF, and gain_scale is exactly 1, so a synchronous run at
        // fixed_delta_seconds = 0.05 is bit-identical to the behaviour before this change.
        Assert.Equal(expectedSteer, a.Steer);
    }

    [Fact]
    public void A_slower_loop_gets_less_lateral_gain_not_more()
    {
        // Twice the period is half the gain: the loop is slowed to fit inside the sampling delay
        // rather than being left to drive a crossover it can no longer reach in time.
        float atNominal = Step(DT, previousSteer: 1.0f).Steer;
        float atHalfRate = Step(DT * 2f, previousSteer: 1.0f).Steer;
        Assert.True(MathF.Abs(atHalfRate) < MathF.Abs(atNominal),
            $"expected less steering authority at half rate, got {atHalfRate} against {atNominal}");
    }

    [Fact]
    public void A_faster_loop_gets_no_more_gain_than_the_tuning_point()
    {
        // The tuning point is a ceiling. A loop running quicker than nominal must not be handed
        // gains above what it was tuned with.
        float atNominal = Step(DT, previousSteer: 1.0f).Steer;
        float atDoubleRate = Step(DT / 2f, previousSteer: 1.0f).Steer;
        Assert.True(MathF.Abs(atDoubleRate) <= MathF.Abs(atNominal) + 1e-6f,
            $"gains scaled up on a fast tick: {atDoubleRate} against {atNominal}");
    }

    [Fact]
    public void The_steering_slew_budget_is_spent_per_second_not_per_tick()
    {
        // Held hard against the slew limit from a large previous steer, so the clamp is what
        // decides the output. Twice the period should allow twice the movement.
        const float previousSteer = 0.8f;
        float oneTick = previousSteer - Step(DT, angularDeviation: -0.5f, previousAngular: -0.5f,
                                             previousSteer: previousSteer).Steer;
        float twoTicks = previousSteer - Step(DT * 2f, angularDeviation: -0.5f, previousAngular: -0.5f,
                                              previousSteer: previousSteer).Steer;
        Assert.Equal(2.0f, twoTicks / oneTick, 3);
    }

    [Theory]
    [InlineData(0.0f)]        // nothing measured
    [InlineData(-1.0f)]       // time ran backwards
    [InlineData(1000.0f)]     // a hitch long enough to be meaningless
    public void An_implausible_period_is_clamped_rather_than_obeyed(float dt)
    {
        ActuationSignal a = Step(dt);
        Assert.InRange(a.Steer, -Constants.PID.MAX_STEERING, Constants.PID.MAX_STEERING);
        Assert.InRange(a.Throttle, 0f, Constants.PID.MAX_THROTTLE);
        Assert.InRange(a.Brake, 0f, Constants.PID.MAX_BRAKE);
        Assert.False(float.IsNaN(a.Steer) || float.IsInfinity(a.Steer));
    }

    [Fact]
    public void The_clamp_floor_and_ceiling_are_where_the_constants_say()
    {
        Assert.Equal(Step(Constants.PID.MIN_CONTROL_DT).Steer, Step(0.0001f).Steer, 6);
        Assert.Equal(Step(Constants.PID.MAX_CONTROL_DT).Steer, Step(5.0f).Steer, 6);
    }
}
