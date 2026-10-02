using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Enums;
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
    /// <summary>The attribute an actor's role name is carried in.</summary>
    public const string RoleNameAttribute = "role_name";

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
    /// <see cref="ObservedTransform"/> and <see cref="ObservedVelocity"/> answer at all. It is
    /// idempotent in effect -- a second
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
    /// <remarks>
    /// <para>The record's grid digests are asked for only where the record says the surface is draped,
    /// which is the only case in which the server holds any grids. The grids themselves are never
    /// fetched: the digests are what the check compares.</para>
    ///
    /// <para>A server built before it published the digests answers that call with an error. The
    /// record is then described with no digests, which the check refuses under a drape and names,
    /// rather than the start failing on an error that says nothing about the world.</para>
    /// </remarks>
    public LoadedWorld DescribeLoadedWorld()
    {
        GeoLocation origin = _client.GetCesiumOriginAsync().GetAwaiter().GetResult();
        string openDrive = _client.GetMapDataAsync().GetAwaiter().GetResult() ?? string.Empty;
        IReadOnlyList<double> scalars = _client.GetBareEarthReferenceAsync().GetAwaiter().GetResult();

        BareEarthRecord? record = null;
        if (scalars is { Count: >= 7 })
        {
            bool draped = scalars[1] != 0.0;
            IReadOnlyList<string> digests = draped ? ReadGridDigests() : [];
            record = new BareEarthRecord(
                scalars[0], draped, scalars[2], scalars[3], scalars[4], (int)scalars[5], (int)scalars[6],
                digests.Count >= 2 ? digests[0] : string.Empty,
                digests.Count >= 2 ? digests[1] : string.Empty);
        }

        return new LoadedWorld(origin.Latitude, origin.Longitude, origin.Altitude, openDrive, record);
    }

    /// <summary>The server's digests of its record's two grids, or none where it publishes none.</summary>
    private IReadOnlyList<string> ReadGridDigests()
    {
        try
        {
            return _client.GetBareEarthDigestAsync().GetAwaiter().GetResult() ?? [];
        }
        catch (CarlaRpcException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public bool AdoptBareEarthGrids(string packagePath) => _client.AdoptBareEarthReference(packagePath);

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
    public ActorId Spawn(string blueprintId, Transform at, string roleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(blueprintId);
        ArgumentException.ThrowIfNullOrEmpty(roleName);
        if (!_blueprints.TryGetValue(blueprintId, out ActorDescription description))
        {
            throw new CoSimSessionRefusedException(
                $"The server offers no blueprint '{blueprintId}', though the catalogue holds a "
                + $"measurement for it, so the catalogue was measured against a content build this "
                + $"server does not have. The server offers {_blueprints.Count} blueprints. A body "
                + "of another shape is not a substitute: the truth record would describe the "
                + "vehicle the scenario asked for and the imagery would show something else.")
            {
                Cause = CoSimStopCause.MissingBlueprint,
            };
        }

        Actor actor = _client.SpawnActorAsync(WithRole(description, roleName), at).GetAwaiter().GetResult();
        return actor.Id;
    }

    /// <summary>
    /// A blueprint's description with its <c>role_name</c> set to the role given, in place of the
    /// default the definition carries, and every other attribute as the definition gives it.
    /// </summary>
    /// <remarks>
    /// Every vehicle definition declares <c>role_name</c> as a variation that is not restricted to its
    /// recommended values (doc 04 D4.9), so the server takes any role. One that declares none is given
    /// the attribute, rather than spawned with no provenance.
    /// </remarks>
    private static ActorDescription WithRole(ActorDescription description, string roleName)
    {
        var attributes = new List<ActorAttributeValue>(description.Attributes.Count + 1);
        bool named = false;
        foreach (ActorAttributeValue attribute in description.Attributes)
        {
            if (attribute.Id == RoleNameAttribute)
            {
                attributes.Add(attribute with { Value = roleName });
                named = true;
            }
            else
            {
                attributes.Add(attribute);
            }
        }

        if (!named)
        {
            attributes.Add(new ActorAttributeValue(RoleNameAttribute, ActorAttributeType.String, roleName));
        }

        return description with { Attributes = attributes };
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
    /// <remarks>
    /// A server that answers with an error -- one built before it carried a render set, which has no
    /// such call -- is answered as a refusal carrying its words, as the grid digests are, rather than
    /// thrown: the run goes on, and the report says what the server said.
    /// </remarks>
    public RenderSetWrite WriteRenderSet(IReadOnlyList<LentBody> lent, IReadOnlyList<ActorId> parked)
    {
        ArgumentNullException.ThrowIfNull(lent);
        ArgumentNullException.ThrowIfNull(parked);
        var lentIds = new ActorId[lent.Count];
        var vehicleIds = new string[lent.Count];
        var vehicleTypeIds = new string[lent.Count];
        for (int index = 0; index < lent.Count; index++)
        {
            lentIds[index] = lent[index].Actor;
            vehicleIds[index] = lent[index].VehicleId;
            vehicleTypeIds[index] = lent[index].VehicleTypeId;
        }

        try
        {
            uint found = _client.UpdateRenderSetAsync(lentIds, vehicleIds, vehicleTypeIds, parked.ToArray())
                .GetAwaiter().GetResult();
            return new RenderSetWrite((int)found, null);
        }
        catch (CarlaRpcException refused)
        {
            return new RenderSetWrite(0, refused.Message);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A server that answers with an error -- one built before it carried the call -- is answered as
    /// a refusal carrying its words, as the render set is, rather than thrown: the run goes on with
    /// every body drawn at any range, and the report says what the server said.
    /// </remarks>
    public DrawDistanceWrite WriteDrawDistance(IReadOnlyList<ActorId> bodies, double metres)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        try
        {
            uint found = _client.SetActorsMaxDrawDistanceAsync(bodies, metres).GetAwaiter().GetResult();
            return new DrawDistanceWrite((int)found, null);
        }
        catch (CarlaRpcException refused)
        {
            return new DrawDistanceWrite(0, refused.Message);
        }
    }

    /// <inheritdoc/>
    public Transform? ObservedTransform(ActorId actor) =>
        _client.GetActorSnapshot(actor) is { } snapshot ? snapshot.Transform : null;

    /// <inheritdoc/>
    public Vector3D? ObservedVelocity(ActorId actor) =>
        _client.GetActorSnapshot(actor) is { } snapshot ? snapshot.Velocity : null;

    /// <inheritdoc/>
    /// <remarks>
    /// <c>CarlaClient.GetSnapshotFrame</c>, the same history the recorder places a capture's camera
    /// from. A client whose history holds nothing yet answers from the newest snapshot.
    /// </remarks>
    public Transform? ObservedTransformAt(ActorId actor, ulong frame)
    {
        if (_client.GetSnapshotFrame(frame, out _) is { } snapshots)
        {
            return snapshots.TryGetValue(actor, out ActorSnapshot? held) ? held.Transform : null;
        }

        return ObservedTransform(actor);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One <c>get_actors_by_id</c> for the one actor. The ids go as a collection expression, which the
    /// transport writes as the array of its element type that the server reads.
    /// </remarks>
    public CameraOptics? DescribeCamera(ActorId camera)
    {
        IReadOnlyList<Actor> actors = _client.GetActorsByIdAsync([camera]).GetAwaiter().GetResult();
        if (actors.Count == 0 || actors[0].Id != camera)
        {
            return null;
        }

        Dictionary<string, string> attributes = [];
        foreach (ActorAttributeValue attribute in actors[0].Description.Attributes ?? [])
        {
            attributes[attribute.Id] = attribute.Value;
        }

        return CameraOptics.FromAttributes(attributes);
    }

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
