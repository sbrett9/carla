using System.Security.Cryptography;

namespace CarlaNet.CoSim;

/// <summary>
/// Establishes whether a scenario is the one its compile lock binds -- the same configuration, route
/// file and network, compiled against the same catalogue and epoch -- and refuses a session where it
/// is not; or, where no lock sits beside the configuration, records that the scenario is uncompiled.
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
/// <para><b>No lock is not a refusal.</b> A scenario a generator wrote directly as SUMO files -- the
/// shipped Arapahoe scenario is one -- has no lock, and it runs. The report records it as uncompiled,
/// so a run of one can never be mistaken for a run of a compiled scenario.</para>
///
/// <para><b>What it cannot see.</b></para>
/// <list type="bullet">
/// <item><b>The catalogue's contents.</b> The catalogue's declared digest is compared, not recomputed:
/// a catalogue edited without re-digesting it passes.</item>
/// <item><b>Files the lock does not bind.</b> Additional files, and the supervision plan, which the
/// session does not run. An additional file named by a configuration whose digest matches is the one
/// the compiler named, but its bytes are not checked.</item>
/// <item><b>A file changed after the check.</b> It is taken once, when the session starts, just before
/// SUMO reads the files.</item>
/// <item><b>Whether the lock is the compiler's.</b> A lock rewritten to match edited files passes; the
/// check establishes that the files are the ones the lock describes, not who wrote the lock.</item>
/// </list>
/// </remarks>
public sealed class ScenarioLockCheck
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private ScenarioLockCheck(string expectedLockPath, ScenarioLock? locked, bool epochCompared)
    {
        ExpectedLockPath = expectedLockPath;
        Lock = locked;
        EpochCompared = epochCompared;
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
    /// Where the compile lock of a configuration is: beside it, named for it, <c>X.sumocfg</c> to
    /// <c>X.lock.json</c>.
    /// </summary>
    public static string LockPathFor(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        return System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(configurationPath), ".lock.json");
    }

    /// <summary>
    /// Check the scenario against its compile lock, or record that it has none.
    /// </summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <param name="catalogue">The catalogue the session loads.</param>
    /// <param name="epoch">The epoch the session declares, or null where it declares none.</param>
    /// <returns>What was found, for the run report.</returns>
    /// <exception cref="CoSimSessionRefusedException">
    /// A lock sits beside the configuration and cannot be read, or the files, the catalogue or the
    /// epoch disagree with it; every disagreement is named.
    /// </exception>
    public static ScenarioLockCheck Require(string scenarioPath, VehicleCatalogue catalogue,
                                            SolarEpoch? epoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        ArgumentNullException.ThrowIfNull(catalogue);

        string lockPath = LockPathFor(scenarioPath);
        if (!System.IO.File.Exists(lockPath))
        {
            return new ScenarioLockCheck(lockPath, null, epochCompared: false);
        }

        ScenarioLock locked = ScenarioLock.Read(lockPath);
        string lockDirectory = System.IO.Path.GetDirectoryName(lockPath)!;
        SumoConfiguration configuration = SumoConfiguration.Load(
            scenarioPath, "the files it runs cannot be compared with its compile lock");

        List<string> disagreements = [];

        // The configuration being run: the one the lock names, with the bytes the lock digests.
        string lockedConfig = System.IO.Path.GetFullPath(locked.Config.Path, lockDirectory);
        if (!string.Equals(lockedConfig, configuration.Path, PathComparison))
        {
            disagreements.Add($"the lock binds the configuration {lockedConfig}, not {configuration.Path}");
        }
        else
        {
            CompareDigest(disagreements, "configuration", configuration.Path, locked.Config.Sha256);
        }

        // The route files SUMO will load: exactly the one the lock names.
        string lockedRoutes = System.IO.Path.GetFullPath(locked.Routes.Path, lockDirectory);
        IReadOnlyList<string> routes = configuration.FilesOf("route-files", "routes", "r");
        if (routes.Count != 1 || !string.Equals(routes[0], lockedRoutes, PathComparison))
        {
            disagreements.Add("the configuration runs the route file(s) "
                              + (routes.Count == 0 ? "(none)" : string.Join(", ", routes))
                              + $" and the lock binds {lockedRoutes}");
        }
        else
        {
            CompareDigest(disagreements, "route file", lockedRoutes, locked.Routes.Sha256);
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
                + string.Join("; ", disagreements.Select((problem, index) => $"({index + 1}) {problem}"))
                + ". The compiler's output is byte-reproducible, so a file whose digest has moved was "
                + "changed after it was compiled, and the traffic it produces is not the traffic the lock "
                + "and the resolution report describe. SUMO has not been started. Recompile the scenario, "
                + "or give the session the catalogue and epoch it was compiled against.");
        }

        return new ScenarioLockCheck(lockPath, locked, epochCompared: epoch is not null);
    }

    /// <summary>What was found, in the report's words.</summary>
    public override string ToString()
    {
        if (Lock is not { } locked)
        {
            return $"none at {ExpectedLockPath}: an uncompiled scenario, nothing compared";
        }

        return $"{locked.ScenarioId}, compiled by {Shown(locked.Compiler)}; configuration, route file, "
               + "network and catalogue agree with it; "
               + (EpochCompared ? "so does the epoch" : "the epoch not compared, as the session declares none");
    }

    /// <summary>The routing release the lock records, in the report's words.</summary>
    public string RoutedByText =>
        Lock is not { } locked
            ? "not recorded: an uncompiled scenario"
            : $"{Shown(locked.RoutedByTool)} {Shown(locked.RoutedByRelease)} against the world converter "
              + $"'{Shown(locked.RoutedAgainstConverter)}' ({Shown(locked.RoutingAgreement)}"
              + (locked.RoutingMismatchAccepted == true ? ", accepted explicitly" : string.Empty) + ")";

    /// <summary>The world the lock records the scenario was compiled for, in the report's words.</summary>
    public string WorldText =>
        Lock is not { } locked
            ? "not recorded: an uncompiled scenario"
            : $"{Shown(locked.WorldPackage)}, map {Shown(locked.WorldMapName)}, network "
              + $"{Shown(locked.WorldNetworkFingerprint)}, OpenDRIVE {Shown(locked.WorldOpenDriveSha256)}, "
              + $"converted by {Shown(locked.WorldConverter)}";

    private static void CompareDigest(List<string> disagreements, string role, string path, string expected)
    {
        if (!System.IO.File.Exists(path))
        {
            disagreements.Add($"the {role} {path} is not there");
            return;
        }

        string actual;
        try
        {
            actual = Sha256Of(path);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            disagreements.Add($"the {role} {path} cannot be read: {unreadable.Message}");
            return;
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            disagreements.Add($"the {role} {path} digests as {actual} and the lock records {expected}");
        }
    }

    /// <summary>Lowercase hex SHA-256 of a file's bytes, as the compiler digests what it wrote.</summary>
    public static string Sha256Of(string path)
    {
        using FileStream stream = System.IO.File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Shown(string? value) => string.IsNullOrEmpty(value) ? "(not recorded)" : value;
}
