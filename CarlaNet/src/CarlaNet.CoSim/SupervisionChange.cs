using CarlaNet.Types.Supervision;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// What the author asserts, from a change on, of the vehicle one lent body draws, as the server is told
/// of it.
/// </summary>
/// <param name="Actor">The body.</param>
/// <param name="Supervision">
/// The vehicle's supervision, replacing whatever the body carried; unlabelled clears it.
/// </param>
public readonly record struct BodySupervision(ActorId Actor, SupervisionInForce Supervision);

/// <summary>
/// One change to the supervision a session holds on the server: the plan it is bound from and the bodies
/// whose supervision changes. Every row is a vehicle's; nothing is held for the world apart from the plan
/// (06 §3.5).
/// </summary>
/// <param name="Fresh">
/// Whether the server drops every row it holds before applying the change: a session's first change, and
/// the first after it binds another plan.
/// </param>
/// <param name="Plan">The plan everything is bound from; <see langword="null"/> withdraws all supervision.</param>
/// <param name="Bodies">The bodies whose supervision changes.</param>
public sealed record SupervisionChange(
    bool Fresh,
    SupervisionPlanIdentity? Plan,
    IReadOnlyList<BodySupervision> Bodies)
{
    /// <summary>The change that withdraws all supervision, so the world carries none again.</summary>
    public static SupervisionChange Withdrawal { get; } = new(false, null, []);
}

/// <summary>
/// What a world made of a change to the supervision put to it.
/// </summary>
/// <param name="BodiesApplied">
/// How many of the bodies named it found lent and gave their supervision; zero where it refused the change.
/// </param>
/// <param name="Refusal">
/// Why it refused the change, in the server's words, or <see langword="null"/> where it took it. A server
/// built before it carried supervision on its snapshots refuses every change.
/// </param>
public readonly record struct SupervisionWrite(int BodiesApplied, string? Refusal)
{
    /// <summary>Whether the world took the change.</summary>
    public bool Taken => Refusal is null;
}
