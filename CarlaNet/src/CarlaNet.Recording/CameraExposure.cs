using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using CarlaNet.Types.Rpc.Actors;

namespace CarlaNet.Recording;

/// <summary>
/// The exposure a camera was given: the post-process profile it loaded, and the exposure the camera's
/// own attributes set over it -- the method, the ISO, the shutter, the aperture and the compensation.
/// Recorded on every capture of the camera as <c>&lt;_carla_exposure&gt;</c>, beside
/// <c>&lt;_carla_intrinsics&gt;</c>, and in the run manifest's camera entry.
/// </summary>
/// <remarks>
/// <para><b>Read from the camera, as the server spawned it.</b> <see cref="Of"/> reads the camera actor's
/// attributes -- the description the server holds and returns for it, which every client reads alike,
/// as <see cref="CameraName.Of"/> reads the camera's name -- so a recorder in any process writes the
/// same exposure for one camera. A run sends every one of these attributes to the camera, so the record
/// is the exposure the run gave it; nothing reads back what the server loaded.</para>
///
/// <para><b>One rule on both sides.</b> The plugin applies the attributes over the profile it loaded
/// (<c>ApplyCameraExposureAttributes</c> in <c>ActorBlueprintFunctionLibrary.cpp</c>): the attributes set
/// the exposure and the profile the rest of the picture. Upstream CARLA 0.9's names and units: the
/// mode is <c>histogram</c> where it says so, ignoring case, and manual for any other value;
/// <c>shutter_speed</c> is per second, as UE's <c>CameraShutterSpeed</c> is, so 320 is 1/320 s.</para>
///
/// <para><b>EV100 is written under manual alone.</b> It is the engine's own figure for a manual exposure,
/// <c>log2(N² · S · 100 / max(1, ISO))</c> with <c>S</c> the shutter speed per second, which is
/// <c>log2(N²/t) − log2(ISO/100)</c>, the compensation apart. Under histogram the engine meters each
/// frame and sets its own exposure, so no EV100 was given to the camera and none is written; the
/// compensation still applies.</para>
///
/// <para>A camera that does not carry all six attributes -- a camera of a server built before it
/// published its exposure, or one that publishes no post-process pair, such as the depth camera -- has
/// no record, and its captures carry no element.</para>
/// </remarks>
/// <param name="PostProcessProfile">The <c>post_process_profile</c> the camera was given, as given.</param>
/// <param name="Method"><see cref="Manual"/> or <see cref="Histogram"/>.</param>
/// <param name="Iso">The sensor's sensitivity, ISO.</param>
/// <param name="ShutterSpeedPerSecond">The shutter as the camera takes it, per second.</param>
/// <param name="FStop">The aperture, as an f-number.</param>
/// <param name="CompensationEv">The exposure compensation, EV; above 0 brightens.</param>
public sealed record CameraExposure(
    string PostProcessProfile,
    string Method,
    double Iso,
    double ShutterSpeedPerSecond,
    double FStop,
    double CompensationEv)
{
    /// <summary>The element a sidecar's platform event carries the record in.</summary>
    public const string ElementName = "_carla_exposure";

    /// <summary>A fixed exposure, set by the ISO, the shutter and the aperture.</summary>
    public const string Manual = "manual";

    /// <summary>An exposure the engine meters from each frame.</summary>
    public const string Histogram = "histogram";

    /// <summary>The camera attributes the record is read from.</summary>
    public const string ProfileAttribute = "post_process_profile";

    /// <inheritdoc cref="ProfileAttribute"/>
    public const string ModeAttribute = "exposure_mode";

    /// <inheritdoc cref="ProfileAttribute"/>
    public const string IsoAttribute = "iso";

    /// <inheritdoc cref="ProfileAttribute"/>
    public const string ShutterSpeedAttribute = "shutter_speed";

    /// <inheritdoc cref="ProfileAttribute"/>
    public const string FStopAttribute = "fstop";

    /// <inheritdoc cref="ProfileAttribute"/>
    public const string CompensationAttribute = "exposure_compensation";

    /// <summary>The shutter, seconds.</summary>
    public double ShutterSeconds => 1.0 / ShutterSpeedPerSecond;

    /// <summary>The EV100 the ISO, shutter and aperture give a manual exposure; null under histogram.</summary>
    public double? Ev100 => Method == Manual
        ? Math.Log2(FStop * FStop * ShutterSpeedPerSecond * 100.0 / Math.Max(1.0, Iso))
        : null;

    /// <summary>
    /// The exposure <paramref name="camera"/> was given, from its attributes as the server returned it
    /// at spawn or lists it among the world's actors; null where it does not carry all six.
    /// </summary>
    public static CameraExposure? Of(Actor camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ActorAttributeValue attribute in camera.Description.Attributes ?? [])
        {
            attributes[attribute.Id] = attribute.Value;
        }

        return FromAttributes(attributes);
    }

    /// <summary>
    /// The exposure a camera's attributes give it, by attribute id; null where one of the six is missing,
    /// a number does not read as a finite one, or the shutter speed is not above zero, so a record is
    /// never written for an exposure it cannot state.
    /// </summary>
    public static CameraExposure? FromAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (!attributes.TryGetValue(ProfileAttribute, out string? profile)
            || !attributes.TryGetValue(ModeAttribute, out string? mode)
            || !Number(attributes, IsoAttribute, out double iso)
            || !Number(attributes, ShutterSpeedAttribute, out double shutterSpeed)
            || !Number(attributes, FStopAttribute, out double fStop)
            || !Number(attributes, CompensationAttribute, out double compensation)
            || !(shutterSpeed > 0.0))
        {
            return null;
        }

        string method = string.Equals(mode, Histogram, StringComparison.OrdinalIgnoreCase) ? Histogram : Manual;
        return new CameraExposure(profile, method, iso, shutterSpeed, fStop, compensation);
    }

    /// <summary>
    /// Write the record as <c>&lt;_carla_exposure&gt;</c>: <c>post_process_profile</c>, <c>method</c>,
    /// <c>iso</c>, <c>shutter_s</c>, <c>fstop</c>, <c>compensation_ev</c>, and <c>ev100</c> under manual.
    /// </summary>
    public void WriteElement(XmlWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartElement(ElementName);
        w.WriteAttributeString("post_process_profile", PostProcessProfile);
        w.WriteAttributeString("method", Method);
        w.WriteAttributeString("iso", F(Iso, "0.###"));
        w.WriteAttributeString("shutter_s", F(ShutterSeconds, "0.#########"));
        w.WriteAttributeString("fstop", F(FStop, "0.###"));
        w.WriteAttributeString("compensation_ev", F(CompensationEv, "0.###"));
        if (Ev100 is { } ev100)
        {
            w.WriteAttributeString("ev100", F(ev100, "0.###"));
        }

        w.WriteEndElement();
    }

    /// <summary>
    /// Write the record's fields as properties of the JSON object open on <paramref name="json"/>, under
    /// the element's names, with <c>ev100</c> null under histogram.
    /// </summary>
    public void WriteJsonProperties(Utf8JsonWriter json)
    {
        ArgumentNullException.ThrowIfNull(json);
        json.WriteString("post_process_profile", PostProcessProfile);
        json.WriteString("method", Method);
        json.WriteNumber("iso", Iso);
        json.WriteNumber("shutter_s", ShutterSeconds);
        json.WriteNumber("fstop", FStop);
        json.WriteNumber("compensation_ev", CompensationEv);
        if (Ev100 is { } ev100)
        {
            json.WriteNumber("ev100", ev100);
        }
        else
        {
            json.WriteNull("ev100");
        }
    }

    /// <summary>The record as one JSON object, as the run manifest's camera entry carries it.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            WriteJsonProperties(json);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool Number(IReadOnlyDictionary<string, string> attributes, string id, out double value)
    {
        value = double.NaN;
        return attributes.TryGetValue(id, out string? text)
               && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    // Negative zero is written as zero, as everywhere else in a sidecar.
    private static string F(double value, string format) =>
        (value == 0.0 ? 0.0 : value).ToString(format, CultureInfo.InvariantCulture);
}
