// The exposure a camera was given, read from its attributes and written on every capture beside the
// intrinsics. See Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/08_Collection_And_EPoL.md §4.8 and
// D8.26-D8.28: a declared camera fact, as the intrinsics are, read from the camera as the server spawned
// it, by the rule the plugin applies the attributes with (ActorBlueprintFunctionLibrary.cpp,
// ApplyCameraExposureAttributes).
using System.Text.Json;
using System.Xml.Linq;
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class CameraExposureTests
{
    // The Default profile's exposure, which is the blueprint's recommended values: manual, ISO 100,
    // 1/320 s, f/4 and no compensation.
    private static Dictionary<string, string> DefaultAttributes() => new(StringComparer.Ordinal)
    {
        ["post_process_profile"] = "Default",
        ["exposure_mode"] = "manual",
        ["iso"] = "100.0",
        ["shutter_speed"] = "320.0",
        ["fstop"] = "4.0",
        ["exposure_compensation"] = "0.0",
    };

    [Fact]
    public void The_Default_Profile_s_Exposure_Reads_As_EV100_12_32_With_The_Shutter_In_Seconds()
    {
        CameraExposure exposure = CameraExposure.FromAttributes(DefaultAttributes())!;

        Assert.Equal(new CameraExposure("Default", CameraExposure.Manual, 100.0, 320.0, 4.0, 0.0), exposure);
        Assert.Equal(1.0 / 320.0, exposure.ShutterSeconds, 12);
        // log2(N²/t) - log2(ISO/100) = log2(16 · 320) = 12.3219, doc 08 §2.9's +12.32.
        Assert.Equal(Math.Log2(16.0 * 320.0), exposure.Ev100!.Value, 12);
        Assert.Equal(12.32, exposure.Ev100!.Value, 2);
    }

    [Fact]
    public void Four_Times_The_ISO_Is_Two_Stops_Brighter_And_Two_Fewer_EV100()
    {
        Dictionary<string, string> attributes = DefaultAttributes();
        attributes["iso"] = "400.0";

        Assert.Equal(CameraExposure.FromAttributes(DefaultAttributes())!.Ev100!.Value - 2.0,
                     CameraExposure.FromAttributes(attributes)!.Ev100!.Value, 12);
    }

    [Fact]
    public void The_Engine_Reads_An_ISO_Below_One_As_One_And_So_Does_The_Record()
    {
        Dictionary<string, string> attributes = DefaultAttributes();
        attributes["iso"] = "0.5";
        Dictionary<string, string> one = DefaultAttributes();
        one["iso"] = "1.0";

        Assert.Equal(CameraExposure.FromAttributes(one)!.Ev100, CameraExposure.FromAttributes(attributes)!.Ev100);
    }

    [Fact]
    public void Histogram_Is_Histogram_In_Any_Case_And_Carries_No_EV100()
    {
        Dictionary<string, string> attributes = DefaultAttributes();
        attributes["exposure_mode"] = "Histogram";

        CameraExposure exposure = CameraExposure.FromAttributes(attributes)!;

        Assert.Equal(CameraExposure.Histogram, exposure.Method);
        Assert.Null(exposure.Ev100);
    }

    [Fact]
    public void Any_Mode_But_Histogram_Is_Manual_As_The_Plugin_Applies_It()
    {
        Dictionary<string, string> attributes = DefaultAttributes();
        attributes["exposure_mode"] = "basic";

        Assert.Equal(CameraExposure.Manual, CameraExposure.FromAttributes(attributes)!.Method);
    }

    [Theory]
    [InlineData("post_process_profile")]
    [InlineData("exposure_mode")]
    [InlineData("iso")]
    [InlineData("shutter_speed")]
    [InlineData("fstop")]
    [InlineData("exposure_compensation")]
    public void A_Camera_Missing_Any_Of_The_Six_Has_No_Record(string missing)
    {
        // A camera of a server built before it published its exposure carries post_process_profile alone.
        Dictionary<string, string> attributes = DefaultAttributes();
        attributes.Remove(missing);

        Assert.Null(CameraExposure.FromAttributes(attributes));
    }

    [Theory]
    [InlineData("iso", "fast")]
    [InlineData("fstop", "NaN")]
    [InlineData("shutter_speed", "0")]
    [InlineData("shutter_speed", "-320")]
    public void An_Exposure_The_Record_Cannot_State_Is_Not_Recorded(string attribute, string value)
    {
        Dictionary<string, string> attributes = DefaultAttributes();
        attributes[attribute] = value;

        Assert.Null(CameraExposure.FromAttributes(attributes));
    }

    [Fact]
    public void The_Record_Is_Read_From_The_Camera_Actor_s_Own_Attributes()
    {
        var camera = new Actor(42u, 0u, new ActorDescription(7u, "sensor.camera.rgb",
            [.. DefaultAttributes().Select(each => new ActorAttributeValue(each.Key, AttributeType(each.Key), each.Value)),
             new ActorAttributeValue("role_name", ActorAttributeType.String, "Overwatch_1")]),
            default, [], []);
        var depth = new Actor(43u, 0u, new ActorDescription(8u, "sensor.camera.depth",
            [new ActorAttributeValue("max_range", ActorAttributeType.Float, "20000.0")]), default, [], []);

        Assert.Equal(CameraExposure.FromAttributes(DefaultAttributes()), CameraExposure.Of(camera));
        Assert.Null(CameraExposure.Of(depth));
    }

    [Fact]
    public void The_Platform_Event_Carries_The_Exposure_Beside_The_Intrinsics()
    {
        CameraExposure exposure = CameraExposure.FromAttributes(DefaultAttributes())!;

        XElement element = PlatformDetail(Pose(exposure)).Element(CameraExposure.ElementName)!;

        Assert.NotNull(element);
        Assert.Equal("_carla_intrinsics", (element.PreviousNode as XElement)?.Name.LocalName);
        Assert.Equal("Default", (string?)element.Attribute("post_process_profile"));
        Assert.Equal("manual", (string?)element.Attribute("method"));
        Assert.Equal("100", (string?)element.Attribute("iso"));
        Assert.Equal("0.003125", (string?)element.Attribute("shutter_s"));
        Assert.Equal("4", (string?)element.Attribute("fstop"));
        Assert.Equal("0", (string?)element.Attribute("compensation_ev"));
        Assert.Equal("12.322", (string?)element.Attribute("ev100"));
    }

    [Fact]
    public void Under_Histogram_The_Element_Carries_The_Compensation_And_No_EV100()
    {
        Dictionary<string, string> attributes = DefaultAttributes();
        attributes["exposure_mode"] = "histogram";
        attributes["exposure_compensation"] = "-1.5";

        XElement element = PlatformDetail(Pose(CameraExposure.FromAttributes(attributes)!))
            .Element(CameraExposure.ElementName)!;

        Assert.Equal("histogram", (string?)element.Attribute("method"));
        Assert.Equal("-1.5", (string?)element.Attribute("compensation_ev"));
        Assert.Null(element.Attribute("ev100"));
    }

    [Fact]
    public void A_Camera_That_Carries_No_Exposure_Writes_No_Element()
    {
        Assert.Null(PlatformDetail(Pose(null)).Element(CameraExposure.ElementName));
    }

    [Fact]
    public void The_Manifest_s_Json_Carries_The_Same_Fields_With_EV100_Null_Under_Histogram()
    {
        using JsonDocument manual = JsonDocument.Parse(CameraExposure.FromAttributes(DefaultAttributes())!.ToJson());
        Assert.Equal("Default", manual.RootElement.GetProperty("post_process_profile").GetString());
        Assert.Equal("manual", manual.RootElement.GetProperty("method").GetString());
        Assert.Equal(100.0, manual.RootElement.GetProperty("iso").GetDouble());
        Assert.Equal(1.0 / 320.0, manual.RootElement.GetProperty("shutter_s").GetDouble(), 12);
        Assert.Equal(4.0, manual.RootElement.GetProperty("fstop").GetDouble());
        Assert.Equal(0.0, manual.RootElement.GetProperty("compensation_ev").GetDouble());
        Assert.Equal(Math.Log2(16.0 * 320.0), manual.RootElement.GetProperty("ev100").GetDouble(), 12);

        Dictionary<string, string> attributes = DefaultAttributes();
        attributes["exposure_mode"] = "histogram";
        using JsonDocument histogram = JsonDocument.Parse(CameraExposure.FromAttributes(attributes)!.ToJson());
        Assert.Equal(JsonValueKind.Null, histogram.RootElement.GetProperty("ev100").ValueKind);
    }

    private static ActorAttributeType AttributeType(string id) =>
        id is "post_process_profile" or "exposure_mode" ? ActorAttributeType.String : ActorAttributeType.Float;

    private static SensorPose Pose(CameraExposure? exposure) => new(
        "a-f-A-M-F-Q", "Overwatch_1", "CARLA-SENSOR-42", 38.9, -119.76, 1872.0, 0.0,
        120.0, -60.0, 0.0, 120.0, 0.0, 1280, 720, 640.0, 640.0, 640.0, 360.0, 90.0, 58.7,
        "sensor.camera.rgb", "pinhole", "none", exposure);

    private static XElement PlatformDetail(SensorPose sensor)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc), [], sensor: sensor);
            XElement events = XDocument.Load(path).Root!;
            return events.Elements("event").Single(e => (string?)e.Attribute("uid") == sensor.Uid).Element("detail")!;
        }
        finally { File.Delete(path); }
    }
}
