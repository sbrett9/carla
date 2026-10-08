using System.Text.Json.Nodes;
using CarlaNet.Types.Illumination;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim.Schemas;

/// <summary>
/// The schemas of the world truth track <see cref="WorldTruthTrackWriter"/> writes: a Frictionless Table
/// Schema of its CSV, column by column, and a JSON Schema of the summary written beside it.
/// </summary>
/// <remarks>
/// The CSV carries no version of its own, so that its header stays the columns alone; its format version is
/// the summary's <c>world_truth_track_version</c>, and both schemas describe that version. The columns are
/// the writer's own list, in its order, and generation fails for a column this class does not describe.
/// </remarks>
public static class WorldTruthTrackSchemas
{
    /// <summary>The kind the Table Schema's URN names.</summary>
    public const string TableKind = "world-truth-track";

    /// <summary>The kind the summary schema's URN names.</summary>
    public const string SummaryKind = "world-truth-track-summary";

    /// <summary>The Table Schema profile the track's schema is written to.</summary>
    public const string TableSchemaProfile = "https://datapackage.org/profiles/2.0/tableschema.json";

    /// <summary>How the track writes an instant in UTC, as a Table Schema datetime format.</summary>
    public const string InstantFormat = "%Y-%m-%dT%H:%M:%S.%fZ";

    private const string Seconds = "seconds";
    private const string Meters = "meters";
    private const string Degrees = "degrees";
    private const string MetersPerSecond = "meters per second";

    /// <summary>The Table Schema's URN.</summary>
    public static string TableUrn => CaptureSchemas.Urn(TableKind, WorldTruthTrackWriter.FormatVersion);

    /// <summary>The summary schema's URN.</summary>
    public static string SummaryUrn => CaptureSchemas.Urn(SummaryKind, WorldTruthTrackWriter.FormatVersion);

    /// <summary>The Table Schema, as published.</summary>
    public static string TableText() => SchemaJson.Write(Table());

    /// <summary>The summary's JSON Schema, as published.</summary>
    public static string SummaryText() => SchemaJson.Write(Summary());

    /// <summary>The Table Schema of the track's CSV.</summary>
    public static JsonObject Table()
    {
        int version = WorldTruthTrackWriter.FormatVersion;
        Dictionary<string, JsonObject> described = Columns();
        var fields = new JsonArray();
        foreach (string column in WorldTruthTrackWriter.Columns)
        {
            if (!described.Remove(column, out JsonObject? field))
            {
                throw new InvalidOperationException(
                    $"The world truth track writes a column {column} that its Table Schema does not describe.");
            }

            fields.Add(field);
        }

        if (described.Count > 0)
        {
            throw new InvalidOperationException(
                "The world truth track's Table Schema describes columns the track does not write: "
                + string.Join(", ", described.Keys) + ".");
        }

        return new JsonObject
        {
            ["$schema"] = TableSchemaProfile,
            ["$id"] = TableUrn,
            ["name"] = "world_truth_track",
            ["title"] = $"World truth track, format version {version}",
            ["description"] =
                "Every vehicle SUMO had, at each sampled SUMO frame inside the capture window, drawn or not: one row "
                + "per vehicle per sample, in order of sumo_id within a sample. A capture's truth sidecars list the "
                + "vehicles a frame drew; this lists what the world contained. Rows join to a sidecar's vehicle "
                + "events by sumo_id and frame. An empty cell is a missing value. The format version is the one the "
                + $"summary beside the track declares in world_truth_track_version; this schema describes version {version}.",
            ["x-format-version"] = version,
            ["fields"] = fields,
            ["missingValues"] = SchemaJson.Array([string.Empty]),
            ["primaryKey"] = SchemaJson.Array(["sumo_id", "frame"]),
        };
    }

    /// <summary>The JSON Schema of the summary beside the track.</summary>
    public static JsonObject Summary()
    {
        int version = WorldTruthTrackWriter.FormatVersion;
        JsonObject document = SchemaJson.Document(
            SummaryUrn,
            $"World truth track summary, format version {version}",
            "The summary written beside a world truth track, <track>.summary.json: the track's format version, what "
            + "made it, its columns and sampling rate, what it holds so far and why it ended. It is written when the "
            + "track opens and again when the run ends; ended null is a track still being written, or one whose run "
            + $"was killed. This schema describes world_truth_track_version {version}; a summary without the field "
            + "is version 1, an older shape with fewer columns that this schema does not describe.");
        var definitions = new JsonObject();
        ProducerSchema.Define(definitions);

        JsonObject ended = SchemaJson.Object(
            "How the run ended.",
            new JsonField("reason", SchemaJson.Words("Why the run ended.", RunManifestWriter.Endings)),
            new JsonField("stage", SchemaJson.Words(
                "The stage a stopped run stopped at. Only where reason is run_stopped.",
                Enum.GetValues<CoSimSessionStage>().Select(stage => stage.ToString())), Required: false),
            new JsonField("cause", SchemaJson.Words(
                "Why a stopped run stopped. Only where reason is run_stopped.",
                Enum.GetValues<CoSimStopCause>().Select(cause => cause.Code())), Required: false),
            new JsonField("last_sumo_frame_s", SchemaJson.Number("The last SUMO frame read.", Seconds)),
            new JsonField("last_rendered_s", SchemaJson.OrNull(SchemaJson.Number(
                "The last frame rendered; null where none was.", Seconds))),
            new JsonField("last_rendered_frame", SchemaJson.OrNull(SchemaJson.Integer(
                "The last frame rendered; null where none was.", minimum: 0))));
        ended["if"] = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["reason"] = new JsonObject { ["const"] = RunManifestWriter.RunStoppedEnding },
            },
        };
        ended["then"] = new JsonObject { ["required"] = SchemaJson.Array(["stage", "cause"]) };
        ended["else"] = new JsonObject
        {
            ["not"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    new JsonObject { ["required"] = SchemaJson.Array(["stage"]) },
                    new JsonObject { ["required"] = SchemaJson.Array(["cause"]) }),
            },
        };

        JsonObject body = SchemaJson.Object(
            "The summary.",
            new JsonField("world_truth_track_version", SchemaJson.Constant(
                "The format version of the track and its summary.", version)),
            ProducerSchema.Field("producer",
                "What made the track, stamped each time the summary is written. Absent from a summary written before "
                + "summaries carried it.", required: false),
            new JsonField("track", SchemaJson.Text("The track's file name, beside the summary.")),
            new JsonField("columns", SchemaJson.Constant(
                "The track's columns, in order: its header line.", SchemaJson.Array(WorldTruthTrackWriter.Columns))),
            new JsonField("instant", SchemaJson.Constant(
                "The clock every row's sim_time_s is read on: TraCI's, for the SUMO frame the row describes.",
                WorldTruthTrackWriter.InstantClock)),
            new JsonField("sumo_step_s", SchemaJson.Number("SUMO's step length.", Seconds)),
            new JsonField("interval_s", SchemaJson.Number("Simulated time between samples.", Seconds)),
            new JsonField("every_sumo_steps", SchemaJson.Integer(
                "How many SUMO frames inside the window each sample is apart; 1 samples every frame.", minimum: 1)),
            new JsonField("outside_window_interval_s", SchemaJson.Constant(
                "Always null: nothing is written outside the capture window.", null)),
            new JsonField("samples", SchemaJson.Integer("Samples written so far.", minimum: 0)),
            new JsonField("rows", SchemaJson.Integer("Rows written so far, one per vehicle per sample.", minimum: 0)),
            new JsonField("first_sample_s", SchemaJson.OrNull(SchemaJson.Number(
                "The first sample's instant; null before one is written.", Seconds))),
            new JsonField("last_sample_s", SchemaJson.OrNull(SchemaJson.Number(
                "The last sample's instant; null before one is written.", Seconds))),
            new JsonField("ended", SchemaJson.OrNull(ended)));
        foreach ((string name, JsonNode? value) in body.ToList())
        {
            if (name == "description")
            {
                continue;
            }

            body.Remove(name);
            document[name] = value;
        }

        document["$defs"] = definitions;
        return document;
    }

    private static Dictionary<string, JsonObject> Columns()
    {
        IEnumerable<string> bands = IlluminationBands.Names;
        IEnumerable<string> kinds = PngChunkSchemas.ElevationKinds();
        var columns = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        void Add(string name, string type, string description, string? unit = null, bool required = true,
                 JsonObject? constraints = null, Action<JsonObject>? more = null)
        {
            var field = new JsonObject { ["name"] = name, ["type"] = type };
            more?.Invoke(field);
            field["description"] = description;
            if (unit is not null)
            {
                field["x-unit"] = unit;
            }

            constraints ??= new JsonObject();
            if (required)
            {
                constraints.Insert(0, "required", true);
            }

            if (constraints.Count > 0)
            {
                field["constraints"] = constraints;
            }

            columns.Add(name, field);
        }

        static JsonObject Range(double? minimum, double? maximum)
        {
            var range = new JsonObject();
            if (minimum is { } low)
            {
                range["minimum"] = low;
            }

            if (maximum is { } high)
            {
                range["maximum"] = high;
            }

            return range;
        }

        static JsonObject Enumeration(IEnumerable<string> words) => new() { ["enum"] = SchemaJson.Array(words) };

        Add("time_utc", "datetime",
            "The row's instant as a UTC time, from the scenario's epoch: the simulated civil time, not the wall "
            + "clock. Empty where the run declared no epoch.", required: false,
            more: field => field["format"] = InstantFormat);
        Add("sim_time_s", "number", "Simulated time of the SUMO frame the row describes, on TraCI's clock.", Seconds);
        Add("uid", "string",
            "The vehicle's track identifier, CARLA-TRUTH-SUMO-<sumo_id>: the uid a capture sidecar gives the same vehicle.",
            constraints: new JsonObject { ["pattern"] = "CARLA-TRUTH-SUMO-.+" });
        Add("callsign", "string", "<base_type>-<sumo_id>, as a capture sidecar's callsign.");
        Add("cot_type", "string", "The Cursor-on-Target type: a ground vehicle of neutral affiliation.",
            constraints: Enumeration(["a-n-G-E-V"]));
        Add("how", "string", "How the position was obtained: m-g, machine generated.", constraints: Enumeration(["m-g"]));
        Add("lat", "number", "Latitude, WGS84, of the point on the ground under the vehicle's front bumper.", Degrees,
            constraints: Range(-90, 90));
        Add("lon", "number", "Longitude, WGS84, of the same point.", Degrees, constraints: Range(-180, 180));
        Add("hae_m", "number",
            "Height above the WGS84 ellipsoid of the bare-earth ground under that point. Empty where the vehicle stands "
            + "off the world's ground grid.", Meters, required: false);
        Add("ce_m", "number", "Circular error. Always 0: truth.", Meters, constraints: Range(0, 0));
        Add("le_m", "number", "Linear error. Always 0: truth.", Meters, constraints: Range(0, 0));
        Add("course_deg", "number", "SUMO's heading for the vehicle, clockwise from true north.", Degrees,
            constraints: Range(0, 360));
        Add("speed_mps", "number", "SUMO's speed for the vehicle.", MetersPerSecond, constraints: Range(0, null));
        Add("vx", "number", "Velocity east, from SUMO's speed and heading, in CARLA's frame (x east, y south).",
            MetersPerSecond);
        Add("vy", "number", "Velocity south, from SUMO's speed and heading.", MetersPerSecond);
        Add("vz", "number", "Velocity up. Always 0: SUMO moves vehicles on the plane.", MetersPerSecond,
            constraints: Range(0, 0));
        Add("base_type", "string",
            "The vehicle's base type, such as car, van or truck: the vehicle catalogue's for the blueprint its SUMO type "
            + "names, or the one its SUMO vehicle class maps to.");
        Add("type_id", "string",
            "The SUMO vehicle type. A capture sidecar calls this vtype_id; its own type_id is the CARLA blueprint.");
        Add("special_type", "string", "The vehicle's kind from the catalogue. Empty where it has none.", required: false);
        Add("length_m", "number", "The length the SUMO type declares, which SUMO's car-following used.", Meters,
            constraints: Range(0, null));
        Add("width_m", "number", "The width the SUMO type declares.", Meters, constraints: Range(0, null));
        Add("height_m", "number", "The height the SUMO type declares.", Meters, constraints: Range(0, null));
        Add("color", "string", "The SUMO type's color, red,green,blue, each 0 to 255.",
            constraints: new JsonObject { ["pattern"] = "[0-9]{1,3},[0-9]{1,3},[0-9]{1,3}" });
        Add("role_name", "string",
            "The SUMO flow the vehicle came from: its id up to the last dot, or the whole id where it has none.");
        Add("edge", "string", "The SUMO edge the vehicle was on.");
        Add("lane", "string", "The SUMO lane the vehicle was on. Empty where it was on none, as a vehicle parked off the road is.",
            required: false);
        Add("sumo_x", "number", "Position east in SUMO's network coordinates.", Meters);
        Add("sumo_y", "number", "Position north in SUMO's network coordinates.", Meters);
        Add("carla_x", "number", "Position east in CARLA's frame: sumo_x.", Meters);
        Add("carla_y", "number", "Position south in CARLA's frame: minus sumo_y.", Meters);
        Add("sumo_id", "string", "The SUMO vehicle: the key that joins the row to the sidecars and the supervision plan.");
        Add("entity_id", "string", "The vehicle's entity id, which is its SUMO id.");
        Add("frame", "integer", "The CARLA frame that rendered this SUMO frame.", constraints: Range(0, null));
        Add("render_state", "string",
            "rendered where a body drew the vehicle on the frame; simulated_only where none did.",
            constraints: Enumeration(CoreFamily("render_state")));
        Add("render_reason", "string",
            "Why no body drew the vehicle, the first that holds: no_world, the run renders no world; outside_limit, an "
            + "optional render-set limit left it no body; no_blueprint or unknown_extent, its type has no measured "
            + "body; no_ground, it stands off the world's ground grid; not_drawn, none of those. Empty where it was "
            + "rendered.", required: false, constraints: Enumeration(WorldTruthTrackWriter.RenderReasons));
        Add("actor_id", "integer", "The body that drew the vehicle. Empty where none did.", required: false,
            constraints: Range(0, null));
        Add("in_window", "boolean", "Always 1: the track holds only frames inside the capture window.",
            more: field =>
            {
                field["trueValues"] = SchemaJson.Array(["1"]);
                field["falseValues"] = SchemaJson.Array(["0"]);
            });
        Add("sun_elevation_deg", "number",
            "The sun's geometric elevation the world reported on the frame's tick. Empty where it reported none.",
            Degrees, required: false);
        Add("sun_corrected_elevation_deg", "number",
            "The sun's refraction-corrected elevation, where the world's reading carries it.", Degrees, required: false);
        Add("illumination_band", "string",
            "The sun's illumination band, cut as a capture's is, by " + IlluminationBands.Source
            + ". Empty where there is no sun.", required: false, constraints: Enumeration(bands));
        Add("illumination_band_elevation", "string",
            "Which elevation the band was cut from: refraction_corrected wherever the reading carries it.",
            required: false, constraints: Enumeration(kinds));
        return columns;
    }

    private static IEnumerable<string> CoreFamily(string family) =>
        CoreVocabulary.Families.Single(entry => entry.Family == family).Terms;
}
