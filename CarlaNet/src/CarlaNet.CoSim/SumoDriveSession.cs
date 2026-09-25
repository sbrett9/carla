using System.Diagnostics;
using CarlaNet.Map.WorldPackage;
using CarlaNet.Sumo;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// The playback bridge: one session that owns the advance of simulated time on both sides, computes
/// every vehicle's CARLA pose from SUMO's state, and records what it computed.
/// </summary>
/// <remarks>
/// <para><b>Why a session that does nothing is worth running.</b> The conversion from a SUMO state
/// to a CARLA transform has four independent ways of being wrong -- the frame, the yaw, the
/// reference point and the seating -- and every one of them produces imagery that looks ordinary.
/// A vehicle placed half a car length forward is a rendered scene full of plausible cars in
/// plausible places, with every truth box in the wrong place, and no amount of looking at the
/// imagery finds it. Computed and not applied, the same error is a residual in a report.</para>
///
/// <para>Everything except the application of a pose runs: the clock's validation, the frame
/// identity check, the population lease, the two-tier subscription, the render set, the one-step
/// lookahead, the lane interpolation and the pose conversion. The world is ticked, by whatever the
/// caller supplied, so the session owns the advance of simulated time on both sides exactly as it
/// will when it drives.</para>
/// </remarks>
public sealed class SumoDriveSession : IDisposable
{
    private readonly SumoDriveSessionOptions _options;
    private readonly SumoConnection _sumo;
    private readonly SubscribedPopulation _population;
    private readonly RenderSetManager _renderSet;
    private readonly VehicleTypeBinder _binder;
    private readonly PoseConverter _converter;
    private readonly LaneArcInterpolator _interpolator;
    private readonly SumoRoadNetwork _network;
    private readonly PopulationLease _lease;
    private readonly WorldSettingsLease? _settings;
    private readonly LayerVisibilityLease? _layers;
    private readonly VehicleBodyPool? _pool;
    private readonly Func<bool> _tickWorld;
    private readonly List<Command> _batch = [];
    private readonly List<Command> _parked = [];
    private readonly List<(string VehicleId, ActorId Actor, VehiclePose Pose)> _commanded = [];
    private readonly Dictionary<string, (double X, double Y)> _positions = [];
    private readonly Dictionary<string, CoSimVehicleFrame> _previous = [];
    private readonly Dictionary<string, CoSimVehicleFrame> _next = [];
    private readonly Stopwatch _bridgeClock = new();
    private readonly Stopwatch _sumoClock = new();

    private SolarLease? _sun;
    private long _tickIndex;
    private bool _disposed;

    private SumoDriveSession(SumoDriveSessionOptions options,
                             SumoConnection sumo,
                             CoSimClock clock,
                             SumoRoadNetwork network,
                             GroundSurface ground,
                             VehicleCatalogue catalogue,
                             PopulationLease lease,
                             WorldSettingsLease? settings,
                             LayerVisibilityLease? layers,
                             VehicleBodyPool? pool)
    {
        _options = options;
        _sumo = sumo;
        _network = network;
        _lease = lease;
        _settings = settings;
        _layers = layers;
        _pool = pool;
        _tickWorld = options.World is { } world ? world.Tick : options.TickWorld ?? (() => true);
        _population = new SubscribedPopulation(sumo.TraCI);
        _renderSet = new RenderSetManager(options.RenderSet, Release);
        _binder = new VehicleTypeBinder(sumo.TraCI, catalogue);
        _converter = new PoseConverter(ground, options.MeasuredSeatHeights);
        _interpolator = new LaneArcInterpolator(network);

        Clock = clock;
        Report = new CoSimRunReport
        {
            Clock = clock,
            ScenarioPath = options.ScenarioPath,
            WorldPackagePath = options.WorldPackagePath,
            CatalogueDigest = catalogue.CatalogueDigest,
            SumoStepOverrideSeconds = options.SumoStepOverrideSeconds,
            LayerVisibility = layers?.Applied ?? new Dictionary<string, bool>(),
        };
    }

    /// <summary>The three rates the session resolved and validated.</summary>
    public CoSimClock Clock { get; }

    /// <summary>What the run has established so far.</summary>
    public CoSimRunReport Report { get; }

    /// <summary>The simulated instant the last world tick rendered.</summary>
    public double RenderedTimeSeconds { get; private set; }

    /// <summary>The vehicles that would hold a rendered actor right now.</summary>
    public IReadOnlyCollection<string> RenderedVehicleIds => _renderSet.RenderedVehicleIds;

    /// <summary>
    /// The session's hold on the world's sun: what it was bound to, what the world reported back,
    /// and what it was found holding. Null where the session binds no sun.
    /// </summary>
    public SolarLease? Sun => _sun;

    /// <summary>
    /// Start a session: validate the clock, check the network is the world's, take the population
    /// lease, and buffer the one SUMO step of lookahead every sub-step pose is interpolated inside.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The clock does not divide, the world is asynchronous, the network is not the one the world
    /// was built from, or something else already holds the world's population.
    /// </exception>
    public static SumoDriveSession Start(SumoDriveSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        WorldPackageManifest manifest = WorldPackage.ReadManifest(options.WorldPackagePath);
        GroundSurface ground = GroundSurface.FromWorldPackage(options.WorldPackagePath);
        SumoRoadNetwork network = SumoRoadNetwork.FromWorldPackage(options.WorldPackagePath);
        VehicleCatalogue catalogue = VehicleCatalogue.Load(options.CataloguePath);

        List<string> extraArguments = [];
        if (options.SumoStepOverrideSeconds is { } forced)
        {
            extraArguments.Add("--step-length");
            extraArguments.Add(forced.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        SumoConnection sumo = SumoConnection.Start(
            SumoInstallation.LocateOrThrow(),
            options.ScenarioPath,
            new SumoLaunchOptions
            {
                ExtraArguments = extraArguments,
                Output = options.SumoOutput ?? (_ => { }),
            });

        WorldSettingsLease? settings = null;
        LayerVisibilityLease? layers = null;
        try
        {
            RequireOneWayToAdvanceTheWorld(options);

            // Take the world's clock before anything else is checked against it: the settings the
            // session validates its own against have to be the ones the world is holding, not the
            // ones the caller asked for.
            settings = options.World is { } claimed
                ? WorldSettingsLease.Take(claimed, options.WorldDeltaSeconds)
                : null;

            // What is in frame, decided once and before anything is rendered. Both layers are a
            // property of the corpus rather than of whoever launched the run, so the session writes
            // them rather than trusting a launcher to: the generated road surface is a flat ribbon
            // drawn over the photogrammetry of the real road, and the generated signals are meshes
            // frequently misaligned against it. Hiding either is rendering-only -- the road keeps
            // its collision and a hidden signal keeps its stop-line trigger -- so nothing here
            // removes a surface to drive on. Nothing in this mode would notice if it did: a
            // SUMO-driven body is teleported with its physics off.
            layers = options.World is { } rendered
                ? LayerVisibilityLease.Take(rendered, new Dictionary<string, bool>
                {
                    [LayerVisibilityLease.RoadLayer] = options.RoadLayerVisible,
                    [LayerVisibilityLease.SignalLayer] = options.SignalLayerVisible,
                })
                : null;

            CoSimClock clock = CoSimClock.ForSession(
                sumo.StepLength,
                settings is { } held ? held.FixedDeltaSeconds : options.WorldDeltaSeconds,
                options.CaptureRateHz,
                settings is { } asked ? asked.Applied.SynchronousMode : options.WorldIsSynchronous);
            RequireTheWorldSNetwork(manifest, network, options);

            PopulationLease lease = WorldDriveAuthority.ForWorld(options.WorldKey)
                .Acquire(PopulationMode.SumoDrivenPlayback, options.Holder);

            VehicleBodyPool? pool = options.World is { } world
                ? new VehicleBodyPool(world, VehicleParking.BeyondTheSurface(ground),
                                      options.MaximumBodies)
                : null;

            var session = new SumoDriveSession(options, sumo, clock, network, ground, catalogue,
                                               lease, settings, layers, pool);
            try
            {
                session.Prime();
                session.BindTheSun();
                return session;
            }
            catch
            {
                session._sun?.Dispose();
                pool?.DestroyAll();
                lease.Dispose();
                throw;
            }
        }
        catch
        {
            // Everything this method changed, given back, in the reverse order it was taken. A
            // session that failed to start must leave the world exactly as it found it: an operator
            // whose editor is stranded in synchronous mode is waiting on a tick from a process that
            // never started.
            layers?.Dispose();
            settings?.Dispose();
            sumo.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Advance one SUMO step, and the world by the ticks that step is worth, computing every pose
    /// on the way.
    /// </summary>
    /// <returns>
    /// False where SUMO has nothing left to simulate, which is how a run ends rather than by a
    /// count of steps.
    /// </returns>
    public bool Advance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int tick = 0; tick < Clock.WorldTicksPerSumoStep; tick++)
        {
            _bridgeClock.Start();
            double fraction = Clock.InterpolationFraction(tick);
            ComputePoses(fraction);
            WriteTheBatch();
            _bridgeClock.Stop();

            if (!_tickWorld())
            {
                throw new CoSimSessionRefusedException(
                    $"The CARLA world produced no frame for tick {_tickIndex}. A world that stops "
                    + "ticking while SUMO keeps stepping renders a timeline nothing simulated.");
            }

            MeasureDivergence();
            _tickIndex++;
            Report.Ticks++;
            RenderedTimeSeconds += Clock.WorldDeltaSeconds;
        }

        bool more = AdvanceSumo();
        Report.BridgeSecondsOnTicks = _bridgeClock.Elapsed.TotalSeconds;
        Report.SumoSecondsOnSteps = _sumoClock.Elapsed.TotalSeconds;
        return more;
    }

    /// <summary>
    /// Close the open intervals, give back every body, give back the lease, and end the simulation.
    /// </summary>
    /// <remarks>
    /// Every step runs whatever the ones before it did. A session is disposed on its failure paths
    /// as well as its happy one, and a failure that skipped the rest of the shutdown would leave the
    /// operator's world holding the wreckage of the run that failed -- which is exactly the state
    /// nobody is in a position to clean up, because whatever was driving it has just thrown. The
    /// rendering layers are global state of the same class as the world's clock: a run that hid the
    /// road mesh and threw must not leave an editor showing a world with no road network in it.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception> failures = [];
        Attempt(failures, () => _renderSet.CloseAll(RenderedTimeSeconds));
        Attempt(failures, () =>
        {
            Report.Admissions = _renderSet.Admissions;
            Report.CapacityDeclines = _renderSet.CapacityDeclines;
            foreach (UnrenderableReason reason in _binder.RefusedTypes.Values)
            {
                Report.CountRefusedType(reason);
            }

            Report.BridgeSecondsOnTicks = _bridgeClock.Elapsed.TotalSeconds;
            Report.SumoSecondsOnSteps = _sumoClock.Elapsed.TotalSeconds;
            if (_pool is { } counted)
            {
                Report.BodiesSpawned = counted.Bodies.Count;
                Report.BodyDeclines = counted.Exhaustions;
            }
        });
        Attempt(failures, () => _sun?.Dispose());
        Attempt(failures, () => _pool?.DestroyAll());
        Attempt(failures, () => _layers?.Dispose());
        Attempt(failures, () => _settings?.Dispose());
        Attempt(failures, _lease.Dispose);
        Attempt(failures, _sumo.Dispose);

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "The session did not shut down cleanly. Every step was attempted; these are the "
                + "ones that failed.", failures);
        }
    }

    /// <summary>
    /// Fast-forward SUMO to the window's start and buffer the first two frames, so every sub-step
    /// pose is interpolated between two known states rather than extrapolated from one.
    /// </summary>
    private void Prime()
    {
        while (_sumo.Time < _options.WarmUpToSimulatedSecond)
        {
            _sumo.Step();
        }

        _population.Seed(_sumo.Vehicles.Ids);
        ReconcileAndRead();
        RenderedTimeSeconds = _sumo.Time;
        AdvanceSumo();
    }

    /// <summary>
    /// Bind the world's sun to the civil instant of the first frame the session will render.
    /// </summary>
    /// <remarks>
    /// Here, and nowhere earlier: SUMO has been fast-forwarded, so the instant of the first rendered
    /// frame is known, and the world has not yet ticked, so no frame has been rendered under whatever
    /// sun it was holding. Nothing ticks the world during the fast-forward, so nothing could have
    /// moved the sun in between either.
    /// </remarks>
    private void BindTheSun()
    {
        if (_options.World is not { } world
            || _options.Illumination is not { BindsTheSun: true } policy
            || _options.Epoch is not { } epoch)
        {
            return;
        }

        _sun = SolarLease.Take(world, new DeclaredSun(epoch, policy, RenderedTimeSeconds));
    }

    private bool AdvanceSumo()
    {
        _sumoClock.Start();
        _sumo.Step();
        Report.SumoSteps++;
        int remaining = _sumo.Simulation.ExpectedVehicleCount;
        _sumoClock.Stop();

        CopyFrames(_next, _previous);
        ReconcileAndRead();
        MeasureLaneGeometry();
        return remaining > 0;
    }

    private void ReconcileAndRead()
    {
        _population.Reconcile(_sumo.Simulation.DepartedVehicleIds,
                              _sumo.Simulation.ArrivedVehicleIds);
        _population.ReadPositions(_positions);
        _renderSet.ReconcileSubscriptions(_population, _positions);
        _population.ReadFrames(_next);
        _renderSet.ReconcileRenderSet(_sumo.Time, _next);
        Report.Admissions = _renderSet.Admissions;
        Report.CapacityDeclines = _renderSet.CapacityDeclines;
    }

    private void ComputePoses(double fraction)
    {
        // Every body released since the last tick goes back to its slot, in this same batch. A
        // parking pose is a pose like any other, so it costs an entry rather than a round trip.
        _batch.Clear();
        _batch.AddRange(_parked);
        _parked.Clear();
        _commanded.Clear();

        foreach (string vehicleId in _renderSet.RenderedVehicleIds)
        {
            if (!_next.TryGetValue(vehicleId, out CoSimVehicleFrame to))
            {
                continue;
            }

            if (!_binder.TryBind(to.TypeId, out VehicleExtent extent))
            {
                Report.VehicleTicksWithNoMeasuredBody++;
                continue;
            }

            CoSimVehicleFrame from = _previous.TryGetValue(vehicleId, out CoSimVehicleFrame held)
                ? held
                : to;
            InterpolatedState state = _interpolator.Interpolate(from, to, fraction,
                                                                Clock.SumoStepSeconds);
            Report.CountCase(state.Case);
            if (state.Case == LaneInterpolationCase.Discontinuous)
            {
                Report.SampleDiscontinuity(from, to, _interpolator.RouteDistance(from, to));
            }

            VehiclePose? pose = _converter.Convert(vehicleId, extent, state.X, state.Y,
                                                   state.HeadingDegrees,
                                                   state.SpeedMetresPerSecond);
            if (pose is not { } applied)
            {
                Report.PosesRefusedForMissingGround++;
                continue;
            }

            Report.PosesComputed++;
            if (applied.SeatHeightWasApproximated)
            {
                Report.PosesOnAnApproximatedSeatHeight++;
            }

            Report.WorstBumperResidualMetres = Math.Max(
                Report.WorstBumperResidualMetres,
                BumperResidual(applied, extent, state.X, state.Y));

            ActorId actor = 0;
            if (_pool is { } pool)
            {
                if (pool.TryCheckOut(vehicleId, extent.BlueprintId, out PooledBody body))
                {
                    actor = body.Actor;
                    _batch.Add(new ApplyTransformCommand(actor, TransformOf(applied)));
                    _commanded.Add((vehicleId, actor, applied));
                }
                else
                {
                    Report.PoseDeclinesForNoBody++;
                }
            }

            _options.OnPose?.Invoke(new CoSimPoseRecord(
                _tickIndex, RenderedTimeSeconds, Clock.IsCaptureTick(_tickIndex), actor, applied,
                state.Case, state.X, state.Y, state.HeadingDegrees));
        }

        if (_pool is { } counted)
        {
            Report.BodiesSpawned = counted.Bodies.Count;
            Report.BodyDeclines = counted.Exhaustions;
        }
    }

    /// <summary>
    /// A vehicle has left the render set: take its body back and pass the interval on with the
    /// body named.
    /// </summary>
    /// <remarks>
    /// The render set decides who is rendered and knows nothing about which body renders them, so
    /// the two facts meet here and nowhere else. A consumer holding a track in the imagery has an
    /// actor id and needs the vehicle; only the pair of instants tells it which of the succession of
    /// vehicles that body carried was the one it is looking at.
    /// </remarks>
    private void Release(RenderedVehicleInterval interval)
    {
        ActorId actor = 0;
        if (_pool is { } pool && pool.TryCheckIn(interval.VehicleId, out PooledBody body))
        {
            actor = body.Actor;
            _parked.Add(new ApplyTransformCommand(actor, body.Parking));
        }

        _options.OnRelease?.Invoke(interval with { Actor = actor });
    }

    /// <summary>
    /// Write every pose this tick in one round trip.
    /// </summary>
    /// <remarks>
    /// <para><b>One batch, and no variable tail.</b> Every command CARLA's batch endpoint takes is
    /// supported at both ends, so N vehicles cost one round trip rather than N. The .NET traffic
    /// manager already writes its control frame this way, against the same endpoint.</para>
    ///
    /// <para><b>No target velocity beside the transform.</b> The runtime section's D3.5 has the
    /// bridge emit one, and it will once the engine change beside it lands. Today it would be a
    /// command per vehicle per tick that cannot do anything: on a body that is not simulating,
    /// <c>SetPhysicsLinearVelocity</c> writes a physics body that
    /// <c>UPrimitiveComponent::GetComponentVelocity</c> will not read, and disabling physics
    /// destroys that body in the first place. The engine classifies the call as invalid on a
    /// non-simulating body and logs it in every non-shipping build, so emitting it now buys a line
    /// of log per vehicle per tick and nothing else. SUMO's own speed is on the pose record either
    /// way, which is where the truth path reads it.</para>
    ///
    /// <para>A failed command is counted rather than thrown on. The batch's responses name the
    /// commands that failed, and a run in which some poses did not take is a run whose imagery is
    /// wrong in a way only the count makes visible.</para>
    /// </remarks>
    private void WriteTheBatch()
    {
        if (_options.World is not { } world || _batch.Count == 0)
        {
            return;
        }

        IReadOnlyList<CommandResponse> responses = world.ApplyBatch(_batch);
        Report.Batches++;
        Report.CommandsWritten += _batch.Count;
        foreach (CommandResponse response in responses)
        {
            if (response.HasError)
            {
                Report.SampleBatchFailure(response.Error);
            }
        }
    }

    /// <summary>
    /// Compare every pose written this tick against what the world says the body became.
    /// </summary>
    /// <remarks>
    /// <para>Taken after the tick, because the world observer reports the state of a frame once that
    /// frame exists, and the pose was written for the frame the tick just produced.</para>
    ///
    /// <para>Free: the observer streams every actor's transform every tick whether or not anything
    /// reads it, so this is an array read and a subtraction per rendered vehicle. That is what makes
    /// it affordable per vehicle per tick rather than as a sample, and being per vehicle per tick is
    /// what lets a residual be attributed to a vehicle rather than to the run.</para>
    /// </remarks>
    private void MeasureDivergence()
    {
        if (_options.World is not { } world)
        {
            return;
        }

        foreach ((string vehicleId, ActorId actor, VehiclePose pose) in _commanded)
        {
            if (world.ObservedTransform(actor) is not { } observed)
            {
                Report.VehicleTicksWithNoReadBack++;
                continue;
            }

            PoseDivergence divergence = PoseDivergence.Between(
                _tickIndex, RenderedTimeSeconds, vehicleId, actor, pose, observed);
            Report.AddDivergence(divergence);
            _options.OnDivergence?.Invoke(divergence);
        }
    }

    /// <summary>The CARLA transform a computed pose is, in the units the batch is written in.</summary>
    private static Transform TransformOf(in VehiclePose pose) =>
        new(new Location((float)pose.X, (float)pose.Y, (float)pose.Z),
            new Rotation((float)pose.PitchDegrees, (float)pose.YawDegrees, (float)pose.RollDegrees));

    private static void Attempt(List<Exception> failures, Action step)
    {
        try
        {
            step();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
    }

    /// <summary>
    /// Refuse a session given two ways to advance the world.
    /// </summary>
    /// <remarks>
    /// A world and a tick delegate together is not an ambiguity to resolve in favour of one of them:
    /// whichever the session picked, the caller believes the other is running, and the run that
    /// results is a world ticked a different number of times from the timeline its poses were
    /// computed for.
    /// </remarks>
    private static void RequireOneWayToAdvanceTheWorld(SumoDriveSessionOptions options)
    {
        if (options.World is not null && options.TickWorld is not null)
        {
            throw new CoSimSessionRefusedException(
                "The session was given both a CARLA world and a tick delegate. It owns the advance "
                + "of simulated time on both sides, so exactly one thing may advance the world: "
                + "give the world to drive it, or the delegate to compute every pose and apply "
                + "none of them.");
        }
    }

    /// <summary>
    /// How far the lane the bridge read puts a vehicle from where SUMO says it is, at a SUMO frame.
    /// </summary>
    /// <remarks>
    /// Taken at the frames themselves rather than between them, so what it measures is whether the
    /// network in the world package is the network SUMO is driving on -- not how good the
    /// interpolation is. A network from a different netconvert run answers here and nowhere else.
    /// </remarks>
    private void MeasureLaneGeometry()
    {
        foreach (string vehicleId in _renderSet.RenderedVehicleIds)
        {
            if (!_next.TryGetValue(vehicleId, out CoSimVehicleFrame frame)
                || !_network.TryGetLane(frame.LaneId, out SumoLane lane))
            {
                continue;
            }

            (double x, double y, _, _) = lane.PointAt(frame.LanePositionMetres);
            Report.AddLaneGeometryResidual(
                Math.Sqrt(Math.Pow(x - frame.X, 2) + Math.Pow(y - frame.Y, 2)));
        }
    }

    private static double BumperResidual(in VehiclePose pose,
                                         in VehicleExtent extent,
                                         double sumoX,
                                         double sumoY)
    {
        double radians = pose.YawDegrees * (Math.PI / 180.0);
        double forwardX = Math.Cos(radians);
        double forwardY = Math.Sin(radians);
        double bumperX = pose.X + (extent.BumperToOriginMetres * forwardX)
                         - (extent.LateralOffsetMetres * forwardY);
        double bumperY = pose.Y + (extent.BumperToOriginMetres * forwardY)
                         + (extent.LateralOffsetMetres * forwardX);
        return Math.Sqrt(Math.Pow(bumperX - sumoX, 2) + Math.Pow(-bumperY - sumoY, 2));
    }

    private static void CopyFrames(Dictionary<string, CoSimVehicleFrame> from,
                                   Dictionary<string, CoSimVehicleFrame> to)
    {
        to.Clear();
        foreach ((string vehicleId, CoSimVehicleFrame frame) in from)
        {
            to[vehicleId] = frame;
        }
    }

    /// <summary>
    /// Refuse a network the world was not built from.
    /// </summary>
    /// <remarks>
    /// <para>Three things make the frame conversion a sign on the northing and no offset at all, and
    /// all three are checkable before a single pose is computed: the network's projection has to be
    /// the world's own georeference string, its normalisation offset has to be zero, and its extent
    /// has to lie inside the ground surface the world carries.</para>
    ///
    /// <para>A network built at a different origin converts to a position several hundred metres
    /// away that is inside the sandbox and looks entirely ordinary, which is why this is a refusal
    /// rather than a warning.</para>
    /// </remarks>
    private static void RequireTheWorldSNetwork(WorldPackageManifest manifest,
                                                SumoRoadNetwork network,
                                                SumoDriveSessionOptions options)
    {
        if (network.NetOffset != (0.0, 0.0))
        {
            throw new CoSimSessionRefusedException(
                $"The network in {options.WorldPackagePath} was normalised by "
                + $"{network.NetOffset}, so its coordinates are not the world's. Build it with "
                + "normalisation disabled.");
        }

        if (network.Projection != manifest.GeoReferenceString)
        {
            throw new CoSimSessionRefusedException(
                $"The network projects as '{network.Projection}' and the world it is packaged with "
                + $"as '{manifest.GeoReferenceString}'. A vehicle converted through the wrong one of "
                + "those lands somewhere inside the sandbox and looks entirely ordinary.");
        }

        (double minX, double minY, double maxX, double maxY) = network.ConvBoundary;
        (double gridMinX, double gridMinY, double gridMaxX, double gridMaxY) =
            (manifest.GridMinXMeters, manifest.GridMinYMeters,
             manifest.GridMinXMeters + ((manifest.GridNumCols - 1) * manifest.GridCellSizeMeters),
             manifest.GridMinYMeters + ((manifest.GridNumRows - 1) * manifest.GridCellSizeMeters));

        // The network's extent is in the projected frame and the grid's is in the CARLA frame, so
        // the northings swap places as well as sign. The tolerance is one grid cell: the surface is
        // built from the same bounds the network was clipped to and the two agree to within
        // rounding -- measured on the shipped Arapahoe world, the network overhangs the grid by
        // 0.2 mm at one edge -- so a stricter test refuses every world for a fraction of a cell. A
        // vehicle that genuinely stands off the end of the surface is counted at the tick it
        // happens, which is where an overhang of any size shows up.
        double slack = manifest.GridCellSizeMeters;
        if (minX < gridMinX - slack || maxX > gridMaxX + slack
            || -maxY < gridMinY - slack || -minY > gridMaxY + slack)
        {
            throw new CoSimSessionRefusedException(
                $"The network spans ({minX:0.0}, {minY:0.0}) to ({maxX:0.0}, {maxY:0.0}) and the "
                + $"world's ground surface covers ({gridMinX:0.0}, {gridMinY:0.0}) to "
                + $"({gridMaxX:0.0}, {gridMaxY:0.0}) in the CARLA frame. Part of the network has no "
                + "ground under it, so vehicles there would have no height.");
        }
    }
}
