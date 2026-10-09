using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What one tick writes to the bodies it drives, and the order it writes it in.
/// </summary>
/// <remarks>
/// The order is checked by applying the batch to a world that visits it in order, as the server
/// does, and reading back what each body holds afterwards -- which is the only thing the order is
/// for. Every body here has had its physics disabled first, as the pool disables it before a body
/// is ever lent.
/// </remarks>
public sealed class TickBatchTests
{
    private static readonly Transform Parking =
        new(new Location(500f, 500f, -500f), new Rotation(0f, 0f, 0f));

    private static readonly VehiclePose Driving =
        new("v", "vehicle.fuso.mitsubishi", 12.0, -34.0, 2.5, 30.0, 1.5, -0.5, 10.0, 5.0, 0.25, false);

    private static readonly VehiclePose Following =
        new("w", "vehicle.fuso.mitsubishi", -8.0, 21.0, 1.0, -90.0, 0.0, 0.0, 0.0, -7.0, 0.0, false);

    [Fact]
    public void APoseIsWrittenAsItsTransformThenTheVelocityOfTheSamePose()
    {
        var batch = new TickBatch();
        batch.Begin();
        batch.Pose(7, Driving);

        Assert.Collection(
            batch.Commands,
            command => Assert.Equal(new ApplyTransformCommand(7, ExpectedTransform(Driving)), command),
            command => Assert.Equal(new ApplyTargetVelocityCommand(7, new Vector3D(10f, 5f, 0.25f)), command));
    }

    [Fact]
    public void ABodyGivenBackIsParkedAndStilledAtTheHeadOfTheNextTick()
    {
        (RecordedWorld world, ActorId parked, ActorId next) = TwoKinematicBodies();
        var batch = new TickBatch();
        batch.Begin();
        batch.Pose(parked, Driving);
        world.ApplyBatch(batch.Commands);

        // Given back between ticks, while SUMO's answer is read: nothing is written for it until
        // the next tick begins, and then it comes first.
        batch.Park(parked, Parking);
        Assert.Equal(2, batch.Commands.Count);

        batch.Begin();
        batch.Pose(next, Following);

        Assert.Collection(
            batch.Commands,
            command => Assert.Equal(new ApplyTransformCommand(parked, Parking), command),
            command => Assert.Equal(new ApplyTargetVelocityCommand(parked, new Vector3D(0f, 0f, 0f)), command),
            command => Assert.Equal(new ApplyTransformCommand(next, ExpectedTransform(Following)), command),
            command => Assert.Equal(new ApplyTargetVelocityCommand(next, ExpectedVelocity(Following)), command));

        // Applied, the parked body stands in its slot and reports standing still, rather than the
        // speed it had when it was last driven.
        world.ApplyBatch(batch.Commands);
        Assert.Equal(Parking, world.ObservedTransform(parked));
        Assert.Equal(new Vector3D(0f, 0f, 0f), world.ObservedVelocity(parked));
    }

    [Fact]
    public void ABodyGivenBackAndLentAgainInOneTickHoldsItsNewPoseAndVelocity()
    {
        // One body, released by one vehicle and checked out by the next before the tick is written:
        // two pairs for one actor in one batch. The server keeps the later of them, so the parking
        // pair has to come first or the next vehicle's body renders in the car park.
        (RecordedWorld world, ActorId body, _) = TwoKinematicBodies();
        var batch = new TickBatch();
        batch.Begin();
        batch.Pose(body, Driving);
        world.ApplyBatch(batch.Commands);

        batch.Park(body, Parking);
        batch.Begin();
        batch.Pose(body, Following);
        world.ApplyBatch(batch.Commands);

        Assert.Equal(ExpectedTransform(Following), world.ObservedTransform(body));
        Assert.Equal(ExpectedVelocity(Following), world.ObservedVelocity(body));
    }

    [Fact]
    public void ABodyHeldWithNoPoseThisTickStaysWhereItWasAndReportsStandingStill()
    {
        (RecordedWorld world, ActorId body, _) = TwoKinematicBodies();
        var batch = new TickBatch();
        batch.Begin();
        batch.Pose(body, Driving);
        world.ApplyBatch(batch.Commands);
        Assert.Equal(ExpectedVelocity(Driving), world.ObservedVelocity(body));

        // No ground under its next pose: it is not moved, so it must not go on reporting the speed
        // of a pose it no longer follows.
        batch.Begin();
        batch.HoldStill(body);
        Assert.Equal([new ApplyTargetVelocityCommand(body, new Vector3D(0f, 0f, 0f))], batch.Commands);
        world.ApplyBatch(batch.Commands);

        Assert.Equal(ExpectedTransform(Driving), world.ObservedTransform(body));
        Assert.Equal(new Vector3D(0f, 0f, 0f), world.ObservedVelocity(body));
    }

    [Fact]
    public void NothingWrittenOnOneTickIsWrittenAgainOnTheNext()
    {
        var batch = new TickBatch();
        batch.Begin();
        batch.Pose(7, Driving);
        batch.Begin();

        Assert.Empty(batch.Commands);
    }

    /// <summary>A world holding two bodies with their physics disabled, as the pool leaves them.</summary>
    private static (RecordedWorld World, ActorId First, ActorId Second) TwoKinematicBodies()
    {
        var world = new RecordedWorld();
        ActorId first = world.Spawn("vehicle.fuso.mitsubishi", Parking, VehicleBodyPool.RoleName);
        ActorId second = world.Spawn("vehicle.fuso.mitsubishi", Parking, VehicleBodyPool.RoleName);
        world.ApplyBatch([
            new SetSimulatePhysicsCommand(first, false),
            new SetSimulatePhysicsCommand(second, false),
        ]);
        return (world, first, second);
    }

    /// <summary>
    /// The transform a pose should be written as, spelled out from its fields rather than taken from
    /// the code under test.
    /// </summary>
    private static Transform ExpectedTransform(in VehiclePose pose) =>
        new(new Location((float)pose.X, (float)pose.Y, (float)pose.Z),
            new Rotation((float)pose.PitchDegrees, (float)pose.YawDegrees, (float)pose.RollDegrees));

    /// <summary>The velocity a pose should be written with, spelled out from its fields.</summary>
    private static Vector3D ExpectedVelocity(in VehiclePose pose) =>
        new((float)pose.VelocityX, (float)pose.VelocityY, (float)pose.VelocityZ);
}
