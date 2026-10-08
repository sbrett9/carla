using System.Text.Json.Nodes;
using CarlaNet.CoSim.Schemas;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The schemas of what a capture writes are published under <c>CarlaControl/schemas/</c>, and each published
/// file is exactly what the writers' own constants and word lists generate now.
/// </summary>
/// <remarks>
/// After a deliberate change to what a writer writes, publish the schemas again by running these tests with
/// <c>CARLANET_WRITE_CAPTURE_SCHEMAS=1</c>, and read the diff before committing it.
/// </remarks>
public sealed class CaptureSchemaPublicationTests
{
    /// <summary>The environment variable that has the test publish the schemas rather than only compare them.</summary>
    public const string WriteVariable = "CARLANET_WRITE_CAPTURE_SCHEMAS";

    /// <summary>Where the published schemas live: <c>CarlaControl/schemas/</c> of this checkout.</summary>
    public static string SchemaDirectory { get; } = FindSchemaDirectory();

    public static TheoryData<string> PublishedFiles()
    {
        var files = new TheoryData<string>();
        foreach (PublishedSchema schema in CaptureSchemas.Published())
        {
            files.Add(schema.FileName);
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(PublishedFiles))]
    public void EachPublishedSchemaIsWhatTheWritersGenerate(string fileName)
    {
        PublishedSchema generated = CaptureSchemas.Published().Single(schema => schema.FileName == fileName);
        string path = Path.Combine(SchemaDirectory, fileName);
        if (Environment.GetEnvironmentVariable(WriteVariable) is "1")
        {
            File.WriteAllText(path, generated.Text);
        }

        Assert.True(File.Exists(path), $"{path} is not published; run the schema tests with {WriteVariable}=1.");
        string published = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(published == generated.Text,
                    $"{fileName} is not what the writers generate now; run the schema tests with {WriteVariable}=1 "
                    + "and read the diff.");
    }

    [Fact]
    public void EverySchemaNamesItsFormatVersionAndAUrnOfItsOwn()
    {
        List<string> identifiers = [];
        foreach (PublishedSchema schema in CaptureSchemas.Published())
        {
            string identifier = schema.FileName.EndsWith(".xsd", StringComparison.Ordinal)
                ? TruthSidecarSchema.Urn
                : JsonNode.Parse(schema.Text)!["$id"]!.GetValue<string>();
            Assert.StartsWith(CaptureSchemas.UrnPrefix, identifier);
            Assert.Contains(identifier, schema.Text);
            identifiers.Add(identifier);
        }

        Assert.Equal(identifiers.Count, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("urn:carla-sumo-capture:schema:truth-sidecar:1", TruthSidecarSchema.Urn);
        Assert.Equal("urn:carla-sumo-capture:schema:run-manifest:1", RunManifestSchema.Urn);
        Assert.Equal("urn:carla-sumo-capture:schema:world-truth-track:2", WorldTruthTrackSchemas.TableUrn);
    }

    [Fact]
    public void TheManifestSchemaNamesEveryKindOfRowTheWriterWritesAndTheTrackSchemaEveryColumn()
    {
        JsonObject manifest = RunManifestSchema.Schema();
        Assert.Equal(18, RunManifestWriter.RowKinds.Count);
        Assert.Equal(RunManifestWriter.RowKinds,
                     manifest["properties"]!["row"]!["enum"]!.AsArray().Select(word => word!.GetValue<string>()));
        foreach (string kind in RunManifestWriter.RowKinds)
        {
            Assert.Equal(kind, manifest["$defs"]![kind]!["properties"]!["row"]!["const"]!.GetValue<string>());
        }

        JsonObject table = WorldTruthTrackSchemas.Table();
        Assert.Equal(WorldTruthTrackWriter.Columns,
                     table["fields"]!.AsArray().Select(field => field!["name"]!.GetValue<string>()));
    }

    private static string FindSchemaDirectory()
    {
        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            string candidate = Path.Combine(at.FullName, "CarlaControl", "schemas");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"No CarlaControl/schemas folder above {AppContext.BaseDirectory}: the schema tests run from a checkout.");
    }
}
