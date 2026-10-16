using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// The session's hold on the world's sun: bound to the window's declared instant before the first
/// frame is rendered, read back to confirm the world took it, and given back as it was found.
/// </summary>
/// <remarks>
/// <para><b>Why the sun is bound per window and never inherited.</b> The sun actor persists in a
/// loaded world, and configuring the georeference overwrites its clock and zone but deliberately not
/// its date, so a world reaches a session holding whatever the last session, the last operator or
/// <c>ACesiumSunSky</c>'s class default (2019-09-21) left it at -- and possibly still advancing.
/// Every one of those renders plausible imagery. So the date, the clock, the zone, the advancing flag
/// and the rate are all written, whatever the world was believed to hold.</para>
///
/// <para><b>One write at window open, in one place in the sequence.</b> After SUMO has been
/// fast-forwarded -- no world tick happens during that, so nothing can move the sun -- and before the
/// first world tick, one <c>set_solar_epoch</c> sets the date, the civil clock and the civil offset
/// together and the sun is recomputed once. Then <c>set_time_advance(false, 0)</c>, under every
/// policy: the engine's own advance is never used. Setting the zone to the declared offset is what
/// makes the clock civil time rather than local mean solar time at the map's longitude, a difference
/// of 14 minutes 43 seconds at the sizing site.</para>
///
/// <para><b>Under <c>advance</c>, one write per frame.</b> <see cref="WriteForFrame"/> writes the sun
/// for the frame the world is about to render, before its tick cue, so the frame is lit by the sun
/// written for it. The engine's own advance is not used because its clock passes through the last
/// half-second of every minute, where <c>ACesiumSunSky::GetHMSFromSolarTime</c> rounds the seconds to
/// sixty and drops the minute; the session writes each clock a millisecond past the whole second
/// nearest the frame's declared instant (<see cref="DeclaredSun.WrittenAt"/>), where every second
/// decomposes as declared. The date is written with it, so a window that crosses civil midnight is
/// carried onto the next date when the epoch's calendar advances and held on the epoch's date when it
/// does not; the engine rolls no date the session does not write.</para>
///
/// <para><b>Read back, because a write is not the world having it.</b> The world is asked for its
/// sun on demand -- not from the observer cache, which is paired to the last tick and so predates
/// the write -- and every field that was written is compared exactly. A disagreement gives the sun
/// back and refuses the session, naming each field.</para>
///
/// <para><b>Given back on every path out, as the settings and layer leases are.</b> The sun is
/// global state of a world an operator may also be using, and the state it was found in is
/// readable, so it is restored rather than declared. One thing is not readable and so not restored:
/// whether the sun used its engine's own daylight-saving rule. <c>set_solar_epoch</c> turns that
/// rule off, as configuring the georeference already does, because the declared offset carries
/// daylight saving.</para>
/// </remarks>
public sealed class SolarLease : IDisposable
{
    /// <summary>How far the sun's clock may sit from the one written: the engine's own arithmetic.</summary>
    private const double ClockReadBackHours = 1e-9;

    private readonly ICarlaWorld _world;
    private bool _released;

    private SolarLease(ICarlaWorld world, DeclaredSun declared, SolarReading? asFound)
    {
        _world = world;
        Declared = declared;
        AsFound = asFound;
    }

    /// <summary>The sun the window declares.</summary>
    public DeclaredSun Declared { get; }

    /// <summary>
    /// The sun the world held before the session touched it -- what the session's history would
    /// otherwise have lit the capture with -- or <see langword="null"/> where the world has none.
    /// </summary>
    public SolarReading? AsFound { get; }

    /// <summary>
    /// The sun the world reported on demand after the binding, refraction-corrected elevation
    /// included, or <see langword="null"/> where the world has no sun and the policy allowed that.
    /// </summary>
    public SolarReading? AtWindowOpen { get; private set; }

    /// <summary>Whether the world had no sun to bind, which the policy allowed.</summary>
    public bool NoSun => AsFound is null;

    /// <summary>Whether the sun has been given back.</summary>
    public bool IsReleased => _released;

    /// <summary>How many frames <see cref="WriteForFrame"/> wrote the sun for.</summary>
    public long FrameWrites { get; private set; }

    /// <summary>
    /// Bind the world's sun to the window's declared instant and confirm the world took it.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The world has no sun and the policy requires one; the server refused the epoch or the advance
    /// setting; or the sun read back is not the one written. Nothing is left changed: whatever was
    /// written before the refusal is given back before it is raised.
    /// </exception>
    public static SolarLease Take(ICarlaWorld world, DeclaredSun declared)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(declared);

        SolarReading? asFound = SolarReading.From(world.ReadSolarState());
        if (asFound is not { } found)
        {
            if (declared.Policy.RequireSun)
            {
                throw new CoSimSessionRefusedException(
                    "The world reports no sun: get_solar_state answered with nothing, so it has no "
                    + "CesiumSunSky to bind. A capture under lighting nobody declared is not one "
                    + $"anything can be concluded from, and the '{declared.Policy.Name}' policy "
                    + "requires a sun. Configure the Cesium georeference, which spawns one, or "
                    + "declare that the run does not require a sun.");
            }

            return new SolarLease(world, declared, null);
        }

        var lease = new SolarLease(world, declared, found);
        DateTime sun = declared.WrittenAtWindowOpen;
        if (!world.WriteSolarEpoch(sun.Year, sun.Month, sun.Day, sun.TimeOfDay.TotalHours,
                                   declared.TimeZoneHours))
        {
            // The server leaves the sun untouched when it refuses, so there is nothing to give back.
            throw new CoSimSessionRefusedException(
                $"The world refused the epoch {sun:yyyy-MM-dd HH:mm:ss} at "
                + $"UTC{SolarEpoch.FormatOffset(declared.Epoch.UtcOffset)}. set_solar_epoch "
                + "answers false only when there is no sun or the date is not a calendar date.");
        }

        SolarReading readBack;
        try
        {
            if (!world.WriteTimeAdvance(DeclaredSun.EngineAdvances, DeclaredSun.EngineRate))
            {
                throw new CoSimSessionRefusedException(
                    "The world refused set_time_advance(false, 0) after taking the epoch, so the "
                    + "engine's own advance may still be carrying the sun, and under the "
                    + $"'{declared.Policy.Name}' policy nothing but this session may move it.");
            }

            readBack = SolarReading.From(world.ReadSolarState())
                ?? throw new CoSimSessionRefusedException(
                    "The world took the epoch and then reported no sun when asked for it.");
            List<string> disagreements = Disagreements(declared, readBack);
            if (disagreements.Count > 0)
            {
                throw new CoSimSessionRefusedException(
                    "The world did not take the sun the session wrote: "
                    + string.Join("; ", disagreements)
                    + ". A session that believes it bound the sun while the world holds another "
                    + "renders every frame under lighting nothing declared.");
            }
        }
        catch (Exception refusal)
        {
            try
            {
                lease.Dispose();
            }
            catch (InvalidOperationException restoration)
            {
                throw new AggregateException(refusal, restoration);
            }

            throw;
        }

        lease.AtWindowOpen = readBack;
        return lease;
    }

    /// <summary>
    /// Write the sun for the frame the world renders next, at simulated instant
    /// <paramref name="renderedSeconds"/>, under a policy that advances it; under a freeze, nothing,
    /// because the write at window open holds for the whole window.
    /// </summary>
    /// <remarks>
    /// Called before the frame's tick cue. In synchronous mode the server executes every RPC that
    /// arrives before the cue in the drain of that same frame, before any actor ticks and before the
    /// cameras capture, so the frame is lit by exactly the sun written here -- and the snapshot the
    /// tick publishes carries it, for the audit to compare against the declaration. The date and the
    /// zone are written with the clock every time, so the write is the whole of the sun's state and
    /// never depends on what the previous frame left.
    /// </remarks>
    /// <exception cref="CoSimSessionRefusedException">The world refused the write.</exception>
    /// <exception cref="InvalidOperationException">The sun has already been given back.</exception>
    public void WriteForFrame(double renderedSeconds)
    {
        if (_released)
        {
            throw new InvalidOperationException(
                "The sun has been given back; writing it now would overwrite the sun it was found with.");
        }

        if (NoSun || !Declared.Policy.Advances)
        {
            return;
        }

        DateTime sun = Declared.WrittenAt(renderedSeconds);
        if (!_world.WriteSolarEpoch(sun.Year, sun.Month, sun.Day, sun.TimeOfDay.TotalHours,
                                    Declared.TimeZoneHours))
        {
            throw new CoSimSessionRefusedException(
                "The world refused the sun written for the frame at "
                + $"{renderedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s, "
                + $"{sun:yyyy-MM-dd HH:mm:ss.fff} at UTC{SolarEpoch.FormatOffset(Declared.Epoch.UtcOffset)}. "
                + "set_solar_epoch answers false only when there is no sun or the date is not a "
                + "calendar date, and a frame rendered under the previous sun is lit by an instant "
                + "nothing declared for it.")
            {
                Cause = CoSimStopCause.SolarStateDisagreement,
            };
        }

        FrameWrites++;
    }

    /// <summary>
    /// Whether the world had no sun left to give back to when the session ended -- its sun went away
    /// part-way through the run, so nothing in it holds the session's writes.
    /// </summary>
    public bool SunGoneWhenGivenBack { get; private set; }

    /// <summary>
    /// Put the sun back as it was found. Doing it twice does nothing the second time.
    /// </summary>
    /// <remarks>
    /// A world whose sun went away during the run has nothing holding what the session wrote, so there
    /// is nothing to give back; that is recorded (<see cref="SunGoneWhenGivenBack"/>) rather than raised,
    /// because it is the run's failure -- which stopped it -- and not a second one of the shutdown's.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The world has a sun and would not take the one it was found with back -- a date that is not a
    /// calendar date can be held by a sun but not written to one. The advance setting is restored
    /// regardless.
    /// </exception>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (AsFound is not { } found)
        {
            return;
        }

        bool epochRestored = _world.WriteSolarEpoch(found.Year, found.Month, found.Day,
                                                    found.SolarTimeHours, found.TimeZoneHours);
        bool advanceRestored = _world.WriteTimeAdvance(found.Advancing, found.Rate);
        if (epochRestored)
        {
            return;
        }

        if (!advanceRestored && SolarReading.From(_world.ReadSolarState()) is null)
        {
            SunGoneWhenGivenBack = true;
            return;
        }

        throw new InvalidOperationException(
            $"The world would not take back the sun it was found with ({found}). Its advance "
            + "setting was restored; its date and clock hold the session's.");
    }

    /// <summary>What was bound, what the world reported back, and what it was found holding.</summary>
    public override string ToString()
    {
        string window = $"{Declared.Policy} for a window opening at "
                        + SolarEpoch.FormatCivil(Declared.WindowOpenCivil);
        return NoSun
            ? $"{window}; the world has no sun and the policy did not require one"
            : $"{window}; sun written {Declared.WrittenAtWindowOpen:yyyy-MM-dd HH:mm:ss.fff} at "
              + $"UTC{SolarEpoch.FormatOffset(Declared.Epoch.UtcOffset)}; the world reports "
              + $"{AtWindowOpen}; it was found holding {AsFound}";
    }

    private static List<string> Disagreements(DeclaredSun declared, SolarReading read)
    {
        DateTime sun = declared.WrittenAtWindowOpen;
        List<string> disagreements = [];
        if (read.Year != sun.Year || read.Month != sun.Month || read.Day != sun.Day)
        {
            disagreements.Add($"date {read.Year:0000}-{read.Month:00}-{read.Day:00}, written "
                              + $"{sun:yyyy-MM-dd}");
        }

        if (Math.Abs(read.SolarTimeHours - declared.WrittenClockHours) > ClockReadBackHours)
        {
            disagreements.Add($"clock {read.SolarTimeHours:0.#########} h, written "
                              + $"{declared.WrittenClockHours:0.#########} h");
        }

        if (Math.Abs(read.TimeZoneHours - declared.TimeZoneHours) > ClockReadBackHours)
        {
            disagreements.Add($"time zone {read.TimeZoneHours:0.######} h, written "
                              + $"{declared.TimeZoneHours:0.######} h -- a zone of longitude/15 is "
                              + "local mean solar time, not the site's civil offset");
        }

        if (read.Advancing != DeclaredSun.EngineAdvances)
        {
            disagreements.Add($"advancing {read.Advancing.ToString().ToLowerInvariant()}, written false");
        }

        if (Math.Abs(read.Rate - DeclaredSun.EngineRate) > 1e-12)
        {
            disagreements.Add($"rate {read.Rate:0.######}, written 0");
        }

        return disagreements;
    }
}
