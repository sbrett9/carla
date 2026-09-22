namespace CarlaNet.CoSim;

/// <summary>
/// Decides which subscribed vehicles hold a rendered actor, and records when each one took it up
/// and gave it back.
/// </summary>
/// <remarks>
/// <para>Two decisions, taken in order, because the second depends on the first. The subscription
/// tier is decided from position alone, which every vehicle in the simulation delivers; the render
/// set is decided from the full state, which only a promoted vehicle delivers. Reversing them would
/// ask the render predicate about vehicles whose state has not arrived.</para>
///
/// <para>Nothing here spawns, destroys or moves an actor. The manager says which vehicles a pool
/// would be checked out for; what is done about it is the driving stage's.</para>
/// </remarks>
public sealed class RenderSetManager
{
    private readonly IRenderSetPolicy _policy;
    private readonly Action<RenderedVehicleInterval>? _onRelease;
    private readonly Dictionary<string, double> _admittedAt = [];
    private readonly List<string> _leaving = [];
    private readonly List<(string Id, double Rank)> _candidates = [];

    /// <param name="policy">The predicate and the capacity.</param>
    /// <param name="onRelease">
    /// Where a completed interval goes. Handed out rather than accumulated: a seven-day scenario
    /// inserts sixty-eight thousand vehicles, and a list of every one of them is a run-length leak
    /// in a component that runs on the tick thread.
    /// </param>
    public RenderSetManager(IRenderSetPolicy policy, Action<RenderedVehicleInterval>? onRelease = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _onRelease = onRelease;
    }

    /// <summary>The vehicles holding a rendered actor right now.</summary>
    public IReadOnlyCollection<string> RenderedVehicleIds => _admittedAt.Keys;

    /// <summary>How many vehicles have been admitted since the session started.</summary>
    public long Admissions { get; private set; }

    /// <summary>How many admissions the capacity refused, counted per step per vehicle.</summary>
    public long CapacityDeclines { get; private set; }

    /// <summary>
    /// Bring the subscription tier into line with the policy, from the positions every vehicle in
    /// the simulation delivers.
    /// </summary>
    /// <remarks>
    /// Exhaustion of the render set is a policy event and is counted, never an error and never a
    /// reason to stop subscribing: a vehicle the capacity declined is still simulated, still has
    /// truth to record, and may be admitted on the next step when a nearer one leaves.
    /// </remarks>
    public void ReconcileSubscriptions(SubscribedPopulation population,
                                       IReadOnlyDictionary<string, (double X, double Y)> positions)
    {
        ArgumentNullException.ThrowIfNull(population);
        ArgumentNullException.ThrowIfNull(positions);

        _leaving.Clear();
        foreach ((string vehicleId, (double x, double y)) in positions)
        {
            bool promoted = population.PromotedVehicleIds.Contains(vehicleId);
            if (_policy.ShouldSubscribe(x, y, promoted))
            {
                if (!promoted)
                {
                    population.Promote(vehicleId);
                }
            }
            else if (promoted)
            {
                _leaving.Add(vehicleId);
            }
        }

        foreach (string vehicleId in _leaving)
        {
            population.Demote(vehicleId);
        }
    }

    /// <summary>
    /// Bring the render set into line with the policy, from the state the promoted vehicles
    /// delivered.
    /// </summary>
    /// <param name="simulatedTimeSeconds">The simulated instant the decision is recorded at.</param>
    /// <param name="frames">Every promoted vehicle's state for this step.</param>
    public void ReconcileRenderSet(double simulatedTimeSeconds,
                                   IReadOnlyDictionary<string, CoSimVehicleFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        // A vehicle that was rendered and is no longer in the frames is one SUMO removed, or one
        // demoted out of the subscription margin. Either way it is out of the render set, and which
        // it was is what the reason records.
        _leaving.Clear();
        foreach (string vehicleId in _admittedAt.Keys)
        {
            if (!frames.ContainsKey(vehicleId))
            {
                _leaving.Add(vehicleId);
            }
        }

        foreach (string vehicleId in _leaving)
        {
            Release(vehicleId, simulatedTimeSeconds, RenderSetReleaseReason.LeftTheSimulation);
        }

        _candidates.Clear();
        _leaving.Clear();
        foreach ((string vehicleId, CoSimVehicleFrame frame) in frames)
        {
            bool rendered = _admittedAt.ContainsKey(vehicleId);
            if (_policy.ShouldRender(frame, rendered))
            {
                _candidates.Add((vehicleId, _policy.Rank(frame)));
            }
            else if (rendered)
            {
                _leaving.Add(vehicleId);
            }
        }

        foreach (string vehicleId in _leaving)
        {
            Release(vehicleId, simulatedTimeSeconds, RenderSetReleaseReason.LeftTheRegion);
        }

        // Rank ties are broken on the vehicle id so that two runs of one seed admit the same set in
        // the same order. A dictionary's enumeration order is not a tie-break anybody can reproduce.
        _candidates.Sort(static (left, right) =>
        {
            int byRank = left.Rank.CompareTo(right.Rank);
            return byRank != 0 ? byRank : string.CompareOrdinal(left.Id, right.Id);
        });

        int capacity = _policy.Capacity;
        for (int index = 0; index < _candidates.Count; index++)
        {
            string vehicleId = _candidates[index].Id;
            if (index < capacity)
            {
                if (_admittedAt.TryAdd(vehicleId, simulatedTimeSeconds))
                {
                    Admissions++;
                }
            }
            else
            {
                CapacityDeclines++;
                if (_admittedAt.ContainsKey(vehicleId))
                {
                    Release(vehicleId, simulatedTimeSeconds, RenderSetReleaseReason.Capacity);
                }
            }
        }
    }

    /// <summary>The simulated second a rendered vehicle was admitted at.</summary>
    public bool TryGetAdmissionInstant(string vehicleId, out double simulatedTimeSeconds) =>
        _admittedAt.TryGetValue(vehicleId, out simulatedTimeSeconds);

    /// <summary>Close every open interval, which is what the end of a session does to them.</summary>
    public void CloseAll(double simulatedTimeSeconds)
    {
        _leaving.Clear();
        _leaving.AddRange(_admittedAt.Keys);
        foreach (string vehicleId in _leaving)
        {
            Release(vehicleId, simulatedTimeSeconds, RenderSetReleaseReason.SessionEnded);
        }
    }

    private void Release(string vehicleId, double simulatedTimeSeconds, RenderSetReleaseReason reason)
    {
        if (!_admittedAt.Remove(vehicleId, out double admittedAt))
        {
            return;
        }

        // The actor is filled in by whoever holds the pool: this manager decides who is rendered and
        // knows nothing about which body renders them.
        _onRelease?.Invoke(new RenderedVehicleInterval(vehicleId, 0, admittedAt,
                                                       simulatedTimeSeconds, reason));
    }
}
