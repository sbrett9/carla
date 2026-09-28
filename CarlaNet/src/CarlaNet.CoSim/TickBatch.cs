using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// The commands one world tick writes to the bodies the bridge drives, in the order the server has
/// to apply them.
/// </summary>
/// <remarks>
/// <para><b>Every written pose carries its velocity.</b> A vehicle whose physics is disabled holds the
/// last velocity it was given and reports it to everything that reads the world -- the truth
/// telemetry, the recorder, radar -- so what this batch writes is the whole of what they read. A body
/// whose pose is written gets a target velocity straight after its transform. A body the bridge holds
/// and cannot place this tick gets zero, because it stands where it was left. A body given back gets
/// zero after its parking transform, because otherwise it would report its last speed from beyond the
/// sandbox for as long as it stays parked.</para>
///
/// <para><b>The order is the contract.</b> The server visits a batch in order, so where one batch
/// carries two pairs for one actor -- a body given back by one vehicle and lent to the next in the
/// same tick -- the later pair is what the actor holds when the frame renders. Bodies given back are
/// therefore written at the head of the next tick's batch, and every pose after them.</para>
///
/// <para><b>Placing a new body writes no velocity.</b> The pool disables a body's physics before the
/// body is ever lent, and disabling it zeroes the velocity. A velocity written while physics was still
/// on would have gone to the simulating body rather than to the field a kinematic vehicle is read
/// from, so nothing here may precede that.</para>
/// </remarks>
internal sealed class TickBatch
{
    private static readonly Vector3D Still = new(0f, 0f, 0f);

    private readonly List<Command> _parked = [];
    private readonly List<Command> _commands = [];

    /// <summary>What the current tick writes, in the order it is written.</summary>
    public IReadOnlyList<Command> Commands => _commands;

    /// <summary>
    /// Queue a body that has been given back for the head of the next tick's batch: its parking
    /// transform, then zero velocity.
    /// </summary>
    /// <remarks>
    /// Queued rather than written, because bodies are given back while SUMO's answer is read, between
    /// ticks, and a parking pose is a pose like any other: it costs an entry in the next batch rather
    /// than a round trip of its own.
    /// </remarks>
    public void Park(ActorId actor, Transform parking)
    {
        _parked.Add(new ApplyTransformCommand(actor, parking));
        _parked.Add(new ApplyTargetVelocityCommand(actor, Still));
    }

    /// <summary>Start a tick's batch with every body given back since the last one.</summary>
    public void Begin()
    {
        _commands.Clear();
        _commands.AddRange(_parked);
        _parked.Clear();
    }

    /// <summary>Write a body's pose, then the velocity of the same pose.</summary>
    public void Pose(ActorId actor, in VehiclePose pose)
    {
        _commands.Add(new ApplyTransformCommand(actor, TransformOf(pose)));
        _commands.Add(new ApplyTargetVelocityCommand(actor, VelocityOf(pose)));
    }

    /// <summary>
    /// A body a vehicle holds and that gets no pose this tick: it is not moved, so it is told it is
    /// not moving.
    /// </summary>
    public void HoldStill(ActorId actor) =>
        _commands.Add(new ApplyTargetVelocityCommand(actor, Still));

    /// <summary>The CARLA transform a computed pose is, in the units the batch is written in.</summary>
    public static Transform TransformOf(in VehiclePose pose) =>
        new(new Location((float)pose.X, (float)pose.Y, (float)pose.Z),
            new Rotation((float)pose.PitchDegrees, (float)pose.YawDegrees, (float)pose.RollDegrees));

    /// <summary>The CARLA velocity a computed pose carries, in metres per second.</summary>
    public static Vector3D VelocityOf(in VehiclePose pose) =>
        new((float)pose.VelocityX, (float)pose.VelocityY, (float)pose.VelocityZ);
}
