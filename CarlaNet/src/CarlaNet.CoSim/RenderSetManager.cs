namespace CarlaNet.CoSim;

/// <summary>
/// Keeps the render set -- every vehicle SUMO has -- and records when each one entered it and left.
/// </summary>
/// <remarks>
/// <para><b>SUMO's scenario is the only arbiter of population.</b> Every vehicle that delivers a state
/// is in the render set from the step it is first seen until SUMO removes it or the session ends.
/// Nothing is ranked, shed or held back: a heavier scenario makes a synchronous run slower on the
/// wall clock, never thinner in content.</para>
///
/// <para>Nothing here spawns, destroys or moves an actor. The manager says which vehicles a pool
/// would be checked out for; what is done about it is the driving stage's.</para>
/// </remarks>
public sealed class RenderSetManager
{
    private readonly Action<RenderedVehicleInterval>? _onRelease;
    private readonly Dictionary<string, double> _admittedAt = [];
    private readonly List<string> _leaving = [];
    private readonly List<string> _arriving = [];

    /// <param name="onRelease">
    /// Where a completed interval goes. Handed out rather than accumulated: a seven-day scenario
    /// inserts sixty-eight thousand vehicles, and a list of every one of them is a run-length leak
    /// in a component that runs on the tick thread.
    /// </param>
    public RenderSetManager(Action<RenderedVehicleInterval>? onRelease = null)
    {
        _onRelease = onRelease;
    }

    /// <summary>The vehicles holding a rendered actor right now.</summary>
    public IReadOnlyCollection<string> RenderedVehicleIds => _admittedAt.Keys;

    /// <summary>
    /// How many admissions have been made since the session started: one per vehicle SUMO had while
    /// the session read it.
    /// </summary>
    public long Admissions { get; private set; }

    /// <summary>At the last pass: the vehicles that took up a place.</summary>
    public int LastNewlyAdmitted { get; private set; }

    /// <summary>At the last pass: the vehicles that gave one up, for any reason.</summary>
    public int LastReleased { get; private set; }

    /// <summary>
    /// Bring the render set into line with the vehicles SUMO has, from the state they delivered.
    /// </summary>
    /// <param name="simulatedTimeSeconds">The simulated instant the decision is recorded at.</param>
    /// <param name="frames">Every vehicle's state for this step.</param>
    /// <param name="vanished">
    /// The vehicles that stopped reporting a state this step without SUMO listing them among its
    /// arrivals (<see cref="SubscribedPopulation.LastVanished"/>), or null where there were none.
    /// </param>
    /// <remarks>
    /// Vehicles new to the set are admitted in ordinal order of their ids, so two runs of one scenario
    /// admit them -- and lend their bodies -- in the same order. The order the frames arrive in is a
    /// hash set's, which is no order anybody can reproduce.
    /// </remarks>
    public void ReconcileRenderSet(double simulatedTimeSeconds,
                                   IReadOnlyDictionary<string, CoSimVehicleFrame> frames,
                                   IReadOnlyCollection<string>? vanished = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        LastNewlyAdmitted = 0;
        LastReleased = 0;

        // A vehicle that was rendered and is no longer in the frames is one SUMO removed. How it went
        // is what the reason records: SUMO listed it as arrived, or it vanished without being listed.
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

        _arriving.Clear();
        foreach (string vehicleId in frames.Keys)
        {
            if (!_admittedAt.ContainsKey(vehicleId))
            {
                _arriving.Add(vehicleId);
            }
        }

        _arriving.Sort(StringComparer.Ordinal);
        foreach (string vehicleId in _arriving)
        {
            _admittedAt.Add(vehicleId, simulatedTimeSeconds);
            Admissions++;
            LastNewlyAdmitted++;
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

        LastReleased++;

        // The actor is filled in by whoever holds the pool: this manager decides who is rendered and
        // knows nothing about which body renders them.
        _onRelease?.Invoke(new RenderedVehicleInterval(vehicleId, 0, admittedAt,
                                                       simulatedTimeSeconds, reason));
    }
}
