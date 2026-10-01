using CarlaNet.Types.Rpc.Commands;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Lending bodies to vehicles: spawn once, reuse, grow to whatever the scenario needs, destroy at the
/// end.
/// </summary>
public sealed class VehicleBodyPoolTests
{
    private static readonly VehicleParking Parking = new(5000.0, 5000.0, -300.0, 20.0, 16);

    [Fact]
    public void ABodyIsSpawnedOnceAndLentAgainToTheNextVehicleOfItsBlueprint()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking);

        PooledBody held = pool.CheckOut("first", "vehicle.dodge.charger");
        Assert.True(pool.TryCheckIn("first", out PooledBody returned));
        Assert.Equal(held.Actor, returned.Actor);
        PooledBody again = pool.CheckOut("second", "vehicle.dodge.charger");

        Assert.Equal(held.Actor, again.Actor);
        Assert.Equal(["vehicle.dodge.charger"], world.Spawned);
    }

    [Fact]
    public void EveryBodyStandsInItsOwnSlotWithNoPhysicsAndNoGravity()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking);

        PooledBody first = pool.CheckOut("first", "vehicle.dodge.charger");
        PooledBody second = pool.CheckOut("second", "vehicle.dodge.charger");

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
    public void ThePoolGrowsToEveryVehicleTheScenarioHoldsAtOnce()
    {
        // Three hundred vehicles at once, of three blueprints: every one is lent a body of its own,
        // each standing in its own slot, and none is refused.
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking);
        string[] blueprints = ["vehicle.dodge.charger", "vehicle.lincoln.mkz", "vehicle.mini.cooper"];

        List<PooledBody> lent = [.. Enumerable.Range(0, 300)
            .Select(index => pool.CheckOut($"v{index:000}", blueprints[index % blueprints.Length]))];

        Assert.Equal(300, pool.HeldBodies);
        Assert.Equal(300, pool.Bodies.Count);
        Assert.Equal(300, world.Spawned.Count);
        Assert.Equal(300, lent.Select(body => body.Actor).Distinct().Count());
        Assert.Equal(300, lent.Select(body => (body.Parking.Location.X, body.Parking.Location.Y))
                              .Distinct().Count());
    }

    [Fact]
    public void ABodyGivenBackIsLentOnlyToAVehicleOfItsOwnBlueprint()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking);

        PooledBody charger = pool.CheckOut("first", "vehicle.dodge.charger");
        pool.TryCheckIn("first", out _);

        // Another blueprint gets a body of its own shape, spawned for it; the free body waits for one
        // of its own.
        PooledBody mini = pool.CheckOut("second", "vehicle.mini.cooper");
        Assert.NotEqual(charger.Actor, mini.Actor);
        Assert.Equal(charger.Actor, pool.CheckOut("third", "vehicle.dodge.charger").Actor);
        Assert.Equal(["vehicle.dodge.charger", "vehicle.mini.cooper"], world.Spawned);
    }

    [Fact]
    public void TheSessionSEndDestroysEveryBodyInOneBatch()
    {
        var world = new RecordedWorld();
        var pool = new VehicleBodyPool(world, Parking);

        pool.CheckOut("first", "vehicle.dodge.charger");
        pool.CheckOut("second", "vehicle.lincoln.mkz");
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
        var pool = new VehicleBodyPool(world, Parking);

        PooledBody once = pool.CheckOut("first", "vehicle.dodge.charger");
        PooledBody twice = pool.CheckOut("first", "vehicle.dodge.charger");

        Assert.Equal(once.Actor, twice.Actor);
        Assert.Single(world.Spawned);
        Assert.Equal(1, pool.HeldBodies);
    }
}
