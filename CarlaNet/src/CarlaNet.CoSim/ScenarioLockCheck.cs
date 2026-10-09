using System.Security.Cryptography;
using System.Xml;
using CarlaNet.Map;

namespace CarlaNet.CoSim;

/// <summary>
/// Establishes whether a scenario is the one its compile lock binds -- the same configuration, route
/// file, network and additional file, compiled against the same catalogue and epoch, with the
/// supervision plan compiled against those files -- and refuses a session where it is not; or, where no
/// lock sits beside the configuration, records that the scenario is uncompiled.
/// </summary>
/// <remarks>
/// <para><b>Why a session checks it.</b> The lock is the scenario compiler's statement of what it
/// produced: the digests of the files it wrote, the catalogue whose vehicle types it bound, the epoch
/// it resolved every civil instant against, and the SUMO release that routed the demand. The
/// compiler's output is byte-reproducible, and the repository stores these files exactly as written
/// (<c>.gitattributes</c> under <c>Import/</c>), so a file whose digest has moved is a file somebody
/// changed after it was compiled -- a hand edit, a merge, another compile -- and the traffic it
/// produces is not the traffic the lock and its resolution report describe.</para>
///
/// <para><b>What is compared.</b></para>
/// <list type="bullet">
/// <item><b>The configuration</b> the session runs: it must be the one the lock names, and its bytes
/// must be the ones the lock digests.</item>
/// <item><b>The route files</b> the configuration names, read as SUMO reads them: exactly the one the
/// lock names, with the lock's digest.</item>
/// <item><b>The network</b> the configuration names: the one the lock names, with the lock's digest.
/// Whether that network is the world package's is <see cref="ScenarioNetworkCheck"/>'s, made
/// before this one.</item>
/// <item><b>The additional files</b> the configuration names, read as SUMO reads them: exactly the one
/// the lock names, with its digest, or none where the lock names none.</item>
/// <item><b>The supervision plan</b>, where the lock names one: <c>&lt;stem&gt;.supervision.json</c>
/// beside the configuration, with the lock's digest.</item>
/// <item><b>The catalogue</b>: the digest the catalogue the session loads declares, against the one
/// the lock records.</item>
/// <item><b>The epoch</b>, where the session declares one: <see cref="SolarEpoch.Digest"/> of it,
/// against the lock's <c>epoch_block_sha256</c>, which the compiler computes with the same function.
/// A session that declares no epoch binds no sun and derives no civil instant, so there is nothing to
/// disagree with, and the report says it was not compared.</item>
/// </list>
/// <para>Every disagreement is named in one refusal. The routing release and the lock's world
/// identity are recorded on the run report, not compared: the world is compared by the checks that
/// read the package and the loaded world.</para>
///
/// <para><b>The plan is bound to the files both ways.</b> Once the files agree with the lock, the plan
/// is read (<see cref="SupervisionPlan.Read"/>, which refuses a plan it cannot bind) and its own
/// digests are compared with the files the session runs: the route file's, the configuration's and the
/// additional file's SHA-256, or none where it was compiled against none, the network's canonical
/// fingerprint, and its vocabulary's digest against the lock's. A plan compiled against another
/// generation of the files would resolve some of its ids and not others (<c>06</c> §8.1), and nothing
/// in a run would show it. The plan is then <see cref="Plan"/>, for the session to bind.</para>
///
/// <para><b>No lock is not a refusal.</b> A scenario a generator wrote directly as SUMO files has no
/// lock, and it runs. The report records it as uncompiled, so a run of one can never be mistaken for a
/// run of a compiled scenario. Its supervision is not read: a <c>.supervision.json</c> beside an
/// uncompiled scenario is nothing a lock binds, and may be a legacy sidecar of the same name.</para>
///
/// <para><b>A lock that names no plan is not a refusal either.</b> Every lock the compiler writes names
/// one, since every compile writes a plan; a lock without one binds no supervision, and the run binds
/// none and says so. A lock that names a plan which is not there is refused: supervision travels in
/// the plan alone (06 D6.1), and a run without it would bind none of the rows its author declared.</para>
///
/// <para><b>A lock whose compile skipped its SUMO-only run is a refusal, unless the run accepts it.</b>
/// The compiler runs the files it writes in SUMO alone over the scenario's whole span and refuses a
/// scenario in which a vehicle the supervision plan names never enters the simulation (its check 59);
/// <c>--skip-dry-run</c> skips that, and the lock records <c>ran: false</c> with the reason
/// (<see cref="LockedDryRun"/>). A lock with no <c>dry_run</c> block at all was written before the
/// compiler ran the check, and is treated the same. Once the files, the catalogue, the epoch and the
/// plan agree with the lock, such a lock is refused naming the scenario and the lock's reason, unless
/// <see cref="SumoDriveSessionOptions.AcceptSkippedDryRun"/>: a run that started would find the same
/// fault only when SUMO dropped the vehicle, hours of rendering in. Accepted or not, the report says
/// what the lock records of the run (<see cref="DryRunText"/>).</para>
///
/// <para><b>What it cannot see.</b></para>
/// <list type="bullet">
/// <item><b>The catalogue's contents.</b> The catalogue's declared digest is compared, not recomputed:
/// a catalogue edited without re-digesting it passes.</item>
/// <item><b>A file changed after the check.</b> It is taken once, when the session starts, just before
/// SUMO reads the files.</item>
/// <item><b>Whether the lock is the compiler's.</b> A lock rewritten to match edited files passes; the
/// check establishes that the files are the ones the lock describes, not who wrote the lock. A plan
/// edited with its vocabulary's digest recomputed and the lock rewritten to match passes too.</item>
/// </list>
/// </remarks>
public sealed class ScenarioLockCheck
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The names SUMO takes its additional-files option by in a configuration: the option, its synonym
    /// and its one-letter abbreviation.
    /// </summary>
    private static readonly string[] AdditionalOptionNames = ["additional-files", "additional", "a"];

    private ScenarioLockCheck(string expectedLockPath, ScenarioLock? locked, bool epochCompared,
                              SupervisionPlan? plan, bool skippedDryRunAccepted)
    {
        ExpectedLockPath = expectedLockPath;
        Lock = locked;
        EpochCompared = epochCompared;
        Plan = plan;
        SkippedDryRunAccepted = skippedDryRunAccepted;
    }

    /// <summary>Where the session looked for the lock: <c>&lt;stem&gt;.lock.json</c> beside the configuration.</summary>
    public string ExpectedLockPath { get; }

    /// <summary>The lock, where there was one; null for an uncompiled scenario.</summary>
    public ScenarioLock? Lock { get; }

    /// <summary>Whether a lock sat beside the configuration, so the scenario is a compiled one.</summary>
    public bool Compiled => Lock is not null;

    /// <summary>Whether the epoch was compared: a lock was found and the session declares an epoch.</summary>
    public bool EpochCompared { get; }

    /// <summary>
    /// The supervision plan the lock binds, read and bound to the files the session runs: every row the
    /// run may bind. Null for an uncompiled scenario and for a lock that names no plan.
    /// </summary>
    public SupervisionPlan? Plan { get; }

    /// <summary>
    /// Whether the compiler ran the scenario in SUMO alone before writing it, as the lock records
    /// (<see cref="ScenarioLock.DryRun"/>). False for an uncompiled scenario, for a lock that says the
    /// run was skipped and for one written before the compiler ran the check.
    /// </summary>
    public bool DryRunRan => Lock?.DryRun is { Ran: true };

    /// <summary>
    /// Whether the lock's compile skipped its SUMO-only run, or records none, and the run accepted that
    /// explicitly. False where the run ran, since there was nothing to accept.
    /// </summary>
    public bool SkippedDryRunAccepted { get; }

    /// <summary>
    /// Where the compile lock of a configuration is: beside it, named for it, <c>X.sumocfg</c> to
    /// <c>X.lock.json</c>.
    /// </summary>
    public static string LockPathFor(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        return System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(configurationPath), ".lock.json");
    }

    /// <summary>
    /// Where a session reads a compiled scenario's supervision plan: beside its configuration, named for
    /// it, <c>X.sumocfg</c> to <c>X.supervision.json</c>, as the compiler writes it.
    /// </summary>
    public static string PlanPathFor(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        return System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(configurationPath), ".supervision.json");
    }

    /// <summary>
    /// Check the scenario against its compile lock, and its supervision plan against both, or record
    /// that it has no lock. A lock whose compile skipped its SUMO-only run is refused.
    /// </summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <param name="catalogue">The catalogue the session loads.</param>
    /// <param name="epoch">The epoch the session declares, or null where it declares none.</param>
    /// <returns>What was found, for the run report, with the plan for the session to bind.</returns>
    /// <exception cref="CoSimSessionRefusedException">
    /// A lock sits beside the configuration and cannot be read; the files, the catalogue or the epoch
    /// disagree with it; the plan it names is not there, cannot be bound, or was compiled against other
    /// files; or the lock says the compile skipped its SUMO-only run, or records none. Every
    /// disagreement of one kind is named in one refusal.
    /// </exception>
    public static ScenarioLockCheck Require(string scenarioPath, VehicleCatalogue catalogue,
                                            SolarEpoch? epoch) =>
        Require(scenarioPath, catalogue, epoch, acceptSkippedDryRun: false);

    /// <summary>
    /// Check the scenario against its compile lock, and its supervision plan against both, or record
    /// that it has no lock.
    /// </summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <param name="catalogue">The catalogue the session loads.</param>
    /// <param name="epoch">The epoch the session declares, or null where it declares none.</param>
    /// <param name="acceptSkippedDryRun">
    /// The run accepted explicitly a lock whose compile skipped its SUMO-only run, or records none
    /// (<see cref="SumoDriveSessionOptions.AcceptSkippedDryRun"/>).
    /// </param>
    /// <returns>What was found, for the run report, with the plan for the session to bind.</returns>
    /// <exception cref="CoSimSessionRefusedException">
    /// A lock sits beside the configuration and cannot be read; the files, the catalogue or the epoch
    /// disagree with it; the plan it names is not there, cannot be bound, or was compiled against other
    /// files; or the lock says the compile skipped its SUMO-only run, or records none, and
    /// <paramref name="acceptSkippedDryRun"/> is not set. Every disagreement of one kind is named in one
    /// refusal.
    /// </exception>
    public static ScenarioLockCheck Require(string scenarioPath, VehicleCatalogue catalogue,
                                            SolarEpoch? epoch, bool acceptSkippedDryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        ArgumentNullException.ThrowIfNull(catalogue);

        string lockPath = LockPathFor(scenarioPath);
        if (!System.IO.File.Exists(lockPath))
        {
            return new ScenarioLockCheck(lockPath, null, epochCompared: false, plan: null,
                                         skippedDryRunAccepted: false);
        }

        ScenarioLock locked = ScenarioLock.Read(lockPath);
        string lockDirectory = System.IO.Path.GetDirectoryName(lockPath)!;
        SumoConfiguration configuration = SumoConfiguration.Load(
            scenarioPath, "the files it runs cannot be compared with its compile lock");

        List<string> disagreements = [];

        // The configuration being run: the one the lock names, with the bytes the lock digests.
        string? configDigest = null;
        string lockedConfig = System.IO.Path.GetFullPath(locked.Config.Path, lockDirectory);
        if (!string.Equals(lockedConfig, configuration.Path, PathComparison))
        {
            disagreements.Add($"the lock binds the configuration {lockedConfig}, not {configuration.Path}");
        }
        else
        {
            configDigest = CompareDigest(disagreements, "configuration", configuration.Path, locked.Config.Sha256);
        }

        // The route files SUMO will load: exactly the one the lock names.
        string? routesDigest = null;
        string lockedRoutes = System.IO.Path.GetFullPath(locked.Routes.Path, lockDirectory);
        IReadOnlyList<string> routes = configuration.FilesOf("route-files", "routes", "r");
        if (routes.Count != 1 || !string.Equals(routes[0], lockedRoutes, PathComparison))
        {
            disagreements.Add("the configuration runs the route file(s) " + Listed(routes)
                              + $" and the lock binds {lockedRoutes}");
        }
        else
        {
            routesDigest = CompareDigest(disagreements, "route file", lockedRoutes, locked.Routes.Sha256);
        }

        // The network SUMO will load: the one the lock names.
        string lockedNetwork = System.IO.Path.GetFullPath(locked.Network.Path, lockDirectory);
        string network = ScenarioNetworkCheck.NetworkFileOf(configuration, scenarioPath);
        if (!string.Equals(network, lockedNetwork, PathComparison))
        {
            disagreements.Add($"the configuration runs the network {network} and the lock binds {lockedNetwork}");
        }
        else
        {
            CompareDigest(disagreements, "network", lockedNetwork, locked.Network.Sha256);
        }

        // The additional files SUMO will load: the one the lock names, or none where it names none.
        string? additionalDigest = null;
        IReadOnlyList<string> additional = configuration.FilesOf(AdditionalOptionNames);
        if (locked.Additional is { } lockedAdditionalFile)
        {
            string lockedAdditional = System.IO.Path.GetFullPath(lockedAdditionalFile.Path, lockDirectory);
            if (additional.Count != 1 || !string.Equals(additional[0], lockedAdditional, PathComparison))
            {
                disagreements.Add("the configuration runs the additional file(s) " + Listed(additional)
                                  + $" and the lock binds {lockedAdditional}");
            }
            else
            {
                additionalDigest = CompareDigest(disagreements, "additional file", lockedAdditional,
                                                 lockedAdditionalFile.Sha256);
            }
        }
        else if (additional.Count > 0)
        {
            disagreements.Add("the configuration runs the additional file(s) " + Listed(additional)
                              + " and the lock binds none");
        }

        // The supervision plan, where the lock names one: beside the configuration, with its digest.
        string planPath = PlanPathFor(scenarioPath);
        if (locked.Supervision is { } lockedPlanFile)
        {
            string lockedPlan = System.IO.Path.GetFullPath(lockedPlanFile.Path, lockDirectory);
            if (!string.Equals(lockedPlan, planPath, PathComparison))
            {
                disagreements.Add($"the lock binds the supervision plan {lockedPlan}, and a session reads the "
                                  + $"plan beside its configuration, at {planPath}");
            }
            else if (!System.IO.File.Exists(planPath))
            {
                disagreements.Add($"the lock binds the supervision plan {planPath} and it is not there; a "
                                  + "compiled scenario's supervision travels in its plan alone, so a run "
                                  + "without it would bind none of the rows its author declared");
            }
            else
            {
                CompareDigest(disagreements, "supervision plan", planPath, lockedPlanFile.Sha256);
            }
        }

        if (!string.Equals(catalogue.CatalogueDigest, locked.CatalogueDigest, StringComparison.Ordinal))
        {
            disagreements.Add($"the catalogue the session loads ('{catalogue.CatalogueId}') declares digest "
                              + $"{Shown(catalogue.CatalogueDigest)} and the scenario was compiled against "
                              + $"{Shown(locked.CatalogueDigest)}"
                              + (locked.CatalogueId is { } id ? $" ('{id}')" : string.Empty)
                              + ", so its vehicle types may name bodies measured differently or not at all");
        }

        if (epoch is not null && !string.Equals(epoch.Digest, locked.EpochDigest, StringComparison.Ordinal))
        {
            disagreements.Add($"the session's epoch ({epoch}) digests as {epoch.Digest} and the scenario "
                              + $"was compiled against the epoch {locked.EpochDigest}, so every civil "
                              + "instant its resolution report states was resolved against another t = 0");
        }

        if (disagreements.Count > 0)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} is not the one its compile lock {lockPath} binds: "
                + Numbered(disagreements)
                + ". The compiler's output is byte-reproducible, so a file whose digest has moved was "
                + "changed after it was compiled, and the traffic it produces is not the traffic the lock "
                + "and the resolution report describe. SUMO has not been started. Recompile the scenario, "
                + "or give the session the catalogue and epoch it was compiled against.");
        }

        SupervisionPlan? plan = null;
        if (locked.Supervision is { } bound)
        {
            plan = SupervisionPlan.Read(planPath);
            List<string> unbound = PlanDisagreements(plan, locked, bound, configuration.Path, configDigest!,
                                                     lockedRoutes, routesDigest!, network, additional,
                                                     additionalDigest);
            if (unbound.Count > 0)
            {
                throw new CoSimSessionRefusedException(
                    $"The supervision plan {planPath} was not compiled against the files the scenario "
                    + $"{scenarioPath} runs: " + Numbered(unbound)
                    + ". A plan is bound to the files it was compiled against, so one compiled against "
                    + "another generation of them would resolve some of its ids and not others, and nothing "
                    + "in the run would show it. SUMO has not been started. Recompile the scenario.");
            }
        }

        // Whether the compiler ran these files in SUMO alone and saw every planned vehicle enter. A
        // compile that skipped that, or one from before the compiler did it, reaches here unchecked, and
        // the run must say it knows.
        bool dryRunSkipped = locked.DryRun is not { Ran: true };
        if (dryRunSkipped && !acceptSkippedDryRun)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} ('{locked.ScenarioId}') was compiled without its SUMO-only "
                + $"run: its compile lock {lockPath} "
                + (locked.DryRun is { } skipped
                    ? "records that the run was skipped"
                      + (string.IsNullOrEmpty(skipped.Reason) ? string.Empty : $" ({skipped.Reason})")
                    : "records no dry_run block, so it was written before the compiler ran one")
                + ". That run is what finds a vehicle the supervision plan names that never enters the "
                + "simulation -- discarded after waiting max-depart-delay at its entrance, or still waiting "
                + "when the scenario ends -- and a capture that starts without it finds the same fault only "
                + "when SUMO drops the vehicle, hours of rendering in. SUMO has not been started. Recompile "
                + "the scenario without --skip-dry-run, or accept the skipped run explicitly "
                + "(AcceptSkippedDryRun; run_sumo_drive.py --accept-skipped-dry-run; run_capture "
                + "scenario.accept_skipped_dry_run), which the run report then records.");
        }

        return new ScenarioLockCheck(lockPath, locked, epochCompared: epoch is not null, plan,
                                     skippedDryRunAccepted: dryRunSkipped);
    }

    /// <summary>What was found, in the report's words.</summary>
    public override string ToString()
    {
        if (Lock is not { } locked)
        {
            return $"none at {ExpectedLockPath}: an uncompiled scenario, nothing compared";
        }

        List<string> agreeing = ["configuration", "route file", "network"];
        if (locked.Additional is not null)
        {
            agreeing.Add("additional file");
        }

        if (Plan is not null)
        {
            agreeing.Add("supervision plan");
        }

        agreeing.Add("catalogue");
        return $"{locked.ScenarioId}, compiled by {Shown(locked.Compiler)}; "
               + string.Join(", ", agreeing.SkipLast(1)) + $" and {agreeing[^1]} agree with it; "
               + (EpochCompared ? "so does the epoch" : "the epoch not compared, as the session declares none");
    }

    /// <summary>The supervision plan the run binds, in the report's words.</summary>
    public string PlanText =>
        Lock is null
            ? "not read: an uncompiled scenario binds no supervision plan"
            : Plan is not { } plan
                ? "none: the lock names no supervision plan, so the run binds no supervision"
                : $"{plan}; compiled against the files the run loads";

    /// <summary>
    /// The compiler's SUMO-only run of the scenario, as the lock records it, in the report's words: its
    /// release and counts where it ran; where it was skipped or the lock records none, that the run
    /// accepted it explicitly, since no session starts otherwise.
    /// </summary>
    public string DryRunText =>
        Lock is not { } locked
            ? "not recorded: an uncompiled scenario"
            : locked.DryRun is not { } dryRun
                ? "NOT RECORDED: the lock was written before the compiler ran one, accepted explicitly"
                : dryRun.Ran
                    ? dryRun.ToString()
                    : $"SKIPPED at the compile ({Shown(dryRun.Reason)}), accepted explicitly";

    /// <summary>The routing release the lock records, in the report's words.</summary>
    public string RoutedByText =>
        Lock is not { } locked
            ? "not recorded: an uncompiled scenario"
            : $"{Shown(locked.RoutedByTool)} {Shown(locked.RoutedByRelease)} against the world converter "
              + $"'{Shown(locked.RoutedAgainstConverter)}' ({Shown(locked.RoutingAgreement)}"
              + (locked.RoutingMismatchAccepted == true ? ", accepted explicitly" : string.Empty) + ")";

    /// <summary>
    /// The SUMO processing options the lock records the compiler wrote, in the report's words. The
    /// configuration's digest already binds them; they are restated so a run report says what the
    /// scenario was compiled to do without opening its files.
    /// </summary>
    public string ProcessingText =>
        Lock is not { } locked
            ? "not recorded: an uncompiled scenario"
            : $"{TeleportingCheck.OptionName} '{Shown(locked.TimeToTeleport)}', "
              + $"{SumoLaneChangeDuration.OptionName} '{Shown(locked.LaneChangeDuration)}'";

    /// <summary>The world the lock records the scenario was compiled for, in the report's words.</summary>
    public string WorldText =>
        Lock is not { } locked
            ? "not recorded: an uncompiled scenario"
            : $"{Shown(locked.WorldPackage)}, map {Shown(locked.WorldMapName)}, network "
              + $"{Shown(locked.WorldNetworkFingerprint)}, OpenDRIVE {Shown(locked.WorldOpenDriveSha256)}, "
              + $"converted by {Shown(locked.WorldConverter)}";

    /// <summary>
    /// Every way the plan's own digests disagree with the files the session runs, which already agree
    /// with the lock: so each digest here is the file's as the lock binds it.
    /// </summary>
    private static List<string> PlanDisagreements(SupervisionPlan plan, ScenarioLock locked, LockedFile bound,
                                                  string configuration, string configDigest,
                                                  string routes, string routesDigest, string network,
                                                  IReadOnlyList<string> additional, string? additionalDigest)
    {
        List<string> unbound = [];

        // The bytes read are the bytes the lock digests: a plan changed between the two reads is not.
        if (!string.Equals(plan.Sha256, bound.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            unbound.Add($"the plan read digests as {plan.Sha256} and the lock records {bound.Sha256}");
        }

        if (locked.ScenarioId.Length > 0 && !string.Equals(plan.ScenarioId, locked.ScenarioId, StringComparison.Ordinal))
        {
            unbound.Add($"it is the plan of the scenario '{plan.ScenarioId}' and the lock binds "
                        + $"'{locked.ScenarioId}'");
        }

        ComparePlanDigest(unbound, "configuration", configuration, plan.ConfigDigest, configDigest);
        ComparePlanDigest(unbound, "route file", routes, plan.RoutesDigest, routesDigest);

        string? fingerprint = FingerprintOf(unbound, network);
        if (fingerprint is not null && !string.Equals(fingerprint, plan.NetworkDigest, StringComparison.Ordinal))
        {
            unbound.Add($"it was compiled against the network fingerprint {plan.NetworkDigest} and the "
                        + $"network {network} fingerprints as {fingerprint}");
        }

        if (plan.AdditionalDigest is null)
        {
            if (additional.Count > 0)
            {
                unbound.Add("it was compiled against no additional file and the configuration runs "
                            + Listed(additional));
            }
        }
        else if (additional.Count == 0)
        {
            unbound.Add($"it was compiled against the additional file digest {plan.AdditionalDigest} and the "
                        + "configuration runs none");
        }
        else
        {
            // One, the lock's: the files agree with the lock, which names none where none is run.
            ComparePlanDigest(unbound, "additional file", additional[0], plan.AdditionalDigest, additionalDigest!);
        }

        if (locked.VocabularyDigest is { } vocabulary
            && !string.Equals(plan.Vocabulary.Digest, vocabulary, StringComparison.Ordinal))
        {
            unbound.Add($"it carries the vocabulary digest {plan.Vocabulary.Digest} and the lock records "
                        + vocabulary);
        }

        return unbound;
    }

    private static void ComparePlanDigest(List<string> unbound, string role, string path, string compiled,
                                          string actual)
    {
        if (!string.Equals(compiled, actual, StringComparison.OrdinalIgnoreCase))
        {
            unbound.Add($"it was compiled against the {role} digest {compiled} and the {role} {path} digests "
                        + $"as {actual}");
        }
    }

    /// <summary>The canonical fingerprint of the network, or a disagreement saying why there is none.</summary>
    private static string? FingerprintOf(List<string> unbound, string network)
    {
        try
        {
            return NetworkFingerprint.ComputeFile(network);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException
                                               or XmlException or InvalidDataException)
        {
            unbound.Add($"the network {network} cannot be read as a SUMO network to fingerprint: "
                        + unreadable.Message);
            return null;
        }
    }

    /// <summary>
    /// Compare a file's bytes with the digest the lock records for it, adding a disagreement where they
    /// differ or the file cannot be read; the file's digest, or null where it has none.
    /// </summary>
    private static string? CompareDigest(List<string> disagreements, string role, string path, string expected)
    {
        if (!System.IO.File.Exists(path))
        {
            disagreements.Add($"the {role} {path} is not there");
            return null;
        }

        string actual;
        try
        {
            actual = Sha256Of(path);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            disagreements.Add($"the {role} {path} cannot be read: {unreadable.Message}");
            return null;
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            disagreements.Add($"the {role} {path} digests as {actual} and the lock records {expected}");
        }

        return actual;
    }

    /// <summary>Lowercase hex SHA-256 of a file's bytes, as the compiler digests what it wrote.</summary>
    public static string Sha256Of(string path)
    {
        using FileStream stream = System.IO.File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Listed(IReadOnlyList<string> files) =>
        files.Count == 0 ? "(none)" : string.Join(", ", files);

    private static string Numbered(List<string> problems) =>
        string.Join("; ", problems.Select((problem, index) => $"({index + 1}) {problem}"));

    private static string Shown(string? value) => string.IsNullOrEmpty(value) ? "(not recorded)" : value;
}
