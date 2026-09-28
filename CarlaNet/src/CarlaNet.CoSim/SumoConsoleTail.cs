namespace CarlaNet.CoSim;

/// <summary>
/// The last few lines SUMO wrote to its console, kept so that a refusal raised because SUMO failed
/// can say what SUMO said.
/// </summary>
/// <remarks>
/// A TraCI failure on the client's side is a closed socket or a refused command; why SUMO closed it
/// -- a route it could not load, an edge that does not exist -- is on SUMO's own console, written a
/// moment earlier from another thread. Every line is also passed on to whatever the caller asked for.
/// </remarks>
internal sealed class SumoConsoleTail
{
    private const int Lines = 8;

    private readonly Queue<string> _lines = new();
    private readonly Action<string>? _forward;

    public SumoConsoleTail(Action<string>? forward)
    {
        _forward = forward;
    }

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
