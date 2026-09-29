using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Freezes a process without ending it, and lets it go again: a SUMO that has stopped answering while
/// its socket stays open, which is what a hung microsimulation looks like from the client.
/// </summary>
/// <remarks>
/// A killed SUMO closes its socket and the client sees the connection end; a hung one does not, and
/// nothing a scenario can author produces one. Suspending the real process is the one way to make a
/// real SUMO go silent mid-step. Windows suspends every thread of the process through
/// <c>NtSuspendProcess</c>; Linux stops it with <c>SIGSTOP</c>.
/// </remarks>
internal static class ProcessSuspension
{
    private const int SigStop = 19;
    private const int SigCont = 18;

    /// <summary>Freeze the process.</summary>
    public static void Suspend(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.GetProcessById(processId);
            Check(NtSuspendProcess(process.Handle), "suspend");
        }
        else
        {
            Check(Kill(processId, SigStop), "stop");
        }
    }

    /// <summary>Let the process run again.</summary>
    public static void Resume(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.GetProcessById(processId);
            Check(NtResumeProcess(process.Handle), "resume");
        }
        else
        {
            Check(Kill(processId, SigCont), "continue");
        }
    }

    /// <summary>Whether a process of that id is still running.</summary>
    public static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Check(int status, string what)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"Could not {what} the process: status {status}.");
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int processId, int signal);
}
