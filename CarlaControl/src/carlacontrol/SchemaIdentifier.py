"""The identifier every schema we publish carries: a URN naming the file kind and its format version.

A schema's `$id` names what it describes, not where it is kept, so it is a URN rather than a web
address nothing serves: `urn:carla-sumo-capture:schema:<kind>:<format version>`, the kind spelled in
lower case with hyphens (`run-result`, `scenario-lock`) and the version the integer the file itself
declares (`result_version`, `lock_version`). Two versions of one file kind are two schemas, with two
identifiers.

This module imports nothing, so the schema documents that are themselves validators
(`ScenarioSchema`) can name themselves with it.
"""
from __future__ import annotations

import re

PREFIX = "urn:carla-sumo-capture:schema"
DIALECT = "https://json-schema.org/draft/2020-12/schema"
_KIND = re.compile(r"^[a-z][a-z0-9]*(-[a-z0-9]+)*$")


class SchemaIdentifier:
    """Builds and reads the `$id` of a published schema."""

    @staticmethod
    def urn(kind: str, version: int) -> str:
        """`urn:carla-sumo-capture:schema:<kind>:<version>`.

        Raises:
            ValueError: the kind is not lower case with hyphens, or the version is not a positive
                integer.
        """
        if not _KIND.match(kind):
            raise ValueError(f"a schema kind is lower case words joined by hyphens, not {kind!r}")
        if isinstance(version, bool) or not isinstance(version, int) or version < 1:
            raise ValueError(f"a format version is an integer from 1, not {version!r}")
        return f"{PREFIX}:{kind}:{version}"

    @staticmethod
    def version_of(identifier: str) -> int:
        """The format version a schema identifier names."""
        return int(identifier.rsplit(":", 1)[1])
