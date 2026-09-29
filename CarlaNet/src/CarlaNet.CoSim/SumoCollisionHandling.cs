namespace CarlaNet.CoSim;

/// <summary>
/// What SUMO will do about a collision in the scenario's configuration, as SUMO reads it: whether it
/// registers one at all, and what it does to the two vehicles when it does.
/// </summary>
/// <remarks>
/// <para><b>Read and recorded, not judged.</b> The session records every collision SUMO registers and
/// goes on (<see cref="CollisionSpan"/>); which actions a corpus may carry is the truth record's
/// question. This says which action governed the run, because the same collision renders differently
/// under each, read from SUMO 1.27.0:</para>
/// <list type="bullet">
/// <item><c>warn</c> -- SUMO registers it, writes a warning, and both vehicles carry on as SUMO moves
/// them. What the scenario compiler writes.</item>
/// <item><c>teleport</c> -- SUMO's default (<c>MSFrame.cpp:399</c>): the collider is moved to the next
/// edge of its route, a jump in position the interpolation cannot connect.</item>
/// <item><c>remove</c> -- both vehicles are taken out, and leave the render set as arrivals.</item>
/// <item><c>none</c> -- the lane's collision check returns before registering anything
/// (<c>MSLane::detectCollisions</c>), so the session sees no collision at all.</item>
/// </list>
/// <para><c>ignore-accidents</c> set true turns the check off entirely, whatever the action.</para>
///
/// <para><b>What it cannot see.</b> What SUMO counts as a collision -- a gap below the follower's
/// <c>minGap</c> unless <c>collision.mingap-factor</c> lowers it, junctions only under
/// <c>collision.check-junctions</c> -- is SUMO's rule and is not repeated here.</para>
/// </remarks>
public sealed class SumoCollisionHandling
{
    /// <summary>The option naming the action, as SUMO registers it.</summary>
    public const string ActionOption = "collision.action";

    /// <summary>The option that turns the check off, as SUMO registers it.</summary>
    public const string IgnoreOption = "ignore-accidents";

    /// <summary>The action SUMO takes when the configuration names none.</summary>
    public const string SumoDefaultAction = "teleport";

    private static readonly string[] TrueWords = ["1", "yes", "true", "on", "x", "t"];

    private SumoCollisionHandling(string? declared, string action, bool ignored)
    {
        Declared = declared;
        Action = action;
        Ignored = ignored;
    }

    /// <summary>The action the configuration names, as written, or null where it names none.</summary>
    public string? Declared { get; }

    /// <summary>The action SUMO takes: the declared one, or <see cref="SumoDefaultAction"/>.</summary>
    public string Action { get; }

    /// <summary>Whether <c>ignore-accidents</c> turns the collision check off.</summary>
    public bool Ignored { get; }

    /// <summary>Whether SUMO registers a collision at all, so the session can record one.</summary>
    public bool Registered => !Ignored && !string.Equals(Action, "none", StringComparison.Ordinal);

    /// <summary>Read the configuration's collision options.</summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <exception cref="CoSimSessionRefusedException">It cannot be read as XML.</exception>
    public static SumoCollisionHandling Read(string scenarioPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        SumoConfiguration configuration = SumoConfiguration.Load(
            scenarioPath, "what SUMO does about a collision cannot be established");
        IReadOnlyList<string> actions = configuration.ValuesOf(ActionOption);
        string? declared = actions.Count == 0 ? null : string.Join(", ", actions);
        bool ignored = configuration.ValuesOf(IgnoreOption)
            .Any(value => TrueWords.Contains(value.Trim().ToLowerInvariant()));
        return new SumoCollisionHandling(declared, actions.Count == 0 ? SumoDefaultAction : actions[^1].Trim(),
                                         ignored);
    }

    /// <summary>What governs the run, in the report's words.</summary>
    public override string ToString()
    {
        string source = Declared is null
            ? $"{ActionOption} not set, so SUMO's default '{SumoDefaultAction}'"
            : $"{ActionOption} '{Declared}'";
        if (Ignored)
        {
            return $"not checked: {IgnoreOption} is set, so SUMO registers none ({source})";
        }

        return Action switch
        {
            "warn" => $"registered, warned and carried on ({source})",
            "teleport" => $"registered, and the collider moved to the next edge of its route ({source})",
            "remove" => $"registered, and both vehicles taken out ({source})",
            "none" => $"not registered, so none can be recorded ({source})",
            _ => $"'{Action}', which SUMO does not name ({source})",
        };
    }
}
