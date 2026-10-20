using CarlaNet.Map.WorldPackage;
using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// Settles which SUMO a session launches and refuses one it cannot or should not launch, before it
/// is started.
/// </summary>
/// <remarks>
/// The session resolves the installation, then checks that it has the binary the session is to
/// launch, then checks that binary's release against the converter that built the world. Each step
/// needs the one before it, and none needs a running simulation.
/// </remarks>
internal static class SumoLaunchCheck
{
    /// <summary>
    /// The SUMO installation the session will launch: the one the caller named, or the one the
    /// search resolves.
    /// </summary>
    internal static SumoInstallation ResolveSumo(SumoDriveSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SumoHome))
        {
            return SumoInstallation.LocateOrThrow();
        }

        try
        {
            return SumoInstallation.At(options.SumoHome);
        }
        catch (DirectoryNotFoundException missing)
        {
            throw new CoSimSessionRefusedException(
                $"The session was told to launch the SUMO installation at {options.SumoHome}, and "
                + $"there is no sumo there to launch. {missing.Message}", missing);
        }
    }

    /// <summary>
    /// Refuse a session asked to show its simulation in <c>sumo-gui</c> where the installation it
    /// launches from has none.
    /// </summary>
    /// <remarks>
    /// Settled before anything is started, like the release. Left to the launch, the failure would
    /// be the operating system's word for a missing file, naming neither the option that asked for
    /// the GUI nor what builds it. The GUI is looked for in the installation already resolved, never
    /// elsewhere: a <c>sumo-gui</c> from another installation would be another SUMO, and the
    /// simulation it showed would not be the one the world's converter was checked against.
    /// </remarks>
    internal static void RequireTheGui(SumoInstallation installation, SumoDriveSessionOptions options)
    {
        if (!options.SumoGui || installation.HasSumoGui)
        {
            return;
        }

        throw new CoSimSessionRefusedException(
            "The session was asked to launch sumo-gui in place of sumo (SumoGui; carla-drive "
            + $"--sumo-gui), and the SUMO installation at {installation.Home} (matched by "
            + $"{installation.Source}) has no {installation.SumoGui}. A CARLA distribution's own SUMO "
            + "has none: name an installation of your own that has one, of the release that converted "
            + "the world (SumoHome; carla-drive --sumo-home). In a source checkout, SUMO builds it only "
            + "where the FOX toolkit is found, and the repository's setup script builds and stages it with "
            + "the rest of the SUMO toolchain: run CarlaSetup.ps1 on Windows or CarlaSetup.sh on Linux, "
            + "which rebuilds a staged installation that is missing it. Or run without the GUI. SUMO has "
            + "not been started.");
    }

    /// <summary>
    /// Refuse a SUMO whose release is not the one that converted the world, unless the mismatch was
    /// accepted.
    /// </summary>
    /// <remarks>
    /// <para>The world's network is one netconvert's output and the scenario was authored against
    /// it; a different release is not guaranteed to read it, route on it or drive it the same way,
    /// and nothing in the imagery says which SUMO produced the traffic in it. More than one SUMO is
    /// commonly installed -- the repository pins one, and a system-wide installer leaves another in
    /// <c>SUMO_HOME</c> -- so this is checked every run rather than assumed.</para>
    ///
    /// <para>Compared by release number (<see cref="SumoRelease"/>): the package records what the
    /// converter printed and the installation reports its release, and the two are the same release
    /// however each was written. A package that records no converter is not evidence of a mismatch
    /// and proceeds; the report says the release went unchecked. An accepted mismatch proceeds and
    /// the report says that too.</para>
    ///
    /// <para>The release is the one the binary about to be launched reports -- <c>sumo-gui</c>'s own
    /// where the session launches it in place of <c>sumo</c> -- because that is the program whose
    /// reading of the network the run is.</para>
    /// </remarks>
    internal static SumoReleaseCheck RequireTheWorldSConverter(SumoInstallation installation,
                                                               WorldPackageManifest manifest,
                                                               SumoDriveSessionOptions options)
    {
        SumoReleaseCheck release = SumoReleaseCheck.Of(
            installation,
            options.SumoGui ? SumoInstallation.SumoGuiName : SumoInstallation.SumoName,
            manifest.NetconvertVersion,
            options.AllowSumoVersionMismatch);
        if (release.Refused)
        {
            throw new CoSimSessionRefusedException(
                $"The world package {options.WorldPackagePath} was converted by "
                + $"'{release.RecordedConverter}', and the SUMO the session would launch, "
                + $"{release.Binary}, is "
                + (release.Release is { } found ? $"release {found}" : "of a release that could not be read")
                + $" (the installation at {release.Home}, matched by {release.Source}). Two SUMO releases are not "
                + "guaranteed to build the same network from the same OSM or to drive it the same way, "
                + "so SUMO has not been started. Name an installation of the world's release (SumoHome; "
                + "run_sumo_drive.py --sumo-home), rebuild the world with this one, or accept the "
                + "mismatch explicitly (AllowSumoVersionMismatch; run_sumo_drive.py "
                + "--allow-sumo-version-mismatch), which the run report then records.");
        }

        return release;
    }
}
