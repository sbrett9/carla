namespace CarlaNet.CoSim;

/// <summary>
/// How a session drives the sun across a capture window: the four policies of the scenario
/// contract (<c>04_Contracts.md</c> section 11.6).
/// </summary>
public enum IlluminationPolicyKind
{
    /// <summary>
    /// Set once, to the civil instant the window opens, and held for the window. Illumination is a
    /// controlled constant within a window and a deliberate variable between windows.
    /// </summary>
    FreezeAtWindowStart,

    /// <summary>
    /// Set to the civil instant the window opens, then carried forward by the engine at a declared
    /// number of sun-clock seconds per simulated second.
    /// </summary>
    Advance,

    /// <summary>
    /// Set once, to a declared civil time of day, whatever instant the window opens at. The frame's
    /// civil time and the sun it is lit by then disagree on purpose, and the record says so.
    /// </summary>
    FreezeAt,

    /// <summary>
    /// The sun is not touched. The run's lighting is whatever the world held, and the record says
    /// the epoch was not honoured.
    /// </summary>
    Ignore,
}
