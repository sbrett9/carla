// Source: carla/trafficmanager/PIDController.h
//
// Pure functional port of the PID controller used by <see cref="MotionPlanStage"/>.
// One static <see cref="RunStep"/> method runs both the longitudinal
// (velocity → throttle/brake) and lateral (angular deviation → steer) loops.
//
// State management lives in the caller (MotionPlanStage maintains a
// per-actor <c>StateEntry pid_state_map</c>). The controller itself is
// stateless — it consumes <c>(present_state, previous_state)</c> and emits
// a single <see cref="ActuationSignal"/>.
//
// Clamping rules (mirroring upstream PIDController.h):
//   throttle ∈ [0, MAX_THROTTLE]
//   brake    ∈ [0, MAX_BRAKE]
//   steer    ∈ [-MAX_STEERING, MAX_STEERING]
//   |Δsteer| ≤ MAX_STEERING_RATE * dt  (slew-rate limit, per second of sim time)
//
// All constants live in <see cref="Constants.PID"/>.
#nullable enable

namespace CarlaNet.TrafficManager.Stages;

/// <summary>
/// Stateless PID controller. One <see cref="RunStep"/> call produces the
/// per-tick actuation signal from the previous + current PID error state.
/// </summary>
internal static class PIDController
{
    private const float DT = Constants.PID.DT;
    private const float INV_DT = Constants.PID.INV_DT;
    private const float MAX_THROTTLE = Constants.PID.MAX_THROTTLE;
    private const float MAX_BRAKE = Constants.PID.MAX_BRAKE;
    private const float MAX_STEERING = Constants.PID.MAX_STEERING;
    private const float MAX_STEERING_RATE = Constants.PID.MAX_STEERING_RATE;
    private const float MIN_CONTROL_DT = Constants.PID.MIN_CONTROL_DT;
    private const float MAX_CONTROL_DT = Constants.PID.MAX_CONTROL_DT;

    /// <summary>
    /// Compute the actuation signal that minimises the PID error between
    /// <paramref name="presentState"/> and <paramref name="previousState"/>.
    /// Mirrors <c>PID::RunStep</c> in PIDController.h:27–59 byte-for-byte.
    /// </summary>
    /// <param name="presentState">Current PID error snapshot.</param>
    /// <param name="previousState">Previous tick's PID error snapshot.</param>
    /// <param name="longitudinalParameters">{Kp, Ki, Kd} for velocity loop.</param>
    /// <param name="lateralParameters">{Kp, Ki, Kd} for steering loop.</param>
    /// <param name="controlDt">
    /// Measured simulation-time period between the two states. The gains are tuned at the nominal
    /// <see cref="Constants.PID.DT"/>; where the loop actually runs slower, leaving the
    /// discretisation fixed halves the phase margin and the lateral loop goes from damped to
    /// divergent -- the vehicle weaves, then the recovery logic cycles it full-lock in place.
    /// </param>
    public static ActuationSignal RunStep(
        StateEntry presentState,
        StateEntry previousState,
        ReadOnlySpan<float> longitudinalParameters,
        ReadOnlySpan<float> lateralParameters,
        float controlDt = DT)
    {
        float dt = MathF.Max(MIN_CONTROL_DT, MathF.Min(controlDt, MAX_CONTROL_DT));
        float invDt = 1.0f / dt;
        // Sample-rate compensation for the lateral loop. The dominant effect of a longer control
        // period is added loop delay: the crossover frequency the gains were tuned for no longer
        // fits inside the slower sampling, so the loop has to be slowed proportionally. Gains are
        // never scaled UP on fast ticks -- the tuning point is the ceiling.
        float gainScale = MathF.Min(1.0f, DT * invDt);

        // ── Longitudinal PID: throttle / brake ────────────────────────────
        // The speed loop is first-order and far less delay-sensitive than the lateral one, so only
        // the integral and derivative discretisation uses the measured period; Kp is untouched.
        float exprV =
            longitudinalParameters[0] * presentState.VelocityDeviation
          + longitudinalParameters[1] * (presentState.VelocityDeviation + previousState.VelocityDeviation) * dt
          + longitudinalParameters[2] * (presentState.VelocityDeviation - previousState.VelocityDeviation) * invDt;

        float throttle;
        float brake;
        if (exprV > 0.0f)
        {
            throttle = MathF.Min(exprV, MAX_THROTTLE);
            brake = 0.0f;
        }
        else
        {
            throttle = 0.0f;
            brake = MathF.Min(MathF.Abs(exprV), MAX_BRAKE);
        }

        // ── Lateral PID: steer ─────────────────────────────────────────────
        float steer = gainScale * (
            lateralParameters[0] * presentState.AngularDeviation
          + lateralParameters[1] * (presentState.AngularDeviation + previousState.AngularDeviation) * dt
          + lateralParameters[2] * (presentState.AngularDeviation - previousState.AngularDeviation) * invDt);

        // Slew-rate limit (a budget per second of simulated time, so the physical steering speed
        // does not depend on the tick rate) and absolute clamp.
        float maxSteeringDiff = MAX_STEERING_RATE * dt;
        steer = MathF.Max(previousState.Steer - maxSteeringDiff,
                          MathF.Min(steer, previousState.Steer + maxSteeringDiff));
        steer = MathF.Max(-MAX_STEERING, MathF.Min(steer, MAX_STEERING));

        return new ActuationSignal(throttle, brake, steer);
    }
}
