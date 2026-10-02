// OSM → OpenDRIVE conversion by shelling out to the native SUMO `netconvert`
// executable (SUMO 1.27). netconvert is a standalone CLI: it reads a .osm file
// and writes a .xodr file. We run it as a subprocess.
//
// The default flag set mirrors CARLA's own osm2odr (carla-simulator/sumo
// OSM2ODRSettings): default lane width 3.35 m, default sidewalk width 2.80 m,
// proj string "+proj=tmerc", centred map, generated traffic lights.
//
// Ramp meters (OSM traffic_signals=ramp_meter) are the one kind of signal handled apart: kept out
// of junction joining and given a metering programme rather than a junction's (OsmRampMeters,
// RampMeterProgram). An extract with none gets exactly the invocation it always got.
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

    /// <summary>
    /// Honour OSM <c>turn:lanes</c> arrows when building lane-to-lane connections
    /// (<c>--osm.turn-lanes</c>). netconvert defaults this OFF and then guesses every
    /// connection from junction geometry alone, which mis-assigns turn lanes wherever a
    /// road is mapped as a dual carriageway: dedicated left-turn lanes get demoted to
    /// U-turns and through movements fan out across the whole approach. With it on,
    /// netconvert reproduces the painted arrows and falls back to the geometric guess only
    /// on edges whose tagging it cannot apply.
    /// </summary>
    public bool ImportTurnLanes { get; init; } = true;

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

    /// <summary>
    /// Write street names onto the edges (<c>--output.street-names</c>). On by default because the
    /// scenario side's place index is built from them, and because the world build and the scenario
    /// build must now produce one network between them.
    /// </summary>
    /// <remarks>
    /// This is not free annotation: setting the option changes the graph, because
    /// <c>NBEdge::expandableBy</c> refuses to merge two edges carrying different street names.
    /// Measured on one Arapahoe extract, 1,017 roads and 183 junctions become 1,021 and 184.
    /// </remarks>
    public bool OutputStreetNames { get; init; } = true;

    /// <summary>
    /// Write each lane's originating OSM way id as a <c>&lt;param key="origId"&gt;</c>
    /// (<c>--output.original-names</c>). Measured: this does not change the graph -- it only adds
    /// the parameters -- but it is part of the flag set the scenario side expects, and the two must
    /// agree argument for argument.
    /// </summary>
    public bool OutputOriginalNames { get; init; } = true;

    /// <summary>
    /// Distance within which neighbouring junctions are merged (<c>--junctions.join-dist</c>), in
    /// metres. Null keeps netconvert's own default of 10 m.
    /// </summary>
    /// <remarks>
    /// Where two junctions sit closer than this, the edge between them is trimmed to a fraction of a
    /// metre while still spanning tens of metres of geometry, and nothing can merge across it -- a
    /// freeway ramp built that way stands still under SUMO.
    /// </remarks>
    public double? JunctionJoinDistanceMeters { get; init; } = 25.0;

    /// <summary>
    /// The control type netconvert gives a guessed signal (<c>--tls.default-type</c>). netconvert's
    /// own default, <c>static</c>, is a fixed-time program on a 90 s cycle; <c>actuated</c> holds
    /// green while traffic is still arriving, which is worth a great deal of throughput at a
    /// junction carrying freeway ramp traffic. The flag is passed only when it differs from
    /// netconvert's default, so that what ran and what a scenario asks for compare equal.
    /// </summary>
    public string TrafficLightDefaultType { get; init; } = "actuated";

    /// <summary>
    /// Treat the extract's ramp meters -- nodes tagged <c>traffic_signals=ramp_meter</c> -- as meters
    /// rather than as junctions: keep them out of junction joining (<c>--junctions.join-exclude</c>)
    /// and give each the metering programme of <see cref="RampMeterProgram"/>
    /// (<c>--tllogic-files</c>). Applies only when <see cref="GenerateTrafficLights"/> is on, and adds
    /// nothing to the invocation of an extract that has no ramp meter.
    /// </summary>
    /// <remarks>
    /// Without it netconvert joins a meter standing near a merge with the merge and with the freeway
    /// beside it, and the joined signal puts the freeway on a red light every cycle; and it gives every
    /// meter a junction's 80 s green, which meters nothing. Measured on Arapahoe I-25: meter 582785322
    /// was joined into a two-phase signal alternating its loop ramp with five lanes of I-25, each held
    /// for up to 56 s.
    /// </remarks>
    public bool MeterRamps { get; init; } = true;

    /// <summary>Escape hatch: extra raw netconvert arguments appended verbatim, for tuning
    /// without code changes. Each entry is passed as a single argument token.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>
/// The output of one netconvert run, with the invocation that produced it.
/// </summary>
/// <param name="OpenDrive">The OpenDRIVE document.</param>
/// <param name="Network">The SUMO network from the same run. Never reconstructable afterwards.</param>
/// <param name="NetconvertArgv">Every argument as passed, so a later build can be compared with
/// this one rather than with a guess at what it must have been.</param>
/// <param name="NetconvertPath">The executable that ran, resolved.</param>
/// <param name="NetconvertVersion">Its self-reported version, read from the executable that ran
/// rather than inferred from a path or a pin.</param>
/// <param name="RampMeters">What became of the extract's ramp meters: which were given the metering
/// programme, which kept netconvert's and why, and the <c>--tllogic-files</c> document the recorded
/// invocation read. Null when the extract has no ramp meter or metering is off.</param>
public sealed record OsmConversionResult(
    string OpenDrive,
    string Network,
    IReadOnlyList<string> NetconvertArgv,
    string NetconvertPath,
    string NetconvertVersion,
    RampMeterPlan? RampMeters = null);

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
            // The meters are kept out of junction joining here too, so the OpenDRIVE is the one a
            // world build with a network makes. Their programmes are not needed: they live only in
            // the network, and the OpenDRIVE is measured byte-identical with and without them.
            await RunNetconvertAsync(ResolveNetconvertPath(),
                                     BuildArguments(osmPath, xodrPath, rampMeters: RampMetersOf(osmPath)),
                                     xodrPath, ct)
                .ConfigureAwait(false);
            return await File.ReadAllTextAsync(xodrPath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(xodrPath);
        }
    }

    /// <summary>
    /// Convert an .osm file to OpenDRIVE and return the SUMO network (<c>.net.xml</c>) from the same
    /// netconvert run, together with the invocation that produced both.
    /// </summary>
    /// <remarks>
    /// <para>The network is not a by-product: it is the road graph a SUMO scenario is authored
    /// against, and it <b>cannot be reconstructed by running netconvert again, even with identical
    /// flags</b>. Asking for OpenDRIVE output makes netconvert default <c>rectangular-lane-cut</c>
    /// to true (<c>NWFrame::checkOptions</c>), which feeds junction shape computation: measured on
    /// one Arapahoe extract, 743 of 4,978 canonical rows differ between a run that wrote OpenDRIVE
    /// and one that did not, with lane lengths moving by up to 3.3 m. So the network a world is
    /// distributed with has to come out of the invocation that built the world.</para>
    /// <para>Measured: requesting the network alongside the OpenDRIVE does not change the OpenDRIVE.
    /// The two documents are byte-identical either way once netconvert's timestamped header is
    /// normalised, with and without traffic-light generation.</para>
    /// <para>The network also carries the guessed traffic-light <c>&lt;tlLogic&gt;</c> phase programs
    /// <c>TrafficLightInjector</c> reads to build per-phase controllers. That is why it was first
    /// produced, and it is now produced whether or not traffic lights are generated.</para>
    /// <para><b>An extract with ramp meters is converted twice</b> (<see cref="OsmConversionOptions.MeterRamps"/>).
    /// A metering programme names every link of its signal, and which links a meter has -- one per
    /// lane, or two where a lane fans out, on whichever edge netconvert made of the ramp -- is known
    /// only once netconvert has built the network. So the first run, with the meters kept out of
    /// junction joining, is read for the meters' links; the programmes are written for them; and the
    /// second run is the first one again with <c>--tllogic-files</c> naming that file. The second run
    /// is the one recorded and the one whose outputs are returned, so the package still holds the
    /// network and OpenDRIVE of the invocation it records. Measured on Arapahoe I-25: the two runs'
    /// networks differ in the four meters' programmes and nothing else, and their OpenDRIVE documents
    /// are identical but for netconvert's timestamped header. The result is checked against the
    /// programmes written, so a netconvert that read them differently fails the build rather than
    /// shipping a world whose meters run something else.</para>
    /// </remarks>
    public async Task<OsmConversionResult> ConvertFileWithNetworkAsync(
        string osmPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(osmPath))
            throw new ArgumentException("OSM path must be provided.", nameof(osmPath));
        if (!File.Exists(osmPath))
            throw new FileNotFoundException("OSM file not found.", osmPath);

        var xodrPath = Path.Combine(Path.GetTempPath(), $"carlanet_osm_{Guid.NewGuid():N}.xodr");
        var netPath = Path.Combine(Path.GetTempPath(), $"carlanet_osm_{Guid.NewGuid():N}.net.xml");
        string? programsPath = null;
        try
        {
            var exe = ResolveNetconvertPath();
            var meters = RampMetersOf(osmPath);
            var argv = BuildArguments(osmPath, xodrPath, netPath, meters);
            string network = await RunForNetworkAsync(exe, argv, xodrPath, netPath, ct).ConfigureAwait(false);

            RampMeterPlan? plan = null;
            if (meters.Count > 0)
            {
                plan = RampMeterProgram.Plan(network, meters);
                if (plan.Metered.Count > 0)
                {
                    programsPath = Path.Combine(Path.GetTempPath(), $"carlanet_osm_{Guid.NewGuid():N}.tll.xml");
                    await File.WriteAllTextAsync(programsPath, plan.ProgramFile, new UTF8Encoding(false), ct)
                        .ConfigureAwait(false);
                    argv = BuildArguments(osmPath, xodrPath, netPath, meters, programsPath);
                    network = await RunForNetworkAsync(exe, argv, xodrPath, netPath, ct).ConfigureAwait(false);
                    var problems = RampMeterProgram.Verify(network, plan);
                    if (problems.Count > 0)
                        throw new InvalidOperationException(
                            "netconvert did not give the ramp meters the programmes it was given:\n  "
                            + string.Join("\n  ", problems));
                }
            }

            return new OsmConversionResult(
                OpenDrive: await File.ReadAllTextAsync(xodrPath, ct).ConfigureAwait(false),
                Network: network,
                NetconvertArgv: RecordedArguments(argv, xodrPath, netPath, programsPath),
                NetconvertPath: exe,
                NetconvertVersion: ResolveNetconvertVersion(exe),
                RampMeters: plan);
        }
        finally
        {
            TryDelete(xodrPath);
            TryDelete(netPath);
            if (programsPath != null)
                TryDelete(programsPath);
        }
    }

    /// <summary>
    /// The ramp meters this conversion treats as meters: the extract's, when metering is on and
    /// signals are generated at all; otherwise none.
    /// </summary>
    internal IReadOnlyList<string> RampMetersOf(string osmPath)
        => _options.MeterRamps && _options.GenerateTrafficLights ? OsmRampMeters.Read(osmPath) : [];

    private async Task<string> RunForNetworkAsync(
        string exe, IReadOnlyList<string> argv, string xodrPath, string netPath, CancellationToken ct)
    {
        await RunNetconvertAsync(exe, argv, xodrPath, ct).ConfigureAwait(false);
        if (!File.Exists(netPath))
            throw new InvalidOperationException(
                "netconvert reported success but produced no SUMO network at "
                + $"'{netPath}'. A world cannot be built without one: the network is what a "
                + "scenario is authored against and it cannot be reproduced afterwards.");
        return await File.ReadAllTextAsync(netPath, ct).ConfigureAwait(false);
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

    private async Task RunNetconvertAsync(
        string exe, IReadOnlyList<string> argv, string xodrPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in argv)
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
    /// <param name="osmPath">The extract.</param>
    /// <param name="xodrPath">Where the OpenDRIVE is written.</param>
    /// <param name="netPath">Where the SUMO network is written, or null for none.</param>
    /// <param name="rampMeters">The extract's ramp meters (<see cref="RampMetersOf"/>), kept out of
    /// junction joining. Empty or null adds nothing, so an extract with no meter gets exactly the
    /// invocation it got before meters were handled at all.</param>
    /// <param name="programsPath">The meters' programme file (<see cref="RampMeterProgram"/>), or null
    /// before it has been written.</param>
    internal IReadOnlyList<string> BuildArguments(string osmPath, string xodrPath, string? netPath = null,
                                                  IReadOnlyList<string>? rampMeters = null,
                                                  string? programsPath = null)
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

        // Build lane connections from the painted turn arrows rather than from junction
        // geometry alone. Only add the flag when enabled — netconvert's own default is OFF.
        if (_options.ImportTurnLanes)
            args.Add("--osm.turn-lanes");

        // Street names. These must be OMITTED to turn them off, NEVER set to "false".
        // NBEdge::expandableBy guards on `!isDefault("output.street-names")` -- on whether the
        // option was given at all, not on its value -- so passing "false" produces exactly the
        // graph that passing "true" produces, and a later attempt to turn names off by setting the
        // flag false silently changes nothing. Verified by fingerprinting both outputs.
        if (_options.OutputStreetNames)
        {
            args.Add("--output.street-names");
            args.Add("true");
        }

        // Lane-level provenance back to the OSM way. Measured not to change the graph.
        if (_options.OutputOriginalNames)
        {
            args.Add("--output.original-names");
            args.Add("true");
        }

        // A pinned origin must not be shifted; otherwise honour CenterMap.
        if (disableNormalization)
            args.Add("--offset.disable-normalization");

        if (_options.GenerateTrafficLights)
        {
            // Merge clustered OSM nodes into single signalized intersections.
            args.Add("--junctions.join");

            // Only when it differs from netconvert's own default, so the flag list a scenario
            // validates against matches argument for argument rather than by interpretation.
            if (!string.IsNullOrWhiteSpace(_options.TrafficLightDefaultType)
                && _options.TrafficLightDefaultType != "static")
            {
                args.Add("--tls.default-type");
                args.Add(_options.TrafficLightDefaultType);
            }

            // Ramp meters are meters, not junctions. Kept out of joining, a meter controls its ramp
            // alone and the merge beyond it stays an unsignalised priority merge, so no meter can
            // put the freeway on a red light; and the programme file gives each its metering cycle
            // in place of the junction programme netconvert would guess. netconvert reads each of
            // these options once, so a caller passing either through ExtraArgs is refused rather
            // than having one list silently replace the other.
            if (rampMeters is { Count: > 0 })
            {
                RefuseExtraArgument("--junctions.join-exclude");
                RefuseExtraArgument("--tllogic-files", "-i");
                args.Add("--junctions.join-exclude");
                args.Add(string.Join(",", rampMeters));
                if (programsPath != null)
                {
                    args.Add("--tllogic-files");
                    args.Add(programsPath);
                }
            }
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

        // Write the SUMO network whenever a path is given. It is not a by-product of traffic-light
        // generation: it is the road graph a scenario is authored against, and it cannot be
        // reproduced by a later netconvert run because asking for OpenDRIVE output changes the
        // graph (see ConvertFileWithNetworkAsync). Measured: adding this output leaves the
        // OpenDRIVE byte-identical.
        if (netPath != null)
        {
            args.Add("--output-file");
            args.Add(netPath);
        }

        if (_options.JunctionJoinDistanceMeters is double joinDistance)
        {
            args.Add("--junctions.join-dist");
            args.Add(joinDistance.ToString(inv));
        }

        args.AddRange(_options.ExtraArgs);
        return args;
    }

    /// <summary>Refuses an extra argument the ramp meters already set.</summary>
    private void RefuseExtraArgument(params string[] spellings)
    {
        foreach (string argument in _options.ExtraArgs)
        {
            foreach (string option in spellings)
            {
                if (argument == option || argument.StartsWith(option + "=", StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"the extra netconvert arguments set {option}, which the conversion sets for "
                        + "the extract's ramp meters, and netconvert reads it once. Leave it out of the "
                        + "extra arguments, or turn ramp metering off (MeterRamps = false) to pass it "
                        + "yourself.");
            }
        }
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

    /// <summary>
    /// The version the resolved executable reports, e.g. "Eclipse SUMO netconvert Version 1.27.0".
    /// </summary>
    /// <remarks>
    /// Read from the executable that is about to run rather than inferred from its path or from a
    /// pinned version elsewhere in the build: the pinned toolchain and whatever <c>SUMO_HOME</c>
    /// points at have already diverged once. Cached per path, because a world build converts once
    /// but a batch converts many times and this is a process launch.
    /// Empty when the executable cannot be asked, which is the same signal as a package built
    /// before the version was recorded at all.
    /// </remarks>
    internal static string ResolveNetconvertVersion(string exe)
    {
        lock (VersionsByPath)
        {
            if (VersionsByPath.TryGetValue(exe, out var cached))
                return cached;
        }

        string version = string.Empty;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(VersionQueryTimeoutMs);
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0) { version = trimmed; break; }
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException or IOException)
        {
            version = string.Empty;
        }

        lock (VersionsByPath)
        {
            VersionsByPath[exe] = version;
        }
        return version;
    }

    private const int VersionQueryTimeoutMs = 10_000;

    private static readonly Dictionary<string, string> VersionsByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What an OpenDRIVE output path is recorded as in the build's arguments.</summary>
    public const string RecordedOpenDriveOutput = "<opendrive-output>";

    /// <summary>What the network output path is recorded as in the build's arguments.</summary>
    public const string RecordedNetworkOutput = "<output-file>";

    /// <summary>
    /// What the ramp meters' programme file is recorded as in the build's arguments. Its content is
    /// carried in the world package beside the network (<c>WorldPackage.TrafficLightProgramsEntry</c>).
    /// </summary>
    public const string RecordedProgramsInput = "<tllogic-files>";

    /// <summary>
    /// The arguments as a build records them: the output files, and the ramp meters' programme file
    /// when there is one, are this run's scratch files, named at random, and where they sit changes
    /// nothing netconvert produces, so they are recorded by fixed names. Recorded as passed, every
    /// build of an identical world differed from the last, and a scenario compiled against the first
    /// looked stale against the second.
    /// </summary>
    internal static IReadOnlyList<string> RecordedArguments(IReadOnlyList<string> argv,
                                                            string openDrivePath,
                                                            string networkPath,
                                                            string? programsPath = null) =>
        [.. argv.Select(argument => argument == openDrivePath ? RecordedOpenDriveOutput
                                    : argument == networkPath ? RecordedNetworkOutput
                                    : programsPath != null && argument == programsPath ? RecordedProgramsInput
                                    : argument)];

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temp cleanup is best effort */ }
    }
}
