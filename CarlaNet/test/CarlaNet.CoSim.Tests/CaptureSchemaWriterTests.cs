using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarlaNet.CoSim.Schemas;
using CarlaNet.Recording;
using CarlaNet.Types.Provenance;
using Xunit.Abstractions;
using static CarlaNet.CoSim.Tests.PlanRows;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What the writers write keeps its published schema: every PNG text chunk in every shape, and the run
/// manifest, the world truth track and its summary of real runs against a real SUMO -- drawn and world-less,
/// supervised, teleporting, with a defect, and stopped.
/// </summary>
/// <remarks>
/// <para>The JSON is checked here with <see cref="SchemaCheck"/>, the subset of JSON Schema the published
/// schemas use; the Python suite validates the recorded files with a full validator.</para>
///
/// <para>After a deliberate change to what a writer writes, record these runs' files as the fixtures
/// <c>CarlaControl/test/test_capture_schemas.py</c> validates, by running these tests with
/// <c>CARLANET_RECORD_CAPTURE_SCHEMA_FIXTURES</c> naming the fixtures directory
/// (<c>CarlaControl/test/fixtures/capture_schemas</c>). Paths in the manifest's opening row are cut to file
/// names; read the diff before committing it.</para>
/// </remarks>
public sealed class CaptureSchemaWriterTests : IDisposable
{
    private const int StepLimit = 2000;

    private static readonly SchemaCheck Manifest = new(RunManifestSchema.Schema());
    private static readonly SchemaCheck Summary = new(WorldTruthTrackSchemas.Summary());

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
                                                      "carlanet-schemas-" + Guid.NewGuid().ToString("n"));

    public CaptureSchemaWriterTests(ITestOutputHelper output)
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

    // ---- the PNG text chunks ---------------------------------------------------------------------------------

    [Fact]
    public void EveryChunkAStillCarriesKeepsItsSchemaWithAndWithoutItsOptionalFields()
    {
        var producer = new ProducerRecord("carlacontrol.CaptureSession", "0.10.0", "0.10.0+g1a2b3c4d5",
                                          ServerBuildIdentity.NotAnswered("built before the call"), "1.27.0",
                                          new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
        AssertChunk(PngChunkSchemas.CaptureKeyword,
                    new CaptureIdentity(23674, 194.179475, "cap-1", "Arapahoe_I25", 42) { Producer = producer }.ToJson());
        AssertChunk(PngChunkSchemas.CaptureKeyword, new CaptureIdentity(0, 0.0).ToJson());
        AssertChunk(PngChunkSchemas.CaptureKeyword,
                    new CaptureIdentity(1, 0.05, "run-1") { Producer = producer.Untimed() with { Server = null } }.ToJson());

        AssertChunk(PngChunkSchemas.SolarKeyword,
                    SolarMetadata.ToJson([7.45, 2026, 9, 29, -6, 39.59431, -104.88449, 5.549, 97.827, 0, 0, 5.694]));
        AssertChunk(PngChunkSchemas.SolarKeyword,
                    SolarMetadata.ToJson([7.45, 2026, 9, 29, -6, 39.59431, -104.88449, 5.549, 97.827, 1, 60]));
        // A sun the engine could not compute has no band.
        string unbanded = SolarMetadata.ToJson([0, 2026, 1, 1, 0, 0, 0, -180, 0, 0, 0]);
        Assert.DoesNotContain("illumination_band", unbanded);
        AssertChunk(PngChunkSchemas.SolarKeyword, unbanded);

        AssertChunk(PngChunkSchemas.IlluminationKeyword,
                    new IlluminationDeclaration("advance", true, true)
                    {
                        Rate = 60, EpochDigest = new string('a', 64), EpochCivil = "2026-09-29T07:26:00-06:00",
                        UtcOffsetHours = -6, DeclaredCivil = "2026-09-29T07:27:00.5-06:00",
                        DeclaredUtc = "2026-09-29T13:27:00.5Z", SunDeclared = "2026-09-29T07:27:00-06:00",
                        SunElevationDeclaredDegrees = 5.5, SunCorrectedElevationDeclaredDegrees = 5.6,
                        DeclaredElevationKind = "refraction_corrected", ResidualClockSeconds = -0.001,
                        ResidualDegrees = 0.0001, ResidualCorrectedDegrees = 0,
                    }.ToJson());
        AssertChunk(PngChunkSchemas.IlluminationKeyword,
                    new IlluminationDeclaration("freeze_at", false, false) { FreezeAtCivilTime = "16:30:00" }.ToJson());
        AssertChunk(PngChunkSchemas.IlluminationKeyword, new IlluminationDeclaration("ignore", false, false).ToJson());

        AssertChunk(PngChunkSchemas.SensorKeyword, SensorMetadata.ToJson(new SensorPose(
            "a-f-A-M-F-Q", "Check_Overhead_1", "CARLA-SENSOR-107", 39.5971345, -104.8891363, 1818.06, -0.63,
            90, -70.346, 0, 90, 0, 1920, 1080, 2058.73, 2058.73, 960, 540, 50, 29.395,
            "sensor.camera.rgb", "pinhole", "none")));
    }

    [Theory]
    [InlineData("carla:capture", """{"tick":23674,"sim_time_s":194.179475,"run_id":"cap-20261007-173433-41f49b"}""")]
    [InlineData("carla:solar", """{"solar_time":7.45,"date":"2026-09-29","time_zone":-6,"lat":39.59431,"lon":-104.88449,"sun_elevation_deg":5.549088,"sun_azimuth_deg":97.826561,"advancing":false,"rate":0,"sun_corrected_elevation_deg":5.694,"illumination_band":"golden","illumination_band_elevation":"refraction_corrected"}""")]
    [InlineData("carla:illumination", """{"policy":"freeze_at_window_start","epoch_honoured":true,"audited":true,"epoch_digest":"b92057175df8fcf7d3f5070d76aac03c301b672e891bd7b044b888efa8a24831","epoch_civil":"2026-09-29T07:26:00-06:00","utc_offset_hours":-6,"declared_civil":"2026-09-29T07:27:00-06:00","declared_utc":"2026-09-29T13:27:00Z","sun_declared":"2026-09-29T07:27:00-06:00","sun_elevation_declared_deg":5.5491,"sun_corrected_elevation_declared_deg":5.694,"declared_elevation":"refraction_corrected","residual_clock_s":0.001,"residual_deg":0,"residual_corrected_deg":0}""")]
    [InlineData("carla:sensor", """{"uid":"CARLA-SENSOR-107","type":"a-f-A-M-F-Q","callsign":"Check_Overhead_1","lat":39.5971345,"lon":-104.8891363,"hae":1818.06,"align_offset_m":-0.63,"az_deg":90,"el_deg":-70.346,"roll_deg":0,"course_deg":90,"speed_mps":0.00,"intrinsics":{"width":1920,"height":1080,"fx":2058.73,"fy":2058.73,"cx":960,"cy":540,"hfov_deg":50,"vfov_deg":29.395,"model":"pinhole","distortion":"none","sensor_model":"sensor.camera.rgb"}}""")]
    public void AChunkWrittenBeforeChunksCarriedAFormatVersionIsVersionOneAndValid(string keyword, string legacy)
    {
        AssertChunk(keyword, legacy);
    }

    [Theory]
    [InlineData("carla:capture", """{"format_version":2,"tick":1,"sim_time_s":0}""")]
    [InlineData("carla:capture", """{"tick":1,"sim_time_s":0,"frame":1}""")]
    [InlineData("carla:capture", """{"sim_time_s":0}""")]
    [InlineData("carla:solar", """{"solar_time":7.45,"date":"2026-09-29","time_zone":-6,"lat":39.59431,"lon":-104.88449,"sun_elevation_deg":5.549,"sun_azimuth_deg":97.8,"advancing":false,"rate":0,"illumination_band":"golden"}""")]
    [InlineData("carla:solar", """{"solar_time":7.45,"date":"2026-09-29","time_zone":-6,"lat":39.59431,"lon":-104.88449,"sun_elevation_deg":5.549,"sun_azimuth_deg":97.8,"advancing":false,"rate":0,"illumination_band":"dusk","illumination_band_elevation":"geometric"}""")]
    [InlineData("carla:illumination", """{"policy":"noon","epoch_honoured":true,"audited":true}""")]
    public void AChunkTheWriterNeverWritesIsRefused(string keyword, string json)
    {
        Assert.NotEmpty(new SchemaCheck(PngChunkSchemas.Schema(keyword)).Problems(json));
    }

    [Fact]
    public void AStringWithAControlCharacterAQuoteOrABackslashIsWrittenAsValidJsonAndReadsBackAsWritten()
    {
        const string awkward = "run \"7\"\tC:\\captures\nline two\u0001\u001f, +05:30, café";

        string capture = new CaptureIdentity(1, 0.05, awkward, awkward, 3).ToJson();
        string illumination = new IlluminationDeclaration(awkward, true, false)
        {
            FreezeAtCivilTime = awkward, EpochCivil = awkward, DeclaredCivil = awkward,
        }.ToJson();
        string sensor = SensorMetadata.ToJson(new SensorPose(
            awkward, awkward, awkward, 39.5971345, -104.8891363, 1818.06, -0.63, 90, -70.346, 0, 90, 0, 1920, 1080,
            2058.73, 2058.73, 960, 540, 50, 29.395, awkward, awkward, awkward));

        foreach ((string chunk, string[] fields) in new[]
                 {
                     (capture, new[] { "run_id", "scenario_id" }),
                     (illumination, new[] { "policy", "freeze_at_civil_time", "epoch_civil", "declared_civil" }),
                     (sensor, new[] { "uid", "type", "callsign" }),
                 })
        {
            Assert.DoesNotContain('\n', chunk);
            Assert.DoesNotContain('\t', chunk);
            Assert.DoesNotContain('\u0001', chunk);
            using JsonDocument read = JsonDocument.Parse(chunk);
            foreach (string field in fields)
            {
                Assert.Equal(awkward, read.RootElement.GetProperty(field).GetString());
            }
        }

        using JsonDocument intrinsics = JsonDocument.Parse(sensor);
        Assert.Equal(awkward, intrinsics.RootElement.GetProperty("intrinsics").GetProperty("model").GetString());
        // The minimum JSON requires is escaped, and nothing more: an offset reads as written.
        Assert.Contains("+05:30", capture);
        // Every number keeps the form the chunk gives it.
        Assert.Contains("\"sim_time_s\":0.05,", capture);
        Assert.Contains("\"lat\":39.5971345,", sensor);
        Assert.Contains("\"speed_mps\":0.00,", sensor);
    }

    // ---- the run manifest and the world truth track ------------------------------------------------------------

    [RequiresSumoFact]
    public void ADrawnRunSManifestTrackAndSummaryKeepTheirSchemas()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "drawn", "manifest.jsonl");
        string track = Path.Combine(_directory, "drawn", "world_truth_track.csv");
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.SuccessionScenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = new RecordedWorld { Loaded = world.AsLoaded() },
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            RunManifestPath = manifest,
            RunManifestHeader = """{"run_id": "cap-test", "channels": [{"channel": 0, "sensor_id": "deck"}]}""",
            WorldTruthTrackPath = track,
        };

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            session.RunManifest!.PlaceSensor("deck", 4121,
                                             new CameraExposure("Default", CameraExposure.Manual, 100, 320, 4, 0));
            session.RunManifest!.PlaceSensor("plain", 4122);
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
            }
        }

        AssertRows(manifest, ["manifest_opened", "sensor_placed", "render_admitted", "render_released",
                              "solar_window_open", "solar_window_end", "manifest_closed"]);
        AssertTrack(track, rendered: true);
        Record("drawn", manifest, track);
    }

    [RequiresSumoFact]
    public void ASupervisedRunSPlanAndIntervalRowsKeepTheSchema()
    {
        using CompiledFixture compiled = SupervisionBinderSessionTests.Supervised();
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        string manifest = Path.Combine(_directory, "supervised", "manifest.jsonl");
        string track = Path.Combine(_directory, "supervised", "world_truth_track.csv");
        SumoDriveSessionOptions options = WorldLess(compiled.Scenario, world);
        options.RunManifestPath = manifest;
        options.WorldTruthTrackPath = track;
        Drive(options);

        AssertRows(manifest, ["manifest_opened", "instance", "series", "cohort", "interval_opened",
                              "interval_closed", "render_admitted", "render_released", "manifest_closed"]);
        AssertTrack(track, rendered: false);
        Record("supervised", manifest, track);
    }

    [RequiresSumoFact]
    public void ATeleportADefectAndAStoppedRunKeepTheSchema()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        string teleporting = Path.Combine(_directory, "teleport", "manifest.jsonl");
        SumoDriveSessionOptions jam = WorldLess(CoSimFixtures.JamScenario, world);
        jam.RunManifestPath = teleporting;
        jam.AllowTeleporting = true;
        Drive(jam);
        AssertRows(teleporting, ["teleport", "manifest_closed"]);

        // The dweller's stop anchored on a lane SUMO does not make it on.
        using CompiledFixture compiled = CompiledFixture.Write(routes: CoSimFixtures.SupervisedRoutes);
        compiled.WritePlan(WithRows(compiled.PlanDocument(),
            [
                Instance("standoff", "annotated", ["test:standoff"], [("dweller", "subject")],
                         Interval("dweller", "standoff", Anchor(Stop(0, "turn_east_0", 60.0), StopEnd(0, "turn_east_0", 60.0)),
                                  declaredDuration: 20.0)),
            ],
            entities: [Entity("dweller", "annotated", "Supervised/standoff"), Entity("parker", "unlabelled"), Entity("passer", "unlabelled")]));
        compiled.WriteLock(SolarLeaseTests.PortEpoch());
        string defective = Path.Combine(_directory, "defect", "manifest.jsonl");
        SumoDriveSessionOptions defect = WorldLess(compiled.Scenario, world);
        defect.RunManifestPath = defective;
        Drive(defect);
        AssertRows(defective, ["supervision_defect", "manifest_closed"]);

        string stopped = Path.Combine(_directory, "stopped", "manifest.jsonl");
        string track = Path.Combine(_directory, "stopped", "world_truth_track.csv");
        SumoDriveSessionOptions stopping = WorldLess(CoSimFixtures.DwellScenario, world);
        stopping.RunManifestPath = stopped;
        stopping.WorldTruthTrackPath = track;
        int ticks = 0;
        stopping.TickWorld = () => ++ticks <= 100;
        using (SumoDriveSession session = SumoDriveSession.Start(stopping))
        {
            Assert.Throws<CoSimSessionRefusedException>(() =>
            {
                while (session.Advance())
                {
                }
            });
        }

        AssertRows(stopped, ["manifest_closed"]);
        Assert.Contains("\"ended\":\"run_stopped\"", File.ReadAllLines(stopped)[^1]);
        AssertTrack(track, rendered: false);
        Record("teleport", teleporting, null);
        Record("defect", defective, null);
        Record("stopped", stopped, track);
    }

    // ---- building blocks ------------------------------------------------------------------------------------------

    private static void AssertChunk(string keyword, string json)
    {
        List<string> problems = new SchemaCheck(PngChunkSchemas.Schema(keyword)).Problems(json);
        Assert.True(problems.Count == 0, $"{keyword}: {string.Join("; ", problems)}\n{json}");
    }

    private static SumoDriveSessionOptions WorldLess(string scenario, SyntheticWorld world) =>
        new(scenario, world.PackagePath, CoSimFixtures.VehicleCatalogue, "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            SumoStepOverrideSeconds = 1.0,
        };

    private static void Drive(SumoDriveSessionOptions options)
    {
        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < StepLimit && session.Advance(); step++)
        {
        }
    }

    /// <summary>Every row of the manifest keeps the schema, and it holds at least the kinds named.</summary>
    private void AssertRows(string manifest, IEnumerable<string> kinds)
    {
        string[] lines = File.ReadAllLines(manifest);
        HashSet<string> seen = [];
        List<string> problems = [];
        foreach (string line in lines)
        {
            JsonNode row = JsonNode.Parse(line)!;
            seen.Add(row["row"]!.GetValue<string>());
            problems.AddRange(Manifest.Problems(row).Select(problem => $"{row["row"]}: {problem}"));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Subset(seen, kinds.ToHashSet());
        Assert.Equal(RunManifestWriter.OpenedRow, JsonNode.Parse(lines[0])!["row"]!.GetValue<string>());
        Assert.Equal(RunManifestWriter.ClosedRow, JsonNode.Parse(lines[^1])!["row"]!.GetValue<string>());
        _output.WriteLine($"{manifest}: {lines.Length} rows, {string.Join(", ", seen)}");
    }

    /// <summary>The track's every row keeps its Table Schema, and its summary its JSON Schema.</summary>
    private void AssertTrack(string track, bool rendered)
    {
        string[] lines = File.ReadAllLines(track);
        Assert.True(lines.Length > 1, "the track holds no row");
        List<string> problems = TableSchemaCheck.Problems(WorldTruthTrackSchemas.Table(), lines);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
        Assert.Equal(rendered, lines.Skip(1).Any(line => line.Contains(",rendered,", StringComparison.Ordinal)));

        string summary = File.ReadAllText(WorldTruthTrackWriter.SummaryPathFor(track));
        List<string> wrong = Summary.Problems(summary);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong) + "\n" + summary);
        _output.WriteLine($"{track}: {lines.Length - 1} rows");
    }

    /// <summary>
    /// Record a run's files as the Python suite's fixtures, where the variable names their directory: the
    /// manifest with its opening row's paths cut to file names, and the track and summary as written.
    /// </summary>
    private static void Record(string name, string manifest, string? track)
    {
        if (Environment.GetEnvironmentVariable("CARLANET_RECORD_CAPTURE_SCHEMA_FIXTURES") is not { Length: > 0 } root)
        {
            return;
        }

        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        string[] lines = File.ReadAllLines(manifest);
        JsonObject opening = JsonNode.Parse(lines[0])!.AsObject();
        foreach ((string parent, string key) in new[] { ("scenario", "config_path"), ("scenario", "world_package"),
                                                        ("scenario", "lock_path"), ("plan", "path"), ("sumo", "binary") })
        {
            if (opening[parent]?[key] is JsonValue value && value.GetValue<string>() is { Length: > 0 } path)
            {
                opening[parent]![key] = Path.GetFileName(path);
            }
        }

        if (opening["world_truth_track"] is JsonValue written && written.GetValue<string>() is { Length: > 0 } trackPath)
        {
            opening["world_truth_track"] = Path.GetFileName(trackPath);
        }

        // Written back as the writer writes it, a version's '+' unescaped.
        lines[0] = opening.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(directory, "manifest.jsonl"), string.Join("\n", lines) + "\n");
        if (track is not null)
        {
            File.Copy(track, Path.Combine(directory, "world_truth_track.csv"), overwrite: true);
            File.Copy(WorldTruthTrackWriter.SummaryPathFor(track),
                      Path.Combine(directory, "world_truth_track.summary.json"), overwrite: true);
        }
    }
}
