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
/// <param name="Simulated">
/// Bodies standing where SUMO put them at one of its steps on every frame of the step being drawn: placed
/// at SUMO's later step across a discontinuity rather than interpolated.
/// </param>
/// <param name="Held">Bodies left where their last pose put them, because the session could not place them.</param>
/// <param name="Cleared">Bodies that follow the step again.</param>
public sealed record PoseSourceChange(
    bool DeclareStep,
    uint TicksPerStep,
    IReadOnlyList<ActorId> Simulated,
    IReadOnlyList<ActorId> Held,
    IReadOnlyList<ActorId> Cleared)
{
    /// <summary>The change that withdraws the step and every body's name, so the world carries none again.</summary>
    public static PoseSourceChange Withdrawal { get; } = new(true, 0, [], [], []);

    /// <summary>Whether the change withdraws everything.</summary>
    public bool IsWithdrawal => DeclareStep && TicksPerStep == 0;
}

/// <summary>
/// What a world made of a change to the pose source put to it.
/// </summary>
/// <param name="BodiesApplied">
/// How many of the bodies named it found, and lent where they were named simulated or held; zero where it
/// refused the change.
/// </param>
/// <param name="Refusal">
/// Why it refused the change, in the server's words, or <see langword="null"/> where it took it. A server
/// built before it carried a pose source on its snapshots refuses every change.
/// </param>
public readonly record struct PoseSourceWrite(int BodiesApplied, string? Refusal)
{
    /// <summary>Whether the world took the change.</summary>
    public bool Taken => Refusal is null;
}
