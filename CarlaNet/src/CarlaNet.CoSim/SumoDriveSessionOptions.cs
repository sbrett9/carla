namespace CarlaNet.CoSim;

/// <summary>What a co-simulation session is pointed at, and what it is allowed to do.</summary>
/// <param name="ScenarioPath">
/// The scenario's SUMO configuration. The network it names must be the one the world package
/// carries, compared by canonical fingerprint before SUMO is started (<see cref="ScenarioNetworkCheck"/>);
/// where a compile lock sits beside it, the files it runs must be the ones the lock binds
/// (<see cref="ScenarioLockCheck"/>); it must not let SUMO teleport a waiting vehicle unless
/// <see cref="AllowTeleporting"/> (<see cref="TeleportingCheck"/>, <see cref="SumoDistributionEditCheck"/>);
/// and it must not let SUMO move or remove a vehicle at a collision, offset its departures at random or
/// seed itself from the wall clock (<see cref="SumoDistributionEditCheck"/>).
/// </param>
/// <param name="WorldPackagePath">
/// The world package: the ground surface the poses are seated on, and the road network they are
/// interpolated along.
/// </param>
/// <param name="CataloguePath">The measured vehicle catalogue.</param>
/// <param name="WorldKey">
/// What identifies the world to every component that could claim its population -- a server address
/// and the map it has loaded.
/// </param>
/// <remarks>
/// <para>The four that decide what a session <i>is</i> are constructor parameters and the rest are
/// settable. A session is orchestrated from Python, where an object initialiser is not expressible
/// and an init-only property cannot be written at all, so an options object that can only be built
/// with one is an options object the orchestrator cannot build.</para>
///
/// <para>By default nothing here limits how many vehicles are rendered. The scenario is the only
/// arbiter of population: every vehicle SUMO has gets a body, and a scenario heavier than the machine
/// is comfortable with makes a synchronous run slower on the wall clock, never different in
/// content. Two optional performance controls, off unless set, trade fidelity for speed:
/// <see cref="DrawDistanceMetres"/> limits how far from a camera a body is drawn, and changes no
/// vehicle's body, pose or truth; <see cref="RenderSet"/> limits which vehicles get a body at all, and a
/// vehicle it leaves out is simulated by SUMO and in no frame and no truth record.</para>
/// </remarks>
public sealed record SumoDriveSessionOptions(
    string ScenarioPath,
    string WorldPackagePath,
    string CataloguePath,
    string WorldKey)
{
    /// <summary>
    /// The fixed delta the world ticks at: what a session with a <see cref="World"/> sets it to,
    /// and what one without takes on trust.
    /// </summary>
    public double WorldDeltaSeconds { get; set; } = 0.05;

    /// <summary>Frames per simulated second a recorder would emit.</summary>
    public double CaptureRateHz { get; set; } = 2.0;

    /// <summary>
    /// Whether the world advances only on a tick cue, for a session with no <see cref="World"/>.
    /// False refuses the session: a world that advances on its own moves between the pose write and
    /// the frame, so no captured frame corresponds to any SUMO step.
    /// </summary>
    /// <remarks>
    /// Ignored where a world is given, because the session then puts that world into synchronous
    /// mode itself and validates its clock against what the world reports back rather than against
    /// what a caller declared. Two sources for one fact is how they come to disagree.
    /// </remarks>
    public bool WorldIsSynchronous { get; set; } = true;

    /// <summary>
    /// The CARLA world the session drives: where the bodies are spawned, the poses written and the
    /// ticks cued.
    /// </summary>
    /// <remarks>
    /// Absent, the session computes every pose and applies none of them, which is what established
    /// the pose conversion before anything moved and stays the way to check it again. A session with
    /// no world still owns the advance of simulated time on both sides -- it just advances a counter
    /// instead of a world -- so what it exercises is the whole bridge bar the writing.
    /// </remarks>
    public ICarlaWorld? World { get; set; }

    /// <summary>
    /// Advance the CARLA world by one tick, answering false where the tick did not produce a frame.
    /// </summary>
    /// <remarks>
    /// For a session with no <see cref="World"/>: a run with no CARLA at all supplies one that
    /// counts. Giving both is refused, because a session with two ways to advance the world has two
    /// clocks and no way to say which a frame belongs to.
    /// </remarks>
    public Func<bool>? TickWorld { get; set; }

    /// <summary>Who to name if something else has already claimed the world's population.</summary>
    public string Holder { get; set; } = "CarlaNet.CoSim playback bridge";

    /// <summary>
    /// The SUMO installation to launch: the directory holding <c>bin/sumo</c>.
    /// </summary>
    /// <remarks>
    /// <para>Null or blank resolves one by <see cref="CarlaNet.Sumo.SumoInstallation.Locate"/>'s rules:
    /// <c>CARLANET_SUMO_HOME</c>, then the repository's pinned build found upward from the running
    /// assemblies, then <c>SUMO_HOME</c>, then the executable search path. A runtime loaded from an
    /// installed wheel has no repository above its assemblies, so there it is <c>SUMO_HOME</c> that
    /// decides; a caller that knows which installation converted the world names it here.</para>
    ///
    /// <para>Whichever it is, the session compares its release against the converter the world
    /// package records before starting it, and the report names the installation, its release and
    /// the rule that found it.</para>
    /// </remarks>
    public string? SumoHome { get; set; }

    /// <summary>
    /// Run with a SUMO whose release differs from the converter that built the world, rather than
    /// refusing.
    /// </summary>
    /// <remarks>
    /// For an operator with a reason to accept the risk. It is not silent: the report records that
    /// the mismatch was accepted and names both releases.
    /// </remarks>
    public bool AllowSumoVersionMismatch { get; set; }

    /// <summary>
    /// Launch the installation's <c>sumo-gui</c> in place of <c>sumo</c>, so SUMO's own view of the
    /// simulation the session is stepping is on screen beside the CARLA world.
    /// </summary>
    /// <remarks>
    /// <para>It replaces <c>sumo</c> rather than joining it: one SUMO process, the one the session
    /// drives, launched from the same installation with the same arguments and the ones that let it
    /// follow the session with nobody at the window (<see cref="CarlaNet.Sumo.SumoLaunchOptions.Gui"/>).
    /// The release pin applies to it as it does to <c>sumo</c>: the release compared against the
    /// world's converter is the one <c>sumo-gui</c> itself reports, and the report names the binary
    /// that ran.</para>
    ///
    /// <para>An installation without one is refused before anything is started, naming the file and
    /// the setup script that builds and stages it. Pausing the GUI pauses the run -- SUMO answers
    /// nothing while it is paused, so a pause longer than <see cref="SumoAnswerTimeoutSeconds"/>
    /// stops the run as a SUMO that stopped answering -- and closing its window stops the run as a
    /// SUMO that died. Its delay control slows every step while it is raised.</para>
    /// </remarks>
    public bool SumoGui { get; set; }

    /// <summary>
    /// Run a scenario whose configuration lets SUMO teleport a waiting vehicle, rather than refusing: by
    /// <c>time-to-teleport</c>, by any of the other teleport options or by a vehicle type's own attribute.
    /// </summary>
    /// <remarks>
    /// A teleport moves a vehicle straight to a lane further along its route, which renders as a body
    /// dragged across the map; the runtime jump detector releases and re-admits such a vehicle rather
    /// than interpolating across the gap, whether or not this is set. Not silent: the report records
    /// the wait SUMO teleports after and that it was accepted (<see cref="TeleportingCheck"/>), and every
    /// other trigger with the value it ran under (<see cref="SumoDistributionEditCheck"/>). It accepts no
    /// collision action that teleports: a collision is constrained to <c>warn</c> or <c>none</c> whatever
    /// this says.
    /// </remarks>
    public bool AllowTeleporting { get; set; }

    /// <summary>
    /// Height of the actor origin above the contact surface per blueprint, where it has been
    /// measured by settling a body on level ground rather than taken from its bounding box.
    /// </summary>
    public IReadOnlyDictionary<string, double>? MeasuredSeatHeights { get; set; }

    /// <summary>
    /// A SUMO step length to force, overriding what the scenario authored.
    /// </summary>
    /// <remarks>
    /// Behaviour-changing, and recorded in the report for that reason. Measured on the shipped port
    /// scenario: moving from a one-second step to a tenth left the demand identical -- same
    /// insertions, same routes -- and cut mean time loss per vehicle by 62%, which is most of what
    /// the run is capturing truth about.
    /// </remarks>
    public double? SumoStepOverrideSeconds { get; set; }

    /// <summary>Simulated second to fast-forward SUMO to before the first world tick.</summary>
    /// <remarks>
    /// Never paced, whatever <see cref="RealTimeFactor"/> is: the fast-forward steps SUMO alone and
    /// cues no world tick, so nothing renders during it and there is nothing to hold to the wall
    /// clock. Pacing starts at the first tick cue.
    /// </remarks>
    public double WarmUpToSimulatedSecond { get; set; }

    /// <summary>
    /// The simulated second the capture window opens -- its first captured frame -- where the session
    /// renders from earlier than that, or null where the window opens at the first frame the session
    /// renders.
    /// </summary>
    /// <remarks>
    /// <para>A run that prewarms renders from <see cref="WarmUpToSimulatedSecond"/> and captures from
    /// here, so the two are different instants: a 300 s prewarm before a window at 10:05:00 renders
    /// from 10:00:00. A sun frozen at the window's start is pinned here, and an advancing sun is
    /// anchored here, so the window's first captured frame is lit by its own instant whatever the
    /// prewarm.</para>
    ///
    /// <para>Refused where it is before <see cref="WarmUpToSimulatedSecond"/> or not a number: a
    /// window cannot open on an instant the session never renders.</para>
    /// </remarks>
    public double? WindowOpensAtSimulatedSecond { get; set; }

    /// <summary>
    /// Simulated seconds per wall-clock second the world's tick cues are held to: 1.0 is the pace of
    /// real traffic, 0.5 half of it, 2.0 twice it, and 0 -- the default -- holds them to nothing, so
    /// the world ticks as fast as the machine allows.
    /// </summary>
    /// <remarks>
    /// <para>Declared once, read when the session starts, and fixed for the session: a run whose
    /// pace changed part-way is two runs in one record. A negative, infinite or undefined factor is
    /// refused.</para>
    ///
    /// <para>Pacing changes only when a cue goes out. Every frame is stamped with the simulated
    /// instant it renders and the SUMO step, the world delta and the capture rate keep the same whole
    /// ratio at any factor, so a world held below real time renders exactly the frames it would have
    /// rendered unpaced -- more slowly. What a run actually held is measured and published on the
    /// report's <see cref="CoSimRunReport.Pacing"/>, paced or not.</para>
    /// </remarks>
    public double RealTimeFactor { get; set; }

    /// <summary>
    /// Wall-clock seconds each window of the published achieved factor spans.
    /// </summary>
    /// <remarks>
    /// Long enough that the figure is not the jitter of one tick against the system timer, short
    /// enough that an operator watching a live run sees a slowdown within seconds of it starting.
    /// Must be a positive number of seconds.
    /// </remarks>
    public double PacingWindowSeconds { get; set; } = 5.0;

    /// <summary>
    /// The wall clock the session holds its cues to and times them against.
    /// </summary>
    /// <remarks>
    /// The system's by default. A test supplies one that advances only when told to, which is what
    /// lets the schedule be asserted to the tick rather than within a timer's resolution.
    /// </remarks>
    public TimeProvider WallClock { get; set; } = TimeProvider.System;

    /// <summary>
    /// What simulated second zero means in civil time at the site: the scenario's epoch.
    /// </summary>
    /// <remarks>
    /// The only statement of what time the scene is. Every civil instant the session derives -- the
    /// one the sun is bound to and the one each frame is recorded under -- is
    /// <see cref="SolarEpoch.CivilInstantAt"/> of the simulated instant, so a malformed epoch is
    /// refused where it is built, before anything is started, naming what is wrong with it.
    /// </remarks>
    public SolarEpoch? Epoch { get; set; }

    /// <summary>
    /// What the session does with the world's sun across its window.
    /// </summary>
    /// <remarks>
    /// Under any policy that binds the sun, the session writes the date, the civil clock, the civil
    /// offset, the advancing flag and the rate once, after SUMO has been fast-forwarded and before
    /// the first world tick, for the civil instant the window opens (<see cref="WindowOpensAtSimulatedSecond"/>,
    /// the first rendered frame's where none is given) -- and reads the
    /// sun back to confirm the world took it. The sun the world was found with is given back when the
    /// session ends, on its failure paths as on its normal one.
    /// </remarks>
    public IlluminationPolicy? Illumination { get; set; }

    /// <summary>
    /// Whether the generated road surface is drawn for the session.
    /// </summary>
    /// <remarks>
    /// <para>Off, because the imagery this mode exists to produce is electro-optical: the generated
    /// road mesh is a flat grey ribbon drawn over the photogrammetry of the real road surface, so
    /// leaving it on puts the same rendering artefact in every frame of the corpus. Turned on it is
    /// a debugging view -- where the network the vehicles are driving on actually lies.</para>
    ///
    /// <para>Whichever it is, it is decided here, at the session's start, and holds for the whole
    /// run. A layer that changed mid-capture would make two frames of one run incomparable with
    /// nothing in the record saying why, so the report carries what was set.</para>
    /// </remarks>
    public bool RoadLayerVisible { get; set; }

    /// <summary>
    /// Whether the generated traffic-light and sign actors are drawn for the session.
    /// </summary>
    /// <remarks>
    /// Off, for the same reason and one more: the available signal meshes are a limited set and are
    /// frequently misaligned against the photogrammetry, so a detector trained on them learns an
    /// artefact. SUMO still simulates the signals and its vehicles still obey them -- what is
    /// dropped is the rendering of the signal, not the signal -- and hiding them is rendering-only,
    /// so their stop-line triggers stay live.
    /// </remarks>
    public bool SignalLayerVisible { get; set; }

    /// <summary>
    /// How far from a camera, in metres, a vehicle's body is drawn; null, the default, draws every
    /// body at any range.
    /// </summary>
    /// <remarks>
    /// <para>An optional performance control, off unless set, and rendering only. Every vehicle still
    /// gets its body, is posed on every tick and is in the truth exactly as without it; a body farther
    /// than this from a camera is simply not drawn in that camera's image. The distance is set once on
    /// each pooled body as it is spawned, and kept across every vehicle the body is lent to, as the
    /// maximum draw distance of its meshes and lamps (<see cref="ICarlaWorld.WriteDrawDistance"/>):
    /// the renderer culls by it per view, so it holds for every camera at once, the fixed camera, a
    /// flown one and a capture run's channels alike. <see cref="SumoDriveSession.SetDrawDistance"/>
    /// changes it during a run.</para>
    ///
    /// <para>Each frame's render set carries the distance the frame was drawn under
    /// (<see cref="CarlaNet.Recording.RenderSet.DrawDistanceMetres"/>), so a recorder marks, in that
    /// camera's sidecar, every vehicle the camera did not draw for it. A server built before it
    /// carried the call refuses it: the run goes on with every body drawn at any range, the frames
    /// carry no distance, and the report names the refusal.</para>
    ///
    /// <para>A positive number of metres, or null. Anything else is refused before anything is
    /// started.</para>
    /// </remarks>
    public double? DrawDistanceMetres { get; set; }

    /// <summary>
    /// Which of the vehicles SUMO has hold a body, and how many may at once: every vehicle, with no
    /// capacity, unless another policy is given.
    /// </summary>
    /// <remarks>
    /// <para>The default, <see cref="EveryVehicleRenderSetPolicy"/> with no capacity, limits nothing.
    /// A capacity on it, a circle (<see cref="RegionRenderSetPolicy"/>) or the registered cameras'
    /// footprints (<see cref="CameraFootprintRenderSetPolicy"/>, with
    /// <see cref="SumoDriveSession.AddCamera"/>) are optional performance controls. A vehicle the
    /// policy leaves out is still simulated by SUMO -- its traffic is unchanged -- and has no body in
    /// CARLA, so it is in no frame and in no truth record; the report states the policy and counts what
    /// it left out, and the release reasons say why a track ended.</para>
    ///
    /// <para>Whatever the policy, every vehicle SUMO has stays subscribed to its full state, one SUMO
    /// inserts is drawn from the frame SUMO first reports it in, and every body drawn is seated on its
    /// road, turned along its path and placed across its lane exactly as with no limit.</para>
    /// </remarks>
    public IRenderSetPolicy RenderSet { get; set; } = new EveryVehicleRenderSetPolicy();

    /// <summary>Where each computed pose goes.</summary>
    public Action<CoSimPoseRecord>? OnPose { get; set; }

    /// <summary>Where a completed render-set interval goes.</summary>
    public Action<RenderedVehicleInterval>? OnRelease { get; set; }

    /// <summary>
    /// Where each commanded-against-applied comparison goes, one per rendered vehicle per tick.
    /// </summary>
    /// <remarks>
    /// Handed out rather than accumulated, like the poses: a capture run produces one of these for
    /// every vehicle on every tick, and a list of all of them is a run-length leak on the tick
    /// thread. The summary a run needs is on the report either way.
    /// </remarks>
    public Action<PoseDivergence>? OnDivergence { get; set; }

    /// <summary>Where each admission pass goes: one per SUMO step.</summary>
    /// <remarks>
    /// The same record <see cref="CoSimRunReport.LastAdmissionPass"/> holds, handed out as it is made
    /// so a writer can keep every row -- including the two made while the session starts, before the
    /// caller's first advance. From the tick thread, once per SUMO step; it must not block.
    /// </remarks>
    public Action<AdmissionPass>? OnAdmissionPass { get; set; }

    /// <summary>Where SUMO's own console output goes.</summary>
    public Action<string>? SumoOutput { get; set; }

    /// <summary>
    /// How long, in seconds, the session waits for SUMO to answer any one command -- a step included
    /// -- before it decides SUMO has stopped answering and stops the run.
    /// </summary>
    /// <remarks>
    /// <para>A SUMO that has hung or been suspended keeps its socket open and simply never answers, and
    /// a session waiting on it forever holds the world in synchronous mode with nothing ever ticking it.
    /// A bound turns that into a refusal like any other SUMO failure, and everything is given back.</para>
    ///
    /// <para>It has to exceed the slowest step the scenario will ever produce, because a step's answer
    /// takes as long as the step. Measured steps are milliseconds -- 9.26 ms at 388 vehicles on the
    /// Arapahoe network with the bridge's subscriptions -- so the default of 60 s is thousands of times
    /// the slowest seen and still stops a hung run within a minute. Must be a positive number of
    /// seconds.</para>
    /// </remarks>
    public double SumoAnswerTimeoutSeconds { get; set; } = 60.0;

    /// <summary>
    /// Whether the session drives each body's lamps: SUMO's brake and indicator signals, mapped bit by
    /// bit, and headlights from the sun.
    /// </summary>
    /// <remarks>
    /// On by default: the cost is a command in the tick's batch when a vehicle's lamps change and none
    /// otherwise, and imagery with dark lamps is knowably wrong about something the simulation knows.
    /// Off is a control condition for a sweep that wants to isolate the effect -- no lamp is then written
    /// at all, so every body keeps the lamps it was spawned with, which are off.
    /// </remarks>
    public bool VehicleLampsDriven { get; set; } = true;

    /// <summary>
    /// The elevation of the sun, degrees, below which every rendered vehicle has its headlights on.
    /// </summary>
    /// <remarks>
    /// Read against the geometric elevation the world reports; see <see cref="HeadlightRule"/>.
    /// Headlights are driven only where the session binds the sun, so that the sun they follow is the
    /// declared one.
    /// </remarks>
    public double HeadlightOnBelowDegrees { get; set; } = HeadlightRule.DefaultOnBelowDegrees;

    /// <summary>
    /// The elevation of the sun, degrees, above which headlights go off again. Above
    /// <see cref="HeadlightOnBelowDegrees"/>, or the session is refused.
    /// </summary>
    public double HeadlightOffAboveDegrees { get; set; } = HeadlightRule.DefaultOffAboveDegrees;

    /// <summary>
    /// Where each collision SUMO registered goes, once it is over: the two vehicles, what SUMO called
    /// it, where, the span of simulated time it lasted and the bodies that rendered the vehicles.
    /// </summary>
    /// <remarks>
    /// A collision does not stop the run. It is a fact about the corpus a consumer filters on, and this
    /// is the record it filters on. From the tick thread, once per collision; it must not block.
    /// </remarks>
    public Action<CollisionSpan>? OnCollision { get; set; }

    /// <summary>
    /// Where each vehicle SUMO gave up trying to insert goes, as the session notices it.
    /// </summary>
    /// <remarks>
    /// SUMO drops such a vehicle without a word, so this is the only account of it. From the tick
    /// thread; it must not block.
    /// </remarks>
    public Action<VehicleNotInserted>? OnVehicleNotInserted { get; set; }

    /// <summary>
    /// What follows the run from inside the session: each observer is told of every SUMO frame the
    /// session reads, every frame it renders and completes, and the session's end
    /// (<see cref="ISumoStepObserver"/>). None by default.
    /// </summary>
    /// <remarks>
    /// <para>Read once, when the session starts, like the real-time factor: an observer added afterwards
    /// is told nothing, and the session tells the ones it started with, in the order they were added.
    /// From the tick thread; none may block.</para>
    ///
    /// <para>A list to add to rather than a callback to set, because more than one thing follows a run
    /// at once -- a truth track, a manifest, an interval binder -- and none should have to know of the
    /// others. From Python, add to it.</para>
    /// </remarks>
    public IList<ISumoStepObserver> StepObservers { get; set; } = new List<ISumoStepObserver>();

    /// <summary>
    /// Where the session writes the world truth track: every vehicle SUMO has, at each sampled SUMO
    /// frame inside the capture window, drawn or not (<see cref="WorldTruthTrackWriter"/>). Null, the
    /// default, writes none.
    /// </summary>
    /// <remarks>
    /// <para>The record of what the world contained, as against what a camera could see: a base rate
    /// taken over the capture sidecars alone is taken over the vehicles that were drawn. A capture run
    /// writes one; a session driven for any other purpose writes one only when asked.</para>
    ///
    /// <para>A CSV, appended a row at a time and flushed with each, with a summary beside it
    /// (<see cref="WorldTruthTrackWriter.SummaryPathFor"/>). A file already there is refused rather
    /// than written over, before anything is started: a run writes a track of its own and never over
    /// another run's. The directory is made if it does not exist.</para>
    /// </remarks>
    public string? WorldTruthTrackPath { get; set; }

    /// <summary>
    /// Simulated seconds between the SUMO frames the world truth track samples, counted from the
    /// window's opening; null, the default, samples every SUMO frame.
    /// </summary>
    /// <remarks>
    /// A whole number of SUMO steps, or the session is refused once SUMO's step is known, before
    /// anything is rendered: a sample between two SUMO frames would be a state nobody simulated.
    /// Every SUMO frame is the default because the base rate is computed from the track and a coarser
    /// one leaves the shortest-lived vehicles out of its denominator. Refused without
    /// <see cref="WorldTruthTrackPath"/>: a rate with no track to write is a setting that does nothing.
    /// </remarks>
    public double? WorldTruthTrackIntervalSeconds { get; set; }
}
