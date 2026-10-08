using System.Text.Json.Nodes;
using CarlaNet.Recording;
using CarlaNet.Types.Illumination;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim.Schemas;

/// <summary>
/// The JSON Schema of one row of the run manifest <see cref="RunManifestWriter"/> writes, every kind of row,
/// told apart by <c>row</c>.
/// </summary>
/// <remarks>
/// The schema describes one line of <c>manifest.jsonl</c>. What holds across lines -- the opening row
/// first, the terminal row last and only in a run that reached its end -- is the file's, and a validator
/// checks it beside the schema. The rows' kinds, the admission, release and ending words, the stages and
/// causes a stopped run names, and every vocabulary word are read from the writer and the types it writes
/// them from.
/// </remarks>
public static class RunManifestSchema
{
    /// <summary>The kind the schema's URN names.</summary>
    public const string Kind = "run-manifest";

    private const string Seconds = "seconds";
    private const string Meters = "meters";
    private const string Degrees = "degrees";
    private const string MetersPerSecond = "meters per second";

    private const string ExposureDefinition = "camera_exposure";
    private const string IlluminationDefinition = "illumination_policy";
    private const string WorstDefinition = "worst_divergence";
    private const string TripleDefinition = "interval_triple";

    /// <summary>The schema's URN.</summary>
    public static string Urn => CaptureSchemas.Urn(Kind, RunManifestWriter.FormatVersion);

    /// <summary>The schema, as published.</summary>
    public static string Text() => SchemaJson.Write(Schema());

    /// <summary>The schema.</summary>
    public static JsonObject Schema()
    {
        int version = RunManifestWriter.FormatVersion;
        JsonObject document = SchemaJson.Document(
            Urn,
            $"Run manifest row, format version {version}",
            "One row of a run manifest, truth/manifest.jsonl: one JSON object per line, appended as the run "
            + "goes, its kind in row. The first row is manifest_opened, which carries the manifest's format "
            + "version in manifest_version; the last row of a run that reached its end is manifest_closed, and a "
            + "manifest without it is a run that was interrupted. Every sim_time_s is simulated time in seconds: "
            + "TraCI's clock for the SUMO frame an admission or an event describes, and for a release the instant "
            + "of the first frame that no longer draws the vehicle. "
            + $"This schema describes manifest_version {version}.");
        document["type"] = "object";
        document["required"] = SchemaJson.Array(["row"]);
        document["properties"] = new JsonObject
        {
            ["row"] = SchemaJson.Words("The row's kind.", RunManifestWriter.RowKinds),
        };

        var rows = new JsonArray();
        var definitions = new JsonObject();
        foreach ((string kind, JsonObject row) in Rows())
        {
            definitions[kind] = row;
            rows.Add(new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["properties"] = new JsonObject { ["row"] = new JsonObject { ["const"] = kind } },
                    ["required"] = SchemaJson.Array(["row"]),
                },
                ["then"] = SchemaJson.Reference(kind),
            });
        }

        document["allOf"] = rows;
        Shared(definitions);
        document["$defs"] = definitions;
        return document;
    }

    private static IEnumerable<(string Kind, JsonObject Row)> Rows()
    {
        yield return (RunManifestWriter.OpenedRow, Opened());
        yield return (RunManifestWriter.InstanceRow, Instance());
        yield return (RunManifestWriter.SeriesRow, Series());
        yield return (RunManifestWriter.CohortRow, Cohort());
        yield return (RunManifestWriter.SensorPlacedRow, Row(RunManifestWriter.SensorPlacedRow,
            "A camera the caller placed and named.",
            new JsonField("sensor_id", SchemaJson.Text("The camera's name, which its stills and its platform track carry.")),
            new JsonField("camera_actor_id", SchemaJson.Integer("The camera's CARLA actor id.", minimum: 0)),
            new JsonField("after_frame", OrNull(SchemaJson.Integer(
                "The last frame rendered before the camera was placed; null before any frame.", minimum: 0))),
            new JsonField("exposure", SchemaJson.Reference(ExposureDefinition,
                "The exposure the camera was given. Absent for a camera that carries none."), Required: false)));
        yield return (RunManifestWriter.AdmittedRow, Row(RunManifestWriter.AdmittedRow,
            "A vehicle taking a place in the render set, written once the frame that first drew it has rendered.",
            new JsonField("sim_time_s", SchemaJson.Number("When it was admitted.", Seconds)),
            new JsonField("sumo_id", SchemaJson.Text("The SUMO vehicle.")),
            new JsonField("vtype_id", SchemaJson.Text("Its SUMO vehicle type; empty where the frame did not carry it.")),
            new JsonField("reason", SchemaJson.Words(
                "Why: rendering_began, the first frame after the run fast-forwarded SUMO; inserted, SUMO inserted it "
                + "at this frame; entered_limit, it entered an optional render-set limit.",
                RunManifestWriter.AdmissionReasons)),
            new JsonField("frame", OrNull(SchemaJson.Integer(
                "The frame that first drew it, or the frame stamped with its admission where no body drew it; null "
                + "where no frame rendered it.", minimum: 0))),
            new JsonField("actor_id", OrNull(SchemaJson.Integer(
                "The body that first drew it; null where none did.", minimum: 0)))));
        yield return (RunManifestWriter.ReleasedRow, Row(RunManifestWriter.ReleasedRow,
            "A vehicle giving up its place in the render set. A vehicle still in the set when the run ends has no "
            + "release row.",
            new JsonField("sim_time_s", SchemaJson.Number(
                "The end of its interval: the instant of the first frame that no longer draws it.", Seconds)),
            new JsonField("sumo_id", SchemaJson.Text("The SUMO vehicle.")),
            new JsonField("vtype_id", SchemaJson.Text("Its SUMO vehicle type, as admitted.")),
            new JsonField("actor_id", OrNull(SchemaJson.Integer("The body that drew it; null where none did.", minimum: 0))),
            new JsonField("admitted_s", SchemaJson.Number("When it was admitted.", Seconds)),
            new JsonField("reason", SchemaJson.Words("Why it was released.",
                Enum.GetValues<RenderSetReleaseReason>().Select(RunManifestWriter.ReasonName)))));
        yield return (RunManifestWriter.CollisionBeganRow, Row(RunManifestWriter.CollisionBeganRow,
            "A collision SUMO reported, as it begins.",
            [new JsonField("sim_time_s", SchemaJson.Number("The SUMO frame that first reported it.", Seconds)),
             .. Collision()]));
        yield return (RunManifestWriter.CollisionEndedRow, Row(RunManifestWriter.CollisionEndedRow,
            "A collision SUMO reported, as a span, once it is over.",
            [new JsonField("sim_time_s", SchemaJson.Number("When it ended.", Seconds)),
             .. Collision(),
             new JsonField("began_s", SchemaJson.Number("When it began.", Seconds)),
             new JsonField("ended_s", SchemaJson.Number("When it ended: the first frame the two were apart.", Seconds)),
             new JsonField("collider_actor_id", OrNull(SchemaJson.Integer(
                 "The body that drew the collider; null where none did.", minimum: 0))),
             new JsonField("victim_actor_id", OrNull(SchemaJson.Integer(
                 "The body that drew the victim; null where none did.", minimum: 0)))]));
        yield return (RunManifestWriter.NotInsertedRow, Row(RunManifestWriter.NotInsertedRow,
            "A vehicle SUMO gave up inserting.",
            new JsonField("sim_time_s", SchemaJson.Number("When SUMO gave it up.", Seconds)),
            new JsonField("sumo_id", SchemaJson.Text("The SUMO vehicle.")),
            new JsonField("waiting_at_s", SchemaJson.Number("When it was last seen waiting to be inserted.", Seconds))));
        yield return (RunManifestWriter.EmergencyStopRow, Row(RunManifestWriter.EmergencyStopRow,
            "A vehicle SUMO reported making an emergency stop.",
            new JsonField("sim_time_s", SchemaJson.Number("The SUMO frame that reported it.", Seconds)),
            new JsonField("sumo_id", SchemaJson.Text("The SUMO vehicle."))));
        yield return (RunManifestWriter.TeleportRow, Row(RunManifestWriter.TeleportRow,
            "A vehicle SUMO began moving by teleport.",
            new JsonField("sim_time_s", SchemaJson.Number("The SUMO frame that reported it.", Seconds)),
            new JsonField("sumo_id", SchemaJson.Text("The SUMO vehicle."))));
        yield return (RunManifestWriter.WindowOpenRow, WindowSun(RunManifestWriter.WindowOpenRow, "begin"));
        yield return (RunManifestWriter.WindowEndRow, WindowSun(RunManifestWriter.WindowEndRow, "end"));
        yield return (RunManifestWriter.IntervalOpenedRow, Row(RunManifestWriter.IntervalOpenedRow,
            "One of the supervision plan's intervals, as the run opens it.",
            [new JsonField("sim_time_s", OrNull(SchemaJson.Number(
                 "The instant it opened: committed by an event, or failing that declared; null where neither is "
                 + "known, as for a phase entered before the window.", Seconds))),
             .. Triple(),
             new JsonField("role", SchemaJson.Text("The participant's role in the instance.")),
             .. Onsets()]));
        yield return (RunManifestWriter.IntervalClosedRow, Row(RunManifestWriter.IntervalClosedRow,
            "One of the supervision plan's intervals, as the run closes it. An interval whose participant SUMO never "
            + "inserted closes without having opened.",
            [new JsonField("sim_time_s", OrNull(SchemaJson.Number("The instant it closed.", Seconds))),
             .. Triple(),
             new JsonField("closed_by", OrNull(SchemaJson.Words("Why it closed.", CoreFamily("closed_by")))),
             .. Onsets(),
             new JsonField("committed_end_s", OrNull(SchemaJson.Number(
                 "When SUMO committed its end; null where it did not.", Seconds))),
             new JsonField("not_drawn", SchemaJson.List(
                 "The spans its participant was not drawn while it was open.",
                 SchemaJson.Object("A span the participant was not drawn.",
                     new JsonField("from_s", SchemaJson.Number("Its start.", Seconds)),
                     new JsonField("to_s", SchemaJson.Number("Its end.", Seconds)))))]));
        yield return (RunManifestWriter.DefectRow, Row(RunManifestWriter.DefectRow,
            "A defect the supervision binder found in how the run carried the plan out, rather than in the scenario.",
            new JsonField("found_by_s", OrNull(SchemaJson.Number(
                "The SUMO frame it was found at or before; null before any SUMO frame.", Seconds))),
            new JsonField("defect", SchemaJson.Text("The defect, in the binder's words; it names its own instants."))));
        yield return (RunManifestWriter.ClosedRow, Closed());
    }

    private static JsonObject Opened()
    {
        JsonObject illumination = SchemaJson.Reference(IlluminationDefinition);
        return Row(RunManifestWriter.OpenedRow,
            "The first row, written before anything is rendered: the manifest's format, what made it, the caller's "
            + "header and what the session established itself.",
            new JsonField("manifest_version", SchemaJson.Constant(
                "The manifest's format version.", RunManifestWriter.FormatVersion)),
            ProducerSchema.Field("producer",
                "What made the manifest. Absent from a manifest written before manifests carried it.", required: false),
            new JsonField("opened_wall_utc", SchemaJson.Text(
                "When the manifest was opened, by the wall clock, in UTC.", ProducerSchema.UtcMillisecondsPattern)),
            new JsonField("run", OrNull(SchemaJson.AnyObject(
                "The header the caller handed over, verbatim: the run's and the session's identity and its "
                + "channels. Its fields are the caller's; null where the caller gave none."))),
            new JsonField("scenario", SchemaJson.Object(
                "The scenario's files and digests, from its compile lock.",
                new JsonField("config_path", SchemaJson.Text("The SUMO configuration the run started.")),
                new JsonField("world_package", SchemaJson.Text("The world package the run drove.")),
                new JsonField("compiled", SchemaJson.Boolean("Whether a compile lock was found and checked.")),
                new JsonField("lock_path", SchemaJson.Text("Where the compile lock was looked for.")),
                new JsonField("scenario_id", OrNull(SchemaJson.Text("The scenario's id; null with no lock."))),
                new JsonField("specification_sha256", OrNull(SchemaJson.Text("The scenario specification's SHA-256, as the compile lock records it; null with no lock."))),
                new JsonField("config_sha256", OrNull(SchemaJson.Text("The SUMO configuration's SHA-256, as the compile lock records it; null with no lock."))),
                new JsonField("routes_sha256", OrNull(SchemaJson.Text("The routes file's SHA-256, as the compile lock records it; null with no lock."))),
                new JsonField("network_sha256", OrNull(SchemaJson.Text("The network's SHA-256, as the compile lock records it; null with no lock."))),
                new JsonField("additional_sha256", OrNull(SchemaJson.Text("The additional file's SHA-256, as the compile lock records it; null where there is none."))),
                new JsonField("catalogue_digest", SchemaJson.Text("The vehicle catalogue's digest.")),
                new JsonField("epoch_digest", OrNull(SchemaJson.Text("The scenario epoch's digest; null with no lock."))),
                new JsonField("world_opendrive_sha256", OrNull(SchemaJson.Text(
                    "The world's OpenDRIVE SHA-256 the lock records; null where it records none."))),
                new JsonField("world_network_fingerprint", OrNull(SchemaJson.Text(
                    "The world network's fingerprint the lock records; null where it records none."))),
                new JsonField("dry_run_ran", OrNull(SchemaJson.Boolean(
                    "Whether the compiler ran the scenario in SUMO alone before writing it; null where the lock records "
                    + "no such run, or there is no lock."))),
                new JsonField("skipped_dry_run_accepted", SchemaJson.Boolean(
                    "Whether the run was accepted although the compile skipped that run.")))),
            new JsonField("plan", OrNull(SchemaJson.Object(
                "The supervision plan the compile lock binds; null where there is none.",
                new JsonField("path", SchemaJson.Text("The plan file.")),
                new JsonField("sha256", Sha("Its SHA-256.")),
                new JsonField("plan_id", SchemaJson.Text("The plan's id.")),
                new JsonField("spec_version", SchemaJson.Integer("The specification version it was compiled from.")),
                new JsonField("scenario_id", SchemaJson.Text("The scenario it belongs to.")),
                new JsonField("instances", Count("How many pattern instances it declares.")),
                new JsonField("series", Count("How many recurring series.")),
                new JsonField("cohorts", Count("How many cohorts.")),
                new JsonField("entities", Count("How many entities."))))),
            new JsonField("vocabulary", OrNull(SchemaJson.Object(
                "The plan's annotation vocabulary; null where there is no plan.",
                new JsonField("vocabulary_version", SchemaJson.Integer("The core vocabulary's version.", minimum: 1)),
                new JsonField("vocabulary_digest", SchemaJson.Text("The digest that pins what every label means.")),
                new JsonField("namespaces", SchemaJson.List("The author namespaces the plan uses.",
                    SchemaJson.Object("One author namespace.",
                        new JsonField("namespace", SchemaJson.Text("Its name.")),
                        new JsonField("version", SchemaJson.Integer("Its version.")))))))),
            new JsonField("sumo", SchemaJson.Object(
                "The SUMO settings the session checked and runs under.",
                new JsonField("release", OrNull(SchemaJson.Text("The SUMO release, such as 1.27.0; null where it could not be read."))),
                new JsonField("binary", OrNull(SchemaJson.Text("The SUMO executable the run launched."))),
                new JsonField("seed", SchemaJson.Integer("SUMO's random seed.")),
                new JsonField("step_s", SchemaJson.Number("SUMO's step length.", Seconds)),
                new JsonField("step_override_s", OrNull(SchemaJson.Number(
                    "The step length the run imposed over the configuration's; null where it imposed none.", Seconds))),
                new JsonField("collision_action", SchemaJson.Text(
                    "What SUMO does on a collision, as in force: SUMO's own word, such as warn, or none where "
                    + "accidents are ignored.")),
                new JsonField("collision_action_declared", OrNull(SchemaJson.Text(
                    "The collision action the configuration declares; null where it declares none."))),
                new JsonField("teleport_triggers", SchemaJson.List(
                    "SUMO's teleport triggers and whether each is enabled.",
                    SchemaJson.Object("One teleport trigger.",
                        new JsonField("name", SchemaJson.Text("The SUMO option, such as time-to-teleport.highways.")),
                        new JsonField("declared", OrNull(SchemaJson.Text("The value the configuration declares; null where none."))),
                        new JsonField("seconds", SchemaJson.Number("The value in force.", Seconds)),
                        new JsonField("enabled", SchemaJson.Boolean("Whether it can teleport a vehicle."))))),
                new JsonField("teleporting_accepted", SchemaJson.Boolean("Whether the run accepted teleporting.")),
                new JsonField("random_depart_offset_declared", OrNull(SchemaJson.Text(
                    "SUMO's random-depart-offset as declared; null where not."))),
                new JsonField("random_declared", OrNull(SchemaJson.Text("SUMO's random option as declared; null where not."))),
                new JsonField("scale", SchemaJson.Number("SUMO's demand scale.")),
                new JsonField("max_num_vehicles", SchemaJson.Integer("SUMO's vehicle limit; -1 for none.")),
                new JsonField("max_depart_delay_s", SchemaJson.Number(
                    "How long SUMO waits to insert a vehicle before giving it up; -1 for no limit.", Seconds)),
                new JsonField("lanechange_duration_s", SchemaJson.Number(
                    "How long a lane change takes; 0 for SUMO's instant lane change.", Seconds)))),
            new JsonField("clock", SchemaJson.Object(
                "How the run's clocks relate.",
                new JsonField("sumo_step_s", SchemaJson.Number("SUMO's step length.", Seconds)),
                new JsonField("world_delta_s", SchemaJson.Number("The CARLA world's fixed step.", Seconds)),
                new JsonField("capture_hz", SchemaJson.Number("Captures per simulated second.", "per second")),
                new JsonField("world_ticks_per_sumo_step", SchemaJson.Integer("World ticks per SUMO step.", minimum: 1)),
                new JsonField("world_ticks_per_capture", SchemaJson.Integer("World ticks per capture.", minimum: 1)))),
            new JsonField("window", SchemaJson.Object(
                "When the run renders and when its capture window opens.",
                new JsonField("rendered_from_s", SchemaJson.Number(
                    "The simulated second rendering starts at; SUMO runs alone before it.", Seconds)),
                new JsonField("opens_at_s", OrNull(SchemaJson.Number(
                    "The simulated second the capture window opens at; null where it opens with rendering.", Seconds))))),
            new JsonField("render_set", SchemaJson.Object(
                "Which vehicles the run draws.",
                new JsonField("policy", SchemaJson.Text("The render-set policy, described in words.")),
                new JsonField("limits", SchemaJson.Boolean("Whether an optional limit can leave a vehicle without a body.")),
                new JsonField("capacity", OrNull(SchemaJson.Integer("The limit's capacity; null for none.", minimum: 0))),
                new JsonField("draw_distance_m", OrNull(SchemaJson.Number(
                    "The draw distance bodies are drawn under; null for none.", Meters))))),
            new JsonField("vehicle_lights", SchemaJson.Object(
                "The rule the vehicle lights follow for the whole run.",
                new JsonField("driven", SchemaJson.Boolean(
                    "Whether the session drives the lights at all; where not, every body keeps the lights it was "
                    + "spawned with and the rest is null.")),
                new JsonField("headlights_follow_sun", SchemaJson.Boolean(
                    "Whether the headlights follow the sun; false under a policy that leaves the sun alone, when "
                    + "every headlight stays off.")),
                new JsonField("headlights_on_below_deg", OrNull(SchemaJson.Number(
                    "The sun elevation the headlights come on below.", Degrees))),
                new JsonField("headlights_off_above_deg", OrNull(SchemaJson.Number(
                    "The sun elevation the headlights go off above.", Degrees))),
                new JsonField("headlights_elevation", OrNull(SchemaJson.Words(
                    "Which elevation the headlights read.",
                    [SolarElevationKinds.Name(SolarElevationKind.Geometric)]))),
                new JsonField("brake_lights", OrNull(SchemaJson.Words(
                    "Where the brake lights come from: SUMO's signals for each vehicle.",
                    [RunManifestWriter.SumoSignalsSource]))),
                new JsonField("turn_signals", OrNull(SchemaJson.Words(
                    "Where the turn signals come from: SUMO's signals for each vehicle.",
                    [RunManifestWriter.SumoSignalsSource]))))),
            new JsonField("solar", SchemaJson.Object(
                "The epoch and the illumination the run declared.",
                new JsonField("epoch", OrNull(SchemaJson.AnyObject(
                    "The scenario's epoch object as declared, which a scenario file defines; null where none was."))),
                new JsonField("epoch_declared", SchemaJson.Boolean("Whether an epoch was declared.")),
                new JsonField("epoch_block_sha256", OrNull(Sha("The epoch's digest; null where none was declared."))),
                new JsonField("illumination_declared", OrNull(SchemaJson.Reference(IlluminationDefinition,
                    "The illumination policy the run declared; null for none."))),
                new JsonField("illumination_in_force", OrNull(SchemaJson.Reference(IlluminationDefinition,
                    "The illumination policy in force; null for none."))),
                new JsonField("epoch_honoured", SchemaJson.Boolean(
                    "Whether each frame is lit by the sun of its own declared civil instant.")),
                new JsonField("advance_mechanism", SchemaJson.Constant(
                    "How an advancing sun is moved: the session writes it on every tick.",
                    RunManifestWriter.AdvanceMechanism)))),
            new JsonField("world_truth_track", OrNull(SchemaJson.Text(
                "Where the world truth track is written; null where none is."))));
    }

    private static JsonObject Instance()
    {
        return Row(RunManifestWriter.InstanceRow,
            "One pattern instance of the supervision plan, as declared.",
            new JsonField("instance_id", SchemaJson.Text("The instance: <scenario_id>/<name>.")),
            new JsonField("supervision", SupervisionState()),
            new JsonField("labels", Terms("The terms it is labeled with.")),
            new JsonField("parameters", SchemaJson.AnyObject("Its parameters, as the author declared them.")),
            new JsonField("hard_negative_for", OrNull(Terms("The patterns it is a matched negative for; null for none."))),
            new JsonField("aoi_refs", SchemaJson.List("The areas of interest it names.", SchemaJson.Text("An area of interest."))),
            new JsonField("participants", SchemaJson.List("Its participants.",
                SchemaJson.Object("One participant.",
                    new JsonField("participant", SchemaJson.Text("The plan's entity id.")),
                    new JsonField("sumo_id", SchemaJson.Text("The SUMO vehicle it is.")),
                    new JsonField("role", SchemaJson.Text("Its role in the instance."))))),
            new JsonField("intervals", SchemaJson.List("The intervals it declares.",
                SchemaJson.Object("One declared interval.",
                    new JsonField("participant", SchemaJson.Text("The participant it is about.")),
                    new JsonField("phase", SchemaJson.Text("Its phase.")),
                    new JsonField("anchor_start", OrNull(Anchor("The event its start is anchored to; null where it is declared by time."))),
                    new JsonField("anchor_end", OrNull(Anchor("The event its end is anchored to; null where none is."))),
                    new JsonField("declared_start_s", OrNull(SchemaJson.Number("Its declared start.", Seconds))),
                    new JsonField("declared_start_civil", OrNull(SchemaJson.Text("Its declared start as a civil time."))),
                    new JsonField("declared_end_s", OrNull(SchemaJson.Number("Its declared end.", Seconds))),
                    new JsonField("declared_end_civil", OrNull(SchemaJson.Text("Its declared end as a civil time."))),
                    new JsonField("declared_duration_s", OrNull(SchemaJson.Number("Its declared duration.", Seconds)))))));
    }

    private static JsonObject Series()
    {
        return Row(RunManifestWriter.SeriesRow,
            "One recurring series of the supervision plan, as declared, with its slots.",
            new JsonField("series_id", SchemaJson.Text("The series.")),
            new JsonField("rota_ref", SchemaJson.Text("The schedule in the scenario specification it is expanded from.")),
            new JsonField("cadence", SchemaJson.Words("How it lists its occasions.", CoreFamily("cadence"))),
            new JsonField("member_role", SchemaJson.Text("The role each slot's vehicle plays.")),
            new JsonField("supervision", SupervisionState()),
            new JsonField("labels", Terms("The terms it is labeled with.")),
            new JsonField("parameters", SchemaJson.AnyObject("Its parameters, as the author declared them.")),
            new JsonField("hard_negative_for", OrNull(Terms("The patterns it is a matched negative for; null for none."))),
            new JsonField("slots", SchemaJson.List("Its slots.",
                SchemaJson.Object("One slot.",
                    new JsonField("slot_key", SchemaJson.Text("The slot.")),
                    new JsonField("aoi_ref", SchemaJson.Text("The area of interest it is sited at.")),
                    new JsonField("declared_start_s", SchemaJson.Number("Its declared start.", Seconds)),
                    new JsonField("declared_end_s", OrNull(SchemaJson.Number("Its declared end.", Seconds))),
                    new JsonField("entity_id", SchemaJson.Text("The vehicle that fills it."))))));
    }

    private static JsonObject Cohort() =>
        Row(RunManifestWriter.CohortRow,
            "One cohort of the supervision plan: every vehicle a flow emits, as declared.",
            new JsonField("flow_id", SchemaJson.Text("The SUMO flow.")),
            new JsonField("supervision", SupervisionState()),
            new JsonField("labels", Terms("The terms its vehicles are labeled with.")),
            new JsonField("parameters", SchemaJson.AnyObject("Its parameters, as the author declared them.")));

    private static JsonObject WindowSun(string kind, string end)
    {
        bool opening = end == "begin";
        string at = opening ? "first" : "last";
        List<JsonField> fields =
        [
            new("window_index", SchemaJson.Integer("The capture window; always 0, a run having one.", minimum: 0)),
            new("sim_time_s", opening
                ? SchemaJson.Number($"The window's {at} capture tick, to the microsecond.", Seconds)
                : OrNull(SchemaJson.Number($"The window's {at} capture tick; null where it had none.", Seconds))),
            new("frame", opening
                ? SchemaJson.Integer("That tick's frame.", minimum: 0)
                : OrNull(SchemaJson.Integer("That tick's frame; null where it had none.", minimum: 0))),
            new(opening ? "begin_s" : "end_s", opening
                ? SchemaJson.Number("The same instant.", Seconds)
                : OrNull(SchemaJson.Number("The same instant.", Seconds))),
            new(opening ? "civil_begin" : "civil_end", OrNull(SchemaJson.Text(
                "The same instant as a civil time; null where no epoch was declared.", PngChunkSchemas.CivilPattern))),
            new($"solar_date_{end}", OrNull(SchemaJson.Text("The sun's date; null where the frame carried no sun.",
                                                            PngChunkSchemas.DatePattern))),
            new($"solar_time_{end}", OrNull(SchemaJson.Number("The sun's clock in its own time zone.", "hours"))),
            new($"sun_elevation_{end}_deg", OrNull(SchemaJson.Number("The sun's geometric elevation.", Degrees))),
            new($"sun_corrected_elevation_{end}_deg", OrNull(SchemaJson.Number(
                "The sun's refraction-corrected elevation; null where the world did not report it.", Degrees))),
            new($"sun_azimuth_{end}_deg", OrNull(SchemaJson.Number("The sun's azimuth, clockwise from true north.", Degrees))),
            new($"illumination_band_{end}", OrNull(SchemaJson.Words(
                "The sun's illumination band, cut as a capture's is.", IlluminationBands.Names))),
            new($"illumination_band_{end}_elevation", OrNull(SchemaJson.Words(
                "Which elevation the band was cut from.", PngChunkSchemas.ElevationKinds()))),
            new("advancing", OrNull(SchemaJson.Boolean("Whether the engine itself advanced the sun's clock."))),
            new("rate", OrNull(SchemaJson.Number("Sun-clock seconds per simulated second while it does.",
                                                 "seconds per second"))),
        ];
        if (opening)
        {
            fields.Add(new JsonField("sun_time_zone_hours", OrNull(SchemaJson.Number(
                "The time zone the world's sun was configured with before the run bound it.", "hours"))));
            fields.Add(new JsonField("no_sun", SchemaJson.Boolean("Whether the world held no sun.")));
            fields.Add(new JsonField("declared_civil_vs_solar_delta_h", OrNull(SchemaJson.Number(
                "The declared sun's clock minus the civil clock at the window's opening.", "hours"))));
            return Row(kind, "The sun the world reported at the capture window's first capture tick.", [.. fields]);
        }

        fields.Add(new JsonField("capture_ticks", Count("How many capture ticks the window held.")));
        fields.Add(new JsonField("solar_residual", SchemaJson.Object(
            "The audit of the world's sun against the declaration over the window.",
            new JsonField("max_delta_solar_s", OrNull(SchemaJson.Number("The worst clock residual.", Seconds))),
            new JsonField("max_delta_elev_deg", OrNull(SchemaJson.Number("The worst angle between the two suns.", Degrees))),
            new JsonField("max_delta_corrected_deg", OrNull(SchemaJson.Number(
                "The worst refraction-corrected elevation residual.", Degrees))),
            new JsonField("max_at_tick", OrNull(SchemaJson.Integer("The tick of the worst angle.", minimum: 0))),
            new JsonField("max_at_sim_time_s", OrNull(SchemaJson.Number("The instant of the worst angle.", Seconds))),
            new JsonField("tolerance_s", OrNull(SchemaJson.Number("The audit's clock tolerance.", Seconds))),
            new JsonField("tolerance_elev_deg", OrNull(SchemaJson.Number("The audit's angle tolerance.", Degrees))),
            new JsonField("audited_ticks", Count("How many ticks were audited.")),
            new JsonField("capture_ticks", Count("How many capture ticks the window held.")),
            new JsonField("within_tolerance", SchemaJson.Boolean("Whether the audit held within its tolerance.")),
            new JsonField("audit_skipped", SchemaJson.Boolean("Whether no audit was made.")))));
        fields.Add(new JsonField("no_sun", SchemaJson.Boolean("Whether the world held no sun.")));
        fields.Add(new JsonField("sun_matched_declaration", SchemaJson.Boolean(
            "A fact, not a verdict: an epoch was declared, the policy bound the sun, the world held one, and the "
            + "audit held within its tolerance over the window.")));
        return Row(kind, "The sun the world reported at the capture window's last capture tick, and the audit over the window.",
                   [.. fields]);
    }

    private static JsonObject Closed()
    {
        return Row(RunManifestWriter.ClosedRow,
            "The last row of a run that reached its end: why it ended, what the manifest holds, and the plan's "
            + "intervals still open or never opened.",
            new JsonField("ended", SchemaJson.Words("Why the run ended.", RunManifestWriter.Endings)),
            new JsonField("stage", OrNull(SchemaJson.Words(
                "The stage a stopped run stopped at; null where it was not stopped.",
                Enum.GetValues<CoSimSessionStage>().Select(stage => stage.ToString())))),
            new JsonField("cause", OrNull(SchemaJson.Words(
                "Why a stopped run stopped; null where it was not stopped.",
                Enum.GetValues<CoSimStopCause>().Select(cause => cause.Code())))),
            new JsonField("caller_reason", OrNull(SchemaJson.Text(
                "The caller's own word for why it stopped, such as window_end; null where it gave none."))),
            new JsonField("last_sumo_frame_s", OrNull(SchemaJson.Number("The last SUMO frame read.", Seconds))),
            new JsonField("last_rendered_s", OrNull(SchemaJson.Number("The last frame rendered.", Seconds))),
            new JsonField("last_rendered_frame", OrNull(SchemaJson.Integer("The last frame rendered.", minimum: 0))),
            new JsonField("render_admitted", Count("Admission rows written.")),
            new JsonField("render_released", Count("Release rows written.")),
            new JsonField("still_in_render_set", Count("Vehicles still in the render set as the run ended.")),
            new JsonField("events", Count("Collision, not-inserted, emergency-stop and teleport rows written.")),
            new JsonField("intervals_opened", Count("Interval openings written.")),
            new JsonField("intervals_closed", Count("Interval closings written.")),
            new JsonField("supervision_defects", Count("Supervision defects written.")),
            new JsonField("open_intervals", SchemaJson.List(
                "The intervals still open as the run ended, which the run closes after this row.",
                SchemaJson.Object("One open interval.",
                    [.. Triple(),
                     new JsonField("opened_s", OrNull(SchemaJson.Number("When it opened.", Seconds))),
                     new JsonField("begun_before_window", SchemaJson.Boolean("Whether it began before the window."))]))),
            new JsonField("open_intervals_close_as", SchemaJson.Words(
                "The closed_by word the open intervals are closed with after this row.",
                [CoreVocabulary.Name(ClosedBy.ScenarioEnd), CoreVocabulary.Name(ClosedBy.CaptureWindowEnd)])),
            new JsonField("never_opened", SchemaJson.List(
                "The plan's intervals nothing in the run opened or closed.",
                SchemaJson.Reference(TripleDefinition))),
            new JsonField("bridge_divergence", SchemaJson.Object(
                "How far the world departed from what the bridge commanded, over the whole run.",
                new JsonField("samples", Count("Comparisons taken; 0 where nothing was compared.")),
                new JsonField("vehicle_ticks_with_no_read_back", Count("Vehicle-ticks nothing read back.")),
                new JsonField("worst_position_m", SchemaJson.Number("The worst position difference.", Meters, minimum: 0)),
                new JsonField("mean_position_m", SchemaJson.Number("The mean position difference.", Meters, minimum: 0)),
                new JsonField("worst_yaw_deg", SchemaJson.Number("The worst yaw difference.", Degrees, minimum: 0)),
                new JsonField("worst_pitch_deg", SchemaJson.Number("The worst pitch difference.", Degrees, minimum: 0)),
                new JsonField("worst_roll_deg", SchemaJson.Number("The worst roll difference.", Degrees, minimum: 0)),
                new JsonField("worst_velocity_m_per_s", SchemaJson.Number(
                    "The worst velocity difference.", MetersPerSecond, minimum: 0)),
                new JsonField("mean_velocity_m_per_s", SchemaJson.Number(
                    "The mean velocity difference.", MetersPerSecond, minimum: 0)),
                new JsonField("mean_commanded_speed_m_per_s", SchemaJson.Number(
                    "The mean commanded speed the differences are read against.", MetersPerSecond, minimum: 0)),
                new JsonField("worst_position_on", OrNull(SchemaJson.Reference(WorstDefinition,
                    "Where the worst position difference was measured; null where nothing was compared."))),
                new JsonField("worst_velocity_on", OrNull(SchemaJson.Reference(WorstDefinition,
                    "Where the worst velocity difference was measured; null where nothing was compared."))))),
            new JsonField("rows_before", Count("How many rows precede this one.")),
            new JsonField("closed_wall_utc", SchemaJson.Text(
                "When the manifest was closed, by the wall clock, in UTC.", ProducerSchema.UtcMillisecondsPattern)));
    }

    private static void Shared(JsonObject definitions)
    {
        ProducerSchema.Define(definitions);
        definitions[ExposureDefinition] = SchemaJson.Object(
            "The exposure a camera was given, under the names its stills' sidecars use.",
            new JsonField("post_process_profile", SchemaJson.Text("The post-process profile the camera loaded.")),
            new JsonField("method", SchemaJson.Words("manual or histogram.", [CameraExposure.Manual, CameraExposure.Histogram])),
            new JsonField("iso", SchemaJson.Number("The sensor's sensitivity, ISO.")),
            new JsonField("shutter_s", SchemaJson.Number("The shutter time.", Seconds)),
            new JsonField("fstop", SchemaJson.Number("The aperture, as an f-number.")),
            new JsonField("compensation_ev", SchemaJson.Number("Exposure compensation; above 0 brightens.", "EV")),
            new JsonField("ev100", OrNull(SchemaJson.Number(
                "The exposure value at ISO 100 under manual; null under histogram.", "EV"))));
        definitions[IlluminationDefinition] = SchemaJson.Object(
            "An illumination policy.",
            new JsonField("policy", SchemaJson.Words("The policy's name.", PngChunkSchemas.PolicyNames())),
            new JsonField("rate_sun_s_per_sim_s", SchemaJson.Number(
                "Sun-clock seconds per simulated second, under advance only.", "seconds per second"), Required: false),
            new JsonField("freeze_at_civil_time", SchemaJson.Text(
                "The time of day the sun is held at, under freeze_at only.", PngChunkSchemas.ClockPattern),
                Required: false),
            new JsonField("freeze_date_advances", SchemaJson.Boolean("Whether a frozen sun's date follows the calendar.")),
            new JsonField("require_sun", SchemaJson.Boolean("Whether the run refuses a world with no sun.")),
            new JsonField("note", OrNull(SchemaJson.Text("Why this policy, in the author's words; null for none."))));
        definitions[WorstDefinition] = SchemaJson.Object(
            "The vehicle and instant one worst figure was measured on.",
            new JsonField("sumo_id", SchemaJson.Text("The SUMO vehicle.")),
            new JsonField("sim_time_s", SchemaJson.Number("The instant the pose was computed for.", Seconds)),
            new JsonField("tick", SchemaJson.Integer("The session's tick.", minimum: 0)),
            new JsonField("actor_id", SchemaJson.Integer("The body it was written to.", minimum: 0)));
        definitions[TripleDefinition] = SchemaJson.Object(
            "One interval of the plan, named by its instance, participant and phase.",
            [.. Triple()]);
    }

    // ---- building blocks ------------------------------------------------------------------------------------------

    private static JsonObject Row(string kind, string description, params JsonField[] fields) =>
        SchemaJson.Object(description,
            [new JsonField("row", SchemaJson.Constant("The row's kind.", kind)), .. fields]);

    private static IEnumerable<JsonField> Collision() =>
    [
        new("collider", SchemaJson.Text("The vehicle SUMO holds responsible: the follower of a rear-end collision.")),
        new("victim", SchemaJson.Text("The other vehicle.")),
        new("collider_vtype_id", SchemaJson.Text("The collider's SUMO vehicle type.")),
        new("victim_vtype_id", SchemaJson.Text("The victim's SUMO vehicle type.")),
        new("kind", SchemaJson.Text("SUMO's own word for what happened, such as collision, frontal or junction.")),
        new("lane", SchemaJson.Text("The SUMO lane it was registered on.")),
        new("lane_pos_m", SchemaJson.Number("Where along that lane.", Meters)),
    ];

    private static IEnumerable<JsonField> Triple() =>
    [
        new("instance_id", SchemaJson.Text("The pattern instance.")),
        new("participant", SchemaJson.Text("The participant the interval is about.")),
        new("phase", SchemaJson.Text("The interval's phase.")),
    ];

    private static IEnumerable<JsonField> Onsets() =>
    [
        new("declared_start_s", OrNull(SchemaJson.Number("The start the author declared; null where none.", Seconds))),
        new("declared_end_s", OrNull(SchemaJson.Number("The end the author declared; null where none.", Seconds))),
        new("declared_duration_s", OrNull(SchemaJson.Number("The duration the author declared; null where none.", Seconds))),
        new("committed_start_s", OrNull(SchemaJson.Number(
            "When SUMO committed its start, by TraCI's clock; null where it did not.", Seconds))),
        new("observed_start_s", OrNull(SchemaJson.Number(
            "The rendered frame that showed its start; null where none did.", Seconds))),
        new("observed_start_frame", OrNull(SchemaJson.Integer("That frame; null where none did.", minimum: 0))),
        new("begun_before_window", SchemaJson.Boolean("Whether it began before the capture window opened.")),
    ];

    private static JsonObject SupervisionState() =>
        SchemaJson.Words("What the author asserts: annotated, nominal (an authored negative) or unlabelled (no assertion).",
                         CoreFamily("supervision_state"));

    private static JsonObject Terms(string description) =>
        SchemaJson.List(description, SchemaJson.Text("A term, namespace:name.", "^[a-z0-9_]+:[a-z0-9_]+$"));

    private static JsonObject Anchor(string description) =>
        SchemaJson.Text(description + " An anchor is an event of the vehicle's, a stop or a phase with its index "
                        + "counted from 0: " + string.Join(", ", AnchorSpellings()) + ".",
                        "^(" + string.Join("|", CoreFamily("interval_anchor")) + ")(:[0-9]+)?$");

    private static IEnumerable<string> AnchorSpellings() =>
        CoreFamily("interval_anchor").Select(name => name == CoreVocabulary.Name(AnchorEvent.Depart) ? name : name + ":0");

    private static JsonObject Sha(string description) => SchemaJson.Text(description, PngChunkSchemas.Sha256Pattern);

    private static JsonObject Count(string description) => SchemaJson.Integer(description, minimum: 0);

    private static JsonObject OrNull(JsonObject schema) => SchemaJson.OrNull(schema);

    private static IEnumerable<string> CoreFamily(string family) =>
        CoreVocabulary.Families.Single(entry => entry.Family == family).Terms;
}
