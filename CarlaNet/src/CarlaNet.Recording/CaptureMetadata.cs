using System.Globalization;
using System.Text;

namespace CarlaNet.Recording;

/// <summary>
/// Identifies a single capture: the simulation tick that produced it, and the run it belongs to.
///
/// Wall-clock time cannot serve this purpose. The simulation clock does not advance in step with real
/// time — a world ticking at a fixed step falls behind under load — so two runs of the same scenario
/// carry unrelated wall-clock timestamps and cannot be compared without first being re-aligned. The
/// tick count is what identifies the same instant across runs, and it is what pairs a still with the
/// truth recorded beside it.
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
public sealed record CaptureIdentity(
    ulong Tick,
    double SimTimeSeconds,
    string? RunId = null,
    string? ScenarioId = null,
    long? Seed = null)
{
    /// PNG tEXt chunk carrying the capture identity, so a still is self-describing even once separated
    /// from its sidecar.
    public IEnumerable<(string Keyword, string Text)> PngTextChunks()
    {
        yield return ("carla:capture", ToJson());
    }

    /// Compact JSON of the capture identity (ASCII, safe as a PNG tEXt value).
    public string ToJson()
    {
        var sb = new StringBuilder("{");
        sb.Append("\"tick\":").Append(Tick.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"sim_time_s\":").Append(F(SimTimeSeconds));
        Append(sb, "run_id", RunId);
        Append(sb, "scenario_id", ScenarioId);
        if (Seed.HasValue)
            sb.Append(",\"seed\":").Append(Seed.Value.ToString(CultureInfo.InvariantCulture));
        return sb.Append('}').ToString();
    }

    private static void Append(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append(",\"").Append(key).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
