using System.Xml;
using System.Xml.Linq;
using CarlaNet.Map;
using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim;

/// <summary>
/// Establishes whether the road network a scenario runs SUMO on is the network the world package
/// carries, by the canonical fingerprint of each, and refuses a session where it is not.
/// </summary>
/// <remarks>
/// <para><b>Why it is needed.</b> The session interpolates every vehicle along the lanes of the
/// package's <c>map.net.xml</c>, and SUMO drives it along the lanes of whatever network the scenario's
/// configuration names. Two networks converted from one extract with one flag set are not one graph
/// -- asking netconvert for OpenDRIVE output changes junction shapes, so a second run moves lanes by
/// metres -- and they agree on the projection, the offset and the boundary, which is everything the
/// session's frame checks compare. Measured on the shipped Arapahoe scenario against its world, over
/// 193,426 frames: a lane-geometry residual of 0.258 m on average and 1.342 m at worst, and 68
/// spurious discontinuities, where the world's own network gives 0.000 m -- in imagery that looks
/// entirely ordinary.</para>
///
/// <para><b>What is compared.</b></para>
/// <list type="bullet">
/// <item><b>The scenario's network</b>: the file SUMO loads for its <c>net-file</c> option, read the
/// way SUMO reads a configuration -- under any of the three names it takes the option by,
/// <c>net-file</c>, <c>net</c> and <c>n</c> (<c>MSFrame.cpp:78-79</c>), at any depth, from a
/// <c>value</c> or <c>v</c> attribute or the element's text (<c>OptionsLoader.cpp</c>), and a relative
/// path resolved against the configuration's own directory (<c>OptionsCont::relocateFiles</c>). The
/// session passes SUMO no network of its own, so this is the network SUMO runs.</item>
/// <item><b>The package's network</b>: its <c>map.net.xml</c>, which is what the session reads its
/// lanes from.</item>
/// <item><b>The fingerprint the package records</b>, <see cref="WorldPackageManifest.NetworkFingerprint"/>,
/// where it records one: written by the world build from the network it converted, beside the
/// OpenDRIVE digest the loaded-world check compares. A package carrying a network other than the
/// one it records was assembled from two builds, and nothing ties what it carries to the world the
/// server loaded. A package that records none is compared on what it carries alone.</item>
/// </list>
///
/// <para>Compared by <see cref="NetworkFingerprint"/> -- the parsed graph, not the bytes. netconvert
/// stamps every output with the moment it ran and the paths it was handed, so two files of one graph
/// differ in bytes, and a file can be re-serialised without its graph changing.</para>
///
/// <para><b>What it cannot see.</b></para>
/// <list type="bullet">
/// <item><b>What the fingerprint leaves out.</b> It covers edges, lanes (id, index, speed, length,
/// width, permissions and shape), junctions (id, type, position and lanes), every attribute of every
/// connection, each signal programme's type and phase states, and the location. Two networks that
/// differ only elsewhere pass: in the phase durations of a signal programme, a junction's
/// right-of-way rows (<c>&lt;request&gt;</c>), a lane's <c>changeLeft</c>, <c>changeRight</c> or
/// <c>acceleration</c>, a junction's or edge's drawn shape, or a <c>&lt;roundabout&gt;</c> -- all of
/// which the shipped networks carry, and some of which change how traffic moves.</item>
/// <item><b>Anything else the scenario loads.</b> Route and additional files are not read, and an
/// additional file can carry signal programmes of its own.</item>
/// <item><b>SUMO's substitutions.</b> A <c>${...}</c> or a leading <c>~</c> in the network's path is
/// taken literally, so a scenario that names its network that way is refused as naming a file that
/// is not there, even where SUMO would find one. A compressed network is refused as unreadable.</item>
/// <item><b>A file changed after the check.</b> It is taken once, when the session starts, just
/// before SUMO reads the file. The lane-geometry residual on the run report reads zero on a network
/// this check admits, and is what would show one changed in between.</item>
/// </list>
/// </remarks>
public static class ScenarioNetworkCheck
{
    /// <summary>
    /// The names SUMO takes its network option by in a configuration: the option, its synonym and its
    /// one-letter abbreviation.
    /// </summary>
    private static readonly string[] NetworkOptionNames = ["net-file", "net", "n"];

    /// <summary>
    /// Refuse a scenario whose network is not the one the world package carries, or a package that
    /// carries a network other than the one it records, naming both networks and both fingerprints.
    /// </summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <param name="packagePath">The world package.</param>
    /// <exception cref="CoSimSessionRefusedException">
    /// The package's network is not the one it records; the configuration names no network, names
    /// one more than once, or names one that is not there or cannot be read; or the network it names
    /// is not the package's.
    /// </exception>
    public static void Require(string scenarioPath, string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        WorldPackageManifest manifest = WorldPackage.ReadManifest(packagePath);
        string carried = NetworkFingerprint.Compute(WorldPackage.ReadNetwork(packagePath));
        string recorded = manifest.NetworkFingerprint;
        if (recorded.Length > 0 && recorded != carried)
        {
            throw new CoSimSessionRefusedException(
                $"The world package {packagePath} records network fingerprint {recorded} and carries "
                + $"a map.net.xml that fingerprints as {carried}. It was assembled from two builds, "
                + "so nothing ties the network it carries to the world it was built as. SUMO has not "
                + "been started. Rebuild the world.");
        }

        string networkPath = NetworkFileOf(scenarioPath);
        string scenario = FingerprintOf(networkPath, scenarioPath);
        if (scenario != carried)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} runs SUMO on the network {networkPath}, which "
                + $"fingerprints as {scenario}, and the world package {packagePath} carries a "
                + $"map.net.xml that fingerprints as {carried}. They are different road graphs: the "
                + "session would interpolate every vehicle along the package's lanes while SUMO drives "
                + "it along the scenario's, and every pose would be off by the difference, in imagery "
                + "that looks entirely ordinary. SUMO has not been started. Compile the scenario "
                + "against this world package -- the compiler writes the package's own network beside "
                + "the configuration -- or give the session the package the scenario was compiled "
                + "against.");
        }
    }

    /// <summary>
    /// The network file a SUMO configuration loads, as a full path: the one value of its network
    /// option, resolved against the configuration's own directory where it is relative.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The configuration is not there or is not XML, sets no network, or sets it more than once.
    /// </exception>
    public static string NetworkFileOf(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        string configuration = Path.GetFullPath(configurationPath);

        XDocument document;
        try
        {
            document = XDocument.Load(configuration);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException
                                               or XmlException)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {configurationPath} cannot be read as a SUMO configuration, so the "
                + $"network it runs on cannot be compared with the world's: {unreadable.Message}",
                unreadable);
        }

        List<string> named = [];
        foreach (XElement option in document.Descendants())
        {
            if (!NetworkOptionNames.Contains(option.Name.LocalName, StringComparer.Ordinal))
            {
                continue;
            }

            // SUMO takes the value from either attribute, or failing both from the element's text,
            // and ignores an empty one.
            string value = (string?)option.Attribute("value")
                           ?? (string?)option.Attribute("v")
                           ?? option.Value.Trim();
            if (value.Length > 0)
            {
                named.Add(value);
            }
        }

        if (named.Count == 0)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {configurationPath} names no network: it sets none of "
                + $"{string.Join(", ", NetworkOptionNames)}. SUMO would not start on it, and there is "
                + "nothing to compare with the world's network.");
        }

        if (named.Count > 1)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {configurationPath} names its network {named.Count} times ("
                + string.Join(", ", named.Select(value => $"'{value}'"))
                + "). SUMO refuses an option set twice, and which of them would be compared with the "
                + "world's network is not the configuration's to leave open.");
        }

        return Path.GetFullPath(named[0], Path.GetDirectoryName(configuration)!);
    }

    /// <summary>The canonical fingerprint of the network a scenario names, or a refusal saying why not.</summary>
    private static string FingerprintOf(string networkPath, string scenarioPath)
    {
        if (!File.Exists(networkPath))
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} runs SUMO on the network {networkPath}, resolved against "
                + "the configuration's own directory as SUMO resolves it, and there is no file there.");
        }

        try
        {
            return NetworkFingerprint.ComputeFile(networkPath);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException
                                               or XmlException or InvalidDataException)
        {
            throw new CoSimSessionRefusedException(
                $"The network {networkPath} the scenario {scenarioPath} runs on cannot be read as a "
                + $"SUMO network, so it cannot be compared with the world's: {unreadable.Message}",
                unreadable);
        }
    }
}
