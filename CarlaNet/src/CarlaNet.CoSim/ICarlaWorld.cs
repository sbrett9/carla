using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// Everything the playback bridge asks of a CARLA world, and nothing else.
/// </summary>
/// <remarks>
/// <para>Ten operations. The bridge places bodies, writes their poses in one batch, reads back
/// where the world says they went, advances the world a tick, reads and writes the episode settings
/// so it can hand the world back as it found it, shows or hides the rendering layers whose presence
/// is a property of the imagery, and reads and writes the sun the imagery is lit by. Anything larger
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
    /// Whether the engine carries the sun's clock forward with the world tick, and at how many
    /// sun-clock seconds per simulated second.
    /// </summary>
    /// <returns>False where the world has no sun.</returns>
    bool WriteTimeAdvance(bool advancing, double rate);
}
