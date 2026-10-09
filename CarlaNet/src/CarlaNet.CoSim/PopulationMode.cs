namespace CarlaNet.CoSim;

/// <summary>What is generating the vehicles a world contains.</summary>
public enum PopulationMode
{
    /// <summary>SUMO simulates the population and the bridge renders it.</summary>
    SumoDrivenPlayback,

    /// <summary>The traffic manager generates and drives ambient traffic over the staging ring.</summary>
    TrafficManagerAmbient,

    /// <summary>
    /// A storyboard places named entities. It generates no population at all, so it takes no
    /// population authority and cannot hold the lease.
    /// </summary>
    StoryboardExecution,

    /// <summary>The engine replays a recorded run, respawning its actors and owning their poses.</summary>
    RecordedReplay,
}
