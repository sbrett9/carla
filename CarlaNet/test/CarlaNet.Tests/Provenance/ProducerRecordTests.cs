// The record of what made a file, in the one shape every file carries it, and the release the CarlaNet
// assemblies say they are. Offline: no server, no SUMO.
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CarlaNet.Types.Provenance;

namespace CarlaNet.Tests.Provenance;

public sealed class ProducerRecordTests
{
    private static readonly DateTime Written = new(2026, 10, 7, 12, 0, 0, 123, DateTimeKind.Utc);

    private static ServerBuildIdentity Package() => ServerBuildIdentity.FromAnswer(new Dictionary<string, string>
    {
        ["release"] = "0.10.0",
        ["world_interface"] = "1.0",
        ["build"] = "package",
        ["configuration"] = "Shipping",
        ["carla_commit"] = "025443a83eaf1bb82f18795d608fca50eb77a452",
        ["content_commit"] = "6bcd042a91a54d9a2f2f002869fbf1c75f3768f4",
        ["engine_commit"] = "e5e266de195a2400a6a74180402fb1a3e8f75472",
        ["commits_from"] = "version_file",
    });

    [Fact]
    public void The_Json_Record_Names_The_Tool_The_Releases_The_Server_And_The_Time_In_One_Order()
    {
        var record = new ProducerRecord("carlacontrol.CaptureSession", "0.10.0+g1a2b3c4d5", "0.10.0+g1a2b3c4d5",
                                        Package(), "1.27.0", Written);

        Assert.Equal(
            "{\"tool\":\"carlacontrol.CaptureSession\",\"tool_version\":\"0.10.0+g1a2b3c4d5\","
            + "\"carlanet\":\"0.10.0+g1a2b3c4d5\",\"server\":{\"available\":true,\"release\":\"0.10.0\","
            + "\"world_interface\":\"1.0\",\"build\":\"package\",\"configuration\":\"Shipping\","
            + "\"carla_commit\":\"025443a83eaf1bb82f18795d608fca50eb77a452\","
            + "\"content_commit\":\"6bcd042a91a54d9a2f2f002869fbf1c75f3768f4\","
            + "\"engine_commit\":\"e5e266de195a2400a6a74180402fb1a3e8f75472\",\"commits_from\":\"version_file\"},"
            + "\"sumo\":\"1.27.0\",\"written_utc\":\"2026-10-07T12:00:00.123Z\"}",
            record.ToJson());
    }

    [Fact]
    public void What_Was_Not_Used_Is_Null_And_A_File_Written_Alike_Twice_Carries_No_Time()
    {
        var record = new ProducerRecord("run_sumo_drive", null, "0.10.0", null, null, Written).Untimed();

        Assert.Equal("{\"tool\":\"run_sumo_drive\",\"tool_version\":null,\"carlanet\":\"0.10.0\",\"server\":null,"
                     + "\"sumo\":null}", record.ToJson());
    }

    [Fact]
    public void A_Record_Read_Back_Is_The_Record_Written_Whether_Or_Not_The_Server_Said()
    {
        foreach (ServerBuildIdentity? server in new ServerBuildIdentity?[]
                 {
                     Package(), ServerBuildIdentity.NotAnswered("built before the call", "0.10.0", "1.0"), null,
                 })
        {
            var record = new ProducerRecord("carlacontrol.WorldBuilder", "0.10.0", "0.10.0", server, null, Written);
            using JsonDocument written = JsonDocument.Parse(record.ToJson());

            Assert.Equal(record, ProducerRecord.ReadJson(written.RootElement));
            Assert.Equal(record, JsonSerializer.Deserialize<ProducerRecord>(JsonSerializer.Serialize(record)));
        }
    }

    [Fact]
    public void The_Xml_Record_Is_A_Producer_Element_With_The_Server_Inside_And_Nothing_Unused()
    {
        var record = new ProducerRecord("carlacontrol.CaptureSession", "0.10.0", "0.10.0",
                                        ServerBuildIdentity.NotAnswered("built before the call", "0.10.0"), null,
                                        Written);
        var text = new StringWriter();
        using (XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            record.WriteXml(writer);
        }

        XElement producer = XElement.Parse(text.ToString());
        Assert.Equal("_producer", producer.Name.LocalName);
        Assert.Equal("carlacontrol.CaptureSession", (string?)producer.Attribute("tool"));
        Assert.Equal("0.10.0", (string?)producer.Attribute("tool_version"));
        Assert.Equal("2026-10-07T12:00:00.123Z", (string?)producer.Attribute("written_utc"));
        Assert.Null(producer.Attribute("sumo"));
        XElement server = Assert.Single(producer.Elements("_server"));
        Assert.Equal("false", (string?)server.Attribute("available"));
        Assert.Equal("unknown", (string?)server.Attribute("world_interface"));
        Assert.Equal("built before the call", (string?)server.Attribute("reason"));
        Assert.Null(server.Attribute("carla_commit"));
    }

    [Fact]
    public void The_CarlaNet_Release_Is_The_Release_The_Top_Level_CMakeLists_Sets_With_Its_Commit()
    {
        string cmake = File.ReadAllText(Path.Combine(CheckoutRoot(), "CMakeLists.txt"));
        string Part(string name) =>
            Regex.Match(cmake, $@"set\s*\(\s*CARLA_VERSION_{name}\s+(\d+)\s*\)").Groups[1].Value;
        string release = $"{Part("MAJOR")}.{Part("MINOR")}.{Part("PATCH")}";

        // A test build is not the release: it is named by its commit, or says it cannot be.
        Assert.Matches($@"^{Regex.Escape(release)}\+(g[0-9a-f]{{9}}(\.dirty)?|unknown)$", Producer.CarlaNetVersion);
    }

    [Fact]
    public void A_Declared_Tool_Is_Named_Over_The_Default_And_The_Record_Is_Stamped_To_The_Millisecond()
    {
        try
        {
            Producer.ForgetDeclaredTool();
            Producer.DeclareDefaultTool("probe_script", null);
            Assert.Equal(("probe_script", (string?)null), Producer.Tool);

            Producer.DeclareTool("carlacontrol.CaptureSession", "0.10.0+g1a2b3c4d5");
            Producer.DeclareDefaultTool("another_script", null);
            ProducerRecord record = Producer.Now(Package(), "1.27.0");

            Assert.Equal("carlacontrol.CaptureSession", record.Tool);
            Assert.Equal("0.10.0+g1a2b3c4d5", record.ToolVersion);
            Assert.Equal(Producer.CarlaNetVersion, record.CarlaNet);
            Assert.Equal("1.27.0", record.Sumo);
            Assert.Equal(0, record.WrittenUtc!.Value.Ticks % TimeSpan.TicksPerMillisecond);
            Assert.Equal(DateTimeKind.Utc, record.WrittenUtc.Value.Kind);
        }
        finally
        {
            Producer.ForgetDeclaredTool();
        }
    }

    [Fact]
    public void A_Reader_Reads_What_It_Knows_And_A_File_From_Before_Versions_And_Refuses_A_Newer_One_By_Name()
    {
        Assert.Null(FormatVersions.Refusal("a.xml", "format_version", null, 1));
        Assert.Null(FormatVersions.Refusal("a.xml", "format_version", 1, 1));

        string? newer = FormatVersions.Refusal("C:/run/a.xml", "format_version", 3, 1);
        Assert.Equal("C:/run/a.xml declares format_version 3, and this reader supports format_version 1 and "
                     + "earlier. It was written by a newer release; read it with that release's tools.", newer);
        Assert.Contains("not a format version", FormatVersions.Refusal("a.xml", "format_version", 0, 1));

        using JsonDocument text = JsonDocument.Parse("{\"FormatVersion\":\"2\"}");
        Assert.Null(FormatVersions.Declared(text.RootElement, "world.json", "FormatVersion", out string? malformed));
        Assert.Contains("not an integer", malformed);
        Assert.Equal(2, FormatVersions.Declared("2", "a.xml", "format_version", out string? none));
        Assert.Null(none);
        Assert.Null(FormatVersions.Declared((string?)null, "a.xml", "format_version", out _));
        Assert.Null(FormatVersions.Declared("two", "a.xml", "format_version", out string? word));
        Assert.Contains("\"two\"", word);
    }

    private static string CheckoutRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "CMakeLists.txt");
            if (File.Exists(candidate) && File.ReadAllText(candidate).Contains("CARLA_VERSION_MAJOR"))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("the test is not running inside a CARLA checkout");
    }
}
