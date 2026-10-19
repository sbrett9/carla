using CarlaNet.Recording;
using CarlaNet.Sumo;
using CarlaNet.Types.Rpc.Commands;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A vehicle SUMO inserts during the run is drawn from the frame SUMO first reports it in, at the
/// position SUMO reported and moving from there, and on no tick before it. A vehicle SUMO already has
/// when the session starts rendering is drawn on the first rendered frame.
/// </summary>
/// <remarks>
/// <para>SUMO is stepped a whole second at a time over the cross, twenty world ticks to a step and a
/// capture every ten, which is what a capture scenario runs at. The defect these guard against was
/// measured live at that step on Bahonar: a vehicle drawn one SUMO step before SUMO inserted it,
/// standing at its insertion point for three captures while the truth beside it reported SUMO's speed,
/// then moving off at full speed.</para>
///
/// <para>Fast-forwarded to two seconds, <c>turner</c> is on the network when the first frame renders;
/// <c>goer</c> is inserted in the step of lookahead the session reads while it starts, <c>changer</c>
/// part-way through the run, and <c>unrenderable</c>, whose type names no measured body, later still.
/// What SUMO reports at each frame comes from a second SUMO running the same scenario at the same step
/// alone.</para>
/// </remarks>
public sealed class SumoDriveSessionInsertionTests
{
    private const double StepSeconds = 1.0;
    private const double WarmUpSeconds = 2.0;
    private const int Steps = 12;

    private readonly ITestOutputHelper _output;

    public SumoDriveSessionInsertionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void AVehicleSumoInsertsIsFirstDrawnOnItsInsertionFrameWhereSumoPutItAndAlreadyMoving()
    {
        Drive drive = Run(windowOpensAt: null);
        Dictionary<long, Dictionary<string, Reported>> sumo = WhatSumoReports();
        Dictionary<string, double> insertedAt = FirstReported(sumo);

        // One inserted in the step of lookahead read while the session starts, one part-way through.
        Assert.Equal(WarmUpSeconds + StepSeconds, insertedAt["goer"], 9);
        Assert.True(insertedAt["changer"] > WarmUpSeconds + (2 * StepSeconds),
                    $"changer was inserted at {insertedAt["changer"]} s, not part-way through the run");

        foreach (string vehicle in (string[])["goer", "changer"])
        {
            double at = insertedAt[vehicle];
            Reported there = sumo[Key(at)][vehicle];
            long tick = drive.TickAt(at);
            ulong frame = Frame(tick);
            List<CoSimPoseRecord> drawn = [.. drive.Poses.Where(record => record.Pose.VehicleId == vehicle)
                                                         .OrderBy(record => record.TickIndex)];
            CoSimPoseRecord first = drawn[0];
            _output.WriteLine($"{vehicle}: inserted at {at} s, tick {tick}, first posed at tick {first.TickIndex}");

            // Before its insertion frame it is nowhere: no pose, nothing in any batch at the point SUMO
            // inserts it, and in no frame's render set.
            Assert.Equal(tick, first.TickIndex);
            Assert.DoesNotContain(drive.Carla.DrivenBatches, entry => entry.AtTick < tick
                && entry.Batch.OfType<ApplyTransformCommand>().Any(transform =>
                    Math.Abs(transform.Transform.Location.X - first.Pose.X) < 0.5
                    && Math.Abs(transform.Transform.Location.Y - first.Pose.Y) < 0.5));
            for (ulong earlier = 1; earlier < frame; earlier++)
            {
                Assert.DoesNotContain(drive.RenderSets[earlier].ByActor.Values, rendered => rendered.SumoId == vehicle);
            }

            // On its insertion frame it is where SUMO reported it, at the speed SUMO gave it.
            Assert.Equal(at, first.SimulatedTimeSeconds, 6);
            Assert.True(Distance(first.SumoX, first.SumoY, there.X, there.Y) < 0.05,
                        $"{vehicle} first drawn {Distance(first.SumoX, first.SumoY, there.X, there.Y):0.000} m "
                        + "from where SUMO inserted it");
            Assert.True(there.Speed > 1.0, $"SUMO inserted {vehicle} at {there.Speed} m/s");
            Assert.Equal(there.Speed, Speed(first.Pose), 6);

            // And moving from that frame: every tick of its first step moves it, and by the next capture
            // it has covered at least half of what its insertion speed would carry it.
            CoSimPoseRecord[] firstStep = [.. drawn.Take(drive.TicksPerStep + 1)];
            Assert.Equal(Enumerable.Range(0, drive.TicksPerStep + 1).Select(offset => tick + offset),
                         firstStep.Select(record => record.TickIndex));
            Assert.All(firstStep.Zip(firstStep.Skip(1)), pair => Assert.True(
                Distance(pair.First.Pose.X, pair.First.Pose.Y, pair.Second.Pose.X, pair.Second.Pose.Y) > 0.1,
                $"{vehicle} stood still from tick {pair.First.TickIndex} to {pair.Second.TickIndex}"));
            CoSimPoseRecord captured = drawn.First(record => record.TickIndex > tick && record.IsCaptureTick);
            double elapsed = (captured.TickIndex - tick) * drive.WorldDeltaSeconds;
            Assert.True(Distance(first.Pose.X, first.Pose.Y, captured.Pose.X, captured.Pose.Y) > 0.5 * there.Speed * elapsed,
                        $"{vehicle} had moved {Distance(first.Pose.X, first.Pose.Y, captured.Pose.X, captured.Pose.Y):0.00} m "
                        + $"by the capture {elapsed} s after its first frame");

            // Its admission, the render set's first listing of it and its rendered span all begin there.
            Assert.Equal(frame, Assert.Single(drive.RenderSets[frame].ByActor.Values,
                                              rendered => rendered.SumoId == vehicle).AdmittedTick);
            Assert.Equal(at, Assert.Single(drive.Released, interval => interval.VehicleId == vehicle).AdmittedAtSeconds, 9);
            Assert.Equal(1, Assert.Single(drive.Passes, pass => Key(pass.SimulatedTimeSeconds) == Key(at)).NewlyAdmitted);
        }

        // A vehicle with no measured body is still never drawn, and its vehicle-ticks are counted from
        // the same frame a measured one's drawing would begin on.
        long unrenderable = drive.TickAt(insertedAt["unrenderable"]);
        Assert.InRange(unrenderable, 1, drive.Ticks - 1);
        Assert.DoesNotContain(drive.Poses, record => record.Pose.VehicleId == "unrenderable");
        Assert.All(drive.RenderSets.Values, set => Assert.DoesNotContain(
            set.ByActor.Values, rendered => rendered.SumoId == "unrenderable"));
        Assert.Equal(drive.Ticks - unrenderable, drive.VehicleTicksWithNoMeasuredBody);
    }

    [RequiresSumoFact]
    public void EveryVehicleSumoHasWhenRenderingBeginsIsDrawnOnTheFirstFrameAndTheWindowOpensOnThemAll()
    {
        // Two seconds of prewarm: the window opens at four, rendered from two.
        const double windowOpens = 4.0;
        Drive drive = Run(windowOpensAt: windowOpens);
        Dictionary<long, Dictionary<string, Reported>> sumo = WhatSumoReports();

        // What SUMO has at the fast-forward's frame is drawn on the very first frame, where SUMO had it.
        Dictionary<string, Reported> atStart = sumo[Key(WarmUpSeconds)];
        Assert.Contains("turner", atStart.Keys);
        foreach ((string vehicle, Reported there) in atStart)
        {
            CoSimPoseRecord first = drive.Poses.Where(record => record.Pose.VehicleId == vehicle)
                                               .MinBy(record => record.TickIndex);
            Assert.Equal(0, first.TickIndex);
            Assert.True(Distance(first.SumoX, first.SumoY, there.X, there.Y) < 0.05,
                        $"{vehicle} first drawn away from where SUMO had it");
            Assert.Equal(1ul, Assert.Single(drive.RenderSets[1].ByActor.Values,
                                            rendered => rendered.SumoId == vehicle).AdmittedTick);
            Assert.Equal(WarmUpSeconds, Assert.Single(drive.Released, interval => interval.VehicleId == vehicle)
                                            .AdmittedAtSeconds, 9);
        }

        // The window's first frame draws every vehicle SUMO has at its instant, each already drawn on
        // the prewarm's frames: none of them appears on it.
        ulong opening = Frame(drive.TickAt(windowOpens));
        HashSet<string> living = [.. sumo[Key(windowOpens)].Keys.Where(vehicle => vehicle != "unrenderable")];
        Assert.True(living.Count >= 2, "the window opens on fewer than two vehicles");
        Assert.Equal(living.Order(StringComparer.Ordinal),
                     drive.RenderSets[opening].ByActor.Values.Select(rendered => rendered.SumoId).Order(StringComparer.Ordinal));
        Assert.All(drive.RenderSets[opening].ByActor.Values, rendered => Assert.True(
            rendered.AdmittedTick < opening, $"{rendered.SumoId} first appears on the window's first frame"));
    }

    /// <summary>One run of the fixture, kept whole: every pose, every frame's render set, every interval and pass.</summary>
    private Drive Run(double? windowOpensAt)
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        List<RenderedVehicleInterval> released = [];
        List<AdmissionPass> passes = [];
        Dictionary<ulong, RenderSet> sets = [];
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            SumoStepOverrideSeconds = StepSeconds,
            WarmUpToSimulatedSecond = WarmUpSeconds,
            WindowOpensAtSimulatedSecond = windowOpensAt,
            OnPose = poses.Add,
            OnRelease = released.Add,
            OnAdmissionPass = passes.Add,
        };

        Drive drive;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < Steps && session.Advance(); step++)
            {
                // Every frame the step rendered, taken while the history still holds it.
                for (ulong frame = (ulong)sets.Count + 1; frame <= session.RenderSet.NewestFrame; frame++)
                {
                    Assert.True(session.RenderSet.TryGetRenderSet(frame, out RenderSet set), $"frame {frame} is not held");
                    sets[frame] = set;
                }
            }

            _output.WriteLine(session.Report.ToString());
            Assert.Equal(20, session.Clock.WorldTicksPerSumoStep);
            drive = new Drive(carla, poses, sets, released, passes, session.FirstRenderedSeconds,
                              session.Clock.WorldDeltaSeconds, session.Clock.WorldTicksPerSumoStep,
                              session.Report.Ticks, session.Report.VehicleTicksWithNoMeasuredBody);
        }

        // Disposed, so every vehicle still drawn has had its interval closed and handed out.
        return drive;
    }

    /// <summary>
    /// Every vehicle a SUMO running the fixture alone, at the same step, has at each frame -- where it is
    /// and how fast it is going -- by the frame's instant in milliseconds.
    /// </summary>
    private static Dictionary<long, Dictionary<string, Reported>> WhatSumoReports()
    {
        Dictionary<long, Dictionary<string, Reported>> frames = [];
        using SumoConnection sumo = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario, new SumoLaunchOptions
        {
            ExtraArguments = ["--step-length", "1"],
            Output = _ => { },
        });
        for (int step = 0; step <= WarmUpSeconds + Steps + 2; step++)
        {
            Dictionary<string, Reported> frame = [];
            foreach (string vehicle in sumo.Vehicles.Ids)
            {
                (double x, double y) = sumo.Vehicles.Position(vehicle);
                frame[vehicle] = new Reported(x, y, sumo.Vehicles.Speed(vehicle));
            }

            frames[Key(sumo.Time)] = frame;
            sumo.Step();
        }

        return frames;
    }

    /// <summary>The instant SUMO first reports each vehicle at.</summary>
    private static Dictionary<string, double> FirstReported(Dictionary<long, Dictionary<string, Reported>> frames)
    {
        Dictionary<string, double> first = [];
        foreach ((long millisecond, Dictionary<string, Reported> frame) in frames.OrderBy(entry => entry.Key))
        {
            foreach (string vehicle in frame.Keys)
            {
                first.TryAdd(vehicle, millisecond / 1000.0);
            }
        }

        return first;
    }

    /// <summary>The frame the recorded world numbers a tick's: its count of ticks once the tick is done.</summary>
    private static ulong Frame(long tick) => (ulong)tick + 1;

    private static long Key(double seconds) => (long)Math.Round(seconds * 1000.0);

    private static double Distance(double x0, double y0, double x1, double y1) =>
        Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));

    private static double Speed(in VehiclePose pose) =>
        Math.Sqrt((pose.VelocityX * pose.VelocityX) + (pose.VelocityY * pose.VelocityY)
                  + (pose.VelocityZ * pose.VelocityZ));

    /// <summary>Where SUMO says a vehicle's front bumper is, and its speed.</summary>
    private readonly record struct Reported(double X, double Y, double Speed);

    /// <summary>What one run produced.</summary>
    private sealed record Drive(
        RecordedWorld Carla,
        List<CoSimPoseRecord> Poses,
        Dictionary<ulong, RenderSet> RenderSets,
        List<RenderedVehicleInterval> Released,
        List<AdmissionPass> Passes,
        double FirstRenderedSeconds,
        double WorldDeltaSeconds,
        int TicksPerStep,
        long Ticks,
        long VehicleTicksWithNoMeasuredBody)
    {
        /// <summary>The tick that renders a simulated instant.</summary>
        public long TickAt(double seconds) => (long)Math.Round((seconds - FirstRenderedSeconds) / WorldDeltaSeconds);
    }
}
