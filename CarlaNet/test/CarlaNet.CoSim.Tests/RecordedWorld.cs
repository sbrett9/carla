using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Supervision;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A CARLA world that records what was asked of it and answers as a server would.
/// </summary>
/// <remarks>
/// <para>Everything the bridge does to a world is twenty-one operations wide, so a world that keeps a
/// dictionary of actors, a list of batches and a simulated sun exercises the whole driving path --
/// the check of which world is loaded, the pool, the batch, the render set named to the server, the
/// supervision put to it, the draw distance set on the bodies, the read-back, the tick, the settings
/// restoration, the sun's
/// binding and audit, and the cameras an optional render set follows -- with no server,
/// no engine and no render. What it
/// cannot establish is what a body looks like once the pose is applied, which is the one thing only
/// a live run can answer.</para>
///
/// <para>A camera is placed and moved by the test (<see cref="PlaceCamera"/>, <see cref="MoveCamera"/>),
/// as a script spawns and flies one, and its pose is answered for any frame as it stands now.</para>
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
    private readonly Dictionary<ActorId, SupervisionInForce> _supervisionHeld = [];
    private readonly List<AbsenceInForce> _absencesHeld = [];
    private readonly Dictionary<ulong, PublishedSupervision> _publishedSupervision = [];
    private readonly List<(SupervisionChange Change, long AtTick)> _supervisionWrites = [];
    private SupervisionPlanIdentity? _planHeld;
    private readonly List<(IReadOnlyList<ActorId> Bodies, double Metres, long AtTick)> _drawDistanceWrites = [];
    private readonly Dictionary<ActorId, double> _drawDistances = [];
    private readonly Dictionary<ActorId, CameraOptics> _cameras = [];
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

    /// <summary>
    /// Who holds the world's drive lease, as the server holds it on the episode: null while nobody
    /// does. Set before a session starts to stand for another client's drive holding the world.
    /// </summary>
    public string? DriveLeaseHolder { get; set; }

    /// <summary>
    /// Set to have the world refuse every claim on the drive lease with this message and name no
    /// holder, as a server built before it carried the lease does: it has no such call.
    /// </summary>
    public string? RefusesDriveLease { get; set; }

    /// <summary>Every claim on the drive lease, by the name it was made under, in order.</summary>
    public List<string> DriveLeaseClaims { get; } = [];

    /// <summary>Every release of the drive lease, by the name it was made under, in order.</summary>
    public List<string> DriveLeaseReleases { get; } = [];

    /// <summary>Every batch, in the order it was applied.</summary>
    public IReadOnlyList<IReadOnlyList<Command>> Batches => _batches;

    /// <summary>Blueprints spawned, in order, one entry per body.</summary>
    public List<string> Spawned { get; } = [];

    /// <summary>The role name each body was spawned under, beside <see cref="Spawned"/>.</summary>
    public List<string> SpawnedRoleNames { get; } = [];

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

    /// <summary>
    /// Set to have the world refuse every draw distance with this message, as a server built before
    /// it carried the call does.
    /// </summary>
    public string? RefusesDrawDistance { get; set; }

    /// <summary>
    /// Every draw distance set, with the bodies it was sent for and the tick the world was on when it
    /// arrived -- the session's index of the tick it was set before.
    /// </summary>
    public IReadOnlyList<(IReadOnlyList<ActorId> Bodies, double Metres, long AtTick)> DrawDistanceWrites =>
        _drawDistanceWrites;

    /// <summary>
    /// The draw distance an actor carries, metres, as the server holds it on the actor's components;
    /// null for one never set, or set to zero, which is drawn at any range.
    /// </summary>
    public double? DrawDistanceOf(ActorId actor) =>
        _drawDistances.TryGetValue(actor, out double metres) && metres > 0.0 ? metres : null;

    /// <summary>Bodies the world holds named lent or parked right now.</summary>
    public int NamedBodies => _namedLent.Count + _namedParked.Count;

    /// <summary>
    /// Set to have the world refuse every change to the supervision with this message, as a server built
    /// before it carried supervision does: it has no such call.
    /// </summary>
    public string? RefusesSupervision { get; set; }

    /// <summary>
    /// Every change to the supervision put to the world, copied as it arrived, with the tick the world was
    /// on -- the session's index of the tick it was put for.
    /// </summary>
    public IReadOnlyList<(SupervisionChange Change, long AtTick)> SupervisionWrites => _supervisionWrites;

    /// <summary>
    /// The supervision the world-observer snapshot of a frame carried, as the server publishes it: the
    /// plan, every lent body whose vehicle is annotated or nominal, and the absences, as they stood when
    /// the frame was produced. Null for a frame the world did not produce, or one produced while no plan
    /// was held.
    /// </summary>
    public PublishedSupervision? PublishedSupervisionOf(ulong frame) => _publishedSupervision.GetValueOrDefault(frame);

    /// <summary>The plan the world holds supervision under right now, or null where it holds none.</summary>
    public SupervisionPlanIdentity? SupervisionPlanHeld => _planHeld;

    /// <summary>Bodies the world holds a supervision row for right now.</summary>
    public int SupervisedBodies => _supervisionHeld.Count;

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

    /// <summary>
    /// The vehicle kinds the world's truth telemetry was given, in order, each as a copy of the table
    /// handed over, with the number of descriptions the world had given when it was handed over.
    /// </summary>
    public List<(IReadOnlyDictionary<string, string> SpecialTypes, int AfterDescriptions)> SpecialTypeAdoptions { get; } = [];

    /// <inheritdoc/>
    public void AdoptCatalogueSpecialTypes(IReadOnlyDictionary<string, string> specialTypes)
    {
        Connected(nameof(AdoptCatalogueSpecialTypes));
        SpecialTypeAdoptions.Add((new Dictionary<string, string>(specialTypes), Descriptions));
    }

    /// <summary>
    /// The base types the world's truth telemetry was given, in order, each as a copy of the table
    /// handed over, with the number of descriptions the world had given when it was handed over.
    /// </summary>
    public List<(IReadOnlyDictionary<string, string> BaseTypes, int AfterDescriptions)> BaseTypeAdoptions { get; } = [];

    /// <inheritdoc/>
    public void AdoptCatalogueBaseTypes(IReadOnlyDictionary<string, string> baseTypes)
    {
        Connected(nameof(AdoptCatalogueBaseTypes));
        BaseTypeAdoptions.Add((new Dictionary<string, string>(baseTypes), Descriptions));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Granted as the server grants it: to the first claim while nobody holds it, and refused naming
    /// the holder while anybody does, the same name included.
    /// </remarks>
    public DriveLeaseWrite TakeDriveLease(string holder)
    {
        Connected(nameof(TakeDriveLease));
        DriveLeaseClaims.Add(holder);
        if (RefusesDriveLease is { } refusal)
        {
            return new DriveLeaseWrite(null, refusal);
        }

        if (DriveLeaseHolder is { } held)
        {
            return new DriveLeaseWrite(
                held, $"take_drive_lease: refused; {held} holds the drive lease on this world");
        }

        DriveLeaseHolder = holder;
        return new DriveLeaseWrite(null, null);
    }

    /// <inheritdoc/>
    /// <remarks>Released as the server releases it: by its holder, and by nobody else.</remarks>
    public string? ReleaseDriveLease(string holder)
    {
        Connected(nameof(ReleaseDriveLease));
        DriveLeaseReleases.Add(holder);
        if (RefusesDriveLease is { } refusal)
        {
            return refusal;
        }

        if (DriveLeaseHolder is null)
        {
            return "release_drive_lease: no drive lease is held on this world";
        }

        if (DriveLeaseHolder != holder)
        {
            return $"release_drive_lease: refused; the drive lease is held by {DriveLeaseHolder}, not by {holder}";
        }

        DriveLeaseHolder = null;
        return null;
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
    public ActorId Spawn(string blueprintId, Transform at, string roleName)
    {
        Connected(nameof(Spawn));
        Spawned.Add(blueprintId);
        SpawnedRoleNames.Add(roleName);
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
                    // The naming is held on the actor's own record, so it ends with the actor, and so is
                    // its supervision.
                    _namedLent.Remove(destroy.Actor);
                    _namedParked.Remove(destroy.Actor);
                    _supervisionHeld.Remove(destroy.Actor);
                    _drawDistances.Remove(destroy.Actor);
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
            // A body given back loses the supervision of the vehicle it drew.
            _supervisionHeld.Remove(actor);
            found++;
        }

        ulong next = (ulong)Ticks + 1;
        foreach (LentBody body in lent)
        {
            if (!_actors.ContainsKey(body.Actor))
            {
                continue;
            }

            bool sameVehicle = _namedLent.TryGetValue(body.Actor, out var held) && held.VehicleId == body.VehicleId;
            ulong admitted = sameVehicle ? held.AdmittedFrame : next;
            if (!sameVehicle)
            {
                // Handed to another vehicle: what was asserted of the last one is not about this one.
                _supervisionHeld.Remove(body.Actor);
            }

            _namedParked.Remove(body.Actor);
            _namedLent[body.Actor] = (body.VehicleId, body.VehicleTypeId, admitted);
            found++;
        }

        return new RenderSetWrite(found, null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Held as the server holds it: under one plan, which a change naming another must start afresh;
    /// each body's supervision on its own record and only while the body is named lent, so a body the
    /// world does not have, or holds parked, is not given one; absences for the world, opened and closed
    /// by instance; and a change naming no plan withdrawing everything. A change the server would refuse
    /// is refused here too, with its words.
    /// </remarks>
    public SupervisionWrite WriteSupervision(SupervisionChange change)
    {
        Connected(nameof(WriteSupervision));
        // A copy, because the session reuses its lists from one change to the next.
        _supervisionWrites.Add((change with
        {
            Bodies = [.. change.Bodies],
            AbsencesOpened = [.. change.AbsencesOpened],
            AbsencesClosed = [.. change.AbsencesClosed],
        }, Ticks));
        if (RefusesSupervision is { } refusal)
        {
            return new SupervisionWrite(0, refusal);
        }

        if (change.Bodies.Select(body => body.Supervision.Problem()).FirstOrDefault(problem => problem is not null)
            is { } problem)
        {
            return new SupervisionWrite(0, $"update_supervision: {problem}");
        }

        if (change.Plan is not { } plan)
        {
            if (change.Bodies.Count > 0 || change.AbsencesOpened.Count > 0 || change.AbsencesClosed.Count > 0)
            {
                return new SupervisionWrite(
                    0, "update_supervision: a change naming no plan withdraws all supervision and carries nothing else");
            }

            _planHeld = null;
            _supervisionHeld.Clear();
            _absencesHeld.Clear();
            return new SupervisionWrite(0, null);
        }

        if (_planHeld is not null && !_planHeld.Equals(plan) && !change.Fresh)
        {
            return new SupervisionWrite(
                0, $"update_supervision: plan {_planHeld.PlanId} is in force, and a change naming plan {plan.PlanId} starts afresh");
        }

        if (change.Fresh || _planHeld is null)
        {
            _supervisionHeld.Clear();
            _absencesHeld.Clear();
        }

        _planHeld = plan;
        int applied = 0;
        foreach (BodySupervision body in change.Bodies)
        {
            if (!_actors.ContainsKey(body.Actor) || !_namedLent.ContainsKey(body.Actor))
            {
                continue;
            }

            if (body.Supervision.State == SupervisionState.Unlabelled)
            {
                _supervisionHeld.Remove(body.Actor);
            }
            else
            {
                _supervisionHeld[body.Actor] = body.Supervision;
            }

            applied++;
        }

        foreach (string closed in change.AbsencesClosed)
        {
            _absencesHeld.RemoveAll(absence => absence.InstanceId == closed);
        }

        foreach (AbsenceInForce opened in change.AbsencesOpened)
        {
            int held = _absencesHeld.FindIndex(absence => absence.InstanceId == opened.InstanceId);
            if (held < 0)
            {
                _absencesHeld.Add(opened);
            }
            else
            {
                _absencesHeld[held] = opened;
            }
        }

        return new SupervisionWrite(applied, null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Held as the server holds it, on each actor it finds: a body the world does not have is not
    /// found, and a destroyed body loses it.
    /// </remarks>
    public DrawDistanceWrite WriteDrawDistance(IReadOnlyList<ActorId> bodies, double metres)
    {
        Connected(nameof(WriteDrawDistance));
        // A copy, because the session reuses its list from one write to the next.
        _drawDistanceWrites.Add(([.. bodies], metres, Ticks));
        if (RefusesDrawDistance is { } refusal)
        {
            return new DrawDistanceWrite(0, refusal);
        }

        int found = 0;
        foreach (ActorId actor in bodies)
        {
            if (_actors.ContainsKey(actor))
            {
                _drawDistances[actor] = metres;
                found++;
            }
        }

        return new DrawDistanceWrite(found, null);
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

    /// <summary>The frames a camera pose was asked for, in order.</summary>
    public List<ulong> PoseFramesAsked { get; } = [];

    /// <summary>How many times a camera's attributes were asked for.</summary>
    public int CameraDescriptions { get; private set; }

    /// <summary>Spawn a camera with the attributes a camera blueprint gives it, as a script does.</summary>
    public ActorId PlaceCamera(Transform at, int width, int height, double fovDegrees)
    {
        ActorId actor = _nextActor++;
        _actors[actor] = at;
        _cameras[actor] = new CameraOptics(width, height, fovDegrees);
        return actor;
    }

    /// <summary>Move a camera, as a flight controller or an orbit does between ticks.</summary>
    public void MoveCamera(ActorId camera, Transform to) => _actors[camera] = to;

    /// <summary>Take a camera out of the world, as a script tearing its rig down does.</summary>
    public void DestroyCamera(ActorId camera)
    {
        _actors.Remove(camera);
        _cameras.Remove(camera);
    }

    /// <inheritdoc/>
    /// <remarks>Answers where the actor stands now, which on a synchronous world is the last frame.</remarks>
    public Transform? ObservedTransformAt(ActorId actor, ulong frame)
    {
        Connected(nameof(ObservedTransformAt));
        PoseFramesAsked.Add(frame);
        return _actors.TryGetValue(actor, out Transform held) ? held : null;
    }

    /// <inheritdoc/>
    public CameraOptics? DescribeCamera(ActorId camera)
    {
        Connected(nameof(DescribeCamera));
        CameraDescriptions++;
        return _cameras.TryGetValue(camera, out CameraOptics optics) ? optics : null;
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

            // And the supervision held, while a plan is: only lent bodies carry a row.
            if (_planHeld is { } plan)
            {
                _publishedSupervision[(ulong)Ticks] = new PublishedSupervision(
                    plan,
                    _supervisionHeld.Where(pair => _namedLent.ContainsKey(pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value),
                    [.. _absencesHeld]);
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

/// <summary>
/// The supervision one frame's world-observer snapshot carried, as <see cref="RecordedWorld"/>
/// publishes it.
/// </summary>
/// <param name="Plan">The plan held.</param>
/// <param name="ByActor">Every lent body whose vehicle was annotated or nominal, with its supervision.</param>
/// <param name="Absences">The absences in force, in the order they opened.</param>
internal sealed record PublishedSupervision(
    SupervisionPlanIdentity Plan,
    IReadOnlyDictionary<ActorId, SupervisionInForce> ByActor,
    IReadOnlyList<AbsenceInForce> Absences);
