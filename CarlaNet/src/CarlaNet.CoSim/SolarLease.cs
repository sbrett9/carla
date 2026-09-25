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
/// <para><b>One write, in one place in the sequence.</b> After SUMO has been fast-forwarded -- no
/// world tick happens during that, so nothing can move the sun -- and before the first world tick,
/// one <c>set_solar_epoch</c> sets the date, the civil clock and the civil offset together and the
/// sun is recomputed once. Then <c>set_time_advance</c>, and only then: enabled before the clock is
/// set, an advancing sun would carry a clock that was about to be replaced. Setting the zone to the
/// declared offset is what makes the clock civil time rather than local mean solar time at the map's
/// longitude, a difference of 14 minutes 43 seconds at the sizing site.</para>
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
        DateTime sun = declared.SunAtWindowOpen;
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
            if (!world.WriteTimeAdvance(declared.Policy.Advances, declared.Policy.Rate))
            {
                throw new CoSimSessionRefusedException(
                    "The world refused set_time_advance("
                    + $"{declared.Policy.Advances.ToString().ToLowerInvariant()}, "
                    + $"{declared.Policy.Rate.ToString("0.######", CultureInfo.InvariantCulture)}) "
                    + "after taking the epoch, so whether the sun moves is not what the "
                    + $"'{declared.Policy.Name}' policy declares.");
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
    /// Put the sun back as it was found. Doing it twice does nothing the second time.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The world would not take the sun it was found with back -- a date that is not a calendar date
    /// can be held by a sun but not written to one. The advance setting is restored regardless.
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
        _world.WriteTimeAdvance(found.Advancing, found.Rate);
        if (!epochRestored)
        {
            throw new InvalidOperationException(
                $"The world would not take back the sun it was found with ({found}). Its advance "
                + "setting was restored; its date and clock hold the session's.");
        }
    }

    /// <summary>What was bound, what the world reported back, and what it was found holding.</summary>
    public override string ToString()
    {
        string window = $"{Declared.Policy} for a window opening at "
                        + SolarEpoch.FormatCivil(Declared.WindowOpenCivil);
        return NoSun
            ? $"{window}; the world has no sun and the policy did not require one"
            : $"{window}; sun written {Declared.SunAtWindowOpen:yyyy-MM-dd HH:mm:ss.FFF} at "
              + $"UTC{SolarEpoch.FormatOffset(Declared.Epoch.UtcOffset)}; the world reports "
              + $"{AtWindowOpen}; it was found holding {AsFound}";
    }

    private static List<string> Disagreements(DeclaredSun declared, SolarReading read)
    {
        DateTime sun = declared.SunAtWindowOpen;
        List<string> disagreements = [];
        if (read.Year != sun.Year || read.Month != sun.Month || read.Day != sun.Day)
        {
            disagreements.Add($"date {read.Year:0000}-{read.Month:00}-{read.Day:00}, written "
                              + $"{sun:yyyy-MM-dd}");
        }

        if (Math.Abs(read.SolarTimeHours - sun.TimeOfDay.TotalHours) > ClockReadBackHours)
        {
            disagreements.Add($"clock {read.SolarTimeHours:0.#########} h, written "
                              + $"{sun.TimeOfDay.TotalHours:0.#########} h");
        }

        if (Math.Abs(read.TimeZoneHours - declared.TimeZoneHours) > ClockReadBackHours)
        {
            disagreements.Add($"time zone {read.TimeZoneHours:0.######} h, written "
                              + $"{declared.TimeZoneHours:0.######} h -- a zone of longitude/15 is "
                              + "local mean solar time, not the site's civil offset");
        }

        if (read.Advancing != declared.Policy.Advances)
        {
            disagreements.Add($"advancing {read.Advancing.ToString().ToLowerInvariant()}, written "
                              + declared.Policy.Advances.ToString().ToLowerInvariant());
        }

        if (Math.Abs(read.Rate - declared.Policy.Rate) > 1e-12)
        {
            disagreements.Add($"rate {read.Rate:0.######}, written {declared.Policy.Rate:0.######}");
        }

        return disagreements;
    }
}
