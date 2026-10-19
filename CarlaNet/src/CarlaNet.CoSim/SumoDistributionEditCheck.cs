using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace CarlaNet.CoSim;

/// <summary>
/// Establishes how SUMO will edit the scenario's population on its own -- the collision action, the
/// teleports <c>time-to-teleport</c> leaves open, a random offset on every departure, an unseeded run,
/// the demand scale and the insertion limits -- refusing a session where SUMO would make an edit the
/// truth record cannot carry, and naming the rest as they will run.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> SUMO edits the population it simulates, and none of it shows as an edit in the
/// imagery or the truth: a teleported vehicle renders as a body dragged across the map with a matching
/// false velocity, a vehicle taken out at a collision leaves as though it had arrived, a departure moved
/// by a random offset makes the declared departure a time the vehicle did not depart at, and a run seeded
/// from the wall clock cannot be run again. Those are refused before SUMO is started. Scaling the demand,
/// capping the vehicles running and discarding a vehicle that waited too long to be inserted are recorded
/// instead: the report names each as it will run, so a run's report says which distribution-affecting
/// settings it ran under rather than leaving it to the files.</para>
///
/// <para><b>What is refused, read from SUMO 1.27.0 and measured on the fixture network.</b></para>
/// <list type="bullet">
/// <item>A <c>collision.action</c> other than <c>warn</c> (<see cref="PermittedCollisionActions"/>),
/// SUMO's default of <c>teleport</c> included (<c>MSFrame.cpp:399</c>). Under <c>warn</c> SUMO changes
/// nothing about the traffic and only registers the collision, so the record of collisions exists;
/// <c>teleport</c> moves the collider to the next edge of its route, <c>remove</c> takes both vehicles
/// out, and <c>none</c> skips the check (<c>MSLane::detectCollisions</c>), so the run could not say whether
/// any collision happened. Read case-sensitively, as <c>MSLane::initCollisionAction</c> reads it;
/// measured, an action it does not name, <c>Warn</c> among them, is an error on SUMO's console and the run
/// goes on under <c>teleport</c> (<c>MSLane.cpp:107</c>), so it is refused as <c>teleport</c> is.
/// <c>ignore-accidents</c> set true skips the check whatever the action, so the action in force is then
/// <c>none</c>, and it is refused as <c>none</c> is.</item>
/// <item>Every teleport trigger besides <c>time-to-teleport</c>, unless the run accepted teleporting
/// explicitly. <c>time-to-teleport.highways</c>, default 0 (<c>MSFrame.cpp:441</c>), teleports a vehicle
/// waiting on a lane that does not continue its route, on a road faster than 69 km/h, only where it is
/// positive (<c>MSLane.cpp:2413</c>): measured under a <c>time-to-teleport</c> of -1, a vehicle held on
/// such a lane stood there for the whole 1000 s run with the option absent, 0 or -1, and was teleported
/// after 5 s of waiting with 5. <c>time-to-teleport.disconnected</c>, default -1 (<c>MSFrame.cpp:447</c>),
/// teleports a vehicle whose edge has no lane onto the next edge of its route wherever it is zero or more
/// (<c>MSLane.cpp:2406</c>, <c>&gt;= 0</c>): measured on such a route, absent and -1 never, 0 after one step
/// of waiting and 5 after 5 s. <c>time-to-teleport.bidi</c> and <c>.railsignal-deadlock</c>, default -1,
/// where positive (<c>MSLane.cpp:2418</c>, <c>:2420</c>). And a vehicle type's own <c>timeToTeleport</c>
/// and <c>timeToTeleportBidi</c>, which stand in for <c>time-to-teleport</c> and <c>.bidi</c> for
/// vehicles of that type (<c>SUMOVTypeParameter::getTimeToTeleport</c>), where positive: measured, a type
/// setting 5 under a <c>time-to-teleport</c> of -1 was teleported after 5 s of being blocked.</item>
/// <item>A positive <c>random-depart-offset</c> (<c>MSInsertionControl.cpp:422</c>): measured, 5 moved all
/// four of the fixture's departures, by up to 3.75 s, and -5 moved none.</item>
/// <item><c>random</c>, or its old name <c>abs-rand</c>, set true, which seeds SUMO from the wall clock in
/// place of the seed (<c>RandHelper.cpp:79</c>).</item>
/// </list>
///
/// <para><b>What is recorded and never refused.</b> <c>scale</c>, default 1 (<c>MSFrame.cpp:432</c>), which
/// scales the demand by discarding or duplicating vehicles, and a vehicle type's own <c>scale</c>, which
/// multiplies it for that type (<c>MSInsertionControl.cpp:229</c>); <c>max-num-vehicles</c>, default -1 for
/// no limit (<c>MSFrame.cpp:423</c>), which delays an insertion that would exceed it; and
/// <c>max-depart-delay</c>, default -1 for never (<c>MSFrame.cpp:471</c>), past which a vehicle still not
/// inserted is discarded, from zero up (<c>MSInsertionControl.cpp:168</c>). The session records each
/// vehicle discarded that way as one not inserted.</para>
///
/// <para><b>How it is read.</b> The options as SUMO reads a configuration (<see cref="SumoConfiguration"/>),
/// times as SUMO reads a time, to the millisecond; the vehicle types from every route file and additional
/// file the configuration names, since SUMO loads types from both. An option set twice, a time or a
/// type's attribute SUMO cannot read and a negative scale are refused, as SUMO refuses each. A boolean,
/// whole-number or number option SUMO cannot read is refused too, though SUMO does not refuse it:
/// measured, it writes an error and runs on under the option's default, so the file does not say what
/// ran. A file that is not there is left to SUMO, which refuses it as it loads, before the first step; one
/// that is there and is not XML is refused, since SUMO reads route files a slice at a time as the run goes
/// and would meet the fault part-way through, having loaded the types before it.</para>
///
/// <para><b>What it cannot see.</b> <c>time-to-teleport</c> itself is <see cref="TeleportingCheck"/>'s.
/// <c>time-to-teleport.remove</c>, which takes a vehicle out where a teleport would move it, matters only
/// where a teleport is enabled. <c>time-to-teleport.ride</c> teleports persons and
/// <c>intermodal-collision.action</c> governs collisions with them, and no scenario here has any. A type
/// added or changed through TraCI is not seen; the session adds none.</para>
/// </remarks>
public sealed class SumoDistributionEditCheck
{
    /// <summary>The option that teleports a vehicle waiting on a fast road's wrong lane.</summary>
    public const string HighwaysOption = "time-to-teleport.highways";

    /// <summary>The option that teleports a vehicle whose route its edge cannot follow.</summary>
    public const string DisconnectedOption = "time-to-teleport.disconnected";

    /// <summary>The option that teleports a vehicle waiting on a bidirectional edge.</summary>
    public const string BidiOption = "time-to-teleport.bidi";

    /// <summary>The option that teleports a vehicle held in a rail-signal deadlock.</summary>
    public const string RailSignalDeadlockOption = "time-to-teleport.railsignal-deadlock";

    /// <summary>The option that moves every departure by a random offset.</summary>
    public const string RandomDepartOffsetOption = "random-depart-offset";

    /// <summary>The option that seeds SUMO from the wall clock, as SUMO registers it.</summary>
    public const string RandomOption = "random";

    /// <summary>The option that scales the demand.</summary>
    public const string ScaleOption = "scale";

    /// <summary>The option that caps the vehicles running at once.</summary>
    public const string MaxNumVehiclesOption = "max-num-vehicles";

    /// <summary>The option past which a vehicle not yet inserted is discarded.</summary>
    public const string MaxDepartDelayOption = "max-depart-delay";

    /// <summary>
    /// The collision actions a run may carry: only <c>warn</c>, under which SUMO registers each collision
    /// and both vehicles carry on as SUMO moves them. Every other action is refused, <c>none</c> and
    /// <c>ignore-accidents</c> among them, because the record of collisions must exist and under either
    /// SUMO skips the check that makes it.
    /// </summary>
    public static IReadOnlyList<string> PermittedCollisionActions { get; } = ["warn"];

    /// <summary>
    /// Each teleport option besides <c>time-to-teleport</c>: SUMO's default, whether zero already enables
    /// it, and what it teleports, in words a refusal can finish a sentence with.
    /// </summary>
    private static readonly (string Option, double DefaultSeconds, bool EnabledAtZero, string What)[] TeleportOptions =
    [
        (HighwaysOption, 0.0, false, "a vehicle waiting on a lane that does not continue its route, on a fast road,"),
        (DisconnectedOption, -1.0, true, "a vehicle whose edge has no lane onto the next edge of its route"),
        (BidiOption, -1.0, false, "a vehicle waiting on a bidirectional edge"),
        (RailSignalDeadlockOption, -1.0, false, "a vehicle held in a rail-signal deadlock"),
    ];

    /// <summary>
    /// Each vehicle-type attribute that teleports vehicles of that type, the option it stands in for, and
    /// what it teleports.
    /// </summary>
    private static readonly (string Attribute, string StandsInFor, string What)[] TypeTeleportAttributes =
    [
        ("timeToTeleport", TeleportingCheck.OptionName, "a vehicle of that type that is blocked"),
        ("timeToTeleportBidi", BidiOption, "a vehicle of that type waiting on a bidirectional edge"),
    ];

    private readonly int _typeTriggers;

    private SumoDistributionEditCheck(string? collisionActionDeclared,
                                      string collisionAction,
                                      bool accidentsIgnored,
                                      IReadOnlyList<SumoTeleportTrigger> teleportTriggers,
                                      int typeTriggers,
                                      bool teleportingAccepted,
                                      string? randomDepartOffsetDeclared,
                                      string? randomDeclared,
                                      string? scaleDeclared,
                                      double scale,
                                      IReadOnlyList<(string TypeId, string Scale)> typeScales,
                                      string? maxNumVehiclesDeclared,
                                      int maxNumVehicles,
                                      string? maxDepartDelayDeclared,
                                      double maxDepartDelaySeconds)
    {
        CollisionActionDeclared = collisionActionDeclared;
        CollisionAction = collisionAction;
        AccidentsIgnored = accidentsIgnored;
        TeleportTriggers = teleportTriggers;
        _typeTriggers = typeTriggers;
        TeleportingAccepted = teleportingAccepted;
        RandomDepartOffsetDeclared = randomDepartOffsetDeclared;
        RandomDeclared = randomDeclared;
        ScaleDeclared = scaleDeclared;
        Scale = scale;
        TypeScales = typeScales;
        MaxNumVehiclesDeclared = maxNumVehiclesDeclared;
        MaxNumVehicles = maxNumVehicles;
        MaxDepartDelayDeclared = maxDepartDelayDeclared;
        MaxDepartDelaySeconds = maxDepartDelaySeconds;
    }

    /// <summary>The collision action the configuration names, as written, or null where it names none.</summary>
    public string? CollisionActionDeclared { get; }

    /// <summary>The action SUMO takes: the declared one, or SUMO's default of <c>teleport</c>.</summary>
    public string CollisionAction { get; }

    /// <summary>Whether <c>ignore-accidents</c> turns the collision check off.</summary>
    public bool AccidentsIgnored { get; }

    /// <summary>
    /// The action in force: <see cref="CollisionAction"/>, or <c>none</c> where accidents are ignored. One of
    /// <see cref="PermittedCollisionActions"/> in any run that started.
    /// </summary>
    public string CollisionActionInForce => AccidentsIgnored ? "none" : CollisionAction;

    /// <summary>
    /// Every teleport trigger besides <c>time-to-teleport</c>, with the value it ran under: the four
    /// options, set or at SUMO's default, then each vehicle type's own attribute where one sets it.
    /// </summary>
    public IReadOnlyList<SumoTeleportTrigger> TeleportTriggers { get; }

    /// <summary>Whether any of <see cref="TeleportTriggers"/> lets SUMO teleport a vehicle.</summary>
    public bool TeleportingEnabled => TeleportTriggers.Any(trigger => trigger.Enabled);

    /// <summary>Whether a teleport trigger is enabled and the run accepted it explicitly.</summary>
    public bool TeleportingAccepted { get; }

    /// <summary>The configuration's <c>random-depart-offset</c>, as written, or null where it sets none.</summary>
    public string? RandomDepartOffsetDeclared { get; }

    /// <summary>The configuration's <c>random</c>, as written, or null where it sets none.</summary>
    public string? RandomDeclared { get; }

    /// <summary>The configuration's <c>scale</c>, as written, or null where it sets none.</summary>
    public string? ScaleDeclared { get; }

    /// <summary>The factor SUMO scales the demand by: the declared one, or SUMO's default of 1.</summary>
    public double Scale { get; }

    /// <summary>Each vehicle type that sets its own <c>scale</c>, and the value as written, in file order.</summary>
    public IReadOnlyList<(string TypeId, string Scale)> TypeScales { get; }

    /// <summary>The configuration's <c>max-num-vehicles</c>, as written, or null where it sets none.</summary>
    public string? MaxNumVehiclesDeclared { get; }

    /// <summary>
    /// How many vehicles SUMO lets run at once: the declared number, or SUMO's default of -1, which is no
    /// limit, as is any negative number.
    /// </summary>
    public int MaxNumVehicles { get; }

    /// <summary>The configuration's <c>max-depart-delay</c>, as written, or null where it sets none.</summary>
    public string? MaxDepartDelayDeclared { get; }

    /// <summary>
    /// How long past its departure SUMO keeps trying to insert a vehicle before discarding it, seconds: the
    /// declared value, or SUMO's default of -1, which is never.
    /// </summary>
    public double MaxDepartDelaySeconds { get; }

    /// <summary>Whether SUMO discards a vehicle it has not inserted within <see cref="MaxDepartDelaySeconds"/>.</summary>
    public bool DiscardsLateInsertions => SumoConfiguration.Milliseconds(MaxDepartDelaySeconds) >= 0;

    /// <summary>
    /// Read the configuration and the vehicle types its route and additional files define, and refuse one
    /// that lets SUMO make an edit the truth record cannot carry -- a teleport trigger only unless
    /// <paramref name="allowTeleporting"/>.
    /// </summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <param name="allowTeleporting">The run accepted teleporting explicitly.</param>
    /// <exception cref="CoSimSessionRefusedException">
    /// The configuration or a file it names cannot be read; an option is set more than once or to something
    /// SUMO would not read; or SUMO would make an edit the truth cannot carry -- every one is named.
    /// </exception>
    public static SumoDistributionEditCheck Require(string scenarioPath, bool allowTeleporting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        SumoConfiguration configuration = SumoConfiguration.Load(
            scenarioPath, "how SUMO would edit its own population cannot be established");
        List<string> refused = [];
        bool teleportRefused = false;

        // The collision action, and whether the check that applies it runs at all.
        string? actionDeclared = Single(configuration, refused, SumoCollisionHandling.ActionOption);
        string action = actionDeclared?.Trim() ?? SumoCollisionHandling.SumoDefaultAction;
        string? ignoreDeclared = Single(configuration, refused, SumoCollisionHandling.IgnoreOption);
        bool ignored = false;
        if (ignoreDeclared is not null && !SumoConfiguration.TryParseBoolean(ignoreDeclared, out ignored))
        {
            refused.Add(NotABoolean(SumoCollisionHandling.IgnoreOption, ignoreDeclared));
        }

        // An option set twice is already refused, and what it would have run under is not known.
        bool collisionRead = configuration.ValuesOf(SumoCollisionHandling.ActionOption).Count <= 1
                             && configuration.ValuesOf(SumoCollisionHandling.IgnoreOption).Count <= 1;
        if (collisionRead && !PermittedCollisionActions.Contains(ignored ? "none" : action, StringComparer.Ordinal))
        {
            refused.Add(RefusedCollision(actionDeclared, action, ignored ? ignoreDeclared : null));
        }

        // The teleport options time-to-teleport leaves open.
        List<SumoTeleportTrigger> triggers = [];
        foreach ((string option, double sumoDefault, bool enabledAtZero, string what) in TeleportOptions)
        {
            string? declared = Single(configuration, refused, option);
            if (declared is null)
            {
                triggers.Add(new SumoTeleportTrigger(option, null, sumoDefault, enabled: false));
                continue;
            }

            if (!SumoConfiguration.TryParseTime(declared, out double seconds))
            {
                refused.Add(NotATime(option, declared));
                continue;
            }

            long milliseconds = SumoConfiguration.Milliseconds(seconds);
            bool enabled = enabledAtZero ? milliseconds >= 0 : milliseconds > 0;
            triggers.Add(new SumoTeleportTrigger(option, declared, seconds, enabled));
            if (enabled && !allowTeleporting)
            {
                teleportRefused = true;
                refused.Add($"{option} is '{declared}', so SUMO teleports {what} {Waited(seconds)}"
                            + (enabledAtZero && milliseconds == 0 ? " (zero enables it; -1 disables it)" : string.Empty));
            }
        }

        // And each vehicle type's own: its teleport attributes, which stand in for the options, and its scale.
        int optionTriggers = triggers.Count;
        List<(string TypeId, string Scale)> typeScales = [];
        foreach (string file in configuration.FilesOf("route-files", "routes", "r")
                     .Concat(configuration.FilesOf("additional-files", "additional", "a")))
        {
            // A file that is not there defines nothing SUMO will run: SUMO refuses it the moment it loads,
            // naming it, and the session stops at launch quoting SUMO.
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (XElement type in ReadDefinitions(file, scenarioPath).Descendants()
                         .Where(element => element.Name.LocalName == "vType"))
            {
                string id = (string?)type.Attribute("id") ?? "(no id)";
                string where = $"vehicle type '{id}' in {Path.GetFileName(file)}";
                foreach ((string attribute, string standsInFor, string what) in TypeTeleportAttributes)
                {
                    if ((string?)type.Attribute(attribute) is not { } declared)
                    {
                        continue;
                    }

                    if (!SumoConfiguration.TryParseTime(declared, out double seconds))
                    {
                        refused.Add($"{where} sets {attribute} to '{declared}', which is not a time SUMO reads, "
                                    + "so SUMO would refuse the type");
                        continue;
                    }

                    bool enabled = SumoConfiguration.Milliseconds(seconds) > 0;
                    triggers.Add(new SumoTeleportTrigger($"vType '{id}' {attribute}", declared, seconds, enabled));
                    if (enabled && !allowTeleporting)
                    {
                        teleportRefused = true;
                        refused.Add($"{where} sets {attribute} '{declared}', so SUMO teleports {what} "
                                    + $"{Waited(seconds)}, whatever {standsInFor} says");
                    }
                }

                if ((string?)type.Attribute(ScaleOption) is { } typeScale)
                {
                    if (!TryParseNumber(typeScale, out double factor) || factor < 0.0)
                    {
                        refused.Add($"{where} sets {ScaleOption} to '{typeScale}', which SUMO refuses: a scale "
                                    + "is a number, and not a negative one");
                    }
                    else
                    {
                        typeScales.Add((id, typeScale));
                    }
                }
            }
        }

        // A random offset on every departure.
        string? offsetDeclared = Single(configuration, refused, RandomDepartOffsetOption);
        if (offsetDeclared is not null)
        {
            if (!SumoConfiguration.TryParseTime(offsetDeclared, out double offset))
            {
                refused.Add(NotATime(RandomDepartOffsetOption, offsetDeclared));
            }
            else if (SumoConfiguration.Milliseconds(offset) > 0)
            {
                refused.Add($"{RandomDepartOffsetOption} is '{offsetDeclared}', so SUMO moves every departure by a "
                            + $"random offset of up to {Shown(offset)} s, and no departure the truth declares is "
                            + "one a vehicle departed at; remove it");
            }
        }

        // A seed from the wall clock.
        string? randomDeclared = Single(configuration, refused, RandomOption, "abs-rand");
        if (randomDeclared is not null)
        {
            if (!SumoConfiguration.TryParseBoolean(randomDeclared, out bool unseeded))
            {
                refused.Add(NotABoolean(RandomOption, randomDeclared));
            }
            else if (unseeded)
            {
                refused.Add($"{RandomOption} is '{randomDeclared}', so SUMO seeds itself from the wall clock in "
                            + "place of the seed, the run's traffic cannot be run again and the seed the report "
                            + "names is not the one it ran under; remove it");
            }
        }

        // What is recorded rather than refused: the demand scale and the insertion limits.
        string? scaleDeclared = Single(configuration, refused, ScaleOption);
        double scale = 1.0;
        if (scaleDeclared is not null && !TryParseNumber(scaleDeclared, out scale))
        {
            refused.Add($"{ScaleOption} is '{scaleDeclared}', which is not a number" + RunsOnUnder("1"));
        }
        else if (scale < 0.0)
        {
            refused.Add($"{ScaleOption} is '{scaleDeclared}', and SUMO refuses a negative scale too");
        }

        string? capDeclared = Single(configuration, refused, MaxNumVehiclesOption);
        int cap = -1;
        if (capDeclared is not null
            && !int.TryParse(capDeclared.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cap))
        {
            refused.Add($"{MaxNumVehiclesOption} is '{capDeclared}', which is not a whole number" + RunsOnUnder("-1"));
        }

        string? delayDeclared = Single(configuration, refused, MaxDepartDelayOption);
        double delay = -1.0;
        if (delayDeclared is not null && !SumoConfiguration.TryParseTime(delayDeclared, out delay))
        {
            refused.Add(NotATime(MaxDepartDelayOption, delayDeclared));
        }

        if (refused.Count > 0)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} lets SUMO edit its own population in a way the truth record "
                + "cannot carry, or does not say what SUMO would do: "
                + string.Join("; ", refused.Select((problem, index) => $"({index + 1}) {problem}"))
                + ". SUMO has not been started."
                + (teleportRefused
                    ? " A teleport moves a vehicle straight to a lane further along its route, which would render "
                      + "as a body dragged across the map with a matching false velocity in the truth record. "
                      + "Disable each teleport trigger, or accept teleporting explicitly (AllowTeleporting; "
                      + "run_sumo_drive.py --allow-teleporting), which the run report then records."
                    : string.Empty));
        }

        bool teleporting = triggers.Any(trigger => trigger.Enabled);
        return new SumoDistributionEditCheck(actionDeclared, action, ignored, triggers,
                                             triggers.Count - optionTriggers, allowTeleporting && teleporting,
                                             offsetDeclared, randomDeclared, scaleDeclared, scale, typeScales,
                                             capDeclared, cap, delayDeclared, delay);
    }

    /// <summary>
    /// What was found, in one line: the collision action in force, the teleport triggers, the departures,
    /// the demand scale and the insertion limits.
    /// </summary>
    public override string ToString() =>
        $"collision.action '{CollisionActionInForce}'; "
        + (TeleportingEnabled ? "a teleport trigger ENABLED, accepted explicitly" : "no teleport trigger enabled")
        + "; departures as declared, from the seed; "
        + (Scale == 1.0 ? "demand as written" : $"demand SCALED by {Shown(Scale)}")
        + (TypeScales.Count > 0 ? " and per vehicle type" : string.Empty)
        + "; "
        + (MaxNumVehicles < 0 ? "no vehicle limit" : $"vehicles CAPPED at {MaxNumVehicles}")
        + "; "
        + (DiscardsLateInsertions
            ? $"a vehicle not inserted within {Shown(MaxDepartDelaySeconds)} s discarded"
            : "no vehicle discarded for a late insertion");

    /// <summary>The collision action in force, in the report's words.</summary>
    public string CollisionText
    {
        get
        {
            string source = CollisionActionDeclared is null
                ? $"{SumoCollisionHandling.ActionOption} not set, so SUMO's default '{SumoCollisionHandling.SumoDefaultAction}'"
                : $"{SumoCollisionHandling.ActionOption} '{CollisionActionDeclared}'";
            string permitted = $"a run may carry only {string.Join(" or ", PermittedCollisionActions)}";
            return AccidentsIgnored
                ? $"'none': {SumoCollisionHandling.IgnoreOption} is set, so SUMO registers no collision whatever "
                  + $"the action ({source}); {permitted}"
                : $"'{CollisionAction}' ({source}); {permitted}";
        }
    }

    /// <summary>The teleport triggers besides <c>time-to-teleport</c>, in the report's words.</summary>
    public string TeleportText =>
        (TeleportingEnabled ? "ENABLED, accepted explicitly: " : "off: ")
        + string.Join("; ", TeleportTriggers)
        + (_typeTriggers == 0 ? "; no vehicle type sets its own" : string.Empty);

    /// <summary>Whether a random offset moves the departures, in the report's words.</summary>
    public string DepartOffsetText =>
        "none: no departure moved by a random offset ("
        + (RandomDepartOffsetDeclared is null
            ? $"{RandomDepartOffsetOption} not set, so SUMO's default of 0 s"
            : $"{RandomDepartOffsetOption} '{RandomDepartOffsetDeclared}'")
        + ")";

    /// <summary>How SUMO is seeded, in the report's words.</summary>
    public string SeedingText =>
        "from the seed, so the traffic can be run again ("
        + (RandomDeclared is null ? $"{RandomOption} not set" : $"{RandomOption} '{RandomDeclared}'")
        + ")";

    /// <summary>The demand scale, the whole and each vehicle type's, in the report's words.</summary>
    public string ScaleText
    {
        get
        {
            string source = ScaleDeclared is null
                ? $"{ScaleOption} not set, so SUMO's default of 1"
                : $"{ScaleOption} '{ScaleDeclared}'";
            string whole = Scale == 1.0
                ? $"1, the demand as written ({source})"
                : $"SCALED by {Shown(Scale)}: SUMO discards or duplicates vehicles to match ({source})";
            return whole + "; "
                   + (TypeScales.Count == 0
                       ? "no vehicle type scales its own"
                       : "vehicle types SCALED by their own: "
                         + string.Join(", ", TypeScales.Select(type => $"vType '{type.TypeId}' {ScaleOption} '{type.Scale}'")));
        }
    }

    /// <summary>The cap on vehicles running at once, in the report's words.</summary>
    public string VehicleLimitText
    {
        get
        {
            string source = MaxNumVehiclesDeclared is null
                ? $"{MaxNumVehiclesOption} not set, so SUMO's default of -1"
                : $"{MaxNumVehiclesOption} '{MaxNumVehiclesDeclared}'";
            return MaxNumVehicles < 0
                ? $"none ({source})"
                : $"CAPPED at {MaxNumVehicles} running: SUMO delays an insertion that would exceed it ({source})";
        }
    }

    /// <summary>Whether and when SUMO discards a vehicle it has not inserted, in the report's words.</summary>
    public string DepartDelayText
    {
        get
        {
            string source = MaxDepartDelayDeclared is null
                ? $"{MaxDepartDelayOption} not set, so SUMO's default of -1 s"
                : $"{MaxDepartDelayOption} '{MaxDepartDelayDeclared}'";
            return DiscardsLateInsertions
                ? $"a vehicle not inserted within {Shown(MaxDepartDelaySeconds)} s of its departure is DISCARDED, "
                  + $"and recorded as not inserted ({source})"
                : $"never: a vehicle waits as long as it must to be inserted ({source})";
        }
    }

    /// <summary>
    /// The one value the configuration sets for an option under any of its names, or null; an option set
    /// more than once is refused, as SUMO refuses it.
    /// </summary>
    private static string? Single(SumoConfiguration configuration, List<string> refused, params string[] names)
    {
        IReadOnlyList<string> values = configuration.ValuesOf(names);
        if (values.Count > 1)
        {
            refused.Add($"it sets {names[0]} {values.Count} times ("
                        + string.Join(", ", values.Select(value => $"'{value}'"))
                        + "), and SUMO refuses an option set twice");
            return null;
        }

        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>Why a collision action is refused, as a clause naming where it came from.</summary>
    private static string RefusedCollision(string? declared, string action, string? ignoredBy)
    {
        string permitted = $"; a run may carry only {string.Join(" or ", PermittedCollisionActions)}, under which "
                           + "SUMO registers each collision and changes nothing about the traffic, as the scenario "
                           + "compiler writes";
        if (ignoredBy is not null)
        {
            return $"{SumoCollisionHandling.IgnoreOption} is '{ignoredBy}', under which SUMO skips the collision "
                   + "check whatever the action, so the run could not say whether any collision happened"
                   + permitted;
        }

        string source = declared is null
            ? $"it sets no {SumoCollisionHandling.ActionOption}, and SUMO's default is '{action}'"
            : $"{SumoCollisionHandling.ActionOption} is '{declared}'";
        return source + action switch
        {
            "teleport" => ", under which SUMO moves the collider to the next edge of its route, a jump the "
                          + "interpolation cannot connect",
            "remove" => ", under which SUMO takes both vehicles out, and they leave the population as though "
                        + "they had arrived",
            "none" => ", under which SUMO skips the collision check, so the run could not say whether any "
                      + "collision happened",
            "warn" => string.Empty,
            _ => ", which is not one of SUMO's four actions -- none, warn, teleport, remove -- and under which "
                 + "SUMO writes an error and runs on, moving every collider as teleport does",
        } + permitted;
    }

    /// <summary>A refusal for a value that is not a time SUMO reads.</summary>
    private static string NotATime(string option, string declared) =>
        $"{option} is '{declared}', which is not a time SUMO reads -- seconds, hh:mm:ss or dd:hh:mm:ss -- so "
        + "SUMO would refuse it too";

    /// <summary>A refusal for a value SUMO does not read as true or false.</summary>
    private static string NotABoolean(string option, string declared) =>
        $"{option} is '{declared}', which SUMO does not read as true or false" + RunsOnUnder("false");

    /// <summary>
    /// What SUMO does with a boolean, whole-number or number option it cannot read, measured: an error on
    /// its console, and the run goes on under the option's default, which is not what the file says.
    /// </summary>
    private static string RunsOnUnder(string sumoDefault) =>
        $": SUMO writes an error and runs on under its default of {sumoDefault}, which is not what the "
        + "configuration says";

    /// <summary>After how long a trigger teleports, for a refusal to finish a sentence with.</summary>
    private static string Waited(double seconds) =>
        SumoConfiguration.Milliseconds(seconds) == 0
            ? "as soon as it waits"
            : $"once it has waited {Shown(seconds)} s";

    /// <summary>A number as SUMO's <c>StringUtils::toDouble</c> reads one, finite.</summary>
    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static string Shown(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// A route or additional file, read as XML -- compressed where its name ends in <c>.gz</c>, as SUMO
    /// reads one -- refusing one that cannot be read or is not XML.
    /// </summary>
    private static XDocument ReadDefinitions(string file, string scenarioPath)
    {
        try
        {
            using Stream stream = File.OpenRead(file);
            using Stream content = file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(stream, CompressionMode.Decompress)
                : stream;
            return XDocument.Load(content);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException
                                               or XmlException or InvalidDataException)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} names {file}, which cannot be read as XML, so whether a vehicle "
                + "type it defines teleports or scales its own vehicles cannot be established: "
                + unreadable.Message, unreadable);
        }
    }
}
