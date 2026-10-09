using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// Establishes whether the scenario's configuration lets SUMO teleport a blocked vehicle, and refuses
/// a session where it does unless the run accepted it explicitly.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A SUMO teleport moves a vehicle that has waited too long straight to a lane
/// further along its route. The bridge interpolates between consecutive SUMO poses on the assumption
/// that a drivable path of plausible length connects them, so a teleport would render as a vehicle
/// dragged across the map at an impossible speed with a matching and entirely false velocity in the
/// truth record. The runtime jump detector releases and re-admits a vehicle across a discontinuity
/// whether or not this check ran; this check keeps a scenario that would produce them from starting
/// unnoticed.</para>
///
/// <para><b>What enables it, read from SUMO 1.27.0 and measured.</b> The option is
/// <c>time-to-teleport</c>, registered with no synonym and a default of 300 s
/// (<c>MSFrame.cpp:438</c>); a vehicle is teleported for a jam only where the value is positive
/// (<c>MSLane.cpp:2402-2410</c>, <c>ttt &gt; 0</c>). Measured on the fixture network with a vehicle
/// blocked behind a stopped one: <c>5</c> and <c>00:00:05</c> teleported it after 5 s of waiting,
/// <c>0.5</c> after half a second, the option absent after 300 s, and <c>0</c> and <c>-1</c> never.
/// So a positive value, or no value at all, is refused; zero and negative values disable teleporting
/// and run. The value is read as SUMO reads a time: seconds, or <c>hh:mm:ss</c>, or
/// <c>dd:hh:mm:ss</c>, to the millisecond.</para>
///
/// <para><b>What it leaves to another check.</b> A vehicle type's own <c>timeToTeleport</c> attribute
/// overrides the option for vehicles of that type; the other teleport triggers --
/// <c>time-to-teleport.highways</c>, <c>.disconnected</c>, <c>.bidi</c> and
/// <c>.railsignal-deadlock</c>, all off at SUMO's defaults, though <c>.disconnected</c> is on from zero
/// up -- and a <c>collision.action</c> of <c>teleport</c>, which is SUMO's default, are
/// <see cref="SumoDistributionEditCheck"/>'s, which honours the same acceptance for the triggers.</para>
/// </remarks>
public sealed class TeleportingCheck
{
    /// <summary>The option, as SUMO registers it.</summary>
    public const string OptionName = "time-to-teleport";

    /// <summary>What SUMO waits before teleporting a blocked vehicle when the option is not set, seconds.</summary>
    public const double SumoDefaultSeconds = 300.0;

    private TeleportingCheck(string? declared, double seconds, bool accepted)
    {
        Declared = declared;
        Seconds = seconds;
        Accepted = accepted;
    }

    /// <summary>The value the configuration sets, as written, or null where it sets none.</summary>
    public string? Declared { get; }

    /// <summary>The wait SUMO will use, seconds: the declared value, or SUMO's default of 300.</summary>
    public double Seconds { get; }

    /// <summary>Whether SUMO will teleport a vehicle blocked for longer than <see cref="Seconds"/>.</summary>
    public bool Enabled => Milliseconds(Seconds) > 0;

    /// <summary>Whether teleporting is enabled and the run accepted it explicitly.</summary>
    public bool Accepted { get; }

    /// <summary>
    /// Read the configuration's <c>time-to-teleport</c> and refuse one that enables teleporting, unless
    /// <paramref name="allowTeleporting"/>.
    /// </summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <param name="allowTeleporting">The run accepted teleporting explicitly.</param>
    /// <exception cref="CoSimSessionRefusedException">
    /// The configuration cannot be read, sets the option more than once or to something that is not a
    /// time, or enables teleporting and the run did not accept it.
    /// </exception>
    public static TeleportingCheck Require(string scenarioPath, bool allowTeleporting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        SumoConfiguration configuration = SumoConfiguration.Load(
            scenarioPath, "whether SUMO would teleport a blocked vehicle cannot be established");
        IReadOnlyList<string> values = configuration.ValuesOf(OptionName);
        if (values.Count > 1)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} sets {OptionName} {values.Count} times ("
                + string.Join(", ", values.Select(value => $"'{value}'"))
                + "). SUMO refuses an option set twice, and which of them it would teleport by is not "
                + "the configuration's to leave open.");
        }

        string? declared = values.Count == 1 ? values[0] : null;
        double seconds = declared is null ? SumoDefaultSeconds : ParseTime(declared, scenarioPath);
        var check = new TeleportingCheck(declared, seconds, accepted: allowTeleporting && Milliseconds(seconds) > 0);
        if (check.Enabled && !allowTeleporting)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} lets SUMO teleport a vehicle blocked for "
                + $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)} s ("
                + (declared is null
                    ? $"it sets no {OptionName}, and SUMO's default is {SumoDefaultSeconds:0} s"
                    : $"{OptionName} is '{declared}'")
                + "). A teleport moves a vehicle straight to a lane further along its route, which would "
                + "render as a body dragged across the map with a matching false velocity in the truth "
                + "record. SUMO has not been started. Set time-to-teleport to -1, as the scenario compiler "
                + "does, or accept teleporting explicitly (AllowTeleporting; run_sumo_drive.py "
                + "--allow-teleporting), which the run report then records.");
        }

        return check;
    }

    /// <summary>What was found, in the report's words.</summary>
    public override string ToString()
    {
        string source = Declared is null
            ? $"{OptionName} not set, so SUMO's default of {SumoDefaultSeconds:0} s"
            : $"{OptionName} '{Declared}'";
        return Enabled
            ? $"ENABLED after {Seconds.ToString("0.###", CultureInfo.InvariantCulture)} s of blocking "
              + $"({source}), accepted explicitly"
            : $"disabled ({source})";
    }

    /// <summary>
    /// A SUMO time: seconds, or <c>hh:mm:ss</c>, or <c>dd:hh:mm:ss</c>, as <c>string2time</c> reads it.
    /// </summary>
    private static double ParseTime(string text, string scenarioPath)
    {
        if (!SumoConfiguration.TryParseTime(text, out double seconds))
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} sets {OptionName} to '{text}', which is not a time SUMO "
                + "reads -- seconds, hh:mm:ss or dd:hh:mm:ss -- so SUMO would refuse it too.");
        }

        return seconds;
    }

    private static long Milliseconds(double seconds) => SumoConfiguration.Milliseconds(seconds);
}
