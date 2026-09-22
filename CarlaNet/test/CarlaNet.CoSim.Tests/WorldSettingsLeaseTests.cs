using CarlaNet.Types.Rpc.Environment;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Taking a world's clock, and giving it back on every path out.
/// </summary>
/// <remarks>
/// The reason this has its own tests rather than riding on a session's: a run that left an
/// operator's editor stranded in synchronous mode, waiting for a tick from a process that had
/// finished, is what the lease exists to prevent. That is a property of the failure paths, and a
/// happy-path test establishes none of them.
/// </remarks>
public sealed class WorldSettingsLeaseTests
{
    [Fact]
    public void TheWorldIsPutIntoSynchronousModeAndFoundAgainAsItWas()
    {
        var world = new RecordedWorld();
        EpisodeSettings before = world.Settings;

        using (WorldSettingsLease lease = WorldSettingsLease.Take(world, 0.05))
        {
            Assert.True(world.Settings.SynchronousMode);
            Assert.Equal(0.05, world.Settings.FixedDeltaSeconds);
            Assert.Equal(0.05, lease.FixedDeltaSeconds);

            // Everything else the operator had set is kept. A session owns the clock, not the
            // rendering mode, the culling distance or the substepping.
            Assert.Equal(before.NoRenderingMode, world.Settings.NoRenderingMode);
            Assert.Equal(before.MaxSubsteps, world.Settings.MaxSubsteps);
            Assert.Equal(before.SpectatorAsEgo, world.Settings.SpectatorAsEgo);
        }

        Assert.Equal(before, world.Settings);
    }

    [Fact]
    public void AWorldThatDoesNotTakeTheWriteIsRefusedAndLeftAsItWasFound()
    {
        var world = new RecordedWorld { IgnoresSettingsWrites = true };
        EpisodeSettings before = world.Settings;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => WorldSettingsLease.Take(world, 0.05));

        Assert.Contains("did not take the settings", refused.Message);
        Assert.Contains("asynchronous", refused.Message);
        Assert.Equal(before, world.Settings);

        // The refusal put the original back rather than leaving the caller to: a caller holding no
        // lease has nothing to dispose.
        Assert.Equal(2, world.SettingsWrites.Count);
    }

    [Fact]
    public void GivingTheSettingsBackTwiceDoesNothingTheSecondTime()
    {
        var world = new RecordedWorld();
        WorldSettingsLease lease = WorldSettingsLease.Take(world, 0.05);

        lease.Dispose();
        int writes = world.SettingsWrites.Count;
        lease.Dispose();

        Assert.True(lease.IsReleased);
        Assert.Equal(writes, world.SettingsWrites.Count);
    }

    [Fact]
    public void ATickLengthThatIsNotOneIsRefusedBeforeAnythingIsWritten()
    {
        var world = new RecordedWorld();

        Assert.Throws<CoSimSessionRefusedException>(() => WorldSettingsLease.Take(world, 0.0));
        Assert.Throws<CoSimSessionRefusedException>(
            () => WorldSettingsLease.Take(world, double.NaN));
        Assert.Empty(world.SettingsWrites);
    }
}
