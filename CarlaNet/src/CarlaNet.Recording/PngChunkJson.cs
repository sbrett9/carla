using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CarlaNet.Recording;

/// <summary>
/// Writes the compact JSON object a <c>carla:</c> PNG text chunk carries, with a JSON writer, so every string
/// in it is escaped as JSON requires: a quote, a backslash and every control character.
/// </summary>
/// <remarks>
/// <para>Each chunk gives its numbers a fixed form of its own (<c>0.######</c>, <c>0.00</c> and so on),
/// which a reader of the chunk relies on and which is not the writer's shortest round-trip form, so a
/// number is formatted by the chunk and written as it stands (<see cref="Number"/>).</para>
///
/// <para>The escaping is the record of what made a file's own (<c>ProducerRecord</c>): the minimum JSON
/// requires, so a time's offset, <c>+05:30</c>, and a version's local part, <c>0.10.0+g1a2b3c4d5</c>, read as
/// written. A character beyond ASCII is written as itself; the PNG encoder writes the chunk in Latin-1.</para>
/// </remarks>
internal static class PngChunkJson
{
    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The object <paramref name="writeMembers"/> writes the members of, as compact JSON.</summary>
    public static string Object(Action<Utf8JsonWriter> writeMembers)
    {
        ArgumentNullException.ThrowIfNull(writeMembers);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Options))
        {
            json.WriteStartObject();
            writeMembers(json);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// A number member in the form the chunk formatted it in. The text is the chunk's own formatting of a
    /// number, written as it stands.
    /// </summary>
    public static void Number(Utf8JsonWriter json, string name, string formatted)
    {
        json.WritePropertyName(name);
        json.WriteRawValue(formatted, skipInputValidation: true);
    }
}
