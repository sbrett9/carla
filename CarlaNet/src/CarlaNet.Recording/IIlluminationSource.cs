namespace CarlaNet.Recording;

/// <summary>
/// Whatever declared the sun a run is lit by, answering for each rendered frame what that
/// declaration was and how the world's sun compared with it.
/// </summary>
/// <remarks>
/// Keyed by the simulation frame, which is the capture's own identity, so a still is paired with
/// the declaration and the audit of the very tick that rendered it rather than with whichever was
/// newest when the image arrived.
/// </remarks>
public interface IIlluminationSource
{
    /// <summary>The newest frame the source has an answer for, or null before its first.</summary>
    ulong? NewestFrame { get; }

    /// <summary>The declaration for a frame, where the source still holds one.</summary>
    bool TryGetDeclaration(ulong frame, out IlluminationDeclaration declaration);
}
