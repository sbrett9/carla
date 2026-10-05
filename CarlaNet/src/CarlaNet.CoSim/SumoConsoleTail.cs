namespace CarlaNet.CoSim;

/// <summary>
/// The last few lines SUMO wrote to its console, kept so that a refusal raised because SUMO failed
/// can say what SUMO said, and every warning SUMO wrote, counted with the first few kept.
/// </summary>
/// <remarks>
/// <para>A TraCI failure on the client's side is a closed socket or a refused command; why SUMO closed
/// it -- a route it could not load, an edge that does not exist -- is on SUMO's own console, written a
/// moment earlier from another thread. Every line is also passed on to whatever the caller asked for.</para>
///
/// <para><b>Warnings are SUMO's account of what it did to the traffic.</b> A reroute that found no path,
/// a teleport, a collision, an emergency stop -- SUMO says each on its console and nowhere a client can
/// ask for, so the session keeps the count and SUMO's own words for the run report rather than reading
/// meaning into them. A line is a warning where SUMO began it with <c>Warning:</c>, which is how its
/// message handler writes every one. Lines arrive on SUMO's output thread, a moment after the step that
/// produced them, so a count read mid-run can lag by a step.</para>
///
/// <para><b>A collision warning is kept apart, and every one is kept.</b> Under <c>warn</c> SUMO writes
/// one for each collision it registers for the first time, naming both vehicles
/// (<c>MSLane::handleCollisionBetween</c>: "Vehicle '...'; collision with vehicle '...'", or a frontal,
/// side or junction collision, or a collision with a person). They are one per collision the session
/// records, so they are kept whole for the report to print or only count, and they do not take the
/// places of the other warnings among the first few kept.</para>
/// </remarks>
internal sealed class SumoConsoleTail
{
    private const int Lines = 8;
    private const int WarningSampleLimit = 10;
    private const string WarningPrefix = "Warning:";
    private static readonly string[] CollisionMarks = ["collision with vehicle '", "collision with person '"];

    private readonly Queue<string> _lines = new();
    private readonly List<string> _warnings = [];
    private readonly List<string> _collisionWarnings = [];
    private readonly Action<string>? _forward;
    private long _warningCount;

    public SumoConsoleTail(Action<string>? forward)
    {
        _forward = forward;
    }

    /// <summary>How many warnings SUMO has written, its collision warnings included.</summary>
    public long WarningCount => Interlocked.Read(ref _warningCount);

    /// <summary>The first few warnings SUMO wrote other than its collision warnings, as it wrote them.</summary>
    public IReadOnlyList<string> WarningSamples
    {
        get
        {
            lock (_lines)
            {
                return [.. _warnings];
            }
        }
    }

    /// <summary>Every collision warning SUMO wrote, as it wrote it, in order.</summary>
    public IReadOnlyList<string> CollisionWarnings
    {
        get
        {
            lock (_lines)
            {
                return [.. _collisionWarnings];
            }
        }
    }

    /// <summary>Whether a line of SUMO's is one of its collision warnings.</summary>
    internal static bool IsCollisionWarning(string line) =>
        line.StartsWith(WarningPrefix, StringComparison.Ordinal)
        && CollisionMarks.Any(mark => line.Contains(mark, StringComparison.Ordinal));

    /// <summary>Take one line of SUMO's output.</summary>
    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Lines)
            {
                _lines.Dequeue();
            }

            if (line.StartsWith(WarningPrefix, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _warningCount);
                if (IsCollisionWarning(line))
                {
                    _collisionWarnings.Add(line);
                }
                else if (_warnings.Count < WarningSampleLimit)
                {
                    _warnings.Add(line);
                }
            }
        }

        _forward?.Invoke(line);
    }

    /// <summary>What SUMO last said, as one clause, or a statement that it said nothing.</summary>
    public string Describe()
    {
        string[] said;
        lock (_lines)
        {
            said = [.. _lines];
        }

        return said.Length == 0
            ? "SUMO wrote nothing to its console"
            : "SUMO's console ended: " + string.Join(" | ", said);
    }
}
