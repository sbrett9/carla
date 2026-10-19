using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarlaNet.Types.Supervision;
using Xunit.Abstractions;
using static CarlaNet.CoSim.Tests.PlanRows;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The run manifest's supervision rows: the plan the compile lock binds, row by row, and every interval as
/// the binder opens and closes it, against a real SUMO with no CARLA.
/// </summary>
/// <remarks>
/// What each row is checked against is the binder's own record of the interval, read through the binder
/// rather than through the writer's code: the manifest writes what the binder bound, at the binder's
/// instants, and a manifest cut off at any instant names as open exactly the intervals the binder held
/// open then.
/// </remarks>
public sealed class RunManifestSupervisionTests : IDisposable
{
    private const int StepLimit = 60;

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
                                                      "carlanet-manifest-supervision-" + Guid.NewGuid().ToString("n"));

    public RunManifestSupervisionTests(ITestOutputHelper output)
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
    public void ASupervisedRunWritesThePlanAndEveryIntervalAsTheBinderOpenedAndClosedIt()
    {
        using CompiledFixture compiled = SupervisionBinderSessionTests.Supervised();
        string manifest = Path.Combine(_directory, "manifest.jsonl");

        (SupervisionPlan plan, IReadOnlyList<SupervisionIntervalRecord> bound) = Drive(compiled, manifest, 1.0);

        List<JsonElement> rows = ReadRows(File.ReadAllText(manifest));
        _output.WriteLine(string.Join(Environment.NewLine, rows.Where(row => Kind(row) is not ("render_admitted" or "render_released"))
                                                                 .Select(row => row.GetRawText())));

        // The plan, as declared, straight after the opening row: its instances, its series, its cohorts.
        Assert.Equal(["manifest_opened", .. plan.Instances.Select(_ => "instance"), "series", "cohort"],
                     rows.Take(plan.Instances.Length + 3).Select(Kind));
        Assert.Equal(plan.Instances.Select(instance => instance.InstanceId),
                     rows.Where(row => Kind(row) == "instance").Select(row => row.GetProperty("instance_id").GetString()));
        JsonElement standoff = InstanceRow(rows, "Supervised/standoff");
        Assert.Equal(("annotated", "test:standoff"),
                     (standoff.GetProperty("supervision").GetString(), standoff.GetProperty("labels")[0].GetString()));
        // An instance row says what a vehicle does and nothing of a place: no realisation, no expectation.
        Assert.False(standoff.TryGetProperty("realisation", out _));
        Assert.False(standoff.TryGetProperty("expected", out _));
        JsonElement participant = Assert.Single(standoff.GetProperty("participants").EnumerateArray());
        Assert.Equal(("dweller", "subject"), (participant.GetProperty("participant").GetString(), participant.GetProperty("role").GetString()));
        JsonElement declared = Assert.Single(standoff.GetProperty("intervals").EnumerateArray());
        Assert.Equal(("dweller", "standoff", 20.0),
                     (declared.GetProperty("participant").GetString(), declared.GetProperty("phase").GetString(),
                      declared.GetProperty("declared_duration_s").GetDouble()));
        Assert.Equal(plan.Instances.Single(instance => instance.InstanceId == "Supervised/standoff").Intervals[0].Anchor!.Start.Spelled,
                     declared.GetProperty("anchor_start").GetString());
        Assert.Empty(InstanceRow(rows, "Supervised/haul").GetProperty("intervals").EnumerateArray());
        JsonElement series = rows.Single(row => Kind(row) == "series");
        Assert.Equal(("relief", 1), (series.GetProperty("series_id").GetString(), series.GetProperty("slots").GetArrayLength()));
        // A slot is a realised posting: it names the vehicle that realises it, and nothing expected of a place.
        JsonElement slot = Assert.Single(series.GetProperty("slots").EnumerateArray());
        Assert.Equal(("s1", "passer"), (slot.GetProperty("slot_key").GetString(), slot.GetProperty("entity_id").GetString()));
        Assert.False(slot.TryGetProperty("realised_by", out _));
        Assert.False(slot.TryGetProperty("expected_entity_id", out _));
        JsonElement cohort = rows.Single(row => Kind(row) == "cohort");
        Assert.Equal(("corridor", "annotated"), (cohort.GetProperty("flow_id").GetString(), cohort.GetProperty("supervision").GetString()));

        // Every interval the binder closed in the run has its rows, at the binder's instants: opened before
        // it closed, with the onsets the binder bound.
        SupervisionIntervalRecord[] closedInRun = [.. bound.Where(interval => interval.ClosedBy is not (ClosedBy.ScenarioEnd or ClosedBy.CaptureWindowEnd))];
        Assert.NotEmpty(closedInRun);
        foreach (SupervisionIntervalRecord interval in closedInRun)
        {
            JsonElement opened = Single(rows, "interval_opened", interval);
            JsonElement closed = Single(rows, "interval_closed", interval);
            Assert.True(rows.IndexOf(opened) < rows.IndexOf(closed), $"{interval.Phase} closed before it opened");
            Assert.Equal(interval.CommittedStartSeconds, NumberOrNull(opened, "committed_start_s"));
            Assert.Equal(interval.CommittedStartSeconds ?? interval.DeclaredStartSeconds, NumberOrNull(opened, "sim_time_s"));
            Assert.Equal((interval.DeclaredStartSeconds, interval.DeclaredEndSeconds, interval.DeclaredDurationSeconds),
                         (NumberOrNull(opened, "declared_start_s"), NumberOrNull(opened, "declared_end_s"),
                          NumberOrNull(opened, "declared_duration_s")));
            Assert.Equal(interval.BegunBeforeWindow, opened.GetProperty("begun_before_window").GetBoolean());
            Assert.Equal(interval.Role, opened.GetProperty("role").GetString());
            Assert.Equal(CoreVocabulary.Name(interval.ClosedBy!.Value), closed.GetProperty("closed_by").GetString());
            Assert.Equal(interval.ClosedAtSeconds, NumberOrNull(closed, "sim_time_s"));
            Assert.Equal(interval.CommittedEndSeconds, NumberOrNull(closed, "committed_end_s"));
            Assert.Equal(interval.NotDrawn.Count, closed.GetProperty("not_drawn").GetArrayLength());
        }

        JsonElement standoffOpened = Single(rows, "interval_opened", bound.Single(interval => interval.Phase == "standoff"));
        JsonElement standoffClosed = Single(rows, "interval_closed", bound.Single(interval => interval.Phase == "standoff"));
        Assert.Equal((11.0, 11.0, 20.0), (NumberOrNull(standoffOpened, "sim_time_s"), NumberOrNull(standoffOpened, "committed_start_s"),
                                          NumberOrNull(standoffOpened, "declared_duration_s")));
        Assert.Equal(("trigger", 31.0, 31.0), (standoffClosed.GetProperty("closed_by").GetString(),
                                               NumberOrNull(standoffClosed, "sim_time_s"), NumberOrNull(standoffClosed, "committed_end_s")));
        // Every interval row names its participant: there is no interval of no vehicle (06 §3.5).
        Assert.All(rows.Where(row => Kind(row) is "interval_opened" or "interval_closed"),
                   row => Assert.Equal(JsonValueKind.String, row.GetProperty("participant").ValueKind));

        // The terminal row: the counts, and the intervals the binder closed only once the session ended,
        // listed as open with the word it closed them with.
        JsonElement closing = rows[^1];
        Assert.Equal(RunManifestWriter.ClosedRow, Kind(closing));
        Assert.Equal((rows.Count(row => Kind(row) == "interval_opened"), rows.Count(row => Kind(row) == "interval_closed"), 0),
                     (closing.GetProperty("intervals_opened").GetInt32(), closing.GetProperty("intervals_closed").GetInt32(),
                      closing.GetProperty("supervision_defects").GetInt32()));
        Assert.DoesNotContain(rows, row => Kind(row) == "supervision_defect");
        SupervisionIntervalRecord[] closedAtTheEnd = [.. bound.Where(interval => interval.ClosedBy is ClosedBy.ScenarioEnd or ClosedBy.CaptureWindowEnd)];
        Assert.Equal(Triples(closedAtTheEnd), Triples(closing.GetProperty("open_intervals")));
        foreach (SupervisionIntervalRecord interval in closedAtTheEnd)
        {
            Assert.Equal(CoreVocabulary.Name(interval.ClosedBy!.Value), closing.GetProperty("open_intervals_close_as").GetString());
        }

        Assert.Equal(Triples(bound.Where(interval => interval.Status == SupervisionIntervalStatus.Planned)),
                     Triples(closing.GetProperty("never_opened")));
        Assert.Equal(PlanTriples(plan), RowTriples(rows));
    }

    [RequiresSumoFact]
    public void AManifestCutOffAnywhereNamesAsOpenTheIntervalsTheBinderHeldOpenThen()
    {
        using CompiledFixture compiled = SupervisionBinderSessionTests.Supervised();
        string manifest = Path.Combine(_directory, "manifest.jsonl");
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(compiled, world, manifest, 1.0);

        int sawOpen = 0;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit && session.Advance(); step++)
            {
                // A reader of the manifest as it stands, with no terminal row, takes every interval opened
                // and not closed as open at the cut: exactly those the binder holds open.
                List<JsonElement> written = ReadRows(ReadWhileOpen(manifest));
                Assert.NotEqual(RunManifestWriter.ClosedRow, Kind(written[^1]));
                (string, string?, string)[] inferred = OpenAtInterruption(written);
                Assert.Equal(Triples(session.SupervisionBinder!.Intervals.Where(interval => interval.Status == SupervisionIntervalStatus.Open)),
                             inferred);
                sawOpen += inferred.Length > 0 ? 1 : 0;
            }
        }

        Assert.True(sawOpen > 0, "no cut fell while an interval was open");

        // Cut through the middle of the row after the standoff's opening: the rows before the cut are each
        // whole, the cut row is left off, and the standoff is open at the interruption.
        byte[] whole = File.ReadAllBytes(manifest);
        string text = Encoding.UTF8.GetString(whole);
        int opened = text.IndexOf("\"row\":\"interval_opened\",\"sim_time_s\":11", StringComparison.Ordinal);
        Assert.True(opened > 0, "the standoff's opening row is not in the manifest");
        int lineEnd = text.IndexOf('\n', opened);
        int nextEnd = text.IndexOf('\n', lineEnd + 1);
        string prefix = text[..((lineEnd + nextEnd) / 2)];
        List<JsonElement> kept = ReadRows(prefix);
        Assert.Equal(prefix.Count(character => character == '\n'), kept.Count);
        Assert.Equal("interval_opened", Kind(kept[^1]));
        Assert.Contains(("Supervised/standoff", (string?)"dweller", "standoff"), OpenAtInterruption(kept));
    }

    [RequiresSumoFact]
    public void ACallerThatClosesTheManifestEarlyListsTheIntervalsStillOpenAndThoseNotYetOpened()
    {
        foreach ((int steps, string[] open, string[] notYet) in new[]
                 {
                     // At the fourth step nothing anchored has opened yet; the approach, declared 1 s to 3 s, has closed.
                     (4, Array.Empty<string>(), new[] { "exit", "standoff" }),
                     (12, new[] { "exit", "standoff" }, Array.Empty<string>()),
                 })
        {
            using CompiledFixture compiled = SupervisionBinderSessionTests.Supervised();
            using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
            string manifest = Path.Combine(_directory, $"closed-at-{steps}.jsonl");
            IReadOnlyList<SupervisionIntervalRecord> atTheClose;
            IReadOnlyList<SupervisionIntervalRecord> atTheEnd;
            using (SumoDriveSession session = SumoDriveSession.Start(Options(compiled, world, manifest, 1.0)))
            {
                for (int step = 0; step < steps && session.Advance(); step++)
                {
                }

                session.RunManifest!.Close("window_end");
                atTheClose = session.SupervisionBinder!.Intervals;
                session.Dispose();
                atTheEnd = session.SupervisionBinder.Intervals;
            }

            JsonElement closing = ReadRows(File.ReadAllText(manifest))[^1];
            Assert.Equal(RunManifestWriter.ClosedRow, Kind(closing));
            Assert.Equal(open, closing.GetProperty("open_intervals").EnumerateArray()
                                      .Select(row => row.GetProperty("phase").GetString()!).Order());
            Assert.Equal(notYet, closing.GetProperty("never_opened").EnumerateArray()
                                        .Select(row => row.GetProperty("phase").GetString()!).Order());
            Assert.Equal(Triples(atTheClose.Where(interval => interval.Status == SupervisionIntervalStatus.Open)),
                         Triples(closing.GetProperty("open_intervals")));

            // The binder closed them as the session ended, after the terminal row, with the word it gives.
            Assert.Equal("capture_window_end", closing.GetProperty("open_intervals_close_as").GetString());
            foreach (string phase in open)
            {
                Assert.Equal(ClosedBy.CaptureWindowEnd, atTheEnd.Single(interval => interval.Phase == phase).ClosedBy);
            }
        }
    }

    [RequiresSumoFact]
    public void ASeamDefectTheBinderFindsIsWrittenAsItIsFound()
    {
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
        string manifest = Path.Combine(_directory, "manifest.jsonl");

        (_, IReadOnlyList<SupervisionIntervalRecord> bound) = Drive(compiled, manifest, 1.0, defects: out IReadOnlyList<string> found);

        List<JsonElement> rows = ReadRows(File.ReadAllText(manifest));
        List<JsonElement> written = [.. rows.Where(row => Kind(row) == "supervision_defect")];
        _output.WriteLine(string.Join(Environment.NewLine, written.Select(row => row.GetRawText())));
        Assert.NotEmpty(found);
        Assert.Equal(found, written.Select(row => row.GetProperty("defect").GetString()));
        Assert.Contains("is anchored on lane turn_east_0, and SUMO made that stop on ahead_0", found[0]);
        Assert.Equal(found.Count, rows[^1].GetProperty("supervision_defects").GetInt32());

        // Found as the stop started, and written before the opening it was found at.
        JsonElement opened = Single(rows, "interval_opened", bound.Single());
        Assert.True(rows.IndexOf(written[0]) < rows.IndexOf(opened));
        Assert.Equal(NumberOrNull(opened, "committed_start_s"), NumberOrNull(written[0], "found_by_s"));
    }

    [RequiresSumoFact]
    public void TwoRunsOfOneScenarioWriteTheSameTriplesDifferingOnlyInTheirTimes()
    {
        // 06 D6.8, read from the manifests: a run at another step and a run whose window opens late name
        // the same (instance_id, participant, phase) triples as the first, the plan's own.
        using CompiledFixture compiled = SupervisionBinderSessionTests.Supervised();
        string wholePath = Path.Combine(_directory, "whole.jsonl");
        string finerPath = Path.Combine(_directory, "finer.jsonl");
        string latePath = Path.Combine(_directory, "late.jsonl");
        (SupervisionPlan plan, _) = Drive(compiled, wholePath, 1.0);
        Drive(compiled, finerPath, 0.5);
        Drive(compiled, latePath, 1.0, warmUpTo: 15.0);

        List<JsonElement> whole = ReadRows(File.ReadAllText(wholePath));
        List<JsonElement> finer = ReadRows(File.ReadAllText(finerPath));
        List<JsonElement> late = ReadRows(File.ReadAllText(latePath));
        Assert.Equal(PlanTriples(plan), RowTriples(whole));
        Assert.Equal(RowTriples(whole), RowTriples(finer));
        Assert.Equal(RowTriples(whole), RowTriples(late));

        // And the times did differ.
        Assert.NotEqual(OpenedAt(whole, "standoff"), OpenedAt(finer, "standoff"));
        Assert.NotEqual(BegunBeforeWindow(whole, "approach"), BegunBeforeWindow(late, "approach"));

        RecordFixtures([(wholePath, "supervised_step_1.0.jsonl"), (finerPath, "supervised_step_0.5.jsonl"),
                        (latePath, "supervised_window_15.jsonl")]);
    }

    /// <summary>
    /// After a deliberate change to what the writer writes, record these three manifests as the fixtures
    /// <c>CarlaControl/test/test_run_manifest_diff.py</c> reads, by running this test with
    /// <c>CARLANET_RECORD_RUN_MANIFEST_FIXTURES</c> naming the fixtures directory. The opening row's
    /// paths are cut to file names, as the fixtures carry them; read the diff before committing it.
    /// </summary>
    private static void RecordFixtures(IEnumerable<(string Written, string Fixture)> manifests)
    {
        if (Environment.GetEnvironmentVariable("CARLANET_RECORD_RUN_MANIFEST_FIXTURES") is not { Length: > 0 } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        foreach ((string written, string fixture) in manifests)
        {
            string[] lines = File.ReadAllLines(written);
            JsonNode opening = JsonNode.Parse(lines[0])!;
            foreach ((string parent, string key) in new[] { ("scenario", "config_path"), ("scenario", "world_package"),
                                                            ("scenario", "lock_path"), ("plan", "path") })
            {
                if (opening[parent]?[key] is { } value && value.GetValue<string>() is { Length: > 0 } path)
                {
                    opening[parent]![key] = Path.GetFileName(path);
                }
            }

            lines[0] = opening.ToJsonString();
            File.WriteAllText(Path.Combine(directory, fixture), string.Join("\n", lines) + "\n");
        }
    }

    private static (SupervisionPlan Plan, IReadOnlyList<SupervisionIntervalRecord> Bound) Drive(
        CompiledFixture compiled, string manifest, double stepSeconds, double? warmUpTo = null) =>
        Drive(compiled, manifest, stepSeconds, out _, warmUpTo);

    private static (SupervisionPlan Plan, IReadOnlyList<SupervisionIntervalRecord> Bound) Drive(
        CompiledFixture compiled, string manifest, double stepSeconds, out IReadOnlyList<string> defects,
        double? warmUpTo = null)
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(compiled, world, manifest, stepSeconds);
        options.WarmUpToSimulatedSecond = warmUpTo ?? 0.0;
        SupervisionBinder binder;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < StepLimit / stepSeconds && session.Advance(); step++)
            {
            }

            binder = session.SupervisionBinder!;
        }

        defects = [.. binder.Defects];
        return (binder.Plan, binder.Intervals);
    }

    private static SumoDriveSessionOptions Options(CompiledFixture compiled, SyntheticWorld world, string manifest,
                                                   double stepSeconds) =>
        new(compiled.Scenario, world.PackagePath, CoSimFixtures.VehicleCatalogue, "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            SumoStepOverrideSeconds = stepSeconds,
            RunManifestPath = manifest,
        };

    private static string Kind(JsonElement row) => row.GetProperty("row").GetString()!;

    private static JsonElement InstanceRow(List<JsonElement> rows, string instanceId) =>
        rows.Single(row => Kind(row) == "instance" && row.GetProperty("instance_id").GetString() == instanceId);

    private static JsonElement Single(List<JsonElement> rows, string kind, SupervisionIntervalRecord interval) =>
        rows.Single(row => Kind(row) == kind && TripleOf(row) == (interval.InstanceId, interval.EntityId, interval.Phase));

    private static double? NumberOrNull(JsonElement row, string name) =>
        row.GetProperty(name) is { ValueKind: JsonValueKind.Number } number ? number.GetDouble() : null;

    private static double? OpenedAt(List<JsonElement> rows, string phase) =>
        NumberOrNull(rows.Single(row => Kind(row) == "interval_opened" && TripleOf(row).Phase == phase), "committed_start_s");

    private static bool BegunBeforeWindow(List<JsonElement> rows, string phase) =>
        rows.Single(row => Kind(row) == "interval_opened" && TripleOf(row).Phase == phase)
            .GetProperty("begun_before_window").GetBoolean();

    private static (string Instance, string? Participant, string Phase) TripleOf(JsonElement row) =>
        (row.GetProperty("instance_id").GetString()!, row.GetProperty("participant").GetString(),
         row.GetProperty("phase").GetString()!);

    private static (string, string?, string)[] Triples(IEnumerable<SupervisionIntervalRecord> intervals) =>
        [.. intervals.Select(interval => (interval.InstanceId, interval.EntityId, interval.Phase)).Order()];

    private static (string, string?, string)[] Triples(JsonElement listed) =>
        [.. listed.EnumerateArray().Select(row => ((string, string?, string))TripleOf(row)).Order()];

    private static (string, string?, string)[] PlanTriples(SupervisionPlan plan) =>
        [.. plan.Instances.SelectMany(instance => instance.Intervals.Select(interval =>
            (instance.InstanceId, interval.EntityId, interval.Phase))).Order()];

    /// <summary>
    /// The triples a closed manifest names for its intervals: those its rows opened or closed, and those its
    /// terminal row lists as still open or never opened.
    /// </summary>
    private static (string, string?, string)[] RowTriples(List<JsonElement> rows)
    {
        JsonElement closing = rows[^1];
        Assert.Equal(RunManifestWriter.ClosedRow, Kind(closing));
        return [.. rows.Where(row => Kind(row) is "interval_opened" or "interval_closed")
                       .Concat(closing.GetProperty("open_intervals").EnumerateArray())
                       .Concat(closing.GetProperty("never_opened").EnumerateArray())
                       .Select(row => ((string, string?, string))TripleOf(row))
                       .Distinct()
                       .Order()];
    }

    /// <summary>What a reader infers of a manifest with no terminal row: every triple opened and not closed.</summary>
    private static (string, string?, string)[] OpenAtInterruption(List<JsonElement> rows)
    {
        HashSet<(string, string?, string)> closed = [.. rows.Where(row => Kind(row) == "interval_closed")
                                                            .Select(row => ((string, string?, string))TripleOf(row))];
        return [.. rows.Where(row => Kind(row) == "interval_opened")
                       .Select(row => ((string, string?, string))TripleOf(row))
                       .Where(triple => !closed.Contains(triple))
                       .Order()];
    }

    private static string ReadWhileOpen(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Every line that ends in a line break, each one JSON object; a last line without one is left off.</summary>
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
}
