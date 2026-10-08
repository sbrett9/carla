using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Types.Illumination;
using CarlaNet.Types.Provenance;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim.Schemas;

/// <summary>
/// The XML Schema (XSD 1.0) of the truth sidecar <see cref="CotWriter"/> writes beside every still.
/// </summary>
/// <remarks>
/// <para><b>No namespace.</b> The writer writes its elements in no namespace, so the schema has no target
/// namespace: a target namespace would describe documents the writer does not write. The schema names its
/// URN and format version in its annotation and its <c>version</c> attribute instead.</para>
///
/// <para><b>What XSD 1.0 cannot say.</b> Which attributes a vehicle carries depends on where its box fell
/// and what the recorder measured, a rule across attributes that XSD 1.0 cannot state. Each attribute's
/// documentation says when it is written, and its <c>cap:onlyInPicture</c> annotation marks the ones written
/// only for a vehicle whose box fell in the picture (<c>in_frame</c> of <c>wholly</c> or <c>partly</c>).
/// Every value's unit is in its <c>cap:unit</c> annotation.</para>
/// </remarks>
public static class TruthSidecarSchema
{
    /// <summary>The kind the schema's URN names.</summary>
    public const string Kind = "truth-sidecar";

    /// <summary>The namespace of the schema's own annotations: units and which attributes only a vehicle in the picture carries.</summary>
    public const string AnnotationNamespace = "urn:carla-sumo-capture:schema:annotation";

    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly XNamespace Cap = AnnotationNamespace;

    private const string Meters = "meters";
    private const string Degrees = "degrees";
    private const string Pixels = "pixels";
    private const string Seconds = "seconds";
    private const string MetersPerSecond = "meters per second";

    /// <summary>The schema's URN.</summary>
    public static string Urn => CaptureSchemas.Urn(Kind, CotWriter.FormatVersion);

    /// <summary>The schema, as published.</summary>
    public static string Text()
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var buffer = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(buffer, settings))
        {
            Document().Save(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>The schema as an XML document.</summary>
    public static XDocument Document()
    {
        int version = CotWriter.FormatVersion;
        var schema = new XElement(Xs + "schema",
            new XAttribute(XNamespace.Xmlns + "xs", Xs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cap", Cap.NamespaceName),
            new XAttribute("elementFormDefault", "unqualified"),
            new XAttribute("attributeFormDefault", "unqualified"),
            new XAttribute("version", version.ToString(CultureInfo.InvariantCulture)),
            new XElement(Xs + "annotation",
                new XElement(Xs + "documentation",
                    $"The truth sidecar, format version {version}: one XML file beside every still, "
                    + "<camera>_<local capture time>.xml, holding the truth of the still's own simulation frame. "
                    + "The root <events> carries the capture's identity; under it come what made the file, the sun "
                    + "the world reported, what the run declared the sun to be, the camera platform's event and one "
                    + "Cursor-on-Target event per vehicle. A sidecar with no format_version was written before "
                    + "sidecars carried one and is version 1, which this schema describes. Units are in each "
                    + "attribute's cap:unit annotation; cap:onlyInPicture marks the attributes written only for a "
                    + "vehicle whose box fell in the picture (in_frame of wholly or partly). Positions are WGS84 "
                    + "latitude and longitude, and heights are meters above the WGS84 ellipsoid in the bare-earth "
                    + "convention."),
                new XElement(Xs + "appinfo",
                    new XElement(Cap + "id", Urn),
                    new XElement(Cap + "formatVersion", version))),
            new XElement(Xs + "element", new XAttribute("name", "events"), new XAttribute("type", "Events"),
                Doc("The sidecar's root: one capture of one camera.")));

        schema.Add(SimpleTypes());
        schema.Add(ComplexTypes());
        return new XDocument(new XDeclaration("1.0", "utf-8", null), schema);
    }

    // ---- simple types --------------------------------------------------------------------------------------------

    private static IEnumerable<XElement> SimpleTypes()
    {
        yield return Restriction("FormatVersion", "xs:positiveInteger", "A format version: an integer from 1.");
        yield return Restriction("Instant", "xs:dateTime",
            "An instant in UTC to the millisecond, with a trailing Z.",
            Facet("pattern", "[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z"));
        yield return Restriction("CalendarDate", "xs:date", "A date, year-month-day.",
            Facet("pattern", "[0-9]{4}-[0-9]{2}-[0-9]{2}"));
        yield return Restriction("CivilInstant", "xs:string",
            "A civil instant with its UTC offset, to the second or a fraction of it.",
            Facet("pattern", "[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,7})?[+\\-][0-9]{2}:[0-9]{2}"));
        yield return Restriction("UtcInstant", "xs:string",
            "An instant in UTC, to the second or a fraction of it, with a trailing Z.",
            Facet("pattern", "[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,7})?Z"));
        yield return Restriction("ClockTime", "xs:string", "A time of day, hours:minutes:seconds.",
            Facet("pattern", "[0-9]{2}:[0-9]{2}:[0-9]{2}"));
        yield return Restriction("TrueOrFalse", "xs:boolean", "true or false, spelled out.",
            Facet("pattern", "true|false"));
        yield return Restriction("Latitude", "xs:decimal", "A WGS84 latitude.",
            Facet("minInclusive", "-90"), Facet("maxInclusive", "90"));
        yield return Restriction("Longitude", "xs:decimal", "A WGS84 longitude.",
            Facet("minInclusive", "-180"), Facet("maxInclusive", "180"));
        yield return Restriction("Bearing", "xs:decimal",
            "A direction clockwise from true north, 0 to 360 (360 only where rounding reaches it).",
            Facet("minInclusive", "0"), Facet("maxInclusive", "360"));
        yield return Restriction("NonNegativeDecimal", "xs:decimal", "A decimal number, 0 or more.",
            Facet("minInclusive", "0"));
        yield return Restriction("Fraction", "xs:decimal", "A share, 0 (none) to 1 (all).",
            Facet("minInclusive", "0"), Facet("maxInclusive", "1"));
        yield return Restriction("Sha256", "xs:string", "A SHA-256 digest in lowercase hexadecimal.",
            Facet("pattern", "[0-9a-f]{64}"));
        yield return Restriction("CotType", "xs:string", "A Cursor-on-Target atom type, such as a-n-G-E-V.",
            Facet("pattern", "a-.+"));
        yield return Restriction("Color", "xs:string",
            "A color as red,green,blue, each 0 to 255, or empty where the blueprint has none.",
            Facet("pattern", "([0-9]{1,3},[0-9]{1,3},[0-9]{1,3})?"));
        yield return Restriction("Unknown", "xs:string", "The one word unknown.", Facet("enumeration", "unknown"));

        yield return Words("VehiclesListed",
            "Which vehicles the events are, where they are not every vehicle actor the world held: rendered, "
            + "exactly the bodies the frame drew, each named by the vehicle it drew; unknown, the frame's render set "
            + "was no longer held, so no vehicle is listed and the empty list is not an empty scene.",
            Enum.GetValues<SidecarVehicles>().Select(CotWriter.VehiclesValue).OfType<string>());
        yield return Words("InFrame",
            "Where the vehicle's box fell against the picture, from its eight corners projected through the "
            + "camera: wholly inside it; partly, crossing an edge; none, wholly outside it; behind_camera, a corner "
            + "at or behind the lens, so the box has no projection.",
            Enum.GetValues<InFrame>().Select(BoxProjector.SidecarValue));
        yield return Words("OcclusionUnmeasured",
            "Why occlusion was not measured, the reason nearest the vehicle where several hold.",
            Enum.GetValues<OcclusionUnmeasured>().Select(UnmeasuredOcclusion.SidecarValue));
        yield return Words("PoseSource",
            "Where a SUMO vehicle's drawn pose came from on this frame: sumo, the frame falls on a SUMO step and "
            + "the position is SUMO's own; interpolated, between SUMO steps, filled in along the lane; jump, SUMO "
            + "reported a step too far to drive in one step and the body is shown at SUMO's later position; stale, "
            + "the body could not be placed and stands where it was last drawn.",
            Enum.GetValues<PoseSource>().Select(PoseSources.SidecarValue));
        yield return Words("DrawDistanceReach",
            "Where the draw distance fell on the vehicle: partly, across its bounding sphere, so the image may show "
            + "its body without the parts beyond; wholly, the image shows nothing of it.",
            Enum.GetValues<DrawDistanceReach>().Select(DrawDistanceCheck.SidecarValue).OfType<string>());
        yield return Words("IlluminationBand",
            "An illumination band. The bands are cut by " + IlluminationBands.Source + ".", IlluminationBands.Names);
        yield return Words("ElevationKind", "Which of the sun's two elevations is meant.",
            PngChunkSchemas.ElevationKinds());
        yield return Words("IlluminationPolicy", "The illumination policy a run followed.", PngChunkSchemas.PolicyNames());
        yield return Words("ExposureMethod",
            "manual, a fixed exposure set by the ISO, shutter and aperture; histogram, metered by the engine "
            + "from each frame.", [CameraExposure.Manual, CameraExposure.Histogram]);
        yield return Words("SupervisionState",
            "What the scenario's author asserts of the vehicle: annotated, it carries out the named pattern; "
            + "nominal, an authored negative; unlabelled, no assertion, and never a negative.",
            CoreFamily("supervision_state"));

        int lowest = OcclusionEstimator.LevelFor(0.0);
        int highest = OcclusionEstimator.LevelFor(1.0);
        yield return Restriction("OcclusionLevel", "xs:integer",
            "The occlusion fraction as a band: 0 nothing hidden, 1 less than 30 %, 2 30 % to less than 60 %, "
            + "3 60 % to less than 90 %, 4 90 % or more.",
            Facet("minInclusive", lowest.ToString(CultureInfo.InvariantCulture)),
            Facet("maxInclusive", highest.ToString(CultureInfo.InvariantCulture)));

        // The lights: the words of every light CARLA's flags name, in flag order, and bit<n> for a light no
        // word names; or the single word none.
        List<string> named = [];
        for (int bit = 0; bit < 32; bit++)
        {
            string word = VehicleLights.SidecarValue((VehicleLightStateFlags)(1u << bit));
            if (!word.StartsWith("bit", StringComparison.Ordinal))
            {
                named.Add(word);
            }
        }

        yield return new XElement(Xs + "simpleType", new XAttribute("name", "LightWord"),
            Doc("One light commanded on: a named light, or bit<n> for a light CARLA declares that no word names."),
            new XElement(Xs + "union",
                new XElement(Xs + "simpleType",
                    new XElement(Xs + "restriction", new XAttribute("base", "xs:token"),
                        named.Select(word => Facet("enumeration", word)))),
                new XElement(Xs + "simpleType",
                    new XElement(Xs + "restriction", new XAttribute("base", "xs:token"),
                        Facet("pattern", "bit([0-9]|[12][0-9]|3[01])")))));
        yield return new XElement(Xs + "simpleType", new XAttribute("name", "LightWords"),
            Doc("The lights commanded on, one word each, separated by single spaces, in the order of CARLA's flags."),
            new XElement(Xs + "restriction",
                new XElement(Xs + "simpleType", new XElement(Xs + "list", new XAttribute("itemType", "LightWord"))),
                Facet("minLength", "1")));
        yield return new XElement(Xs + "simpleType", new XAttribute("name", "Lights"),
            Doc($"The lights commanded on for the vehicle, or {VehicleLights.NoneOn} where none is."),
            new XElement(Xs + "union", new XAttribute("memberTypes", "LightWords"),
                new XElement(Xs + "simpleType",
                    new XElement(Xs + "restriction", new XAttribute("base", "xs:token"),
                        Facet("enumeration", VehicleLights.NoneOn)))));

        yield return Restriction("Term", "xs:token",
            "A vocabulary term, namespace:name in lower case, digits and underscores.",
            Facet("pattern", "[a-z0-9_]+:[a-z0-9_]+"));
        yield return new XElement(Xs + "simpleType", new XAttribute("name", "Terms"),
            Doc("Vocabulary terms separated by single spaces."),
            new XElement(Xs + "restriction",
                new XElement(Xs + "simpleType", new XElement(Xs + "list", new XAttribute("itemType", "Term"))),
                Facet("minLength", "1")));
        yield return DecimalList("PixelRectangle", 4,
            "Four numbers: x min, y min, x max, y max, pixels; x across from the picture's left edge, y down from "
            + "its top edge.");
        yield return DecimalList("PixelQuadrilateral", 8,
            "Four corners as x y pairs, pixels, clockwise in the picture from the top-most corner (of two at the "
            + "same height, the left one).");
    }

    // ---- complex types -------------------------------------------------------------------------------------------

    private static IEnumerable<XElement> ComplexTypes()
    {
        yield return Complex("Events",
            "One capture: its identity on the container, then what made it, the sun, the declaration, the platform "
            + "event and the vehicle events.",
            Sequence(
                Element("_producer", "Producer", "What made the sidecar. Absent from a sidecar written before sidecars "
                                                 + "carried it.", optional: true),
                Element("_solar", "Solar", "The sun the world reported on the tick nearest the pixels. Absent where "
                                           + "the world reported no sun.", optional: true),
                Element("_illumination", "Illumination",
                        "What the run declared the sun to be for this frame. Written only in a run that declares its "
                        + "illumination.", optional: true),
                Element("event", "Event",
                        "A Cursor-on-Target event: first the camera platform's, where the recorder was given a "
                        + "platform, then one per vehicle.", optional: true, many: true)),
            Attribute("format_version", "FormatVersion", false,
                      $"The sidecar's format version, {CotWriter.FormatVersion}. Absent from a sidecar written before "
                      + "sidecars carried one, which is version 1.", fixedValue: CotWriter.FormatVersion.ToString(CultureInfo.InvariantCulture)),
            Attribute("captured", "Instant", true, "When the still was captured, by the wall clock, in UTC."),
            Attribute("count", "xs:nonNegativeInteger", true, "How many vehicle events follow. The platform event is not counted."),
            Attribute("source", "xs:string", true, "Always truth: the records are the simulator's ground truth.", fixedValue: "truth"),
            Attribute("tick", "xs:nonNegativeInteger", false,
                      "The simulation frame the image was rendered on; every record below is that frame's truth. "
                      + "Written by every recorder."),
            Attribute("sim_time_s", "xs:decimal", false, "Simulated time at that frame.", Seconds),
            Attribute("run_id", "xs:string", false, "The run the capture belongs to. A recorder always names one."),
            Attribute("scenario_id", "xs:string", false, "The scenario driving the run, where one is."),
            Attribute("seed", "xs:long", false, "The seed the run was started with, where one was given."),
            Attribute("vehicles", "VehiclesListed", false,
                      "Which vehicles the events are. Absent, they are every vehicle actor the world held."),
            Attribute("draw_distance_m", "NonNegativeDecimal", false,
                      "The draw distance the image was rendered under. Absent, the image drew vehicles at any range.",
                      Meters),
            Attribute("supervision", "Unknown", false,
                      "unknown: a supervision plan was in force and this frame's supervision could not be read, so no "
                      + "vehicle carries any. Absent where it was read or no plan was in force."),
            Attribute("plan_id", "xs:string", false, "The supervision plan in force on the frame, where one was."),
            Attribute("vocabulary", "xs:positiveInteger", false,
                      "The annotation vocabulary's core version the plan was compiled against, beside plan_id."),
            Attribute("vocabulary_digest", "Sha256", false,
                      "The digest of the plan's vocabulary, which pins what every label means, beside plan_id."),
            Attribute("lights", "Unknown", false,
                      "unknown: a vehicle in the picture carries no lights because the frame's snapshot did not carry "
                      + "them. Absent where every vehicle in the picture carries them."),
            Attribute("pose_source", "Unknown", false,
                      "unknown: a SUMO vehicle in the picture carries no pose_source because the frame's snapshot did not "
                      + "carry one. Absent where every SUMO vehicle in the picture carries it."));

        yield return Complex("Producer",
            "What made the sidecar: the tool and its release, the carlanet release, the SUMO release where SUMO ran, "
            + "when the file was written, and the CARLA server's build identity.",
            Sequence(Element("_server", "Server", "The CARLA server's build identity.", optional: true)),
            Attribute("tool", "xs:string", true, "The component that wrote the file, such as carlacontrol.CaptureSession."),
            Attribute("tool_version", "xs:string", false, "The release version of the tool's package, where it said."),
            Attribute("carlanet", "xs:string", true, "The carlanet release that wrote the file."),
            Attribute("sumo", "xs:string", false, "The SUMO release, where SUMO ran."),
            Attribute("written_utc", "Instant", false, "When the file was written, in UTC."));

        string unknown = ServerBuildIdentity.Unknown;
        yield return Complex("Server",
            "What the CARLA server says it was built from. A server that answered carries build, configuration and "
            + $"the three commits, each {unknown} where it cannot know it; one built before the call carries reason.",
            null,
            Attribute("available", "TrueOrFalse", true, "Whether the server answered the build identity call."),
            Attribute("release", "xs:string", true, "The CARLA release the server was compiled with."),
            Attribute("world_interface", "xs:string", true, "The world interface version it declares, major.minor."),
            Attribute("build", "xs:string", false, "package or editor. Where available is true."),
            Attribute("configuration", "xs:string", false, "The build configuration. Where available is true."),
            Attribute("carla_commit", "xs:string", false, "The CARLA commit. Where available is true."),
            Attribute("content_commit", "xs:string", false, "The content commit. Where available is true."),
            Attribute("engine_commit", "xs:string", false, "The Unreal Engine commit. Where available is true."),
            Attribute("commits_from", "xs:string", false,
                      "version_file, compiled or none: where the commits came from. Where available is true."),
            Attribute("reason", "xs:string", false, "Why the identity is not available. Where available is false."));

        yield return Complex("Solar",
            "The sun the world reported, and its illumination band, derived from that sun alone.",
            null,
            Attribute("solar_time", "xs:decimal", true, "The sun's clock in its own time zone, 0 up to 24.", "hours"),
            Attribute("date", "CalendarDate", true, "The sun's calendar date."),
            Attribute("time_zone", "xs:decimal", true, "The time zone the sun's clock is read in, from UTC.", "hours"),
            Attribute("lat", "Latitude", true, "The latitude the sun is computed for: the world's georeference origin.", Degrees),
            Attribute("lon", "Longitude", true, "The longitude the sun is computed for.", Degrees),
            Attribute("sun_elevation_deg", "xs:decimal", true, "The sun's geometric elevation, with no atmosphere.", Degrees),
            Attribute("sun_corrected_elevation_deg", "xs:decimal", false,
                      "The sun's elevation with refraction applied: the elevation the frame was lit at. Where the "
                      + "server reports it.", Degrees),
            Attribute("sun_azimuth_deg", "xs:decimal", true, "The sun's azimuth, clockwise from true north.", Degrees),
            Attribute("advancing", "TrueOrFalse", true,
                      "Whether the engine itself moved the sun's clock. false in a SUMO drive, which writes the sun itself."),
            Attribute("rate", "xs:decimal", true, "Sun-clock seconds per simulated second while the engine advances it.",
                      "seconds per second"),
            Attribute("illumination_band", "IlluminationBand", false,
                      "The band of this sun. Absent where the elevation is not a sun's."),
            Attribute("illumination_band_elevation", "ElevationKind", false,
                      "Which elevation the band was cut from: refraction_corrected wherever the block carries it. "
                      + "Written with illumination_band."));

        yield return Complex("Illumination",
            "What the run declared the sun to be for this frame, and the audit's residual against the world's sun.",
            null,
            Attribute("policy", "IlluminationPolicy", true, "The illumination policy the run followed."),
            Attribute("epoch_honoured", "TrueOrFalse", true,
                      "Whether the frame was lit by the sun of its own declared civil instant."),
            Attribute("audited", "TrueOrFalse", true,
                      "Whether the world's sun was compared against the declaration on this frame's tick."),
            Attribute("rate", "xs:decimal", false, "Sun-clock seconds per simulated second, under advance only.",
                      "seconds per second"),
            Attribute("freeze_at_civil_time", "ClockTime", false, "The time of day the sun is held at, under freeze_at only."),
            Attribute("epoch_digest", "Sha256", false, "SHA-256 of the scenario's epoch."),
            Attribute("epoch_civil", "CivilInstant", false, "The civil instant simulated second zero stands for."),
            Attribute("utc_offset_hours", "xs:decimal", false, "The epoch's declared UTC offset.", "hours"),
            Attribute("declared_civil", "CivilInstant", false, "This frame's simulated instant as a civil time."),
            Attribute("declared_utc", "UtcInstant", false, "The same instant in UTC."),
            Attribute("sun_declared", "CivilInstant", false,
                      "The date and clock the sun was declared to hold for this frame: the frame's own civil instant "
                      + "under a policy that honors the epoch, the window's opening instant under a freeze."),
            Attribute("sun_elevation_declared_deg", "xs:decimal", false, "The declared sun's geometric elevation.", Degrees),
            Attribute("sun_corrected_elevation_declared_deg", "xs:decimal", false,
                      "The declared sun's refraction-corrected elevation.", Degrees),
            Attribute("declared_elevation", "ElevationKind", false,
                      "Which of the two elevations a declared window elevation means."),
            Attribute("residual_clock_s", "xs:decimal", false, "The world's sun clock minus the declared one.", Seconds),
            Attribute("residual_deg", "xs:decimal", false, "The angle between the world's sun and the declared sun.", Degrees),
            Attribute("residual_corrected_deg", "xs:decimal", false,
                      "The world's corrected elevation minus the declared one, where the world reports it.", Degrees));

        yield return Complex("Event",
            "A Cursor-on-Target event. The camera platform's event has uid CARLA-SENSOR-<id> or the camera's own uid, "
            + "an air-track type and a detail of contact, track, sensor and intrinsics; a vehicle's event has uid "
            + "CARLA-TRUTH-SUMO-<sumo_id> where a SUMO drive lent its body, CARLA-TRUTH-<actor_id> otherwise, type "
            + "a-<affiliation>-G-E-V, and a detail of track, contact and _carla.",
            Sequence(Element("point", "Point", "Where the camera or the vehicle was."),
                     Element("detail", "Detail", "What the event says besides where.")),
            Attribute("version", "xs:string", true, "The Cursor-on-Target version, 2.0.", fixedValue: "2.0"),
            Attribute("uid", "xs:string", true,
                      "The track's identifier: the same vehicle has the same uid in every capture and in the world "
                      + "truth track."),
            Attribute("type", "CotType", true, "The Cursor-on-Target type."),
            Attribute("how", "xs:string", true, "How the position was obtained: m-g, machine generated.", fixedValue: "m-g"),
            Attribute("time", "Instant", true, "The capture instant, as captured on the container."),
            Attribute("start", "Instant", true, "The same instant."),
            Attribute("stale", "Instant", true, "When the event goes stale: the capture instant plus 3 seconds by default."));

        yield return Complex("Point", "A position.", null,
            Attribute("lat", "Latitude", true, "Latitude, WGS84. For a vehicle, the actor's origin.", Degrees),
            Attribute("lon", "Longitude", true, "Longitude, WGS84.", Degrees),
            Attribute("hae", "xs:decimal", true,
                      "Height above the WGS84 ellipsoid, bare-earth convention: the physical height less the "
                      + "height-align offset under the point.", Meters),
            Attribute("ce", "xs:decimal", true, "Circular error. Always 0.0: truth.", Meters, fixedValue: "0.0"),
            Attribute("le", "xs:decimal", true, "Linear error. Always 0.0: truth.", Meters, fixedValue: "0.0"));

        yield return Complex("Detail",
            "The platform event's detail, or a vehicle event's. They are told apart by their first child: the "
            + "platform's begins with contact, a vehicle's with track.",
            new XElement(Xs + "choice",
                Sequence(
                    Element("contact", "Contact", "The camera's callsign."),
                    Element("track", "Track", "The camera's movement since the previous capture."),
                    Element("sensor", "SensorPointing", "Where the camera pointed and its field of view."),
                    Element("_carla_intrinsics", "Intrinsics", "The camera's pinhole intrinsics."),
                    Element("_carla_exposure", "Exposure",
                            "The exposure the camera was given. Absent for a camera that carries none.", optional: true)),
                Sequence(
                    Element("track", "Track", "The vehicle's course and speed."),
                    Element("contact", "Contact", "The vehicle's callsign: <base_type>-<sumo_id or actor_id>."),
                    Element("_carla", "Carla", "The vehicle's truth record."),
                    Element("_box3d", "Box3d", "The vehicle's box as eight geodetic corners. Only for a vehicle in "
                                               + "the picture.", optional: true, inPictureOnly: true),
                    Element("_supervision", "Supervision",
                            "What the scenario's author asserts of the SUMO vehicle this body drew. Written for every "
                            + "drawn SUMO vehicle where a supervision plan was in force and read.", optional: true))));

        yield return Complex("Contact", "A callsign.", null,
            Attribute("callsign", "xs:string", true, "The track's callsign."));

        yield return Complex("Track", "Course and speed over the ground.", null,
            Attribute("course", "Bearing", true,
                      "Direction of movement, clockwise from true north. Below 0.5 m/s it is the direction the body "
                      + "or the camera points.", Degrees),
            Attribute("speed", "NonNegativeDecimal", true, "Ground speed.", MetersPerSecond));

        yield return Complex("SensorPointing", "Where the camera pointed.", null,
            Attribute("azimuth", "Bearing", true, "Boresight azimuth, clockwise from true north.", Degrees),
            Attribute("elevation", "xs:decimal", true, "Boresight elevation: 0 level, -90 straight down.", Degrees),
            Attribute("roll", "xs:decimal", true, "Roll about the boresight.", Degrees),
            Attribute("fov", "xs:decimal", true, "Horizontal field of view.", Degrees),
            Attribute("vfov", "xs:decimal", true, "Vertical field of view.", Degrees),
            Attribute("range", "xs:decimal", true, "Always 0: no range is declared.", Meters, fixedValue: "0"),
            Attribute("type", "xs:string", true, "Always EO, electro-optical.", fixedValue: "EO"),
            Attribute("model", "xs:string", true, "The camera blueprint, such as sensor.camera.rgb."));

        yield return Complex("Intrinsics",
            "The camera's pinhole intrinsics: square pixels, the principal point at the picture's center.", null,
            Attribute("width", "xs:positiveInteger", true, "The picture's width.", Pixels),
            Attribute("height", "xs:positiveInteger", true, "The picture's height.", Pixels),
            Attribute("fx", "xs:decimal", true, "Focal length across: width / (2 tan(hfov_deg / 2)).", Pixels),
            Attribute("fy", "xs:decimal", true, "Focal length down, equal to fx.", Pixels),
            Attribute("cx", "xs:decimal", true, "Principal point across: width / 2.", Pixels),
            Attribute("cy", "xs:decimal", true, "Principal point down: height / 2.", Pixels),
            Attribute("hfov_deg", "xs:decimal", true, "Horizontal field of view.", Degrees),
            Attribute("vfov_deg", "xs:decimal", true, "Vertical field of view.", Degrees),
            Attribute("model", "xs:string", true, "The projection model: pinhole."),
            Attribute("distortion", "xs:string", true,
                      "none at CARLA's defaults, otherwise CARLA's own lens parameters."),
            Attribute("align_offset_m", "xs:decimal", true,
                      "The height-align offset under the camera: its physical height is the point's hae plus this.",
                      Meters));

        yield return Complex("Exposure",
            "The exposure the camera was given: its post-process profile, and the exposure its attributes set over it.",
            null,
            Attribute("post_process_profile", "xs:string", true, "The post-process profile the camera loaded."),
            Attribute("method", "ExposureMethod", true, "How the exposure was set."),
            Attribute("iso", "xs:decimal", true, "The sensor's sensitivity, ISO."),
            Attribute("shutter_s", "xs:decimal", true, "The shutter time.", Seconds),
            Attribute("fstop", "xs:decimal", true, "The aperture, as an f-number."),
            Attribute("compensation_ev", "xs:decimal", true, "Exposure compensation; above 0 brightens.", "EV"),
            Attribute("ev100", "xs:decimal", false,
                      "The exposure value at ISO 100 the ISO, shutter and aperture make: log2(fstop^2 / shutter_s) - "
                      + "log2(iso / 100). Under manual only.", "EV"));

        yield return Complex("Carla", "One vehicle's truth record.", null,
            Attribute("source", "xs:string", true, "Always truth.", fixedValue: "truth"),
            Attribute("actor_id", "xs:unsignedInt", true,
                      "The CARLA actor that drew the vehicle on this frame. A body drawn for a SUMO drive draws a "
                      + "succession of vehicles, so follow a vehicle by sumo_id."),
            Attribute("type_id", "xs:string", true, "The body's CARLA blueprint, such as vehicle.audi.tt."),
            Attribute("base_type", "xs:string", true,
                      "The vehicle's base type, such as car, van or truck: the vehicle catalogue's for the blueprint, "
                      + "or what the blueprint declares where the catalogue does not list it."),
            Attribute("special_type", "xs:string", true,
                      "The vehicle's kind from the catalogue or the blueprint; empty where it has none."),
            Attribute("length_m", "NonNegativeDecimal", true, "The body's bounding box, front to back.", Meters),
            Attribute("width_m", "NonNegativeDecimal", true, "The body's bounding box, side to side.", Meters),
            Attribute("height_m", "NonNegativeDecimal", true, "The body's bounding box, bottom to top.", Meters),
            Attribute("color", "Color", true, "The body's color attribute."),
            Attribute("role_name", "xs:string", true, "The actor's role name: sumo for a body a SUMO drive lent."),
            Attribute("vx", "xs:decimal", true, "Velocity east, in CARLA's world frame (x east, y south, z up).",
                      MetersPerSecond),
            Attribute("vy", "xs:decimal", true, "Velocity south.", MetersPerSecond),
            Attribute("vz", "xs:decimal", true, "Velocity up.", MetersPerSecond),
            Attribute("heading_deg", "Bearing", false,
                      "The direction the body points, clockwise from true north: its yaw. The track's course is the "
                      + "direction it moves, which differs while it turns or changes lane. Written wherever the body's "
                      + "transform was read.", Degrees),
            Attribute("pitch_deg", "xs:decimal", false, "The body's pitch, positive nose up.", Degrees, inPictureOnly: true),
            Attribute("roll_deg", "xs:decimal", false, "The body's roll, positive right side down.", Degrees,
                      inPictureOnly: true),
            Attribute("in_frame", "InFrame", false,
                      "Where the vehicle's box fell against the picture. Written on every record a recorder wrote."),
            Attribute("occlusion", "Fraction", false,
                      "The share of the vehicle's silhouette hidden from the camera by anything nearer: 0 wholly "
                      + "visible, 1 wholly hidden. Only where occlusion was measured; never beside occlusion_unmeasured."),
            Attribute("occlusion_level", "OcclusionLevel", false, "The occlusion fraction as a band. Only where measured."),
            Attribute("occlusion_samples", "xs:nonNegativeInteger", false,
                      "How many points across the vehicle's outline the fraction was measured over. Few samples mean "
                      + "few possible values. Only where measured."),
            Attribute("apparent_width_px", "xs:nonNegativeInteger", false,
                      "How wide the box appears, its whole projected footprint, any part outside the picture "
                      + "included. Wherever the box has a footprint: not behind the camera.", Pixels),
            Attribute("apparent_height_px", "xs:nonNegativeInteger", false, "How tall the box appears. As apparent_width_px.",
                      Pixels),
            Attribute("box_px", "PixelRectangle", false,
                      "The axis-aligned rectangle the box's eight projected corners span: x min, y min, x max, y max. "
                      + "Not clipped to the picture.", Pixels, inPictureOnly: true),
            Attribute("box_oriented_px", "PixelQuadrilateral", false,
                      "The smallest-area rectangle enclosing the eight projected corners, as four x y corners "
                      + "clockwise from the top-most. Not clipped.", Pixels, inPictureOnly: true),
            Attribute("truncation", "Fraction", false, "The share of box_px's area outside the picture.",
                      inPictureOnly: true),
            Attribute("lights", "Lights", false,
                      "The lights commanded on for the vehicle on this frame, in words, or none. Absent where the "
                      + "frame's snapshot did not carry them, and the container then says lights=\"unknown\".",
                      inPictureOnly: true),
            Attribute("pose_source", "PoseSource", false,
                      "Where the drawn pose came from on this frame. Only for a SUMO vehicle in the picture; absent where "
                      + "the frame's snapshot did not carry it, and the container then says pose_source=\"unknown\".",
                      inPictureOnly: true),
            Attribute("occlusion_unmeasured", "OcclusionUnmeasured", false,
                      "Why there is no occlusion. Only where occlusion was not measured, so an absent fraction is "
                      + "never read as nothing in the way."),
            Attribute("beyond_draw_distance", "DrawDistanceReach", false,
                      "Where the capture's draw distance kept the vehicle out of the image, wholly or partly. Only "
                      + "where the draw distance reached it."),
            Attribute("camera_range_m", "NonNegativeDecimal", false,
                      "Distance from the camera to the center of the vehicle's box. For a vehicle in the picture, and "
                      + "beside beyond_draw_distance.", Meters),
            Attribute("sumo_id", "xs:string", false,
                      "The SUMO vehicle this body drew on this frame. Only where a SUMO drive lent the body; the key that "
                      + "joins the record to the world truth track and the supervision plan."),
            Attribute("vtype_id", "xs:string", false, "The SUMO vehicle type. With sumo_id."),
            Attribute("admitted_tick", "xs:nonNegativeInteger", false,
                      "The frame on which this body began drawing this vehicle. With sumo_id."),
            Attribute("sumo_angle_deg", "xs:decimal", false,
                      "The angle SUMO reported for the vehicle at this frame, clockwise from north, for comparison "
                      + "with heading_deg. With sumo_id, where the recorder knows it.", Degrees));

        yield return Complex("Box3d",
            "The vehicle's box as eight corners, converted to latitude, longitude and bare-earth height as the event's "
            + "point is, in a fixed order: " + string.Join(", ", CaptureBoxes.CornerNames) + ". Corner n + 4 stands "
            + "above corner n; front is the way heading_deg points; left and right are as seen from the driver's seat.",
            Sequence(Element("corner", "Corner", "One corner of the box.", count: CaptureBoxes.CornerNames.Count)),
            Attribute("frame", "xs:string", true, "Always geodetic.", fixedValue: "geodetic"));

        yield return Complex("Corner", "One corner of the box.", null,
            Attribute("lat", "Latitude", true, "Latitude, WGS84.", Degrees),
            Attribute("lon", "Longitude", true, "Longitude, WGS84.", Degrees),
            Attribute("hae", "xs:decimal", true, "Height above the WGS84 ellipsoid, bare-earth convention.", Meters));

        yield return Complex("Supervision",
            "What the scenario's author asserts of the vehicle on this frame: always its state, unlabelled included, "
            + "and one annotation per pattern instance in force for it.",
            Sequence(Element("annotation", "Annotation", "One pattern instance in force for the vehicle.",
                             optional: true, many: true)),
            Attribute("state", "SupervisionState", true, "The vehicle's supervision state."),
            Attribute("vocabulary", "xs:positiveInteger", true, "The annotation vocabulary's core version."),
            Attribute("vocabulary_digest", "Sha256", true, "The digest of the plan's vocabulary."));

        yield return Complex("Annotation", "One pattern instance in force for the vehicle.", null,
            Attribute("instance", "xs:string", true, "The instance, as the plan names it: <scenario_id>/<name>."),
            Attribute("labels", "Terms", false, "The terms the instance is labeled with. Absent where it has none."),
            Attribute("phase", "xs:string", false, "The phase of the instance's interval in force. Absent where none is declared."),
            Attribute("role", "xs:string", false,
                      "The role the vehicle plays in the instance, subject for a one-participant instance. Absent where "
                      + "none is declared."));
    }

    // ---- building blocks ------------------------------------------------------------------------------------------

    /// <summary>The words of one family of the annotation vocabulary's closed core.</summary>
    private static IEnumerable<string> CoreFamily(string family) =>
        CoreVocabulary.Families.Single(entry => entry.Family == family).Terms;

    private static XElement Doc(string text) =>
        new(Xs + "annotation", new XElement(Xs + "documentation", text));

    private static XElement Facet(string facet, string value) =>
        new(Xs + facet, new XAttribute("value", value));

    private static XElement Restriction(string name, string baseType, string documentation, params XElement[] facets) =>
        new(Xs + "simpleType", new XAttribute("name", name), Doc(documentation),
            new XElement(Xs + "restriction", new XAttribute("base", baseType), facets));

    private static XElement Words(string name, string documentation, IEnumerable<string> words) =>
        Restriction(name, "xs:token", documentation, [.. words.Select(word => Facet("enumeration", word))]);

    private static XElement DecimalList(string name, int length, string documentation) =>
        new(Xs + "simpleType", new XAttribute("name", name), Doc(documentation),
            new XElement(Xs + "restriction",
                new XElement(Xs + "simpleType", new XElement(Xs + "list", new XAttribute("itemType", "xs:decimal"))),
                Facet("length", length.ToString(CultureInfo.InvariantCulture))));

    private static XElement Complex(string name, string documentation, XElement? content, params XElement[] attributes) =>
        new(Xs + "complexType", new XAttribute("name", name), Doc(documentation), content, attributes);

    private static XElement Sequence(params XElement[] elements) => new(Xs + "sequence", elements);

    private static XElement Element(string name, string type, string documentation, bool optional = false,
                                    bool many = false, int? count = null, bool inPictureOnly = false)
    {
        var element = new XElement(Xs + "element", new XAttribute("name", name), new XAttribute("type", type));
        if (count is { } exactly)
        {
            element.Add(new XAttribute("minOccurs", exactly), new XAttribute("maxOccurs", exactly));
        }
        else
        {
            if (optional)
            {
                element.Add(new XAttribute("minOccurs", 0));
            }

            if (many)
            {
                element.Add(new XAttribute("maxOccurs", "unbounded"));
            }
        }

        element.Add(Annotated(documentation, null, inPictureOnly));
        return element;
    }

    private static XElement Attribute(string name, string type, bool required, string documentation,
                                      string? unit = null, bool inPictureOnly = false, string? fixedValue = null)
    {
        var attribute = new XElement(Xs + "attribute", new XAttribute("name", name), new XAttribute("type", type),
                                     new XAttribute("use", required ? "required" : "optional"));
        if (fixedValue is not null)
        {
            attribute.Add(new XAttribute("fixed", fixedValue));
        }

        attribute.Add(Annotated(documentation, unit, inPictureOnly));
        return attribute;
    }

    private static XElement Annotated(string documentation, string? unit, bool inPictureOnly)
    {
        var annotation = new XElement(Xs + "annotation", new XElement(Xs + "documentation", documentation));
        if (unit is not null || inPictureOnly)
        {
            var info = new XElement(Xs + "appinfo");
            if (unit is not null)
            {
                info.Add(new XElement(Cap + "unit", unit));
            }

            if (inPictureOnly)
            {
                info.Add(new XElement(Cap + "onlyInPicture", "true"));
            }

            annotation.Add(info);
        }

        return annotation;
    }
}
