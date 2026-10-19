namespace CarlaNet.CoSim;

/// <summary>Which rule a render-set pass decided the render set by.</summary>
public enum RenderSetRule
{
    /// <summary>
    /// Every vehicle SUMO has, up to the capacity where one is set: the default, which with no capacity
    /// limits nothing.
    /// </summary>
    Every,

    /// <summary>The configured circle: inside its admit radius, released beyond its release radius.</summary>
    Circle,

    /// <summary>The registered cameras' ground footprints, with their margins, lead and lag.</summary>
    Cameras,
}
