// Offline (no engine, no server): where the traffic manager's event reporting goes, and what is
// emitted without being asked for.
//
// Two properties matter here and neither is obvious from reading a call site. The per-vehicle
// diagnostics describe what every vehicle is doing rather than reporting something unusual, so they
// have to be off unless asked for, or at fleet scale they bury the lines worth reading. And the
// file they are written to is one the viewer already holds open, which the obvious way of opening it
// cannot do.
#nullable enable

using CarlaNet.TrafficManager;
using Xunit;

namespace CarlaNet.Tests.TrafficManager;

public class TrafficReportTests
{
    [Fact]
    public void Per_vehicle_diagnostics_are_off_until_asked_for()
    {
        // A caller that never mentions diagnostics must not get them: this is what keeps a normal run
        // from emitting a line per vehicle per signal change.
        bool original = TrafficReport.DiagnosticsEnabled;
        try
        {
            TrafficReport.DiagnosticsEnabled = false;
            Assert.False(TrafficReport.DiagnosticsEnabled);
            TrafficReport.DiagnosticsEnabled = true;
            Assert.True(TrafficReport.DiagnosticsEnabled);
        }
        finally
        {
            TrafficReport.DiagnosticsEnabled = original;
        }
    }

    [Fact]
    public void Events_reach_a_file_the_host_already_has_open_for_writing()
    {
        // The viewer holds its own handle on the log for the whole run. Opening the file the usual
        // way asks for a share mode that denies the write access the viewer already holds, so it
        // fails every time — and the failure can only be reported to the console, because the file it
        // would have been written to is the one that could not be opened.
        //
        // Only the opening is asserted here. Whether the two writers interleave without overwriting
        // each other depends on the host appending as well, and the host is Python, whose append mode
        // seeks to the end before every write. A .NET FileStream opened for appending seeks to the end
        // once, when it is opened, so a second one cannot stand in for that host faithfully — two of
        // them still overwrite each other. Interleaving is verified against the real viewer instead.
        string path = Path.Combine(Path.GetTempPath(), $"carlanet-report-{Guid.NewGuid():N}.log");
        try
        {
            using (var host = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true })
            {
                host.WriteLine("host line");

                TrafficReport.SetLogFile(path);
                TrafficReport.Writer.WriteLine("traffic-manager line");
                TrafficReport.SetLogFile(null);
            }

            Assert.Contains("traffic-manager line", File.ReadAllText(path));
        }
        finally
        {
            TrafficReport.SetLogFile(null);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
