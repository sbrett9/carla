using System.Diagnostics;
using System.Globalization;
using CarlaNet.Map.WorldPackage;
using CarlaNet.Recording;
using CarlaNet.Sumo;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Supervision;

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
/// identity check, the population lease, the subscription, the render set, the one-step
/// lookahead, the lane interpolation and the pose conversion. The world is ticked, by whatever the
/// caller supplied, so the session owns the advance of simulated time on both sides exactly as it
/// will when it drives.</para>
///
/// <para><b>By default every vehicle SUMO has is drawn.</b> The scenario is the only arbiter of
/// population: a vehicle holds a body from the frame SUMO first reports it in until SUMO removes it or
/// the session ends, parked vehicles included. One SUMO inserts during the run is drawn first at the
/// position SUMO first reported, moving from there, and never on a frame before SUMO inserted it.
/// Nothing limits how many unless the caller asks for a limit; a scenario heavier than the machine is
/// comfortable with makes a synchronous run slower on the wall clock, never different in content.</para>
///
/// <para><b>A limit on which vehicles get a body, where one is asked for.</b> An optional performance
/// control, off by default (<see cref="SumoDriveSessionOptions.RenderSet"/>): a circle, the footprints
/// of the cameras registered with <see cref="AddCamera"/>, or a capacity. A vehicle outside the limit is
/// still simulated by SUMO and has no body, no frame and no truth record; every one inside it is
/// posed, seated, turned and named to the server exactly as with no limit, and the report states the
/// policy and counts what it left out.</para>
///
/// <para><b>A draw distance, where one is asked for.</b> An optional performance control, off by
/// default (<see cref="SumoDriveSessionOptions.DrawDistanceMetres"/>): each body is set, once, to be
/// drawn no farther than that from any camera. Every vehicle keeps its body, its pose and its truth;
/// a camera simply does not draw a body beyond the distance, and each frame's render set says what
/// distance it was drawn under so a recorder marks those vehicles in that camera's sidecar.</para>
///
/// <para><b>Observers, where any are registered.</b> What follows the run from inside it -- a truth
/// track, a manifest, an interval binder -- is told of every SUMO frame read, with what SUMO did to its
/// vehicles in that step at TraCI's clock, of every frame rendered, and of the session's end
/// (<see cref="SumoDriveSessionOptions.StepObservers"/>), rather than being written into the loop. The
/// world truth track and the run manifest are two, built by the session itself where it is asked for them
/// (<see cref="SumoDriveSessionOptions.WorldTruthTrackPath"/>, <see cref="SumoDriveSessionOptions.RunManifestPath"/>).</para>
/// </remarks>
public sealed class SumoDriveSession : IDisposable
{
    /// <summary>
    /// How near a tick's instant may sit below the window's opening and still be the window's: the
    /// rendered clock is a running sum of the world's delta, so it reaches a whole instant to within
    /// rounding rather than exactly.
    /// </summary>
    private const double WindowOpenTolerance = 1e-6;

    /// <summary>
    /// How long a SUMO that closed the connection is given to exit and deliver the last lines it wrote,
    /// which say why, before the refusal quoting them is composed without them.
    /// </summary>
    private static readonly TimeSpan SumoLastWordsBound = TimeSpan.FromSeconds(5);

    /// <summary>The seed SUMO runs under when its configuration declares none (its <c>--seed</c> default).</summary>
    private const long SumoDefaultSeed = 23423;

    private readonly SumoDriveSessionOptions _options;
    private readonly ICarlaWorld? _world;
    private readonly SumoConnection _sumo;
    private readonly SumoSimulationSubscription _simulation;
    private readonly SumoVehicleQueries _vehicleQueries;
    private readonly ISumoStepObserver[] _observers;
    private readonly WorldTruthTrackWriter? _track;
    private readonly RunManifestWriter? _manifest;
    private readonly SumoConsoleTail _console;
    private readonly SubscribedPopulation _population;
    private readonly RenderSetManager _renderSet;
    private readonly VehicleTypeBinder _binder;
    private readonly PoseConverter _converter;
    private readonly LaneArcInterpolator _interpolator;
    private readonly SumoRoadNetwork _network;
    private readonly PopulationLease _lease;
    private readonly DriveLease? _drive;
    private readonly WorldSettingsLease? _settings;
    private readonly LayerVisibilityLease? _layers;
    private readonly VehicleBodyPool? _pool;
    private readonly Func<ulong?> _tickWorld;
    private readonly IlluminationFrames _illumination = new();
    private readonly RenderSetFrames _renderSets = new();
    private readonly Dictionary<string, (string TypeId, ulong? AdmittedTick)> _renderedSpans = [];
    private readonly Dictionary<ActorId, string> _namedToServer = [];
    private readonly HashSet<ActorId> _heldNow = [];
    private readonly List<LentBody> _lentSinceNamed = [];
    private readonly List<ActorId> _parkedSinceNamed = [];
    private readonly Dictionary<ActorId, (string VehicleId, SupervisionInForce Supervision)> _supervisionNamed = [];
    private readonly Dictionary<string, AbsenceInForce> _absencesNamed = new(StringComparer.Ordinal);
    private readonly List<ActorId> _supervisionDropped = [];
    private readonly List<BodySupervision> _supervisionSinceNamed = [];
    private readonly List<AbsenceInForce> _absencesOpenedSinceNamed = [];
    private readonly List<string> _absencesClosedSinceNamed = [];
    private readonly TickBatch _batch = new();
    private readonly List<(string VehicleId, ActorId Actor, VehiclePose Pose)> _commanded = [];
    private readonly List<VehiclePose> _appliedPoses = [];
    private readonly Dictionary<string, CoSimVehicleFrame> _previous = [];
    private readonly Dictionary<string, CoSimVehicleFrame> _next = [];
    private readonly Stopwatch _bridgeClock = new();
    private readonly Stopwatch _sumoClock = new();
    private readonly RealTimePacer _pacer;
    private readonly Dictionary<(string Collider, string Victim), CollisionSpan> _collisions = [];
    private readonly HashSet<(string Collider, string Victim)> _collisionsReported = [];
    private readonly List<(string Collider, string Victim)> _collisionsOver = [];
    private readonly HashSet<string> _awaitingInsertion = [];
    private readonly HashSet<string> _stillAwaiting = [];
    private readonly List<VehicleNotInserted> _notInsertedThisFrame = [];
    private readonly List<RenderedVehicleInterval> _releasedThisFrame = [];
    private readonly HeadlightRule? _headlights;
    private readonly Dictionary<string, (ActorId Actor, VehicleLightStateFlags Lamps)> _lampsWritten = [];
    private readonly PathHeading _headings = new();
    private readonly Dictionary<string, double> _sumoAngles = [];
    private readonly List<ActorId> _drawDistanceBodies = [];
    private readonly GroundSurface _ground;
    private readonly SortedDictionary<ActorId, CameraOptics> _cameras = [];
    private readonly List<CameraView> _cameraViews = [];
    private readonly long _seed;
    private readonly double _longestBody;
    private readonly double _bodyReach;

    private readonly (double Latitude, double Longitude) _origin;
    private SolarLease? _sun;
    private SupervisionPlanIdentity? _supervisionPlanNamed;
    private long _supervisionRevisionNamed = -1;
    private SolarAudit? _sunAudit;
    private long _tickIndex;
    private double _frameSeconds;
    private SumoStepEvents _events = SumoStepEvents.None(0.0);
    private IReadOnlyList<SumoCollision> _collisionsThisFrame = [];
    private bool _collisionListRead;
    private bool _scenarioFinished;
    private double? _lastCompleteSeconds;
    private ulong? _lastCompleteFrame;
    private double? _reportedSunElevation;
    private SolarReading? _sunReadThisTick;
    private RenderSet? _renderSetNow;
    private double? _drawDistanceAsked;
    private double? _drawDistanceApplied;
    private int _bodiesGivenTheDrawDistance;
    private ulong? _lastFrame;
    private bool _disposed;

    private SumoDriveSession(SumoDriveSessionOptions options,
                             ICarlaWorld? world,
                             SumoConnection sumo,
                             SumoConsoleTail console,
                             SumoReleaseCheck release,
                             ScenarioLockCheck compiled,
                             TeleportingCheck teleporting,
                             RouteErrorCheck routeErrors,
                             SumoDistributionEditCheck distributionEdits,
                             SumoCollisionHandling collisionHandling,
                             SumoLaneChangeDuration laneChanges,
                             HeadlightRule? headlights,
                             CoSimClock clock,
                             SumoRoadNetwork network,
                             GroundSurface ground,
                             RoadSurface roads,
                             VehicleCatalogue catalogue,
                             PopulationLease lease,
                             DriveLease? drive,
                             WorldSettingsLease? settings,
                             LayerVisibilityLease? layers,
                             VehicleBodyPool? pool,
                             (double Latitude, double Longitude) origin,
                             long seed,
                             int trackSumoStepsPerSample)
    {
        _options = options;
        _world = world;
        _origin = origin;
        _ground = ground;
        _seed = seed;
        _longestBody = catalogue.LongestBodyMetres;
        _bodyReach = catalogue.BodyReachMetres;
        _sumo = sumo;
        _simulation = sumo.Simulation.Subscription;
        _console = console;
        _headlights = headlights;
        _network = network;
        _lease = lease;
        _drive = drive;
        _settings = settings;
        _layers = layers;
        _pool = pool;
        _drawDistanceAsked = options.DrawDistanceMetres;
        // A delegate that only counts has no frames of its own, so its ticks are numbered by the
        // session; nothing records a frame of a world that does not exist.
        Func<bool> counted = options.TickWorld ?? (() => true);
        _tickWorld = world is { } driven
            ? driven.Tick
            : () => counted() ? (ulong)(_tickIndex + 1) : null;
        _population = new SubscribedPopulation(sumo.TraCI);
        _renderSet = new RenderSetManager(options.RenderSet, Release);
        _binder = new VehicleTypeBinder(sumo.TraCI, catalogue);
        _converter = new PoseConverter(ground, options.MeasuredSeatHeights, roads);
        _interpolator = new LaneArcInterpolator(network);

        // The factor is read here, once. The options object stays writable after the session starts,
        // and a pace that could change part-way through would make one run two.
        _pacer = new RealTimePacer(options.RealTimeFactor, options.PacingWindowSeconds,
                                   clock.WorldDeltaSeconds, options.WallClock);

        Clock = clock;
        Report = new CoSimRunReport
        {
            Clock = clock,
            ScenarioPath = options.ScenarioPath,
            WorldPackagePath = options.WorldPackagePath,
            CatalogueDigest = catalogue.CatalogueDigest,
            Sumo = release,
            CompileLock = compiled,
            Teleporting = teleporting,
            RouteErrors = routeErrors,
            DistributionEdits = distributionEdits,
            CollisionHandling = collisionHandling,
            CollisionDetail = options.CollisionDetail,
            LaneChanges = laneChanges,
            VehicleLampsDriven = options.VehicleLampsDriven,
            Console = console,
            SumoStepOverrideSeconds = options.SumoStepOverrideSeconds,
            Pacing = _pacer,
            LayerVisibility = layers?.Applied ?? new Dictionary<string, bool>(),
            DriveLeaseHolder = drive?.Holder,
            DriveLeaseRefused = drive?.Refusal,
            Epoch = options.Epoch,
            Illumination = options.Illumination,
            SumoSeed = seed,
            RoadMapping = roads.Mapping,
            DrawDistanceMetres = options.DrawDistanceMetres,
            RenderSetPolicy = options.RenderSet.Description,
            RenderSetLimits = options.RenderSet.Limits,
            RenderSetCapacity = options.RenderSet.Capacity,
        };
        _vehicleQueries = new SumoVehicleQueries(sumo.Vehicles, Report);

        // Last, so nothing after them can leave the files they create behind a constructor that threw.
        // Both are told ahead of the caller's observers, so none of theirs that fails can starve them.
        _track = options.WorldTruthTrackPath is { } trackPath
            ? WorldTruthTrackWriter.Open(trackPath, trackSumoStepsPerSample, clock,
                                         typeId => WorldTruthVehicleType.Read(sumo.TraCI, _binder, catalogue, typeId),
                                         ground, origin, options.Epoch)
            : null;
        try
        {
            _manifest = options.RunManifestPath is { } manifestPath
                ? RunManifestWriter.Open(manifestPath, options, Report, () => _scenarioFinished)
                : null;
        }
        catch
        {
            _track?.Discard();
            throw;
        }

        // Read once, as the pace is: an observer added to the options after the start is told nothing,
        // rather than part of a run.
        ISumoStepObserver[] given = options.StepObservers is { } observers ? [.. observers] : [];

        // A compiled scenario's plan is bound by a binder the session builds itself, told after every
        // other observer, so a step it refuses for a dropped plan subject has been told to all of them.
        // Every observer that writes intervals, the run manifest included, is handed it as a sink, from
        // the first frame.
        IEnumerable<ISumoStepObserver> sinkCandidates = _manifest is { } writer ? given.Prepend(writer) : given;
        SupervisionBinder = compiled.Plan is { } plan
            ? new SupervisionBinder(plan, Supervision, clock, () => WindowOpensAtSeconds,
                                    sinkCandidates.OfType<ISupervisionIntervalSink>())
            : null;
        if (SupervisionBinder is { } defectsFrom)
        {
            // The manifest writes the binder's seam defects beside its intervals.
            _manifest?.Supervise(defectsFrom);
        }

        List<ISumoStepObserver> told = [];
        if (_track is { } track)
        {
            told.Add(track);
        }

        if (_manifest is { } manifest)
        {
            told.Add(manifest);
        }

        told.AddRange(given);
        if (SupervisionBinder is { } binder)
        {
            told.Add(binder);
        }

        _observers = [.. told];
    }

    /// <summary>The three rates the session resolved and validated.</summary>
    public CoSimClock Clock { get; }

    /// <summary>What the run has established so far.</summary>
    public CoSimRunReport Report { get; }

    /// <summary>The simulated instant the next world tick renders.</summary>
    public double RenderedTimeSeconds { get; private set; }

    /// <summary>
    /// The simulated instant of the first frame the session renders: where SUMO was fast-forwarded
    /// to, on SUMO's own step.
    /// </summary>
    public double FirstRenderedSeconds { get; private set; }

    /// <summary>
    /// The simulated instant the capture window opens: <see cref="SumoDriveSessionOptions.WindowOpensAtSimulatedSecond"/>,
    /// or the first rendered frame's where none was given. The frames rendered before it are the
    /// prewarm's; a sun frozen at the window's start is pinned here.
    /// </summary>
    public double WindowOpensAtSeconds { get; private set; }

    /// <summary>
    /// The vehicles in the render set as of the SUMO frame last read, one step ahead of the rendered
    /// clock: every vehicle SUMO has, or under an optional limit the ones it admits. One SUMO inserted at
    /// that frame holds its body from that frame on, once the rendered clock reaches it.
    /// </summary>
    public IReadOnlyCollection<string> RenderedVehicleIds => _renderSet.RenderedVehicleIds;

    /// <summary>
    /// The session's hold on the world's sun: what it was bound to, what the world reported back,
    /// and what it was found holding. Null where the session binds no sun.
    /// </summary>
    public SolarLease? Sun => _sun;

    /// <summary>
    /// The session's hold on the world's drive lease: the name it was taken under, and whether the
    /// server holds it for this run or refused it. Null where the session drives no world.
    /// </summary>
    public DriveLease? Drive => _drive;

    /// <summary>
    /// The comparison of the world's sun against the declared one, taken when the window opened and
    /// on every tick since. Null where the session binds no sun.
    /// </summary>
    public SolarAudit? SunAudit => _sunAudit;

    /// <summary>
    /// Each rendered frame's illumination declaration, for a recorder to write beside the capture of
    /// that frame: the epoch, the policy, the frame's civil instant, the sun declared for it and the
    /// audit's residual on its tick. Empty where the session renders no world.
    /// </summary>
    public IIlluminationSource Illumination => _illumination;

    /// <summary>
    /// Each rendered frame's render set, for a recorder to list beside the capture of that frame:
    /// which bodies the frame drew, the SUMO vehicle each one drew, that vehicle's type and the frame
    /// its rendered span began on. Every body not in a frame's set stood parked out of sight on that
    /// frame. Empty where the session renders no world.
    /// </summary>
    /// <remarks>
    /// Keyed by the frame each tick produced, because bodies are lent and given back between ticks
    /// and an image arrives several ticks after its frame: the set in force when the image arrives is
    /// routinely not the one it was taken under. The last <see cref="RenderSetFrames.Capacity"/>
    /// frames are held.
    /// </remarks>
    public IRenderSetSource RenderSet => _renderSets;

    /// <summary>
    /// The supervision in force for the drive, which the interval binder states per SUMO vehicle and the
    /// session puts on the server for the bodies that draw them, before the cue of each tick it changes on.
    /// </summary>
    /// <remarks>
    /// <para>What the binder hands over, and nothing a recorder reads: the server carries it on every
    /// world-observer snapshot, and every reader -- a recorder beside the session included -- takes it
    /// from there (<c>CarlaClient.GetSnapshotFrame(frame, out renderSet, out supervision)</c>),
    /// so no two clients of the world hold different truth for one frame.</para>
    ///
    /// <para>Nothing is put to the server until a plan is bound, nothing at all where the session renders
    /// no world, and nothing more once the server refuses a change or the render set. The session
    /// withdraws what it put as it ends.</para>
    /// </remarks>
    public DriveSupervision Supervision { get; } = new();

    /// <summary>
    /// The binder of the plan the scenario's compile lock binds (<see cref="ScenarioLockCheck.Plan"/>):
    /// it opens and closes the plan's intervals on SUMO's events and states what is in force on
    /// <see cref="Supervision"/>. Null for a scenario that binds no plan.
    /// </summary>
    /// <remarks>
    /// Built by the session and told of every frame after the caller's observers. A step observer given
    /// to the session that is also an <see cref="ISupervisionIntervalSink"/> is told every interval it
    /// opens and closes. A plan subject SUMO never inserts makes the advance that showed it refuse.
    /// </remarks>
    public SupervisionBinder? SupervisionBinder { get; }

    /// <summary>
    /// The world truth track the session writes -- where, at what rate, and how much it holds so far --
    /// or null where none was asked for (<see cref="SumoDriveSessionOptions.WorldTruthTrackPath"/>).
    /// </summary>
    public WorldTruthTrackWriter? WorldTruthTrack => _track;

    /// <summary>
    /// The run manifest the session writes -- where, and how many rows it holds so far -- or null where
    /// none was asked for (<see cref="SumoDriveSessionOptions.RunManifestPath"/>). Its caller names each
    /// camera on it as the camera is placed, and closes it, saying why the run ended, before it reads
    /// the run's closing gates (<see cref="RunManifestWriter.Close"/>).
    /// </summary>
    public RunManifestWriter? RunManifest => _manifest;

    /// <summary>
    /// Whether SUMO had nothing left to simulate at the last advance, which then answered false.
    /// </summary>
    public bool ScenarioFinished => _scenarioFinished;

    /// <summary>The cameras registered with the session, in actor order.</summary>
    public IReadOnlyCollection<ActorId> Cameras => _cameras.Keys;

    /// <summary>
    /// Register a camera whose view the render set follows: from the next admission pass, a policy that
    /// follows cameras renders the vehicles inside and approaching its ground footprint.
    /// </summary>
    /// <param name="camera">The camera's actor id, spawned by the caller.</param>
    /// <remarks>
    /// <para>Only a policy that follows the cameras (<see cref="CameraFootprintRenderSetPolicy"/>, an
    /// optional performance control) reads them; under any other a registered camera changes
    /// nothing. The camera's image size and field of view are read from its attributes now, in one
    /// round trip; its pose is read at every pass from the client's snapshot of the last frame
    /// rendered, so a camera flown or orbited between passes is followed wherever it goes.
    /// Registering one already registered reads its attributes again and changes nothing else.</para>
    ///
    /// <para>Cameras come and go during a run -- a free view opened late, a rig taken down -- and each
    /// pass decides from the ones registered then. With none registered the policy's fallback decides.
    /// Registered between advances, from the thread that advances the session, as every other call on
    /// it is.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The session renders no world to read a camera from.</exception>
    /// <exception cref="ArgumentException">The world has no such actor, or the actor is not a camera.</exception>
    public void AddCamera(ActorId camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_world is not { } world)
        {
            throw new InvalidOperationException(
                "The session renders no world, so it has no camera to follow: a camera is read from the "
                + "world it was spawned in.");
        }

        _cameras[camera] = world.DescribeCamera(camera)
                           ?? throw new ArgumentException(
                               $"Actor {camera} is not a camera the world knows: it has no image_size_x, "
                               + "image_size_y and fov to take a footprint from.", nameof(camera));
    }

    /// <summary>
    /// Stop following a camera, from the next admission pass. Answers whether it was registered.
    /// </summary>
    /// <remarks>
    /// Call it before destroying the camera: a registered camera the world no longer has is left out of
    /// every pass, and counted on the report, but it is still registered.
    /// </remarks>
    public bool RemoveCamera(ActorId camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _cameras.Remove(camera);
    }

    /// <summary>
    /// How far from a camera, in metres, the bodies are drawn now, or null where every body is drawn
    /// at any range: no draw distance was asked for, the server refused it, or no world is driven.
    /// </summary>
    /// <remarks>
    /// The distance the bodies actually carry, which is what each frame's render set records
    /// (<see cref="CarlaNet.Recording.RenderSet.DrawDistanceMetres"/>). What was asked for is on the
    /// report (<see cref="CoSimRunReport.DrawDistanceMetres"/>), with the server's refusal where it
    /// refused.
    /// </remarks>
    public double? DrawDistanceMetres => _drawDistanceApplied;

    /// <summary>
    /// Change how far from a camera the bodies are drawn, in metres, or draw every body at any range
    /// again with null; it holds from the next tick's frame on.
    /// </summary>
    /// <remarks>
    /// <para>An optional performance control, rendering only: every vehicle keeps its body, its pose
    /// and its truth. Every body the pool holds is set in one round trip, now, and every body spawned
    /// later as it is spawned. Called between advances, from the thread that advances the session,
    /// as every other call on it is.</para>
    ///
    /// <para>A server built before it carried the call refuses it; the bodies are then drawn as they
    /// were, the report names the refusal, and nothing more is sent.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The distance is not a positive number of metres.</exception>
    public void SetDrawDistance(double? metres)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (metres is { } asked && (!double.IsFinite(asked) || asked <= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(metres), asked,
                                                  "A draw distance is a positive number of metres, or null for none.");
        }

        _drawDistanceAsked = metres;
        Report.DrawDistanceMetres = metres;
        if (_world is not { } world || _pool is not { } pool || Report.DrawDistanceRefused is not null
            || (metres is null && _drawDistanceApplied is null))
        {
            // No world to draw in, a server that refused, or nothing set to clear.
            return;
        }

        _drawDistanceBodies.Clear();
        foreach (PooledBody body in pool.Bodies)
        {
            _drawDistanceBodies.Add(body.Actor);
        }

        if (_drawDistanceBodies.Count == 0)
        {
            // Nothing to set yet: the bodies spawned from here on are set as they are spawned.
            _drawDistanceApplied = null;
            return;
        }

        if (WriteTheDrawDistance(world, metres ?? 0.0))
        {
            _drawDistanceApplied = metres;
            _bodiesGivenTheDrawDistance = _drawDistanceBodies.Count;
        }
    }

    /// <summary>
    /// The SUMO this session drives, for a test that has to act on it as something outside the
    /// session would -- take a vehicle out between two steps, suspend the process.
    /// </summary>
    internal SumoConnection Sumo => _sumo;

    /// <summary>
    /// Start a session: check the world package is the loaded world's, check the SUMO it launches is
    /// the release that converted the world, check the scenario runs on the world package's network,
    /// check that network is in the world's frame, take the population lease and the world's drive
    /// lease, start SUMO, validate the clock, and buffer the one SUMO step of lookahead every sub-step
    /// pose is interpolated inside.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The session renders a world and declares no illumination policy, or a policy that binds the
    /// sun and no epoch to bind it from; the real-time factor or its window is not a usable number;
    /// the world package does not describe the world the server has loaded; the named SUMO
    /// installation holds no <c>sumo</c>, or no <c>sumo-gui</c> where the GUI was asked for, or the
    /// binary about to be launched is not the release the world package records as its converter and
    /// the mismatch was not accepted; the scenario's
    /// network is not the one the world package carries, or the package carries a network other than
    /// the one it records; a compile lock beside the scenario binds other files, another catalogue or
    /// another epoch; the scenario lets SUMO teleport a blocked vehicle and that was not accepted; the
    /// clock does not divide, the world is asynchronous, the network is not in
    /// the world's frame, something else already holds the world's population -- in this process, or
    /// on the server as the world's drive lease -- or the world's sun
    /// could not be bound; the scenario tells SUMO to carry on past a route it cannot follow; SUMO could
    /// not load the scenario or failed during its fast-forward; or the connection to the CARLA server
    /// failed. Its <see cref="CoSimSessionRefusedException.Stage"/> says how far the start had got, and
    /// everything taken before it has been given back -- or, for what only an unreachable server could
    /// hold, named in <see cref="CoSimSessionRefusedException.GiveBackFailures"/>.
    /// </exception>
    public static SumoDriveSession Start(SumoDriveSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // How far the start has got, assigned to every refusal that leaves it, so a caller can say
        // what the world went through without reading a message. Every refusal gives back everything
        // taken before it, whatever its stage.
        CoSimSessionStage stage = CoSimSessionStage.Validation;
        var console = new SumoConsoleTail(options.SumoOutput);
        List<Exception> giveBack = [];
        try
        {
            return Begin(options, console, ref stage, giveBack);
        }
        catch (CoSimSessionRefusedException refused)
        {
            refused.Stage = stage;
            refused.GiveBackFailures = giveBack;
            throw;
        }
        catch (Exception failed) when (failed is FatalTraCIError or TraCIException)
        {
            throw new CoSimSessionRefusedException(
                stage,
                (stage == CoSimSessionStage.Launch
                    ? $"SUMO could not be started on the scenario {options.ScenarioPath}"
                    : "SUMO failed while it was fast-forwarded to "
                      + options.WarmUpToSimulatedSecond.ToString("0.###", CultureInfo.InvariantCulture)
                      + " s or while the step of lookahead after it was read")
                + $": {failed.Message}. {console.Describe()}. " + GaveBack(giveBack),
                failed)
            {
                Cause = CoSimStopCause.SumoConnectionLost,
                GiveBackFailures = giveBack,
            };
        }
        catch (WorldConnectionLostException lost)
        {
            throw new CoSimSessionRefusedException(
                stage,
                $"The connection to the CARLA server failed while the session {WhatTheStartWasDoing(stage)} "
                + $"({lost.Operation}): {lost.InnerException!.Message}. A server that cannot be reached "
                + "cannot render the run. " + GaveBack(giveBack),
                lost.InnerException)
            {
                Cause = CoSimStopCause.WorldConnectionLost,
                GiveBackFailures = giveBack,
            };
        }
    }

    /// <summary>What the start sequence was doing at a stage, for a refusal to finish a sentence with.</summary>
    private static string WhatTheStartWasDoing(CoSimSessionStage stage) => stage switch
    {
        CoSimSessionStage.Validation => "was checking the world the server has loaded",
        CoSimSessionStage.Authority => "was taking the world's drive lease",
        CoSimSessionStage.Launch => "was taking the world's clock and rendering layers",
        _ => "was binding the world's sun, before the first tick",
    };

    /// <summary>
    /// The name the session takes the world's drive lease under: the holder the options name, with the
    /// process and the machine, so the server's refusal of another client names something an operator
    /// can find and stop.
    /// </summary>
    private static string DriveLeaseHolderName(SumoDriveSessionOptions options) =>
        $"{options.Holder} (process {Environment.ProcessId} on {Environment.MachineName})";

    /// <summary>What a failed start gave back, as the sentence that ends its refusal.</summary>
    private static string GaveBack(IReadOnlyList<Exception> failures) =>
        failures.Count == 0
            ? "The session has given back everything it took."
            : "The session gave back everything it could reach; " + failures.Count + " give-back step(s) "
              + "failed and are listed on the refusal: " + string.Join("; ", failures.Select(failure => failure.Message));

    /// <summary>The start sequence, with <paramref name="stage"/> kept at how far it has got.</summary>
    private static SumoDriveSession Begin(SumoDriveSessionOptions options,
                                          SumoConsoleTail console,
                                          ref CoSimSessionStage stage,
                                          List<Exception> giveBack)
    {
        RequireADeclaredIllumination(options);
        RequireAUsablePace(options);
        RequireOneWayToAdvanceTheWorld(options);
        RequireAWindowTheSessionRenders(options);
        RequireABoundOnSumoSAnswers(options);
        RequireAUsableDrawDistance(options);
        RequireARenderSetPolicy(options);
        RequireAUsableWorldTruthTrack(options);
        RequireAUsableRunManifest(options);
        HeadlightRule? headlights = options.VehicleLampsDriven
            ? new HeadlightRule(options.HeadlightOnBelowDegrees, options.HeadlightOffAboveDegrees)
            : null;

        // Every call the session makes on the world goes through the guard, so that a connection that
        // failed is told apart from a file that could not be read on the way.
        ICarlaWorld? world = options.World is { } given ? new WorldConnectionGuard(given) : null;

        WorldPackageManifest manifest = WorldPackage.ReadManifest(options.WorldPackagePath);
        GroundSurface ground = GroundSurface.FromWorldPackage(options.WorldPackagePath);
        SumoRoadNetwork network = SumoRoadNetwork.FromWorldPackage(options.WorldPackagePath);
        VehicleCatalogue catalogue = VehicleCatalogue.Load(options.CataloguePath);

        // Before SUMO is started and before anything on the server is written: a package that is not
        // the loaded world's is refused with the world exactly as it was found, and it costs no
        // process to find out.
        if (world is { } loaded)
        {
            LoadedWorldCheck.Require(options.WorldPackagePath, loaded.DescribeLoadedWorld());

            // The package's grids are now known to be the record's, so the truth telemetry beside the
            // session takes them from the package instead of fetching them. It writes nothing to the
            // server, and a world that declines is only slower: the telemetry then fetches as before.
            loaded.AdoptBareEarthGrids(options.WorldPackagePath);

            // And what kind of vehicle each body is: every body the pool lends is one of the
            // catalogue's blueprints, and its truth carries the base type and the special type its
            // catalogue class curates rather than the ones the blueprint declares (doc 06 D6.18).
            // Client-side, like the grids.
            loaded.AdoptCatalogueBaseTypes(catalogue.BaseTypes);
            loaded.AdoptCatalogueSpecialTypes(catalogue.SpecialTypes);
        }

        // The roads every body is seated on, joined once to the lanes SUMO drives. Read from the
        // package's own OpenDRIVE, which the check above has just confirmed is the one the server built
        // its road mesh, waypoints and paths from.
        RoadSurface roads = RoadSurface.FromWorldPackage(options.WorldPackagePath, network);

        // Which SUMO, whether it has the binary the session is to launch, and whether that binary is
        // the release that converted this world: settled before it is started, like every other
        // refusal that needs no simulation to find out.
        SumoInstallation installation = ResolveSumo(options);
        RequireTheGui(installation, options);
        SumoReleaseCheck release = RequireTheWorldSConverter(installation, manifest, options);

        // Whether the package's network is in the world's frame. It needs nothing but the package,
        // so it is settled here rather than once SUMO is running.
        RequireTheWorldSNetwork(manifest, network, options);

        // And whether the network SUMO would drive is the one the session reads its lanes from. The
        // frame check compares the package's network with the package; this is the one that looks at
        // the scenario's.
        ScenarioNetworkCheck.Require(options.ScenarioPath, options.WorldPackagePath);

        // Whether the files SUMO would run are the ones the scenario's compile lock binds, compiled
        // against this catalogue and this epoch, and whether the compiler ran them in SUMO alone before
        // writing them -- or, with no lock beside them, that the scenario is an uncompiled one, which
        // runs and is reported as such.
        ScenarioLockCheck compiled = ScenarioLockCheck.Require(options.ScenarioPath, catalogue,
                                                               options.Epoch, options.AcceptSkippedDryRun);

        // And whether SUMO would teleport a blocked vehicle, which the interpolation would render as a
        // body dragged along its route.
        TeleportingCheck teleporting = TeleportingCheck.Require(options.ScenarioPath,
                                                                options.AllowTeleporting);

        // And whether a route SUMO cannot follow stops SUMO, and so the run, as it does by default.
        RouteErrorCheck routeErrors = RouteErrorCheck.Require(options.ScenarioPath);

        // And how SUMO would edit the population on its own: a collision action that moves or removes
        // vehicles, the teleport triggers time-to-teleport leaves open, a random offset on every departure
        // and a seed from the wall clock are refused; the demand scale and the insertion limits it will
        // run under are what the report names.
        SumoDistributionEditCheck distributionEdits = SumoDistributionEditCheck.Require(
            options.ScenarioPath, options.AllowTeleporting);

        // What SUMO will do about a collision, which the report carries.
        SumoCollisionHandling collisionHandling = SumoCollisionHandling.Read(options.ScenarioPath);

        // How long SUMO takes over a lane change, which the report carries: spread over time, the
        // interpolation puts the vehicle where SUMO has it across the lanes; made inside one step, it can
        // only slide it a lane width within that step.
        SumoLaneChangeDuration laneChanges = SumoLaneChangeDuration.Read(options.ScenarioPath);

        // The seed SUMO will run under, which the report names so the run's traffic can be reproduced.
        long seed = ReadTheSeed(options.ScenarioPath);

        List<string> extraArguments = [];
        if (options.SumoStepOverrideSeconds is { } forced)
        {
            extraArguments.Add("--step-length");
            extraArguments.Add(forced.ToString(CultureInfo.InvariantCulture));
        }

        // Both leases before SUMO is started, so a world another traffic system holds is refused with
        // nothing started and nothing written. The population lease is this process's own, and
        // refuses a second mode started from the same harness; the drive lease is the server's, and
        // refuses a second process -- another drive session, or a traffic manager -- whatever started
        // it. The drive lease is the first thing written to the server, and the last given back.
        stage = CoSimSessionStage.Authority;
        PopulationLease lease = WorldDriveAuthority.ForWorld(options.WorldKey)
            .Acquire(PopulationMode.SumoDrivenPlayback, options.Holder);
        DriveLease? drive = null;
        try
        {
            drive = world is { } driven
                ? DriveLease.Take(driven, DriveLeaseHolderName(options))
                : null;

            stage = CoSimSessionStage.Launch;
            SumoConnection sumo = SumoConnection.Start(
                installation,
                options.ScenarioPath,
                new SumoLaunchOptions
                {
                    ExtraArguments = extraArguments,
                    Output = console.Add,
                    ReceiveTimeout = TimeSpan.FromSeconds(options.SumoAnswerTimeoutSeconds),
                    Gui = options.SumoGui,
                });

            WorldSettingsLease? settings = null;
            LayerVisibilityLease? layers = null;
            try
            {
                // Take the world's clock before anything else is checked against it: the settings the
                // session validates its own against have to be the ones the world is holding, not the
                // ones the caller asked for.
                settings = world is { } claimed
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
                layers = world is { } rendered
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

                // Settled as soon as SUMO's step is known, before anything is rendered: a track sampled
                // between two SUMO frames would record states nobody simulated.
                int trackSumoStepsPerSample = options.WorldTruthTrackPath is null
                    ? 1
                    : WorldTruthTrackWriter.SumoStepsPerSampleAt(options.WorldTruthTrackIntervalSeconds,
                                                                 clock.SumoStepSeconds);

                stage = CoSimSessionStage.PreRoll;
                VehicleBodyPool? pool = null;
                SumoDriveSession? session = null;
                try
                {
                    pool = world is { } bodies
                        ? new VehicleBodyPool(bodies, VehicleParking.BeyondTheSurface(ground))
                        : null;
                    session = new SumoDriveSession(options, world, sumo, console, release, compiled,
                                                   teleporting, routeErrors, distributionEdits,
                                                   collisionHandling, laneChanges, headlights, clock,
                                                   network, ground, roads, catalogue, lease, drive,
                                                   settings, layers, pool,
                                                   (manifest.OriginLatitude, manifest.OriginLongitude),
                                                   seed, trackSumoStepsPerSample);
                    session.Prime();
                    session.BindTheSun();
                    return session;
                }
                catch
                {
                    // Each step is attempted whatever the one before it did: a server that dropped the
                    // connection fails every step that writes to it, and none of those may stop the
                    // leases from being given back below. A start refused writes no track: it rendered
                    // nothing, and a track begun for it would read as a run cut off.
                    Attempt(giveBack, "delete the world truth track", () => session?._track?.Discard());
                    Attempt(giveBack, "delete the run manifest", () => session?._manifest?.Discard());
                    Attempt(giveBack, "give back the world's sun", () => session?._sun?.Dispose());
                    Attempt(giveBack, "destroy the bodies the session spawned", () => pool?.DestroyAll());
                    throw;
                }
            }
            catch
            {
                // Everything this method changed, given back, in the reverse order it was taken. A
                // session that failed to start must leave the world exactly as it found it: an operator
                // whose editor is stranded in synchronous mode is waiting on a tick from a process that
                // never started. Each step is attempted whatever the others did, so an unreachable
                // server costs what only the server can hold and never the SUMO process.
                Attempt(giveBack, "draw the rendering layers again", () => layers?.Dispose());
                Attempt(giveBack, "give back the world's settings", () => settings?.Dispose());
                Attempt(giveBack, "stop SUMO", sumo.Dispose);
                throw;
            }
        }
        catch
        {
            // The leases last, once the world is back as it was found: the drive lease held on the
            // server, where only an unreachable server can keep it, and then this process's own.
            Attempt(giveBack, "give back the world's drive lease", () => drive?.Dispose());
            Attempt(giveBack, "give back the population lease", lease.Dispose);
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
    /// <remarks>
    /// <para>Under a real-time factor, each tick cue waits here for the instant it is due -- after the
    /// tick's poses are written, so the cue goes out at that instant rather than the bridge's work
    /// later -- and every cue is timed whether or not it waited.</para>
    ///
    /// <para>Under an advancing sun, the sun for the frame is written here too, beside the poses and
    /// before the cue, so it is executed in the same drain and the frame is lit by it.</para>
    /// </remarks>
    /// <exception cref="CoSimSessionRefusedException">
    /// The run cannot go on honestly: the world produced no frame, the sun disagreed with its
    /// declaration, was absent or refused its write, the server offered no blueprint for a body the pool
    /// needed, SUMO failed or stopped answering, or the connection to the CARLA server failed -- or the
    /// run had already stopped for one of those. Its <see cref="CoSimSessionRefusedException.Stage"/> is
    /// <see cref="CoSimSessionStage.PreRoll"/> for a tick rendered before the window opens and
    /// <see cref="CoSimSessionStage.Window"/> from then on, its
    /// <see cref="CoSimSessionRefusedException.Cause"/> says which side failed, and the report's
    /// <see cref="CoSimRunReport.Stopped"/> records it with the last frame whose truth holds. The caller
    /// disposes the session, which gives everything back.
    /// </exception>
    public bool Advance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Report.Stopped is { } stopped)
        {
            // One side has already failed, and neither may be advanced without the other: a world
            // ticked again after SUMO has gone renders a frozen pose buffer as though every vehicle
            // stood still.
            throw new CoSimSessionRefusedException(
                stopped.Stage,
                $"The run has already stopped ({stopped.Cause.Code()}), and a stopped run is not "
                + "advanced again: dispose the session to give the world back.")
            {
                Cause = stopped.Cause,
            };
        }

        try
        {
            return AdvanceOneStep();
        }
        catch (CoSimSessionRefusedException refused)
        {
            refused.Stage = StageOfTheTickBeingRendered();
            throw Stop(refused);
        }
        catch (Exception failed) when (failed is FatalTraCIError or TraCIException)
        {
            // A SUMO that closed the connection is exiting, and what it said about why is still on its
            // way through the pipe; one that is hung, or only refused a command, is not exiting.
            if (failed is FatalTraCIError && !_sumo.TraCI.StoppedAnswering)
            {
                _sumo.WaitForExit(SumoLastWordsBound);
            }

            throw Stop(new CoSimSessionRefusedException(
                StageOfTheTickBeingRendered(),
                "SUMO failed at simulated "
                + RenderedTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                + $" s: {failed.Message}. {_console.Describe()}. A world that keeps ticking without "
                + "SUMO renders a timeline nothing simulated, so the run stops here; dispose the session "
                + "to give the world back.",
                failed)
            {
                Cause = CoSimStopCause.SumoConnectionLost,
            });
        }
        catch (WorldConnectionLostException lost)
        {
            throw Stop(new CoSimSessionRefusedException(
                StageOfTheTickBeingRendered(),
                "The connection to the CARLA server failed at simulated "
                + RenderedTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                + $" s ({lost.Operation}): {lost.InnerException!.Message}. SUMO stepping on without a "
                + "world to render it produces truth no frame shows, so the run stops here; dispose the "
                + "session to give back what can still be reached.",
                lost.InnerException)
            {
                Cause = CoSimStopCause.WorldConnectionLost,
            });
        }
    }

    /// <summary>
    /// Record that the run stopped, on the refusal that stopped it, and answer the refusal.
    /// </summary>
    private CoSimSessionRefusedException Stop(CoSimSessionRefusedException refused)
    {
        Report.Stopped ??= new CoSimRunStop(refused.Stage, refused.Cause, _lastCompleteSeconds,
                                            Report.Ticks, refused.Message);
        return refused;
    }

    /// <summary>
    /// <see cref="CoSimSessionStage.PreRoll"/> while the session is rendering the prewarm, before the
    /// window's opening instant; <see cref="CoSimSessionStage.Window"/> from that instant on.
    /// </summary>
    /// <remarks>
    /// A prewarm frame is rendered and never captured as the window, so a run that stops during the
    /// prewarm stopped before its window opened -- the same outcome as a refusal before the first
    /// tick, and it says so.
    /// </remarks>
    private CoSimSessionStage StageOfTheTickBeingRendered() =>
        RenderedTimeSeconds < WindowOpensAtSeconds - WindowOpenTolerance
            ? CoSimSessionStage.PreRoll
            : CoSimSessionStage.Window;

    private bool AdvanceOneStep()
    {
        for (int tick = 0; tick < Clock.WorldTicksPerSumoStep; tick++)
        {
            _bridgeClock.Start();
            double fraction = Clock.InterpolationFraction(tick);
            ComputePoses(fraction);
            WriteTheBatch();
            ApplyTheDrawDistance();
            NameTheRenderSet();
            NameTheSupervision();
            WriteTheSun();
            _bridgeClock.Stop();

            // The server is held in its RPC drain until the cue arrives, with this tick's batch
            // already applied, so waiting here holds the frame and nothing else.
            _pacer.BeforeTickCue(RenderedTimeSeconds);
            if (_tickWorld() is not { } frame)
            {
                throw new CoSimSessionRefusedException(
                    $"The CARLA world produced no frame for tick {_tickIndex}. A world that stops "
                    + "ticking while SUMO keeps stepping renders a timeline nothing simulated.")
                {
                    Cause = CoSimStopCause.WorldTickTimeout,
                };
            }

            _lastFrame = frame;
            RecordTheRenderSet(frame);
            AuditTheSun(frame);
            MeasureDivergence();
            _lastCompleteSeconds = RenderedTimeSeconds;
            _lastCompleteFrame = frame;
            PublishTheRenderedFrame(frame);
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
        Attempt(failures, "close the rendered intervals", () => _renderSet.CloseAll(RenderedTimeSeconds));
        Attempt(failures, "close the collisions still in progress", CloseEveryCollision);
        Attempt(failures, "complete the report", () =>
        {
            Report.Admissions = _renderSet.Admissions;
            Report.CapacityDeclines = _renderSet.CapacityDeclines;
            Report.VehiclePassesOutsideThePolicy = _renderSet.VehiclePassesOutsideThePolicy;
            foreach (UnrenderableReason reason in _binder.RefusedTypes.Values)
            {
                Report.CountRefusedType(reason);
            }

            Report.BridgeSecondsOnTicks = _bridgeClock.Elapsed.TotalSeconds;
            Report.SumoSecondsOnSteps = _sumoClock.Elapsed.TotalSeconds;
            if (_pool is { } counted)
            {
                Report.BodiesSpawned = counted.Bodies.Count;
            }
        });

        // Before SUMO is stopped, so an observer closing what it holds can still ask SUMO about it; each
        // told whatever the one before it did.
        var end = new SessionEndRecord(_frameSeconds, _lastCompleteSeconds, _lastCompleteFrame,
                                       _scenarioFinished, Report.Stopped);
        foreach (ISumoStepObserver observer in _observers)
        {
            Attempt(failures, $"tell an observer ({observer.GetType().Name}) the session has ended",
                    () => observer.OnSessionEnded(end));
        }

        // Closed already where it was told the end; closed here where telling it failed.
        Attempt(failures, "close the world truth track", () => _track?.Dispose());
        Attempt(failures, "close the run manifest", () => _manifest?.Dispose());
        Attempt(failures, "give back the world's sun", () => _sun?.Dispose());
        // Before the bodies go, though they take their own supervision with them: the plan and the
        // absences are held for the world, and outlive every body.
        Attempt(failures, "withdraw the supervision put to the server", WithdrawTheSupervisionAtTheEnd);
        Attempt(failures, "destroy the bodies the session spawned", () => _pool?.DestroyAll());
        Attempt(failures, "draw the rendering layers again", () => _layers?.Dispose());
        Attempt(failures, "give back the world's settings", () => _settings?.Dispose());
        // The drive lease once the world is back as it was found, so nothing another traffic system
        // writes in the gap can reach a world still carrying this run's bodies or clock.
        Attempt(failures, "give back the world's drive lease", () => _drive?.Dispose());
        Attempt(failures, "give back the population lease", _lease.Dispose);
        Attempt(failures, "stop SUMO", _sumo.Dispose);

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
    /// <remarks>
    /// <para>Every vehicle SUMO has at the fast-forward's frame is subscribed there and delivers its state
    /// on that frame, so each has both frames and is drawn on the first rendered frame. A vehicle SUMO
    /// inserts in the step of lookahead after it has only the later frame, and is drawn from that one,
    /// as any vehicle SUMO inserts later is.</para>
    ///
    /// <para>The simulation domain is subscribed first, with one round trip: from then on every step's
    /// answer carries its own clock, the departures, arrivals and insertion queue, and the step's event
    /// lists, so neither the fast-forward nor a step of the run asks for any of them separately.</para>
    /// </remarks>
    private void Prime()
    {
        _simulation.Subscribe();
        while (_simulation.Time < _options.WarmUpToSimulatedSecond)
        {
            _sumo.Step();
        }

        _population.Seed(_sumo.Vehicles.Ids);
        _frameSeconds = _simulation.Time;
        IReadOnlyList<string> departed = ReconcileAndRead();

        // The queue as the fast-forward left it: what SUMO gives up on from here is noticed against it.
        // Whatever it gave up on during the fast-forward was before the first frame, and no frame of
        // this session could have held it.
        NoticeTheInsertionQueue(departed, seeding: true);
        RenderedTimeSeconds = _frameSeconds;
        FirstRenderedSeconds = RenderedTimeSeconds;
        WindowOpensAtSeconds = _options.WindowOpensAtSimulatedSecond ?? RenderedTimeSeconds;
        Report.FirstRenderedSeconds = FirstRenderedSeconds;
        Report.WindowOpensAtSeconds = WindowOpensAtSeconds;
        PublishTheSumoStep(afterFastForward: true);
        AdvanceSumo();
    }

    /// <summary>
    /// Bind the world's sun to the civil instant the window opens.
    /// </summary>
    /// <remarks>
    /// <para>Here, and nowhere earlier: SUMO has been fast-forwarded, so the instant of the first
    /// rendered frame is known, and the world has not yet ticked, so no frame has been rendered under
    /// whatever sun it was holding. Nothing ticks the world during the fast-forward, so nothing could
    /// have moved the sun in between either.</para>
    ///
    /// <para>The instant is the window's opening, which a prewarm renders up to: a sun frozen at the
    /// window's start is pinned there and holds through the prewarm, and an advancing sun is anchored
    /// there and written for every prewarm frame at that frame's own instant. With no prewarm the
    /// window opens at the first rendered frame.</para>
    /// </remarks>
    private void BindTheSun()
    {
        if (_world is not { } world || _options.Illumination is not { } policy)
        {
            return;
        }

        if (!policy.BindsTheSun)
        {
            // Left alone, but not unexamined: a run that requires a sun and has none is lit by
            // nothing anyone declared, whichever policy it runs under.
            if (policy.RequireSun && SolarReading.From(world.ReadSolarState()) is null)
            {
                throw new CoSimSessionRefusedException(
                    "The world reports no sun, and the run requires one. Under the 'ignore' policy "
                    + "the session leaves the sun alone, but a world with no CesiumSunSky is lit by "
                    + "nothing anyone declared; declare that the run does not require a sun if "
                    + "that is the intent.");
            }

            return;
        }

        _sun = SolarLease.Take(world, new DeclaredSun(_options.Epoch!, policy, WindowOpensAtSeconds));
        Report.Sun = _sun;
        if (_sun.AtWindowOpen is { } opened)
        {
            // The first frame's headlights follow the sun the world reported once it was bound.
            _reportedSunElevation = opened.ElevationDegrees;
            Report.Headlights = _headlights;
            // The one comparison that sees the refraction-corrected elevation whatever the server's
            // observer header carries, taken before anything is rendered under it.
            _sunAudit = new SolarAudit(_sun.Declared, _origin.Latitude, _origin.Longitude);
            Report.SunAudit = _sunAudit;
            _sunAudit.AuditWindowOpen(opened);
        }
    }

    /// <summary>
    /// Keep the render set of the frame this tick produced, for a recorder to list beside the capture
    /// of that frame.
    /// </summary>
    /// <remarks>
    /// <para>Read from the pool as the tick left it. Every body given back since the last tick was
    /// parked at the head of this tick's batch and every body lent was posed in it, so the bodies held
    /// now are exactly the bodies the frame drew -- including one whose pose was not written this tick,
    /// which stands where it was last put and is drawn there.</para>
    ///
    /// <para>Built again only when a body has been lent or given back since the last frame, so a
    /// frame whose lending did not change shares its set with the frame before. Recorded before the
    /// sun's audit, which can stop the run, because the frame may already be on its way to a
    /// recorder.</para>
    /// </remarks>
    private void RecordTheRenderSet(ulong frame)
    {
        if (_pool is not { } pool)
        {
            return;
        }

        if (_renderSetNow is null)
        {
            var vehicles = new List<RenderedVehicle>(pool.HeldBodies);
            foreach ((string vehicleId, PooledBody body) in pool.Held)
            {
                (string typeId, ulong? admitted) = _renderedSpans.TryGetValue(vehicleId, out var span)
                    ? span
                    : (string.Empty, null);

                // The first frame a vehicle's body is drawn for it opens its rendered span.
                ulong since = admitted ?? frame;
                if (admitted is null)
                {
                    _renderedSpans[vehicleId] = (typeId, since);
                }

                vehicles.Add(new RenderedVehicle(body.Actor, vehicleId, typeId, since));
            }

            _renderSetNow = new RenderSet(vehicles);
        }

        // The membership changes only when a body is lent or given back; SUMO's angle changes every
        // frame, so each frame's set carries this tick's, and the draw distance the frame was drawn
        // under, which a recorder marks every vehicle beyond in its camera's sidecar against.
        _renderSets.Record(frame, new RenderSet(_renderSetNow.ByActor.Values.Select(rendered =>
            _sumoAngles.TryGetValue(rendered.SumoId, out double angle)
                ? rendered with { SumoAngleDegrees = angle }
                : rendered))
        {
            DrawDistanceMetres = _drawDistanceApplied,
        });
    }

    /// <summary>
    /// An angle a fraction of the way from one to another, the shorter way round, degrees clockwise from
    /// north: SUMO's reported angle between two frames.
    /// </summary>
    private static double AngleBetween(double fromDegrees, double toDegrees, double fraction)
    {
        double turn = Math.IEEERemainder(toDegrees - fromDegrees, 360.0);
        double angle = (fromDegrees + (turn * fraction)) % 360.0;
        return angle < 0.0 ? angle + 360.0 : angle;
    }

    /// <summary>
    /// Name to the server every body lent and every body given back since the last change it was
    /// told of, so the snapshot of the frame this tick produces carries the render set that frame
    /// draws.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the server has to be told.</b> The render set kept for the recorder is in this
    /// process, and every other reader of the world -- the live pull, the CoT feed, a recorder in
    /// another process -- sees only the world's vehicle actors, a parked body among them standing 300 m
    /// below the ground with no SUMO vehicle to its name. Told, the server carries the set on every
    /// world-observer snapshot, and every one of those readers lists the bodies a frame drew, each named
    /// by its vehicle, and no parked body.</para>
    ///
    /// <para><b>The same set as the recorder's, frame for frame.</b> Read from the pool after this
    /// tick's batch, as <see cref="RecordTheRenderSet"/> reads it once the tick returns, and nothing
    /// lends or takes back a body in between; named before the tick cue, so the server applies it in
    /// the drain the frame is cued from and the frame's snapshot is the first to carry it. A body given
    /// back since the last tick is named parked; one lent is named with its vehicle, including one
    /// handed straight from one vehicle to the next.</para>
    ///
    /// <para><b>Only on a change.</b> Nothing is sent on a tick whose lending did not change, which
    /// <see cref="_renderSetNow"/> already says: it is cleared exactly when a body is lent or given
    /// back. A server that refuses the first change is told nothing more, and the report says why.</para>
    /// </remarks>
    private void NameTheRenderSet()
    {
        if (_world is not { } world || _pool is not { } pool || _renderSetNow is not null
            || Report.RenderSetRefused is not null)
        {
            return;
        }

        _lentSinceNamed.Clear();
        _parkedSinceNamed.Clear();
        _heldNow.Clear();
        foreach ((string vehicleId, PooledBody body) in pool.Held)
        {
            _heldNow.Add(body.Actor);
            if (!_namedToServer.TryGetValue(body.Actor, out string? named) || named != vehicleId)
            {
                string typeId = _renderedSpans.TryGetValue(vehicleId, out var span) ? span.TypeId : string.Empty;
                _lentSinceNamed.Add(new LentBody(body.Actor, vehicleId, typeId));
            }
        }

        foreach (ActorId actor in _namedToServer.Keys)
        {
            if (!_heldNow.Contains(actor))
            {
                _parkedSinceNamed.Add(actor);
            }
        }

        if (_lentSinceNamed.Count == 0 && _parkedSinceNamed.Count == 0)
        {
            return;
        }

        RenderSetWrite written = world.WriteRenderSet(_lentSinceNamed, _parkedSinceNamed);
        if (!written.Taken)
        {
            Report.RenderSetRefused = written.Refusal;
            return;
        }

        foreach (ActorId actor in _parkedSinceNamed)
        {
            _namedToServer.Remove(actor);
        }

        foreach (LentBody lent in _lentSinceNamed)
        {
            _namedToServer[lent.Actor] = lent.VehicleId;
        }

        Report.RenderSetUpdates++;
        Report.RenderSetBodiesNotFound += Math.Max(0, _lentSinceNamed.Count + _parkedSinceNamed.Count - written.BodiesFound);
    }

    /// <summary>
    /// Put to the server every change to the supervision in force since the last it was told of, onto
    /// the bodies drawing the vehicles it is about, so the snapshot of the frame this tick produces
    /// carries the supervision in force on that frame.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the server, and only the server.</b> Supervision held in this process would be one
    /// client's truth; every other reader of the world -- a recorder in another process, the live CoT
    /// feed -- would have none, or its own. Put to the server, it rides on every world-observer snapshot
    /// beside the render set, and every reader, a recorder beside the session included, reads the same
    /// truth for the same frame.</para>
    ///
    /// <para><b>Onto the bodies, after the render set.</b> The binder states supervision per SUMO vehicle
    /// (<see cref="Supervision"/>); the server holds it per body, and drops a body's when the body is given
    /// back or handed to another vehicle. So this runs after <see cref="NameTheRenderSet"/> in the same
    /// drain: the bodies whose supervision the render set change dropped are forgotten here, and a
    /// supervised vehicle lent a body anew has its supervision named for the new body. A vehicle no body
    /// draws, or whose body the server was not told of, is named nothing, because no frame shows it.</para>
    ///
    /// <para><b>Only on a change.</b> Nothing is sent on a tick whose lending did not change and whose
    /// supervision did not either -- nearly every tick, since an interval opens or closes a handful of
    /// times in a run -- and an unlabelled vehicle is named only to clear a body that carried something.
    /// The first change after a plan is bound starts the server afresh. A server that refuses a change, or
    /// refused the render set, is told nothing more, and the report says why.</para>
    /// </remarks>
    private void NameTheSupervision()
    {
        if (_world is not { } world || _pool is not { } pool
            || Report.SupervisionRefused is not null || Report.RenderSetRefused is not null)
        {
            return;
        }

        SupervisionPlanIdentity? plan = Supervision.Plan;
        if (plan is null)
        {
            if (_supervisionPlanNamed is not null)
            {
                WithdrawTheSupervision(world);
            }

            return;
        }

        bool lendingChanged = _renderSetNow is null;
        bool fresh = !plan.Equals(_supervisionPlanNamed);
        if (!fresh && !lendingChanged && Supervision.Revision == _supervisionRevisionNamed)
        {
            return;
        }

        if (fresh)
        {
            _supervisionNamed.Clear();
            _absencesNamed.Clear();
        }
        else if (lendingChanged)
        {
            // The server dropped the supervision of every body given back or handed to another vehicle
            // when it was told so, a moment ago in this drain.
            _supervisionDropped.Clear();
            foreach ((ActorId actor, (string vehicleId, _)) in _supervisionNamed)
            {
                if (!_namedToServer.TryGetValue(actor, out string? drawing) || drawing != vehicleId)
                {
                    _supervisionDropped.Add(actor);
                }
            }

            foreach (ActorId actor in _supervisionDropped)
            {
                _supervisionNamed.Remove(actor);
            }
        }

        _supervisionSinceNamed.Clear();
        foreach ((string vehicleId, PooledBody body) in pool.Held)
        {
            if (!_namedToServer.TryGetValue(body.Actor, out string? named) || named != vehicleId)
            {
                continue;
            }

            SupervisionInForce wanted = Supervision.Of(vehicleId);
            SupervisionInForce held = _supervisionNamed.TryGetValue(body.Actor, out var carried)
                ? carried.Supervision
                : SupervisionInForce.Unlabelled;
            if (!wanted.Equals(held))
            {
                _supervisionSinceNamed.Add(new BodySupervision(body.Actor, wanted));
            }
        }

        _absencesOpenedSinceNamed.Clear();
        _absencesClosedSinceNamed.Clear();
        foreach (AbsenceInForce absence in Supervision.Absences)
        {
            if (!_absencesNamed.TryGetValue(absence.InstanceId, out AbsenceInForce? named) || !named.Equals(absence))
            {
                _absencesOpenedSinceNamed.Add(absence);
            }
        }

        foreach (string instanceId in _absencesNamed.Keys)
        {
            if (!Supervision.IsOpen(instanceId))
            {
                _absencesClosedSinceNamed.Add(instanceId);
            }
        }

        if (!fresh && _supervisionSinceNamed.Count == 0 && _absencesOpenedSinceNamed.Count == 0
            && _absencesClosedSinceNamed.Count == 0)
        {
            _supervisionRevisionNamed = Supervision.Revision;
            return;
        }

        SupervisionWrite written = world.WriteSupervision(new SupervisionChange(
            fresh, plan, _supervisionSinceNamed, _absencesOpenedSinceNamed, _absencesClosedSinceNamed));
        if (!written.Taken)
        {
            Report.SupervisionRefused = written.Refusal;
            return;
        }

        _supervisionPlanNamed = plan;
        foreach (BodySupervision body in _supervisionSinceNamed)
        {
            if (body.Supervision.State == SupervisionState.Unlabelled)
            {
                _supervisionNamed.Remove(body.Actor);
            }
            else
            {
                _supervisionNamed[body.Actor] = (_namedToServer[body.Actor], body.Supervision);
            }
        }

        foreach (string instanceId in _absencesClosedSinceNamed)
        {
            _absencesNamed.Remove(instanceId);
        }

        foreach (AbsenceInForce absence in _absencesOpenedSinceNamed)
        {
            _absencesNamed[absence.InstanceId] = absence;
        }

        _supervisionRevisionNamed = Supervision.Revision;
        Report.SupervisionUpdates++;
        Report.SupervisionBodiesNotApplied += Math.Max(0, _supervisionSinceNamed.Count - written.BodiesApplied);
    }

    /// <summary>
    /// Withdraw everything put to the server, so the world carries no supervision from the next frame on.
    /// </summary>
    private void WithdrawTheSupervision(ICarlaWorld world)
    {
        SupervisionWrite written = world.WriteSupervision(SupervisionChange.Withdrawal);
        if (!written.Taken)
        {
            Report.SupervisionRefused = written.Refusal;
            return;
        }

        _supervisionPlanNamed = null;
        _supervisionNamed.Clear();
        _absencesNamed.Clear();
        _supervisionRevisionNamed = -1;
        Report.SupervisionUpdates++;
    }

    /// <summary>
    /// Withdraw what the session put to the server, as it ends: the bodies take their own supervision
    /// with them when they are destroyed, but the plan and the absences are the world's.
    /// </summary>
    private void WithdrawTheSupervisionAtTheEnd()
    {
        if (_world is { } world && _supervisionPlanNamed is not null && Report.SupervisionRefused is null)
        {
            WithdrawTheSupervision(world);
        }
    }

    /// <summary>
    /// Set the draw distance on every body the pool has spawned since the last tick, before the cue
    /// of the tick it is first drawn in.
    /// </summary>
    /// <remarks>
    /// <para>A body is spawned during the tick's poses, parked or lent, and set here in the same
    /// drain, so no frame draws it unset. One round trip for every body spawned since the last, and
    /// none on a tick that spawned none, which in a steady scene is nearly every tick: a pooled body
    /// keeps the distance across every vehicle it is lent to.</para>
    ///
    /// <para>Nothing is sent where no distance was asked for, which leaves every body exactly as it
    /// is spawned, drawn at any range. A server that refuses the first is sent nothing more, and the
    /// report says why.</para>
    /// </remarks>
    private void ApplyTheDrawDistance()
    {
        if (_world is not { } world || _pool is not { } pool || _drawDistanceAsked is not { } metres
            || Report.DrawDistanceRefused is not null || pool.Bodies.Count == _bodiesGivenTheDrawDistance)
        {
            return;
        }

        _drawDistanceBodies.Clear();
        for (int index = _bodiesGivenTheDrawDistance; index < pool.Bodies.Count; index++)
        {
            _drawDistanceBodies.Add(pool.Bodies[index].Actor);
        }

        if (WriteTheDrawDistance(world, metres))
        {
            _drawDistanceApplied = metres;
            _bodiesGivenTheDrawDistance = pool.Bodies.Count;
        }
    }

    /// <summary>
    /// Send the draw distance for the bodies gathered, counting what the server made of it; answer
    /// whether it took it.
    /// </summary>
    private bool WriteTheDrawDistance(ICarlaWorld world, double metres)
    {
        DrawDistanceWrite written = world.WriteDrawDistance(_drawDistanceBodies, metres);
        if (!written.Taken)
        {
            Report.DrawDistanceRefused = written.Refusal;
            return false;
        }

        Report.DrawDistanceWrites++;
        Report.DrawDistanceBodiesNotFound += Math.Max(0, _drawDistanceBodies.Count - written.BodiesFound);
        return true;
    }

    /// <summary>
    /// Write the sun for the frame this tick renders, under a policy that advances it.
    /// </summary>
    /// <remarks>
    /// <para>Every tick, including the first, whose sun the binding already wrote: the write is the
    /// whole of the sun's state, so a frame's sun never depends on what an earlier write left. It is
    /// one <c>set_solar_epoch</c> round trip, measured at a median of 0.13 ms from the server's drain;
    /// no batch command sets the sun.</para>
    ///
    /// <para>The engine's own advance stays off. Its clock passes through the last half-second of
    /// every minute, where the engine's clock decomposition drops the minute, whereas the written clock
    /// is always a millisecond past the whole second nearest the frame's declared instant. Under a
    /// freeze this writes nothing: the window-open write holds for the whole window.</para>
    /// </remarks>
    private void WriteTheSun() => _sun?.WriteForFrame(RenderedTimeSeconds);

    /// <summary>
    /// Compare the sun this tick's snapshot carried against the sun declared for the instant it
    /// rendered, failing the run on a disagreement.
    /// </summary>
    /// <remarks>
    /// Free: the snapshot already arrived with the tick. Taken on every tick rather than only those a
    /// recorder keeps, so a disagreement is caught on the tick it starts rather than at the next
    /// capture, and never corrected: rewriting the sun would hide whichever of a wrong mapping or a
    /// second writer caused it.
    /// </remarks>
    private void AuditTheSun(ulong frame)
    {
        _sunReadThisTick = null;
        if (_world is not { } world || _options.Illumination is not { } policy)
        {
            return;
        }

        SolarAuditSample? sample = null;
        try
        {
            if (_sunAudit is { } audit)
            {
                sample = audit.AuditTick(_tickIndex, RenderedTimeSeconds, world.ObservedSolarState());
                _reportedSunElevation = sample.Observed.ElevationDegrees;
                _sunReadThisTick = sample.Observed;
            }
        }
        catch (SolarAuditFailedException failed)
        {
            // The frame this tick rendered may already be on its way to a recorder, and it should
            // say what it was measured against rather than arrive with nothing.
            _illumination.Record(frame, Declare(policy, failed.Sample));
            throw;
        }

        _illumination.Record(frame, Declare(policy, sample));
    }

    /// <summary>
    /// What a frame's illumination was declared to be, and the audit's residual on its tick.
    /// </summary>
    private IlluminationDeclaration Declare(IlluminationPolicy policy, SolarAuditSample? sample)
    {
        SolarEpoch? epoch = _options.Epoch;
        DateTimeOffset? civil = epoch?.CivilInstantAt(RenderedTimeSeconds);
        var declared = new IlluminationDeclaration(
            policy.Name, policy.HonoursTheEpoch && _sun is { NoSun: false }, sample is not null)
        {
            Rate = policy.Advances ? policy.Rate : null,
            FreezeAtCivilTime = policy.FreezeAtCivilTimeOfDay?.ToString("hh\\:mm\\:ss",
                                                                        System.Globalization.CultureInfo.InvariantCulture),
            EpochDigest = epoch?.Digest,
            EpochCivil = epoch?.CivilDateTimeText,
            UtcOffsetHours = epoch?.UtcOffsetHours,
            DeclaredCivil = civil is { } local ? SolarEpoch.FormatCivil(local) : null,
            DeclaredUtc = civil is { } instant ? SolarEpoch.FormatUtc(instant) : null,
        };

        return sample is not { } measured || epoch is null
            ? declared
            : declared with
            {
                SunDeclared = SolarEpoch.FormatCivil(new DateTimeOffset(measured.DeclaredSun, epoch.UtcOffset)),
                SunElevationDeclaredDegrees = measured.Modelled.ElevationDegrees,
                SunCorrectedElevationDeclaredDegrees = measured.Modelled.CorrectedElevationDegrees,
                DeclaredElevationKind = DeclaredSunElevation.Name,
                ResidualClockSeconds = measured.ClockResidualSeconds,
                ResidualDegrees = measured.AngleResidualDegrees,
                ResidualCorrectedDegrees = measured.CorrectedResidualDegrees,
            };
    }

    private bool AdvanceSumo()
    {
        _sumoClock.Start();
        _sumo.Step();
        Report.SumoSteps++;
        int remaining = _simulation.ExpectedVehicleCount;
        _sumoClock.Stop();

        double waitingAt = _frameSeconds;
        _frameSeconds = _simulation.Time;
        CopyFrames(_next, _previous);
        IReadOnlyList<string> departed = ReconcileAndRead();
        NoticeTheInsertionQueue(departed, seeding: false, waitingAt);
        MeasureLaneGeometry();
        _scenarioFinished = remaining <= 0;
        PublishTheSumoStep(afterFastForward: false);
        return remaining > 0;
    }

    /// <summary>
    /// Read the SUMO frame just stepped to: what happened to SUMO's vehicles in the step, who departed and
    /// arrived, every vehicle's state, the render set that follows, and the collisions SUMO registered.
    /// Answers the departures, which the insertion queue is compared against.
    /// </summary>
    /// <remarks>
    /// The event lists and the clock come from the step's own answer, through the simulation domain's
    /// subscription, so they cost nothing here, and so does whether a collision began. The collision list
    /// is one round trip, because SUMO writes it as a compound only a decoder of its own reads, and it is
    /// asked for only on a step where a collision began or one is still going on.
    /// </remarks>
    private IReadOnlyList<string> ReconcileAndRead()
    {
        _releasedThisFrame.Clear();
        _events = _simulation.ReadEvents();
        IReadOnlyList<string> departed = _events.Departed;
        _population.Reconcile(departed, _events.Arrived);
        _population.ReadFrames(_next);
        BeginThePass();
        _renderSet.ReconcileRenderSet(_frameSeconds, _next, _population.LastVanished);
        Report.Admissions = _renderSet.Admissions;
        Report.CapacityDeclines = _renderSet.CapacityDeclines;
        Report.VehiclePassesOutsideThePolicy = _renderSet.VehiclePassesOutsideThePolicy;
        PublishTheAdmissionPass();
        RecordCollisions();
        RecordTheStepEvents();
        return departed;
    }

    /// <summary>
    /// Count on the report the step's events that change the behaviour being captured -- the emergency
    /// stops, and the teleports a run that accepted teleporting lets SUMO make -- each at TraCI's clock for
    /// the step that listed it.
    /// </summary>
    private void RecordTheStepEvents()
    {
        foreach (string vehicleId in _events.EmergencyStops)
        {
            Report.AddEmergencyStop(new SumoVehicleEvent(vehicleId, _events.TimeSeconds));
        }

        foreach (string vehicleId in _events.TeleportsStarted)
        {
            Report.AddTeleport(new SumoVehicleEvent(vehicleId, _events.TimeSeconds));
        }
    }

    /// <summary>
    /// Tell every observer of the SUMO frame just read, once the session has made what it makes of it:
    /// the step's events at TraCI's clock, the collisions, the vehicles SUMO gave up inserting, every
    /// vehicle's state and the render set.
    /// </summary>
    /// <param name="afterFastForward">Whether the frame is the one SUMO was fast-forwarded to.</param>
    private void PublishTheSumoStep(bool afterFastForward)
    {
        if (_observers.Length == 0)
        {
            return;
        }

        var step = new SumoStepRecord(_tickIndex, _frameSeconds, afterFastForward, _events, _collisionsThisFrame,
                                      [.. _notInsertedThisFrame], _population.LastVanished, _next,
                                      _renderSet.RenderedVehicleIds, Report.LastAdmissionPass!, _vehicleQueries)
        {
            Released = [.. _releasedThisFrame],
        };
        foreach (ISumoStepObserver observer in _observers)
        {
            observer.OnSumoStep(step);
        }
    }

    /// <summary>
    /// Tell every observer of the frame this tick produced, once the tick is complete: its render set and
    /// its illumination as a recorder is answered with them.
    /// </summary>
    private void PublishTheRenderedFrame(ulong frame)
    {
        if (_observers.Length == 0)
        {
            return;
        }

        var rendered = new RenderedFrameRecord(
            frame,
            _tickIndex,
            RenderedTimeSeconds,
            Clock.IsCaptureTick(_tickIndex),
            StageOfTheTickBeingRendered() == CoSimSessionStage.Window,
            _renderSets.TryGetRenderSet(frame, out RenderSet set) ? set : null,
            _illumination.TryGetDeclaration(frame, out IlluminationDeclaration declared) ? declared : null)
        {
            Sun = _sunReadThisTick,
            AppliedPoses = _appliedPoses,
        };
        foreach (ISumoStepObserver observer in _observers)
        {
            observer.OnFrameRendered(rendered);
        }
    }

    /// <summary>
    /// Tell the render-set policy what the session holds for the pass about to be made: the frame the
    /// pass decides, SUMO's step, the seed, the bodies' reach and every registered camera's pose on the
    /// last frame rendered.
    /// </summary>
    /// <remarks>
    /// <para>A camera's pose comes from the client's snapshot of that frame, a read with no round trip.
    /// Before the first tick there is no frame, and the newest snapshot is read. A registered camera the
    /// snapshot does not hold -- destroyed without being removed -- is left out and counted: it has no
    /// view to follow. Under every policy but the cameras' this is all read and nothing in it decides
    /// anything, and with no camera registered nothing is read at all.</para>
    ///
    /// <para>What the policy made of the pass -- the rule in force and every camera's footprint and
    /// range cap -- goes on the report as it is made.</para>
    /// </remarks>
    private void BeginThePass()
    {
        _cameraViews.Clear();
        if (_world is { } world)
        {
            foreach ((ActorId camera, CameraOptics optics) in _cameras)
            {
                Transform? pose = _lastFrame is { } frame
                    ? world.ObservedTransformAt(camera, frame)
                    : world.ObservedTransform(camera);
                if (pose is { } seen)
                {
                    _cameraViews.Add(new CameraView(camera, seen, optics));
                }
                else
                {
                    Report.CameraPosesUnread++;
                }
            }
        }

        _renderSet.BeginPass(new RenderSetPass(_frameSeconds, Clock.SumoStepSeconds, _seed, _longestBody,
                                               _bodyReach, [.. _cameraViews], GroundHeightAt));
        IRenderSetPolicy policy = _renderSet.Policy;
        Report.CountPass(policy.ActiveRule);
        foreach (CameraFootprint footprint in policy.Footprints)
        {
            Report.RecordFootprint(footprint);
        }
    }

    /// <summary>The ground surface's CARLA-local height under a CARLA-frame position, or null off the grid.</summary>
    private double? GroundHeightAt(double carlaX, double carlaY) =>
        _ground.Sample(carlaX, carlaY) is { } surface ? surface - _ground.OriginHeightMetres : null;

    /// <summary>
    /// The seed the scenario's configuration runs SUMO under, or SUMO's own default where it declares
    /// none.
    /// </summary>
    /// <remarks>
    /// Read from the configuration SUMO is about to be started on, the way SUMO reads it, so the seed the
    /// report names is the one the traffic is simulated under.
    /// </remarks>
    private static long ReadTheSeed(string scenarioPath)
    {
        IReadOnlyList<string> declared = SumoConfiguration
            .Load(scenarioPath, "the seed SUMO runs under cannot be read")
            .ValuesOf("seed");
        return declared.Count > 0
               && long.TryParse(declared[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed)
            ? seed
            : SumoDefaultSeed;
    }

    /// <summary>
    /// Open a span for every collision SUMO reports for the first time, and close and hand out every
    /// one it no longer reports.
    /// </summary>
    /// <remarks>
    /// <para>A collision is a fact about the corpus, not a failure of the run, so nothing here stops
    /// anything: the run goes on as SUMO simulates it, and the span is what a consumer filters on.
    /// SUMO reports a collision again on each step its two vehicles stay in contact, keeping the roles
    /// it first gave them, so a pair seen before is the same collision still going on.</para>
    ///
    /// <para><b>The list is asked for only where it can hold something.</b> SUMO's list is one round trip;
    /// whether a collision began arrives with the step's own answer, as SUMO's count of the vehicles one
    /// began for. SUMO keeps a collision in its list on every later step its vehicles stay in contact
    /// without counting them again, and drops it at the end of the first step they are not, so on a step
    /// where none began and none the session holds is still open the list is empty, and it is not asked
    /// for. It is asked for on the first frame read whatever the count, since a collision that began in
    /// the fast-forward may still be going on. The session refuses a configuration under which SUMO skips
    /// the check, so a run that has started always has the record (<see cref="SumoDistributionEditCheck"/>).</para>
    /// </remarks>
    private void RecordCollisions()
    {
        bool mayHoldOne = !_collisionListRead || _simulation.CollidingVehicleCount > 0 || _collisions.Count > 0;
        if (!mayHoldOne)
        {
            _collisionsThisFrame = [];
            return;
        }

        _collisionListRead = true;
        Report.CollisionListReads++;
        _collisionsReported.Clear();
        _collisionsThisFrame = _sumo.Simulation.Collisions;
        foreach (SumoCollision collision in _collisionsThisFrame)
        {
            (string Collider, string Victim) pair = (collision.ColliderId, collision.VictimId);
            _collisionsReported.Add(pair);
            if (_collisions.TryGetValue(pair, out CollisionSpan open))
            {
                // A vehicle SUMO inserted on the step its collision began is given its body only once
                // the rendered clock reaches the frame it was inserted at, so a body not yet named is
                // named once the vehicle holds one.
                if (open.ColliderActor == 0 || open.VictimActor == 0)
                {
                    _collisions[pair] = open with
                    {
                        ColliderActor = open.ColliderActor != 0 ? open.ColliderActor : BodyOf(pair.Collider),
                        VictimActor = open.VictimActor != 0 ? open.VictimActor : BodyOf(pair.Victim),
                    };
                }

                continue;
            }

            Report.Collisions++;
            _collisions[pair] = new CollisionSpan(collision, _frameSeconds, _frameSeconds,
                                                  BodyOf(collision.ColliderId), BodyOf(collision.VictimId));
        }

        _collisionsOver.Clear();
        foreach ((string Collider, string Victim) pair in _collisions.Keys)
        {
            if (!_collisionsReported.Contains(pair))
            {
                _collisionsOver.Add(pair);
            }
        }

        foreach ((string Collider, string Victim) pair in _collisionsOver)
        {
            CloseCollision(pair, _frameSeconds);
        }
    }

    /// <summary>Close every collision still in progress, which is what the end of a session does to them.</summary>
    private void CloseEveryCollision()
    {
        foreach ((string Collider, string Victim) pair in _collisions.Keys.ToList())
        {
            CloseCollision(pair, _frameSeconds);
        }
    }

    private void CloseCollision((string Collider, string Victim) pair, double endedAtSeconds)
    {
        if (!_collisions.Remove(pair, out CollisionSpan open))
        {
            return;
        }

        CollisionSpan closed = open with { EndedAtSeconds = endedAtSeconds };
        Report.AddCollision(closed);
        _options.OnCollision?.Invoke(closed);
    }

    /// <summary>The body a vehicle holds, or zero where it holds none.</summary>
    private ActorId BodyOf(string vehicleId) =>
        _pool is { } pool && pool.TryGetHeld(vehicleId, out PooledBody body) ? body.Actor : 0;

    /// <summary>
    /// Compare SUMO's insertion queue with the one the previous frame had, and record every vehicle
    /// that left it without departing.
    /// </summary>
    /// <param name="departed">The vehicles SUMO inserted in the step just taken.</param>
    /// <param name="seeding">Take the queue as it stands, with nothing before it to compare against.</param>
    /// <param name="waitingAtSeconds">The simulated second of the previous frame.</param>
    /// <remarks>
    /// SUMO drops a vehicle it could not insert within <c>max-depart-delay</c> without a warning or a
    /// state change, so a vehicle that was waiting and is neither waiting nor departed now is the only
    /// sign of it. The authored population is then smaller than the scenario says, and nothing else in
    /// the run would show it.
    /// </remarks>
    private void NoticeTheInsertionQueue(IReadOnlyList<string> departed, bool seeding,
                                         double waitingAtSeconds = 0.0)
    {
        _stillAwaiting.Clear();
        _notInsertedThisFrame.Clear();
        foreach (string vehicleId in _simulation.PendingVehicleIds)
        {
            _stillAwaiting.Add(vehicleId);
        }

        if (!seeding)
        {
            foreach (string vehicleId in _awaitingInsertion)
            {
                if (_stillAwaiting.Contains(vehicleId) || departed.Contains(vehicleId))
                {
                    continue;
                }

                var dropped = new VehicleNotInserted(vehicleId, waitingAtSeconds, _frameSeconds);
                Report.AddNotInserted(dropped);
                _notInsertedThisFrame.Add(dropped);
                _options.OnVehicleNotInserted?.Invoke(dropped);
            }
        }

        _awaitingInsertion.Clear();
        _awaitingInsertion.UnionWith(_stillAwaiting);
        Report.VehiclesAwaitingInsertion = _awaitingInsertion.Count;
    }

    /// <summary>
    /// Publish the pass just made -- the population, the vehicles admitted and released, and under an
    /// optional limit the eligible, the drawn and the shed -- on the report and to the caller's writer,
    /// as it happens.
    /// </summary>
    /// <remarks>
    /// Once per SUMO step, not per tick: the render set is decided when SUMO's state is read and holds
    /// for the ticks the step is worth, except that a vehicle the pass admits is drawn from the frame
    /// the pass was made for, the first SUMO reports it in. A new record each time, replaced whole, so
    /// a reader between two advances reads one pass.
    /// </remarks>
    private void PublishTheAdmissionPass()
    {
        IRenderSetPolicy policy = _renderSet.Policy;
        var pass = new AdmissionPass(
            _tickIndex,
            _frameSeconds,
            _population.SubscribedVehicleIds.Count,
            _renderSet.LastNewlyAdmitted,
            _renderSet.LastReleased,
            _renderSet.Admissions)
        {
            Eligible = _renderSet.LastEligible,
            Admitted = _renderSet.RenderedVehicleIds.Count,
            Shed = _renderSet.LastShed,
            Held = _renderSet.LastHeld,
            Capacity = policy.Capacity,
            Rule = policy.ActiveRule,
            Cameras = policy.Footprints.Count,
            Limited = policy.Limits,
        };
        Report.LastAdmissionPass = pass;
        _options.OnAdmissionPass?.Invoke(pass);
    }

    private void ComputePoses(double fraction)
    {
        // Every body released since the last tick goes back to its slot at the head of this same
        // batch, ahead of every pose, so a body lent to another vehicle this tick holds that
        // vehicle's pose and velocity once the batch has been applied.
        _batch.Begin();
        _commanded.Clear();
        _appliedPoses.Clear();
        _sumoAngles.Clear();
        VehicleLightStateFlags headlights = HeadlightsForThisTick();

        foreach (string vehicleId in _renderSet.RenderedVehicleIds)
        {
            if (!_next.TryGetValue(vehicleId, out CoSimVehicleFrame to))
            {
                continue;
            }

            // A vehicle with no earlier frame is one SUMO inserted in the step just read: admitted at
            // the frame it first appears in, and drawn from that frame on, when the next step's ticks
            // interpolate from it. The ticks before that frame are SUMO's last step without it, so it
            // holds no body, writes nothing and is in no frame's render set for them.
            if (!_previous.TryGetValue(vehicleId, out CoSimVehicleFrame from))
            {
                continue;
            }

            if (!_binder.TryBind(to.TypeId, out VehicleExtent extent))
            {
                Report.VehicleTicksWithNoMeasuredBody++;
                continue;
            }

            InterpolatedState state = _interpolator.Interpolate(from, to, fraction,
                                                                Clock.SumoStepSeconds);
            Report.CountCase(state.Case);
            if (state.Case == LaneInterpolationCase.Discontinuous)
            {
                Report.SampleDiscontinuity(from, to, _interpolator.RouteDistance(from, to));
            }

            // SUMO's reported angle at this instant, recorded beside the pose; and the body's own
            // heading and velocity, from the path its bumper takes (PathHeading). The bumper stays where
            // SUMO put it: the heading only turns the body about it.
            double sumoAngle = AngleBetween(from.HeadingDegrees, to.HeadingDegrees, fraction);
            _sumoAngles[vehicleId] = sumoAngle;
            PathHeading.Step path = _headings.Advance(
                vehicleId, _tickIndex, state.X, state.Y, state.SpeedMetresPerSecond, Clock.WorldDeltaSeconds,
                PathHeading.RearAxleMetres(extent), sumoAngle,
                restart: state.Case == LaneInterpolationCase.Discontinuous);
            if (path.Held)
            {
                Report.HeadingsHeldAcrossAJump++;
            }

            // The lane the interpolation walked names the road the body is on, and where along it:
            // SUMO's position alone does not, where a deck and the road beneath it share it.
            VehiclePose? pose = _converter.Convert(vehicleId, extent, state.X, state.Y,
                                                   path.HeadingDegrees,
                                                   state.SpeedMetresPerSecond,
                                                   state.LaneId, state.LanePositionMetres,
                                                   path.Velocity);
            if (pose is not { } applied)
            {
                Report.PosesRefusedForMissingGround++;

                // A body already lent to this vehicle stays where its last pose put it, so it is
                // told it is standing still rather than left reporting the speed of a pose it no
                // longer follows.
                if (_pool is { } holding && holding.TryGetHeld(vehicleId, out PooledBody stranded))
                {
                    _batch.HoldStill(stranded.Actor);
                }

                continue;
            }

            Report.PosesComputed++;
            if (applied.SeatHeightWasApproximated)
            {
                Report.PosesOnAnApproximatedSeatHeight++;
            }

            Report.CountSeat(applied);

            Report.WorstBumperResidualMetres = Math.Max(
                Report.WorstBumperResidualMetres,
                BumperResidual(applied, extent, state.X, state.Y));

            ActorId actor = 0;
            VehicleLightStateFlags lamps = VehicleLightStateFlags.None;
            if (_pool is { } pool)
            {
                actor = pool.CheckOut(vehicleId, extent.BlueprintId).Actor;
                if (_renderedSpans.TryAdd(vehicleId, (to.TypeId, null)))
                {
                    // A body newly lent changes the render set from this tick's frame on.
                    _renderSetNow = null;
                }

                _batch.Pose(actor, applied);
                _commanded.Add((vehicleId, actor, applied));
                _appliedPoses.Add(applied);
                lamps = WriteTheLamps(vehicleId, actor, from.Signals, headlights);
            }

            _options.OnPose?.Invoke(new CoSimPoseRecord(
                _tickIndex, RenderedTimeSeconds, Clock.IsCaptureTick(_tickIndex), actor, applied,
                state.Case, state.X, state.Y, sumoAngle, from.Signals, lamps,
                state.LaneId, state.LanePositionMetres));
        }

        if (_pool is { } counted)
        {
            Report.BodiesSpawned = counted.Bodies.Count;
        }
    }

    /// <summary>
    /// The headlights every rendered vehicle shows this tick, from the sun the world last reported;
    /// none where the session drives no lamps or binds no sun.
    /// </summary>
    /// <remarks>
    /// Only a sun the session bound and audits drives them: under the policy that leaves the sun alone
    /// nothing says the sun in the imagery is the declared one, and a lamp rule fed by it would inherit
    /// whatever the world was holding.
    /// </remarks>
    private VehicleLightStateFlags HeadlightsForThisTick() =>
        _headlights is { } rule && _sunAudit is not null && _reportedSunElevation is { } elevation
            ? rule.Update(elevation)
            : VehicleLightStateFlags.None;

    /// <summary>
    /// Write a body's lamps where they differ from what the session last wrote for this vehicle on
    /// this body, and answer the lamps it holds.
    /// </summary>
    /// <remarks>
    /// A body newly lent to a vehicle has nothing written for that vehicle yet, so its lamps are written
    /// whatever they are, <see cref="VehicleLightStateFlags.None"/> included: the actor keeps the lamps
    /// of the last vehicle that held it, and a car admitted at noon must not wear the headlights of the
    /// one that drove it at dusk. After that only a change is written, so a tick's batch carries a lamp
    /// command only for a vehicle whose signals changed at that SUMO frame or when the headlights switch.
    /// </remarks>
    private VehicleLightStateFlags WriteTheLamps(string vehicleId, ActorId actor, SumoVehicleSignals signals,
                                                 VehicleLightStateFlags headlights)
    {
        if (!_options.VehicleLampsDriven)
        {
            return VehicleLightStateFlags.None;
        }

        VehicleLightStateFlags lamps = VehicleLampMapping.FromSumo(signals) | headlights;
        if (!_lampsWritten.TryGetValue(vehicleId, out (ActorId Actor, VehicleLightStateFlags Lamps) last)
            || last.Actor != actor || last.Lamps != lamps)
        {
            _batch.Lamps(actor, lamps);
            _lampsWritten[vehicleId] = (actor, lamps);
            Report.LampCommandsWritten++;
        }

        return lamps;
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
            _batch.Park(actor, body.Parking, darken: _options.VehicleLampsDriven);
            if (_options.VehicleLampsDriven)
            {
                Report.LampCommandsWritten++;
            }
        }

        _lampsWritten.Remove(interval.VehicleId);
        _headings.Forget(interval.VehicleId);
        if (_renderedSpans.Remove(interval.VehicleId))
        {
            // Parked at the head of the next tick's batch, so absent from that tick's frame on.
            _renderSetNow = null;
        }

        Report.CountRelease(interval.ReleaseReason);
        RenderedVehicleInterval released = interval with { Actor = actor };
        _releasedThisFrame.Add(released);
        _options.OnRelease?.Invoke(released);
    }

    /// <summary>
    /// Write every pose this tick, and the velocity that goes with it, in one round trip.
    /// </summary>
    /// <remarks>
    /// <para><b>One batch, and no variable tail.</b> Every command CARLA's batch endpoint takes is
    /// supported at both ends, so N vehicles cost one round trip rather than N. The .NET traffic
    /// manager already writes its control frame this way, against the same endpoint.</para>
    ///
    /// <para><b>A target velocity beside every transform.</b> A vehicle whose root does not simulate
    /// reports, through <c>APawn::GetVelocity</c>, its pawn movement component's <c>Velocity</c> --
    /// not the physics body, and not <c>ComponentVelocity</c>. Nothing on the vehicle path writes
    /// that field except <c>set_actor_target_velocity</c> on a vehicle whose physics is disabled, so
    /// a body moved by transforms alone reports zero to the world observer, the recorder, radar and
    /// the truth telemetry. Disabling physics does not take the body's physics state away: it tears
    /// down the Chaos vehicle simulation, and the movement component's
    /// <c>OnDestroyPhysicsState</c> recreates the mesh's physics state, kinematic. That body is
    /// written by the same call as it always was, and nothing reads it back. What a tick writes,
    /// and in which order, is <see cref="TickBatch"/>'s; SUMO's own speed stays on the pose record
    /// beside it.</para>
    ///
    /// <para>A failed command is counted rather than thrown on. The batch's responses name the
    /// commands that failed, and a run in which some poses did not take is a run whose imagery is
    /// wrong in a way only the count makes visible.</para>
    /// </remarks>
    private void WriteTheBatch()
    {
        if (_world is not { } world || _batch.Commands.Count == 0)
        {
            return;
        }

        IReadOnlyList<CommandResponse> responses = world.ApplyBatch(_batch.Commands);
        Report.Batches++;
        Report.CommandsWritten += _batch.Commands.Count;
        foreach (CommandResponse response in responses)
        {
            if (response.HasError)
            {
                Report.SampleBatchFailure(response.Error);
            }
        }
    }

    /// <summary>
    /// Compare every pose and velocity written this tick against what the world says the body
    /// became.
    /// </summary>
    /// <remarks>
    /// <para>Taken after the tick, because the world observer reports the state of a frame once that
    /// frame exists, and the pose was written for the frame the tick just produced.</para>
    ///
    /// <para>Free: the observer streams every actor's transform and velocity every tick whether or
    /// not anything reads them, so this is an array read and a subtraction per rendered vehicle. That
    /// is what makes it affordable per vehicle per tick rather than as a sample, and being per
    /// vehicle per tick is what lets a residual be attributed to a vehicle rather than to the
    /// run.</para>
    ///
    /// <para>The velocity read is the one every other reader of the world takes -- the truth
    /// telemetry reads the same snapshot -- so a velocity gap here is a gap in the truth record, and a
    /// bridge that sends no velocity shows up as a gap equal to the commanded speed on every moving
    /// vehicle.</para>
    /// </remarks>
    private void MeasureDivergence()
    {
        if (_world is not { } world)
        {
            return;
        }

        foreach ((string vehicleId, ActorId actor, VehiclePose pose) in _commanded)
        {
            if (world.ObservedTransform(actor) is not { } observed
                || world.ObservedVelocity(actor) is not { } moving)
            {
                Report.VehicleTicksWithNoReadBack++;
                continue;
            }

            PoseDivergence divergence = PoseDivergence.Between(
                _tickIndex, RenderedTimeSeconds, vehicleId, actor, pose, observed, moving);
            Report.AddDivergence(divergence);
            _options.OnDivergence?.Invoke(divergence);
        }
    }

    /// <summary>
    /// Run one step of giving the world back, keeping its failure rather than letting it stop the
    /// steps after it.
    /// </summary>
    /// <param name="failures">Where a failure is kept.</param>
    /// <param name="what">The step, in words that finish "could not ...".</param>
    /// <param name="step">The step.</param>
    private static void Attempt(List<Exception> failures, string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception failure)
        {
            // A connection that failed is kept as the connection's own failure, named by the step.
            Exception cause = failure is WorldConnectionLostException lost ? lost.InnerException! : failure;
            failures.Add(new InvalidOperationException($"Could not {what}: {cause.Message}", cause));
        }
    }

    /// <summary>
    /// Refuse a session that renders a world and does not say what its sun is doing.
    /// </summary>
    /// <remarks>
    /// <para>There is no default policy, and that is the point. A frozen run and a run nobody
    /// configured write byte-identical records, so a default would make an absent declaration
    /// indistinguishable from a deliberate one -- and the absent one is today's behaviour, under which
    /// every capture was lit by whatever the world last held. Freezing at the window's opening
    /// instant is the recommended policy; it is recommended, not assumed.</para>
    ///
    /// <para>Checked before anything is started, because it needs nothing but the options. A session
    /// with no world renders nothing and has no sun, so it declares nothing.</para>
    /// </remarks>
    private static void RequireADeclaredIllumination(SumoDriveSessionOptions options)
    {
        if (options.World is null)
        {
            return;
        }

        if (options.Illumination is not { } policy)
        {
            throw new CoSimSessionRefusedException(
                "The session renders a world and declares no illumination policy. A frozen run and "
                + "a run nobody configured write identical records, so an absent policy cannot be "
                + "told from a chosen one, and there is no default. Declare one: "
                + "freeze_at_window_start (recommended -- the sun is set to the civil instant the "
                + "window opens and held there, so the window is one lighting condition), advance "
                + "(carried forward at a declared rate and written for every frame), freeze_at "
                + "(held at a declared civil time of day) or ignore (left as the world holds it, and "
                + "recorded as not honouring any epoch).");
        }

        if (policy.BindsTheSun && options.Epoch is null)
        {
            throw new CoSimSessionRefusedException(
                $"The '{policy.Name}' policy binds the sun to the scenario's civil time, and the "
                + "session declares no epoch to take it from. Declare what simulated second zero "
                + "means in civil time, or run under 'ignore', which leaves the sun alone and records "
                + "that the run's lighting honours no epoch.");
        }
    }

    /// <summary>
    /// Refuse a real-time factor, or a window to measure it over, that describes no pace.
    /// </summary>
    /// <remarks>
    /// Checked before anything is started, because it needs nothing but the options. A factor that
    /// is negative, infinite or undefined is not rounded to the nearest pace that makes sense:
    /// whichever pace that was, it is one nobody asked for, and the run would record it as declared.
    /// </remarks>
    private static void RequireAUsablePace(SumoDriveSessionOptions options)
    {
        if (!double.IsFinite(options.RealTimeFactor) || options.RealTimeFactor < 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"The real-time factor is {options.RealTimeFactor}. It is simulated seconds per "
                + "wall-clock second: 1.0 holds the world to the pace of real traffic, 0.5 to half "
                + "of it, 2.0 to twice it, and 0 runs it as fast as the machine allows. A negative, "
                + "infinite or undefined factor is no pace at all.");
        }

        if (!double.IsFinite(options.PacingWindowSeconds) || options.PacingWindowSeconds <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"The pacing window is {options.PacingWindowSeconds} s. The achieved real-time "
                + "factor is published over windows of that much wall clock, so it has to be a "
                + "positive number of seconds.");
        }

        if (options.WallClock is null)
        {
            throw new CoSimSessionRefusedException(
                "The session was given no wall clock to pace against and time its ticks by. Leave "
                + "it at the system's unless a test is standing one in.");
        }
    }

    /// <summary>
    /// Refuse a bound on SUMO's answers that is not a positive length of time.
    /// </summary>
    /// <remarks>
    /// A session with no bound waits forever on a SUMO that has hung, holding the world in synchronous
    /// mode with nothing ticking it. A bound that is zero, negative or not a number is not rounded to
    /// one that works: whichever that was, nobody asked for it.
    /// </remarks>
    private static void RequireABoundOnSumoSAnswers(SumoDriveSessionOptions options)
    {
        if (!double.IsFinite(options.SumoAnswerTimeoutSeconds) || options.SumoAnswerTimeoutSeconds <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"SUMO's answers are bounded at {options.SumoAnswerTimeoutSeconds} s. The bound is how "
                + "long the session waits for SUMO to answer one command, a step included, before it "
                + "decides SUMO has stopped answering and stops the run, so it has to be a positive "
                + "number of seconds longer than the slowest step the scenario produces.");
        }
    }

    /// <summary>
    /// Refuse a draw distance that is not a positive number of metres.
    /// </summary>
    /// <remarks>
    /// No limit is null, never zero or a negative number standing in for it: a value that means
    /// something other than what it says is one a report would record as declared.
    /// </remarks>
    private static void RequireAUsableDrawDistance(SumoDriveSessionOptions options)
    {
        if (options.DrawDistanceMetres is { } metres && (!double.IsFinite(metres) || metres <= 0.0))
        {
            throw new CoSimSessionRefusedException(
                $"The draw distance is {metres} m. It is how far from a camera a vehicle's body is "
                + "drawn, so it has to be a positive number of metres; leave it unset to draw every "
                + "body at any range.");
        }
    }

    /// <summary>
    /// Refuse a session given no render-set policy.
    /// </summary>
    /// <remarks>
    /// The default draws every vehicle SUMO has, so a session with none is one whose caller cleared it,
    /// and what that was meant to draw is not something to guess.
    /// </remarks>
    private static void RequireARenderSetPolicy(SumoDriveSessionOptions options)
    {
        if (options.RenderSet is null)
        {
            throw new CoSimSessionRefusedException(
                "The session was given no render-set policy. Leave it at its default to draw every vehicle "
                + "SUMO has, or give a circle, the cameras or a capacity as a limit.");
        }
    }

    /// <summary>
    /// Refuse a world truth track with no usable path or rate, or one whose path already holds a track.
    /// </summary>
    /// <remarks>
    /// Settled before anything is started. A rate given with no path is a setting that does nothing, and
    /// a path that already holds a track belongs to another run, whose record is not written over.
    /// Whether the rate is a whole number of SUMO steps waits for SUMO's step, which is known once SUMO
    /// has loaded the scenario.
    /// </remarks>
    private static void RequireAUsableWorldTruthTrack(SumoDriveSessionOptions options)
    {
        if (options.WorldTruthTrackPath is not { } path)
        {
            if (options.WorldTruthTrackIntervalSeconds is { } orphan)
            {
                throw new CoSimSessionRefusedException(
                    $"The world truth track is to be sampled every {orphan.ToString("0.###", CultureInfo.InvariantCulture)} s, "
                    + "and no path was given to write it to. Give the track a path, or leave the rate unset.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CoSimSessionRefusedException(
                "The world truth track was given a blank path. Give the file it is to be written to, or leave "
                + "it unset to write none.");
        }

        if (options.WorldTruthTrackIntervalSeconds is { } interval && (!double.IsFinite(interval) || interval <= 0.0))
        {
            throw new CoSimSessionRefusedException(
                $"The world truth track is to be sampled every {interval.ToString(CultureInfo.InvariantCulture)} s. "
                + "The interval is the simulated time between its samples, so it has to be a positive number "
                + "of seconds; leave it unset to sample every SUMO frame.");
        }

        WorldTruthTrackWriter.RefuseAnExistingTrack(path);
    }

    /// <summary>
    /// Refuse a run manifest with no usable path, a header that is not a JSON object, or a path that
    /// already holds a manifest or is the world truth track's.
    /// </summary>
    /// <remarks>
    /// Settled before anything is started, as the track's are. A header given with no path is a header
    /// nobody writes; a manifest already on disk belongs to another run.
    /// </remarks>
    private static void RequireAUsableRunManifest(SumoDriveSessionOptions options)
    {
        if (options.RunManifestPath is not { } path)
        {
            if (options.RunManifestHeader is not null)
            {
                throw new CoSimSessionRefusedException(
                    "The run manifest was given a header and no path to write it to. Give the manifest a path, "
                    + "or leave the header unset.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CoSimSessionRefusedException(
                "The run manifest was given a blank path. Give the file it is to be written to, or leave it "
                + "unset to write none.");
        }

        if (options.WorldTruthTrackPath is { } track
            && string.Equals(Path.GetFullPath(track), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new CoSimSessionRefusedException(
                $"The run manifest and the world truth track are both to be written to {path}. Each is a file of "
                + "its own.");
        }

        RunManifestWriter.RequireAHeaderObject(options.RunManifestHeader);
        RunManifestWriter.RefuseAnExistingManifest(path);
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
    /// Refuse a window that opens before the first instant the session renders, or at no instant.
    /// </summary>
    /// <remarks>
    /// The window's opening is where a frozen sun is pinned and an advancing one anchored. One before
    /// the fast-forward's end is an instant no frame of the session is rendered at, so its sun would
    /// light nothing the run captures as the window's start.
    /// </remarks>
    private static void RequireAWindowTheSessionRenders(SumoDriveSessionOptions options)
    {
        if (options.WindowOpensAtSimulatedSecond is not { } opens)
        {
            return;
        }

        if (!double.IsFinite(opens) || opens < options.WarmUpToSimulatedSecond)
        {
            throw new CoSimSessionRefusedException(
                $"The window is declared to open at {opens.ToString("0.###", CultureInfo.InvariantCulture)} s "
                + "and the session renders from "
                + options.WarmUpToSimulatedSecond.ToString("0.###", CultureInfo.InvariantCulture)
                + " s, where SUMO is fast-forwarded to. A window opens on an instant the session renders: at "
                + "the first rendered frame, or after the prewarm that precedes it.");
        }
    }

    /// <summary>
    /// The SUMO installation the session will launch: the one the caller named, or the one the
    /// search resolves.
    /// </summary>
    private static SumoInstallation ResolveSumo(SumoDriveSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SumoHome))
        {
            return SumoInstallation.LocateOrThrow();
        }

        try
        {
            return SumoInstallation.At(options.SumoHome);
        }
        catch (DirectoryNotFoundException missing)
        {
            throw new CoSimSessionRefusedException(
                $"The session was told to launch the SUMO installation at {options.SumoHome}, and "
                + $"there is no sumo there to launch. {missing.Message}", missing);
        }
    }

    /// <summary>
    /// Refuse a session asked to show its simulation in <c>sumo-gui</c> where the installation it
    /// launches from has none.
    /// </summary>
    /// <remarks>
    /// Settled before anything is started, like the release. Left to the launch, the failure would
    /// be the operating system's word for a missing file, naming neither the option that asked for
    /// the GUI nor what builds it. The GUI is looked for in the installation already resolved, never
    /// elsewhere: a <c>sumo-gui</c> from another installation would be another SUMO, and the
    /// simulation it showed would not be the one the world's converter was checked against.
    /// </remarks>
    private static void RequireTheGui(SumoInstallation installation, SumoDriveSessionOptions options)
    {
        if (!options.SumoGui || installation.HasSumoGui)
        {
            return;
        }

        throw new CoSimSessionRefusedException(
            "The session was asked to launch sumo-gui in place of sumo (SumoGui; run_sumo_drive.py "
            + $"--sumo-gui), and the SUMO installation at {installation.Home} (matched by "
            + $"{installation.Source}) has no {installation.SumoGui}. SUMO builds it only where the FOX "
            + "toolkit is found, and the repository's setup script builds and stages it with the rest of "
            + "the SUMO toolchain: run CarlaSetup.ps1 on Windows or CarlaSetup.sh on Linux, which rebuilds "
            + "a staged installation that is missing it. Or name an installation that has one (SumoHome; "
            + "run_sumo_drive.py --sumo-home), or run without the GUI. SUMO has not been started.");
    }

    /// <summary>
    /// Refuse a SUMO whose release is not the one that converted the world, unless the mismatch was
    /// accepted.
    /// </summary>
    /// <remarks>
    /// <para>The world's network is one netconvert's output and the scenario was authored against
    /// it; a different release is not guaranteed to read it, route on it or drive it the same way,
    /// and nothing in the imagery says which SUMO produced the traffic in it. More than one SUMO is
    /// commonly installed -- the repository pins one, and a system-wide installer leaves another in
    /// <c>SUMO_HOME</c> -- so this is checked every run rather than assumed.</para>
    ///
    /// <para>Compared by release number (<see cref="SumoRelease"/>): the package records what the
    /// converter printed and the installation reports its release, and the two are the same release
    /// however each was written. A package that records no converter is not evidence of a mismatch
    /// and proceeds; the report says the release went unchecked. An accepted mismatch proceeds and
    /// the report says that too.</para>
    ///
    /// <para>The release is the one the binary about to be launched reports -- <c>sumo-gui</c>'s own
    /// where the session launches it in place of <c>sumo</c> -- because that is the program whose
    /// reading of the network the run is.</para>
    /// </remarks>
    private static SumoReleaseCheck RequireTheWorldSConverter(SumoInstallation installation,
                                                              WorldPackageManifest manifest,
                                                              SumoDriveSessionOptions options)
    {
        SumoReleaseCheck release = SumoReleaseCheck.Of(
            installation,
            options.SumoGui ? SumoInstallation.SumoGuiName : SumoInstallation.SumoName,
            manifest.NetconvertVersion,
            options.AllowSumoVersionMismatch);
        if (release.Refused)
        {
            throw new CoSimSessionRefusedException(
                $"The world package {options.WorldPackagePath} was converted by "
                + $"'{release.RecordedConverter}', and the SUMO the session would launch, "
                + $"{release.Binary}, is "
                + (release.Release is { } found ? $"release {found}" : "of a release that could not be read")
                + $" (the installation at {release.Home}, matched by {release.Source}). Two SUMO releases are not "
                + "guaranteed to build the same network from the same OSM or to drive it the same way, "
                + "so SUMO has not been started. Name an installation of the world's release (SumoHome; "
                + "run_sumo_drive.py --sumo-home), rebuild the world with this one, or accept the "
                + "mismatch explicitly (AllowSumoVersionMismatch; run_sumo_drive.py "
                + "--allow-sumo-version-mismatch), which the run report then records.");
        }

        return release;
    }

    /// <summary>
    /// How far the lane the bridge read puts a vehicle from where SUMO says it is, at a SUMO frame.
    /// </summary>
    /// <remarks>
    /// Taken at the frames themselves rather than between them, so what it measures is whether the
    /// network in the world package is the network SUMO is driving on -- not how good the
    /// interpolation is. A network from a different netconvert run answers here and nowhere else. The
    /// lane's point is moved across by the frame's lateral offset, which SUMO's position includes part-way
    /// through a lane change spread over time, so a lane change does not answer here.
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

            (double x, double y, double directionX, double directionY) =
                lane.PointAt(frame.LanePositionMetres);
            x -= directionY * frame.LateralOffsetMetres;
            y += directionX * frame.LateralOffsetMetres;
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
    /// Refuse a network that is not in the world's frame.
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
    ///
    /// <para>This compares the package's network with the package. That the scenario drives the
    /// same network is <see cref="ScenarioNetworkCheck"/>'s, settled before SUMO was started: two
    /// networks can agree on all three of these and still be different graphs.</para>
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
