namespace CarlaNet.CoSim;

/// <summary>Which rule a render-set pass decided the render set by.</summary>
public enum RenderSetRule
{
    /// <summary>The configured circle: inside its admit radius, released beyond its release radius.</summary>
    Circle,

    /// <summary>The registered cameras' ground footprints, with their margins, lead and lag.</summary>
    Cameras,
}
