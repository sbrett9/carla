using System.Globalization;
using System.Text;
using System.Text.Json;
using CarlaNet.Recording;
using CarlaNet.Sumo;
using CarlaNet.Types.Illumination;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The world truth track: a row for every vehicle SUMO has at every sampled frame inside the capture
/// window, stamped with TraCI's clock, drawn or not, written so that a file cut off anywhere is the rows
/// before the cut.
/// </summary>
/// <remarks>
/// <para>Run against a real SUMO on the fixture cross. With no CARLA at all the session ticks a counter
/// and renders no world, so every vehicle is simulated and none drawn; with the recording world double
/// the succession fixture hands one body from a measured vehicle to the next, with an unmeasured one
/// between them that no body can draw.</para>
///
/// <para>What each row is checked against is what the session told an observer registered beside the
/// track -- each SUMO frame's clock and every vehicle's state, each rendered frame's bodies and sun --
/// read through the seam rather than through the track's own code, and SUMO's own stamp for a departure,
/// asked on demand, a step before the clock the track writes.</para>
/// </remarks>
public sealed class WorldTruthTrackWriterTests : IDisposable
{
    private const double StepSeconds = 1.0;
    private const int StepLimit = 2000;

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
                                                      "carlanet-track-" + Guid.NewGuid().ToString("n"));

    public WorldTruthTrackWriterTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is not the test's failure.
        }
    }

    [RequiresSumoFact]
    public void AWorldLessRunWritesEveryVehicleAtEverySumoFrameRenderedAtTraCISClock()
    {
        // The ground rises east and falls north, so a height read with the northing's sign wrong is a
        // different number.
        using SyntheticWorld world = SyntheticWorld.Write(
            at => 5.0 + (0.01 * at.X) + (0.02 * at.Y), CoSimFixtures.RightAngleTurnNetwork, "!");
        string track = Path.Combine(_directory, "nested", "world_truth_track.csv");
        var watcher = new Watcher { AskDepartures = true };
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, watcher);
        options.WorldTruthTrackPath = track;

        int ticksPerStep;
        long rowsReported;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }

            ticksPerStep = session.Clock.WorldTicksPerSumoStep;
            rowsReported = session.WorldTruthTrack!.Rows;
        }

        List<Row> rows = ReadRows(File.ReadAllText(track), out string[] header);
        Assert.Equal(WorldTruthTrackWriter.Columns, header);
        Assert.NotEmpty(rows);
        Assert.Equal(rowsReported, rows.Count);

        // Every SUMO frame but the last two has a row for every vehicle SUMO had at it, and nothing else
        // does. The last advance read the last of them and rendered the ticks up to the one before it:
        // SUMO had nothing left, so no tick rendered either.
        List<StepSeen> rendered = watcher.Steps[..^2];
        Assert.Equal(
            rendered.SelectMany(step => step.Frames.Keys.Order(StringComparer.Ordinal)
                                            .Select(id => (Seconds(step.FrameSeconds), id))),
            rows.Select(row => (row["sim_time_s"], row["sumo_id"])));

        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);
        Dictionary<double, (int Index, StepSeen Step)> byInstant = rendered
            .Select((step, index) => (index, step))
            .ToDictionary(entry => entry.step.FrameSeconds, entry => (entry.index, entry.step));
        foreach (Row row in rows)
        {
            // TraCI's clock for the frame, which is the instant the frame that renders it is stamped with.
            double instant = double.Parse(row["sim_time_s"], CultureInfo.InvariantCulture);
            (int index, StepSeen step) = byInstant[instant];
            CoSimVehicleFrame state = step.Frames[row["sumo_id"]];
            Assert.Equal(((ulong)(index * ticksPerStep) + 1).ToString(CultureInfo.InvariantCulture), row["frame"]);

            // The state the session read, in SUMO's frame and CARLA's.
            Assert.Equal(F(state.X, "0.00"), row["sumo_x"]);
            Assert.Equal(F(state.Y, "0.00"), row["sumo_y"]);
            Assert.Equal(F(state.X, "0.00"), row["carla_x"]);
            Assert.Equal(F(-state.Y, "0.00"), row["carla_y"]);
            Assert.Equal(F(state.SpeedMetresPerSecond, "0.00"), row["speed_mps"]);
            Assert.Equal(state.EdgeId, row["edge"]);
            Assert.Equal(state.LaneId, row["lane"]);

            // The bare earth under the bumper, the CARLA frame's y being SUMO's negated.
            Assert.Equal(1000.0 + 5.0 + (0.01 * state.X) + (0.02 * -state.Y),
                         double.Parse(row["hae_m"], CultureInfo.InvariantCulture), 0.006);
            // At the equator a degree of latitude is 110 574 m and one of longitude 111 320 m.
            Assert.Equal(state.Y / 110_574.0, double.Parse(row["lat"], CultureInfo.InvariantCulture), 1e-6);
            Assert.Equal(state.X / 111_320.0, double.Parse(row["lon"], CultureInfo.InvariantCulture), 1e-6);

            // Identity follows the SUMO vehicle; its dimensions are its type's as declared, and its base
            // type and kind the catalogue's for the blueprint the type names: the Fuso is a bus, where the
            // type's truck class would read as a truck, and the callsign carries it.
            Assert.Equal(row["sumo_id"], row["entity_id"]);
            Assert.Equal("CARLA-TRUTH-SUMO-" + row["sumo_id"], row["uid"]);
            Assert.Equal("bus-" + row["sumo_id"], row["callsign"]);
            Assert.Equal(row["sumo_id"], row["role_name"]);
            Assert.Equal(("bus", "measured_truck", "7.02", "2.51"),
                         (row["base_type"], row["type_id"], row["length_m"], row["width_m"]));
            Assert.Equal(catalogue.BaseTypes["vehicle.fuso.mitsubishi"], row["base_type"]);
            Assert.NotEqual(WorldTruthVehicleType.BaseTypeOf("truck"), row["base_type"]);
            Assert.Equal(catalogue.SpecialTypes["vehicle.fuso.mitsubishi"], row["special_type"]);
            Assert.Matches(@"^\d+,\d+,\d+$", row["color"]);

            // No world: nothing drawn, no sun and so no band, and with no epoch no civil instant.
            Assert.Equal(("simulated_only", "no_world", string.Empty, "1", string.Empty),
                         (row["render_state"], row["render_reason"], row["actor_id"], row["in_window"],
                          row["time_utc"]));
            Assert.Equal(["", "", "", ""],
                         (string[])[row["sun_elevation_deg"], row["sun_corrected_elevation_deg"],
                                    row["illumination_band"], row["illumination_band_elevation"]]);
        }

        // A vehicle's first row is at the clock its departure was listed at, a step after SUMO's own stamp.
        foreach ((string vehicleId, (double listedAt, double? stamped)) in watcher.Departures)
        {
            Row first = rows.First(row => row["sumo_id"] == vehicleId);
            Assert.Equal(Seconds(listedAt), first["sim_time_s"]);
            Assert.Equal(listedAt - StepSeconds, stamped!.Value, 9);
        }

        Assert.Equal(["dweller", "parker"], watcher.Departures.Keys.Order(StringComparer.Ordinal));

        // The summary: the rate, what the track holds, and that SUMO had nothing left.
        using JsonDocument summary = ReadSummary(track);
        JsonElement root = summary.RootElement;
        Assert.Equal(2, root.GetProperty("world_truth_track_version").GetInt32());
        // What made the track, in the summary so the CSV's header stays its columns: no world here, so no
        // server, and the SUMO release the session launched.
        JsonElement producer = root.GetProperty("producer");
        Assert.Equal(CarlaNet.Types.Provenance.Producer.CarlaNetVersion, producer.GetProperty("carlanet").GetString());
        Assert.Equal(JsonValueKind.Null, producer.GetProperty("server").ValueKind);
        Assert.False(string.IsNullOrEmpty(producer.GetProperty("sumo").GetString()));
        Assert.False(string.IsNullOrEmpty(producer.GetProperty("written_utc").GetString()));
        Assert.Equal("world_truth_track.csv", root.GetProperty("track").GetString());
        Assert.Equal(WorldTruthTrackWriter.Columns,
                     root.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
        Assert.Contains("illumination_band", WorldTruthTrackWriter.Columns);
        Assert.Equal(StepSeconds, root.GetProperty("sumo_step_s").GetDouble());
        Assert.Equal(StepSeconds, root.GetProperty("interval_s").GetDouble());
        Assert.Equal(1, root.GetProperty("every_sumo_steps").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("outside_window_interval_s").ValueKind);
        Assert.Equal(rows.Count, root.GetProperty("rows").GetInt64());
        // A sample is a frame, so the fast-forward's, with no vehicle yet, is one with no rows.
        Assert.Equal(rendered.Count, root.GetProperty("samples").GetInt64());
        Assert.Equal(rendered[0].FrameSeconds, root.GetProperty("first_sample_s").GetDouble());
        Assert.Equal(rendered[^1].FrameSeconds, root.GetProperty("last_sample_s").GetDouble());
        JsonElement ended = root.GetProperty("ended");
        Assert.Equal("scenario_finished", ended.GetProperty("reason").GetString());
        Assert.Equal(watcher.Steps[^1].FrameSeconds, ended.GetProperty("last_sumo_frame_s").GetDouble());
        Assert.False(File.Exists(WorldTruthTrackWriter.SummaryPathFor(track) + ".partial"));
    }

    [RequiresSumoFact]
    public void EachFrameSRowsAreOnDiskOnceItIsWrittenAndATrackCutOffAnywhereIsTheRowsBeforeTheCut()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string track = Path.Combine(_directory, "track.csv");
        var disk = new DiskReader(track);
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        options.StepObservers.Add(disk);
        options.WorldTruthTrackPath = track;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            disk.Track = session.WorldTruthTrack;
            for (int step = 0; step < 12 && session.Advance(); step++)
            {
            }

            Assert.True(session.WorldTruthTrack!.Rows > 10, "too few rows to cut");
        }

        // Read as each frame left it while the session held the file open: every row written is there,
        // whole, the frame that renders a SUMO frame finds that frame's every row on disk after the
        // rows of the frames before it, and the summary does not yet say the track ended.
        Assert.Contains(disk.Frames, frame => frame.Vehicles > 0);
        foreach (FrameOnDisk frame in disk.Frames)
        {
            Assert.True(frame.EndsInALineBreak, $"frame {frame.Frame} found a line cut short");
            Assert.Equal(frame.RowsWritten, frame.RowsOnDisk);
            Assert.Equal(frame.Vehicles ?? 0, frame.RowsOfTheFrame);
            Assert.Equal(JsonValueKind.Null, frame.Ended);
        }

        // Stopped by its caller before SUMO had finished.
        using (JsonDocument summary = ReadSummary(track))
        {
            Assert.Equal("caller_stopped", summary.RootElement.GetProperty("ended").GetProperty("reason").GetString());
        }

        byte[] whole = File.ReadAllBytes(track);
        List<Row> all = ReadRows(Encoding.UTF8.GetString(whole), out string[] header);
        List<int> cuts = [0, 1, whole.Length];
        for (int at = 0; at < whole.Length; at++)
        {
            if (whole[at] == (byte)'\n')
            {
                // Before the line break, after it, one into the next line, and half way along that line.
                int nextBreak = Array.IndexOf(whole, (byte)'\n', at + 1);
                cuts.AddRange([at, at + 1, at + 2, nextBreak < 0 ? at + 1 : (at + nextBreak) / 2]);
            }
        }

        foreach (int cut in cuts.Where(cut => cut <= whole.Length).Distinct())
        {
            string prefix = Encoding.UTF8.GetString(whole, 0, cut);
            int completeLines = prefix.Count(character => character == '\n');
            List<Row> kept = ReadRows(prefix, out string[] keptHeader);
            if (completeLines == 0)
            {
                Assert.Empty(kept);
                continue;
            }

            Assert.Equal(header, keptHeader);
            Assert.Equal(completeLines - 1, kept.Count);
            Assert.Equal(all.Take(kept.Count).Select(row => row.Line), kept.Select(row => row.Line));
        }
    }

    [RequiresSumoFact]
    public void WithNoPathTheSessionWritesNoTrack()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var watcher = new Watcher();
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, watcher);
        Assert.Null(options.WorldTruthTrackPath);
        Assert.Null(options.WorldTruthTrackIntervalSeconds);
        string[] before = Directory.GetFiles(world.Directory, "*", SearchOption.AllDirectories);

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }

            Assert.Null(session.WorldTruthTrack);
        }

        Assert.NotEmpty(watcher.Steps);
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
        Assert.Equal(before, Directory.GetFiles(world.Directory, "*", SearchOption.AllDirectories));
    }

    [RequiresSumoFact]
    public void AnIntervalSamplesEveryFewSumoFramesFromTheWindowSOpeningAndThePrewarmWritesNothing()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string track = Path.Combine(_directory, "track.csv");
        var watcher = new Watcher();
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, watcher);
        options.WorldTruthTrackPath = track;
        options.WorldTruthTrackIntervalSeconds = 2.0;
        options.WindowOpensAtSimulatedSecond = 3.0;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            Assert.Equal(2, session.WorldTruthTrack!.SumoStepsPerSample);
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }
        }

        List<Row> rows = ReadRows(File.ReadAllText(track), out _);
        List<double> instants = [.. rows.Select(row => double.Parse(row["sim_time_s"], CultureInfo.InvariantCulture))
                                        .Distinct()];
        Assert.True(instants.Count >= 3, $"only {instants.Count} samples");

        // From the window's opening, every second SUMO frame inside it, each with every vehicle SUMO had.
        Assert.Equal(3.0, instants[0]);
        Assert.All(instants.Zip(instants.Skip(1)), pair => Assert.Equal(2.0, pair.Second - pair.First, 9));
        foreach (double instant in instants)
        {
            StepSeen step = watcher.Steps.Single(seen => Math.Abs(seen.FrameSeconds - instant) < 1e-9);
            Assert.Equal(step.Frames.Keys.Order(StringComparer.Ordinal),
                         rows.Where(row => row["sim_time_s"] == Seconds(instant)).Select(row => row["sumo_id"]));
        }

        using JsonDocument summary = ReadSummary(track);
        Assert.Equal(2.0, summary.RootElement.GetProperty("interval_s").GetDouble());
        Assert.Equal(2, summary.RootElement.GetProperty("every_sumo_steps").GetInt32());
        Assert.Equal(3.0, summary.RootElement.GetProperty("first_sample_s").GetDouble());
    }

    [RequiresSumoFact]
    public void ADrawnVehicleIsNamedByItsBodyAndOneNotDrawnSaysWhy()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string track = Path.Combine(_directory, "track.csv");
        var watcher = new Watcher();
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.SuccessionScenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = new RecordedWorld { Loaded = world.AsLoaded() },
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            WorldTruthTrackPath = track,
        };
        options.StepObservers.Add(watcher);

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
        }

        List<Row> rows = ReadRows(File.ReadAllText(track), out _);
        Dictionary<string, FrameSeen> frames = watcher.Frames.ToDictionary(frame => frame.Frame);
        foreach (Row row in rows)
        {
            FrameSeen frame = frames[row["frame"]];
            Assert.Equal(row["sim_time_s"], Seconds(frame.SimulatedTimeSeconds));

            // Drawn exactly where the frame's render set has a body for it, and named by that body.
            Assert.Equal(frame.Bodies.TryGetValue(row["sumo_id"], out uint body)
                             ? ("rendered", string.Empty, body.ToString(CultureInfo.InvariantCulture))
                             : ("simulated_only", row["render_reason"], string.Empty),
                         (row["render_state"], row["render_reason"], row["actor_id"]));

            // The sun the world reported on the frame's tick, and the frame's civil instant in UTC.
            // Both elevations the world reported on the frame's tick, and the band the table gives the
            // refraction-corrected one, which the bands are stated against.
            Assert.NotNull(frame.SunElevationDegrees);
            Assert.NotNull(frame.SunCorrectedElevationDegrees);
            Assert.Equal(F(frame.SunElevationDegrees!.Value, "0.######"), row["sun_elevation_deg"]);
            Assert.Equal(F(frame.SunCorrectedElevationDegrees!.Value, "0.######"), row["sun_corrected_elevation_deg"]);
            Assert.Equal(IlluminationBands.NameOf(frame.SunCorrectedElevationDegrees.Value), row["illumination_band"]);
            Assert.Equal(IlluminationBands.NameOf(Elevation(row, "sun_corrected_elevation_deg")),
                         row["illumination_band"]);
            Assert.Equal("refraction_corrected", row["illumination_band_elevation"]);
            Assert.Equal(SolarLeaseTests.PortEpoch().CivilInstantAt(frame.SimulatedTimeSeconds).UtcDateTime
                             .ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z",
                         row["time_utc"]);
        }

        // The measured vehicles are drawn on every frame, their last SUMO frame included (the owner's ruling
        // of 2026-10-06) -- where that frame was rendered at all: the second vehicle is the scenario's last,
        // and its last frame is among the two read after the last frame rendered. The first has a row at its
        // last frame, drawn by its body. The unmeasured one is never drawn, for want of a body, on its last
        // frame as on every other.
        foreach (string measured in (string[])["first", "second"])
        {
            List<Row> life = [.. rows.Where(row => row["sumo_id"] == measured)];
            Assert.True(life.Count > 2, $"{measured} has {life.Count} rows");
            Assert.All(life, row => Assert.Equal(("rendered", ""), (row["render_state"], row["render_reason"])));
        }

        Assert.Equal(Seconds(watcher.Steps.Last(step => step.Frames.ContainsKey("first")).FrameSeconds),
                     rows.Last(row => row["sumo_id"] == "first")["sim_time_s"]);
        Assert.DoesNotContain(rows, row => row["render_reason"] is "left_the_simulation" or "vanished");

        List<Row> unmeasured = [.. rows.Where(row => row["sumo_id"] == "unrenderable")];
        Assert.NotEmpty(unmeasured);
        Assert.All(unmeasured, row => Assert.Equal(("simulated_only", "no_blueprint"),
                                                    (row["render_state"], row["render_reason"])));
        // Its type names no blueprint, so its base type is its passenger class's, and it has no kind.
        Assert.Equal("car", unmeasured[0]["base_type"]);
        Assert.Equal("car-unrenderable", unmeasured[0]["callsign"]);
        Assert.Equal(string.Empty, unmeasured[0]["special_type"]);
        Assert.All(rows.Where(row => row["sumo_id"] != "unrenderable"),
                   row => Assert.Equal("bus", row["base_type"]));

        // The body the first vehicle gave back drew the second.
        Assert.Equal(rows.First(row => row["sumo_id"] == "first")["actor_id"],
                     rows.First(row => row["sumo_id"] == "second")["actor_id"]);
    }

    [RequiresSumoFact]
    public void AWorldThatReportsOnlyTheGeometricElevationHasItsBandCutFromThatAndSaysSo()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string track = Path.Combine(_directory, "track.csv");
        var watcher = new Watcher();
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.SuccessionScenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = new RecordedWorld { Loaded = world.AsLoaded(), ObserverCarriesCorrectedElevation = false },
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            WorldTruthTrackPath = track,
        };
        options.StepObservers.Add(watcher);

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 100 && session.Advance(); step++)
            {
            }
        }

        List<Row> rows = ReadRows(File.ReadAllText(track), out _);
        Assert.NotEmpty(rows);
        Assert.All(watcher.Frames, frame => Assert.Null(frame.SunCorrectedElevationDegrees));
        Assert.All(rows, row =>
        {
            Assert.Equal(string.Empty, row["sun_corrected_elevation_deg"]);
            Assert.Equal(IlluminationBands.NameOf(Elevation(row, "sun_elevation_deg")), row["illumination_band"]);
            Assert.Equal("geometric", row["illumination_band_elevation"]);
        });
    }

    [Fact]
    public void TheBandIsTheTableSForTheElevationItIsCutFromEdgesIncluded()
    {
        // Driven by hand with the records the session would tell it, so the sun can stand on each edge:
        // a session's own sun is audited against its declaration and stands where the epoch puts it.
        string track = Path.Combine(_directory, "track.csv");
        CoSimClock clock = CoSimClock.ForSession(1.0, 0.05, 2.0, true);
        (double Geometric, double? Corrected, string Band, string CutFrom)[] suns =
        [
            (5.5, 6.0, "golden", "refraction_corrected"),
            (5.5, 6.000001, "day", "refraction_corrected"),
            (-0.4, 0.0, "civil_twilight", "refraction_corrected"),
            (-0.4, 0.000001, "golden", "refraction_corrected"),
            (-6.3, -6.0, "nautical_twilight", "refraction_corrected"),
            (-18.2, -18.0, "night", "refraction_corrected"),
            (-18.2, -17.999999, "astronomical_twilight", "refraction_corrected"),
            (6.0, null, "golden", "geometric"),
            (-180.0, -180.0, string.Empty, string.Empty),
        ];

        using (WorldTruthTrackWriter writer = WorldTruthTrackWriter.Open(
                   track, 1, clock,
                   _ => new WorldTruthVehicleType("passenger", "car", 4.6, 1.8, 1.5, "255,255,0", string.Empty, null),
                   SyntheticWorld.Build(_ => 0.0), (0.0, 0.0), null))
        {
            writer.OnSumoStep(OneVehicleAt(0.0));
            writer.OnSumoStep(OneVehicleAt(1.0));
            for (int index = 0; index < suns.Length; index++)
            {
                writer.OnFrameRendered(new RenderedFrameRecord(
                    (ulong)(index * clock.WorldTicksPerSumoStep) + 1, index * clock.WorldTicksPerSumoStep, index,
                    true, true, null, null)
                {
                    Sun = new SolarReading(7.0, 2026, 3, 21, 3.5, 0.0, 0.0, suns[index].Geometric, 90.0, false, 0.0,
                                           suns[index].Corrected),
                });
                writer.OnSumoStep(OneVehicleAt(index + 2.0));
            }
        }

        List<Row> rows = ReadRows(File.ReadAllText(track), out _);
        Assert.Equal(suns.Length, rows.Count);
        for (int index = 0; index < suns.Length; index++)
        {
            Row row = rows[index];
            (double geometric, double? corrected, string band, string cutFrom) = suns[index];
            Assert.Equal((F(geometric, "0.######"), corrected is { } refracted ? F(refracted, "0.######") : string.Empty),
                         (row["sun_elevation_deg"], row["sun_corrected_elevation_deg"]));
            Assert.Equal((band, cutFrom), (row["illumination_band"], row["illumination_band_elevation"]));
            if (band.Length > 0)
            {
                // The table's band for the elevation the row says it was cut from, read back as written.
                Assert.Equal(IlluminationBands.NameOf(Elevation(
                                 row, cutFrom == "geometric" ? "sun_elevation_deg" : "sun_corrected_elevation_deg")),
                             row["illumination_band"]);
            }
        }
    }

    [RequiresSumoFact]
    public void ARunThatStopsSaysSoInTheSummaryAndKeepsItsRows()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string track = Path.Combine(_directory, "track.csv");
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        options.WorldTruthTrackPath = track;
        int ticks = 0;
        options.TickWorld = () => ++ticks <= 100;

        long rows;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            CoSimSessionRefusedException stopped = Assert.Throws<CoSimSessionRefusedException>(() =>
            {
                while (session.Advance())
                {
                }
            });
            Assert.Equal(CoSimStopCause.WorldTickTimeout, stopped.Cause);
            rows = session.WorldTruthTrack!.Rows;
        }

        Assert.True(rows > 0);
        Assert.Equal(rows, ReadRows(File.ReadAllText(track), out _).Count);
        using JsonDocument summary = ReadSummary(track);
        JsonElement ended = summary.RootElement.GetProperty("ended");
        Assert.Equal("run_stopped", ended.GetProperty("reason").GetString());
        Assert.Equal("world-tick-timeout", ended.GetProperty("cause").GetString());
        Assert.Equal("Window", ended.GetProperty("stage").GetString());
    }

    [RequiresSumoFact]
    public void AnIntervalBetweenSumoFramesIsRefusedOnceTheStepIsKnownAndLeavesNoTrack()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string track = Path.Combine(_directory, "track.csv");
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        options.WorldTruthTrackPath = track;
        options.WorldTruthTrackIntervalSeconds = 1.5;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options).Dispose());

        Assert.Equal(CoSimSessionStage.Launch, refused.Stage);
        Assert.Contains("whole number of SUMO steps", refused.Message);
        Assert.False(File.Exists(track));
        Assert.False(File.Exists(WorldTruthTrackWriter.SummaryPathFor(track)));
    }

    [Fact]
    public void ATrackAlreadyWrittenARateWithNoPathAndARateOfNoTimeAreRefusedBeforeAnythingStarts()
    {
        string track = Path.Combine(_directory, "track.csv");
        File.WriteAllText(track, "another run's track\n");
        SumoDriveSessionOptions Given(string? path, double? interval) =>
            new("missing.sumocfg", "missing.cwp", "missing.catalogue.json", "test://track")
            {
                TickWorld = () => true,
                WorldTruthTrackPath = path,
                WorldTruthTrackIntervalSeconds = interval,
            };

        foreach ((SumoDriveSessionOptions options, string said) in new[]
                 {
                     (Given(track, null), "already written"),
                     (Given(null, 2.0), "no path was given"),
                     (Given(Path.Combine(_directory, "other.csv"), 0.0), "positive number of seconds"),
                     (Given("  ", null), "blank path"),
                 })
        {
            CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
                () => SumoDriveSession.Start(options).Dispose());
            Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
            Assert.Contains(said, refused.Message);
        }

        Assert.Equal("another run's track\n", File.ReadAllText(track));
    }

    [Fact]
    public void ASummaryIsNamedAfterItsTrack()
    {
        Assert.Equal(Path.Combine("truth", "world_truth_track.summary.json"),
                     WorldTruthTrackWriter.SummaryPathFor(Path.Combine("truth", "world_truth_track.csv")));
    }

    private static SumoDriveSessionOptions WorldLess(SyntheticWorld world, string scenario, Watcher watcher)
    {
        var options = new SumoDriveSessionOptions(
            scenario, world.PackagePath, CoSimFixtures.VehicleCatalogue, "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            SumoStepOverrideSeconds = StepSeconds,
        };
        options.StepObservers.Add(watcher);
        return options;
    }

    /// <summary>The track as it stands on disk while the session still holds it open.</summary>
    private static string ReadWhileOpen(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonDocument ReadSummary(string track)
    {
        using var file = new FileStream(WorldTruthTrackWriter.SummaryPathFor(track), FileMode.Open,
                                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(file);
    }

    /// <summary>
    /// The rows a reader takes from a track: every line that ends in a line break, the first of them the
    /// header; a last line without one was cut off, and is left off.
    /// </summary>
    private static List<Row> ReadRows(string text, out string[] header)
    {
        header = [];
        int end = text.LastIndexOf('\n');
        if (end < 0)
        {
            return [];
        }

        string[] lines = text[..end].Split('\n');
        header = ParseLine(lines[0]);
        string[] columns = header;
        return [.. lines.Skip(1).Select(line => new Row(columns, ParseLine(line), line))];
    }

    /// <summary>One CSV line, with quoted fields unquoted.</summary>
    private static string[] ParseLine(string line)
    {
        List<string> fields = [];
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }

    /// <summary>One SUMO frame with one vehicle standing at the origin, as the session would tell it.</summary>
    private static SumoStepRecord OneVehicleAt(double frameSeconds)
    {
        var standing = new CoSimVehicleFrame("parked", 0.0, 0.0, 90.0, 0.0, "ahead", "ahead_0", 10.0, "car",
                                             default);
        return new SumoStepRecord(
            0, frameSeconds, false, SumoStepEvents.None(frameSeconds), [], [], [],
            new Dictionary<string, CoSimVehicleFrame> { [standing.Id] = standing }, [standing.Id],
            new AdmissionPass(0, frameSeconds, 1, 0, 0, 1), null!);
    }

    /// <summary>An elevation column of a row, read back as written.</summary>
    private static double Elevation(Row row, string column) =>
        double.Parse(row[column], CultureInfo.InvariantCulture);

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string F(double value, string format) =>
        (value == 0.0 ? 0.0 : value).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>One row of a track, by column name.</summary>
    private sealed class Row(string[] columns, string[] fields, string line)
    {
        public string Line { get; } = line;

        public string this[string column]
        {
            get
            {
                Assert.Equal(columns.Length, fields.Length);
                return fields[Array.IndexOf(columns, column)];
            }
        }
    }

    /// <summary>A SUMO frame as an observer beside the track was told of it, its states copied.</summary>
    private sealed record StepSeen(double FrameSeconds, Dictionary<string, CoSimVehicleFrame> Frames);

    /// <summary>A rendered frame as an observer beside the track was told of it.</summary>
    private sealed record FrameSeen(string Frame, double SimulatedTimeSeconds, Dictionary<string, uint> Bodies,
                                    double? SunElevationDegrees, double? SunCorrectedElevationDegrees);

    /// <summary>
    /// The track on disk as a rendered frame left it: the vehicles SUMO had at the SUMO frame it renders,
    /// or null where it renders none, the rows the track says it has written, whether the file ends in a
    /// line break, the rows on disk and how many of them are the frame's own, and the summary's
    /// <c>ended</c>.
    /// </summary>
    private sealed record FrameOnDisk(string Frame, int? Vehicles, long RowsWritten, bool EndsInALineBreak,
                                      int RowsOnDisk, int RowsOfTheFrame, JsonValueKind Ended);

    /// <summary>
    /// An observer registered after the track, which the session tells first, reading the track off the
    /// disk as each rendered frame left it, as a reader watching the run would, and keeping each SUMO
    /// frame's vehicle count to say how many rows the frame that renders it should have put there.
    /// </summary>
    private sealed class DiskReader(string track) : ISumoStepObserver
    {
        private readonly List<(double FrameSeconds, int Vehicles)> _sumoFrames = [];

        public WorldTruthTrackWriter? Track { get; set; }

        public List<FrameOnDisk> Frames { get; } = [];

        public void OnSumoStep(SumoStepRecord step) => _sumoFrames.Add((step.FrameSeconds, step.Frames.Count));

        public void OnFrameRendered(RenderedFrameRecord frame)
        {
            string number = frame.Frame.ToString(CultureInfo.InvariantCulture);
            string onDisk = ReadWhileOpen(track);
            List<Row> rows = ReadRows(onDisk, out _);
            using JsonDocument summary = ReadSummary(track);
            int? vehicles = null;
            foreach ((double frameSeconds, int count) in _sumoFrames)
            {
                if (Math.Abs(frameSeconds - frame.SimulatedTimeSeconds) < 1e-6)
                {
                    vehicles = count;
                }
            }

            Frames.Add(new FrameOnDisk(number, vehicles, Track!.Rows, onDisk.EndsWith('\n'), rows.Count,
                                       rows.Count(row => row["frame"] == number),
                                       summary.RootElement.GetProperty("ended").ValueKind));
        }

        public void OnSessionEnded(SessionEndRecord end)
        {
        }
    }

    /// <summary>
    /// An observer registered beside the track, keeping what the session told it: each SUMO frame's
    /// clock and states, each rendered frame's bodies and sun, and each vehicle's departure as SUMO
    /// listed it and as SUMO stamps it.
    /// </summary>
    private sealed class Watcher : ISumoStepObserver
    {
        public List<StepSeen> Steps { get; } = [];

        public List<FrameSeen> Frames { get; } = [];

        public Dictionary<string, (double ListedAt, double? Stamped)> Departures { get; } = [];

        public bool AskDepartures { get; init; }

        public void OnSumoStep(SumoStepRecord step)
        {
            Steps.Add(new StepSeen(step.FrameSeconds, new Dictionary<string, CoSimVehicleFrame>(step.Frames)));
            if (AskDepartures)
            {
                foreach (string vehicleId in step.Events.Departed)
                {
                    Departures[vehicleId] = (step.FrameSeconds, step.Vehicles.Departure(vehicleId));
                }
            }
        }

        public void OnFrameRendered(RenderedFrameRecord frame) =>
            Frames.Add(new FrameSeen(
                frame.Frame.ToString(CultureInfo.InvariantCulture),
                frame.SimulatedTimeSeconds,
                frame.RenderSet?.ByActor.Values.ToDictionary(vehicle => vehicle.SumoId, vehicle => vehicle.ActorId)
                    ?? [],
                frame.SunElevationDegrees,
                frame.SunCorrectedElevationDegrees));

        public void OnSessionEnded(SessionEndRecord end)
        {
        }
    }
}
