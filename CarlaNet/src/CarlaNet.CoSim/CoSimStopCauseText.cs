namespace CarlaNet.CoSim;

/// <summary>The text form of a <see cref="CoSimStopCause"/>.</summary>
public static class CoSimStopCauseText
{
    /// <summary>
    /// The cause as the runtime's failure paths write it -- <c>sumo-connection-lost</c>,
    /// <c>world-connection-lost</c>, <c>world-tick-timeout</c>, <c>solar-state-disagreement</c>,
    /// <c>missing-blueprint</c> -- or <c>none</c>.
    /// </summary>
    public static string Code(this CoSimStopCause cause) => cause switch
    {
        CoSimStopCause.SumoConnectionLost => "sumo-connection-lost",
        CoSimStopCause.WorldConnectionLost => "world-connection-lost",
        CoSimStopCause.WorldTickTimeout => "world-tick-timeout",
        CoSimStopCause.SolarStateDisagreement => "solar-state-disagreement",
        CoSimStopCause.MissingBlueprint => "missing-blueprint",
        _ => "none",
    };
}
