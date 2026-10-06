using System.Globalization;
using System.Text;
using CarlaNet.Types.Rpc.Actors;

namespace CarlaNet.Recording;

/// <summary>
/// The name a camera's recordings are known by. Every still a recorder writes is named after its
/// camera (<see cref="StillStem"/>), and the name is the callsign of the camera's platform track, so
/// two cameras in one world never write files of the same name or report under the same callsign.
/// </summary>
/// <remarks>
/// <para><b>The server issues the names and refuses the duplicates.</b> A camera's name is its
/// <c>role_name</c> on the server (<see cref="RoleNameAttribute"/>), settled when the camera is
/// spawned. A client may name a camera as it likes, within the rule below; a camera spawned with no
/// name is named <c>Camera_&lt;n&gt;</c> by the server (<see cref="ServerPrefix"/>) from a counter
/// held for the server's lifetime, so no number is issued twice while the server runs, whatever
/// world is loaded. The server refuses a client-given name a live camera holds, and one of its own
/// form, which only it may issue. Every client reads the name back from the spawned camera's
/// attributes (<see cref="Of"/>) and uses it everywhere the name is used. The platform track's uid
/// is <c>CARLA-SENSOR-&lt;actor id&gt;</c> (<see cref="Default"/>), whatever the camera is named;
/// the same form is a camera's name only on a server built before it named cameras, which hands an
/// unnamed camera back with its blueprint's role name (<see cref="NamedByServer"/>).</para>
///
/// <para><b>A name is short and plain.</b> It is 1 to <see cref="MaxLength"/> characters, each an ASCII
/// letter, digit, underscore or hyphen -- <c>Overwatch_1</c>, <c>Southeast_1700m_orbit</c>,
/// <c>NapOfEarth_2</c> -- and nothing else: no space, no dot, no other punctuation. Such a name is the
/// same file name on Windows and on Linux, the same text in the image's PNG chunks and the same CoT
/// callsign, so a chosen name is used as given or refused, never rewritten. <see cref="Problem"/>
/// refuses, and says what is allowed, a name with any other character, and four names the characters
/// allow:</para>
/// <list type="bullet">
/// <item>a name Windows keeps for a device -- CON, PRN, AUX, NUL, COM0 to COM9, LPT0 to LPT9 -- in
/// any case, because a channel's directory is named by its camera;</item>
/// <item>a role name the server gives sensors -- front, back, left, right, front_left, front_right,
/// back_left, back_right -- in any case, because a camera spawned without a name carries the first
/// until the server names it, and a camera is told from an unnamed one by it;</item>
/// <item>the server's form, <c>Camera_&lt;digits&gt;</c>, in any case, because the server issues
/// those and a client cannot claim one; and</item>
/// <item>the default form, <c>CARLA-SENSOR-&lt;digits&gt;</c>, for any camera but the one it is the
/// default of, because it would be that other camera's uid.</item>
/// </list>
///
/// <para><b>Case does not tell two names apart</b> (<see cref="Same"/>): a Windows file system does not,
/// so "Deck" and "deck" recording into one directory would write the same files.</para>
///
/// <para><b>Unique in a process too.</b> A recorder holds its camera's name for its life
/// (<see cref="Hold"/>), and no other recorder in the same process can take it.</para>
/// </remarks>
public static class CameraName
{
    /// <summary>The longest name a camera may have, in characters.</summary>
    public const int MaxLength = 63;

    /// <summary>What every platform track's uid begins with, and every unnamed camera's name on a
    /// server built before it named cameras.</summary>
    public const string DefaultPrefix = "CARLA-SENSOR-";

    /// <summary>What every name the server issues begins with: an unnamed camera is
    /// <c>Camera_&lt;n&gt;</c>.</summary>
    public const string ServerPrefix = "Camera_";

    /// <summary>The blueprint attribute a camera's name is carried in, settled by the server at spawn
    /// and readable by every client from the world's actors.</summary>
    public const string RoleNameAttribute = "role_name";

    /// <summary>The capture time in a still's file name: local wall-clock time to the millisecond.</summary>
    public const string StillTimeFormat = "yyyy.MM.dd_HH.mm.ss.fff";

    /// <summary>What a camera name may be, as every refusal says it.</summary>
    public const string Allowed =
        "a camera name is 1 to 63 characters, each an ASCII letter, digit, underscore or hyphen, such "
        + "as Overwatch_1 or Southeast_1700m_orbit";

    private static readonly HashSet<string> WindowsDevices = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(0, 10).Select(n => "COM" + n))
            .Concat(Enumerable.Range(0, 10).Select(n => "LPT" + n)),
        StringComparer.OrdinalIgnoreCase);

    // The role names the server offers every sensor blueprint, the first of them its default: what an
    // unnamed camera carries until the server names it, and so what one from an older server carries.
    private static readonly HashSet<string> SensorRoleNames = new(
        ["front", "back", "left", "right", "front_left", "front_right", "back_left", "back_right"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Holding> Held = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object HeldLock = new();

    /// <summary>The uid of a camera's platform track, and the name of a camera a server built before it
    /// named cameras left unnamed: <c>CARLA-SENSOR-&lt;actor id&gt;</c>.</summary>
    public static string Default(ActorId camera) => DefaultPrefix + camera.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="name"/> has the default form, <c>CARLA-SENSOR-&lt;digits&gt;</c>,
    /// in any case.</summary>
    public static bool IsDefaultForm(string? name) => HasDigitsAfter(name, DefaultPrefix);

    /// <summary>Whether <paramref name="name"/> has the form the server gives every camera it names,
    /// <c>Camera_&lt;digits&gt;</c>, in any case.</summary>
    public static bool IsServerIssued(string? name) => HasDigitsAfter(name, ServerPrefix);

    /// <summary>Whether two names are the same name: compared without regard to case, as a Windows file
    /// system compares file names.</summary>
    public static bool Same(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why <paramref name="name"/> cannot be a name a client gives a camera, or null when it can. Every
    /// refusal says what is allowed (<see cref="Allowed"/>). The server's own form is refused: for the
    /// name a camera already holds, the server's included, see <see cref="HeldProblem"/>.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="camera">The camera it is to name, where known: a name of the default form is
    /// accepted only as this camera's own default.</param>
    public static string? Problem(string? name, ActorId? camera = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "a camera name cannot be empty: " + Allowed;
        }

        foreach (char c in name)
        {
            if (!IsAllowed(c))
            {
                return $"camera name '{Shown(name)}' holds {Described(c)}, which a camera name cannot: "
                       + Allowed;
            }
        }

        if (name.Length > MaxLength)
        {
            return $"camera name '{name}' is {name.Length} characters long: " + Allowed;
        }

        if (WindowsDevices.Contains(name))
        {
            return $"camera name '{name}' is a name Windows keeps for a device, and a channel's directory "
                   + "is named by its camera; choose another, such as Overwatch_1";
        }

        if (SensorRoleNames.Contains(name))
        {
            return $"camera name '{name}' is a role name the server gives sensors, which every camera "
                   + "spawned without a name carries until the server names it, so it is what tells an "
                   + "unnamed camera apart; choose another, such as Overwatch_1";
        }

        if (IsServerIssued(name))
        {
            return $"camera name '{name}' has the form the server gives every camera spawned without one, "
                   + $"{ServerPrefix}<n>, which a client cannot claim; choose another, such as Overwatch_1";
        }

        if (IsDefaultForm(name) && !(camera is { } own && Same(name, Default(own))))
        {
            return $"camera name '{name}' has the form of every camera's platform track uid, "
                   + $"{DefaultPrefix}<actor id>, and "
                   + (camera is { } other ? $"is not camera {other}'s own" : "names no camera of its own")
                   + ": it would be another camera's name; choose another, such as Overwatch_1";
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be the name a camera holds -- the one the server issued it or
    /// accepted from its client -- or null when it can: a name of the server's form is accepted, and
    /// any other is held to <see cref="Problem"/>.
    /// </summary>
    public static string? HeldProblem(string? name, ActorId? camera = null) =>
        IsServerIssued(name) ? null : Problem(name, camera);

    /// <summary>The <c>role_name</c> a spawned camera carries, or null where it carries none.</summary>
    public static string? RoleNameOf(Actor camera)
    {
        foreach (ActorAttributeValue attribute in camera.Description.Attributes ?? [])
        {
            if (attribute.Id == RoleNameAttribute)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>Whether the server named <paramref name="camera"/>: it carries a name of the server's
    /// form. A camera spawned without a name on a server built before it named cameras does not: it
    /// carries its blueprint's role name, and its name is its default (<see cref="Of"/>).</summary>
    public static bool NamedByServer(Actor camera) => IsServerIssued(RoleNameOf(camera));

    /// <summary>
    /// The name <paramref name="camera"/> holds, as the server returned it at spawn or lists it among
    /// the world's actors: its <c>role_name</c> where that is a name -- the server's
    /// <c>Camera_&lt;n&gt;</c> or the one its client gave -- and otherwise, from a server built before
    /// it named cameras, which hands an unnamed camera back with its blueprint's role name, its default,
    /// <c>CARLA-SENSOR-&lt;actor id&gt;</c>.
    /// </summary>
    public static string Of(Actor camera)
    {
        string? held = RoleNameOf(camera);
        return held is not null && HeldProblem(held, camera.Id) is null ? held : Default(camera.Id);
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

    private static bool HasDigitsAfter(string? name, string prefix) =>
        name is not null
        && name.Length > prefix.Length
        && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && name.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsAllowed(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';

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

    private static string Described(char c) => c switch
    {
        ' ' => "a space",
        < ' ' or '\u007F' => $"the control character U+{(int)c:X4}",
        > '~' => $"'{c}' (U+{(int)c:X4}), which is not ASCII",
        _ => $"'{c}'",
    };

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
