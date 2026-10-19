using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// Refuses a session whose options describe no run it could carry out, before anything is started.
/// </summary>
/// <remarks>
/// Every check here needs nothing but the options and the files they name: no world package is read,
/// no SUMO is resolved and nothing on the server is touched. <see cref="SumoDriveSession.Start"/> runs
/// them first, so a setting that means nothing is refused in its own words rather than found part-way
/// through a start that has already taken the world.
/// </remarks>
internal static class SessionOptionsCheck
{
    /// <summary>
    /// Refuse the options where any check below refuses them, in the order the session has always
    /// checked them.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">A setting describes no usable run.</exception>
    internal static void Require(SumoDriveSessionOptions options)
    {
        RequireAnEpoch(options);
        RequireAUsablePace(options);
        RequireOneWayToAdvanceTheWorld(options);
        RequireAWindowTheSessionRenders(options);
        RequireABoundOnSumoSAnswers(options);
        RequireAUsableDrawDistance(options);
        RequireARenderSetPolicy(options);
        RequireAUsableWorldTruthTrack(options);
        RequireAUsableRunManifest(options);
    }

    /// <summary>
    /// When nothing says what civil time it is, refuse a session that renders a world.
    /// </summary>
    /// <remarks>
    /// Checked before anything is started, because it needs nothing but the options. Every still a
    /// recorder writes from a session is named and stamped from the scenario's civil time, and every
    /// row of the world truth track carries it, so a session that renders a world has to declare an
    /// epoch whatever its sun policy does: under <c>ignore</c> the sun is left alone and the record
    /// still says what time the scene was. Where the policy binds the sun as well, the refusal says so,
    /// and says the policy is the default where it is. A session with no world renders nothing, writes
    /// no still and binds no sun, so it needs none.
    /// </remarks>
    private static void RequireAnEpoch(SumoDriveSessionOptions options)
    {
        if (options.World is null || options.Epoch is not null)
        {
            return;
        }

        IlluminationPolicy? policy = options.IlluminationInForce;
        string sun = policy is { BindsTheSun: true }
            ? (policy.Defaulted
                   ? "The session declares no illumination policy. The sun advances by default. "
                   : string.Empty)
              + $"The '{policy.Name}' policy binds the sun to the scenario's civil time. "
            : string.Empty;
        throw new CoSimSessionRefusedException(
            sun
            + "The session declares no epoch. Every still a recorder writes is named and stamped from "
            + "the scenario's civil time, and the world truth track carries it, so a session that "
            + "renders a world needs one under every sun policy. Declare what simulated second zero "
            + "means in civil time.");
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
                + "drawn, so it has to be a positive number of meters; leave it unset to draw every "
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
}
