namespace CarlaNet.Sumo;

/// <summary>
/// The SUMO installation a run launches, set against the converter that built the world it runs on.
/// </summary>
/// <param name="Home">The installation's root.</param>
/// <param name="Source">
/// Which rule found it (<see cref="SumoInstallation.Source"/>): <c>explicit</c> when the caller named
/// it, otherwise the search rule that matched.
/// </param>
/// <param name="Release">
/// The release the installation reported, or <see langword="null"/> where it could not be read.
/// </param>
/// <param name="RecordedConverter">
/// The converter the world package records, verbatim -- <c>Eclipse SUMO netconvert 1.27.0</c> -- or
/// <see langword="null"/> where it records none.
/// </param>
/// <param name="Agreement">How the two stand, compared by release number.</param>
/// <remarks>
/// <para>A world's road network is one netconvert's output, and a scenario authored against it and a
/// session driving it both assume that release's reading of it. Two SUMO releases are not guaranteed
/// to convert the same OSM into the same network or to drive it the same way, and a machine commonly
/// holds more than one: the repository pins a build, and a system-wide installer leaves another in
/// <c>SUMO_HOME</c>. Nothing in the binaries says which one a process picked. So the installation and
/// its release belong on every run's record, and a release that differs from the world's converter is
/// a mismatch unless an operator has accepted it.</para>
///
/// <para>This decides how the two stand and nothing else. Whether a mismatch stops the caller is the
/// caller's to enforce, because what a refusal must tell the operator -- which flag, which option --
/// depends on who is refusing.</para>
/// </remarks>
public sealed record SumoReleaseCheck(string Home,
                                      string Source,
                                      string? Release,
                                      string? RecordedConverter,
                                      SumoReleaseAgreement Agreement)
{
    /// <summary>The release number the recorded converter names.</summary>
    public string? RecordedRelease => SumoRelease.Of(RecordedConverter);

    /// <summary>Whether the caller must refuse: the releases differ and nobody accepted it.</summary>
    public bool Refused => Agreement == SumoReleaseAgreement.Mismatch;

    /// <summary>
    /// Whether the installation is known to be the world's release. False for an accepted mismatch and
    /// for a world that records no converter, which proceed and deserve a warning rather than a note.
    /// </summary>
    public bool Agrees => Agreement == SumoReleaseAgreement.SameRelease;

    /// <summary>The installation, its release and the rule that found it, as one line.</summary>
    public string Installation =>
        $"{Home} (release {Release ?? "unreadable"}, matched by {Source})";

    /// <summary>How the installation stands against the world's converter, as one line.</summary>
    public string Verdict => Agreement switch
    {
        SumoReleaseAgreement.SameRelease =>
            $"the world was converted by '{RecordedConverter}', the same release",
        SumoReleaseAgreement.NotRecorded =>
            "the world package records no converter, so the release is unchecked; rebuild the world "
            + "to record one",
        SumoReleaseAgreement.MismatchAccepted =>
            $"the world was converted by '{RecordedConverter}', a different release; run because the "
            + "mismatch was explicitly accepted",
        _ => $"the world was converted by '{RecordedConverter}', a different release; refused",
    };

    /// <summary>
    /// How <paramref name="installation"/> stands against <paramref name="recordedConverter"/>.
    /// </summary>
    /// <param name="installation">The SUMO about to be launched. Its release is probed if it has not been.</param>
    /// <param name="recordedConverter">What the world package records; empty or null where it records none.</param>
    /// <param name="allowMismatch">Accept a different release rather than calling it a mismatch.</param>
    public static SumoReleaseCheck Of(SumoInstallation installation,
                                      string? recordedConverter,
                                      bool allowMismatch)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return Compare(installation.Home, installation.Source, installation.Release, recordedConverter,
                       allowMismatch);
    }

    /// <summary>
    /// How an installation reporting <paramref name="release"/> stands against
    /// <paramref name="recordedConverter"/>, with nothing launched to find out.
    /// </summary>
    public static SumoReleaseCheck Compare(string home,
                                           string source,
                                           string? release,
                                           string? recordedConverter,
                                           bool allowMismatch)
    {
        string? recorded = string.IsNullOrWhiteSpace(recordedConverter) ? null : recordedConverter.Trim();
        SumoReleaseAgreement agreement =
            recorded is null ? SumoReleaseAgreement.NotRecorded
            : SumoRelease.Same(release, recorded) ? SumoReleaseAgreement.SameRelease
            : allowMismatch ? SumoReleaseAgreement.MismatchAccepted
            : SumoReleaseAgreement.Mismatch;
        return new SumoReleaseCheck(home, source, release, recorded, agreement);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Installation}; {Verdict}";
}
