// Offline (no engine, no server): a registered vehicle is only given up on once it has been missing
// from several consecutive world snapshots, not the first one it misses.
//
// The snapshot is the world observer's streamed actor cache, not a direct read of the simulator. A
// vehicle created moments ago has not necessarily been published into it yet — markedly so under a
// synchronous world, where the stream only advances when the host ticks. Treating a single absence
// as destruction dropped live vehicles out of the traffic manager for good: registration had
// succeeded, the client had no reason to repeat it, and the vehicle simply sat where it was created
// until the host's own stuck guard removed it. Measured live: of the vehicles culled for never
// moving, 0 of 13 were still held by the traffic manager, while every one of 42 had been taken by it
// at spawn.
#nullable enable

using CarlaNet.TrafficManager;
using CarlaNet.TrafficManager.Stages;
using Xunit;

namespace CarlaNet.Tests.TrafficManager;

public class VehicleAbsenceTests
{
    private const ActorId Vehicle = 7u;

    private static HashSet<ActorId> Present(params ActorId[] ids) => new(ids);

    /// <summary>Runs `updates` rounds with the vehicle missing, returning what was given up on.</summary>
    private static HashSet<ActorId> RunAbsent(int updates, Dictionary<ActorId, int> absent)
    {
        var deleted = new HashSet<ActorId>();
        for (int i = 0; i < updates; i++)
            ALSM.CollectDestroyedRegistered(new[] { Vehicle }, Present(), absent, deleted);
        return deleted;
    }

    [Fact]
    public void A_vehicle_in_the_snapshot_is_never_given_up_on()
    {
        var absent = new Dictionary<ActorId, int>();
        var deleted = new HashSet<ActorId>();
        for (int i = 0; i < 50; i++)
            ALSM.CollectDestroyedRegistered(new[] { Vehicle }, Present(Vehicle), absent, deleted);

        Assert.Empty(deleted);
        Assert.Empty(absent);
    }

    [Fact]
    public void One_missed_snapshot_does_not_destroy_a_live_vehicle()
    {
        // The regression itself: a vehicle the observer has not published yet must survive.
        Assert.Empty(RunAbsent(1, new Dictionary<ActorId, int>()));
    }

    [Fact]
    public void A_vehicle_that_reappears_starts_its_count_over()
    {
        var absent = new Dictionary<ActorId, int>();
        var deleted = new HashSet<ActorId>();

        // Miss all but one of the allowance, then turn up again.
        for (int i = 0; i < Constants.VehicleRemoval.MISSES_BEFORE_DESTROYED - 1; i++)
            ALSM.CollectDestroyedRegistered(new[] { Vehicle }, Present(), absent, deleted);
        ALSM.CollectDestroyedRegistered(new[] { Vehicle }, Present(Vehicle), absent, deleted);
        Assert.Empty(deleted);

        // Having been seen, it gets the full allowance again rather than dying on the next miss.
        for (int i = 0; i < Constants.VehicleRemoval.MISSES_BEFORE_DESTROYED - 1; i++)
            ALSM.CollectDestroyedRegistered(new[] { Vehicle }, Present(), absent, deleted);
        Assert.Empty(deleted);
    }

    [Fact]
    public void A_vehicle_that_really_is_gone_is_still_given_up_on()
    {
        // The capability this must not lose: a destroyed vehicle has to leave the traffic manager,
        // and within a few updates rather than eventually.
        var absent = new Dictionary<ActorId, int>();
        HashSet<ActorId> deleted = RunAbsent(Constants.VehicleRemoval.MISSES_BEFORE_DESTROYED, absent);

        Assert.Contains(Vehicle, deleted);
        Assert.Empty(absent);            // and it stops being tracked once given up on
    }
}
