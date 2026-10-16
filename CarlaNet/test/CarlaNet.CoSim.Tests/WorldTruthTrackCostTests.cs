using System.Diagnostics;
using CarlaNet.Recording;
using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Measures what the world truth track costs a SUMO step at a city's population: every vehicle's row
/// composed and written, and the frame's rows forced to disk.
/// </summary>
/// <remarks>
/// <para>The track writes a row for every vehicle SUMO has at every SUMO frame of the window, on the
/// tick thread, so what it costs is paid in every step's wall clock. Driven by hand with the records a
/// session would tell it -- four hundred vehicles moving across the ground grid, every one drawn, a sun
/// on every frame and a civil clock -- so the number is the writer's alone, with nothing of SUMO's or
/// the world's in it.</para>
///
/// <para>Opt-in (<see cref="MeasurementFactAttribute"/>), and it reports rather than asserts beyond
/// every row being written: the run writes some fifty megabytes, and the judgement about whether the
/// number is good enough belongs to whoever is reading it.</para>
/// </remarks>
public sealed class WorldTruthTrackCostTests(ITestOutputHelper output) : IDisposable
{
    private const int Population = 400;
    private const int TypeCount = 8;
    private const int WarmUpSteps = 50;
    private const int MeasuredSteps = 300;
    private const double SumoStepSeconds = 0.1;
    private const double WorldDeltaSeconds = 0.05;

    private readonly string _directory = Path.Combine(Path.GetTempPath(),
                                                      "carlanet-track-cost-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is not the measurement's failure.
        }
    }

    [MeasurementFact]
    public void TheCostOfWritingTheWorldTruthTrackIsMeasured()
    {
        Directory.CreateDirectory(_directory);
        string track = Path.Combine(_directory, "world_truth_track.csv");
        CoSimClock clock = CoSimClock.ForSession(SumoStepSeconds, WorldDeltaSeconds, 2.0, true);
        int ticksPerStep = clock.WorldTicksPerSumoStep;
        var frames = new Dictionary<string, CoSimVehicleFrame>(StringComparer.Ordinal);
        string[] ids = [.. Enumerable.Range(0, Population).Select(index => $"route_{index % 40}.{index}")];
        // Every vehicle drawn, as with no render-set limit: the set a session hands out until a body
        // is lent or given back.
        var drawn = new RenderSet(ids.Select((id, index) => new RenderedVehicle(
                                      (uint)(1000 + index), id, TypeOf(index), 1)));
        double[] perStep = new double[MeasuredSteps];
        long rows;

        using (WorldTruthTrackWriter writer = WorldTruthTrackWriter.Open(
                   track, 1, clock,
                   _ => new WorldTruthVehicleType("passenger", "car", 4.6, 1.8, 1.5, "255,255,0", string.Empty, null),
                   SyntheticWorld.Build(at => 5.0 + (0.01 * at.X) + (0.02 * at.Y)), (35.7, 51.4),
                   SolarLeaseTests.PortEpoch()))
        {
            // A SUMO frame is told a step ahead of the frame that renders it.
            writer.OnSumoStep(StepAt(0, frames, ids));
            writer.OnSumoStep(StepAt(1, frames, ids));
            for (int step = 0; step < WarmUpSteps + MeasuredSteps; step++)
            {
                RenderedFrameRecord[] ticks = [.. Enumerable.Range(0, ticksPerStep).Select(tick => FrameAt(
                                                   step, tick, ticksPerStep, drawn))];
                SumoStepRecord next = StepAt(step + 2, frames, ids);

                long started = Stopwatch.GetTimestamp();
                foreach (RenderedFrameRecord tick in ticks)
                {
                    writer.OnFrameRendered(tick);
                }

                writer.OnSumoStep(next);
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (step >= WarmUpSteps)
                {
                    perStep[step - WarmUpSteps] = milliseconds;
                }
            }

            rows = writer.Rows;
            writer.OnSessionEnded(new SessionEndRecord(
                (WarmUpSteps + MeasuredSteps + 1) * SumoStepSeconds, null, null, false, null));
        }

        Assert.Equal((long)Population * (WarmUpSteps + MeasuredSteps), rows);
        double[] sorted = [.. perStep.Order()];
        double mean = perStep.Average();
        output.WriteLine($"vehicles per SUMO frame   {Population}, every one drawn");
        output.WriteLine($"measured                  {MeasuredSteps} SUMO steps after {WarmUpSteps} unmeasured");
        output.WriteLine($"bytes per row             {new FileInfo(track).Length / (double)rows:0}");
        output.WriteLine(string.Empty);
        output.WriteLine($"per SUMO step, mean       {mean:0.000} ms");
        output.WriteLine($"per SUMO step, median     {sorted[sorted.Length / 2]:0.000} ms");
        output.WriteLine($"per SUMO step, 95th pct   {sorted[(int)(sorted.Length * 0.95)]:0.000} ms");
        output.WriteLine($"per row, mean             {mean * 1000.0 / Population:0.00} us");
    }

    private static string TypeOf(int index) => $"type_{index % TypeCount}";

    /// <summary>
    /// The SUMO frame at a step: every vehicle going round its own circle about the grid's centre, so
    /// each row's position, course and ground differ from the frame before.
    /// </summary>
    private static SumoStepRecord StepAt(int step, Dictionary<string, CoSimVehicleFrame> frames, string[] ids)
    {
        double frameSeconds = step * SumoStepSeconds;
        frames.Clear();
        for (int index = 0; index < ids.Length; index++)
        {
            double radius = 10.0 + (index % 80);
            double speed = 8.0 + (index % 8);
            double angle = (index * 0.37) + (speed * frameSeconds / radius);
            double heading = ((90.0 - (angle * 180.0 / Math.PI) + 270.0) % 360.0 + 360.0) % 360.0;
            frames[ids[index]] = new CoSimVehicleFrame(
                ids[index], radius * Math.Cos(angle), radius * Math.Sin(angle), heading, speed,
                $"edge_{index % 60}", $"edge_{index % 60}_{index % 3}", 12.5 + (speed * frameSeconds), TypeOf(index),
                default);
        }

        return new SumoStepRecord(
            (long)step * 2, frameSeconds, false, SumoStepEvents.None(frameSeconds), [], [], [], frames, frames.Keys,
            new AdmissionPass((long)step * 2, frameSeconds, ids.Length, 0, 0, ids.Length), null!);
    }

    /// <summary>One tick of a step, with the step's every vehicle drawn and the sun the world reported.</summary>
    private static RenderedFrameRecord FrameAt(int step, int tick, int ticksPerStep, RenderSet drawn)
    {
        long tickIndex = ((long)step * ticksPerStep) + tick;
        double elevation = 20.0 + (tickIndex * 0.0001);
        return new RenderedFrameRecord(
            (ulong)tickIndex + 1, tickIndex, (step * SumoStepSeconds) + (tick * WorldDeltaSeconds), false, true,
            drawn, null)
        {
            Sun = new SolarReading(7.0, 2026, 3, 21, 3.5, 35.7, 51.4, elevation, 95.0, false, 0.0, elevation + 0.04),
        };
    }
}
