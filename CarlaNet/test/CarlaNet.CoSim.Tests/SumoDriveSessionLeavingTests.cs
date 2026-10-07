using System.Globalization;
using CarlaNet.Sumo;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Streaming;
using Xunit.Abstractions;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A vehicle SUMO stops reporting is drawn at its last SUMO position on that step's own frame and is gone
/// from the next frame, as the owner ruled on 2026-10-06: its body is lent to nobody else on that frame,
/// goes back to the free bodies from the next, and its interval ends at the next frame's instant.
/// </summary>
/// <remarks>
/// <para>Each drive runs at one world tick per SUMO step, where the next frame is the next step's, and at
/// twenty, where it is the second tick of the same step. What SUMO reported at each step comes from the
/// session's own step records, kept by a <see cref="LeavingTimeline"/>; what each frame drew comes from the
/// render set the recorded world published for it.</para>
///
/// <para><b>The convention the instants are held to.</b> A vehicle's interval runs from the instant of the
/// first frame that draws it to the instant of the first frame that no longer does: admitted at its first
/// drawn frame, released at the frame after its last. The session's end already closed every interval
/// that way, at the frame after the last rendered.</para>
/// </remarks>
public sealed class SumoDriveSessionLeavingTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "leaving-" + Guid.NewGuid().ToString("n"));

    public SumoDriveSessionLeavingTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is not the test's failure.
        }
    }

    [RequiresSumoTheory]
    [InlineData(0.05)]
    [InlineData(0.0025)]
    public void TheLeavingBodyIsLentToNoOtherVehicleOnItsLastFrameAndToTheNextFromTheFrameAfter(double worldDelta)
    {
        // `newcomer` is first reported at the very step `leaver` is last reported at, so the step's frame
        // draws both and newcomer needs a body there; `successor`, of the same blueprint, is first reported a
        // step later. Each on its own lane, so nothing holds any of them up.
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        double lastOfTheLeaver = LastReported(HandOnScenario(null), "leaver");
        string scenario = HandOnScenario(lastOfTheLeaver);

        Drive first = Run(world, scenario, worldDelta, steps: 400);
        Drive again = Run(world, scenario, worldDelta, steps: 400);

        Assert.Equal(lastOfTheLeaver, first.Timeline.LastStepWith("leaver").FrameSeconds, 9);
        Assert.Equal(lastOfTheLeaver, first.Timeline.FirstStepWith("newcomer").FrameSeconds, 9);
        Assert.Equal(lastOfTheLeaver + 0.05, first.Timeline.FirstStepWith("successor").FrameSeconds, 9);

        // The leaver's last step's frame draws it and the newcomer, each with a body of its own.
        ulong last = first.Timeline.FrameAt(lastOfTheLeaver);
        ActorId leaving = first.BodyOf(last, "leaver")!.Value;
        ActorId newcomer = first.BodyOf(last, "newcomer")!.Value;
        Assert.NotEqual(leaving, newcomer);
        Assert.Equal(last, first.LastFrameDrawing("leaver"));

        // From the next frame the leaver's body is parked, and it is the body the successor borrows on its
        // first frame -- the next frame itself at one tick per step -- rather than a third one spawned.
        Assert.Null(first.BodyOf(last + 1, "leaver"));
        ulong successorFirst = first.Timeline.FrameAt(lastOfTheLeaver + 0.05);
        Assert.Equal(successorFirst, first.FirstFrameDrawing("successor"));
        Assert.Equal(leaving, first.BodyOf(successorFirst, "successor"));
        for (ulong frame = last + 1; frame < successorFirst; frame++)
        {
            Assert.Contains(leaving, first.Carla.PublishedRenderSetOf(frame)!.Parked);
        }

        Assert.Equal(2, first.Carla.Spawned.Count);

        // Two runs of one seed lend the same bodies to the same vehicles, frame for frame.
        Assert.Equal(Lending(first), Lending(again));
    }

    [RequiresSumoTheory]
    [InlineData(0.05)]
    [InlineData(0.0025)]
    public void EveryIntervalRunsFromTheFirstFrameThatDrawsItsVehicleToTheFirstThatNoLongerDoes(double worldDelta)
    {
        // Stopped part-way through, with `first` gone and `second` still on the network: one interval ended
        // by SUMO removing its vehicle, one by the session's end, and the unmeasured vehicle's never drawn.
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        int steps = (int)Math.Round(12.0 / 0.05);
        Drive drive = Run(world, CoSimFixtures.SuccessionScenario, worldDelta, steps);

        RenderedVehicleInterval left = Assert.Single(drive.Released, interval => interval.VehicleId == "first");
        RenderedVehicleInterval ended = Assert.Single(drive.Released, interval => interval.VehicleId == "second");
        Assert.Equal(RenderSetReleaseReason.LeftTheSimulation, left.ReleaseReason);
        Assert.Equal(RenderSetReleaseReason.SessionEnded, ended.ReleaseReason);

        foreach (RenderedVehicleInterval interval in drive.Released.Where(interval => interval.Actor != 0))
        {
            ulong firstDrawn = drive.FirstFrameDrawing(interval.VehicleId);
            ulong lastDrawn = drive.LastFrameDrawing(interval.VehicleId);
            _output.WriteLine($"{interval.VehicleId}: drawn on frames {firstDrawn}..{lastDrawn}, interval "
                              + $"{interval.AdmittedAtSeconds:0.####}..{interval.ReleasedAtSeconds:0.####} s, "
                              + $"{interval.ReleaseReason}");

            // Drawn on every frame between, by the one body the interval names.
            for (ulong frame = firstDrawn; frame <= lastDrawn; frame++)
            {
                Assert.Equal(interval.Actor, drive.BodyOf(frame, interval.VehicleId));
            }

            Assert.Equal(drive.Timeline.InstantOf(firstDrawn), interval.AdmittedAtSeconds, 9);
            Assert.Equal(drive.Timeline.InstantOf(lastDrawn) + worldDelta, interval.ReleasedAtSeconds, 9);
        }

        // The vehicle SUMO removed was last drawn on the frame of the last step that had it.
        Assert.Equal(drive.Timeline.FrameAt(drive.Timeline.LastStepWith("first").FrameSeconds),
                     drive.LastFrameDrawing("first"));

        // And the run's own record of the removal, handed to observers with the step after it, carries the
        // same interval.
        Assert.Equal(left, Assert.Single(drive.Timeline.Released, interval => interval.VehicleId == "first"));
    }

    [RequiresSumoTheory]
    [InlineData(0.05)]
    [InlineData(0.0025)]
    public void AVehicleTakenOutBetweenTwoStepsIsDrawnWhereSumoLastHadItAndHasVanished(double worldDelta)
    {
        // Taken out through the session's own connection between two advances, as another client would:
        // SUMO lists no arrival for it, and the session learns of it at the step after the one it was last
        // reported in.
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var timeline = new LeavingTimeline();
        List<RenderedVehicleInterval> released = [];
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.SuccessionScenario, worldDelta, carla,
                                                  timeline, released, poses);

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 40 && session.Advance(); step++)
            {
            }

            Assert.Contains("first", timeline.Steps[^1].Frames.Keys);
            session.Sumo.Vehicles.Remove("first");
            AdvanceUntilReleasedAndTwoStepsOn(session, released, "first");
        }

        RenderedVehicleInterval gone = Assert.Single(released, interval => interval.VehicleId == "first");
        Assert.Equal(RenderSetReleaseReason.Vanished, gone.ReleaseReason);
        Assert.Contains("first", timeline.Steps.Single(step => step.Vanished.Contains("first")).Vanished);
        AssertDrawnOnItsLastStepAndGoneFromTheNext(carla, timeline, poses, gone, worldDelta);
    }

    [RequiresSumoTheory]
    [InlineData(0.05)]
    [InlineData(0.0025)]
    public void TheJamsFollowerTeleportedOffTheNetworkIsDrawnWhereSumoLastHadIt(double worldDelta)
    {
        // SUMO gives up on the follower and teleports it; with nowhere further along its route to put it, it
        // takes it off the network on the same step and lists it among that step's arrivals.
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var timeline = new LeavingTimeline();
        List<RenderedVehicleInterval> released = [];
        List<CoSimPoseRecord> poses = [];
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.JamScenario, worldDelta, carla,
                                                  timeline, released, poses);
        options.AllowTeleporting = true;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            AdvanceUntilReleasedAndTwoStepsOn(session, released, "follower");
        }

        LeavingTimeline.Step teleported = Assert.Single(timeline.Steps, step => step.TeleportsStarted.Contains("follower"));
        Assert.Contains("follower", teleported.Arrived);
        RenderedVehicleInterval follower = Assert.Single(released, interval => interval.VehicleId == "follower");
        Assert.Equal(RenderSetReleaseReason.LeftTheSimulation, follower.ReleaseReason);
        AssertDrawnOnItsLastStepAndGoneFromTheNext(carla, timeline, poses, follower, worldDelta);
    }

    /// <summary>
    /// A leaving vehicle's last drawn frame is the frame of the last step SUMO reported it in, where it
    /// stands exactly where SUMO put it, reads <c>sumo</c>, is lent on the server's render set and is compared
    /// against what the world applied; the next frame has its body parked, named so before that frame's cue;
    /// it is never stale; and its interval ends at the next frame's instant.
    /// </summary>
    private void AssertDrawnOnItsLastStepAndGoneFromTheNext(RecordedWorld carla, LeavingTimeline timeline,
                                                            List<CoSimPoseRecord> poses,
                                                            RenderedVehicleInterval interval, double worldDelta)
    {
        string vehicle = interval.VehicleId;
        ActorId body = interval.Actor;
        Assert.NotEqual(0u, body);
        LeavingTimeline.Step lastStep = timeline.LastStepWith(vehicle);
        CoSimVehicleFrame there = lastStep.Frames[vehicle];
        ulong last = timeline.FrameAt(lastStep.FrameSeconds);
        _output.WriteLine($"{vehicle}: last reported at {lastStep.FrameSeconds:0.###} s, frame {last}, body {body}, "
                          + $"released at {interval.ReleasedAtSeconds:0.####} s ({interval.ReleaseReason})");

        // Drawn on that frame, lent to it on the server's render set, a step frame, reading sumo.
        Assert.Equal((vehicle, last), LastLent(carla, vehicle));
        Assert.Equal(vehicle, carla.PublishedRenderSetOf(last)!.Lent[body].VehicleId);
        ObservedPoseSource source = carla.PublishedPoseSourceOf(last)!;
        Assert.True(source.StepFallsOn(last));
        Assert.Equal(PoseSource.Sumo, source.Of(body, last));

        // Where SUMO put it at that step, with its signals, at fraction zero; moving at SUMO's speed to within
        // the bridge's gate, the body's velocity being its bumper's path, as every body's is.
        CoSimPoseRecord drawn = Assert.Single(poses, record => record.Pose.VehicleId == vehicle
                                                               && (ulong)record.TickIndex + 1 == last);
        Assert.Equal(body, drawn.Actor);
        Assert.Equal(lastStep.FrameSeconds, drawn.SimulatedTimeSeconds, 6);
        Assert.True(Distance(drawn.SumoX, drawn.SumoY, there.X, there.Y) < 0.01,
                    $"{vehicle} drawn {Distance(drawn.SumoX, drawn.SumoY, there.X, there.Y):0.0000} m from where SUMO last had it");
        Assert.True(Math.Abs(there.SpeedMetresPerSecond - Speed(drawn.Pose)) < 0.01,
                    $"{vehicle} drawn at {Speed(drawn.Pose):0.0000} m/s, SUMO had {there.SpeedMetresPerSecond:0.0000} m/s");
        Assert.Equal(there.Signals, drawn.Signals);
        Assert.Equal(there.HeadingDegrees, drawn.SumoAngleDegrees, 6);

        // The bridge's divergence covers that frame: the world holds the pose commanded for it.
        PoseDivergence compared = Assert.Single(timeline.Divergences, divergence => divergence.VehicleId == vehicle
                                                                                    && divergence.TickIndex == drawn.TickIndex);
        _output.WriteLine($"  {Distance(drawn.SumoX, drawn.SumoY, there.X, there.Y):0.000000} m from SUMO's point, "
                          + $"divergence {compared.PositionMetres:0.000000} m and {compared.VelocityMetresPerSecond:0.000000} m/s");
        Assert.True(compared.PositionMetres < 0.01, $"{compared.PositionMetres} m");
        Assert.True(compared.VelocityMetresPerSecond < 0.01, $"{compared.VelocityMetresPerSecond} m/s");

        // Gone from the next frame: parked, and named parked to the server before that frame's cue.
        Assert.Contains(body, carla.PublishedRenderSetOf(last + 1)!.Parked);
        Assert.Contains(carla.RenderSetWrites, write => write.Parked.Contains(body) && write.AtTick == (long)last);
        Assert.Contains(carla.DrivenBatches, entry => entry.AtTick == (long)last
            && entry.Batch.OfType<ApplyTransformCommand>().Any(transform => transform.Actor == body
                                                                            && transform.Transform.Location.Z < -100f));

        // Never stale, on any frame it was drawn.
        for (ulong frame = 1; frame <= last; frame++)
        {
            if (carla.PublishedRenderSetOf(frame)?.Lent.TryGetValue(body, out var lent) == true && lent.VehicleId == vehicle)
            {
                Assert.NotEqual(PoseSource.Stale, carla.PublishedPoseSourceOf(frame)!.Of(body, frame));
            }
        }

        Assert.All(carla.PoseSourceWrites, write => Assert.DoesNotContain(body, write.Change.Stale));

        // Its interval ends at the next frame's instant.
        Assert.Equal(timeline.InstantOf(last) + worldDelta, interval.ReleasedAtSeconds, 9);
        Assert.Equal(timeline.InstantOf(last + 1), interval.ReleasedAtSeconds, 9);
    }

    /// <summary>The last frame whose published render set lends a body to the vehicle, and that vehicle.</summary>
    private static (string Vehicle, ulong Frame) LastLent(RecordedWorld carla, string vehicle)
    {
        ulong last = 0;
        for (ulong frame = 1; frame <= (ulong)carla.Ticks; frame++)
        {
            if (carla.PublishedRenderSetOf(frame)?.Lent.Values.Any(lent => lent.VehicleId == vehicle) == true)
            {
                last = frame;
            }
        }

        return (vehicle, last);
    }

    /// <summary>
    /// Advance until the vehicle's interval has been handed out, and two steps more, so the frames after
    /// its last are rendered.
    /// </summary>
    private static void AdvanceUntilReleasedAndTwoStepsOn(SumoDriveSession session, List<RenderedVehicleInterval> released,
                                                         string vehicle)
    {
        int after = 0;
        for (int step = 0; step < 4000 && after < 2 && session.Advance(); step++)
        {
            if (released.Any(interval => interval.VehicleId == vehicle))
            {
                after++;
            }
        }

        Assert.Contains(released, interval => interval.VehicleId == vehicle);
    }

    /// <summary>Every change to the render set the server was told of: the bodies lent, to whom, and those parked, by tick.</summary>
    private static List<string> Lending(Drive drive) =>
        [.. drive.Carla.RenderSetWrites.Select(write =>
            $"{write.AtTick}: lent "
            + string.Join(",", write.Lent.Select(lent => $"{lent.Actor}={lent.VehicleId}"))
            + " parked " + string.Join(",", write.Parked))];

    /// <summary>
    /// The hand-on scenario on the fixture cross: <c>leaver</c> alone, or with <c>newcomer</c> inserted a step
    /// before <paramref name="leaverLastReported"/>, so SUMO first reports it then, and <c>successor</c> inserted
    /// a step later.
    /// </summary>
    private string HandOnScenario(double? leaverLastReported)
    {
        string later = leaverLastReported is not { } last
            ? string.Empty
            : "    <vehicle id=\"newcomer\" type=\"measured_truck\" route=\"straight\" departLane=\"0\" departSpeed=\"max\" "
              + $"depart=\"{(last - 0.05).ToString("0.00", CultureInfo.InvariantCulture)}\"/>\n"
              + "    <vehicle id=\"successor\" type=\"measured_truck\" route=\"straight\" departLane=\"1\" departSpeed=\"max\" "
              + $"depart=\"{last.ToString("0.00", CultureInfo.InvariantCulture)}\"/>\n";
        string directory = Path.Combine(_directory, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.Copy(CoSimFixtures.RightAngleTurnNetwork, Path.Combine(directory, "RightAngleTurn.net.xml"));
        File.WriteAllText(Path.Combine(directory, "Succession.rou.xml"),
                          "<routes>\n"
                          + "    <vType id=\"measured_truck\" vClass=\"truck\" length=\"7.0184\" width=\"2.5074\" "
                          + "maxSpeed=\"30.00\" accel=\"2.60\" decel=\"4.50\" sigma=\"0\" tau=\"1.0\">"
                          + "<param key=\"carla:blueprint\" value=\"vehicle.fuso.mitsubishi\"/></vType>\n"
                          + "    <route id=\"straight\" edges=\"approach ahead\"/>\n"
                          + "    <vehicle id=\"leaver\" type=\"measured_truck\" route=\"straight\" depart=\"0.00\" "
                          + "departLane=\"1\" departSpeed=\"max\"/>\n"
                          + later
                          + "</routes>\n");
        string path = Path.Combine(directory, "scenario.sumocfg");
        File.Copy(CoSimFixtures.SuccessionScenario, path);
        return path;
    }

    /// <summary>The last instant, by TraCI's clock, a SUMO running the scenario alone reports the vehicle at.</summary>
    private static double LastReported(string scenario, string vehicle)
    {
        using SumoConnection sumo = CoSimFixtures.Open(scenario);
        double last = double.NaN;
        for (int step = 0; step < 2000; step++)
        {
            if (sumo.Vehicles.Ids.Contains(vehicle))
            {
                last = sumo.Time;
            }
            else if (!double.IsNaN(last))
            {
                return last;
            }

            sumo.Step();
        }

        throw new Xunit.Sdk.XunitException($"{vehicle} never left the network");
    }

    private Drive Run(SyntheticWorld world, string scenario, double worldDelta, int steps)
    {
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var timeline = new LeavingTimeline();
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = Options(world, scenario, worldDelta, carla, timeline, released, []);
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < steps && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
        }

        return new Drive(carla, timeline, released);
    }

    private static SumoDriveSessionOptions Options(SyntheticWorld world, string scenario, double worldDelta,
                                                   RecordedWorld carla, LeavingTimeline timeline,
                                                   List<RenderedVehicleInterval> released, List<CoSimPoseRecord> poses)
    {
        var options = new SumoDriveSessionOptions(scenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
                                                  "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            WorldDeltaSeconds = worldDelta,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            OnPose = poses.Add,
            OnRelease = released.Add,
            OnDivergence = timeline.Divergences.Add,
        };
        options.StepObservers.Add(timeline);
        return options;
    }

    private static double Distance(double x0, double y0, double x1, double y1) =>
        Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));

    private static double Speed(in VehiclePose pose) =>
        Math.Sqrt((pose.VelocityX * pose.VelocityX) + (pose.VelocityY * pose.VelocityY)
                  + (pose.VelocityZ * pose.VelocityZ));

    /// <summary>What one drive produced: the world it drew in, what SUMO reported and every interval handed out.</summary>
    private sealed record Drive(RecordedWorld Carla, LeavingTimeline Timeline, List<RenderedVehicleInterval> Released)
    {
        /// <summary>The body the server's render set lends the vehicle on a frame, or null.</summary>
        public ActorId? BodyOf(ulong frame, string vehicle) =>
            Carla.PublishedRenderSetOf(frame)?.Lent.FirstOrDefault(lent => lent.Value.VehicleId == vehicle) is { Key: not 0 } found
                ? found.Key
                : null;

        public ulong FirstFrameDrawing(string vehicle) =>
            Timeline.Frames.First(frame => BodyOf(frame.Frame, vehicle) is not null).Frame;

        public ulong LastFrameDrawing(string vehicle) =>
            Timeline.Frames.Last(frame => BodyOf(frame.Frame, vehicle) is not null).Frame;
    }
}

/// <summary>
/// What a session told an observer, kept whole: every SUMO frame it read, with every vehicle's state and what
/// SUMO listed, every frame it rendered, every interval handed out with a step, and every divergence taken.
/// </summary>
internal sealed class LeavingTimeline : ISumoStepObserver
{
    public List<Step> Steps { get; } = [];

    public List<RenderedFrameRecord> Frames { get; } = [];

    public List<RenderedVehicleInterval> Released { get; } = [];

    public List<PoseDivergence> Divergences { get; } = [];

    public Step FirstStepWith(string vehicle) => Steps.First(step => step.Frames.ContainsKey(vehicle));

    public Step LastStepWith(string vehicle) => Steps.Last(step => step.Frames.ContainsKey(vehicle));

    /// <summary>The frame rendered at an instant.</summary>
    public ulong FrameAt(double instant) =>
        Frames.First(frame => Math.Abs(frame.SimulatedTimeSeconds - instant) < 1e-6).Frame;

    /// <summary>The instant a frame was rendered at.</summary>
    public double InstantOf(ulong frame) => Frames.First(seen => seen.Frame == frame).SimulatedTimeSeconds;

    public void OnSumoStep(SumoStepRecord step)
    {
        Steps.Add(new Step(step.FrameSeconds, step.Frames.ToDictionary(pair => pair.Key, pair => pair.Value),
                           [.. step.Vanished], [.. step.Events.Arrived], [.. step.Events.TeleportsStarted]));
        Released.AddRange(step.Released);
    }

    public void OnFrameRendered(RenderedFrameRecord frame) => Frames.Add(frame);

    public void OnSessionEnded(SessionEndRecord end) => Released.AddRange(end.Released);

    /// <summary>One SUMO frame as the session read it.</summary>
    internal sealed record Step(
        double FrameSeconds,
        Dictionary<string, CoSimVehicleFrame> Frames,
        HashSet<string> Vanished,
        List<string> Arrived,
        List<string> TeleportsStarted);
}
