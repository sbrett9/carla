namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Deciding what is in frame, and putting the world's layers back on every path out.
/// </summary>
/// <remarks>
/// This has its own tests for the same reason the settings lease does. Layer visibility is global
/// state of a world an operator is also using, and a run that hid the road mesh and then threw would
/// otherwise leave an editor showing a world with no road network in it -- which is a property of
/// the failure paths, and a happy-path test establishes none of them.
/// </remarks>
public sealed class LayerVisibilityLeaseTests
{
    [Fact]
    public void EveryLayerIsWrittenOnceAndTheWritesAreRecorded()
    {
        var world = new RecordedWorld();

        using (LayerVisibilityLease lease = LayerVisibilityLease.Take(world, new Dictionary<string, bool>
        {
            [LayerVisibilityLease.RoadLayer] = false,
            [LayerVisibilityLease.SignalLayer] = false,
        }))
        {
            Assert.Equal(2, world.LayerWrites.Count);
            Assert.All(world.LayerWrites, write => Assert.False(write.Visible));
            Assert.False(lease.Applied[LayerVisibilityLease.RoadLayer]);
            Assert.False(lease.Applied[LayerVisibilityLease.SignalLayer]);

            // Before anything was rendered. A layer written after a frame exists has changed the
            // imagery mid-capture, which is the thing the lease exists to make impossible.
            Assert.All(world.LayerWrites, write => Assert.Equal(0, write.AtTick));
        }
    }

    [Fact]
    public void ALayerAskedForIsWrittenAsAskedRatherThanHidden()
    {
        var world = new RecordedWorld();

        using LayerVisibilityLease lease = LayerVisibilityLease.Take(world, new Dictionary<string, bool>
        {
            [LayerVisibilityLease.RoadLayer] = true,
            [LayerVisibilityLease.SignalLayer] = false,
        });

        Assert.True(lease.Applied[LayerVisibilityLease.RoadLayer]);
        Assert.Contains(world.LayerWrites,
                        write => write.Layer == LayerVisibilityLease.RoadLayer && write.Visible);
    }

    [Fact]
    public void EveryLayerTheLeaseWroteIsDrawnAgainWhenItIsGivenBack()
    {
        var world = new RecordedWorld();
        LayerVisibilityLease lease = LayerVisibilityLease.Take(world, new Dictionary<string, bool>
        {
            [LayerVisibilityLease.RoadLayer] = false,
            [LayerVisibilityLease.SignalLayer] = false,
        });

        lease.Dispose();

        Assert.True(lease.IsReleased);
        Assert.Equal(4, world.LayerWrites.Count);
        Assert.All(world.LayerWrites.Skip(2), write => Assert.True(write.Visible));
        Assert.Contains(world.LayerWrites.Skip(2),
                        write => write.Layer == LayerVisibilityLease.RoadLayer);
        Assert.Contains(world.LayerWrites.Skip(2),
                        write => write.Layer == LayerVisibilityLease.SignalLayer);
    }

    [Fact]
    public void GivingTheLayersBackTwiceDoesNothingTheSecondTime()
    {
        var world = new RecordedWorld();
        LayerVisibilityLease lease = LayerVisibilityLease.Take(world, new Dictionary<string, bool>
        {
            [LayerVisibilityLease.RoadLayer] = false,
        });

        lease.Dispose();
        int writes = world.LayerWrites.Count;
        lease.Dispose();

        Assert.Equal(writes, world.LayerWrites.Count);
    }

    [Fact]
    public void AWorldThatRefusesTheSecondWriteIsLeftWithNothingHidden()
    {
        // A world that takes the first layer and refuses the second. Whatever was hidden before the
        // refusal has to be drawn again: the caller holds no lease to dispose, so if the lease does
        // not put it back nothing will.
        var world = new WorldThatRefusesTheSecondLayerWrite();

        Assert.Throws<InvalidOperationException>(
            () => LayerVisibilityLease.Take(world, new Dictionary<string, bool>
            {
                [LayerVisibilityLease.RoadLayer] = false,
                [LayerVisibilityLease.SignalLayer] = false,
            }));

        Assert.Equal(2, world.LayerWrites.Count);
        Assert.False(world.LayerWrites[0].Visible);
        Assert.True(world.LayerWrites[1].Visible);
        Assert.Equal(LayerVisibilityLease.RoadLayer, world.LayerWrites[1].Layer);
    }

    /// <summary>A world whose second layer write fails, as one with no such layer would.</summary>
    private sealed class WorldThatRefusesTheSecondLayerWrite : RecordedWorld
    {
        private int _writes;

        public override void WriteLayerVisible(string layer, bool visible)
        {
            if (++_writes == 2)
            {
                throw new InvalidOperationException("no layer of that name in this world");
            }

            base.WriteLayerVisible(layer, visible);
        }
    }
}
