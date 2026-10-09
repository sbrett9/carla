using CarlaNet.Types.Rpc.Environment;

namespace CarlaNet.CoSim;

/// <summary>
/// The session's hold on a world's episode settings: taken when it starts to tick the world, given
/// back with the settings it found when it lets go.
/// </summary>
/// <remarks>
/// <para><b>Why the bridge writes the settings at all.</b> It owns the advance of simulated time on
/// both sides, which an asynchronous world makes impossible: the world advances on its own between
/// the pose write and the frame, so no captured frame corresponds to any SUMO step. So the session
/// puts the world into synchronous mode at a fixed delta rather than asking an operator to.
/// </para>
///
/// <para><b>And why it reads them back.</b> Writing a setting is not the same as the world having
/// it. A write that did not take leaves a session that believes it owns the clock driving a world
/// that is running on its own, which produces poses stamped with instants nothing rendered. The read
/// back is one round trip at session start and it is the difference between owning the tick and
/// assuming it.</para>
///
/// <para><b>Restoration is a property of this object, not a courtesy at the end of a happy
/// path.</b> A previous run left an operator's editor stranded in synchronous mode, waiting for a
/// tick from a process that had died. Everything between taking this lease and giving it back is
/// inside the session's disposal, every step of which runs whatever the ones before it did, and
/// giving it back twice is a no-op -- so the failure paths restore exactly as the happy one does.
/// </para>
/// </remarks>
public sealed class WorldSettingsLease : IDisposable
{
    private readonly ICarlaWorld _world;
    private readonly EpisodeSettings _original;
    private bool _released;

    private WorldSettingsLease(ICarlaWorld world, EpisodeSettings original, EpisodeSettings applied)
    {
        _world = world;
        _original = original;
        Applied = applied;
    }

    /// <summary>The settings the world had before the session touched them.</summary>
    public EpisodeSettings Original => _original;

    /// <summary>The settings the world reported after the session wrote its own.</summary>
    public EpisodeSettings Applied { get; }

    /// <summary>Whether the settings have been given back.</summary>
    public bool IsReleased => _released;

    /// <summary>The fixed delta the world is actually ticking at.</summary>
    public double FixedDeltaSeconds => Applied.FixedDeltaSeconds ?? 0.0;

    /// <summary>
    /// Put a world into synchronous mode at a fixed delta, keeping what it had.
    /// </summary>
    /// <param name="world">The world to hold.</param>
    /// <param name="fixedDeltaSeconds">Simulated seconds per tick.</param>
    /// <exception cref="CoSimSessionRefusedException">
    /// The world did not take the write. Nothing is left changed that the caller has to undo: the
    /// original settings are put back before the refusal is raised.
    /// </exception>
    public static WorldSettingsLease Take(ICarlaWorld world, double fixedDeltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!double.IsFinite(fixedDeltaSeconds) || fixedDeltaSeconds <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"A fixed delta of {fixedDeltaSeconds} s is not a tick length. The world has to "
                + "advance by a known simulated interval per tick, because that interval is the "
                + "denominator of the interpolation fraction between two SUMO frames.");
        }

        EpisodeSettings original = world.ReadSettings();
        world.WriteSettings(original with
        {
            SynchronousMode = true,
            FixedDeltaSeconds = fixedDeltaSeconds,
        });

        EpisodeSettings applied = world.ReadSettings();
        if (applied.SynchronousMode && applied.FixedDeltaSeconds is { } delta
            && Math.Abs(delta - fixedDeltaSeconds) <= 1e-9)
        {
            return new WorldSettingsLease(world, original, applied);
        }

        world.WriteSettings(original);
        throw new CoSimSessionRefusedException(
            "The world did not take the settings the session wrote. It was asked for synchronous "
            + $"mode at a fixed delta of {fixedDeltaSeconds:0.######} s and reports "
            + $"{(applied.SynchronousMode ? "synchronous" : "asynchronous")} at "
            + $"{(applied.FixedDeltaSeconds is { } reported ? reported.ToString("0.######") : "a variable delta")}. "
            + "A session that believes it owns the tick while the world runs on its own stamps "
            + "every frame with an instant nothing rendered.");
    }

    /// <summary>
    /// Put the settings back as they were found. Doing it twice does nothing the second time.
    /// </summary>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _world.WriteSettings(_original);
    }
}
