namespace CarlaNet.CoSim;

/// <summary>
/// What a world made of a draw distance set on some of its bodies.
/// </summary>
/// <param name="BodiesFound">How many of the bodies named it found; zero where it refused the call.</param>
/// <param name="Refusal">
/// Why it refused, in the server's words, or <see langword="null"/> where it took the distance. A
/// server built before it carried the call refuses every one.
/// </param>
public readonly record struct DrawDistanceWrite(int BodiesFound, string? Refusal)
{
    /// <summary>Whether the world took the distance.</summary>
    public bool Taken => Refusal is null;
}
