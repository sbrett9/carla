"""Check that a capture's imagery carries only the metadata chunks its writer emits.

The corpus is split into a truth channel and an observation channel, and the split is made **at the
writer** so nothing downstream has to remember to strip anything. A PNG is not opaque, though: the
recorder embeds JSON in tEXt chunks, so something that wrote into an image has bypassed the split
whatever the file tree looks like. A check that walks files and not chunks would not see it.

**That is the whole of this check: the chunks present are the chunks the writer emits.** It is a
structural check on the split, not a judgement about what any particular field means. Deciding
whether a quantity may be published is a person's call, made where the writer is written; a
name-matching script is not equipped to make it and does not try.

The three chunks a capture carries, and why each is on the list rather than tolerated by omission:

  * `carla:capture` -- the frame's own identity and the run that produced it.
  * `carla:solar` -- the sun the frame was rendered under.
  * `carla:sensor` -- the platform's position, pose and optics.

A fourth keyword means something started writing into the imagery without the split being revisited,
which is the failure this exists to catch.
"""
from __future__ import annotations

import json
import struct
import zlib
from collections.abc import Iterable, Iterator, Sequence
from dataclasses import dataclass
from pathlib import Path

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# The chunk keywords a capture may carry, named individually so that adding one is a decision.
ALLOWED_CHUNK_KEYWORDS = ("carla:solar", "carla:sensor", "carla:capture")


@dataclass(frozen=True)
class MetadataFinding:
    """A metadata chunk in an image that its writer does not emit."""

    path: Path
    keyword: str
    value: str

    def __str__(self) -> str:
        return (f"{self.path.name}: unrecognised metadata chunk {self.keyword!r}; a capture carries "
                f"only {', '.join(ALLOWED_CHUNK_KEYWORDS)}")


class CaptureMetadataValidator:
    """Reports metadata chunks in an image that the capture writer does not emit."""

    def __init__(self, allowed_keywords: Sequence[str] = ALLOWED_CHUNK_KEYWORDS):
        self.allowed_keywords = frozenset(allowed_keywords)

    # -- the check ---------------------------------------------------------------------------

    def check_png(self, path: str | Path) -> list[MetadataFinding]:
        """Findings for one image."""
        path = Path(path)
        return [MetadataFinding(path, keyword, text[:120])
                for keyword, text in self.read_text_chunks(path).items()
                if keyword not in self.allowed_keywords]

    def check_tree(self, root: str | Path) -> list[MetadataFinding]:
        """Findings across every PNG under a directory, in sorted order so a report is stable."""
        findings: list[MetadataFinding] = []
        for path in sorted(Path(root).rglob("*.png")):
            findings.extend(self.check_png(path))
        return findings

    # -- reading PNG text chunks -------------------------------------------------------------

    @classmethod
    def read_text_chunks(cls, path: str | Path) -> dict[str, str]:
        """Every tEXt, zTXt and iTXt chunk in a PNG, as keyword -> text.

        Compressed and international chunks are read as well as plain ones: a value is no less
        present for having been deflated, and a check that read only tEXt could be evaded by
        changing chunk type.
        """
        chunks: dict[str, str] = {}
        with open(path, "rb") as handle:
            if handle.read(8) != PNG_SIGNATURE:
                return chunks
            for kind, data in cls._chunks(handle):
                decoded = cls._decode_text_chunk(kind, data)
                if decoded is not None:
                    chunks[decoded[0]] = decoded[1]
        return chunks

    @staticmethod
    def _chunks(handle) -> Iterator[tuple[bytes, bytes]]:
        """Walk a PNG's chunks, seeking past the pixel data rather than reading it."""
        while True:
            header = handle.read(8)
            if len(header) < 8:
                return
            length, kind = struct.unpack(">I4s", header)
            if kind in (b"tEXt", b"zTXt", b"iTXt"):
                yield kind, handle.read(length)
            else:
                handle.seek(length, 1)
            handle.seek(4, 1)  # the chunk's CRC
            if kind == b"IEND":
                return

    @staticmethod
    def _decode_text_chunk(kind: bytes, data: bytes) -> tuple[str, str] | None:
        """One text chunk as (keyword, text), or None when it cannot be read as text."""
        keyword, _, rest = data.partition(b"\x00")
        try:
            name = keyword.decode("latin-1")
            if kind == b"tEXt":
                return name, rest.decode("latin-1")
            if kind == b"zTXt":
                return name, zlib.decompress(rest[1:]).decode("latin-1")
            # iTXt: compression flag, method, language tag, translated keyword, then the text.
            flag = rest[0:1]
            body = rest[2:].split(b"\x00", 2)[-1]
            return name, (zlib.decompress(body) if flag == b"\x01" else body).decode("utf-8")
        except (UnicodeDecodeError, zlib.error, IndexError):
            return None

    # -- reporting ---------------------------------------------------------------------------

    @staticmethod
    def describe(findings: Sequence[MetadataFinding]) -> str:
        """One line per finding, for a console or a CI log."""
        return "\n".join(str(finding) for finding in findings)

    @staticmethod
    def images(findings: Iterable[MetadataFinding]) -> list[Path]:
        """The distinct images a set of findings came from, in first-seen order."""
        seen: dict[Path, None] = {}
        for finding in findings:
            seen.setdefault(finding.path, None)
        return list(seen)

    @staticmethod
    def parse_chunk(text: str):
        """A chunk's JSON payload, or None when it is not JSON. For callers that want the values."""
        try:
            return json.loads(text)
        except ValueError:
            return None
