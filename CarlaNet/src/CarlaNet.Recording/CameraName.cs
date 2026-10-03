using System.Globalization;
using System.Text;
using CarlaNet.Transport;
using CarlaNet.Types.Rpc.Actors;

namespace CarlaNet.Recording;

/// <summary>
/// The name a camera's recordings are known by. Every still a recorder writes is named after its
/// camera (<see cref="StillStem"/>), and the name is the callsign of the camera's platform track, so
/// two cameras in one world never write files of the same name or report under the same callsign.
/// </summary>
/// <remarks>
/// <para><b>Every camera has one.</b> A client may name a camera as it likes, within the rule below; a
/// camera it does not name is <c>CARLA-SENSOR-&lt;actor id&gt;</c> (<see cref="Default"/>), which no
/// other camera on the server can hold, because the server never gives two actors one id. The platform
/// track's uid takes the same form, whatever the camera is named.</para>
///
/// <para><b>A chosen name is used as given or refused, never rewritten.</b> The name is a file name on
/// Windows and on Linux, text in the image's PNG chunks and a CoT callsign, and a name altered on the
/// way to any of them would no longer be the name the client chose. So <see cref="Problem"/> refuses,
/// and says why, a name that would not reach all of them unchanged:</para>
/// <list type="bullet">
/// <item>anything but 1 to <see cref="MaxLength"/> printable ASCII characters -- the PNG text chunk
/// holds Latin-1 only and the encoder writes '?' for whatever it cannot hold;</item>
/// <item>any of <c>&lt; &gt; : " / \ | ?</c> and <c>*</c>, which Windows refuses in a file name (Linux
/// refuses '/' as well);</item>
/// <item>a space at either end, or a dot at the end, which Windows drops from a file name, so the file
/// would not carry the name given (this takes in <c>.</c> and <c>..</c>);</item>
/// <item>a name Windows keeps for a device -- CON, PRN, AUX, NUL, COM0 to COM9, LPT0 to LPT9 -- alone
/// or before a dot, in any case: a channel's directory is named by its camera; and</item>
/// <item>the default form, <c>CARLA-SENSOR-&lt;digits&gt;</c>, for any camera but the one it is the
/// default of, because it would be that other camera's name.</item>
/// </list>
///
/// <para><b>Case does not tell two names apart</b> (<see cref="Same"/>): a Windows file system does not,
/// so "Deck" and "deck" recording into one directory would write the same files.</para>
///
/// <para><b>Unique in a process, and visible in the world.</b> A recorder holds its camera's name for
/// its life (<see cref="Hold"/>), and no other recorder in the same process can take it. A client that
/// names a camera when it spawns it sets the name as the camera's <c>role_name</c>
/// (<see cref="RoleNameAttribute"/>), which every client reads from the world's actors, so a name a
/// camera of another client holds is found (<see cref="HolderAmong"/>) and refused before it is used.
/// A camera another client spawned without a name holds its default.</para>
/// </remarks>
public static class CameraName
{
    /// <summary>The longest name a camera may have, in characters.</summary>
    public const int MaxLength = 63;

    /// <summary>What every unnamed camera's name, and every platform track's uid, begins with.</summary>
    public const string DefaultPrefix = "CARLA-SENSOR-";

    /// <summary>The blueprint attribute a camera's chosen name is spawned under, readable by every
    /// client from the world's actors.</summary>
    public const string RoleNameAttribute = "role_name";

    /// <summary>The capture time in a still's file name: local wall-clock time to the millisecond.</summary>
    public const string StillTimeFormat = "yyyy.MM.dd_HH.mm.ss.fff";

    // Refused by Windows in a file name; '/' by Linux too.
    private const string NotInFileNames = "<>:\"/\\|?*";

    private static readonly HashSet<string> WindowsDevices = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(0, 10).Select(n => "COM" + n))
            .Concat(Enumerable.Range(0, 10).Select(n => "LPT" + n)),
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Holding> Held = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object HeldLock = new();

    /// <summary>The name of a camera its client did not name: <c>CARLA-SENSOR-&lt;actor id&gt;</c>.</summary>
    public static string Default(ActorId camera) => DefaultPrefix + camera.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="name"/> has the default form, <c>CARLA-SENSOR-&lt;digits&gt;</c>,
    /// in any case.</summary>
    public static bool IsDefaultForm(string? name) =>
        name is not null
        && name.Length > DefaultPrefix.Length
        && name.StartsWith(DefaultPrefix, StringComparison.OrdinalIgnoreCase)
        && name.AsSpan(DefaultPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>Whether two names are the same name: compared without regard to case, as a Windows file
    /// system compares file names.</summary>
    public static bool Same(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why <paramref name="name"/> cannot be a camera's name, or null when it can.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="camera">The camera it is to name, where known: a name of the default form is
    /// accepted only as this camera's own default.</param>
    public static string? Problem(string? name, ActorId? camera = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "a camera name cannot be empty";
        }

        foreach (char c in name)
        {
            if (c < ' ' || c > '~')
            {
                return $"camera name '{Shown(name)}' holds {Described(c)}: a camera name is printable ASCII, "
                       + "because it is written into the image's PNG text, which holds Latin-1 only, and into "
                       + "a CoT callsign";
            }

            if (NotInFileNames.Contains(c))
            {
                return $"camera name '{name}' holds '{c}', which a Windows file name cannot hold (none of "
                       + "< > : \" / \\ | ? *): every still's file name begins with the camera's name";
            }
        }

        if (name.Length > MaxLength)
        {
            return $"camera name '{name}' is {name.Length} characters long; a camera name is at most "
                   + $"{MaxLength}";
        }

        if (name[0] == ' ' || name[^1] == ' ' || name[^1] == '.')
        {
            return $"camera name '{name}' begins or ends with a space, or ends with a dot, which Windows "
                   + "drops from a file name, so the files would not carry the name given";
        }

        string device = name.Split('.')[0].TrimEnd(' ');
        if (WindowsDevices.Contains(device))
        {
            return $"camera name '{name}' is {device.ToUpperInvariant()}, a name Windows keeps for a "
                   + "device, and a channel's directory is named by its camera";
        }

        if (IsDefaultForm(name) && !(camera is { } own && Same(name, Default(own))))
        {
            return $"camera name '{name}' has the form every unnamed camera's name takes, "
                   + $"{DefaultPrefix}<actor id>, and "
                   + (camera is { } other ? $"is not camera {other}'s own" : "names no camera of its own")
                   + ": it would be another camera's name";
        }

        return null;
    }

    /// <summary>
    /// The stem of a still's files: <c>&lt;name&gt;_&lt;local capture time&gt;</c>, the time to the
    /// millisecond (<see cref="StillTimeFormat"/>). The image is the stem with <c>.png</c>, its truth
    /// sidecar the stem with <c>.xml</c>. Stills written before cameras were named carry
    /// <c>SCTMV</c> where the name is.
    /// </summary>
    public static string StillStem(string name, DateTime capturedUtc) =>
        name + "_" + capturedUtc.ToLocalTime().ToString(StillTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Holds <paramref name="name"/> for one recorder until the returned handle is disposed, so that no
    /// other recorder in this process records under it meanwhile.
    /// </summary>
    /// <param name="name">The camera's name.</param>
    /// <param name="camera">The camera recorded, where known; said in the refusal of another.</param>
    /// <param name="directory">Where the recorder writes; said in the refusal of another.</param>
    /// <exception cref="InvalidOperationException">Another recorder in this process holds the name.</exception>
    public static IDisposable Hold(string name, ActorId? camera, string directory)
    {
        lock (HeldLock)
        {
            if (Held.TryGetValue(name, out Holding? holder))
            {
                throw new InvalidOperationException(
                    $"camera name '{name}' is already being recorded in this process, {holder.Described()}: "
                    + "two cameras in one process cannot share a name, and a camera is recorded by one "
                    + "recorder at a time");
            }

            var holding = new Holding(name, camera, directory);
            Held.Add(name, holding);
            return holding;
        }
    }

    /// <summary>
    /// The camera among <paramref name="actors"/> that holds <paramref name="name"/>, or null: a sensor
    /// spawned under the name as its <c>role_name</c>, or one whose default it is.
    /// </summary>
    /// <param name="actors">The world's actors, as any client reads them.</param>
    /// <param name="name">The name sought.</param>
    /// <param name="except">A camera that may hold it: the one the name is for.</param>
    public static Actor? HolderAmong(IEnumerable<Actor> actors, string name, ActorId? except = null)
    {
        foreach (Actor actor in actors)
        {
            if (actor.Id == except || actor.Description.Id?.StartsWith("sensor.", StringComparison.Ordinal) != true)
            {
                continue;
            }

            if (Same(Default(actor.Id), name))
            {
                return actor;
            }

            foreach (ActorAttributeValue attribute in actor.Description.Attributes ?? [])
            {
                if (attribute.Id == RoleNameAttribute && Same(attribute.Value, name))
                {
                    return actor;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The camera in the world <paramref name="client"/> observes that holds <paramref name="name"/>, or
    /// null (<see cref="HolderAmong"/>). Asks the server for the description of every actor the client's
    /// world snapshot holds, so it is for a name about to be taken, not for a tick loop.
    /// </summary>
    public static async Task<Actor?> HolderInWorldAsync(CarlaClient client, string name, ActorId? except = null)
    {
        IReadOnlyList<ActorId> ids = client.GetCachedActorIds();
        if (ids.Count == 0)
        {
            return null;
        }

        IReadOnlyList<Actor> actors = await client.GetActorsByIdAsync(ids).ConfigureAwait(false);
        return HolderAmong(actors, name, except);
    }

    /// <summary>How a refusal names the camera that holds a name.</summary>
    public static string DescribeHolder(Actor holder) =>
        $"camera {holder.Id.ToString(CultureInfo.InvariantCulture)} ({holder.Description.Id})";

    private static string Shown(string name)
    {
        var shown = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c < ' ' || c > '~')
            {
                shown.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
            else
            {
                shown.Append(c);
            }
        }

        return shown.ToString();
    }

    private static string Described(char c) =>
        c < ' ' || c == '\u007F'
            ? $"the control character U+{(int)c:X4}"
            : $"'{c}' (U+{(int)c:X4})";

    /// <summary>A name held by one recorder, given back when the recorder is disposed.</summary>
    private sealed class Holding(string name, ActorId? camera, string directory) : IDisposable
    {
        public string Described() =>
            camera is { } id
                ? $"by camera {id.ToString(CultureInfo.InvariantCulture)} into {directory}"
                : $"into {directory}";

        public void Dispose()
        {
            lock (HeldLock)
            {
                if (Held.TryGetValue(name, out Holding? holder) && ReferenceEquals(holder, this))
                {
                    Held.Remove(name);
                }
            }
        }
    }
}
