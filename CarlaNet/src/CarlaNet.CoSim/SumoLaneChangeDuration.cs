using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// How long a lane change takes in the scenario's configuration, as SUMO reads it: spread over a span
/// of steps, the vehicle moving sideways at a steady rate, or made inside one step.
/// </summary>
/// <remarks>
/// <para><b>Read and recorded, not judged.</b> The scenario compiler writes a physical duration into
/// every configuration it compiles; an uncompiled scenario may set none. This says which governed the
/// run, because the two render differently, read from SUMO 1.27.0:</para>
/// <list type="bullet">
/// <item>The option is <c>lanechange.duration</c>, default 0 (<c>MSFrame.cpp:489</c>). A lane change
/// is spread over time only where it is longer than the step
/// (<c>MSAbstractLaneChangeModel::startLaneChangeManeuver</c>); then SUMO moves the vehicle across at
/// half the two lanes' widths per duration, keeps reporting the lane it started on until it is past
/// halfway and then the lane it is moving to, and both its reported position and its reported angle
/// include the sideways movement (<c>MSLaneChanger::continueChange</c>,
/// <c>MSVehicle::computeAngle</c>). The bridge puts the vehicle where SUMO has it.</item>
/// <item>Otherwise SUMO moves the vehicle onto the other lane inside one step, and the bridge can only
/// spread that lane width over the step: a sideways slide a lane wide in one step.</item>
/// </list>
///
/// <para><b>What it cannot see.</b> A vehicle type's <c>maxSpeedLat</c> sets that type's sideways rate in
/// place of the duration's, and route files are not read for it; the sublane model
/// (<c>lateral-resolution</c>) moves vehicles across by its own rules and is not read either.</para>
/// </remarks>
public sealed class SumoLaneChangeDuration
{
    /// <summary>The option, as SUMO registers it.</summary>
    public const string OptionName = "lanechange.duration";

    /// <summary>How long SUMO takes over a lane change when the option is not set, seconds: none.</summary>
    public const double SumoDefaultSeconds = 0.0;

    private SumoLaneChangeDuration(string? declared, double seconds)
    {
        Declared = declared;
        Seconds = seconds;
    }

    /// <summary>The value the configuration sets, as written, or null where it sets none.</summary>
    public string? Declared { get; }

    /// <summary>The duration SUMO will use, seconds: the declared value, or SUMO's default of 0.</summary>
    public double Seconds { get; }

    /// <summary>
    /// Whether SUMO spreads a lane change over time at the given step, rather than making it inside one:
    /// only where the duration is longer than the step.
    /// </summary>
    public bool IsSpreadOverTimeAt(double stepSeconds) =>
        SumoConfiguration.Milliseconds(Seconds) > SumoConfiguration.Milliseconds(stepSeconds);

    /// <summary>Read the configuration's <c>lanechange.duration</c>.</summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <exception cref="CoSimSessionRefusedException">
    /// The configuration cannot be read, or sets the option more than once or to something that is not
    /// a time -- each of which SUMO refuses too.
    /// </exception>
    public static SumoLaneChangeDuration Read(string scenarioPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        SumoConfiguration configuration = SumoConfiguration.Load(
            scenarioPath, "how long SUMO takes over a lane change cannot be established");
        IReadOnlyList<string> values = configuration.ValuesOf(OptionName);
        if (values.Count > 1)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} sets {OptionName} {values.Count} times ("
                + string.Join(", ", values.Select(value => $"'{value}'"))
                + "). SUMO refuses an option set twice, and which of them its lane changes would take is "
                + "not the configuration's to leave open.");
        }

        if (values.Count == 0)
        {
            return new SumoLaneChangeDuration(null, SumoDefaultSeconds);
        }

        if (!SumoConfiguration.TryParseTime(values[0], out double seconds))
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} sets {OptionName} to '{values[0]}', which is not a time SUMO "
                + "reads -- seconds, hh:mm:ss or dd:hh:mm:ss -- so SUMO would refuse it too.");
        }

        return new SumoLaneChangeDuration(values[0], seconds);
    }

    /// <summary>What governs the run at the given SUMO step, in the report's words.</summary>
    public string Describe(double stepSeconds)
    {
        string source = Declared is null
            ? $"{OptionName} not set, so SUMO's default of {SumoDefaultSeconds:0} s"
            : $"{OptionName} '{Declared}'";
        string step = stepSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return IsSpreadOverTimeAt(stepSeconds)
            ? $"spread over {Seconds.ToString("0.###", CultureInfo.InvariantCulture)} s, moving across at a "
              + $"steady rate, at a {step} s step ({source})"
            : $"INSTANTANEOUS: a lane width crossed inside one {step} s step ({source})";
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Declared is null
            ? $"{OptionName} not set, so SUMO's default of {SumoDefaultSeconds:0} s"
            : $"{OptionName} '{Declared}', {Seconds.ToString("0.###", CultureInfo.InvariantCulture)} s";
}
