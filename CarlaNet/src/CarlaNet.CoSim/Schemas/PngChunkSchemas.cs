using System.Text.Json.Nodes;
using CarlaNet.Recording;
using CarlaNet.Types.Illumination;

namespace CarlaNet.CoSim.Schemas;

/// <summary>
/// The schemas of the four PNG text chunks a still carries, one per chunk, each the compact JSON its
/// writer puts in a tEXt chunk: <c>carla:capture</c> (<see cref="CaptureIdentity"/>), <c>carla:solar</c>
/// (<see cref="SolarMetadata"/>), <c>carla:illumination</c> (<see cref="IlluminationDeclaration"/>) and
/// <c>carla:sensor</c> (<see cref="SensorMetadata"/>).
/// </summary>
/// <remarks>
/// Each chunk's keyword is read from its writer, and each chunk's format version from its writer's
/// constant. A chunk written before it carried <c>format_version</c> is version 1, and the version 1
/// schema accepts it: the version is optional, and so is the capture chunk's <c>producer</c>.
/// </remarks>
public static class PngChunkSchemas
{
    /// <summary>A date, year-month-day.</summary>
    internal const string DatePattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$";

    /// <summary>A civil instant with its UTC offset, to the second or a fraction of it, as the epoch writes one.</summary>
    internal const string CivilPattern =
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?[+-][0-9]{2}:[0-9]{2}$";

    /// <summary>An instant in UTC, to the second or a fraction of it, with a trailing <c>Z</c>.</summary>
    internal const string UtcPattern =
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z$";

    /// <summary>A clock time of day, hours, minutes and seconds.</summary>
    internal const string ClockPattern = "^[0-9]{2}:[0-9]{2}:[0-9]{2}$";

    /// <summary>A SHA-256 digest in lowercase hexadecimal.</summary>
    internal const string Sha256Pattern = "^[0-9a-f]{64}$";

    /// <summary>The capture chunk's keyword, as its writer names it.</summary>
    public static string CaptureKeyword { get; } = new CaptureIdentity(0, 0.0).PngTextChunks().Single().Keyword;

    /// <summary>The solar chunk's keyword, as its writer names it.</summary>
    public static string SolarKeyword { get; } = SolarMetadata.PngTextChunks(new double[11]).Single().Keyword;

    /// <summary>The illumination chunk's keyword, as its writer names it.</summary>
    public static string IlluminationKeyword { get; } =
        new IlluminationDeclaration(IlluminationPolicy.NameOf(IlluminationPolicyKind.Ignore), false, false)
            .PngTextChunks().Single().Keyword;

    /// <summary>The sensor chunk's keyword, as its writer names it.</summary>
    public static string SensorKeyword { get; } = SensorMetadata.PngTextChunks(new SensorPose(
        "a-f-A-M-F-Q", "Camera", "CARLA-SENSOR-1", 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0.5, 0.5, 90, 90,
        "sensor.camera.rgb", "pinhole", "none")).Single().Keyword;

    /// <summary>Every chunk's keyword, in the order a reader is told of them.</summary>
    public static IReadOnlyList<string> Keywords { get; } = [CaptureKeyword, SolarKeyword, IlluminationKeyword, SensorKeyword];

    /// <summary>The format version each chunk's writer declares.</summary>
    public static int FormatVersion(string keyword) =>
        keyword == CaptureKeyword ? CaptureIdentity.FormatVersion
        : keyword == SolarKeyword ? SolarMetadata.FormatVersion
        : keyword == IlluminationKeyword ? IlluminationDeclaration.FormatVersion
        : keyword == SensorKeyword ? SensorMetadata.FormatVersion
        : throw new ArgumentOutOfRangeException(nameof(keyword), keyword, "not a chunk a still carries");

    /// <summary>The schema of one chunk, as published.</summary>
    public static string Text(string keyword) => SchemaJson.Write(Schema(keyword));

    /// <summary>The schema of one chunk.</summary>
    public static JsonObject Schema(string keyword) =>
        keyword == CaptureKeyword ? Capture()
        : keyword == SolarKeyword ? Solar()
        : keyword == IlluminationKeyword ? Illumination()
        : keyword == SensorKeyword ? Sensor()
        : throw new ArgumentOutOfRangeException(nameof(keyword), keyword, "not a chunk a still carries");

    /// <summary>The URN kind of a chunk's schema: <c>png-chunk-capture</c> and so on.</summary>
    internal static string Kind(string keyword) => "png-chunk-" + keyword[(keyword.IndexOf(':') + 1)..];

    private static JsonObject Head(string keyword, string description)
    {
        int version = FormatVersion(keyword);
        return SchemaJson.Document(
            CaptureSchemas.Urn(Kind(keyword), version),
            $"PNG text chunk {keyword}, format version {version}",
            description + $" This schema describes format version {version}; a chunk with no format_version "
            + "was written before chunks carried one and is version 1.");
    }

    private static JsonField Version(string keyword) =>
        new("format_version", SchemaJson.Constant(
            "The chunk's format version. Absent from a chunk written before chunks carried one, which is version 1.",
            FormatVersion(keyword)), Required: false);

    private static JsonObject Close(JsonObject head, JsonObject body, JsonObject? definitions = null)
    {
        foreach ((string name, JsonNode? value) in body.ToList())
        {
            if (name == "description")
            {
                continue;
            }

            body.Remove(name);
            head[name] = value;
        }

        if (definitions is not null)
        {
            head["$defs"] = definitions;
        }

        return head;
    }

    private static JsonObject Capture()
    {
        string keyword = CaptureKeyword;
        var definitions = new JsonObject();
        ProducerSchema.Define(definitions);
        JsonObject body = SchemaJson.Object(
            "Which capture a still is.",
            Version(keyword),
            new JsonField("tick", SchemaJson.Integer(
                "The simulation frame the image was rendered on. It pairs the still with its truth sidecar and "
                + "identifies the same instant across runs.", minimum: 0)),
            new JsonField("sim_time_s", SchemaJson.Number(
                "Simulated time at that frame, seconds.", unit: "seconds")),
            new JsonField("run_id", SchemaJson.Text(
                "The run the still belongs to. A recorder always names one; it is left out only when empty.",
                "^.+$"), Required: false),
            new JsonField("scenario_id", SchemaJson.Text(
                "The scenario driving the run, where one is.", "^.+$"), Required: false),
            new JsonField("seed", SchemaJson.Integer("The seed the run was started with, where one was given."),
                          Required: false),
            ProducerSchema.Field("producer",
                "What made the still. Absent from a still written before stills carried it.", required: false));
        return Close(Head(keyword,
                          "The identity of one still: the simulation frame and time it was rendered at, and the run "
                          + "it belongs to, so a still separated from its sidecar still says which capture it is."),
                     body, definitions);
    }

    private static JsonObject Solar()
    {
        string keyword = SolarKeyword;
        JsonObject body = SchemaJson.Object(
            "The sun the world reported.",
            Version(keyword),
            new JsonField("solar_time", SchemaJson.Number(
                "The sun's clock in its own time zone, hours from 0 up to 24.", unit: "hours")),
            new JsonField("date", SchemaJson.Text("The sun's calendar date.", DatePattern)),
            new JsonField("time_zone", SchemaJson.Number(
                "The time zone the sun's clock is read in, hours from UTC.", unit: "hours")),
            new JsonField("lat", SchemaJson.Number(
                "The latitude the sun is computed for: the world's georeference origin, WGS84.", unit: "degrees",
                minimum: -90, maximum: 90)),
            new JsonField("lon", SchemaJson.Number(
                "The longitude the sun is computed for, WGS84.", unit: "degrees", minimum: -180, maximum: 180)),
            new JsonField("sun_elevation_deg", SchemaJson.Number(
                "The sun's geometric elevation above the horizon, with no atmosphere.", unit: "degrees")),
            new JsonField("sun_azimuth_deg", SchemaJson.Number(
                "The sun's azimuth, clockwise from true north.", unit: "degrees")),
            new JsonField("advancing", SchemaJson.Boolean(
                "Whether the engine itself moved the sun's clock with the world's tick. False in a SUMO drive, "
                + "which writes an advancing sun itself on every tick.")),
            new JsonField("rate", SchemaJson.Number(
                "Sun-clock seconds per simulated second while the engine advances the sun.",
                unit: "seconds per second")),
            new JsonField("sun_corrected_elevation_deg", SchemaJson.Number(
                "The sun's elevation with atmospheric refraction applied: the elevation the frame was lit at. "
                + "Absent where the server does not report it.", unit: "degrees"), Required: false),
            new JsonField("illumination_band", SchemaJson.Words(
                "The illumination band of this sun. The bands are cut by " + IlluminationBands.Source + ". Absent where the elevation "
                + "is not a sun's, such as the value the engine reports for a sun it could not compute.",
                IlluminationBands.Names), Required: false),
            new JsonField("illumination_band_elevation", SchemaJson.Words(
                "Which elevation the band was cut from: the refraction-corrected one wherever the chunk carries "
                + "it, the geometric one otherwise. Written with illumination_band and only with it.",
                ElevationKinds()), Required: false));
        body["dependentRequired"] = new JsonObject
        {
            ["illumination_band"] = SchemaJson.Array(["illumination_band_elevation"]),
            ["illumination_band_elevation"] = SchemaJson.Array(["illumination_band"]),
        };
        return Close(Head(keyword,
                          "The sun the world reported on the tick nearest the pixels, and its illumination band, "
                          + "derived from that sun alone. A still whose world reported no sun carries no chunk."),
                     body);
    }

    private static JsonObject Illumination()
    {
        string keyword = IlluminationKeyword;
        JsonObject body = SchemaJson.Object(
            "What the run declared the sun to be for this frame.",
            Version(keyword),
            new JsonField("policy", SchemaJson.Words(
                "The illumination policy the run followed.", PolicyNames())),
            new JsonField("epoch_honoured", SchemaJson.Boolean(
                "Whether the frame was lit by the sun of its own declared civil instant.")),
            new JsonField("audited", SchemaJson.Boolean(
                "Whether the world's sun was compared against the declaration on this frame's tick.")),
            new JsonField("rate", SchemaJson.Number(
                "Sun-clock seconds per simulated second, under the advance policy only.",
                unit: "seconds per second"), Required: false),
            new JsonField("freeze_at_civil_time", SchemaJson.Text(
                "The civil time of day the sun is held at, under the freeze_at policy only.", ClockPattern),
                Required: false),
            new JsonField("epoch_digest", SchemaJson.Text(
                "SHA-256 of the scenario's epoch, so a frame separated from its run still names it.", Sha256Pattern),
                Required: false),
            new JsonField("epoch_civil", SchemaJson.Text(
                "The civil instant simulated second zero stands for, with its UTC offset.", CivilPattern),
                Required: false),
            new JsonField("utc_offset_hours", SchemaJson.Number(
                "The epoch's declared UTC offset, hours.", unit: "hours"), Required: false),
            new JsonField("declared_civil", SchemaJson.Text(
                "This frame's simulated instant as a civil time, with its UTC offset.", CivilPattern),
                Required: false),
            new JsonField("declared_utc", SchemaJson.Text(
                "The same instant in UTC: the key to join this frame to anything outside the pipeline.", UtcPattern),
                Required: false),
            new JsonField("sun_declared", SchemaJson.Text(
                "The date and clock the sun was declared to hold for this frame, with its UTC offset: the frame's "
                + "civil instant under a policy that honors the epoch, the window's opening instant under a freeze.",
                CivilPattern), Required: false),
            new JsonField("sun_elevation_declared_deg", SchemaJson.Number(
                "The geometric elevation of the declared sun.", unit: "degrees"), Required: false),
            new JsonField("sun_corrected_elevation_declared_deg", SchemaJson.Number(
                "The refraction-corrected elevation of the declared sun.", unit: "degrees"), Required: false),
            new JsonField("declared_elevation", SchemaJson.Words(
                "Which of the two elevations a declared window elevation means.", ElevationKinds()),
                Required: false),
            new JsonField("residual_clock_s", SchemaJson.Number(
                "The world's sun clock minus the declared one.", unit: "seconds"), Required: false),
            new JsonField("residual_deg", SchemaJson.Number(
                "The angle between the world's sun and the declared sun.", unit: "degrees"), Required: false),
            new JsonField("residual_corrected_deg", SchemaJson.Number(
                "The world's refraction-corrected elevation minus the declared one, where the world reports it.",
                unit: "degrees"), Required: false));
        return Close(Head(keyword,
                          "What the run declared the sun to be for this frame -- the scenario's epoch, the "
                          + "illumination policy, the frame's civil instant and the sun declared for it -- and how far "
                          + "the world's sun was from it. Written only in a run that declares its illumination."),
                     body);
    }

    private static JsonObject Sensor()
    {
        string keyword = SensorKeyword;
        JsonObject intrinsics = SchemaJson.Object(
            "The camera's pinhole intrinsics: square pixels, the principal point at the picture's center.",
            new JsonField("width", SchemaJson.Integer("The picture's width.", unit: "pixels", minimum: 1)),
            new JsonField("height", SchemaJson.Integer("The picture's height.", unit: "pixels", minimum: 1)),
            new JsonField("fx", SchemaJson.Number(
                "Focal length across: width / (2 tan(hfov_deg / 2)).", unit: "pixels")),
            new JsonField("fy", SchemaJson.Number("Focal length down; equal to fx.", unit: "pixels")),
            new JsonField("cx", SchemaJson.Number("Principal point across: width / 2.", unit: "pixels")),
            new JsonField("cy", SchemaJson.Number("Principal point down: height / 2.", unit: "pixels")),
            new JsonField("hfov_deg", SchemaJson.Number("Horizontal field of view.", unit: "degrees")),
            new JsonField("vfov_deg", SchemaJson.Number(
                "Vertical field of view, from the height and the focal length.", unit: "degrees")),
            new JsonField("model", SchemaJson.Text("The projection model: pinhole.")),
            new JsonField("distortion", SchemaJson.Text(
                "The lens distortion: none at CARLA's defaults, otherwise CARLA's own lens parameters.")),
            new JsonField("sensor_model", SchemaJson.Text("The camera blueprint, such as sensor.camera.rgb.")));
        JsonObject body = SchemaJson.Object(
            "Where the camera was and where it pointed.",
            Version(keyword),
            new JsonField("uid", SchemaJson.Text("The camera's track identifier, such as CARLA-SENSOR-107.")),
            new JsonField("type", SchemaJson.Text(
                "The camera platform's Cursor-on-Target air-track type, such as a-f-A-M-F-Q.", "^a-.+$")),
            new JsonField("callsign", SchemaJson.Text("The camera's name, which every still of the camera is named after.")),
            new JsonField("lat", SchemaJson.Number("The camera's latitude, WGS84.", unit: "degrees", minimum: -90, maximum: 90)),
            new JsonField("lon", SchemaJson.Number("The camera's longitude, WGS84.", unit: "degrees", minimum: -180, maximum: 180)),
            new JsonField("hae", SchemaJson.Number(
                "The camera's height above the WGS84 ellipsoid in the bare-earth convention every vehicle's height "
                + "uses: its physical height less align_offset_m.", unit: "meters")),
            new JsonField("align_offset_m", SchemaJson.Number(
                "The height-align offset under the camera, taken off its physical height: physical height = hae + "
                + "align_offset_m.", unit: "meters")),
            new JsonField("az_deg", SchemaJson.Number(
                "Boresight azimuth, clockwise from true north.", unit: "degrees")),
            new JsonField("el_deg", SchemaJson.Number(
                "Boresight elevation: 0 level, -90 straight down.", unit: "degrees")),
            new JsonField("roll_deg", SchemaJson.Number("Roll about the boresight.", unit: "degrees")),
            new JsonField("course_deg", SchemaJson.Number(
                "Direction of the camera's movement over the ground since the previous capture, clockwise from "
                + "true north; the boresight azimuth when it moved slower than 0.5 m/s.", unit: "degrees")),
            new JsonField("speed_mps", SchemaJson.Number(
                "The camera's ground speed since the previous capture.", unit: "meters per second", minimum: 0)),
            new JsonField("intrinsics", intrinsics));
        return Close(Head(keyword,
                          "The camera platform's pose and pinhole intrinsics at the capture, the same values the "
                          + "truth sidecar's platform event carries. A still with no platform pose carries no chunk."),
                     body);
    }

    /// <summary>Every name an elevation kind is written by, in the code's order.</summary>
    internal static IEnumerable<string> ElevationKinds() =>
        Enum.GetValues<SolarElevationKind>().Select(SolarElevationKinds.Name);

    /// <summary>Every illumination policy's name, in the code's order.</summary>
    internal static IEnumerable<string> PolicyNames() =>
        Enum.GetValues<IlluminationPolicyKind>().Select(IlluminationPolicy.NameOf);
}
