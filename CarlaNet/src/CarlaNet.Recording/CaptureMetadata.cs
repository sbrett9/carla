using System.Globalization;
using CarlaNet.Types.Provenance;

namespace CarlaNet.Recording;

/// <summary>
/// Identifies a single capture: the simulation tick that produced it, and the run it belongs to.
///
/// Wall-clock time cannot serve this purpose. The simulation clock does not advance in step with real
/// time — a world ticking at a fixed step falls behind under load — so two runs of the same scenario
/// carry unrelated wall-clock timestamps and cannot be compared without first being re-aligned. The
/// tick count is what identifies the same instant across runs, and it is what pairs a still with the
/// truth recorded beside it: the truth beside a still is always the truth of the still's own tick, and
/// a still whose tick's truth was not to be had is not written (<see cref="FrameRecorder.FrameUnpaired"/>).
///
/// <see cref="SimTimeSeconds"/> is recorded alongside the tick rather than derived from it, because the
/// simulation step is a per-run setting and need not be the same in a later run.
/// </summary>
/// <param name="Tick">Simulation frame number, taken from the sensor frame that produced the capture.</param>
/// <param name="SimTimeSeconds">Elapsed simulation time at that frame.</param>
/// <param name="RunId">Identifier grouping every artifact produced by one execution.</param>
/// <param name="ScenarioId">The scenario being executed, where one is driving the run.</param>
/// <param name="Seed">Seed the run was started with, for reproducing it. Numeric because it seeds
/// pseudo-random generators; typing it so removes any need to validate it downstream.</param>
/// <remarks>
/// The <c>carla:capture</c> chunk also carries its <c>format_version</c> and, where the recorder gave one,
/// what made the still (<see cref="Producer"/>, <c>producer</c>): the chunk that says which capture a
/// still is says what wrote it too, so a still separated from everything else can be traced to the
/// release of the distribution that made it, and the PNG keeps its four chunks
/// (<c>04_Contracts.md</c> rev 42).
/// </remarks>
public sealed record CaptureIdentity(
    ulong Tick,
    double SimTimeSeconds,
    string? RunId = null,
    string? ScenarioId = null,
    long? Seed = null)
{
    /// <summary>The format of the <c>carla:capture</c> chunk, written in it as <c>format_version</c>.</summary>
    public const int FormatVersion = 1;

    /// <summary>What made the still, written in its <c>carla:capture</c> chunk; null writes none.</summary>
    public ProducerRecord? Producer { get; init; }

    /// PNG tEXt chunk carrying the capture identity, so a still is self-describing even once separated
    /// from its sidecar.
    public IEnumerable<(string Keyword, string Text)> PngTextChunks()
    {
        yield return ("carla:capture", ToJson());
    }

    /// <summary>Compact JSON of the capture identity, every string escaped as JSON requires (<see cref="PngChunkJson"/>).</summary>
    public string ToJson() => PngChunkJson.Object(json =>
    {
        PngChunkJson.Number(json, "format_version", FormatVersion.ToString(CultureInfo.InvariantCulture));
        PngChunkJson.Number(json, "tick", Tick.ToString(CultureInfo.InvariantCulture));
        PngChunkJson.Number(json, "sim_time_s", F(SimTimeSeconds));
        if (!string.IsNullOrEmpty(RunId)) json.WriteString("run_id", RunId);
        if (!string.IsNullOrEmpty(ScenarioId)) json.WriteString("scenario_id", ScenarioId);
        if (Seed.HasValue) json.WriteNumber("seed", Seed.Value);
        if (Producer is not null)
        {
            json.WritePropertyName("producer");
            Producer.WriteJson(json);
        }
    });

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
