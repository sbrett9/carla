namespace CarlaNet.CoSim;

/// <summary>
/// The run report's samples of discontinuous steps: one per vehicle, with the ticks it recurred on.
/// </summary>
/// <remarks>
/// A vehicle filed as discontinuous tends to be filed so on every tick until whatever caused it
/// ends. Sampled tick by tick, a few vehicles filled every slot: measured on the Bahonar scenario,
/// all twenty samples were three guards parked at their towers, repeated.
/// </remarks>
internal sealed class DiscontinuitySampler
{
    private readonly int _limit;
    private readonly List<string> _samples = [];
    private readonly List<string> _vehicles = [];
    private readonly Dictionary<string, int> _ticks = [];

    public DiscontinuitySampler(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _limit = limit;
    }

    /// <summary>The first discontinuous step of each sampled vehicle, in the order they occurred.</summary>
    public IReadOnlyList<string> Samples => _samples;

    /// <summary>Record a discontinuous step, or count it against the vehicle's sample.</summary>
    public void Sample(in CoSimVehicleFrame from, in CoSimVehicleFrame to, double? routeDistanceMetres)
    {
        if (_ticks.TryGetValue(from.Id, out int seen))
        {
            _ticks[from.Id] = seen + 1;
            return;
        }

        if (_samples.Count >= _limit)
        {
            return;
        }

        string distance = routeDistanceMetres is { } metres
            ? $"{metres:0.00} m along the route"
            : "no route between them";
        _ticks[from.Id] = 1;
        _vehicles.Add(from.Id);
        _samples.Add(
            $"{from.Id}: {from.LaneId}@{from.LanePositionMetres:0.00} -> "
            + $"{to.LaneId}@{to.LanePositionMetres:0.00}, {distance}, "
            + $"speed {from.SpeedMetresPerSecond:0.0} to {to.SpeedMetresPerSecond:0.0} m/s");
    }

    /// <summary>Each sample as the report prints it, with the ticks it recurred on.</summary>
    public IEnumerable<string> Lines()
    {
        for (int index = 0; index < _samples.Count; index++)
        {
            int ticks = _ticks[_vehicles[index]];
            yield return ticks > 1 ? $"{_samples[index]}, on {ticks} ticks" : _samples[index];
        }
    }
}
