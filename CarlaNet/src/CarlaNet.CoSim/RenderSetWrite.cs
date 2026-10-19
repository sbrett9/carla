namespace CarlaNet.CoSim;

/// <summary>
/// What a world made of a change to the render set named to it.
/// </summary>
/// <param name="BodiesFound">How many of the bodies named it found; zero where it refused the change.</param>
/// <param name="Refusal">
/// Why it refused the change, in the server's words, or <see langword="null"/> where it took it. A
/// server built before it carried a render set on its snapshots refuses every change.
/// </param>
public readonly record struct RenderSetWrite(int BodiesFound, string? Refusal)
{
    /// <summary>Whether the world took the change.</summary>
    public bool Taken => Refusal is null;
}
