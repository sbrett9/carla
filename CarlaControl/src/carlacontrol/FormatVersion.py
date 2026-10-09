"""The rule every reader of our files holds a file's format version to.

A format version is an integer of its own file -- `manifest_version`, `lock_version`, a sidecar's
`format_version` -- independent of the release version: it changes when the file's shape does, and only
then. A reader reads a version it knows; refuses a newer one by name rather than reading it in part,
because a field that moved silently is worse than one that is absent; and reads a file written before
its kind carried a version as version 1, the shape such files have. The refusal names the file, the
version it declares and the newest the reader supports, so whoever meets it knows what to upgrade.

`CarlaNet.Types.Provenance.FormatVersions` holds the C# readers to the same rule, in the same words.
"""
from __future__ import annotations

# The version of a file written before its kind carried one.
LEGACY = 1


class FormatVersionError(ValueError):
    """A file declares a format version this reader cannot read."""


class FormatVersion:
    """Checks the format version a file declares against what a reader supports."""

    @staticmethod
    def check(file: object, field: str, declared: object, supported: int) -> int:
        """The version `file` declares in `field`, `LEGACY` where it declares none.

        Raises:
            FormatVersionError: the version is not an integer, is below 1, or is newer than
                `supported`.
        """
        if declared is None:
            return LEGACY
        if isinstance(declared, str) and declared.isdigit():
            declared = int(declared)
        if isinstance(declared, bool) or not isinstance(declared, int):
            raise FormatVersionError(f"{file} declares {field} {declared!r}, which is not an integer "
                                     "format version")
        if declared < 1:
            raise FormatVersionError(f"{file} declares {field} {declared}, which is not a format "
                                     "version: they start at 1")
        if declared > supported:
            raise FormatVersionError(
                f"{file} declares {field} {declared}, and this reader supports {field} {supported} "
                "and earlier. It was written by a newer release; read it with that release's tools.")
        return declared
