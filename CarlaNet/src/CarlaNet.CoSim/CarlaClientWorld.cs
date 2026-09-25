using CarlaNet.Transport;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// The <see cref="ICarlaWorld"/> a connected <see cref="CarlaClient"/> is.
/// </summary>
/// <remarks>
/// <para>A thin adapter and deliberately nothing more: each operation is the client call of the same
/// name, waited on. The waiting is what makes it thin -- the bridge runs on one thread between
/// ticks, so a task it does not await is a pose written into a frame nobody can name.</para>
///
/// <para>The blueprint definitions are read once and indexed. A pool that spawns a hundred bodies
/// would otherwise ask the server for the same several hundred definitions a hundred times, and the
/// answer cannot change inside a session: the definitions belong to the content build.</para>
/// </remarks>
public sealed class CarlaClientWorld : ICarlaWorld
{
    private readonly CarlaClient _client;
    private readonly Dictionary<string, ActorDescription> _blueprints = [];

    private CarlaClientWorld(CarlaClient client, IReadOnlyList<ActorDefinition> definitions)
    {
        _client = client;
        foreach (ActorDefinition definition in definitions)
        {
            _blueprints[definition.Id] = new ActorDescription(
                definition.Uid,
                definition.Id,
                definition.Attributes
                    .Select(attribute => new ActorAttributeValue(attribute.Id, attribute.Type,
                                                                 attribute.Value))
                    .ToList());
        }
    }

    /// <summary>Every blueprint the server offers, by id.</summary>
    public IReadOnlyCollection<string> BlueprintIds => _blueprints.Keys;

    /// <summary>
    /// Attach to a connected client: read its blueprint definitions and start the world observer the
    /// pose read-back is taken from.
    /// </summary>
    /// <remarks>
    /// Starting the observer here rather than leaving it to the caller is what makes
    /// <see cref="ObservedTransform"/> answer at all. It is idempotent in effect -- a second
    /// subscription would be a second stream -- so a client that already has one is left alone.
    /// </remarks>
    public static CarlaClientWorld Attach(CarlaClient client, bool startWorldObserver = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (startWorldObserver)
        {
            client.StartWorldObserverAsync().GetAwaiter().GetResult();
        }

        IReadOnlyList<ActorDefinition> definitions =
            client.GetActorDefinitionsAsync().GetAwaiter().GetResult();
        return new CarlaClientWorld(client, definitions);
    }

    /// <inheritdoc/>
    public EpisodeSettings ReadSettings() =>
        _client.GetEpisodeSettingsAsync().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public void WriteSettings(EpisodeSettings settings) =>
        _client.SetEpisodeSettingsAsync(settings).GetAwaiter().GetResult();

    /// <inheritdoc/>
    /// <exception cref="CoSimSessionRefusedException">
    /// The content build carries no blueprint of that id, which is a catalogue measured against a
    /// different build and not something to substitute a body for.
    /// </exception>
    public ActorId Spawn(string blueprintId, Transform at)
    {
        ArgumentException.ThrowIfNullOrEmpty(blueprintId);
        if (!_blueprints.TryGetValue(blueprintId, out ActorDescription description))
        {
            throw new CoSimSessionRefusedException(
                $"The server offers no blueprint '{blueprintId}', though the catalogue holds a "
                + $"measurement for it, so the catalogue was measured against a content build this "
                + $"server does not have. The server offers {_blueprints.Count} blueprints. A body "
                + "of another shape is not a substitute: the truth record would describe the "
                + "vehicle the scenario asked for and the imagery would show something else.");
        }

        Actor actor = _client.SpawnActorAsync(description, at).GetAwaiter().GetResult();
        return actor.Id;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandResponse> ApplyBatch(IReadOnlyList<Command> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return commands.Count == 0
            ? []
            : _client.ApplyBatchSyncAsync(commands, doTickCue: false).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public Transform? ObservedTransform(ActorId actor) =>
        _client.GetActorSnapshot(actor) is { } snapshot ? snapshot.Transform : null;

    /// <inheritdoc/>
    /// <remarks>
    /// The cue is answered with the frame it produced, and the client waits for the world observer
    /// to deliver that frame before returning. A tick whose frame never arrives answers null rather
    /// than throwing, because it is the session that decides a world which stopped producing frames
    /// is a failed run.
    /// </remarks>
    public ulong? Tick()
    {
        ulong frame = _client.SendTickCueAsync().GetAwaiter().GetResult();
        return _client.LatestObservedFrame >= frame ? frame : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The answer is discarded. The server returns a bare success for every layer name it
    /// recognises and for every one it does not, so nothing about it establishes that the frame
    /// changed; what a hidden layer did to the imagery is read off a written frame. The call is
    /// rendering-only at the far end -- collision is a separate server call that this one does not
    /// touch -- so hiding the road surface leaves it collidable and hiding the signals leaves their
    /// stop-line triggers live.
    /// </remarks>
    public void WriteLayerVisible(string layer, bool visible)
    {
        ArgumentException.ThrowIfNullOrEmpty(layer);
        _client.SetLayerVisibleAsync(layer, visible).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public IReadOnlyList<double> ReadSolarState() =>
        _client.GetSolarStateAsync().GetAwaiter().GetResult() ?? [];

    /// <inheritdoc/>
    public bool WriteSolarEpoch(int year, int month, int day, double hours, double utcOffsetHours) =>
        _client.SetSolarEpochAsync(year, month, day, hours, utcOffsetHours).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public bool WriteTimeAdvance(bool advancing, double rate) =>
        _client.SetTimeAdvanceAsync(advancing, rate).GetAwaiter().GetResult();

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="Tick"/> returns once the observer has delivered the frame the cue produced, and in
    /// synchronous mode no later frame exists until the next cue, so after a tick this is that tick's
    /// sun.
    /// </remarks>
    public IReadOnlyList<double> ObservedSolarState() => _client.GetCachedSolarState();
}
