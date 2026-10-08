"""The tEXt chunks of a PNG file: the text a still carries beside its pixels.

A still's recorder writes its text chunks -- `carla:solar`, `carla:illumination`, `carla:sensor`,
`carla:capture` -- between the image header and the pixel data, so they are read without reading the
pixels: the chunks are walked from the signature to the first `IDAT` chunk and no further. Each tEXt
chunk is a Latin-1 keyword, a zero byte and Latin-1 text.
"""
from __future__ import annotations

import struct
import zlib
from pathlib import Path

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


class PngTextChunksError(ValueError):
    """A file is not a PNG, or its chunks before the pixel data are broken."""


class PngTextChunks:
    """Reads the tEXt chunks a PNG carries before its pixel data."""

    @staticmethod
    def read(path: str | Path) -> list[tuple[str, str]]:
        """Every tEXt chunk before the first IDAT, as (keyword, text) in file order.

        Raises:
            PngTextChunksError: the file is not a PNG, a chunk is cut short or fails its checksum,
                or a tEXt chunk has no keyword separator.
        """
        chunks: list[tuple[str, str]] = []
        with open(path, "rb") as file:
            if file.read(len(PNG_SIGNATURE)) != PNG_SIGNATURE:
                raise PngTextChunksError(f"{path} is not a PNG file: its signature is wrong")
            while True:
                head = file.read(8)
                if len(head) < 8:
                    raise PngTextChunksError(f"{path} ends before its pixel data")
                length, kind = struct.unpack(">I4s", head)
                if kind in (b"IDAT", b"IEND"):
                    return chunks
                body = file.read(length)
                checksum = file.read(4)
                if len(body) < length or len(checksum) < 4:
                    raise PngTextChunksError(f"{path}: its {kind.decode('latin-1')} chunk is cut short")
                if struct.unpack(">I", checksum)[0] != zlib.crc32(kind + body) & 0xFFFFFFFF:
                    raise PngTextChunksError(f"{path}: its {kind.decode('latin-1')} chunk fails its checksum")
                if kind == b"tEXt":
                    keyword, separator, text = body.partition(b"\0")
                    if not separator:
                        raise PngTextChunksError(f"{path}: a tEXt chunk has no keyword separator")
                    chunks.append((keyword.decode("latin-1"), text.decode("latin-1")))

    @staticmethod
    def write(path: str | Path, chunks: list[tuple[str, str]], width: int = 1, height: int = 1) -> Path:
        """Write a gray PNG of the given size carrying `chunks`: a stand-in still for a test."""
        def chunk(kind: bytes, body: bytes) -> bytes:
            return (struct.pack(">I", len(body)) + kind + body
                    + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF))

        rows = b"".join(b"\0" + b"\x80\x80\x80" * width for _ in range(height))
        header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
        data = PNG_SIGNATURE + chunk(b"IHDR", header)
        for keyword, text in chunks:
            data += chunk(b"tEXt", keyword.encode("latin-1") + b"\0" + text.encode("latin-1"))
        data += chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
        path = Path(path)
        path.write_bytes(data)
        return path
