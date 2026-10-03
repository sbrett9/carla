using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// Everything the playback bridge asks of a CARLA world, and nothing else.
/// </summary>
/// <remarks>
/// <para>Nineteen operations. The bridge asks which world is loaded, hands the world's truth
/// telemetry the package's ground and the catalogue's vehicle kinds once the package is established
/// as that world's, places bodies,
/// writes their poses and velocities in one batch, names to the server which bodies are lent and
/// which parked, sets how far from a camera the bodies are drawn, reads back where the world says
/// they went and how
/// fast it says they are moving, advances the world a tick, reads and
/// writes the episode settings so it can hand the world back as it found it, shows or hides the
/// rendering layers whose presence is a property of the imagery, reads and writes the sun the
/// imagery is lit by, and reads the cameras an optional render set that follows them is decided
/// from. Anything larger
/// than that would be the client's whole surface, and a driving session tested against the client's
/// whole surface is a session that can only be tested against a running server.</para>
///
/// <para><b>Synchronous by design.</b> Every call here happens on the tick thread, between one world
/// tick and the next, in an order the session fixes. There is nothing for a caller to overlap, and
/// an asynchronous signature would invite a bridge that writes a pose while the frame it belongs to
/// is being rendered.</para>
/// </remarks>
public interface ICarlaWorld
{
    /// <summary>
    /// What the server says about the world it has loaded: its georeference origin, the OpenDRIVE it
    /// serves, and the bare-earth reference record published for it, with the server's digest of each
    /// of its grids in place of the grid.
    /// </summary>
    /// <remarks>
    /// Several round trips and the whole OpenDRIVE, so it is asked once, when a session starts, and
    /// never on the tick thread. The grids stay on the server: they are the size of the drape -- sixty
    /// megabytes for the largest world in <c>Build/world-packages</c>, whose two grids took 146 s and
    /// 153 s to fetch -- and their digests prove equality as strictly.
    /// </remarks>
    LoadedWorld DescribeLoadedWorld();

    /// <summary>
    /// Give the world's truth telemetry the bare-earth grids of a world package, so that nothing on
    /// this connection fetches them from the server, where the server's digests show they are its
    /// record's; answer whether they were taken.
    /// </summary>
    /// <remarks>
    /// <para>The session asks once, after <see cref="LoadedWorldCheck"/> has admitted the package: the
    /// recorder that captures beside it reports bare-earth truth from the same record, and would
    /// otherwise fetch the grids the check just proved it already has.</para>
    ///
    /// <para>Declining changes nothing: the telemetry then fetches the record's own grids when truth
    /// is first asked for, as it does on a connection with no package. So a world that declines is
    /// slower, never wrong, and the session does not refuse it.</para>
    /// </remarks>
    bool AdoptBareEarthGrids(string packagePath);

    /// <summary>
    /// Give the world's truth telemetry the vehicle catalogue's <c>special_type</c> for every
    /// blueprint a class of it draws, by blueprint id, so that a vehicle of such a blueprint is
    /// reported with the catalogue's kind rather than the one its blueprint declares.
    /// </summary>
    /// <remarks>
    /// <para>The session gives them once, when the package has been admitted: the bodies it lends are
    /// the catalogue's blueprints, and the recorder that captures beside it and the live pull of the
    /// same process report them through the truth telemetry of this connection. The kind is the
    /// catalogue's by ruling (doc 06 D6.18): the content build's own is hand-edited, and was empty for
    /// every blueprint when first swept.</para>
    ///
    /// <para>Nothing is written to the server, so nothing is given back, and a truth reader on another
    /// connection still reports each vehicle with the kind its blueprint declares.</para>
    /// </remarks>
    void AdoptCatalogueSpecialTypes(IReadOnlyDictionary<string, string> specialTypes);

    /// <summary>The episode settings as the server currently holds them.</summary>
    EpisodeSettings ReadSettings();

    /// <summary>Write the episode settings.</summary>
    void WriteSettings(EpisodeSettings settings);

    /// <summary>
    /// Place one body of the named blueprint at a transform under a role name, answering the actor
    /// it became.
    /// </summary>
    /// <remarks>
    /// <para>CARLA refuses a spawn whose point is occupied and offers no queue and no retry, so a
    /// caller spawns at a point it knows to be clear and never spawns at a point a vehicle is
    /// driving through.</para>
    ///
    /// <para>The role name is the actor's <c>role_name</c> attribute, in place of the blueprint's
    /// default, and like every attribute it is fixed for the actor's life. It is provenance: which
    /// authority drives the actor (doc 04 D4.9), and the truth record carries it.</para>
    /// </remarks>
    ActorId Spawn(string blueprintId, Transform at, string roleName);

    /// <summary>
    /// Apply a batch of commands in one round trip, answering the server's response per command.
    /// </summary>
    IReadOnlyList<CommandResponse> ApplyBatch(IReadOnlyList<Command> commands);

    /// <summary>
    /// Name to the server the bodies lent since the last call, each with the vehicle it is drawn for,
    /// and the bodies given back, which stand parked out of sight; answer what the server made of it.
    /// </summary>
    /// <remarks>
    /// <para>The server carries every body named on each world-observer snapshot from the next frame
    /// on, so the truth telemetry of every client -- the live pull and the CoT feed of any process,
    /// not only a recorder beside the session -- lists the bodies a frame drew, each named by its
    /// vehicle, and leaves the parked ones out. Only the bodies named are affected, and the server
    /// holds the naming on its record of each actor, so destroying a body ends it.</para>
    ///
    /// <para>One round trip, made only on a tick whose lending changed and before that tick's cue,
    /// so the frame the change is drawn in is the first to carry it. A body given back and lent again
    /// in one call ends lent.</para>
    ///
    /// <para>A server built before it carried a render set refuses the call, and says why. That is
    /// not a failed run: the session's own recorded truth is cut to its render set in its own process
    /// either way, and only the truth other processes read goes back to every vehicle actor.</para>
    /// </remarks>
    RenderSetWrite WriteRenderSet(IReadOnlyList<LentBody> lent, IReadOnlyList<ActorId> parked);

    /// <summary>
    /// Set how far from a camera the named bodies are drawn, in metres, zero for no limit; answer what
    /// the server made of it.
    /// </summary>
    /// <remarks>
    /// <para>Rendering only, and per view: the renderer culls each of a body's meshes and lamps by its
    /// own bounds against every camera's position, so one setting holds for every camera, the ones
    /// spawned after it included. A body beyond the distance from a camera keeps its pose, its
    /// velocity and its place in the truth, and is simply not in that camera's image.</para>
    ///
    /// <para>One round trip for any number of bodies, and set once on a body: a pooled body keeps it
    /// across every vehicle it is lent to.</para>
    ///
    /// <para>A server built before it carried the call refuses it, and says why. That is not a failed
    /// run: every body is then drawn at any range, as with no limit, and the run says so.</para>
    /// </remarks>
    DrawDistanceWrite WriteDrawDistance(IReadOnlyList<ActorId> bodies, double metres);

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
    /// The velocity the world says an actor has, in metres per second in the CARLA frame, or
    /// <see langword="null"/> where it has reported nothing.
    /// </summary>
    /// <remarks>
    /// <para>A read of the same snapshot as <see cref="ObservedTransform"/>, so comparing the
    /// velocity a body was given against the one it reports costs nothing either. It is the velocity
    /// every other reader of the world sees: the truth telemetry, the recorder and radar all take it
    /// from the same place.</para>
    ///
    /// <para>For a vehicle whose physics is disabled it is the velocity last written to it with a
    /// target-velocity command, on a server built with the kinematic-velocity change (doc 03 D3.5).
    /// On a server built before it, such a vehicle reads zero whatever it was given.</para>
    /// </remarks>
    Vector3D? ObservedVelocity(ActorId actor);

    /// <summary>
    /// Where the world says an actor stood on a frame, or <see langword="null"/> where no snapshot the
    /// client holds has it.
    /// </summary>
    /// <remarks>
    /// A read of the client's snapshot of that frame -- the nearest it still holds, where it no longer
    /// holds that one -- not a round trip. A render set that follows the cameras reads each registered
    /// camera's pose here on the last frame the session rendered, which in synchronous mode is also the
    /// newest frame, named by its number so that a camera flown from another thread is read where the
    /// frame had it.
    /// </remarks>
    Transform? ObservedTransformAt(ActorId actor, ulong frame);

    /// <summary>
    /// A camera's image size and horizontal field of view, from the attributes it was spawned with, or
    /// <see langword="null"/> where the actor is unknown or is not a camera.
    /// </summary>
    /// <remarks>
    /// One round trip, asked once when a camera is registered with the session and never on the tick
    /// thread's schedule: a camera's attributes are fixed when it is spawned.
    /// </remarks>
    CameraOptics? DescribeCamera(ActorId camera);

    /// <summary>
    /// Advance the world one tick, answering the simulation frame it produced, or
    /// <see langword="null"/> where it produced none.
    /// </summary>
    /// <remarks>
    /// The frame is the identity every capture of that tick carries, which is how a still is paired
    /// with what the session established about the tick that rendered it.
    /// </remarks>
    ulong? Tick();

    /// <summary>
    /// Show or hide one of the world's rendering layers.
    /// </summary>
    /// <remarks>
    /// <para><b>Rendering only — collision is a separate setting.</b> The server drives visibility
    /// and collision through two independent calls, and hiding a layer changes neither the
    /// collision of the road surface nor the stop-line triggers of a signal. Hiding the road mesh
    /// therefore does not remove a driving surface, and hiding the signals does not stop a signal
    /// detecting vehicles.</para>
    ///
    /// <para><b>There is no reader.</b> The server binds a setter and no getter, so a caller cannot
    /// ask a world how a layer is currently drawn and cannot restore one to what it found. A caller
    /// that has to give a layer back has to declare the state it gives it back to.</para>
    /// </remarks>
    void WriteLayerVisible(string layer, bool visible);

    /// <summary>
    /// The sun as the server computes it now, asked for on demand: the values
    /// <see cref="SolarReading.From"/> reads, the refraction-corrected elevation included, or none
    /// where the world has no sun.
    /// </summary>
    /// <remarks>
    /// A round trip, and deliberately not the world-observer cache: the cache is paired to the last
    /// tick, so right after a write it still holds the sun from before it.
    /// </remarks>
    IReadOnlyList<double> ReadSolarState();

    /// <summary>
    /// Bind the sun's date, its clock and the zone the clock is read in, recomputing it once.
    /// </summary>
    /// <param name="year">Calendar year.</param>
    /// <param name="month">Calendar month.</param>
    /// <param name="day">Calendar day.</param>
    /// <param name="hours">The clock, hours in [0, 24) in the zone below.</param>
    /// <param name="utcOffsetHours">
    /// The zone, as a civil offset in hours. Written as the sun's time zone, which is what makes the
    /// clock civil time rather than local mean solar time at the map's longitude.
    /// </param>
    /// <returns>False where the world has no sun or the date is not a calendar date; the sun is then
    /// left as it was.</returns>
    bool WriteSolarEpoch(int year, int month, int day, double hours, double utcOffsetHours);

    /// <summary>
    /// Whether the engine's own time-of-day advance carries the sun's clock forward with the world
    /// tick, and at how many sun-clock seconds per simulated second. A session sets it off under every
    /// policy and writes an advancing sun itself.
    /// </summary>
    /// <returns>False where the world has no sun.</returns>
    bool WriteTimeAdvance(bool advancing, double rate);

    /// <summary>
    /// The sun the last tick's world-observer snapshot carried: the values
    /// <see cref="SolarReading.From"/> reads, or none where the world published no sun.
    /// </summary>
    /// <remarks>
    /// A read of the snapshot the tick already delivered, not a round trip, which is what makes a
    /// comparison on every tick affordable. Eleven values from a server whose observer header carries
    /// the geometric elevation only, twelve from one that also carries the refraction-corrected one.
    /// </remarks>
    IReadOnlyList<double> ObservedSolarState();
}
