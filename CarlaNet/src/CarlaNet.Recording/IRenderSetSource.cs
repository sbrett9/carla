namespace CarlaNet.Recording;

/// <summary>
/// Whatever lends vehicle bodies to the vehicles it renders, answering for each rendered frame which
/// bodies were lent and to which vehicle.
/// </summary>
/// <remarks>
/// <para>A body lent from a pool renders a succession of vehicles over a run and stands out of sight
/// between loans, so the world's actor list says neither which of its vehicles are in the picture nor
/// who any of them is. Only whatever lent the bodies knows, and only frame by frame: a body is given
/// back and lent again between two ticks.</para>
///
/// <para>Keyed by the simulation frame, which is the capture's own identity, so a still is paired
/// with the render set of the very tick that rendered it rather than with whichever was newest when
/// the image arrived.</para>
/// </remarks>
public interface IRenderSetSource
{
    /// <summary>The newest frame the source has an answer for, or null before its first.</summary>
    ulong? NewestFrame { get; }

    /// <summary>The render set of a frame, where the source still holds it.</summary>
    bool TryGetRenderSet(ulong frame, out RenderSet renderSet);
}
