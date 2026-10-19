using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// One of the ways SUMO teleports a waiting vehicle besides <c>time-to-teleport</c>, as the scenario
/// leaves it: an option, or a vehicle type's own attribute in place of one.
/// </summary>
/// <remarks>
/// Read by <see cref="SumoDistributionEditCheck"/>, which refuses a session where one is enabled unless
/// the run accepted teleporting explicitly, and lists every one on the run report with the value it ran
/// under.
/// </remarks>
public sealed class SumoTeleportTrigger
{
    internal SumoTeleportTrigger(string name, string? declared, double seconds, bool enabled)
    {
        Name = name;
        Declared = declared;
        Seconds = seconds;
        Enabled = enabled;
    }

    /// <summary>
    /// What sets it: the option as SUMO registers it, or the vehicle type and its attribute, as
    /// <c>vType 'bus' timeToTeleport</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>The value as written, or null where the option is not set and SUMO's default stands.</summary>
    public string? Declared { get; }

    /// <summary>The wait SUMO will use, seconds: the declared value, or SUMO's default.</summary>
    public double Seconds { get; }

    /// <summary>Whether SUMO will teleport a vehicle by it.</summary>
    public bool Enabled { get; }

    /// <summary>What was found, in the report's words.</summary>
    public override string ToString()
    {
        string seconds = Seconds.ToString("0.###", CultureInfo.InvariantCulture);
        if (Declared is null)
        {
            return $"{Name} not set, so SUMO's default of {seconds} s";
        }

        return Enabled ? $"{Name} '{Declared}', ENABLED after {seconds} s" : $"{Name} '{Declared}'";
    }
}
