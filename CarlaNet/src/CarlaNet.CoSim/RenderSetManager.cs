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
///
/// <para><b>The release lag is kept here, for every policy.</b> A rendered vehicle that stops passing
/// the policy's predicate is held, still rendered, until it has failed it for the policy's
/// <see cref="IRenderSetPolicy.ReleaseLagSeconds"/> -- counted from the first pass it failed, and
/// started again if it passes in between -- and released then. A held vehicle is still eligible and
/// still counted as admitted, but ranks after every vehicle the predicate passes, the longest-held
/// first to go. Under a lag of zero, the circle's, a vehicle is released at the first pass it fails,
/// as it always was.</para>
/// </remarks>
public sealed class RenderSetManager
{
    /// <summary>
    /// How much short of the lag a vehicle's time outside may fall and still count as the whole lag:
    /// passes are a sum of SUMO steps, so they reach a whole number of seconds to within rounding.
    /// </summary>
    private const double LagTolerance = 1e-9;

    private readonly IRenderSetPolicy _policy;
    private readonly Action<RenderedVehicleInterval>? _onRelease;
    private readonly Dictionary<string, double> _admittedAt = [];
    private readonly Dictionary<string, double> _failingSince = [];
    private readonly List<string> _leaving = [];
    private readonly List<(string Id, double Rank)> _candidates = [];
    private readonly List<(string Id, double Since)> _held = [];

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

    /// <summary>
    /// How many admissions have been made since the session started. A vehicle released and admitted
    /// again counts each time.
    /// </summary>
    public long Admissions { get; private set; }

    /// <summary>How many admissions the capacity refused, counted per step per vehicle.</summary>
    public long CapacityDeclines { get; private set; }

    /// <summary>
    /// At the last pass: the vehicles the predicate admitted a place to, and the rendered vehicles the
    /// release lag held.
    /// </summary>
    public int LastEligible { get; private set; }

    /// <summary>At the last pass: the rendered vehicles held by the release lag after failing the predicate.</summary>
    public int LastHeld { get; private set; }

    /// <summary>
    /// Tell the policy what the session holds for the pass about to be made: the instant, the step, the
    /// seed, the bodies' reach and the cameras.
    /// </summary>
    public void BeginPass(RenderSetPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        _policy.BeginPass(pass);
    }

    /// <summary>At the last pass: the vehicles that took up a place.</summary>
    public int LastNewlyAdmitted { get; private set; }

    /// <summary>At the last pass: the vehicles that gave one up, for any reason.</summary>
    public int LastReleased { get; private set; }

    /// <summary>At the last pass: the eligible the capacity declined.</summary>
    public int LastShed { get; private set; }

    /// <summary>
    /// Bring the subscription tier into line with the policy, from the positions every vehicle in
    /// the simulation delivers.
    /// </summary>
    /// <remarks>
    /// <para>Exhaustion of the render set is a policy event and is counted, never an error and never a
    /// reason to stop subscribing: a vehicle the capacity declined is still simulated, still has
    /// truth to record, and may be admitted on the next step when a nearer one leaves.</para>
    ///
    /// <para><b>A rendered vehicle is never demoted.</b> It is released first, by the render set, and
    /// demoted at a later pass. Demoted while rendered, it would drop out of the frames and be released
    /// as though it had left the simulation, wherever it was -- which a vehicle held by a release lag,
    /// or one that jumped across the subscription margin in a step, would otherwise be.</para>
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
            if (_policy.ShouldSubscribe(x, y, promoted) || _admittedAt.ContainsKey(vehicleId))
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
    /// <param name="vanished">
    /// The vehicles that stopped reporting a state this step without SUMO listing them among its
    /// arrivals (<see cref="SubscribedPopulation.LastVanished"/>), or null where there were none.
    /// </param>
    public void ReconcileRenderSet(double simulatedTimeSeconds,
                                   IReadOnlyDictionary<string, CoSimVehicleFrame> frames,
                                   IReadOnlyCollection<string>? vanished = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        LastNewlyAdmitted = 0;
        LastReleased = 0;
        LastShed = 0;
        LastHeld = 0;

        // A vehicle that was rendered and is no longer in the frames is one SUMO removed: a rendered
        // vehicle is never demoted out of the subscription margin. It is out of the render set, and how
        // it went is what the reason records: SUMO listed it as arrived, or it vanished without being
        // listed.
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
            Release(vehicleId, simulatedTimeSeconds,
                    vanished is not null && vanished.Contains(vehicleId)
                        ? RenderSetReleaseReason.Vanished
                        : RenderSetReleaseReason.LeftTheSimulation);
        }

        _candidates.Clear();
        _held.Clear();
        _leaving.Clear();
        double lag = _policy.ReleaseLagSeconds;
        foreach ((string vehicleId, CoSimVehicleFrame frame) in frames)
        {
            bool rendered = _admittedAt.ContainsKey(vehicleId);
            if (_policy.ShouldRender(frame, rendered))
            {
                _candidates.Add((vehicleId, _policy.Rank(frame, rendered)));
                _failingSince.Remove(vehicleId);
            }
            else if (rendered)
            {
                if (!_failingSince.TryGetValue(vehicleId, out double since))
                {
                    since = simulatedTimeSeconds;
                    _failingSince[vehicleId] = since;
                }

                if (simulatedTimeSeconds - since < lag - LagTolerance)
                {
                    _held.Add((vehicleId, since));
                }
                else
                {
                    _leaving.Add(vehicleId);
                }
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

        // Held vehicles rank after every vehicle the predicate passes, the most recently in view first,
        // so the capacity sheds the one out of view longest before any other.
        _held.Sort(static (left, right) =>
        {
            int bySince = right.Since.CompareTo(left.Since);
            return bySince != 0 ? bySince : string.CompareOrdinal(left.Id, right.Id);
        });
        foreach ((string vehicleId, _) in _held)
        {
            _candidates.Add((vehicleId, double.PositiveInfinity));
        }

        int capacity = _policy.Capacity;
        LastEligible = _candidates.Count;
        LastHeld = _held.Count;
        for (int index = 0; index < _candidates.Count; index++)
        {
            string vehicleId = _candidates[index].Id;
            if (index < capacity)
            {
                if (_admittedAt.TryAdd(vehicleId, simulatedTimeSeconds))
                {
                    Admissions++;
                    LastNewlyAdmitted++;
                }
            }
            else
            {
                CapacityDeclines++;
                LastShed++;
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
        _failingSince.Remove(vehicleId);
        if (!_admittedAt.Remove(vehicleId, out double admittedAt))
        {
            return;
        }

        LastReleased++;

        // The actor is filled in by whoever holds the pool: this manager decides who is rendered and
        // knows nothing about which body renders them.
        _onRelease?.Invoke(new RenderedVehicleInterval(vehicleId, 0, admittedAt,
                                                       simulatedTimeSeconds, reason));
    }
}
