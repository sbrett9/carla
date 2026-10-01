namespace CarlaNet.Recording;

/// <summary>
/// Which vehicles a truth sidecar lists.
/// </summary>
public enum SidecarVehicles
{
    /// <summary>
    /// Every vehicle actor the world held at the frame the records describe. What a run with no
    /// render set writes -- traffic-manager traffic, where every vehicle actor is a vehicle in the
    /// scene -- and the sidecar says nothing extra.
    /// </summary>
    World,

    /// <summary>
    /// Exactly the bodies the frame rendered, each named by the vehicle it rendered: paired with the
    /// render set of a source in the recorder's own process, or, with none, cut to the render set the
    /// server published with the frame. Written as <c>vehicles="rendered"</c> on the sidecar's
    /// container.
    /// </summary>
    Rendered,

    /// <summary>
    /// None, because the frame's render set was no longer held: which bodies it rendered, and for
    /// whom, is unknown, so no vehicle is listed rather than a guessed set. Written as
    /// <c>vehicles="unknown"</c>, so an empty list is never read as an empty scene.
    /// </summary>
    Unknown,
}
