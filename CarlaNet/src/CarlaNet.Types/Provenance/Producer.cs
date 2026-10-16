using System.Reflection;

namespace CarlaNet.Types.Provenance;

/// <summary>
/// Who is writing, for every <see cref="ProducerRecord"/> this process makes: the tool it runs as, and
/// the carlanet release.
/// </summary>
/// <remarks>
/// <para>The tool is declared once, by the component a program runs as -- <c>carlacontrol</c>'s capture
/// session declares <c>carlacontrol.CaptureSession</c> and its release -- and every writer in the
/// process reads it, so a still written by a recorder the session started names the session. Until one
/// is declared, the carlanet shim's default stands: the program's own name and no version
/// (<see cref="DeclareDefaultTool"/>); with neither, <c>unknown</c>.</para>
///
/// <para>The carlanet release is the CarlaNet assemblies' informational version, which their build
/// sets from the distribution's one release number (<c>CarlaNet/Directory.Build.props</c>), so it is
/// never a guess: <c>0.10.0</c> for the tagged release, <c>0.10.0+g1a2b3c4d5</c> for any other commit.</para>
/// </remarks>
public static class Producer
{
    private static readonly object Gate = new();
    private static (string Tool, string? Version)? _declared;
    private static (string Tool, string? Version)? _default;

    /// <summary>The carlanet release: the CarlaNet assemblies' informational version.</summary>
    public static string CarlaNetVersion { get; } =
        typeof(Producer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        is { Length: > 0 } version
            ? version
            : ServerBuildIdentity.Unknown;

    /// <summary>The tool every record this process makes names, and its release version.</summary>
    public static (string Tool, string? Version) Tool
    {
        get
        {
            lock (Gate)
            {
                return _declared ?? _default ?? (ServerBuildIdentity.Unknown, null);
            }
        }
    }

    /// <summary>Declare the tool this process runs as, and the release version of the package it comes from.</summary>
    public static void DeclareTool(string tool, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        lock (Gate)
        {
            _declared = (tool, string.IsNullOrWhiteSpace(version) ? null : version);
        }
    }

    /// <summary>
    /// The tool to name until one is declared: the carlanet shim gives the program's name. Never
    /// replaces a declared tool.
    /// </summary>
    public static void DeclareDefaultTool(string tool, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        lock (Gate)
        {
            _default = (tool, string.IsNullOrWhiteSpace(version) ? null : version);
        }
    }

    /// <summary>Forget the declared tool, so the default stands again. For tests.</summary>
    public static void ForgetDeclaredTool()
    {
        lock (Gate)
        {
            _declared = null;
        }
    }

    /// <summary>
    /// A record of what is writing a file now: the declared tool, the carlanet release, and the server
    /// and the SUMO release where the caller says one was used, stamped to the millisecond.
    /// </summary>
    public static ProducerRecord Now(ServerBuildIdentity? server = null, string? sumo = null)
    {
        (string tool, string? version) = Tool;
        DateTime now = DateTime.UtcNow;
        // To the millisecond the record is written at, so a record read back is the record written.
        now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        return new ProducerRecord(tool, version, CarlaNetVersion, server,
                                  string.IsNullOrWhiteSpace(sumo) ? null : sumo, now);
    }
}
