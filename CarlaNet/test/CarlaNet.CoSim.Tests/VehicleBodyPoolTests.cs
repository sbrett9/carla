using CarlaNet.Types.Rpc.Commands;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Lending bodies to vehicles: spawn once, reuse, refuse past the budget, destroy at the end.
/// </summary>
public sealed class VehicleBodyPoolTests
{
    private static readonly VehicleParking Parking = new(5000.0, 5000.0, -300.0, 20.0, 16);

    [Fact]
    public void ABodyIsSpawnedOnceAndLentAgainToTheNextVehicleOfItsBlueprint()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking, maximumBodies: 8);

        Assert.True(pool.TryCheckOut("first", "vehicle.dodge.charger", out PooledBody held));
        Assert.True(pool.TryCheckIn("first", out PooledBody returned));
        Assert.Equal(held.Actor, returned.Actor);
        Assert.True(pool.TryCheckOut("second", "vehicle.dodge.charger", out PooledBody again));

        Assert.Equal(held.Actor, again.Actor);
        Assert.Equal(["vehicle.dodge.charger"], world.Spawned);
    }

    [Fact]
    public void EveryBodyStandsInItsOwnSlotWithNoPhysicsAndNoGravity()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking, maximumBodies: 8);

        pool.TryCheckOut("first", "vehicle.dodge.charger", out PooledBody first);
        pool.TryCheckOut("second", "vehicle.dodge.charger", out PooledBody second);

        // Two bodies of one blueprint, because the first is still out. Neither may be spawned onto
        // the other: CARLA refuses a spawn whose point is occupied.
        Assert.NotEqual(first.Actor, second.Actor);
        Assert.NotEqual((first.Parking.Location.X, first.Parking.Location.Y),
                        (second.Parking.Location.X, second.Parking.Location.Y));

        // And each spawn is followed, before any tick, by the two commands that stop the world
        // moving it.
        Assert.Equal(2, world.Batches.Count);
        foreach (IReadOnlyList<Command> batch in world.Batches)
        {
            Assert.Collection(
                batch,
                command => Assert.False(Assert.IsType<SetSimulatePhysicsCommand>(command).Enabled),
                command => Assert.False(Assert.IsType<SetEnableGravityCommand>(command).Enabled));
        }
    }

    [Fact]
    public void TheBudgetIsSpentRatherThanExceeded()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking, maximumBodies: 2);

        Assert.True(pool.TryCheckOut("first", "vehicle.dodge.charger", out _));
        Assert.True(pool.TryCheckOut("second", "vehicle.lincoln.mkz", out _));
        Assert.False(pool.TryCheckOut("third", "vehicle.mini.cooper", out _));

        Assert.Equal(1, pool.Exhaustions);
        Assert.Equal(2, world.Spawned.Count);

        // A decline is not a refusal for ever: the next release frees a body, though only for a
        // vehicle that wants that blueprint.
        pool.TryCheckIn("first", out _);
        Assert.False(pool.TryCheckOut("third", "vehicle.mini.cooper", out _));
        Assert.True(pool.TryCheckOut("fourth", "vehicle.dodge.charger", out _));
    }

    [Fact]
    public void TheSessionSEndDestroysEveryBodyInOneBatch()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking, maximumBodies: 8);

        pool.TryCheckOut("first", "vehicle.dodge.charger", out _);
        pool.TryCheckOut("second", "vehicle.lincoln.mkz", out _);
        pool.TryCheckIn("second", out _);

        IReadOnlyList<CommandResponse> responses = pool.DestroyAll();

        Assert.Equal(2, responses.Count);
        Assert.All(world.Batches[^1], command => Assert.IsType<DestroyActorCommand>(command));
        Assert.Empty(pool.Bodies);
        Assert.Equal(0, pool.HeldBodies);
    }

    [Fact]
    public void AVehicleAskingTwiceIsLentTheSameBodyRatherThanASecond()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking, maximumBodies: 8);

        pool.TryCheckOut("first", "vehicle.dodge.charger", out PooledBody once);
        pool.TryCheckOut("first", "vehicle.dodge.charger", out PooledBody twice);

        Assert.Equal(once.Actor, twice.Actor);
        Assert.Single(world.Spawned);
        Assert.Equal(1, pool.HeldBodies);
    }
}
