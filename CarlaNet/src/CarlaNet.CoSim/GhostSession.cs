using System.Diagnostics;
using CarlaNet.Map.WorldPackage;
using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// A co-simulation session that computes every pose it would apply, records it, and applies none of
/// them.
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
public sealed class GhostSession : IDisposable
{
    private readonly GhostSessionOptions _options;
    private readonly SumoConnection _sumo;
    private readonly SubscribedPopulation _population;
    private readonly RenderSetManager _renderSet;
    private readonly VehicleTypeBinder _binder;
    private readonly PoseConverter _converter;
    private readonly LaneArcInterpolator _interpolator;
    private readonly SumoRoadNetwork _network;
    private readonly PopulationLease _lease;
    private readonly Dictionary<string, (double X, double Y)> _positions = [];
    private readonly Dictionary<string, CoSimVehicleFrame> _previous = [];
    private readonly Dictionary<string, CoSimVehicleFrame> _next = [];
    private readonly Stopwatch _bridgeClock = new();
    private readonly Stopwatch _sumoClock = new();

    private long _tickIndex;
    private bool _disposed;

    private GhostSession(GhostSessionOptions options,
                         SumoConnection sumo,
                         CoSimClock clock,
                         SumoRoadNetwork network,
                         GroundSurface ground,
                         VehicleCatalogue catalogue,
                         PopulationLease lease)
    {
        _options = options;
        _sumo = sumo;
        _network = network;
        _lease = lease;
        _population = new SubscribedPopulation(sumo.TraCI);
        _renderSet = new RenderSetManager(options.RenderSet, options.OnRelease);
        _binder = new VehicleTypeBinder(sumo.TraCI, catalogue);
        _converter = new PoseConverter(ground, options.MeasuredSeatHeights);
        _interpolator = new LaneArcInterpolator(network);

        Clock = clock;
        Report = new GhostRunReport
        {
            Clock = clock,
            ScenarioPath = options.ScenarioPath,
            WorldPackagePath = options.WorldPackagePath,
            CatalogueDigest = catalogue.CatalogueDigest,
            SumoStepOverrideSeconds = options.SumoStepOverrideSeconds,
        };
    }

    /// <summary>The three rates the session resolved and validated.</summary>
    public CoSimClock Clock { get; }

    /// <summary>What the run has established so far.</summary>
    public GhostRunReport Report { get; }

    /// <summary>The simulated instant the last world tick rendered.</summary>
    public double RenderedTimeSeconds { get; private set; }

    /// <summary>The vehicles that would hold a rendered actor right now.</summary>
    public IReadOnlyCollection<string> RenderedVehicleIds => _renderSet.RenderedVehicleIds;

    /// <summary>
    /// Start a session: validate the clock, check the network is the world's, take the population
    /// lease, and buffer the one SUMO step of lookahead every sub-step pose is interpolated inside.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The clock does not divide, the world is asynchronous, the network is not the one the world
    /// was built from, or something else already holds the world's population.
    /// </exception>
    public static GhostSession Start(GhostSessionOptions options)
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

        try
        {
            CoSimClock clock = CoSimClock.ForSession(sumo.StepLength, options.WorldDeltaSeconds,
                                                     options.CaptureRateHz,
                                                     options.WorldIsSynchronous);
            RequireTheWorldSNetwork(manifest, network, options);

            PopulationLease lease = WorldDriveAuthority.ForWorld(options.WorldKey)
                .Acquire(PopulationMode.SumoDrivenPlayback, options.Holder);

            var session = new GhostSession(options, sumo, clock, network, ground, catalogue, lease);
            try
            {
                session.Prime();
                return session;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch
        {
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
            _bridgeClock.Stop();

            if (_options.TickWorld is { } tickWorld && !tickWorld())
            {
                throw new CoSimSessionRefusedException(
                    $"The CARLA world produced no frame for tick {_tickIndex}. A world that stops "
                    + "ticking while SUMO keeps stepping renders a timeline nothing simulated.");
            }

            _tickIndex++;
            Report.Ticks++;
            RenderedTimeSeconds += Clock.WorldDeltaSeconds;
        }

        bool more = AdvanceSumo();
        Report.BridgeSecondsOnTicks = _bridgeClock.Elapsed.TotalSeconds;
        Report.SumoSecondsOnSteps = _sumoClock.Elapsed.TotalSeconds;
        return more;
    }

    /// <summary>Close the open intervals, give back the lease, and end the simulation.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderSet.CloseAll(RenderedTimeSeconds);
        Report.Admissions = _renderSet.Admissions;
        Report.CapacityDeclines = _renderSet.CapacityDeclines;
        foreach (UnrenderableReason reason in _binder.RefusedTypes.Values)
        {
            Report.CountRefusedType(reason);
        }

        Report.BridgeSecondsOnTicks = _bridgeClock.Elapsed.TotalSeconds;
        Report.SumoSecondsOnSteps = _sumoClock.Elapsed.TotalSeconds;
        _lease.Dispose();
        _sumo.Dispose();
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

            _options.OnPose?.Invoke(new GhostPoseRecord(
                _tickIndex, RenderedTimeSeconds, Clock.IsCaptureTick(_tickIndex), applied,
                state.Case, state.X, state.Y, state.HeadingDegrees));
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
                                                GhostSessionOptions options)
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
