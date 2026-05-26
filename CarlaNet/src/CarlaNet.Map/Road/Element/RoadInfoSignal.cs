// Source: carla/road/element/RoadInfoSignal.h
namespace CarlaNet.Map.Road.Element;

/// <summary>
/// A reference to a <see cref="Signal"/> placed on a road. Upstream uses two
/// constructors (with and without the resolved Signal*), so this class accepts
/// a nullable <see cref="Signal"/> that Wave 2's MapBuilder later patches in.
/// </summary>
public sealed class RoadInfoSignal : RoadInfo
{
    public SignId SignalId { get; }
    public Signal? Signal { get; internal set; }
    public RoadId RoadId { get; }

    /// <summary>Same as Distance (kept for fidelity with upstream's GetS()).</summary>
    public double S { get; }
    public double T { get; }
    public string OrientationRaw { get; }

    /// <summary>Set of lane-id ranges in which the signal is valid (populated by MapBuilder).</summary>
    public List<LaneValidity> Validities { get; } = new();

    public RoadInfoSignal(
        SignId signalId,
        Signal signal,
        RoadId roadId,
        double s,
        double t,
        string orientation)
        : base(s)
    {
        SignalId = signalId;
        Signal = signal;
        RoadId = roadId;
        S = s;
        T = t;
        OrientationRaw = orientation;
    }

    public RoadInfoSignal(
        SignId signalId,
        RoadId roadId,
        double s,
        double t,
        string orientation)
        : base(s)
    {
        SignalId = signalId;
        Signal = null;
        RoadId = roadId;
        S = s;
        T = t;
        OrientationRaw = orientation;
    }

    public bool IsDynamic => Signal?.IsDynamic ?? false;

    public SignalOrientation Orientation => OrientationRaw switch
    {
        "+" => SignalOrientation.Positive,
        "-" => SignalOrientation.Negative,
        _   => SignalOrientation.Both,
    };
}
