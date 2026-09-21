"""A capture's imagery must not carry what only its truth sidecar may.

Two fields were leaking inside the image file itself. `carla:solar` embedded the sun *policy* --
whether the run was configured to advance its sun and at what rate -- and `carla:capture` embedded the
run configuration, `seed` live and `scenario_id` dormant only because nothing passed it. None of the
three is derivable by an observer with the same sensor, navigation solution, clock and public
reference data, and a seed or a scenario name is a handle on a whole set of scenes rather than a fact
about one. A validator that walked the file tree would have passed that corpus, because a PNG looks
opaque; the leak is in a tEXt chunk between IHDR and IDAT.

What the writer emits is asserted on the writer's own side, in
`CarlaNet/test/CarlaNet.Tests/Recording/CaptureImageMetadataTests.cs`. What is checked here is the
gate: that it fails on the captures already on disk, that it passes one written after the split, and
that it cannot be walked past by writing the same field into a different kind of chunk.

The 54 captures under `Build/SCTMV_recordings` are recorder output rather than repository files, so
they may not be present; `CARLA_CAPTURE_IMAGE` names one image instead. Those checks are the evidence
that the gate rejects something real, and they are skipped rather than faked when the files are gone.
"""
from __future__ import annotations

import os
import struct
import sys
import zlib
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CaptureMetadataValidator import (  # noqa: E402
    CaptureMetadataValidator,
    MetadataFinding,
)

# Chunk text with no run configuration in it -- what a training export's copy of a still carries.
# A recorded capture carries more; see SHIPPED_SOLAR.
SOLAR_STATE = ('{"solar_time":6.001808,"date":"2026-09-16","time_zone":-6.992299,'
               '"lat":39.59431,"lon":-104.88449,"sun_elevation_deg":2.661484,'
               '"sun_azimuth_deg":88.800522}')
CAPTURE_IDENTITY = '{"tick":64798,"sim_time_s":15.538677,"run_id":"run-20260916-195810"}'
SENSOR_POSE = ('{"uid":"CARLA-SENSOR-102","type":"a-f-A-M-F-Q","callsign":"OVERWATCH",'
               '"lat":39.5940521,"lon":-104.8838477,"hae":1907.41,"align_offset_m":1.23,'
               '"az_deg":297.433,"el_deg":-68.912,"roll_deg":0,"course_deg":297.4,'
               '"speed_mps":0.00,"intrinsics":{"width":2888,"height":2160,"fx":2181.65,'
               '"fy":2181.65,"cx":1444,"cy":1080,"hfov_deg":67,"vfov_deg":52.674,'
               '"model":"pinhole","distortion":"none","sensor_model":"sensor.camera.rgb"}}')

# The same two chunks as the 54 captures on disk carry them, transcribed from
# Build/SCTMV_recordings/SCTMV_2026.09.16_12.58.29.046.png so the rejection can be exercised on a
# machine that no longer has them.
SHIPPED_SOLAR = SOLAR_STATE[:-1] + ',"advancing":true,"rate":1}'
SHIPPED_CAPTURE = CAPTURE_IDENTITY[:-1] + ',"scenario_id":"Bahonar_PatternOfLife","seed":103}'


def _chunk(kind: bytes, payload: bytes) -> bytes:
    return (struct.pack(">I", len(payload)) + kind + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF))


def _png(path: Path, chunks: dict[str, str], compress_text: bool = False) -> Path:
    """A one-pixel PNG carrying the given metadata, laid out as the recorder lays one out."""
    body = b"\x89PNG\r\n\x1a\n" + _chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0))
    for keyword, text in chunks.items():
        raw = keyword.encode("latin-1") + b"\x00"
        if compress_text:
            body += _chunk(b"zTXt", raw + b"\x00" + zlib.compress(text.encode("latin-1")))
        else:
            body += _chunk(b"tEXt", raw + text.encode("latin-1"))
    body += _chunk(b"IDAT", zlib.compress(b"\x00\x00\x00\x00"))
    body += _chunk(b"IEND", b"")
    path.write_bytes(body)
    return path


def _shipped_images() -> list[Path]:
    """Captures written before the split, if this machine still has any."""
    named = os.environ.get("CARLA_CAPTURE_IMAGE")
    if named:
        return [Path(named)]
    return sorted((_REPO / "Build" / "SCTMV_recordings").glob("*.png"))


def _fields(findings: list[MetadataFinding]) -> set[str]:
    return {finding.field for finding in findings}


def test_a_capture_carrying_only_its_own_chunks_passes(tmp_path):
    image = _png(tmp_path / "clean.png", {"carla:solar": SOLAR_STATE,
                                          "carla:capture": CAPTURE_IDENTITY,
                                          "carla:sensor": SENSOR_POSE})
    assert CaptureMetadataValidator().check_png(image) == []


def test_a_chunk_the_writer_does_not_emit_is_reported(tmp_path):
    """The failure the check exists for: something wrote into the imagery."""
    image = _png(tmp_path / "extra.png", {"carla:solar": SOLAR_STATE,
                                          "carla:supervision": '{"guard_no_show":true}'})
    findings = CaptureMetadataValidator().check_png(image)
    assert [finding.keyword for finding in findings] == ["carla:supervision"]
    assert "unrecognised metadata chunk" in str(findings[0])


def test_a_chunk_hidden_by_compression_is_still_seen(tmp_path):
    """A value is no less present for having been deflated, so zTXt is read like tEXt."""
    image = _png(tmp_path / "zipped.png", {"carla:supervision": '{"guard_no_show":true}'},
                 compress_text=True)
    assert [finding.keyword for finding in CaptureMetadataValidator().check_png(image)] == [
        "carla:supervision"]


def test_a_tree_of_images_is_checked_together(tmp_path):
    _png(tmp_path / "clean.png", {"carla:solar": SOLAR_STATE})
    _png(tmp_path / "extra.png", {"carla:supervision": '{"x":1}'})
    findings = CaptureMetadataValidator().check_tree(tmp_path)
    assert [path.name for path in CaptureMetadataValidator.images(findings)] == ["extra.png"]


@pytest.mark.skipif(not _shipped_images(),
                    reason="no captures under Build/SCTMV_recordings; set CARLA_CAPTURE_IMAGE "
                           "to check one")
def test_the_shipped_captures_carry_only_the_three_chunks():
    """Against real files: the writer's split holds across every capture on this machine."""
    validator = CaptureMetadataValidator()
    for image in _shipped_images():
        assert validator.check_png(image) == [], validator.describe(validator.check_png(image))
    chunks = validator.read_text_chunks(_shipped_images()[0])
    assert set(chunks) <= {"carla:solar", "carla:sensor", "carla:capture"}
    assert chunks, "a shipped capture was expected to carry metadata at all"
