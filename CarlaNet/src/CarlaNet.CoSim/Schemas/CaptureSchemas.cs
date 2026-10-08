namespace CarlaNet.CoSim.Schemas;

/// <summary>One published schema: the file it is published as, and its text.</summary>
/// <param name="FileName">Its name under <c>CarlaControl/schemas/</c>, where the wheel ships it from.</param>
/// <param name="Text">The schema, exactly as published.</param>
public sealed record PublishedSchema(string FileName, string Text);

/// <summary>
/// The schemas of what a capture writes, generated from the writers' own constants and word lists: the
/// truth sidecar, the four PNG text chunks, the run manifest, the world truth track and its summary.
/// </summary>
/// <remarks>
/// <para><b>Generated, and held to the code.</b> Every vocabulary a schema lists -- where a box fell, why
/// occlusion went unmeasured, the lights, the pose sources, the annotation vocabulary's closed core, the
/// render and release reasons, the stop causes -- is read from the list or naming function the writer
/// itself uses, and every format version from the writer's own constant. A test holds each published file
/// equal to what is generated here, so a writer that changes what it writes changes its schema with it.</para>
///
/// <para><b>Identified by URN.</b> Each schema's identifier is
/// <c>urn:carla-sumo-capture:schema:&lt;kind&gt;:&lt;format version&gt;</c> (<see cref="Urn"/>): a name, not an
/// address, so nothing has to be served from it. The truth sidecar is XML in no namespace, as its writer
/// writes it, so its schema has no target namespace and names its URN in its annotation.</para>
/// </remarks>
public static class CaptureSchemas
{
    /// <summary>What every schema identifier here begins with.</summary>
    public const string UrnPrefix = "urn:carla-sumo-capture:schema:";

    /// <summary>The truth sidecar's schema file.</summary>
    public const string TruthSidecarFile = "truth_sidecar.xsd";

    /// <summary>The run manifest's schema file, covering every kind of row.</summary>
    public const string RunManifestFile = "run_manifest.schema.json";

    /// <summary>The world truth track's Table Schema file.</summary>
    public const string WorldTruthTrackFile = "world_truth_track.tableschema.json";

    /// <summary>The world truth track summary's schema file.</summary>
    public const string WorldTruthTrackSummaryFile = "world_truth_track_summary.schema.json";

    /// <summary>The identifier of the schema of <paramref name="kind"/> at <paramref name="formatVersion"/>.</summary>
    public static string Urn(string kind, int formatVersion) =>
        UrnPrefix + kind + ":" + formatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The schema file of the PNG text chunk <paramref name="keyword"/>, <c>carla:capture</c> and so on.</summary>
    public static string PngChunkFile(string keyword) =>
        "png_chunk_" + keyword[(keyword.IndexOf(':') + 1)..] + ".schema.json";

    /// <summary>Every schema of what a capture writes, as published.</summary>
    public static IReadOnlyList<PublishedSchema> Published()
    {
        List<PublishedSchema> published = [new(TruthSidecarFile, TruthSidecarSchema.Text())];
        foreach (string keyword in PngChunkSchemas.Keywords)
        {
            published.Add(new PublishedSchema(PngChunkFile(keyword), PngChunkSchemas.Text(keyword)));
        }

        published.Add(new PublishedSchema(RunManifestFile, RunManifestSchema.Text()));
        published.Add(new PublishedSchema(WorldTruthTrackFile, WorldTruthTrackSchemas.TableText()));
        published.Add(new PublishedSchema(WorldTruthTrackSummaryFile, WorldTruthTrackSchemas.SummaryText()));
        return published;
    }
}
