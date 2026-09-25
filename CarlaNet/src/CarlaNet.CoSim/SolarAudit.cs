using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// Compares the sun a world reports against the sun the session declared: on demand when the
/// window opens, and then on every tick from the world-observer snapshot the tick already delivered.
/// </summary>
/// <remarks>
/// <para><b>What is compared, and against what.</b> Always against the declaration -- the epoch
/// projected through the policy by <see cref="DeclaredSun.SunAt"/> -- and never against the sun's own
/// clock, which would compare the sun with itself and pass unconditionally. Four things, each of
/// which catches a different way to be wrong:</para>
/// <list type="bullet">
/// <item>The zone, the advancing flag and the rate, exactly. A zone other than the declared offset is
/// a clock read as local mean solar time; a flag or rate other than the policy's is a second
/// component driving the sun.</item>
/// <item>The instant the sun holds -- its date and its clock together -- within
/// <see cref="ToleranceSeconds"/>. A wrong date is a whole day out and cannot pass.</item>
/// <item>The direction the sun reports, from its geometric elevation and azimuth, within
/// <see cref="ToleranceDegrees"/> of where the engine's own algorithm puts the sun at the declared
/// instant -- evaluated at the instant itself, not through the engine's clock decomposition, so a
/// sun the engine renders a minute early is a disagreement rather than a match.</item>
/// <item>The refraction-corrected elevation, which is what the sun's light is rotated by and what a
/// window's elevation is declared against, within the same tolerance -- wherever the reading carries
/// it. The on-demand read-back at window open always does; the per-tick snapshot does from a server
/// whose observer header was widened to carry it, and <see cref="TicksWithCorrectedElevation"/>
/// says whether it did.</item>
/// </list>
///
/// <para><b>The tolerances are derived, not chosen.</b> The clock floor is half a second: the engine
/// evaluates its sun at whole seconds, so a clock nearer than that to the declared one cannot change
/// the sun it renders. The angle floor is <see cref="SolarPositionModel.ResolutionFloorDegrees"/>,
/// the finest agreement with the engine that is resolvable at all; measured, the engine agrees with
/// the algorithm three orders of magnitude inside it. Under an advancing sun both widen by two ticks'
/// worth of advance -- the engine's advance and the snapshot need not fall on the same side of a tick
/// -- which at a real-time rate and a 0.05 s tick leaves both at the floor.</para>
///
/// <para><b>A disagreement stops the run.</b> See <see cref="SolarAuditFailedException"/>. Where the
/// disagreement is the engine's own clock decomposition -- the world holds the declared clock and
/// renders the sun of the minute before -- the failure says so, because the remedy is in the engine
/// and not in the declaration.</para>
///
/// <para><b>What it cannot see.</b> It reads the sun's state, not the light: a second directional
/// light added to the level, a sky that was re-lit, or exposure that compensated the change away all
/// leave the sun agreeing with the declaration and the frame disagreeing with it. It compares the sun
/// against the world package's origin, so it establishes that the world's sun is the declared sun
/// <i>at that origin</i>; that the imagery was streamed for the same place is a separate question.</para>
/// </remarks>
public sealed class SolarAudit
{
    /// <summary>Half the engine's one-second clock: nearer than this cannot change the sun rendered.</summary>
    public const double ClockFloorSeconds = 0.5;

    /// <summary>How far an exactly compared quantity may move in the engine's own arithmetic.</summary>
    private const double ExactTolerance = 1e-9;

    private DateTime _modelledFor;
    private SunPosition _modelled;
    private bool _hasModelled;

    /// <param name="declared">The sun the window declares.</param>
    /// <param name="originLatitude">The world package's origin, which the sun is computed for.</param>
    /// <param name="originLongitude">Likewise.</param>
    /// <param name="worldDeltaSeconds">The fixed delta the world ticks at.</param>
    public SolarAudit(DeclaredSun declared, double originLatitude, double originLongitude,
                      double worldDeltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(declared);
        Declared = declared;
        OriginLatitude = originLatitude;
        OriginLongitude = originLongitude;
        ToleranceSeconds = Math.Max(ClockFloorSeconds, 2.0 * declared.Policy.Rate * worldDeltaSeconds);
        ToleranceDegrees = Math.Max(SolarPositionModel.ResolutionFloorDegrees,
                                    15.0 * ToleranceSeconds / 3600.0);
    }

    /// <summary>The sun the window declares.</summary>
    public DeclaredSun Declared { get; }

    /// <summary>The latitude the declared sun is computed for.</summary>
    public double OriginLatitude { get; }

    /// <summary>The longitude the declared sun is computed for.</summary>
    public double OriginLongitude { get; }

    /// <summary>How far the sun's instant may sit from the declared one, in seconds.</summary>
    public double ToleranceSeconds { get; }

    /// <summary>How far the sun's direction, or its corrected elevation, may sit from the declared.</summary>
    public double ToleranceDegrees { get; }

    /// <summary>The on-demand comparison taken when the window opened.</summary>
    public SolarAuditSample? AtWindowOpen { get; private set; }

    /// <summary>Ticks whose snapshot was compared.</summary>
    public long AuditedTicks { get; private set; }

    /// <summary>
    /// Of those, the ones whose snapshot carried the refraction-corrected elevation, so it was
    /// compared on that tick and not only when the window opened.
    /// </summary>
    public long TicksWithCorrectedElevation { get; private set; }

    /// <summary>The comparison whose clock sat furthest from the declared instant.</summary>
    public SolarAuditSample? WorstClock { get; private set; }

    /// <summary>The comparison whose sun pointed furthest from the declared sun.</summary>
    public SolarAuditSample? WorstAngle { get; private set; }

    /// <summary>The comparison whose corrected elevation sat furthest from the declared one.</summary>
    public SolarAuditSample? WorstCorrected { get; private set; }

    /// <summary>The most recent comparison.</summary>
    public SolarAuditSample? Last { get; private set; }

    /// <summary>The comparison that stopped the run, if one did.</summary>
    public SolarAuditSample? Failure { get; private set; }

    /// <summary>
    /// Compare the sun read back on demand right after it was bound, before any tick.
    /// </summary>
    /// <exception cref="SolarAuditFailedException">It is not the declared sun.</exception>
    public SolarAuditSample AuditWindowOpen(SolarReading onDemand)
    {
        SolarAuditSample sample = Compare(null, Declared.WindowOpensAtSimulatedSecond, onDemand);
        AtWindowOpen = sample;
        return sample;
    }

    /// <summary>
    /// Compare the sun the world published with a tick's snapshot against the sun declared for the
    /// instant that tick rendered.
    /// </summary>
    /// <param name="tickIndex">The session's tick.</param>
    /// <param name="renderedSeconds">The simulated instant the tick rendered.</param>
    /// <param name="observed">The snapshot's solar block, as the client parsed it.</param>
    /// <exception cref="SolarAuditFailedException">
    /// The snapshot carried no sun, or not the declared one.
    /// </exception>
    public SolarAuditSample AuditTick(long tickIndex, double renderedSeconds, IReadOnlyList<double> observed)
    {
        if (SolarReading.From(observed) is not { } reading)
        {
            throw new SolarAuditFailedException(
                $"The world published no sun with tick {tickIndex} "
                + $"({renderedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s), though one "
                + "was bound when the window opened. A frame with no sun beside it is as unusable as "
                + "one with the wrong sun, and the sidecar would silently omit it.", null);
        }

        SolarAuditSample sample = Compare(tickIndex, renderedSeconds, reading);
        AuditedTicks++;
        if (reading.CorrectedElevationDegrees is not null)
        {
            TicksWithCorrectedElevation++;
        }

        return sample;
    }

    private SolarAuditSample Compare(long? tickIndex, double simulatedSeconds, SolarReading observed)
    {
        DateTimeOffset civil = Declared.Epoch.CivilInstantAt(simulatedSeconds);
        DateTime declaredSun = Declared.SunAt(simulatedSeconds);
        SunPosition modelled = ModelledAt(declaredSun);

        List<string> problems = [];
        if (Math.Abs(observed.TimeZoneHours - Declared.TimeZoneHours) > ExactTolerance)
        {
            bool meanSolar = Math.Abs(observed.TimeZoneHours - (observed.LongitudeDegrees / 15.0)) < 1e-6;
            problems.Add($"its time zone is {Hours(observed.TimeZoneHours)} h where "
                         + $"{Hours(Declared.TimeZoneHours)} h was declared"
                         + (meanSolar
                             ? " -- longitude/15, so its clock is local mean solar time, not civil time"
                             : string.Empty));
        }

        if (observed.Advancing != Declared.Policy.Advances
            || Math.Abs(observed.Rate - Declared.Policy.Rate) > ExactTolerance)
        {
            problems.Add($"it is {(observed.Advancing ? "advancing" : "not advancing")} at rate "
                         + $"{Hours(observed.Rate)} where the '{Declared.Policy.Name}' policy declared "
                         + $"{(Declared.Policy.Advances ? "advancing" : "not advancing")} at "
                         + $"{Hours(Declared.Policy.Rate)}: something other than this session is driving it");
        }

        double clockResidual = double.NaN;
        if (observed.LocalInstant is not { } held)
        {
            problems.Add($"its date {observed.Year:0000}-{observed.Month:00}-{observed.Day:00} is not a "
                         + "calendar date, which renders a sun at -180 degrees");
        }
        else
        {
            clockResidual = (held - declaredSun).TotalSeconds;
            if (Math.Abs(clockResidual) > ToleranceSeconds)
            {
                double days = Math.Round(clockResidual / 86_400.0);
                problems.Add($"it holds {held:yyyy-MM-dd HH:mm:ss.fff}, "
                             + $"{clockResidual.ToString("0.###", CultureInfo.InvariantCulture)} s from the "
                             + "declared instant"
                             + (days != 0.0 && Math.Abs(clockResidual - (days * 86_400.0)) <= ToleranceSeconds
                                 ? $" -- a whole {Math.Abs(days):0} day(s): the date is wrong, which is a "
                                   + "wrong seasonal sun"
                                 : string.Empty));
            }
        }

        double angle = SolarPositionModel.SeparationDegrees(observed.ElevationDegrees, observed.AzimuthDegrees,
                                                            modelled.ElevationDegrees, modelled.AzimuthDegrees);
        if (angle > ToleranceDegrees)
        {
            problems.Add($"it points {angle.ToString("0.####", CultureInfo.InvariantCulture)} degrees from "
                         + $"the declared sun (elevation {observed.ElevationDegrees:0.####} against "
                         + $"{modelled.ElevationDegrees:0.####}, azimuth {observed.AzimuthDegrees:0.####} "
                         + $"against {modelled.AzimuthDegrees:0.####})" + WhyTheDirectionDisagrees(observed));
        }

        double? correctedResidual = observed.CorrectedElevationDegrees is { } corrected
            ? corrected - modelled.CorrectedElevationDegrees
            : null;
        if (correctedResidual is { } correction && Math.Abs(correction) > ToleranceDegrees)
        {
            problems.Add($"its refraction-corrected elevation, which its light is rotated by, is "
                         + $"{observed.CorrectedElevationDegrees:0.####} degrees against a declared "
                         + $"{modelled.CorrectedElevationDegrees:0.####}");
        }

        var sample = new SolarAuditSample(tickIndex, simulatedSeconds, civil, declaredSun, observed,
                                          modelled, clockResidual, angle, correctedResidual);
        if (problems.Count > 0)
        {
            Failure = sample;
            throw new SolarAuditFailedException(
                $"The sun disagrees with the declaration at {sample.Where}, civil "
                + $"{SolarEpoch.FormatCivil(civil)}: " + string.Join("; ", problems) + ". Declared: "
                + $"{declaredSun:yyyy-MM-dd HH:mm:ss.fff} at UTC{SolarEpoch.FormatOffset(Declared.Epoch.UtcOffset)} "
                + $"under {Declared.Policy}, at {OriginLatitude:0.#####}, {OriginLongitude:0.#####}. "
                + $"The world reports {observed}. Tolerances {ToleranceSeconds:0.###} s of clock and "
                + $"{ToleranceDegrees:0.####} degrees of sun. Every frame from here on would carry a sun "
                + "nothing declared, so the run stops.", sample);
        }

        Record(sample);
        return sample;
    }

    /// <summary>
    /// Why a sun whose inputs agree points elsewhere: the engine's clock decomposition, or a sun
    /// computed for another place.
    /// </summary>
    private string WhyTheDirectionDisagrees(SolarReading observed)
    {
        SunPosition fromItsOwnClock = SolarPositionModel.AtEngineClock(
            observed.LatitudeDegrees, observed.LongitudeDegrees, observed.TimeZoneHours,
            observed.Year, observed.Month, observed.Day, observed.SolarTimeHours);
        bool engineIsConsistent = SolarPositionModel.SeparationDegrees(
            observed.ElevationDegrees, observed.AzimuthDegrees,
            fromItsOwnClock.ElevationDegrees, fromItsOwnClock.AzimuthDegrees) <= ToleranceDegrees;

        if (Math.Abs(observed.LatitudeDegrees - OriginLatitude) > 1e-6
            || Math.Abs(observed.LongitudeDegrees - OriginLongitude) > 1e-6)
        {
            return $". The world computes its sun at {observed.LatitudeDegrees:0.#####}, "
                   + $"{observed.LongitudeDegrees:0.#####}, which is not the world package's origin: "
                   + "the georeference is somewhere else";
        }

        if (engineIsConsistent)
        {
            (int hour, int minute, int second) = SolarPositionModel.EngineClock(observed.SolarTimeHours);
            TimeSpan clock = TimeSpan.FromHours(observed.SolarTimeHours);
            TimeSpan evaluated = new(hour, minute, second);
            if (Math.Abs((clock - evaluated).TotalSeconds) > 0.5)
            {
                return $". The world holds the declared clock, {clock:hh\\:mm\\:ss\\.fff}, and its sun "
                       + $"evaluates it as {evaluated:hh\\:mm\\:ss}: ACesiumSunSky::GetHMSFromSolarTime "
                       + "rounds the seconds to sixty and drops the minute they carry into, so the "
                       + "frame is lit by the sun of a minute earlier. The remedy is that carry in the "
                       + "engine, not the declaration";
            }
        }

        return engineIsConsistent
            ? string.Empty
            : ". It is not the sun the engine's own algorithm gives for the clock it holds either";
    }

    private SunPosition ModelledAt(DateTime declaredSun)
    {
        // A frozen sun asks the same question on every tick.
        if (!_hasModelled || declaredSun != _modelledFor)
        {
            _modelled = SolarPositionModel.AtInstant(OriginLatitude, OriginLongitude,
                                                     Declared.TimeZoneHours, declaredSun);
            _modelledFor = declaredSun;
            _hasModelled = true;
        }

        return _modelled;
    }

    private void Record(SolarAuditSample sample)
    {
        Last = sample;
        if (WorstClock is null || Math.Abs(sample.ClockResidualSeconds) > Math.Abs(WorstClock.ClockResidualSeconds))
        {
            WorstClock = sample;
        }

        if (WorstAngle is null || sample.AngleResidualDegrees > WorstAngle.AngleResidualDegrees)
        {
            WorstAngle = sample;
        }

        if (sample.CorrectedResidualDegrees is { } corrected
            && (WorstCorrected?.CorrectedResidualDegrees is not { } worst || Math.Abs(corrected) > Math.Abs(worst)))
        {
            WorstCorrected = sample;
        }
    }

    private static string Hours(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
