using CarlaNet.Recording;
using CarlaNet.Sumo;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A supervision binder fed as a session feeds it, from a stream a test writes: SUMO frames with their
/// events and the vehicles SUMO has, and rendered frames with what they drew and the speed each body was
/// given. One world tick per SUMO step, so the session's order is S0 S1 R0 S2 R1 S3 R2 ...: a frame
/// renders one step behind the SUMO frame last read.
/// </summary>
internal sealed class BinderFeed
{
    private readonly HashSet<string> _alive = new(StringComparer.Ordinal);
    private bool _first = true;
    private ulong _frame;
    private long _tick;

    public BinderFeed(SupervisionPlan plan, double windowOpensAtSeconds = 0.0, double stepSeconds = 1.0)
    {
        Clock = CoSimClock.ForSession(stepSeconds, stepSeconds, 1.0 / stepSeconds, worldIsSynchronous: true);
        Binder = new SupervisionBinder(plan, Table, Clock, () => windowOpensAtSeconds, [Sink]);
    }

    public CoSimClock Clock { get; }

    public DriveSupervision Table { get; } = new();

    public SupervisionBinder Binder { get; }

    public RecordingSink Sink { get; } = new();

    public FakeAnswers Answers { get; } = new();

    /// <summary>The edge each vehicle reports, where a test cares; any other is on <c>approach</c>.</summary>
    public Dictionary<string, string> Edges { get; } = new(StringComparer.Ordinal);

    /// <summary>Vehicles SUMO has that the render set holds no place for.</summary>
    public HashSet<string> OutsideTheLimit { get; } = new(StringComparer.Ordinal);

    /// <summary>Vehicles SUMO already has at the first frame, inserted before anything watched.</summary>
    public BinderFeed Already(params string[] vehicles)
    {
        _alive.UnionWith(vehicles);
        return this;
    }

    /// <summary>One SUMO frame, as the session tells it.</summary>
    public void Step(double at, string[]? departed = null, string[]? arrived = null, string[]? stopsStarted = null,
                     string[]? stopsEnded = null, string[]? teleports = null, string[]? vanished = null,
                     VehicleNotInserted[]? notInserted = null)
    {
        _alive.UnionWith(departed ?? []);
        _alive.ExceptWith(arrived ?? []);
        _alive.ExceptWith(vanished ?? []);
        var frames = _alive.ToDictionary(
            id => id,
            id => new CoSimVehicleFrame(id, 0.0, 0.0, 0.0, 10.0, Edges.GetValueOrDefault(id, "approach"),
                                        Edges.GetValueOrDefault(id, "approach") + "_0", 10.0, "truck", default));
        var events = new SumoStepEvents(at, departed ?? [], arrived ?? [], stopsStarted ?? [], stopsEnded ?? [],
                                        [], [], teleports ?? [], []);
        var record = new SumoStepRecord(
            _tick, at, _first, events, [], notInserted ?? [], vanished ?? [], frames,
            [.. _alive.Where(id => !OutsideTheLimit.Contains(id))],
            new AdmissionPass(_tick, at, frames.Count, 0, 0, 0), null!);
        _first = false;
        Binder.Observe(record, Answers);
    }

    /// <summary>One rendered frame: the bodies it drew, by vehicle, and the speed each was given.</summary>
    public void Render(double at, string[]? drawn = null, (string Vehicle, double Speed)[]? speeds = null,
                       bool inWindow = true)
    {
        RenderSet? set = drawn is null
            ? null
            : new RenderSet(drawn.Select((id, index) => new RenderedVehicle((uint)index + 1, id, "truck", 0)));
        Binder.OnFrameRendered(new RenderedFrameRecord(++_frame, _tick++, at, true, inWindow, set, null)
        {
            AppliedPoses = speeds is null
                ? []
                : [.. speeds.Select(entry => new VehiclePose(entry.Vehicle, "vehicle.fuso.mitsubishi", 0, 0, 0, 0, 0, 0,
                                                             entry.Speed, 0, 0, false))],
        });
    }

    public void End(double lastRendered, bool scenarioFinished) =>
        Binder.OnSessionEnded(new SessionEndRecord(lastRendered + Clock.SumoStepSeconds, lastRendered,
                                                   (ulong)_frame, scenarioFinished, null));

    /// <summary>The record of the plan's interval of an instance and phase.</summary>
    public SupervisionIntervalRecord Interval(string instance, string phase) =>
        Binder.Intervals.Single(interval => interval.InstanceId == $"{PlanRows.ScenarioId}/{instance}"
                                            && interval.Phase == phase);

    /// <summary>What the table holds for a vehicle: its state, and its annotations as instance:phase, in order.</summary>
    public (SupervisionState State, string Annotations) Of(string vehicle)
    {
        SupervisionInForce held = Table.Of(vehicle);
        return (held.State, string.Join(", ", held.Annotations.Select(annotation => $"{annotation.InstanceId}:{annotation.Phase}")));
    }

    /// <summary>A stop SUMO reports, with SUMO's own stamps.</summary>
    public static SumoStop StopAt(string lane, double endPosition, double? arrival, double? departure) =>
        new(lane, 0.0, endPosition, string.Empty, SumoStopFlags.None, 10.0, null, null, arrival, departure,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0.0);
}

/// <summary>The binder's questions to SUMO, answered from what a test put in, every question counted.</summary>
internal sealed class FakeAnswers : SupervisionBinder.IVehicleAnswers
{
    public Dictionary<string, double> Departures { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<SumoStop>> Completed { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<SumoStop>> Upcoming { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> RouteIndices { get; } = new(StringComparer.Ordinal);

    public List<string> Asked { get; } = [];

    public double? Departure(string vehicleId)
    {
        Asked.Add($"departure {vehicleId}");
        return Departures.TryGetValue(vehicleId, out double stamp) ? stamp : null;
    }

    public IReadOnlyList<SumoStop>? CompletedStops(string vehicleId)
    {
        Asked.Add($"completed {vehicleId}");
        return Completed.TryGetValue(vehicleId, out List<SumoStop>? stops) ? [.. stops] : [];
    }

    public IReadOnlyList<SumoStop>? UpcomingStops(string vehicleId)
    {
        Asked.Add($"upcoming {vehicleId}");
        return Upcoming.TryGetValue(vehicleId, out List<SumoStop>? stops) ? [.. stops] : [];
    }

    public int? RouteIndex(string vehicleId)
    {
        Asked.Add($"route index {vehicleId}");
        return RouteIndices.TryGetValue(vehicleId, out int index) ? index : null;
    }
}

/// <summary>Every interval opened and closed, in the order the binder told them.</summary>
internal sealed class RecordingSink : ISupervisionIntervalSink
{
    public List<(string Told, SupervisionIntervalRecord Interval)> Told { get; } = [];

    public void OnIntervalOpened(SupervisionIntervalRecord interval) => Told.Add(("opened", interval));

    public void OnIntervalClosed(SupervisionIntervalRecord interval) => Told.Add(("closed", interval));
}
