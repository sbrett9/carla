using System.Text.RegularExpressions;

namespace CarlaNet.Sumo;

/// <summary>
/// The SUMO release number out of a version string, however it was written, and whether two strings
/// name the same release.
/// </summary>
/// <remarks>
/// <para>The two sides of every comparison write the version differently. A world package records what
/// the converter printed when it ran -- <c>Eclipse SUMO netconvert 1.27.0</c> -- because that is what
/// was captured at the moment of conversion, while an installation is asked for its release and answers
/// <c>1.27.0</c>. Compared verbatim, a correctly matched pair is refused, and a false refusal is worse
/// than the failure the comparison exists for: it stops work that should proceed, and the operator's
/// only way past it is the flag that switches the comparison off. So both sides are reduced to the
/// release number before they meet.</para>
///
/// <para><b>What it cannot see.</b> A release number names a release, not a build: two builds of one
/// release with different patches or build options compare equal, and anything a development build
/// prints after the number (a git description, for example) is not part of the comparison.</para>
/// </remarks>
public static class SumoRelease
{
    // `Eclipse SUMO netconvert 1.27.0` / `Eclipse SUMO sumo 1.27.1` -- the first line of any SUMO
    // tool's --version output. The dot-separated number is the release; anything after it is not.
    private static readonly Regex VersionLine = new(@"Eclipse SUMO \S+ v?(\d+(?:\.\d+)*)",
                                                    RegexOptions.Compiled);

    // A bare release, as an older record may carry it: `1.27.0` or `v1.27.0`.
    private static readonly Regex BareRelease = new(@"\Av?(\d+(?:\.\d+)*)\z", RegexOptions.Compiled);

    /// <summary>
    /// The release number <paramref name="text"/> names, or <see langword="null"/> where it names
    /// nothing.
    /// </summary>
    /// <returns>
    /// The number out of a tool's version line, or out of a bare release; <see langword="null"/> for a
    /// null, empty or blank string; and otherwise the trimmed text itself, so a string that is neither
    /// still compares equal only to itself rather than to everything else that failed to parse.
    /// </returns>
    public static string? Of(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match line = VersionLine.Match(text);
        if (line.Success)
        {
            return line.Groups[1].Value;
        }

        string trimmed = text.Trim();
        Match bare = BareRelease.Match(trimmed);
        return bare.Success ? bare.Groups[1].Value : trimmed;
    }

    /// <summary>
    /// Whether <paramref name="installed"/> and <paramref name="recorded"/> name the same release.
    /// </summary>
    /// <remarks>
    /// An installation whose release could not be read agrees with nothing: it is not evidence of a
    /// match, and treating it as one would let an unreadable SUMO through every check.
    /// </remarks>
    public static bool Same(string? installed, string? recorded) =>
        Of(installed) is { } release && release == Of(recorded);

    /// <summary>
    /// The release in a SUMO tool's <c>--version</c> output, or <see langword="null"/> where the output
    /// carries no version line.
    /// </summary>
    /// <remarks>
    /// Stricter than <see cref="Of"/>, because this reads what a binary said about itself: output that
    /// is not a version line means the binary could not be asked, not that it is some other release.
    /// </remarks>
    public static string? FromToolOutput(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        Match line = VersionLine.Match(output);
        return line.Success ? line.Groups[1].Value : null;
    }
}
