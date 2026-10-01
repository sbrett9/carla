using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;
using CarlaNet.Types.Rpc.Lighting;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A CARLA world that records what was asked of it and answers as a server would.
/// </summary>
/// <remarks>
/// <para>Everything the bridge does to a world is fifteen operations wide, so a world that keeps a
/// dictionary of actors, a list of batches and a simulated sun exercises the whole driving path --
/// the check of which world is loaded, the pool, the batch, the render set named to the server, the
/// read-back, the tick, the settings restoration and the sun's binding and audit -- with no server,
/// no engine and no render. What it
/// cannot establish is what a body looks like once the pose is applied, which is the one thing only
/// a live run can answer.</para>
///
/// <para>It answers the read-back with exactly what was commanded, plus whatever
/// <see cref="TransformDrift"/> is set to. A drift of zero is a world that does what it is told and
/// proves the comparison is wired; a drift of something is a world that does not, and proves the
/// comparison would notice.</para>
///
/// <para>Velocity is answered as a server built with the kinematic-velocity change answers it. A body
/// is spawned simulating; switching its physics either way zeroes its velocity; a target velocity
/// written while its physics is off is what it reports until the next one; and one written while its
/// physics is on goes to the simulating body, which this world does not model and counts instead.
/// <see cref="ReportsNoKinematicVelocity"/> makes it a server built before that change.</para>
/// </remarks>
internal class RecordedWorld : ICarlaWorld
{
    private static readonly Vector3D Still = new(0f, 0f, 0f);

    private readonly Dictionary<ActorId, Transform> _actors = [];
    private readonly Dictionary<ActorId, Vector3D> _velocities = [];
    private readonly HashSet<ActorId> _simulating = [];
    private readonly Dictionary<ActorId, VehicleLightStateFlags> _lamps = [];
    private readonly List<IReadOnlyList<Command>> _batches = [];
    private readonly List<long> _batchTicks = [];
    private readonly List<(string Layer, bool Visible, long AtTick)> _layerWrites = [];
    private readonly List<(string Call, long AtTick)> _solarWrites = [];
    private readonly Dictionary<ActorId, (string VehicleId, string VehicleTypeId, ulong AdmittedFrame)> _namedLent = [];
    private readonly HashSet<ActorId> _namedParked = [];
    private readonly Dictionary<ulong, PublishedRenderSet> _published = [];
    private readonly List<(IReadOnlyList<LentBody> Lent, IReadOnlyList<ActorId> Parked, long AtTick)> _renderSetWrites = [];
    private ActorId _nextActor = 1;
    private (string Operation, Exception Failure)? _severAt;
    private Exception? _severedWith;

    /// <summary>
    /// What the world answers when asked which world it has loaded. A stock map by default: an
    /// origin at zero, no road network and no bare-earth record, which a session refuses. A test
    /// driving a package sets this to <see cref="SyntheticWorld.AsLoaded"/> of that package.
    /// </summary>
    public LoadedWorld Loaded { get; set; } = new(0.0, 0.0, 0.0, string.Empty, null);

    /// <summary>How many times the world was asked which world it has loaded.</summary>
    public int Descriptions { get; private set; }

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

    /// <summary>
    /// Whether the connection has dropped, so every call throws the failure it dropped with.
    /// </summary>
    public bool Severed => _severedWith is not null;

    /// <summary>How many calls were made after the connection dropped, each of which threw.</summary>
    public int CallsAfterSevering { get; private set; }

    /// <summary>
    /// Drop the connection at the next call to <paramref name="operation"/>: that call and every call
    /// after it, of any operation, throws <paramref name="failure"/>, as a client whose server has gone
    /// does -- the write that finds the socket closed, and every one after it.
    /// </summary>
    /// <param name="operation">An operation's name, as <see cref="ICarlaWorld"/> spells it.</param>
    /// <param name="failure">What each call throws.</param>
    public void SeverAt(string operation, Exception failure) => _severAt = (operation, failure);

    /// <summary>Drop the connection now.</summary>
    public void Sever(Exception failure) => _severedWith = failure;

    /// <summary>Every layer visibility written, with the tick the world was on when it arrived.</summary>
    public IReadOnlyList<(string Layer, bool Visible, long AtTick)> LayerWrites => _layerWrites;

    /// <summary>
    /// The world's sun, or <see langword="null"/> for a world that has none -- stock content, or a
    /// generated world whose georeference was never configured.
    /// </summary>
    public SimulatedSun? Sun { get; set; } = new();

    /// <summary>
    /// Whether the world-observer snapshot carries the refraction-corrected elevation, as a server
    /// whose header was widened to hold it does. False is a server built before that.
    /// </summary>
    public bool ObserverCarriesCorrectedElevation { get; set; } = true;

    /// <summary>Set to have the world publish no sun with its snapshots, as a sunless world does.</summary>
    public bool ObserverPublishesNoSun { get; set; }

    /// <summary>Every write to the sun, by call, with the tick the world was on when it arrived.</summary>
    public IReadOnlyList<(string Call, long AtTick)> SolarWrites => _solarWrites;

    /// <summary>
    /// The lamps an actor holds, as the server holds them on the actor: off when spawned, and whatever
    /// was last written to it after that, whoever it was written for.
    /// </summary>
    public VehicleLightStateFlags LampsOf(ActorId actor) => _lamps.GetValueOrDefault(actor);

    /// <summary>Batches carrying at least one transform, which is what a driven tick writes.</summary>
    public IEnumerable<IReadOnlyList<Command>> PoseBatches =>
        _batches.Where(batch => batch.Any(command => command is ApplyTransformCommand));

    /// <summary>
    /// Batches carrying a transform or a velocity, each with the tick the world was on when it
    /// arrived -- which is the session's index of the tick it was written for.
    /// </summary>
    public IEnumerable<(IReadOnlyList<Command> Batch, long AtTick)> DrivenBatches =>
        _batches.Zip(_batchTicks)
            .Where(entry => entry.First.Any(command => command is ApplyTransformCommand
                                                       or ApplyTargetVelocityCommand));

    /// <summary>
    /// Set to have a vehicle whose physics is disabled report zero velocity whatever it is given, as
    /// a server built before the kinematic-velocity change does. To the observer that is exactly a
    /// bridge that sends no velocity.
    /// </summary>
    public bool ReportsNoKinematicVelocity { get; set; }

    /// <summary>
    /// Target velocities written to a body whose physics was still on, which reach the simulating
    /// body rather than the field a kinematic vehicle is read from.
    /// </summary>
    public int VelocityWritesWhileSimulating { get; private set; }

    /// <summary>
    /// Set to have the world record a settings write and not act on it, as a server that refuses
    /// one does.
    /// </summary>
    public bool IgnoresSettingsWrites { get; set; }

    /// <summary>
    /// Set to have the world refuse every change to the render set with this message, as a server
    /// built before it carried a render set does: it has no such call.
    /// </summary>
    public string? RefusesRenderSet { get; set; }

    /// <summary>
    /// Every change to the render set named to the world, with the tick the world was on when it
    /// arrived -- which is the session's index of the tick it was named for.
    /// </summary>
    public IReadOnlyList<(IReadOnlyList<LentBody> Lent, IReadOnlyList<ActorId> Parked, long AtTick)> RenderSetWrites =>
        _renderSetWrites;

    /// <summary>
    /// The render set the world-observer snapshot of a frame carried, as the server publishes it:
    /// every body named lent, with its vehicle, its type and the frame its span began on, and every
    /// body named parked, as the naming stood when the frame was produced. Null for a frame the world
    /// did not produce, or one produced before any body was named.
    /// </summary>
    public PublishedRenderSet? PublishedRenderSetOf(ulong frame) => _published.GetValueOrDefault(frame);

    /// <summary>Bodies the world holds named lent or parked right now.</summary>
    public int NamedBodies => _namedLent.Count + _namedParked.Count;

    /// <inheritdoc/>
    public LoadedWorld DescribeLoadedWorld()
    {
        Connected(nameof(DescribeLoadedWorld));
        Descriptions++;
        return Loaded;
    }

    /// <summary>
    /// The packages the world's truth telemetry was asked to take its bare-earth grids from, in order,
    /// each with the number of descriptions the world had given when it was asked.
    /// </summary>
    public List<(string PackagePath, int AfterDescriptions)> Adoptions { get; } = [];

    /// <inheritdoc/>
    /// <remarks>Takes them whenever asked: what a world does with them is the client's to show.</remarks>
    public bool AdoptBareEarthGrids(string packagePath)
    {
        Connected(nameof(AdoptBareEarthGrids));
        Adoptions.Add((packagePath, Descriptions));
        return true;
    }

    /// <inheritdoc/>
    public EpisodeSettings ReadSettings()
    {
        Connected(nameof(ReadSettings));
        return Settings;
    }

    /// <inheritdoc/>
    public void WriteSettings(EpisodeSettings settings)
    {
        Connected(nameof(WriteSettings));
        SettingsWrites.Add(settings);
        if (!IgnoresSettingsWrites)
        {
            Settings = settings;
        }
    }

    /// <inheritdoc/>
    public ActorId Spawn(string blueprintId, Transform at)
    {
        Connected(nameof(Spawn));
        Spawned.Add(blueprintId);
        ActorId actor = _nextActor++;
        _actors[actor] = at;

        // A vehicle is spawned simulating, as the server spawns one.
        _simulating.Add(actor);
        _velocities[actor] = Still;
        _lamps[actor] = VehicleLightStateFlags.None;
        return actor;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Applied in order, as the server visits a batch, so where one batch writes an actor twice the
    /// later write is what the actor holds.
    /// </remarks>
    public IReadOnlyList<CommandResponse> ApplyBatch(IReadOnlyList<Command> commands)
    {
        Connected(nameof(ApplyBatch));
        // A copy, because the session reuses its batch from one tick to the next.
        _batches.Add([.. commands]);
        _batchTicks.Add(Ticks);
        var responses = new List<CommandResponse>(commands.Count);
        foreach (Command command in commands)
        {
            switch (command)
            {
                case ApplyTransformCommand transform:
                    _actors[transform.Actor] = transform.Transform;
                    responses.Add(CommandResponse.Success(transform.Actor));
                    break;
                case ApplyTargetVelocityCommand velocity:
                    if (_simulating.Contains(velocity.Actor))
                    {
                        VelocityWritesWhileSimulating++;
                    }
                    else if (!ReportsNoKinematicVelocity)
                    {
                        _velocities[velocity.Actor] = velocity.Velocity;
                    }

                    responses.Add(CommandResponse.Success(velocity.Actor));
                    break;
                case SetSimulatePhysicsCommand physics:
                    if (physics.Enabled)
                    {
                        _simulating.Add(physics.Actor);
                    }
                    else
                    {
                        _simulating.Remove(physics.Actor);
                    }

                    _velocities[physics.Actor] = Still;
                    responses.Add(CommandResponse.Success(physics.Actor));
                    break;
                case SetVehicleLightStateCommand lamps:
                    _lamps[lamps.Actor] = lamps.LightState;
                    responses.Add(CommandResponse.Success(lamps.Actor));
                    break;
                case DestroyActorCommand destroy:
                    _actors.Remove(destroy.Actor);
                    _lamps.Remove(destroy.Actor);
                    _velocities.Remove(destroy.Actor);
                    _simulating.Remove(destroy.Actor);
                    // The naming is held on the actor's own record, so it ends with the actor.
                    _namedLent.Remove(destroy.Actor);
                    _namedParked.Remove(destroy.Actor);
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
    /// <remarks>
    /// Named as the server names them: on the world's record of each actor, so a body the world does
    /// not have is not found and a destroyed body loses its naming; bodies given back before bodies
    /// lent; a body still lent to the same vehicle keeps the frame its span began on, and one lent anew
    /// begins its span on the next frame the world produces.
    /// </remarks>
    public RenderSetWrite WriteRenderSet(IReadOnlyList<LentBody> lent, IReadOnlyList<ActorId> parked)
    {
        Connected(nameof(WriteRenderSet));
        // Copies, because the session reuses its lists from one change to the next.
        _renderSetWrites.Add(([.. lent], [.. parked], Ticks));
        if (RefusesRenderSet is { } refusal)
        {
            return new RenderSetWrite(0, refusal);
        }

        int found = 0;
        foreach (ActorId actor in parked)
        {
            if (!_actors.ContainsKey(actor))
            {
                continue;
            }

            _namedLent.Remove(actor);
            _namedParked.Add(actor);
            found++;
        }

        ulong next = (ulong)Ticks + 1;
        foreach (LentBody body in lent)
        {
            if (!_actors.ContainsKey(body.Actor))
            {
                continue;
            }

            ulong admitted = _namedLent.TryGetValue(body.Actor, out var held) && held.VehicleId == body.VehicleId
                ? held.AdmittedFrame
                : next;
            _namedParked.Remove(body.Actor);
            _namedLent[body.Actor] = (body.VehicleId, body.VehicleTypeId, admitted);
            found++;
        }

        return new RenderSetWrite(found, null);
    }

    /// <inheritdoc/>
    public Transform? ObservedTransform(ActorId actor)
    {
        Connected(nameof(ObservedTransform));
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
    public Vector3D? ObservedVelocity(ActorId actor)
    {
        Connected(nameof(ObservedVelocity));
        return _velocities.TryGetValue(actor, out Vector3D velocity) ? velocity : null;
    }

    /// <inheritdoc/>
    /// <remarks>The frame is the tick count, which is what a server's frame counter is to a session.</remarks>
    public ulong? Tick()
    {
        Connected(nameof(Tick));
        if (ThrowOnTick is { } failure)
        {
            throw failure;
        }

        Ticks++;
        if (ProducesFrames)
        {
            // The time-of-day controller is an actor, and actors tick before the world observer
            // publishes the frame, so the sun a frame is published with has already moved.
            Sun?.Tick(Settings.FixedDeltaSeconds ?? 0.0);

            // And the frame's snapshot carries every body named so far, as the naming stands now.
            if (NamedBodies > 0)
            {
                _published[(ulong)Ticks] = new PublishedRenderSet(
                    _namedLent.ToDictionary(pair => pair.Key, pair => pair.Value),
                    new HashSet<ActorId>(_namedParked));
            }
        }

        return ProducesFrames ? (ulong)Ticks : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The tick count at the moment of the write is kept with it, because when a layer was written
    /// is half of what the session promises about it: a layer written at tick zero was fixed before
    /// the first frame existed, and one written later changed the imagery mid-capture.
    /// </remarks>
    public virtual void WriteLayerVisible(string layer, bool visible)
    {
        Connected(nameof(WriteLayerVisible));
        _layerWrites.Add((layer, visible, Ticks));
    }

    /// <inheritdoc/>
    public IReadOnlyList<double> ReadSolarState()
    {
        Connected(nameof(ReadSolarState));
        return Sun?.Read() ?? [];
    }

    /// <inheritdoc/>
    public bool WriteSolarEpoch(int year, int month, int day, double hours, double utcOffsetHours)
    {
        Connected(nameof(WriteSolarEpoch));
        _solarWrites.Add(("set_solar_epoch", Ticks));
        return Sun?.WriteEpoch(year, month, day, hours, utcOffsetHours) ?? false;
    }

    /// <inheritdoc/>
    public bool WriteTimeAdvance(bool advancing, double rate)
    {
        Connected(nameof(WriteTimeAdvance));
        _solarWrites.Add(("set_time_advance", Ticks));
        return Sun?.WriteAdvance(advancing, rate) ?? false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<double> ObservedSolarState()
    {
        Connected(nameof(ObservedSolarState));
        if (Sun is null || ObserverPublishesNoSun)
        {
            return [];
        }

        IReadOnlyList<double> block = Sun.Read();
        return ObserverCarriesCorrectedElevation ? block : block.Take(SolarReading.RequiredValues).ToArray();
    }

    /// <summary>
    /// Throw the failure the connection dropped with, where it has dropped or drops at this call.
    /// </summary>
    private void Connected(string operation)
    {
        if (_severedWith is null && _severAt is { } planned && planned.Operation == operation)
        {
            _severedWith = planned.Failure;
        }

        if (_severedWith is { } failure)
        {
            CallsAfterSevering++;
            throw failure;
        }
    }
}

/// <summary>
/// The render set one frame's world-observer snapshot carried, as <see cref="RecordedWorld"/>
/// publishes it.
/// </summary>
/// <param name="Lent">Every body named lent: the vehicle it was drawn for, its type, and the frame its span began on.</param>
/// <param name="Parked">Every body named parked.</param>
internal sealed record PublishedRenderSet(
    IReadOnlyDictionary<ActorId, (string VehicleId, string VehicleTypeId, ulong AdmittedFrame)> Lent,
    IReadOnlySet<ActorId> Parked);
