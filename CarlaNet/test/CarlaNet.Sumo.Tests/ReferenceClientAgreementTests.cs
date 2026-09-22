using System.Diagnostics;
using System.Globalization;
using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// Differences this client's readings against SUMO's own client's, on the same scenario and the
/// same seed.
/// </summary>
/// <remarks>
/// <para>This is the check that establishes the decode. A round trip through this client's own
/// writer proves the two halves agree with each other, which they would even if both were wrong
/// about the protocol. The reference client is a decade-old implementation of the same wire format
/// by the people who defined it, and two independent decoders of the same frames do not agree by
/// accident.</para>
///
/// <para>It crosses the read-path boundary at the same time: the reference side reads each variable
/// of each vehicle directly, the way <c>SumoCotBridge</c> does, while this side reads the whole
/// population by subscription. So a disagreement is either a decode error or a difference between
/// what SUMO delivers by subscription and what it answers to a get -- and both are worth knowing.</para>
///
/// <para>Skipped where the SUMO installation carries no Python tools, or where no Python is on the
/// executable search path. Neither is a failure of this client: it needs neither to run.</para>
/// </remarks>
public class ReferenceClientAgreementTests(ITestOutputHelper output)
{
    /// <summary>
    /// Steps to compare on the fixture network. Long enough that both vehicles depart, accelerate
    /// and run the length of the edge, so the rows cover a moving vehicle rather than a stationary
    /// one.
    /// </summary>
    private const int FixtureSteps = 400;

    /// <summary>
    /// How far two readings of the same quantity may differ. Both sides decode the same IEEE-754
    /// doubles out of the same deterministic simulation, so this is a guard against a formatting
    /// round trip, not a tolerance on the physics.
    /// </summary>
    private const double Tolerance = 1e-9;

    [RequiresSumoFact(withPythonTools: true)]
    public void EveryReadingAgreesWithSumosOwnClient()
    {
        // Both fixture vehicles run the length of the edge inside the window, so a short comparison
        // means a recorder stopped early -- which would otherwise pass as agreement about almost
        // nothing.
        Difference(SumoFixtures.TwoVehicles, warmup: 0, steps: FixtureSteps, minimumRows: 400);
    }

    /// <summary>
    /// The same check against a real scenario, opt-in. Two vehicles on a straight edge exercise the
    /// codec; a few hundred through junctions, lane changes and signals exercise the values.
    /// </summary>
    [NamedScenarioFact]
    public void EveryReadingAgreesOnTheScenarioTheEnvironmentNames()
    {
        Difference(NamedScenarioFactAttribute.Scenario!,
                   NamedScenarioFactAttribute.Warmup,
                   NamedScenarioFactAttribute.Steps,
                   minimumRows: 1);
    }

    /// <summary>
    /// The check is pointed at a disagreement and watched to report it. A comparison that has never
    /// rejected anything establishes nothing about the agreement it claims.
    /// </summary>
    [Fact]
    public void TheComparisonReportsADisagreement()
    {
        Row reference = new(3, "first", 12.5, -1.6, 90.0, 4.25, "west_to_east", "west_to_east_0", "car", 0);
        Row shifted = reference with { X = 12.5000001 };
        Row relabelled = reference with { LaneId = "west_to_east_1" };

        List<string> disagreements = [];
        Compare(reference, shifted, disagreements);
        Compare(reference, relabelled, disagreements);
        Compare(reference, reference, disagreements);

        Assert.Equal(2, disagreements.Count);
        Assert.Contains(disagreements, entry => entry.Contains("x", StringComparison.Ordinal));
        Assert.Contains(disagreements, entry => entry.Contains("lane", StringComparison.Ordinal));
    }

    /// <summary>One vehicle's state at one step, as both recorders write it.</summary>
    private readonly record struct Row(
        int Step,
        string Id,
        double X,
        double Y,
        double Angle,
        double Speed,
        string EdgeId,
        string LaneId,
        string TypeId,
        int Signals);

    private void Difference(string scenario, double warmup, int steps, int minimumRows)
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        string? python = FindPython();
        if (python is null)
        {
            // Nothing to difference against on this machine. Recorded as passing rather than
            // failing, because the client under test needs no Python at all.
            output.WriteLine("No python on the executable search path; nothing was compared.");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("traci-agreement").FullName;
        try
        {
            string referencePath = Path.Combine(directory, "reference.csv");
            RunReferenceRecorder(python, installation, scenario, warmup, steps, referencePath);

            IReadOnlyList<Row> reference = ReadRows(referencePath);
            IReadOnlyList<Row> ours = RecordWithThisClient(installation, scenario, warmup, steps);

            Assert.True(reference.Count >= minimumRows,
                        $"The reference recorder wrote only {reference.Count} rows.");
            Assert.Equal(reference.Count, ours.Count);

            List<string> disagreements = [];
            for (int index = 0; index < reference.Count; index++)
            {
                Compare(reference[index], ours[index], disagreements);
            }

            output.WriteLine($"{reference.Count} vehicle-steps compared over {steps} steps of "
                             + $"{Path.GetFileName(scenario)}, {disagreements.Count} disagreements.");
            Assert.Empty(disagreements);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Compare(Row expected, Row actual, List<string> disagreements)
    {
        if (expected.Step != actual.Step || expected.Id != actual.Id)
        {
            disagreements.Add($"row {expected.Step}/{expected.Id} lines up with "
                              + $"{actual.Step}/{actual.Id}");
            return;
        }

        string where = $"step {expected.Step} vehicle {expected.Id}";
        CompareNumber(where, "x", expected.X, actual.X, disagreements);
        CompareNumber(where, "y", expected.Y, actual.Y, disagreements);
        CompareNumber(where, "angle", expected.Angle, actual.Angle, disagreements);
        CompareNumber(where, "speed", expected.Speed, actual.Speed, disagreements);
        CompareText(where, "edge", expected.EdgeId, actual.EdgeId, disagreements);
        CompareText(where, "lane", expected.LaneId, actual.LaneId, disagreements);
        CompareText(where, "type", expected.TypeId, actual.TypeId, disagreements);
        CompareText(where, "signals", expected.Signals.ToString(CultureInfo.InvariantCulture),
                    actual.Signals.ToString(CultureInfo.InvariantCulture), disagreements);
    }

    private static void CompareNumber(string where, string field, double expected, double actual,
                                      List<string> disagreements)
    {
        if (Math.Abs(expected - actual) > Tolerance)
        {
            disagreements.Add($"{where}: {field} is {expected} upstream and {actual} here");
        }
    }

    private static void CompareText(string where, string field, string expected, string actual,
                                    List<string> disagreements)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            disagreements.Add($"{where}: {field} is '{expected}' upstream and '{actual}' here");
        }
    }

    /// <summary>
    /// Drive the same scenario through this client, subscribing each vehicle as SUMO inserts it and
    /// releasing it as SUMO removes it -- which is the shape the co-simulation bridge runs.
    /// </summary>
    private static IReadOnlyList<Row> RecordWithThisClient(SumoInstallation installation,
                                                           string scenario,
                                                           double warmup,
                                                           int steps)
    {
        using SumoConnection sumo = SumoConnection.Start(
            installation, scenario,
            new SumoLaunchOptions
            {
                Output = _ => { },
                ExtraArguments = ["--no-step-log", "true"],
            });

        if (warmup > 0)
        {
            sumo.Step(warmup);
            foreach (string running in sumo.Vehicles.Ids)
            {
                sumo.Vehicles.Subscription.Add(running);
            }
        }

        List<Row> rows = [];
        Dictionary<string, SumoVehicleState> states = [];
        for (int step = 0; step < steps; step++)
        {
            sumo.Step();

            foreach (string departed in sumo.Simulation.DepartedVehicleIds)
            {
                sumo.Vehicles.Subscription.Add(departed);
            }

            foreach (string arrived in sumo.Simulation.ArrivedVehicleIds)
            {
                sumo.Vehicles.Subscription.Release(arrived);
            }

            // A vehicle inserted during this step was not subscribed while the step ran, so its
            // first state arrives with the next one. The reference recorder sees it a step earlier,
            // which is a difference in when the two were asked and not in what SUMO said, so this
            // side reads it directly for that one step.
            sumo.Vehicles.Subscription.Read(states);
            foreach (string vehicleId in sumo.Simulation.DepartedVehicleIds)
            {
                states.TryAdd(vehicleId, sumo.Vehicles.ReadStateWithoutSubscription(vehicleId));
            }

            foreach (string vehicleId in states.Keys.Order(StringComparer.Ordinal))
            {
                SumoVehicleState state = states[vehicleId];
                rows.Add(new Row(step, state.Id, state.X, state.Y, state.HeadingDegrees,
                                 state.SpeedMetresPerSecond, state.EdgeId, state.LaneId,
                                 state.TypeId, (int)state.Signals));
            }
        }

        return rows;
    }

    private static void RunReferenceRecorder(string python,
                                             SumoInstallation installation,
                                             string scenario,
                                             double warmup,
                                             int steps,
                                             string output)
    {
        string recorder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReferenceStateRecorder.py");
        ProcessStartInfo start = new(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(recorder);
        start.ArgumentList.Add("--tools");
        start.ArgumentList.Add(installation.ToolsDirectory);
        start.ArgumentList.Add("--sumo");
        start.ArgumentList.Add(installation.Sumo);
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add("--steps");
        start.ArgumentList.Add(steps.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--warmup");
        start.ArgumentList.Add(warmup.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);

        using Process process = Process.Start(start)!;
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0,
                    $"The reference recorder failed:\n{standardOutput}\n{standardError}");
    }

    private static IReadOnlyList<Row> ReadRows(string path)
    {
        List<Row> rows = [];
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] fields = line.Split(',');
            if (fields.Length != 10)
            {
                continue;
            }

            rows.Add(new Row(
                int.Parse(fields[0], CultureInfo.InvariantCulture),
                fields[1],
                double.Parse(fields[2], CultureInfo.InvariantCulture),
                double.Parse(fields[3], CultureInfo.InvariantCulture),
                double.Parse(fields[4], CultureInfo.InvariantCulture),
                double.Parse(fields[5], CultureInfo.InvariantCulture),
                fields[6],
                fields[7],
                fields[8],
                int.Parse(fields[9], CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private static string? FindPython()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        string[] names = OperatingSystem.IsWindows() ? ["python.exe"] : ["python3", "python"];
        foreach (string entry in path.Split(Path.PathSeparator))
        {
            foreach (string name in names)
            {
                if (entry.Length > 0 && File.Exists(Path.Combine(entry, name)))
                {
                    return Path.Combine(entry, name);
                }
            }
        }

        return null;
    }
}
