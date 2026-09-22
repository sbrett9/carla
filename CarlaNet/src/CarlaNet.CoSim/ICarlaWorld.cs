using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// Everything the playback bridge asks of a CARLA world, and nothing else.
/// </summary>
/// <remarks>
/// <para>Six operations. The bridge places bodies, writes their poses in one batch, reads back where
/// the world says they went, advances the world a tick, and reads and writes the episode settings so
/// it can hand the world back as it found it. Anything larger than that would be the client's whole
/// surface, and a driving session tested against the client's whole surface is a session that can
/// only be tested against a running server.</para>
///
/// <para><b>Synchronous by design.</b> Every call here happens on the tick thread, between one world
/// tick and the next, in an order the session fixes. There is nothing for a caller to overlap, and
/// an asynchronous signature would invite a bridge that writes a pose while the frame it belongs to
/// is being rendered.</para>
/// </remarks>
public interface ICarlaWorld
{
    /// <summary>The episode settings as the server currently holds them.</summary>
    EpisodeSettings ReadSettings();

    /// <summary>Write the episode settings.</summary>
    void WriteSettings(EpisodeSettings settings);

    /// <summary>
    /// Place one body of the named blueprint at a transform, answering the actor it became.
    /// </summary>
    /// <remarks>
    /// CARLA refuses a spawn whose point is occupied and offers no queue and no retry, so a caller
    /// spawns at a point it knows to be clear and never spawns at a point a vehicle is driving
    /// through.
    /// </remarks>
    ActorId Spawn(string blueprintId, Transform at);

    /// <summary>
    /// Apply a batch of commands in one round trip, answering the server's response per command.
    /// </summary>
    IReadOnlyList<CommandResponse> ApplyBatch(IReadOnlyList<Command> commands);

    /// <summary>
    /// Where the world says an actor is, or <see langword="null"/> where it has reported nothing.
    /// </summary>
    /// <remarks>
    /// A read of the world-observer snapshot the last tick delivered, not a round trip: the observer
    /// streams every actor's transform every tick whether or not anything reads it, so comparing a
    /// commanded pose against what the world did with it costs nothing.
    /// </remarks>
    Transform? ObservedTransform(ActorId actor);

    /// <summary>
    /// Advance the world one tick, answering false where the tick produced no frame.
    /// </summary>
    bool Tick();
}
