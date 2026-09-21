"""Check that a capture's imagery does not carry what only its truth sidecar may.

A recorded still is the observation channel. Beside it sits a Cursor-on-Target sidecar, which is the
truth channel, and the split between them is made at the writer so that nothing downstream has to
remember to strip anything. A PNG, though, is not opaque: the recorder embeds JSON in tEXt chunks, so
a field removed from the sidecar and left in the image has not been removed at all. A validator that
walks files and not chunks passes a corpus that leaks, and that is exactly the shape of the two leaks
found in the tree: `advancing` and `rate` inside `carla:solar`, and `seed` inside `carla:capture`.

What may be in the imagery is decided by one rule, not case by case: **a quantity may ride in the
observation channel only if a fielded system with the same sensor, the same navigation solution, the
same clock and public reference data could compute it without observing the scene's contents.**

Three consequences of that rule are what this enforces:

  * **Sun state passes, sun policy does not.** Solar time, date, time zone, latitude, longitude and
    the sun's elevation and azimuth follow from a clock, a position and public ephemeris. Whether the
    run was configured to advance its sun, and how fast, describe the simulation -- and disclose that
    the frame belongs to a controlled sweep.
  * **`carla:sensor` stays.** The platform's position is the navigation solution and the intrinsics
    are the sensor; the rule grants a fielded observer both by name. It is on the allow-list rather
    than tolerated by omission.
  * **Run configuration does not pass.** A scenario name and a seed are handles on a whole *set* of
    scenes. A corpus carrying them can be learned by scenario rather than by scene, which is the same
    hazard that partitioning a held-back release at session granularity addresses.

The check is mechanical and knows nothing about what a field means: a chunk keyword that is not on
the allow-list is reported, and so is any excluded field name found in a chunk, wherever it is nested.
Deciding that a finding is acceptable is a person's job; not noticing it is what this prevents.
"""
from __future__ import annotations

import json
import struct
import zlib
from collections.abc import Iterable, Iterator, Sequence
from dataclasses import dataclass
from pathlib import Path

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# The chunks a capture may carry. `carla:sensor` is named here rather than tolerated by omission:
# the platform's own position and its own optics are what the observer-derivability rule grants a
# fielded observer, so they are a decision rather than an oversight.
ALLOWED_CHUNK_KEYWORDS = ("carla:solar", "carla:sensor", "carla:capture")

# Field names that must never appear in the imagery, whatever chunk they are written into.
#
# The first group is run configuration: how the simulation was set up, and the handles that index the
# set of scenes it produced. The second is scene truth: anything that could only be written by
# something that looked at the scene and knows what is in it.
EXCLUDED_FIELDS = (
    "advancing", "rate", "solar_policy", "scenario_id", "seed",
    "actor_id", "entity_id", "instance_id", "occlusion", "opacity", "hae_dtm", "light_state",
    "visible_signature", "lit_face_px", "shadow_px", "observability_level",
    "truth_separation_px", "truth_separation_norm", "truth_neighbour_count",
)


@dataclass(frozen=True)
class MetadataFinding:
    """One thing an image carries that the observation channel may not."""

    path: Path
    keyword: str
    field: str
    value: str

    def __str__(self) -> str:
        if self.field:
            return (f"{self.path.name}: {self.keyword} carries {self.field}={self.value!r}, "
                    "which belongs in the truth sidecar")
        return (f"{self.path.name}: unrecognised metadata chunk {self.keyword!r}; the imagery may "
                f"carry only {', '.join(ALLOWED_CHUNK_KEYWORDS)}")


class CaptureMetadataValidator:
    """Reports what a recorded still carries in its metadata that only its sidecar may carry."""

    def __init__(self, allowed_keywords: Sequence[str] = ALLOWED_CHUNK_KEYWORDS,
                 excluded_fields: Sequence[str] = EXCLUDED_FIELDS):
        self.allowed_keywords = frozenset(allowed_keywords)
        self.excluded_fields = frozenset(excluded_fields)

    # -- the check ---------------------------------------------------------------------------

    def check_png(self, path: str | Path) -> list[MetadataFinding]:
        """Findings for one image."""
        path = Path(path)
        findings = []
        for keyword, text in self.read_text_chunks(path).items():
            if keyword not in self.allowed_keywords:
                findings.append(MetadataFinding(path, keyword, "", text[:120]))
            for field, value in self._fields(text):
                if field in self.excluded_fields:
                    findings.append(MetadataFinding(path, keyword, field, value))
        return findings

    def check_tree(self, root: str | Path) -> list[MetadataFinding]:
        """Findings for every image under a directory, or for a single image."""
        root = Path(root)
        images = sorted(root.rglob("*.png")) if root.is_dir() else [root]
        return [finding for image in images for finding in self.check_png(image)]

    # -- reading -----------------------------------------------------------------------------

    @classmethod
    def read_text_chunks(cls, path: str | Path) -> dict[str, str]:
        """Every textual chunk in a PNG, by keyword.

        All three of the textual chunk types are read, not just the uncompressed one the recorder
        writes: a validator that only understood `tEXt` could be walked past by writing the same
        field as `zTXt`, and a check with a gap in it is worse than none because it is trusted.

        Chunk payloads are skipped over rather than read, so this costs the same on a 79 MB capture
        as on a thumbnail.
        """
        chunks: dict[str, str] = {}
        with open(path, "rb") as handle:
            if handle.read(len(PNG_SIGNATURE)) != PNG_SIGNATURE:
                raise ValueError(f"{path} is not a PNG")
            for kind, data in cls._chunks(handle):
                decoded = cls._decode_text_chunk(kind, data)
                if decoded is not None:
                    chunks[decoded[0]] = decoded[1]
        return chunks

    @staticmethod
    def _chunks(handle) -> Iterator[tuple[bytes, bytes]]:
        """Each chunk's type and, for textual chunks only, its data. Others are seeked past."""
        textual = (b"tEXt", b"zTXt", b"iTXt")
        while True:
            header = handle.read(8)
            if len(header) < 8:
                return
            length, kind = struct.unpack(">I4s", header)
            if kind in textual:
                yield kind, handle.read(length)
            else:
                handle.seek(length, 1)
            handle.seek(4, 1)   # the chunk's CRC
            if kind == b"IEND":
                return

    @staticmethod
    def _decode_text_chunk(kind: bytes, data: bytes) -> tuple[str, str] | None:
        """A textual chunk as (keyword, text), or None if it cannot be read as one.

        Latin-1 is the PNG keyword encoding; an `iTXt` value is UTF-8. A chunk whose payload does not
        decompress is reported as its raw bytes rather than dropped, so that an unreadable chunk is
        still visible to the caller instead of silently passing.
        """
        keyword, separator, rest = data.partition(b"\x00")
        if not separator:
            return None
        name = keyword.decode("latin-1", errors="replace")
        if kind == b"tEXt":
            return name, rest.decode("latin-1", errors="replace")
        if kind == b"zTXt":
            body = rest[1:] if rest else b""       # a compression-method byte precedes the data
            try:
                return name, zlib.decompress(body).decode("latin-1", errors="replace")
            except zlib.error:
                return name, repr(body)
        # iTXt: compression flag, compression method, language tag, translated keyword, then text.
        if len(rest) < 2:
            return None
        compressed, _method, remainder = rest[0], rest[1], rest[2:]
        _language, _, remainder = remainder.partition(b"\x00")
        _translated, _, body = remainder.partition(b"\x00")
        if compressed:
            try:
                body = zlib.decompress(body)
            except zlib.error:
                return name, repr(body)
        return name, body.decode("utf-8", errors="replace")

    @classmethod
    def _fields(cls, text: str) -> list[tuple[str, str]]:
        """Every field name and value in a chunk, however deeply nested.

        A chunk that is not JSON is reported as one field per line so that a name written into free
        text is still seen; the recorder writes JSON, but the check must not depend on that.
        """
        try:
            return list(cls._walk(json.loads(text)))
        except (json.JSONDecodeError, TypeError):
            return [(word.strip(" \"'{},:"), text[:120])
                    for word in text.replace(":", " ").replace(",", " ").split()]

    @classmethod
    def _walk(cls, node) -> Iterator[tuple[str, str]]:
        if isinstance(node, dict):
            for key, value in node.items():
                yield str(key), "" if isinstance(value, dict | list) else str(value)
                yield from cls._walk(value)
        elif isinstance(node, list):
            for value in node:
                yield from cls._walk(value)

    # -- reporting ---------------------------------------------------------------------------

    @staticmethod
    def describe(findings: Sequence[MetadataFinding]) -> str:
        """One line per finding, for a log or a failing test."""
        if not findings:
            return "no image carries anything the observation channel may not"
        return "\n".join(str(finding) for finding in findings)

    @staticmethod
    def images(paths: Iterable[MetadataFinding]) -> list[Path]:
        """The distinct images a set of findings came from."""
        return sorted({finding.path for finding in paths})
