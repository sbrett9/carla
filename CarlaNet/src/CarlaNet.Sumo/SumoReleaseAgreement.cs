namespace CarlaNet.Sumo;

/// <summary>
/// How the release of the SUMO a run launches stands against the converter a world was built with.
/// </summary>
public enum SumoReleaseAgreement
{
    /// <summary>The installation reports the release the world's converter reported.</summary>
    SameRelease,

    /// <summary>
    /// The world records no converter, so there is nothing to compare against. A package written
    /// before the converter was recorded is not evidence of a mismatch, so this proceeds.
    /// </summary>
    NotRecorded,

    /// <summary>The releases differ, and the operator accepted the difference explicitly.</summary>
    MismatchAccepted,

    /// <summary>The releases differ, or the installation's release could not be read.</summary>
    Mismatch,
}
