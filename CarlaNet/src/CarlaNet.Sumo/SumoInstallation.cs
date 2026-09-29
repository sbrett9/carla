using System.Diagnostics;

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

    /// <summary>The microsimulation's executable name, without the platform's extension.</summary>
    public const string SumoName = "sumo";

    /// <summary>
    /// The graphical build of the same microsimulation, without the platform's extension.
    /// </summary>
    public const string SumoGuiName = "sumo-gui";

    /// <summary>Where <c>CarlaSetup</c> stages the pinned toolchain, relative to the repository root.</summary>
    private const string StagedInstallation = "Build/sumo-install";

    /// <summary>
    /// Where SUMO's own build writes its binaries before <c>CarlaSetup</c> stages them. Checked
    /// after the staged copy, so a build interrupted before staging still resolves to the pinned
    /// release rather than falling through to an unrelated system-wide one.
    /// </summary>
    private const string SourceBuild = "Build/sumo-src";

    private static readonly object LocateGate = new();
    private static SumoInstallation? _located;
    private static bool _locateAttempted;
    private static string[] _searched = [];

    private readonly Func<string, string?> _probe;
    private readonly Dictionary<string, string?> _releases = [];

    private SumoInstallation(string home, string source)
        : this(home, source, ReadRelease)
    {
    }

    /// <summary>
    /// An installation whose executables' releases are read by <paramref name="probe"/>, handed an
    /// executable's full path, rather than by running it.
    /// </summary>
    /// <remarks>
    /// For a test that needs an installation whose binaries disagree about their release, which no
    /// real installation on the machine is guaranteed to have.
    /// </remarks>
    internal SumoInstallation(string home, string source, Func<string, string?> probe)
    {
        Home = home;
        Source = source;
        _probe = probe;
    }

    /// <summary>The installation root, holding <c>bin/</c> and, in a complete installation, <c>tools/</c>.</summary>
    public string Home { get; }

    /// <summary>
    /// Which rule matched: <c>explicit</c> for an installation a caller named (<see cref="At"/>), or
    /// the search rule -- <c>CARLANET_SUMO_HOME</c>, <c>staged</c>, <c>source-build</c>,
    /// <c>SUMO_HOME</c> or <c>PATH</c>. The path alone says what resolved but not why, which is the
    /// question asked when it is the wrong one.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// The directory holding <c>sumo</c>, <c>duarouter</c> and <c>netconvert</c>, and <c>sumo-gui</c>
    /// where it was built.
    /// </summary>
    public string BinaryDirectory => Path.Combine(Home, "bin");

    /// <summary>
    /// The directory holding SUMO's Python tools, including its own reference TraCI client. Present
    /// in a complete installation and absent from a bare binary directory, which is why the
    /// constants check that reads it skips rather than fails when it is missing.
    /// </summary>
    public string ToolsDirectory => Path.Combine(Home, "tools");

    /// <summary>The microsimulation binary, launched as a child process and spoken to over TraCI.</summary>
    public string Sumo => Executable(SumoName);

    /// <summary>
    /// The graphical build of the microsimulation, in the same directory as <see cref="Sumo"/>. It
    /// serves TraCI exactly as <c>sumo</c> does and draws what it simulates, so launched in place of
    /// <c>sumo</c> it shows the very simulation a client is stepping.
    /// </summary>
    /// <remarks>
    /// Not every installation has one: SUMO builds it only where the FOX toolkit was found, and the
    /// repository's setup scripts stage it for development. <see cref="HasSumoGui"/> says whether
    /// this one does.
    /// </remarks>
    public string SumoGui => Executable(SumoGuiName);

    /// <summary>Whether <see cref="SumoGui"/> is there to launch.</summary>
    public bool HasSumoGui => File.Exists(SumoGui);

    /// <summary>The route validator scenario authoring runs.</summary>
    public string Duarouter => Executable("duarouter");

    /// <summary>The network converter the world pipeline runs.</summary>
    public string Netconvert => Executable("netconvert");

    /// <summary>
    /// The SUMO release this installation reports, or <see langword="null"/> when <c>sumo</c> could
    /// not be run. Probed once, from <c>sumo --version</c>.
    /// </summary>
    public string? Release => ReleaseOf(SumoName);

    /// <summary>
    /// The release one of this installation's executables reports, or <see langword="null"/> when it
    /// could not be run. Probed once per executable, from its own <c>--version</c>.
    /// </summary>
    /// <param name="name">The executable, without the platform's extension: <see cref="SumoName"/>,
    /// <see cref="SumoGuiName"/>, <c>netconvert</c>.</param>
    /// <remarks>
    /// Asked of the binary that will actually run rather than of <c>sumo</c> on its behalf. Two
    /// executables in one directory are usually one build, but nothing makes them so -- a copy
    /// dropped in beside the others is another release that shares their path.
    /// </remarks>
    public string? ReleaseOf(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_releases)
        {
            if (!_releases.TryGetValue(name, out string? release))
            {
                release = _probe(Executable(name));
                _releases[name] = release;
            }

            return release;
        }
    }

    /// <summary>Full path to one of SUMO's executables in this installation.</summary>
    public string Executable(string name) =>
        Path.Combine(BinaryDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);

    /// <summary>
    /// The installation rooted at <paramref name="home"/>, named by the caller rather than searched
    /// for.
    /// </summary>
    /// <remarks>
    /// <para>The way to launch one particular SUMO whatever the environment holds. The search in
    /// <see cref="Locate"/> finds the repository's pinned build by walking upward from the running
    /// assemblies, and a hosted runtime loaded from an installed wheel has no repository above them:
    /// the walk finds nothing, <c>SUMO_HOME</c> decides, and on a machine with a system-wide SUMO that
    /// is a different release from the one that converted the world. A caller that knows which
    /// installation it wants names it here, and <see cref="Source"/> says so.</para>
    ///
    /// <para>Not cached and not recorded in <see cref="SearchedDirectories"/>: naming an installation
    /// is not a search, and it leaves what <see cref="Locate"/> resolves untouched.</para>
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="home"/> holds no <c>bin/sumo</c>, so there is nothing there to launch.
    /// </exception>
    public static SumoInstallation At(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        string full = Path.GetFullPath(home);
        string executable = Path.Combine(full, "bin", OperatingSystem.IsWindows() ? "sumo.exe" : "sumo");
        if (!File.Exists(executable))
        {
            throw new DirectoryNotFoundException(
                $"{full} is not a SUMO installation: there is no {executable} to launch. Name the "
                + "directory that holds SUMO's bin/ directory.");
        }

        return new SumoInstallation(full, "explicit");
    }

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
            return SumoRelease.FromToolOutput(output);
        }
        catch (Exception exception) when (exception is IOException
                                              or System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            return null;
        }
    }
}
