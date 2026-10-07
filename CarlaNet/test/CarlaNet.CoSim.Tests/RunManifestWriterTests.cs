using System.Globalization;
using System.Text;
using System.Text.Json;
using CarlaNet.Recording;
using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The run manifest: rows appended as the run goes, each a JSON object on a line of its own and flushed
/// as it is written, opened with what the run is and closed by a terminal row on every way a run ends.
/// </summary>
/// <remarks>
/// <para>Run against a real SUMO on the fixture cross, with no CARLA at all or with the recording world
/// double. What each row is checked against is what the session handed out beside it -- the intervals
/// <see cref="SumoDriveSessionOptions.OnRelease"/> is given, the events an observer is told at TraCI's
/// clock, SUMO's own stamp for a departure, asked on demand -- read through the seam rather than through
/// the writer's own code.</para>
/// </remarks>
public sealed class RunManifestWriterTests : IDisposable
{
    private const double StepSeconds = 1.0;
    private const int StepLimit = 2000;

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
                                                      "carlanet-manifest-" + Guid.NewGuid().ToString("n"));

    public RunManifestWriterTests(ITestOutputHelper output)
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
    public void AWorldLessRunIsOpenedAdmitsAndReleasesAtTraCISClockAndIsClosedLast()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "truth", "manifest.jsonl");
        var watcher = new Watcher();
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, watcher);
        options.RunManifestPath = manifest;
        options.RunManifestHeader = """{"run_id": "cap-test", "sensors": [{"channel": 0, "sensor_id": "deck"}]}""";
        options.OnRelease = released.Add;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }

            Assert.True(session.ScenarioFinished);
            Assert.False(session.RunManifest!.Closed);
        }

        List<JsonElement> rows = ReadRows(File.ReadAllText(manifest));
        Assert.Equal(RunManifestWriter.OpenedRow, Kind(rows[0]));
        Assert.Equal(RunManifestWriter.ClosedRow, Kind(rows[^1]));
        Assert.Single(rows, row => Kind(row) == RunManifestWriter.OpenedRow);
        Assert.Single(rows, row => Kind(row) == RunManifestWriter.ClosedRow);

        // The header the caller handed over, verbatim, beside what the session established itself.
        JsonElement opened = rows[0];
        Assert.Equal(1, opened.GetProperty("manifest_version").GetInt32());
        Assert.Equal("cap-test", opened.GetProperty("run").GetProperty("run_id").GetString());
        Assert.Equal("deck", opened.GetProperty("run").GetProperty("sensors")[0].GetProperty("sensor_id").GetString());
        Assert.Equal(CoSimFixtures.DwellScenario, opened.GetProperty("scenario").GetProperty("config_path").GetString());
        Assert.False(opened.GetProperty("scenario").GetProperty("compiled").GetBoolean());
        // Uncompiled, so no lock records a dry run, and nothing was accepted.
        Assert.Equal(JsonValueKind.Null, opened.GetProperty("scenario").GetProperty("dry_run_ran").ValueKind);
        Assert.False(opened.GetProperty("scenario").GetProperty("skipped_dry_run_accepted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, opened.GetProperty("plan").ValueKind);
        Assert.Equal(StepSeconds, opened.GetProperty("sumo").GetProperty("step_s").GetDouble());
        Assert.Equal(42, opened.GetProperty("sumo").GetProperty("seed").GetInt64());
        Assert.Equal("warn", opened.GetProperty("sumo").GetProperty("collision_action").GetString());
        Assert.Equal(20, opened.GetProperty("clock").GetProperty("world_ticks_per_sumo_step").GetInt32());
        Assert.False(opened.GetProperty("solar").GetProperty("epoch_declared").GetBoolean());

        // The rule the vehicle lights follow, at its defaults: driven, headlights on below +3 and off above
        // +6 degrees of the geometric sun, brake lights and turn signals from SUMO's signals. No policy binds
        // a sun here, so the headlights follow none and stay off.
        JsonElement lights = opened.GetProperty("vehicle_lights");
        Assert.True(lights.GetProperty("driven").GetBoolean());
        Assert.False(lights.GetProperty("headlights_follow_sun").GetBoolean());
        Assert.Equal(HeadlightRule.DefaultOnBelowDegrees, lights.GetProperty("headlights_on_below_deg").GetDouble());
        Assert.Equal(HeadlightRule.DefaultOffAboveDegrees, lights.GetProperty("headlights_off_above_deg").GetDouble());
        Assert.Equal("geometric", lights.GetProperty("headlights_elevation").GetString());
        Assert.Equal("sumo_signals", lights.GetProperty("brake_lights").GetString());
        Assert.Equal("sumo_signals", lights.GetProperty("turn_signals").GetString());

        // An admission for each vehicle, at the clock its departure was listed at, a step after SUMO's own
        // stamp, resolved on the frame stamped with that instant; no world, so no body drew either.
        List<JsonElement> admitted = [.. rows.Where(row => Kind(row) == "render_admitted")];
        Assert.Equal(["dweller", "parker"], admitted.Select(row => row.GetProperty("sumo_id").GetString()).Order(StringComparer.Ordinal));
        foreach (JsonElement admission in admitted)
        {
            string vehicleId = admission.GetProperty("sumo_id").GetString()!;
            (double listedAt, double? stamped) = watcher.Departures[vehicleId];
            Assert.Equal(listedAt, admission.GetProperty("sim_time_s").GetDouble());
            Assert.Equal(listedAt - StepSeconds, stamped!.Value, 9);
            Assert.Equal(("inserted", "measured_truck"),
                         (admission.GetProperty("reason").GetString(), admission.GetProperty("vtype_id").GetString()));
            Assert.Equal(JsonValueKind.Null, admission.GetProperty("actor_id").ValueKind);
            Assert.Equal(watcher.FrameAt(listedAt), admission.GetProperty("frame").GetUInt64());
        }

        // Every release is the interval the session handed out, after the vehicle's admission.
        AssertTheReleasesAreTheIntervals(rows, released);
        Assert.Equal(2, released.Count);

        // The sun at the window's opening and end: none, with no world, so no sun matched the declaration.
        JsonElement open = Assert.Single(rows, row => Kind(row) == "solar_window_open");
        Assert.Equal(watcher.FirstFrameSeconds, open.GetProperty("begin_s").GetDouble());
        Assert.True(open.GetProperty("no_sun").GetBoolean());
        JsonElement end = Assert.Single(rows, row => Kind(row) == "solar_window_end");
        Assert.Equal(rows.Count - 2, rows.IndexOf(end));
        Assert.True(end.GetProperty("solar_residual").GetProperty("audit_skipped").GetBoolean());
        Assert.False(end.GetProperty("sun_matched_declaration").GetBoolean());
        Assert.False(end.TryGetProperty("corpus_eligible", out _));

        // Closed at the session's end, SUMO having nothing left.
        JsonElement closed = rows[^1];
        Assert.Equal("scenario_finished", closed.GetProperty("ended").GetString());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("caller_reason").ValueKind);
        Assert.Equal(watcher.Steps[^1], closed.GetProperty("last_sumo_frame_s").GetDouble());
        Assert.Equal(2, closed.GetProperty("render_admitted").GetInt64());
        Assert.Equal(2, closed.GetProperty("render_released").GetInt64());
        Assert.Equal(rows.Count - 1, closed.GetProperty("rows_before").GetInt64());

        // No world, so nothing was compared: zero samples and no worst case, never a zero that reads as
        // a measurement.
        JsonElement divergence = closed.GetProperty("bridge_divergence");
        Assert.Equal(0, divergence.GetProperty("samples").GetInt64());
        Assert.Equal(0, divergence.GetProperty("vehicle_ticks_with_no_read_back").GetInt64());
        Assert.Equal(JsonValueKind.Null, divergence.GetProperty("worst_position_on").ValueKind);
        Assert.Equal(JsonValueKind.Null, divergence.GetProperty("worst_velocity_on").ValueKind);
    }

    [RequiresSumoFact]
    public void TheOpeningRowStatesTheRuleTheVehicleLightsFollowOrThatTheyAreNotDriven()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        // Driven, at thresholds of the run's own.
        string driven = Path.Combine(_directory, "driven.jsonl");
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        options.RunManifestPath = driven;
        options.HeadlightOnBelowDegrees = 2.5;
        options.HeadlightOffAboveDegrees = 7.0;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            session.Advance();
        }

        JsonElement lights = ReadRows(File.ReadAllText(driven))[0].GetProperty("vehicle_lights");
        Assert.True(lights.GetProperty("driven").GetBoolean());
        Assert.False(lights.GetProperty("headlights_follow_sun").GetBoolean());
        Assert.Equal(2.5, lights.GetProperty("headlights_on_below_deg").GetDouble());
        Assert.Equal(7.0, lights.GetProperty("headlights_off_above_deg").GetDouble());
        Assert.Equal("geometric", lights.GetProperty("headlights_elevation").GetString());
        Assert.Equal("sumo_signals", lights.GetProperty("brake_lights").GetString());
        Assert.Equal("sumo_signals", lights.GetProperty("turn_signals").GetString());

        // Not driven: every body keeps the lights it was spawned with, and the row says there is no rule.
        string undriven = Path.Combine(_directory, "undriven.jsonl");
        SumoDriveSessionOptions off = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        off.RunManifestPath = undriven;
        off.VehicleLampsDriven = false;
        using (SumoDriveSession session = SumoDriveSession.Start(off))
        {
            session.Advance();
        }

        JsonElement none = ReadRows(File.ReadAllText(undriven))[0].GetProperty("vehicle_lights");
        Assert.False(none.GetProperty("driven").GetBoolean());
        Assert.False(none.GetProperty("headlights_follow_sun").GetBoolean());
        foreach (string field in new[] { "headlights_on_below_deg", "headlights_off_above_deg", "headlights_elevation",
                                         "brake_lights", "turn_signals" })
        {
            Assert.Equal(JsonValueKind.Null, none.GetProperty(field).ValueKind);
        }

        // An inverted band is refused before any manifest is opened, so a manifest never states one.
        string refused = Path.Combine(_directory, "refused.jsonl");
        SumoDriveSessionOptions inverted = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        inverted.RunManifestPath = refused;
        inverted.HeadlightOnBelowDegrees = 6.0;
        inverted.HeadlightOffAboveDegrees = 3.0;
        Assert.Throws<CoSimSessionRefusedException>(() => SumoDriveSession.Start(inverted).Dispose());
        Assert.False(File.Exists(refused));
    }

    [RequiresSumoFact]
    public void TheTerminalRowCarriesTheBridgeDivergenceTheRunMeasuredAndNamesItsWorstVehicle()
    {
        // Three worlds: one that applies every pose exactly, one half a metre out on every body, and one
        // whose bodies report no velocity. The terminal row has to carry each run's own figures, equal to
        // the session report's, and name the vehicle and instant the worst was measured on.
        foreach ((string name, RecordedWorld carla) in new (string, RecordedWorld)[]
                 {
                     ("exact", new RecordedWorld()),
                     ("drifted", new RecordedWorld { TransformDrift = new CarlaNet.Types.Geom.Location(0.5f, 0f, 0f) }),
                     ("no-velocity", new RecordedWorld { ReportsNoKinematicVelocity = true }),
                 })
        {
            using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
            carla.Loaded = world.AsLoaded();
            string manifest = Path.Combine(_directory, $"{name}.jsonl");
            var options = new SumoDriveSessionOptions(
                CoSimFixtures.RightAngleTurnScenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
                "test://" + Guid.NewGuid().ToString("n"))
            {
                World = carla,
                Epoch = SolarLeaseTests.PortEpoch(),
                Illumination = IlluminationPolicy.FreezeAtWindowStart(),
                RunManifestPath = manifest,
            };

            CoSimRunReport report;
            using (SumoDriveSession session = SumoDriveSession.Start(options))
            {
                for (int step = 0; step < 200 && session.Advance(); step++)
                {
                }

                report = session.Report;
            }

            JsonElement closed = ReadRows(File.ReadAllText(manifest))[^1];
            Assert.Equal(RunManifestWriter.ClosedRow, Kind(closed));
            JsonElement divergence = closed.GetProperty("bridge_divergence");
            _output.WriteLine($"{name}: {divergence.GetRawText()}");

            // The block is the report's figures, and the report compared something.
            Assert.True(report.DivergenceSamples > 0, $"{name}: nothing was compared");
            Assert.Equal(report.DivergenceSamples, divergence.GetProperty("samples").GetInt64());
            Assert.Equal(report.VehicleTicksWithNoReadBack, divergence.GetProperty("vehicle_ticks_with_no_read_back").GetInt64());
            Assert.Equal(report.WorstPositionDivergenceMetres, divergence.GetProperty("worst_position_m").GetDouble());
            Assert.Equal(report.MeanPositionDivergenceMetres, divergence.GetProperty("mean_position_m").GetDouble());
            Assert.Equal(report.WorstYawDivergenceDegrees, divergence.GetProperty("worst_yaw_deg").GetDouble());
            Assert.Equal(report.WorstPitchDivergenceDegrees, divergence.GetProperty("worst_pitch_deg").GetDouble());
            Assert.Equal(report.WorstRollDivergenceDegrees, divergence.GetProperty("worst_roll_deg").GetDouble());
            Assert.Equal(report.WorstVelocityDivergenceMetresPerSecond, divergence.GetProperty("worst_velocity_m_per_s").GetDouble());
            Assert.Equal(report.MeanVelocityDivergenceMetresPerSecond, divergence.GetProperty("mean_velocity_m_per_s").GetDouble());
            Assert.Equal(report.MeanCommandedSpeedMetresPerSecond, divergence.GetProperty("mean_commanded_speed_m_per_s").GetDouble());

            // The worst position and the worst velocity each name the vehicle, the instant on TraCI's
            // clock, the tick and the body the report holds for them.
            foreach ((string field, PoseDivergence? worst) in new[]
                     {
                         ("worst_position_on", report.WorstDivergence),
                         ("worst_velocity_on", report.WorstVelocityDivergence),
                     })
            {
                PoseDivergence sample = worst!.Value;
                JsonElement on = divergence.GetProperty(field);
                Assert.Equal(sample.VehicleId, on.GetProperty("sumo_id").GetString());
                Assert.Equal(Math.Round(sample.SimulatedTimeSeconds, 6), on.GetProperty("sim_time_s").GetDouble());
                Assert.Equal(sample.TickIndex, on.GetProperty("tick").GetInt64());
                Assert.Equal(sample.Actor, on.GetProperty("actor_id").GetUInt32());
                Assert.NotEmpty(sample.VehicleId);
            }

            // And the figures say what each world did.
            double worstPosition = divergence.GetProperty("worst_position_m").GetDouble();
            double meanVelocity = divergence.GetProperty("mean_velocity_m_per_s").GetDouble();
            double meanCommanded = divergence.GetProperty("mean_commanded_speed_m_per_s").GetDouble();
            Assert.True(meanCommanded > 5.0, $"{name}: mean commanded {meanCommanded} m/s");
            switch (name)
            {
                case "exact":
                    Assert.True(worstPosition < 1e-3, $"worst {worstPosition} m");
                    Assert.True(meanVelocity < 1e-4, $"mean velocity {meanVelocity} m/s");
                    break;
                case "drifted":
                    Assert.Equal(0.5, worstPosition, 3);
                    Assert.True(meanVelocity < 1e-4, $"mean velocity {meanVelocity} m/s");
                    break;
                default:
                    Assert.True(worstPosition < 1e-3, $"worst {worstPosition} m");
                    Assert.Equal(meanCommanded, meanVelocity, 6);
                    break;
            }
        }
    }

    [RequiresSumoFact]
    public void ADrawnRunNamesTheBodyEveryAdmissionAndReleaseIsTheSessionSInterval()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        var watcher = new Watcher();
        List<RenderedVehicleInterval> released = [];
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.SuccessionScenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = new RecordedWorld { Loaded = world.AsLoaded() },
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            RunManifestPath = manifest,
            OnRelease = released.Add,
        };
        options.StepObservers.Add(watcher);

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            session.RunManifest!.PlaceSensor("deck", 4121);
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }
        }

        List<JsonElement> rows = ReadRows(File.ReadAllText(manifest));
        foreach (string row in new[] { "manifest_opened", "sensor_placed", "render_admitted", "render_released",
                                       "solar_window_open", "solar_window_end", "manifest_closed" })
        {
            _output.WriteLine(rows.First(entry => Kind(entry) == row).GetRawText());
        }

        JsonElement sensor = Assert.Single(rows, row => Kind(row) == "sensor_placed");
        Assert.Equal(("deck", 4121u), (sensor.GetProperty("sensor_id").GetString(), sensor.GetProperty("camera_actor_id").GetUInt32()));
        Assert.Equal(1, rows.IndexOf(sensor));

        // Every vehicle the run admitted was released by SUMO, and each release is the session's interval.
        AssertTheReleasesAreTheIntervals(rows, released);
        Assert.Equal(["first", "second", "unrenderable"], released.Select(interval => interval.VehicleId).Order(StringComparer.Ordinal));

        // A measured vehicle is admitted naming the body that first drew it, on the frame it first drew it,
        // which is the body its interval names; the unmeasured one names none.
        Dictionary<string, JsonElement> admitted = rows.Where(row => Kind(row) == "render_admitted")
            .ToDictionary(row => row.GetProperty("sumo_id").GetString()!);
        foreach (RenderedVehicleInterval interval in released)
        {
            JsonElement admission = admitted[interval.VehicleId];
            Assert.Equal(interval.AdmittedAtSeconds, admission.GetProperty("sim_time_s").GetDouble());
            if (interval.Actor == 0)
            {
                Assert.Equal("unrenderable", interval.VehicleId);
                Assert.Equal(JsonValueKind.Null, admission.GetProperty("actor_id").ValueKind);
                continue;
            }

            Assert.Equal(interval.Actor, admission.GetProperty("actor_id").GetUInt32());
            ulong frame = admission.GetProperty("frame").GetUInt64();
            Assert.Equal(interval.Actor, watcher.BodyOn(frame, interval.VehicleId));
            Assert.Null(watcher.BodyOn(frame - 1, interval.VehicleId));
        }

        // The sun at the window's first and last capture tick, as the world reported it, with its band; the
        // audit stayed in tolerance, and the run declared its epoch and bound the sun.
        JsonElement open = Assert.Single(rows, row => Kind(row) == "solar_window_open");
        FrameSeen first = watcher.Frames.First(frame => frame.IsCaptureTick);
        Assert.Equal(first.Sun!.Value.ElevationDegrees, open.GetProperty("sun_elevation_begin_deg").GetDouble());
        Assert.Equal(first.Sun.Value.CorrectedElevationDegrees!.Value,
                     open.GetProperty("sun_corrected_elevation_begin_deg").GetDouble());
        Assert.Equal(CarlaNet.Types.Illumination.IlluminationBands.NameOf(first.Sun.Value.CorrectedElevationDegrees.Value),
                     open.GetProperty("illumination_band_begin").GetString());
        Assert.Equal(0.0, open.GetProperty("declared_civil_vs_solar_delta_h").GetDouble(), 6);
        JsonElement end = Assert.Single(rows, row => Kind(row) == "solar_window_end");
        FrameSeen last = watcher.Frames.Last(frame => frame.IsCaptureTick);
        Assert.Equal(Math.Round(last.SimulatedTimeSeconds, 6), end.GetProperty("end_s").GetDouble());
        Assert.Equal(watcher.Frames.Count(frame => frame.IsCaptureTick), end.GetProperty("capture_ticks").GetInt64());
        Assert.True(end.GetProperty("solar_residual").GetProperty("within_tolerance").GetBoolean());
        Assert.True(end.GetProperty("sun_matched_declaration").GetBoolean());
        Assert.Equal("freeze_at_window_start",
                     rows[0].GetProperty("solar").GetProperty("illumination_in_force").GetProperty("policy").GetString());
        Assert.Equal(SolarLeaseTests.PortEpoch().Digest,
                     rows[0].GetProperty("solar").GetProperty("epoch_block_sha256").GetString());

        // A policy that binds the sun: the headlights follow it, which is what the drive's own report says.
        JsonElement lights = rows[0].GetProperty("vehicle_lights");
        Assert.True(lights.GetProperty("driven").GetBoolean());
        Assert.True(lights.GetProperty("headlights_follow_sun").GetBoolean());
    }

    [RequiresSumoFact]
    public void ACameraPlacedWithItsExposureCarriesItsProfileAndExposureAndOneWithoutCarriesNone()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        options.RunManifestPath = manifest;
        // What the camera's attributes give it: the GoPro profile, with the exposure set over it.
        var exposure = new CameraExposure("GoPro", CameraExposure.Manual, 200.0, 500.0, 5.6, -0.5);

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            session.RunManifest!.PlaceSensor("deck", 4121, exposure);
            session.RunManifest!.PlaceSensor("Camera_7", 4122, null);
            session.RunManifest!.PlaceSensor("plain", 4123);
        }

        List<JsonElement> placed = [.. ReadRows(File.ReadAllText(manifest)).Where(row => Kind(row) == "sensor_placed")];
        Assert.Equal(["deck", "Camera_7", "plain"], placed.Select(row => row.GetProperty("sensor_id").GetString()));
        _output.WriteLine(placed[0].GetRawText());

        JsonElement written = placed[0].GetProperty("exposure");
        Assert.Equal("GoPro", written.GetProperty("post_process_profile").GetString());
        Assert.Equal("manual", written.GetProperty("method").GetString());
        Assert.Equal(200.0, written.GetProperty("iso").GetDouble());
        Assert.Equal(1.0 / 500.0, written.GetProperty("shutter_s").GetDouble(), 12);
        Assert.Equal(5.6, written.GetProperty("fstop").GetDouble());
        Assert.Equal(-0.5, written.GetProperty("compensation_ev").GetDouble());
        Assert.Equal(exposure.Ev100!.Value, written.GetProperty("ev100").GetDouble(), 12);
        Assert.False(placed[1].TryGetProperty("exposure", out _));
        Assert.False(placed[2].TryGetProperty("exposure", out _));
    }

    [RequiresSumoFact]
    public void ATeleportIsWrittenAtTheClockSumoListedItAt()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        var watcher = new Watcher();
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.JamScenario, watcher);
        options.RunManifestPath = manifest;
        options.AllowTeleporting = true;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }
        }

        List<JsonElement> rows = ReadRows(File.ReadAllText(manifest));
        JsonElement teleport = Assert.Single(rows, row => Kind(row) == "teleport");
        _output.WriteLine(teleport.GetRawText());
        Assert.Equal("follower", teleport.GetProperty("sumo_id").GetString());
        Assert.Equal(Assert.Single(watcher.Teleports), teleport.GetProperty("sim_time_s").GetDouble());
        Assert.Equal(1, rows[^1].GetProperty("events").GetInt64());
    }

    [RequiresSumoFact]
    public void EveryRowIsOnDiskAsItIsWrittenAndAManifestCutOffAnywhereIsTheRowsBeforeTheCut()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        options.RunManifestPath = manifest;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
                string onDisk = ReadWhileOpen(manifest);
                Assert.EndsWith("\n", onDisk);
                Assert.Equal(session.RunManifest!.Rows, onDisk.Count(character => character == '\n'));
            }
        }

        byte[] whole = File.ReadAllBytes(manifest);
        List<string> all = [.. ReadRows(Encoding.UTF8.GetString(whole)).Select(row => row.GetRawText())];
        Assert.True(all.Count > 6, $"only {all.Count} rows to cut");
        List<int> cuts = [0, 1, whole.Length];
        for (int at = 0; at < whole.Length; at++)
        {
            if (whole[at] == (byte)'\n')
            {
                int nextBreak = Array.IndexOf(whole, (byte)'\n', at + 1);
                cuts.AddRange([at, at + 1, at + 2, nextBreak < 0 ? at + 1 : (at + nextBreak) / 2]);
            }
        }

        foreach (int cut in cuts.Where(cut => cut <= whole.Length).Distinct())
        {
            string prefix = Encoding.UTF8.GetString(whole, 0, cut);
            List<JsonElement> kept = ReadRows(prefix);
            Assert.Equal(prefix.Count(character => character == '\n'), kept.Count);
            Assert.Equal(all.Take(kept.Count), kept.Select(row => row.GetRawText()));
            if (kept.Count > 0)
            {
                Assert.Equal(RunManifestWriter.OpenedRow, Kind(kept[0]));
            }

            // Only the whole manifest ends with its terminal row.
            Assert.Equal(kept.Count == all.Count, kept.Count > 0 && Kind(kept[^1]) == RunManifestWriter.ClosedRow);
        }
    }

    [RequiresSumoFact]
    public void ACallerThatClosesTheManifestWhenItsWindowClosesIsTheLastRowAndTheEndAddsNothing()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
        options.RunManifestPath = manifest;

        long rows;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 6 && session.Advance(); step++)
            {
            }

            session.RunManifest!.Close("window_end");
            Assert.True(session.RunManifest.Closed);
            rows = session.RunManifest.Rows;

            // Advanced again after closing, as nothing should: the manifest takes no more rows.
            session.Advance();
            session.RunManifest.Close("again");
            Assert.Equal(rows, session.RunManifest.Rows);
        }

        List<JsonElement> written = ReadRows(File.ReadAllText(manifest));
        Assert.Equal(rows, written.Count);
        JsonElement closed = written[^1];
        Assert.Equal(RunManifestWriter.ClosedRow, Kind(closed));
        Assert.Equal(("caller_stopped", "window_end"),
                     (closed.GetProperty("ended").GetString(), closed.GetProperty("caller_reason").GetString()));
        // The vehicles still in the render set were never released: admitted, and no release row.
        Assert.Equal(2, closed.GetProperty("still_in_render_set").GetInt64());
        Assert.DoesNotContain(written, row => Kind(row) == "render_released");
    }

    [RequiresSumoFact]
    public void ACallerThatClosesTheManifestAfterTheLastStepWritesTheRemovalThatStepReadAtTheInstantTheEndReleasesIt()
    {
        // The last vehicle to leave leaves at the step that finishes the scenario, which the session reads one
        // step ahead of the rendered clock. The frame of its last step, which would draw it, is never rendered,
        // so the session's end releases it, at that frame's instant, the first that does not draw it; a caller
        // that closes the manifest before disposing the session has that release written all the same.
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        var watcher = new Watcher();
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, watcher);
        options.RunManifestPath = manifest;
        options.OnRelease = released.Add;

        double endsAt;
        int handedOutBeforeTheEnd;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }

            Assert.True(session.ScenarioFinished);
            endsAt = session.RenderedTimeSeconds;
            session.RunManifest!.Close("window_end");
            handedOutBeforeTheEnd = released.Count;
        }

        List<JsonElement> rows = ReadRows(File.ReadAllText(manifest));
        Assert.Equal(2, released.Count);
        Assert.True(handedOutBeforeTheEnd < released.Count, "every interval was handed out before the end");
        AssertTheReleasesAreTheIntervals(rows, released);
        Assert.Equal(endsAt, released[^1].ReleasedAtSeconds);
        Assert.Equal(watcher.Frames[^1].SimulatedTimeSeconds + 0.05, released[^1].ReleasedAtSeconds, 9);
        Assert.Equal(0, rows[^1].GetProperty("still_in_render_set").GetInt64());
        Assert.Equal(("scenario_finished", "window_end"),
                     (rows[^1].GetProperty("ended").GetString(), rows[^1].GetProperty("caller_reason").GetString()));
    }

    [RequiresSumoFact]
    public void ARunThatStopsIsClosedSayingWhereAndWhyWhetherOrNotItsCallerClosesIt()
    {
        foreach (bool callerCloses in (bool[])[false, true])
        {
            using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
            string manifest = Path.Combine(_directory, $"stopped-{callerCloses}.jsonl");
            SumoDriveSessionOptions options = WorldLess(world, CoSimFixtures.DwellScenario, new Watcher());
            options.RunManifestPath = manifest;
            int ticks = 0;
            options.TickWorld = () => ++ticks <= 100;

            using (SumoDriveSession session = SumoDriveSession.Start(options))
            {
                Assert.Throws<CoSimSessionRefusedException>(() =>
                {
                    while (session.Advance())
                    {
                    }
                });
                if (callerCloses)
                {
                    session.RunManifest!.Close("fault:CoSimSessionRefusedException");
                }
            }

            JsonElement closed = ReadRows(File.ReadAllText(manifest))[^1];
            Assert.Equal(RunManifestWriter.ClosedRow, Kind(closed));
            Assert.Equal(("run_stopped", "Window", "world-tick-timeout"),
                         (closed.GetProperty("ended").GetString(), closed.GetProperty("stage").GetString(),
                          closed.GetProperty("cause").GetString()));
            Assert.Equal(callerCloses ? "fault:CoSimSessionRefusedException" : null,
                         closed.GetProperty("caller_reason").GetString());
        }
    }

    [RequiresSumoFact]
    public void AStartRefusedAfterTheManifestWasOpenedLeavesNone()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.DwellScenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            // A world with no sun, under a policy that requires one: refused once SUMO has been
            // fast-forwarded, after the session -- and its manifest -- were made.
            World = new RecordedWorld { Loaded = world.AsLoaded(), Sun = null },
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            RunManifestPath = manifest,
        };

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options).Dispose());
        Assert.Equal(CoSimSessionStage.PreRoll, refused.Stage);
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public void AHeaderThatIsNotAnObjectAHeaderWithNoPathAManifestAlreadyWrittenAndTheTrackSPathAreRefused()
    {
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        File.WriteAllText(manifest, "another run's manifest\n");
        SumoDriveSessionOptions Given(string? path, string? header, string? track = null) =>
            new("missing.sumocfg", "missing.cwp", "missing.catalogue.json", "test://manifest")
            {
                TickWorld = () => true,
                RunManifestPath = path,
                RunManifestHeader = header,
                WorldTruthTrackPath = track,
            };

        string other = Path.Combine(_directory, "other.jsonl");
        foreach ((SumoDriveSessionOptions options, string said) in new[]
                 {
                     (Given(manifest, null), "already written"),
                     (Given(null, "{}"), "no path to write it to"),
                     (Given(other, "[1, 2]"), "not an object"),
                     (Given(other, "{run"), "not JSON"),
                     (Given("  ", null), "blank path"),
                     (Given(other, null, other), "both to be written"),
                 })
        {
            CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
                () => SumoDriveSession.Start(options).Dispose());
            Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
            Assert.Contains(said, refused.Message);
        }

        Assert.Equal("another run's manifest\n", File.ReadAllText(manifest));
        Assert.False(File.Exists(other));
    }

    /// <summary>
    /// Every release row is the interval the session handed out -- the vehicle, the instants, the body and
    /// the reason, in the order they were handed out -- and follows that vehicle's admission.
    /// </summary>
    private static void AssertTheReleasesAreTheIntervals(List<JsonElement> rows, List<RenderedVehicleInterval> released)
    {
        List<JsonElement> releases = [.. rows.Where(row => Kind(row) == "render_released")];
        Assert.Equal(released.Count, releases.Count);
        for (int index = 0; index < released.Count; index++)
        {
            RenderedVehicleInterval interval = released[index];
            JsonElement row = releases[index];
            Assert.Equal(interval.VehicleId, row.GetProperty("sumo_id").GetString());
            Assert.Equal(interval.AdmittedAtSeconds, row.GetProperty("admitted_s").GetDouble());
            Assert.Equal(interval.ReleasedAtSeconds, row.GetProperty("sim_time_s").GetDouble());
            Assert.Equal(interval.ReleaseReason == RenderSetReleaseReason.LeftTheSimulation ? "left_the_simulation" : "vanished",
                         row.GetProperty("reason").GetString());
            uint? actor = row.GetProperty("actor_id").ValueKind == JsonValueKind.Null ? null : row.GetProperty("actor_id").GetUInt32();
            Assert.Equal(interval.Actor == 0 ? null : interval.Actor, actor);
            int admittedAt = rows.FindIndex(entry => Kind(entry) == "render_admitted"
                                                     && entry.GetProperty("sumo_id").GetString() == interval.VehicleId);
            Assert.InRange(admittedAt, 1, rows.IndexOf(row) - 1);
        }
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

    private static string Kind(JsonElement row) => row.GetProperty("row").GetString()!;

    /// <summary>The manifest as it stands on disk while the session still holds it open.</summary>
    private static string ReadWhileOpen(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The rows a reader takes from a manifest: every line that ends in a line break, each one JSON object;
    /// a last line without one was cut off, and is left off.
    /// </summary>
    private static List<JsonElement> ReadRows(string text)
    {
        int end = text.LastIndexOf('\n');
        if (end < 0)
        {
            return [];
        }

        return [.. text[..end].Split('\n').Select(line =>
        {
            using JsonDocument parsed = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
            return parsed.RootElement.Clone();
        })];
    }

    /// <summary>A rendered frame as an observer beside the manifest was told of it.</summary>
    private sealed record FrameSeen(ulong Frame, double SimulatedTimeSeconds, bool IsCaptureTick,
                                    Dictionary<string, uint> Bodies, SolarReading? Sun);

    /// <summary>
    /// An observer registered beside the manifest, keeping what the session told it: each SUMO frame's
    /// clock, the teleports listed, each vehicle's departure as SUMO listed it and as SUMO stamps it, and
    /// each rendered frame's bodies and sun.
    /// </summary>
    private sealed class Watcher : ISumoStepObserver
    {
        public List<double> Steps { get; } = [];

        public List<double> Teleports { get; } = [];

        public List<FrameSeen> Frames { get; } = [];

        public Dictionary<string, (double ListedAt, double? Stamped)> Departures { get; } = [];

        public double FirstFrameSeconds => Frames[0].SimulatedTimeSeconds;

        public ulong FrameAt(double instant) =>
            Frames.First(frame => Math.Abs(frame.SimulatedTimeSeconds - instant) < 1e-6).Frame;

        public uint? BodyOn(ulong frame, string vehicleId) =>
            Frames.FirstOrDefault(seen => seen.Frame == frame) is { } seen && seen.Bodies.TryGetValue(vehicleId, out uint body)
                ? body
                : null;

        public void OnSumoStep(SumoStepRecord step)
        {
            Steps.Add(step.FrameSeconds);
            foreach (string vehicleId in step.Events.Departed)
            {
                Departures[vehicleId] = (step.FrameSeconds, step.Vehicles.Departure(vehicleId));
            }

            Teleports.AddRange(step.Events.TeleportsStarted.Select(_ => step.FrameSeconds));
        }

        public void OnFrameRendered(RenderedFrameRecord frame) =>
            Frames.Add(new FrameSeen(
                frame.Frame, frame.SimulatedTimeSeconds, frame.IsCaptureTick,
                frame.RenderSet?.ByActor.Values.ToDictionary(vehicle => vehicle.SumoId, vehicle => vehicle.ActorId) ?? [],
                frame.Sun));

        public void OnSessionEnded(SessionEndRecord end)
        {
        }
    }
}
