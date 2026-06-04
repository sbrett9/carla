// OSM → OpenDRIVE conversion by shelling out to the native SUMO `netconvert`
// executable (SUMO 1.27). netconvert is a standalone CLI: it reads a .osm file
// and writes a .xodr file. We run it as a subprocess.
//
// The default flag set mirrors CARLA's own osm2odr (carla-simulator/sumo
// OSM2ODRSettings): default lane width 3.35 m, default sidewalk width 2.80 m,
// proj string "+proj=tmerc", centred map, generated traffic lights.
//
// CRITICAL: PROJ needs its proj.db data file. When a proj.db directory is
// supplied we set PROJ_LIB and PROJ_DATA on the child process so libproj can
// find it; otherwise reprojection fails at runtime.
using System.Diagnostics;
using System.Text;

namespace CarlaNet.Map;

/// <summary>
/// Options controlling the OSM→OpenDRIVE conversion performed by <see cref="OsmConverter"/>.
/// Defaults reproduce CARLA's osm2odr behaviour. The single source of truth for the
/// netconvert flag list is <see cref="OsmConverter.BuildArguments"/>.
/// </summary>
public sealed record OsmConversionOptions
{
    /// <summary>Explicit path to the netconvert executable. When null, discovery is used
    /// (CARLA_NETCONVERT env var, then well-known relative paths). See
    /// <see cref="OsmConverter.ResolveNetconvertPath"/>.</summary>
    public string? NetconvertPath { get; init; }

    /// <summary>Directory containing PROJ's <c>proj.db</c>. When set it is exported to the
    /// child process as both <c>PROJ_LIB</c> and <c>PROJ_DATA</c>. When null the env var
    /// <c>PROJ_LIB</c> (if already present) is left untouched.</summary>
    public string? ProjDataDirectory { get; init; }

    /// <summary>Default lane width in metres (netconvert <c>--default.lanewidth</c>).</summary>
    public double DefaultLaneWidth { get; init; } = 3.35;

    /// <summary>Default sidewalk width in metres (netconvert <c>--default.sidewalk-width</c>).</summary>
    public double DefaultSidewalkWidth { get; init; } = 2.80;

    /// <summary>PROJ string passed to netconvert (<c>--proj</c>).</summary>
    public string ProjString { get; init; } = "+proj=tmerc";

    /// <summary>Guess and emit traffic lights (<c>--tls.guess</c>).</summary>
    public bool GenerateTrafficLights { get; init; } = true;

    /// <summary>Centre the generated map about the origin (netconvert auto-normalizes so the
    /// map's min corner sits at 0,0). Ignored when <see cref="OriginLatitude"/> /
    /// <see cref="OriginLongitude"/> are set (a pinned origin forces normalization OFF).</summary>
    public bool CenterMap { get; init; } = true;

    /// <summary>
    /// Optional georeferenced origin latitude (WGS84 degrees). When BOTH this and
    /// <see cref="OriginLongitude"/> are set, the converter pins the projection so this exact
    /// lat/lon maps to <b>(0,0)</b> in the OpenDRIVE / world frame — regardless of where the
    /// OSM bounding box sits — and forces offset normalization OFF so the origin is not
    /// shifted. This lets callers choose a semantic origin (e.g. a landmark such as a
    /// stadium's home plate) without having to centre their OSM extract on it. Overrides
    /// <see cref="ProjString"/> and <see cref="CenterMap"/> when set.
    /// </summary>
    public double? OriginLatitude { get; init; }

    /// <summary>Optional georeferenced origin longitude (WGS84 degrees). See
    /// <see cref="OriginLatitude"/>. Both must be set for origin pinning to take effect.</summary>
    public double? OriginLongitude { get; init; }

    /// <summary>Escape hatch: extra raw netconvert arguments appended verbatim, for tuning
    /// without code changes. Each entry is passed as a single argument token.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>
/// Converts OpenStreetMap data to OpenDRIVE (.xodr) by invoking the native SUMO
/// <c>netconvert</c> executable as a subprocess.
/// </summary>
public sealed class OsmConverter
{
    private readonly OsmConversionOptions _options;

    public OsmConverter(OsmConversionOptions? options = null)
        => _options = options ?? new OsmConversionOptions();

    /// <summary>
    /// Convert an .osm file on disk to OpenDRIVE and return the .xodr text.
    /// </summary>
    public async Task<string> ConvertFileAsync(string osmPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(osmPath))
            throw new ArgumentException("OSM path must be provided.", nameof(osmPath));
        if (!File.Exists(osmPath))
            throw new FileNotFoundException("OSM file not found.", osmPath);

        var xodrPath = Path.Combine(Path.GetTempPath(),
            $"carlanet_osm_{Guid.NewGuid():N}.xodr");
        try
        {
            await RunNetconvertAsync(osmPath, xodrPath, ct).ConfigureAwait(false);
            return await File.ReadAllTextAsync(xodrPath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(xodrPath);
        }
    }

    /// <summary>
    /// Convert raw OSM XML text to OpenDRIVE and return the .xodr text. The OSM is first
    /// written to a temporary file (netconvert only reads files).
    /// </summary>
    public async Task<string> ConvertTextAsync(string osmXml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(osmXml))
            throw new ArgumentException("OSM XML must be provided.", nameof(osmXml));

        var osmPath = Path.Combine(Path.GetTempPath(),
            $"carlanet_osm_{Guid.NewGuid():N}.osm");
        try
        {
            await File.WriteAllTextAsync(osmPath, osmXml, ct).ConfigureAwait(false);
            return await ConvertFileAsync(osmPath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(osmPath);
        }
    }

    // ── netconvert invocation ────────────────────────────────────────────────

    private async Task RunNetconvertAsync(string osmPath, string xodrPath, CancellationToken ct)
    {
        var exe = ResolveNetconvertPath();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(osmPath, xodrPath))
            psi.ArgumentList.Add(arg);

        // PROJ data discovery: libproj needs proj.db to reproject.
        if (!string.IsNullOrWhiteSpace(_options.ProjDataDirectory))
        {
            psi.Environment["PROJ_LIB"] = _options.ProjDataDirectory!;
            psi.Environment["PROJ_DATA"] = _options.ProjDataDirectory!;
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start netconvert at '{exe}'.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"netconvert exited with code {process.ExitCode}.\n" +
                $"Command: {exe} {string.Join(' ', psi.ArgumentList)}\n" +
                $"stderr:\n{stderr}\nstdout:\n{stdout}");

        if (!File.Exists(xodrPath))
            throw new InvalidOperationException(
                $"netconvert reported success but produced no OpenDRIVE output at '{xodrPath}'.\n" +
                $"stderr:\n{stderr}");
    }

    /// <summary>
    /// SINGLE SOURCE OF TRUTH for the netconvert flag set. Tune the OSM→xodr behaviour here.
    /// Mirrors CARLA osm2odr defaults; <see cref="OsmConversionOptions.ExtraArgs"/> appends
    /// extra raw flags for experimentation without touching this method.
    /// </summary>
    internal IReadOnlyList<string> BuildArguments(string osmPath, string xodrPath)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Resolve the projection + whether to keep netconvert's normalization offset.
        // A georeferenced origin (lat/lon) pins that point to (0,0) via a fully-specified
        // transverse-Mercator projection and MUST disable normalization, or the pinned
        // origin gets shifted by the bounding-box offset. Otherwise: a bare ProjString is
        // auto-centred (CenterMap=true) or left as raw projected coords (CenterMap=false).
        string projString;
        bool disableNormalization;
        if (_options.OriginLatitude is double lat && _options.OriginLongitude is double lon)
        {
            projString = string.Format(inv,
                "+proj=tmerc +lat_0={0} +lon_0={1} +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs",
                lat, lon);
            disableNormalization = true;
        }
        else
        {
            projString = _options.ProjString;
            disableNormalization = !_options.CenterMap;
        }

        var args = new List<string>
        {
            "--osm-files", osmPath,
            "--opendrive-output", xodrPath,
            "--proj", projString,
            "--default.lanewidth", _options.DefaultLaneWidth.ToString(inv),
            "--default.sidewalk-width", _options.DefaultSidewalkWidth.ToString(inv),
            "--tls.guess", _options.GenerateTrafficLights ? "true" : "false",
            // Geometry/topology cleanup matching CARLA's osm2odr output.
            "--geometry.remove",
            "--roundabouts.guess",
        };

        // A pinned origin must not be shifted; otherwise honour CenterMap.
        if (disableNormalization)
            args.Add("--offset.disable-normalization");

        if (_options.GenerateTrafficLights)
        {
            // Merge clustered OSM nodes into single signalized intersections.
            args.Add("--junctions.join");
        }
        else
        {
            // Fully suppress traffic lights so CARLA creates no (ungrouped)
            // TrafficLightComponents that flood the runtime log. --tls.guess only
            // blocks GUESSED lights, so also discard OSM-loaded ones; and skip
            // --junctions.join, which re-creates TL-controlled junctions even after
            // discard (verified on WrigleyVille: join -> 19 TLs, no-join -> 0).
            args.Add("--tls.discard-loaded");
        }

        args.AddRange(_options.ExtraArgs);
        return args;
    }

    /// <summary>
    /// Locate the netconvert executable: explicit option → CARLA_NETCONVERT env var →
    /// well-known relative paths under the app base directory and Build/sumo-install.
    /// </summary>
    internal string ResolveNetconvertPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.NetconvertPath))
            return _options.NetconvertPath!;

        var fromEnv = Environment.GetEnvironmentVariable("CARLA_NETCONVERT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv!;

        var exeName = OperatingSystem.IsWindows() ? "netconvert.exe" : "netconvert";
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "sumo", "bin", exeName),
            Path.Combine(baseDir, "Build", "sumo-install", "bin", exeName),
        ];
        foreach (var c in candidates)
            if (File.Exists(c))
                return c;

        // Fall back to bare name — relies on PATH; surfaces a clear failure at Start().
        return exeName;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temp cleanup is best effort */ }
    }
}
