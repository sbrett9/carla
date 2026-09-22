namespace CarlaNet.CoSim;

/// <summary>
/// The three rates a co-simulation session runs at, and the whole-number relationships between
/// them that make a rendered frame's simulated instant exact.
/// </summary>
/// <remarks>
/// <para>Three clocks meet in a capture: SUMO advances in steps of its configuration's
/// <c>step-length</c>, the CARLA world advances in fixed deltas, and a recorder emits a frame every
/// so many of those. The bridge owns all three, and owning them is only worth anything if they
/// divide: a world tick has to land exactly on a SUMO step boundary every
/// <see cref="WorldTicksPerSumoStep"/> ticks, and a captured frame has to land exactly on a world
/// tick every <see cref="WorldTicksPerCapture"/> ticks.</para>
///
/// <para><b>A trio that does not divide does not fail loudly, which is why it is refused here.</b>
/// It produces a run in which the interpolation fraction never reaches 1.0 before the buffer
/// advances, so every SUMO step is rendered slightly short and the error accumulates against the
/// timestamp the truth record carries. The imagery looks ordinary, the truth record looks ordinary,
/// and they describe different instants. Nothing downstream can detect it and nothing can repair
/// it, so it is checked once, at the start, against the values the three sides actually report --
/// not against what a configuration file says they are.</para>
/// </remarks>
public sealed class CoSimClock
{
    /// <summary>
    /// How far from a whole number a ratio may sit and still count as one, relative to the larger
    /// of the two quantities. A step length of 0.05 s is not representable in binary, so
    /// <c>1.0 / 0.05</c> is 20.000000000000004 and an exact test rejects a trio that divides
    /// perfectly in the decimal the configuration was written in.
    /// </summary>
    private const double RatioTolerance = 1e-9;

    private CoSimClock(double sumoStepSeconds,
                       double worldDeltaSeconds,
                       double captureRateHz,
                       int worldTicksPerSumoStep,
                       int worldTicksPerCapture)
    {
        SumoStepSeconds = sumoStepSeconds;
        WorldDeltaSeconds = worldDeltaSeconds;
        CaptureRateHz = captureRateHz;
        WorldTicksPerSumoStep = worldTicksPerSumoStep;
        WorldTicksPerCapture = worldTicksPerCapture;
    }

    /// <summary>Simulated seconds in one SUMO step, as <c>Simulation.getDeltaT</c> reported it.</summary>
    public double SumoStepSeconds { get; }

    /// <summary>Simulated seconds in one CARLA world tick, as the world's settings reported it.</summary>
    public double WorldDeltaSeconds { get; }

    /// <summary>Frames per simulated second the capture is asked for.</summary>
    public double CaptureRateHz { get; }

    /// <summary>
    /// World ticks per SUMO step. The number of interpolated poses produced between one pair of
    /// buffered SUMO frames, and 1 where the scenario already steps at the world's rate.
    /// </summary>
    public int WorldTicksPerSumoStep { get; }

    /// <summary>World ticks between captured frames.</summary>
    public int WorldTicksPerCapture { get; }

    /// <summary>Simulated seconds of one whole SUMO step's worth of world ticks.</summary>
    public double RenderedSecondsPerSumoStep => WorldTicksPerSumoStep * WorldDeltaSeconds;

    /// <summary>
    /// Validate the three rates against each other and build the clock, or refuse the session.
    /// </summary>
    /// <param name="sumoStepSeconds">What <c>Simulation.getDeltaT</c> reports, not what the
    /// configuration file says: an override on the command line changes one and not the other.</param>
    /// <param name="worldDeltaSeconds">The world's <c>fixed_delta_seconds</c>. Zero or absent means
    /// the world runs at a variable delta, which no simulated clock can be projected onto.</param>
    /// <param name="captureRateHz">Frames per simulated second the run will record.</param>
    /// <param name="worldIsSynchronous">
    /// Whether the world only advances when a client cues it. The bridge owns the advance of
    /// simulated time on both sides, which an asynchronous world makes impossible: it advances on
    /// its own between the pose write and the frame, so no frame corresponds to any SUMO step.
    /// </param>
    /// <exception cref="CoSimSessionRefusedException">Any of the conditions above.</exception>
    public static CoSimClock ForSession(double sumoStepSeconds,
                                        double worldDeltaSeconds,
                                        double captureRateHz,
                                        bool worldIsSynchronous)
    {
        if (!worldIsSynchronous)
        {
            throw new CoSimSessionRefusedException(
                "The CARLA world is in asynchronous mode. A co-simulation session owns the advance "
                + "of simulated time on both sides, so the world must advance only on a tick cue: "
                + "asynchronously it advances between the pose write and the frame, no captured "
                + "frame corresponds to a SUMO step, and a sensor listening for one is never "
                + "handed a frame at all. Set synchronous_mode on the world settings before "
                + "starting the session.");
        }

        if (!double.IsFinite(worldDeltaSeconds) || worldDeltaSeconds <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"The CARLA world reports a fixed delta of {Describe(worldDeltaSeconds)} s, so it "
                + "runs at whatever delta the last frame took. A simulated clock cannot be "
                + "projected onto a variable one, and the interpolation fraction between two SUMO "
                + "frames has no denominator. Set fixed_delta_seconds on the world settings.");
        }

        if (!double.IsFinite(sumoStepSeconds) || sumoStepSeconds <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"SUMO reports a step length of {Describe(sumoStepSeconds)} s.");
        }

        if (!double.IsFinite(captureRateHz) || captureRateHz <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"The capture rate is {Describe(captureRateHz)} Hz.");
        }

        int ticksPerStep = WholeRatio(sumoStepSeconds, worldDeltaSeconds)
            ?? throw new CoSimSessionRefusedException(
                $"The SUMO step of {sumoStepSeconds:0.######} s is not a whole number of "
                + $"{worldDeltaSeconds:0.######} s world ticks -- it is "
                + $"{sumoStepSeconds / worldDeltaSeconds:0.####} of them. Every world tick between "
                + "two SUMO frames is an interpolated fraction of the step, and a fraction that "
                + "never reaches 1.0 before the buffer advances renders every step short, with the "
                + "error accumulating against the instant the truth record carries. "
                + NearestDivisors(sumoStepSeconds, worldDeltaSeconds));

        int ticksPerCapture = WholeRatio(1.0 / captureRateHz, worldDeltaSeconds)
            ?? throw new CoSimSessionRefusedException(
                $"A capture rate of {captureRateHz:0.######} Hz is one frame every "
                + $"{1.0 / captureRateHz:0.######} s, which is not a whole number of "
                + $"{worldDeltaSeconds:0.######} s world ticks. A frame would then be recorded on "
                + "the nearest tick rather than on the instant it is stamped with, and the stamp is "
                + "what a detect-and-track consumer aligns its tracks to.");

        return new CoSimClock(sumoStepSeconds, worldDeltaSeconds, captureRateHz,
                              ticksPerStep, ticksPerCapture);
    }

    /// <summary>
    /// The interpolation fraction for one world tick within a SUMO step: 0.0 on the tick that lands
    /// on the buffered frame, and strictly below 1.0 on every other -- the fraction reaches 1.0 only
    /// as the next step's first tick, which is that step's 0.0.
    /// </summary>
    /// <param name="tickWithinStep">Which tick of the step, from 0 to
    /// <see cref="WorldTicksPerSumoStep"/> - 1.</param>
    public double InterpolationFraction(int tickWithinStep)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tickWithinStep);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tickWithinStep, WorldTicksPerSumoStep);
        return (double)tickWithinStep / WorldTicksPerSumoStep;
    }

    /// <summary>Whether a frame is captured on this tick of the session.</summary>
    /// <param name="tickIndex">Ticks since the session's first, counting from zero.</param>
    public bool IsCaptureTick(long tickIndex) => tickIndex % WorldTicksPerCapture == 0;

    /// <summary>What this clock resolved to, for the run manifest and for a refusal to sit beside.</summary>
    public override string ToString() =>
        $"SUMO step {SumoStepSeconds:0.######} s = {WorldTicksPerSumoStep} world ticks of "
        + $"{WorldDeltaSeconds:0.######} s; capture {CaptureRateHz:0.######} Hz = one frame every "
        + $"{WorldTicksPerCapture} ticks";

    /// <summary>
    /// <paramref name="numerator"/> divided by <paramref name="denominator"/> where that is a
    /// positive whole number, or <see langword="null"/> where it is not.
    /// </summary>
    private static int? WholeRatio(double numerator, double denominator)
    {
        double ratio = numerator / denominator;
        double rounded = Math.Round(ratio);
        if (rounded < 1.0 || rounded > int.MaxValue)
        {
            return null;
        }

        return Math.Abs(numerator - (rounded * denominator)) <= RatioTolerance * Math.Max(numerator, denominator)
            ? (int)rounded
            : null;
    }

    /// <summary>
    /// The two world deltas either side of the one given that would divide the SUMO step, so a
    /// refusal says what to change it to rather than only that it is wrong.
    /// </summary>
    private static string NearestDivisors(double sumoStepSeconds, double worldDeltaSeconds)
    {
        int below = Math.Max(1, (int)Math.Floor(sumoStepSeconds / worldDeltaSeconds));
        int above = below + 1;
        return $"A world delta of {sumoStepSeconds / above:0.######} s ({above} ticks per step) or "
               + $"{sumoStepSeconds / below:0.######} s ({below}) would divide it.";
    }

    private static string Describe(double value) =>
        double.IsFinite(value) ? value.ToString("0.######") : value.ToString();
}
