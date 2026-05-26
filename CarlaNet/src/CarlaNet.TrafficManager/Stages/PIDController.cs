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
//   |Δsteer| ≤ MAX_STEERING_DIFF  (slew-rate limit between ticks)
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
    private const float MAX_STEERING_DIFF = Constants.PID.MAX_STEERING_DIFF;

    /// <summary>
    /// Compute the actuation signal that minimises the PID error between
    /// <paramref name="presentState"/> and <paramref name="previousState"/>.
    /// Mirrors <c>PID::RunStep</c> in PIDController.h:27–59 byte-for-byte.
    /// </summary>
    /// <param name="presentState">Current PID error snapshot.</param>
    /// <param name="previousState">Previous tick's PID error snapshot.</param>
    /// <param name="longitudinalParameters">{Kp, Ki, Kd} for velocity loop.</param>
    /// <param name="lateralParameters">{Kp, Ki, Kd} for steering loop.</param>
    public static ActuationSignal RunStep(
        StateEntry presentState,
        StateEntry previousState,
        ReadOnlySpan<float> longitudinalParameters,
        ReadOnlySpan<float> lateralParameters)
    {
        // ── Longitudinal PID: throttle / brake ────────────────────────────
        float exprV =
            longitudinalParameters[0] * presentState.VelocityDeviation
          + longitudinalParameters[1] * (presentState.VelocityDeviation + previousState.VelocityDeviation) * DT
          + longitudinalParameters[2] * (presentState.VelocityDeviation - previousState.VelocityDeviation) * INV_DT;

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
        float steer =
            lateralParameters[0] * presentState.AngularDeviation
          + lateralParameters[1] * (presentState.AngularDeviation + previousState.AngularDeviation) * DT
          + lateralParameters[2] * (presentState.AngularDeviation - previousState.AngularDeviation) * INV_DT;

        // Slew-rate limit and absolute clamp.
        steer = MathF.Max(previousState.Steer - MAX_STEERING_DIFF,
                          MathF.Min(steer, previousState.Steer + MAX_STEERING_DIFF));
        steer = MathF.Max(-MAX_STEERING, MathF.Min(steer, MAX_STEERING));

        return new ActuationSignal(throttle, brake, steer);
    }
}
