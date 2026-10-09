namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The population lease. Every test here is a refusal or the release that ends one, because a lease
/// that has never refused anything has not been tested.
/// </summary>
public sealed class WorldDriveAuthorityTests
{
    private static WorldDriveAuthority Fresh() =>
        WorldDriveAuthority.ForWorld("test://" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void StartingAmbientTrafficWhileSumoHoldsTheWorldFailsAndNamesTheHolder()
    {
        WorldDriveAuthority authority = Fresh();
        using PopulationLease held = authority.Acquire(PopulationMode.SumoDrivenPlayback,
                                                       "CarlaNet.CoSim session 7");

        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => authority.Acquire(PopulationMode.TrafficManagerAmbient, "TrafficController"));

        Assert.Equal(PopulationMode.TrafficManagerAmbient, refused.Requested);
        Assert.Equal(PopulationMode.SumoDrivenPlayback, refused.HeldMode);
        Assert.Equal("CarlaNet.CoSim session 7", refused.HeldBy);
        Assert.Contains("CarlaNet.CoSim session 7", refused.Message);
    }

    [Fact]
    public void AndTheRefusalRunsTheOtherWayRound()
    {
        WorldDriveAuthority authority = Fresh();
        using PopulationLease held = authority.Acquire(PopulationMode.TrafficManagerAmbient,
                                                       "TrafficController");

        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => authority.Acquire(PopulationMode.SumoDrivenPlayback, "CarlaNet.CoSim"));
        Assert.Equal("TrafficController", refused.HeldBy);
    }

    [Fact]
    public void AComponentThatOnlyWantsToKnowIsRefusedBeforeItSpawnsAnything()
    {
        WorldDriveAuthority authority = Fresh();
        authority.RequireAvailable(PopulationMode.TrafficManagerAmbient);   // free: no refusal

        using PopulationLease held = authority.Acquire(PopulationMode.SumoDrivenPlayback, "CoSim");
        Assert.Throws<PopulationAuthorityHeldException>(
            () => authority.RequireAvailable(PopulationMode.TrafficManagerAmbient));
    }

    [Fact]
    public void AReleasedLeaseLeavesTheWorldToWhoeverComesNext()
    {
        WorldDriveAuthority authority = Fresh();
        PopulationLease first = authority.Acquire(PopulationMode.SumoDrivenPlayback, "CoSim");
        Assert.NotNull(authority.CurrentHolder);

        first.Dispose();
        Assert.True(first.IsReleased);
        Assert.Null(authority.CurrentHolder);

        using PopulationLease second = authority.Acquire(PopulationMode.TrafficManagerAmbient,
                                                         "TrafficController");
        Assert.Equal(PopulationMode.TrafficManagerAmbient, second.Mode);
    }

    [Fact]
    public void ReleasingTwiceDoesNotTakeTheWorldFromTheNextHolder()
    {
        WorldDriveAuthority authority = Fresh();
        PopulationLease first = authority.Acquire(PopulationMode.SumoDrivenPlayback, "CoSim");
        first.Dispose();

        using PopulationLease second = authority.Acquire(PopulationMode.TrafficManagerAmbient, "TM");
        first.Dispose();

        Assert.Same(second, authority.CurrentHolder);
    }

    [Fact]
    public void AStoryboardTakesNoLeaseAndIsRefusedIfItAsksForOne()
    {
        WorldDriveAuthority authority = Fresh();
        Assert.False(WorldDriveAuthority.ClaimsPopulationAuthority(PopulationMode.StoryboardExecution));

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => authority.Acquire(PopulationMode.StoryboardExecution, "ScenarioExecutor"));
        Assert.Contains("generates no population", refused.Message);
    }

    [Fact]
    public void TwoWorldsAreLeasedIndependently()
    {
        WorldDriveAuthority one = Fresh();
        WorldDriveAuthority other = Fresh();

        using PopulationLease first = one.Acquire(PopulationMode.SumoDrivenPlayback, "CoSim");
        using PopulationLease second = other.Acquire(PopulationMode.TrafficManagerAmbient, "TM");

        Assert.NotNull(one.CurrentHolder);
        Assert.NotNull(other.CurrentHolder);
    }

    [Fact]
    public void TheHolderOfThePopulationIsTheOneThatCommandsTheSun()
    {
        WorldDriveAuthority authority = Fresh();
        using PopulationLease held = authority.Acquire(PopulationMode.SumoDrivenPlayback, "CoSim");
        Assert.True(held.CommandsTheSun);
    }

    [Fact]
    public void ThePlaybackBridgeCannotNameTheTrafficManagerAtAll()
    {
        // The first of the three lockout mechanisms, and the only one a test can establish outright:
        // a component that cannot reference an assembly cannot start what is in it.
        string[] referenced = typeof(WorldDriveAuthority).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("CarlaNet.TrafficManager", referenced);
    }
}
