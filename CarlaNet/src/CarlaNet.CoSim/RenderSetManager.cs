namespace CarlaNet.CoSim;

/// <summary>
/// Keeps the render set -- the vehicles SUMO has that hold a body in CARLA -- and records when each
/// one entered it and left.
/// </summary>
/// <remarks>
/// <para><b>By default every vehicle SUMO has is in it.</b> Under the default policy
/// (<see cref="EveryVehicleRenderSetPolicy"/> with no capacity) a vehicle is in the render set from the
/// step it is first seen until SUMO removes it or the session ends, and vehicles new to the set are
/// admitted in ordinal order of their ids, so two runs of one scenario admit them -- and lend their
/// bodies -- in the same order. SUMO's scenario is the arbiter of population: a heavier scenario makes a
/// synchronous run slower on the wall clock, never thinner in content.</para>
///
/// <para><b>An optional limit leaves vehicles out, and says so.</b> A circle, the cameras' footprints or
/// a capacity, chosen by the caller as a performance control, can leave a vehicle without a body: it
/// is still simulated by SUMO, in no frame and in no truth record. The manager counts what each pass
/// left out (<see cref="LastEligible"/>, <see cref="LastShed"/>) and records why each vehicle left
/// (<see cref="RenderSetReleaseReason"/>).</para>
///
/// <para><b>The release lag is kept here, for every policy.</b> A rendered vehicle that stops passing
/// the policy's predicate is held, still rendered, until it has failed it for the policy's
/// <see cref="IRenderSetPolicy.ReleaseLagSeconds"/> -- counted from the first pass it failed, and
/// started again if it passes in between -- and released then. A held vehicle is still counted as
/// eligible, but ranks after every vehicle the predicate passes, the longest-held first to go. Under
/// a lag of zero, the circle's, a vehicle is released at the first pass it fails.</para>
///
/// <para>Nothing here spawns, destroys or moves an actor. The manager says which vehicles a pool
/// would be checked out for; what is done about it, and from which frame, is the driving stage's,
/// which draws a vehicle admitted at a step from that step's frame on, and a vehicle released because
/// SUMO no longer has it on the frame of the last step that had it, completing its interval once that
/// frame has rendered.</para>
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

    /// <summary>A render set of every vehicle SUMO has, with no limit: the default.</summary>
    /// <param name="onRelease">Where a completed interval goes.</param>
    public RenderSetManager(Action<RenderedVehicleInterval>? onRelease = null)
        : this(new EveryVehicleRenderSetPolicy(), onRelease)
    {
    }

    /// <param name="policy">The predicate, the order and the capacity.</param>
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

    /// <summary>The policy deciding the render set.</summary>
    public IRenderSetPolicy Policy => _policy;

    /// <summary>The vehicles holding a rendered actor right now.</summary>
    public IReadOnlyCollection<string> RenderedVehicleIds => _admittedAt.Keys;

    /// <summary>
    /// How many admissions have been made since the session started. A vehicle released and admitted
    /// again counts each time; with no limit, that is one per vehicle SUMO had while the session read it.
    /// </summary>
    public long Admissions { get; private set; }

    /// <summary>How many admissions a capacity refused, counted per pass per vehicle.</summary>
    public long CapacityDeclines { get; private set; }

    /// <summary>
    /// Vehicles the policy's predicate did not admit, summed over the passes: each a vehicle SUMO had
    /// at that pass that held no body for the step it decides. Zero with no limit.
    /// </summary>
    public long VehiclePassesOutsideThePolicy { get; private set; }

    /// <summary>At the last pass: the vehicles SUMO had, each delivering its state.</summary>
    public int LastPopulation { get; private set; }

    /// <summary>
    /// At the last pass: the vehicles the predicate admitted a place to, and the rendered vehicles the
    /// release lag held. With no limit, every vehicle SUMO had.
    /// </summary>
    public int LastEligible { get; private set; }

    /// <summary>At the last pass: the rendered vehicles held by the release lag after failing the predicate.</summary>
    public int LastHeld { get; private set; }

    /// <summary>At the last pass: the vehicles that took up a place.</summary>
    public int LastNewlyAdmitted { get; private set; }

    /// <summary>At the last pass: the vehicles that gave one up, for any reason.</summary>
    public int LastReleased { get; private set; }

    /// <summary>At the last pass: the eligible the capacity declined.</summary>
    public int LastShed { get; private set; }

    /// <summary>
    /// Tell the policy what the session holds for the pass about to be made: the instant, the step, the
    /// seed, the bodies' reach and the cameras.
    /// </summary>
    public void BeginPass(RenderSetPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        _policy.BeginPass(pass);
    }

    /// <summary>
    /// Bring the render set into line with the policy, from the state every vehicle SUMO has delivered.
    /// </summary>
    /// <param name="simulatedTimeSeconds">The simulated instant the decision is recorded at.</param>
    /// <param name="frames">Every vehicle's state for this step.</param>
    /// <param name="vanished">
    /// The vehicles that stopped reporting a state this step without SUMO listing them among its
    /// arrivals (<see cref="SubscribedPopulation.LastVanished"/>), or null where there were none.
    /// </param>
    /// <remarks>
    /// The candidates are admitted in the policy's rank order, ties broken on the vehicle id, so two
    /// runs of one scenario admit them -- and lend their bodies -- in the same order. The order the
    /// frames arrive in is a hash set's, which is no order anybody can reproduce.
    /// </remarks>
    public void ReconcileRenderSet(double simulatedTimeSeconds,
                                   IReadOnlyDictionary<string, CoSimVehicleFrame> frames,
                                   IReadOnlyCollection<string>? vanished = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        LastNewlyAdmitted = 0;
        LastReleased = 0;
        LastShed = 0;
        LastHeld = 0;
        LastPopulation = frames.Count;

        // A vehicle that was rendered and is no longer in the frames is one SUMO removed. How it went
        // is what the reason records: SUMO listed it as arrived, or it vanished without being listed. Its
        // interval is handed out stamped with this pass; the driving stage, which still draws it on the
        // frame of its last step, ends it at the frame after that one before passing it on.
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

        // Released in ordinal order, as admitted: a released body goes back to its blueprint's free
        // bodies, and the order they go back in is the order the next vehicles borrow them in.
        _leaving.Sort(StringComparer.Ordinal);
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
        // so a capacity sheds the one out of view longest before any other.
        _held.Sort(static (left, right) =>
        {
            int bySince = right.Since.CompareTo(left.Since);
            return bySince != 0 ? bySince : string.CompareOrdinal(left.Id, right.Id);
        });
        foreach ((string vehicleId, _) in _held)
        {
            _candidates.Add((vehicleId, double.PositiveInfinity));
        }

        int capacity = _policy.Capacity ?? int.MaxValue;
        LastEligible = _candidates.Count;
        LastHeld = _held.Count;
        VehiclePassesOutsideThePolicy += Math.Max(0, frames.Count - _candidates.Count);
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
