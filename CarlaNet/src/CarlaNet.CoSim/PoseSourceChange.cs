using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// One change to the pose source a session holds on the server: the SUMO step it poses its bodies from,
/// where it declares it, and the lent bodies whose pose stops or starts following that step.
/// </summary>
/// <param name="DeclareStep">
/// Whether the change declares the step: <paramref name="TicksPerStep"/> world ticks per SUMO step, a step
/// falling on the frame the next tick cue produces. With <paramref name="TicksPerStep"/> zero it withdraws
/// the step and every body's name, and names no body.
/// </param>
/// <param name="TicksPerStep">World ticks per SUMO step; read only where the step is declared.</param>
/// <param name="Sumo">
/// Bodies standing where SUMO put them at one of its steps on every frame, whichever frame it is. The
/// session names none so; a server built before the jump state is given its jumping bodies here
/// (<see cref="WithoutJump"/>).
/// </param>
/// <param name="Stale">Bodies standing where they were last drawn, because the session could not place them.</param>
/// <param name="Cleared">Bodies that follow the step again.</param>
/// <param name="Jump">
/// Bodies shown at SUMO's later position for every frame of the step being drawn, because SUMO reported a
/// step too far from the last to drive in one step.
/// </param>
public sealed record PoseSourceChange(
    bool DeclareStep,
    uint TicksPerStep,
    IReadOnlyList<ActorId> Sumo,
    IReadOnlyList<ActorId> Stale,
    IReadOnlyList<ActorId> Cleared,
    IReadOnlyList<ActorId> Jump)
{
    /// <summary>The change that withdraws the step and every body's name, so the world carries none again.</summary>
    public static PoseSourceChange Withdrawal { get; } = new(true, 0, [], [], [], []);

    /// <summary>Whether the change withdraws everything.</summary>
    public bool IsWithdrawal => DeclareStep && TicksPerStep == 0;

    /// <summary>
    /// Whether the change goes to a server built before the jump state, in the five arguments it binds:
    /// it has no name for a jumping body but sumo, so every body in <see cref="Jump"/> is named sumo with
    /// those in <see cref="Sumo"/>.
    /// </summary>
    public bool WithoutJump { get; init; }
}

/// <summary>
/// What a world made of a change to the pose source put to it.
/// </summary>
/// <param name="BodiesApplied">
/// How many of the bodies named it found, and lent where they were named sumo, stale or jump; zero where
/// it refused the change.
/// </param>
/// <param name="Refusal">
/// Why it refused the change, in the server's words, or <see langword="null"/> where it took it. A server
/// built before it carried a pose source on its snapshots refuses every change.
/// </param>
/// <param name="KnowsNoJump">
/// Whether the refusal says the server binds the call with another number of arguments: a server built
/// before the jump state, which takes the change only <see cref="PoseSourceChange.WithoutJump"/>.
/// </param>
public readonly record struct PoseSourceWrite(int BodiesApplied, string? Refusal, bool KnowsNoJump = false)
{
    /// <summary>Whether the world took the change.</summary>
    public bool Taken => Refusal is null;
}
