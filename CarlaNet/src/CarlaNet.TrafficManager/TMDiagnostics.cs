namespace CarlaNet.TrafficManager;

/// <summary>
/// Compile-time-cheap diagnostic helpers for the TrafficManager worker.
///
/// Enable by setting <c>CARLANET_TM_DEBUG=1</c> (or any truthy value:
/// <c>true</c>, <c>yes</c>, <c>on</c>) before the process starts. When
/// disabled, every call collapses to a single bool check.
///
/// Once enabled, the worker emits:
/// <list type="bullet">
///   <item>A one-shot banner when <c>TrafficManagerLocal.Start</c> completes.</item>
///   <item>Per-second summary lines with vehicle count + control-frame size +
///         the first vehicle's <c>VehicleControl</c> (throttle / steer / brake).</item>
///   <item>The first occurrence of each (stage, exception-type) pair with a
///         full stack trace, then suppressed thereafter.</item>
/// </list>
///
/// Output goes to <c>Console.Error</c> so it interleaves with pythonnet's
/// stderr capture for the embedded Python host.
/// </summary>
internal static class TMDiagnostics
{
    public static readonly bool Enabled = ResolveFlag();

    private static readonly object _gate = new();
    private static readonly System.Collections.Generic.HashSet<string> _seen = new();

    private static bool ResolveFlag()
    {
        var v = System.Environment.GetEnvironmentVariable("CARLANET_TM_DEBUG");
        if (string.IsNullOrEmpty(v)) return false;
        return v.Equals("1", System.StringComparison.Ordinal)
            || v.Equals("true", System.StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes",  System.StringComparison.OrdinalIgnoreCase)
            || v.Equals("on",   System.StringComparison.OrdinalIgnoreCase);
    }

    public static void Log(string message)
    {
        if (!Enabled) return;
        System.Console.Error.WriteLine(message);
    }

    /// First occurrence of each (stage, exception type) is printed with a
    /// stack trace; subsequent identical failures are suppressed so a
    /// per-tick exception doesn't drown the log.
    public static void LogFirstFailure(string stage, System.Exception ex, long tick)
    {
        if (!Enabled) return;
        var key = stage + ":" + ex.GetType().Name;
        lock (_gate)
        {
            if (!_seen.Add(key)) return;
        }
        System.Console.Error.WriteLine($"[TM tick {tick}] {stage} FIRST failure: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace is { } st) System.Console.Error.WriteLine(st);
    }
}
