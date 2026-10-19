// Offline (no engine, no server): what the traffic manager says when the server refuses its control
// frame because a SUMO drive holds the world's drive lease.
//
// The server refuses every apply_control_to_vehicle in the frame with the holder named, and the
// traffic manager discards batch responses, so without this it would drive nothing and say nothing.
// It says so once, whatever CARLANET_TM_DEBUG is set to, and says nothing for any other error.
#nullable enable

using CarlaNet.TrafficManager;
using CarlaNet.Types.Rpc.Commands;
using Xunit;

namespace CarlaNet.Tests.TrafficManager;

public class DriveLeaseLockoutTests
{
    private const string Refusal =
        "apply_control_to_vehicle: refused while a SUMO drive (process 41 on HOST) holds the drive lease "
        + "on this world; no other traffic drives a vehicle here until the holder releases it";

    [Fact]
    public void A_Refused_Control_Frame_Is_Said_Once_Naming_The_Holder_And_Other_Errors_Are_Not()
    {
        TextWriter original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            TrafficManagerLocal.WarnIfLockedOut([CommandResponse.Failure("actor not found"), CommandResponse.Success(7)]);
            Assert.DoesNotContain("locked out", captured.ToString());

            TrafficManagerLocal.WarnIfLockedOut([CommandResponse.Success(7), CommandResponse.Failure(Refusal)]);
            TrafficManagerLocal.WarnIfLockedOut([CommandResponse.Failure(Refusal), CommandResponse.Failure(Refusal)]);
        }
        finally
        {
            Console.SetError(original);
        }

        string said = captured.ToString();
        Assert.Contains("locked out of this world", said);
        Assert.Contains("a SUMO drive (process 41 on HOST)", said);
        Assert.Equal(1, Count(said, "locked out of this world"));
    }

    [Fact]
    public void The_Mark_Is_The_Text_Every_Server_Refusal_Carries()
    {
        // CarlaServer.cpp's DriveLeaseRefusal writes "<call>: refused while <holder> holds the drive
        // lease on this world; ..." for each of the four refused calls.
        Assert.Contains(TrafficManagerLocal.DriveLeaseRefusalMark, Refusal);
        Assert.Contains(TrafficManagerLocal.DriveLeaseRefusalMark,
                        "set_actor_autopilot: refused while x holds the drive lease on this world; ...");
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
