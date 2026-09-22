using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A CARLA world that records what was asked of it and answers as a server would.
/// </summary>
/// <remarks>
/// <para>Everything the bridge does to a world is six operations wide, so a world that keeps a
/// dictionary of actors and a list of batches exercises the whole driving path -- the pool, the
/// batch, the read-back, the tick and the settings restoration -- with no server, no engine and no
/// render. What it cannot establish is what a body looks like once the pose is applied, which is the
/// one thing only a live run can answer.</para>
///
/// <para>It answers the read-back with exactly what was commanded, plus whatever
/// <see cref="TransformDrift"/> is set to. A drift of zero is a world that does what it is told and
/// proves the comparison is wired; a drift of something is a world that does not, and proves the
/// comparison would notice.</para>
/// </remarks>
internal sealed class RecordedWorld : ICarlaWorld
{
    private readonly Dictionary<ActorId, Transform> _actors = [];
    private readonly List<IReadOnlyList<Command>> _batches = [];
    private ActorId _nextActor = 1;

    /// <summary>The settings the world holds, as a server would.</summary>
    public EpisodeSettings Settings { get; set; } =
        new(SynchronousMode: false, NoRenderingMode: false, FixedDeltaSeconds: null,
            Substepping: true, MaxSubstepDeltaTime: 0.01, MaxSubsteps: 10,
            MaxCullingDistance: 0f, DeterministicRagdolls: false, TileStreamDistance: 3000f,
            ActorActiveDistance: 2000f, SpectatorAsEgo: true);

    /// <summary>How many times the settings were written.</summary>
    public List<EpisodeSettings> SettingsWrites { get; } = [];

    /// <summary>Every batch, in the order it was applied.</summary>
    public IReadOnlyList<IReadOnlyList<Command>> Batches => _batches;

    /// <summary>Blueprints spawned, in order, one entry per body.</summary>
    public List<string> Spawned { get; } = [];

    /// <summary>World ticks asked for.</summary>
    public long Ticks { get; private set; }

    /// <summary>What the world does with a commanded pose before reporting it back.</summary>
    public Location TransformDrift { get; set; }

    /// <summary>Degrees added to the reported yaw, pitch and roll.</summary>
    public Rotation RotationDrift { get; set; }

    /// <summary>Set to stop the world producing frames, as a stalled server does.</summary>
    public bool ProducesFrames { get; set; } = true;

    /// <summary>Set to have the next tick throw, as a dropped connection does.</summary>
    public Exception? ThrowOnTick { get; set; }

    /// <summary>Batches carrying at least one transform, which is what a driven tick writes.</summary>
    public IEnumerable<IReadOnlyList<Command>> PoseBatches =>
        _batches.Where(batch => batch.Any(command => command is ApplyTransformCommand));

    /// <inheritdoc/>
    public EpisodeSettings ReadSettings() => Settings;

    /// <inheritdoc/>
    public void WriteSettings(EpisodeSettings settings)
    {
        SettingsWrites.Add(settings);
        Settings = settings;
    }

    /// <inheritdoc/>
    public ActorId Spawn(string blueprintId, Transform at)
    {
        Spawned.Add(blueprintId);
        ActorId actor = _nextActor++;
        _actors[actor] = at;
        return actor;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandResponse> ApplyBatch(IReadOnlyList<Command> commands)
    {
        _batches.Add(commands);
        var responses = new List<CommandResponse>(commands.Count);
        foreach (Command command in commands)
        {
            switch (command)
            {
                case ApplyTransformCommand transform:
                    _actors[transform.Actor] = transform.Transform;
                    responses.Add(CommandResponse.Success(transform.Actor));
                    break;
                case DestroyActorCommand destroy:
                    _actors.Remove(destroy.Actor);
                    responses.Add(CommandResponse.Success(destroy.Actor));
                    break;
                default:
                    responses.Add(CommandResponse.Success(0));
                    break;
            }
        }

        return responses;
    }

    /// <inheritdoc/>
    public Transform? ObservedTransform(ActorId actor)
    {
        if (!_actors.TryGetValue(actor, out Transform held))
        {
            return null;
        }

        return new Transform(
            new Location(held.Location.X + TransformDrift.X,
                         held.Location.Y + TransformDrift.Y,
                         held.Location.Z + TransformDrift.Z),
            new Rotation(held.Rotation.Pitch + RotationDrift.Pitch,
                         held.Rotation.Yaw + RotationDrift.Yaw,
                         held.Rotation.Roll + RotationDrift.Roll));
    }

    /// <inheritdoc/>
    public bool Tick()
    {
        if (ThrowOnTick is { } failure)
        {
            throw failure;
        }

        Ticks++;
        return ProducesFrames;
    }
}
