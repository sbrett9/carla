// Every camera's recordings are named after it, and the name is its platform track's callsign: see
// CameraName, and FrameRecorderCameraNameTests for the same rule through a real recorder. The cases
// here are the ones CarlaControl's mirror of the rule (carlacontrol.CameraName, which run_capture's
// offline checks use) is tested against, so the two are held to the same answers. A name is 1 to 63
// ASCII letters, digits, underscores and hyphens -- Overwatch_1, Southeast_1700m_orbit -- and every
// refusal says so.
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class CameraNameTests
{
    [Fact]
    public void A_Camera_Given_No_Name_Is_Named_By_Its_Actor_Id()
    {
        Assert.Equal("CARLA-SENSOR-42", CameraName.Default(42));
        Assert.Null(CameraName.Problem(CameraName.Default(42), 42));
    }

    [Theory]
    [InlineData("OVERWATCH")]
    [InlineData("Overwatch_1")]
    [InlineData("Southeast_1700m_orbit")]
    [InlineData("NapOfEarth_2")]
    [InlineData("DECK-I25")]
    [InlineData("a")]
    [InlineData("-lead")]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("CON_1")]
    [InlineData("Front_1")]
    [InlineData("frontier")]
    [InlineData("CARLA-SENSOR-")]
    [InlineData("CARLA-SENSOR-12a")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890123")]
    public void A_Short_Plain_Name_Is_Accepted(string name)
    {
        Assert.Null(CameraName.Problem(name));
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", "is 64 characters long")]
    [InlineData("Deck Cam 1", "holds a space")]
    [InlineData(" lead", "holds a space")]
    [InlineData("cam.v2", "holds '.'")]
    [InlineData("dot.", "holds '.'")]
    [InlineData(".", "holds '.'")]
    [InlineData("Aux.cam", "holds '.'")]
    [InlineData("DECK:I25", "holds ':'")]
    [InlineData("a/b", "holds '/'")]
    [InlineData("a\\b", "holds '\\'")]
    [InlineData("a<b", "holds '<'")]
    [InlineData("a>b", "holds '>'")]
    [InlineData("a\"b", "holds '\"'")]
    [InlineData("a|b", "holds '|'")]
    [InlineData("a?b", "holds '?'")]
    [InlineData("a*b", "holds '*'")]
    [InlineData("Deck#1", "holds '#'")]
    [InlineData("Deck(1)", "holds '('")]
    [InlineData("tab\there", "control character U+0009")]
    [InlineData("caf\u00e9", "U+00E9), which is not ASCII")]
    [InlineData("CON", "keeps for a device")]
    [InlineData("con", "keeps for a device")]
    [InlineData("Nul", "keeps for a device")]
    [InlineData("COM0", "keeps for a device")]
    [InlineData("COM1", "keeps for a device")]
    [InlineData("LPT9", "keeps for a device")]
    [InlineData("front", "role name the server gives sensors")]
    [InlineData("Back_Left", "role name the server gives sensors")]
    [InlineData("CARLA-SENSOR-12", "another camera's name")]
    [InlineData("carla-sensor-12", "another camera's name")]
    public void A_Name_Outside_The_Rule_Is_Refused_Saying_What_Is_Allowed(string name, string reason)
    {
        string? problem = CameraName.Problem(name);

        Assert.NotNull(problem);
        Assert.Contains(reason, problem);
        // Every refusal points at what would be accepted.
        Assert.Contains("Overwatch_1", problem);
    }

    [Fact]
    public void The_Default_Form_Is_Accepted_Only_As_The_Default_Of_The_Camera_It_Names()
    {
        Assert.Null(CameraName.Problem("CARLA-SENSOR-42", 42));
        Assert.Null(CameraName.Problem("carla-sensor-42", 42));
        Assert.Contains("is not camera 43's own", CameraName.Problem("CARLA-SENSOR-42", 43));
        Assert.Contains("names no camera of its own", CameraName.Problem("CARLA-SENSOR-42"));
    }

    [Fact]
    public void Case_Does_Not_Tell_Two_Names_Apart()
    {
        Assert.True(CameraName.Same("Deck", "deck"));
        Assert.False(CameraName.Same("Deck", "Deck-2"));
    }

    [Fact]
    public void A_Still_s_Stem_Is_The_Camera_s_Name_And_The_Local_Capture_Time_To_The_Millisecond()
    {
        var captured = new DateTime(2026, 10, 2, 14, 7, 22, 481, DateTimeKind.Utc);
        string local = captured.ToLocalTime().ToString("yyyy.MM.dd_HH.mm.ss.fff");

        Assert.Equal($"DECK-I25_{local}", CameraName.StillStem("DECK-I25", captured));
        // The stills written before cameras were named have the same shape, SCTMV where the name is.
        Assert.Equal($"SCTMV_{local}", CameraName.StillStem("SCTMV", captured));
    }

    [Fact]
    public void A_Name_Held_By_One_Recorder_Cannot_Be_Taken_By_Another_Until_It_Is_Given_Back()
    {
        string name = "HELD-" + Guid.NewGuid().ToString("N")[..8];
        IDisposable first = CameraName.Hold(name, 42, "first");

        var refused = Assert.Throws<InvalidOperationException>(
            () => CameraName.Hold(name.ToLowerInvariant(), 43, "second"));
        Assert.Contains("already being recorded in this process", refused.Message);
        Assert.Contains("by camera 42 into first", refused.Message);

        first.Dispose();
        using IDisposable second = CameraName.Hold(name, 43, "second");
        // Giving back a name twice, or after another recorder took it, does not free it for a third.
        first.Dispose();
        Assert.Throws<InvalidOperationException>(() => CameraName.Hold(name, 44, "third"));
    }

    [Fact]
    public void The_Camera_That_Holds_A_Name_In_The_World_Is_Found_Among_The_Actors()
    {
        Actor[] world =
        [
            Described(11, "vehicle.audi.tt", "DECK"),
            Described(12, "sensor.camera.depth", "front"),
            Described(13, "sensor.camera.rgb", "DECK"),
            Described(14, "sensor.camera.rgb", null),
        ];

        // A vehicle's role_name names no camera; a sensor's does, in any case.
        Assert.Equal(13u, CameraName.HolderAmong(world, "deck")?.Id);
        Assert.Null(CameraName.HolderAmong(world, "deck", except: 13));
        // A camera spawned with no name holds its default.
        Assert.Equal(14u, CameraName.HolderAmong(world, "CARLA-SENSOR-14")?.Id);
        Assert.Null(CameraName.HolderAmong(world, "OVERWATCH"));
        Assert.Equal("camera 13 (sensor.camera.rgb)", CameraName.DescribeHolder(world[2]));
    }

    private static Actor Described(ActorId id, string type, string? roleName) => new(
        id, 0u,
        new ActorDescription(id, type, roleName is null
            ? []
            : [new ActorAttributeValue(CameraName.RoleNameAttribute, ActorAttributeType.String, roleName)]),
        default, [], []);
}
