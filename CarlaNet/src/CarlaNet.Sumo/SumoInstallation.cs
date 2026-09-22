using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CarlaNet.Sumo;

/// <summary>
/// One SUMO installation on this machine, and which rule found it.
/// </summary>
/// <remarks>
/// <para>SUMO is not a NuGet package. Its executables live in an installation directory that SUMO's
/// own convention names with the <c>SUMO_HOME</c> environment variable, and something has to find
/// that directory before <c>sumo</c> can be launched.</para>
///
/// <para><b>The repository's own pinned build is preferred over <c>SUMO_HOME</c>, and that is
/// deliberate.</b> The identifiers in <see cref="TraCIConstants"/> were translated from one SUMO
/// release, and this machine already has two SUMO installations of different releases on it. A
/// protocol client survives that far better than a linked binding would -- a mismatch is reported
/// by the handshake rather than being undefined -- but "reported" still means a run that had to be
/// thrown away, so the default resolves to the release the client was written for. A distribution
/// recipient has no repository build at all, which is where <c>SUMO_HOME</c> takes over, and it is
/// how the launcher scripts already point at the bundled toolchain.</para>
///
/// <para>Every resolution records <see cref="Source"/>, because more than one SUMO can be installed
/// and the binaries give no hint of which one a process picked.</para>
/// </remarks>
public sealed class SumoInstallation
{
    /// <summary>
    /// Environment variable naming an installation root outright, ahead of every other rule. The
    /// escape hatch for a machine where neither the repository build nor <c>SUMO_HOME</c> is the
    /// one wanted.
    /// </summary>
    public const string OverrideVariable = "CARLANET_SUMO_HOME";

    /// <summary>SUMO's own convention for the root holding <c>bin/</c> and <c>tools/</c>.</summary>
    public const string HomeVariable = "SUMO_HOME";

    /// <summary>Where <c>CarlaSetup</c> stages the pinned toolchain, relative to the repository root.</summary>
    private const string StagedInstallation = "Build/sumo-install";

    /// <summary>
    /// Where SUMO's own build writes its binaries before <c>CarlaSetup</c> stages them. Checked
    /// after the staged copy, so a build interrupted before staging still resolves to the pinned
    /// release rather than falling through to an unrelated system-wide one.
    /// </summary>
    private const string SourceBuild = "Build/sumo-src";

    // `Eclipse SUMO sumo 1.27.0` -- the first line of any SUMO tool's --version output. A
    // development build appends a git description, which is not part of the release number.
    private static readonly Regex VersionLine = new(@"Eclipse SUMO \S+ v?(\d+(?:\.\d+)*)",
                                                    RegexOptions.Compiled);

    private static readonly object LocateGate = new();
    private static SumoInstallation? _located;
    private static bool _locateAttempted;
    private static string[] _searched = [];

    private string? _release;
    private bool _releaseProbed;

    private SumoInstallation(string home, string source)
    {
        Home = home;
        Source = source;
    }

    /// <summary>The installation root, holding <c>bin/</c> and, in a complete installation, <c>tools/</c>.</summary>
    public string Home { get; }

    /// <summary>
    /// Which rule matched: <c>CARLANET_SUMO_HOME</c>, <c>staged</c>, <c>source-build</c>,
    /// <c>SUMO_HOME</c> or <c>PATH</c>. The path alone says what resolved but not why, which is the
    /// question asked when it is the wrong one.
    /// </summary>
    public string Source { get; }

    /// <summary>The directory holding <c>sumo</c>, <c>duarouter</c> and <c>netconvert</c>.</summary>
    public string BinaryDirectory => Path.Combine(Home, "bin");

    /// <summary>
    /// The directory holding SUMO's Python tools, including its own reference TraCI client. Present
    /// in a complete installation and absent from a bare binary directory, which is why the
    /// constants check that reads it skips rather than fails when it is missing.
    /// </summary>
    public string ToolsDirectory => Path.Combine(Home, "tools");

    /// <summary>The microsimulation binary, launched as a child process and spoken to over TraCI.</summary>
    public string Sumo => Executable("sumo");

    /// <summary>The route validator scenario authoring runs.</summary>
    public string Duarouter => Executable("duarouter");

    /// <summary>The network converter the world pipeline runs.</summary>
    public string Netconvert => Executable("netconvert");

    /// <summary>
    /// The SUMO release this installation reports, or <see langword="null"/> when <c>sumo</c> could
    /// not be run. Probed once, from <c>sumo --version</c>.
    /// </summary>
    public string? Release
    {
        get
        {
            if (_releaseProbed)
            {
                return _release;
            }

            _releaseProbed = true;
            _release = ReadRelease(Sumo);
            return _release;
        }
    }

    /// <summary>Full path to one of SUMO's executables in this installation.</summary>
    public string Executable(string name) =>
        Path.Combine(BinaryDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);

    /// <summary>
    /// The installation this process will use, or <see langword="null"/> when none resolves.
    /// Resolved once and cached; <see cref="SearchedDirectories"/> then says where it looked.
    /// </summary>
    public static SumoInstallation? Locate()
    {
        lock (LocateGate)
        {
            if (_locateAttempted)
            {
                return _located;
            }

            _locateAttempted = true;
            List<string> searched = [];
            _located = Search(searched);
            _searched = [.. searched];
            return _located;
        }
    }

    /// <summary>
    /// The installation this process will use, or a <see cref="DirectoryNotFoundException"/> naming
    /// every directory that was tried.
    /// </summary>
    public static SumoInstallation LocateOrThrow() =>
        Locate() ?? throw new DirectoryNotFoundException(
            "No SUMO installation was found. Run CarlaSetup to stage one under "
            + $"{StagedInstallation}, set {HomeVariable} or {OverrideVariable} to an installation's "
            + "root, or put SUMO's bin directory on the executable search path. Looked in: "
            + (SearchedDirectories.Count == 0 ? "nothing was given" : string.Join(", ", SearchedDirectories)));

    /// <summary>Every directory <see cref="Locate"/> tried, in order. Empty until it has run once.</summary>
    public static IReadOnlyList<string> SearchedDirectories => _searched;

    private static SumoInstallation? Search(List<string> searched)
    {
        foreach ((string source, string? home) in Candidates())
        {
            if (string.IsNullOrWhiteSpace(home))
            {
                continue;
            }

            string full = Path.GetFullPath(home);
            searched.Add(full);
            string executable = Path.Combine(full, "bin",
                                             OperatingSystem.IsWindows() ? "sumo.exe" : "sumo");
            if (File.Exists(executable))
            {
                return new SumoInstallation(full, source);
            }
        }

        return null;
    }

    private static IEnumerable<(string Source, string? Home)> Candidates()
    {
        yield return (OverrideVariable, Environment.GetEnvironmentVariable(OverrideVariable));

        // Both repository locations hold the release the constant table was translated from.
        yield return ("staged", FindUpwards(StagedInstallation));
        yield return ("source-build", FindUpwards(SourceBuild));

        yield return (HomeVariable, Environment.GetEnvironmentVariable(HomeVariable));

        yield return ("PATH", FindOnPath());
    }

    /// <summary>
    /// Walk from the running assembly towards the filesystem root looking for a directory that
    /// holds <paramref name="relative"/>. The build output sits several levels below the repository
    /// root and deeper still inside a git worktree, so the depth cannot be hard-coded.
    /// </summary>
    private static string? FindUpwards(string relative)
    {
        foreach (string start in SearchRoots())
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, relative);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>
    /// Where the upward walk starts: the application's base directory, and this assembly's own.
    /// </summary>
    /// <remarks>
    /// Two starting points, and the second is not redundant. An application's base directory is the
    /// executable's, and <b>there is no executable when the runtime is hosted</b> -- loaded into
    /// another process, as it is when a Python orchestrator drives the bridge,
    /// <see cref="AppContext.BaseDirectory"/> is the empty string and constructing a
    /// <see cref="DirectoryInfo"/> from it raises. The assembly's own directory is under the
    /// repository in exactly that case, which is the case where the repository build is the
    /// installation wanted.
    /// </remarks>
    internal static IEnumerable<string> SearchRoots()
    {
        string applicationBase = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(applicationBase))
        {
            yield return applicationBase;
        }

        string assembly = Path.GetDirectoryName(typeof(SumoInstallation).Assembly.Location)
                          ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(assembly)
            && !string.Equals(Path.TrimEndingDirectorySeparator(assembly),
                              Path.TrimEndingDirectorySeparator(applicationBase),
                              StringComparison.OrdinalIgnoreCase))
        {
            yield return assembly;
        }
    }

    /// <summary>
    /// A <c>sumo</c> on the executable search path, reported as the installation root above the
    /// directory holding it.
    /// </summary>
    private static string? FindOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        string executable = OperatingSystem.IsWindows() ? "sumo.exe" : "sumo";
        foreach (string entry in path.Split(Path.PathSeparator))
        {
            if (entry.Length > 0 && File.Exists(Path.Combine(entry, executable)))
            {
                return Path.GetDirectoryName(Path.GetFullPath(entry));
            }
        }

        return null;
    }

    private static string? ReadRelease(string executable)
    {
        if (!File.Exists(executable))
        {
            return null;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Match match = VersionLine.Match(output);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception exception) when (exception is IOException
                                              or System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            return null;
        }
    }
}
