namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Taking the world's drive lease, being refused it, running on a server that has none, and giving
/// it back on every path out.
/// </summary>
/// <remarks>
/// The lease is the one mechanism that reaches a second process: the process-local
/// <see cref="WorldDriveAuthority"/> cannot see a traffic manager started from another harness, and
/// the server's refusal of every control write is only in force while the server holds the lease. So
/// which of the three outcomes a claim had -- granted, held by another, or no lease to grant -- and
/// what each leaves on the server are what these establish.
/// </remarks>
public sealed class DriveLeaseTests
{
    [Fact]
    public void ALeaseIsTakenUnderItsNameAndIsInForce()
    {
        var world = new RecordedWorld();

        using DriveLease lease = DriveLease.Take(world, "a drive (process 41 on HOST)");

        Assert.True(lease.InForce);
        Assert.Null(lease.Refusal);
        Assert.Equal("a drive (process 41 on HOST)", lease.Holder);
        Assert.Equal("a drive (process 41 on HOST)", world.DriveLeaseHolder);
        Assert.Equal(["a drive (process 41 on HOST)"], world.DriveLeaseClaims);
        Assert.Contains("held by a drive (process 41 on HOST)", lease.ToString());
    }

    [Fact]
    public void ALeaseIsGivenBackUnderItsNameAndOnlyOnce()
    {
        var world = new RecordedWorld();
        DriveLease lease = DriveLease.Take(world, "a drive");

        lease.Dispose();
        lease.Dispose();

        Assert.Null(world.DriveLeaseHolder);
        Assert.Equal(["a drive"], world.DriveLeaseReleases);
        Assert.True(lease.IsReleased);
        Assert.False(lease.InForce);
        Assert.Contains("given back", lease.ToString());
    }

    [Fact]
    public void ALeaseAnotherHoldsIsRefusedNamingTheHolderAndTakesNothing()
    {
        var world = new RecordedWorld { DriveLeaseHolder = "another drive (process 7 on ELSEWHERE)" };

        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => DriveLease.Take(world, "this drive"));

        Assert.Equal(CoSimSessionStage.Authority, refused.Stage);
        Assert.Equal("another drive (process 7 on ELSEWHERE)", refused.HeldBy);
        Assert.Equal(PopulationMode.SumoDrivenPlayback, refused.Requested);
        Assert.Equal(PopulationMode.SumoDrivenPlayback, refused.HeldMode);
        Assert.Contains("another drive (process 7 on ELSEWHERE)", refused.Message);
        Assert.Contains("break_drive_lease", refused.Message);
        // The world is as it was found: the other holder keeps the lease, and nothing was released.
        Assert.Equal("another drive (process 7 on ELSEWHERE)", world.DriveLeaseHolder);
        Assert.Empty(world.DriveLeaseReleases);
    }

    [Fact]
    public void AServerWithoutTheLeaseIsNotInForceAndSaysSoAndReleasesNothing()
    {
        const string refusal = "rpclib: server could not find function 'take_drive_lease' with argument count 2.";
        var world = new RecordedWorld { RefusesDriveLease = refusal };

        DriveLease lease = DriveLease.Take(world, "this drive");

        Assert.False(lease.InForce);
        Assert.Equal(refusal, lease.Refusal);
        Assert.Contains("not held", lease.ToString());
        Assert.Contains(refusal, lease.ToString());

        // Nothing was granted, so nothing is given back: a release on such a server would be refused
        // too, and would read as a failure of the shutdown.
        lease.Dispose();
        Assert.Empty(world.DriveLeaseReleases);
        Assert.True(lease.IsReleased);
    }

    [Fact]
    public void AReleaseTheServerRefusesIsRaisedInTheServersWords()
    {
        var world = new RecordedWorld();
        DriveLease lease = DriveLease.Take(world, "this drive");
        // Something ended the lease under the run -- break_drive_lease, or a world reload -- and
        // another took it.
        world.DriveLeaseHolder = "a drive that took it after it was broken";

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(lease.Dispose);

        Assert.Contains("this drive", refused.Message);
        Assert.Contains("a drive that took it after it was broken", refused.Message);
        Assert.True(lease.IsReleased);
    }

    [Fact]
    public void ABlankNameIsRefusedBeforeAnythingIsSent()
    {
        var world = new RecordedWorld();

        Assert.Throws<ArgumentException>(() => DriveLease.Take(world, " "));

        Assert.Empty(world.DriveLeaseClaims);
    }
}
