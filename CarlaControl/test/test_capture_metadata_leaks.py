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

# The chunk text the recorder writes since the split, with the sun policy and the run configuration
# gone and the platform block untouched.
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


def test_a_capture_written_after_the_split_passes(tmp_path):
    image = _png(tmp_path / "after.png", {"carla:solar": SOLAR_STATE,
                                          "carla:sensor": SENSOR_POSE,
                                          "carla:capture": CAPTURE_IDENTITY})
    findings = CaptureMetadataValidator().check_png(image)
    assert findings == [], CaptureMetadataValidator.describe(findings)


def test_the_sun_policy_is_rejected(tmp_path):
    image = _png(tmp_path / "policy.png", {"carla:solar": SHIPPED_SOLAR})
    assert _fields(CaptureMetadataValidator().check_png(image)) == {"advancing", "rate"}


def test_the_run_configuration_is_rejected(tmp_path):
    """Supplying scenario_id without this change would have activated the dormant half of the leak."""
    image = _png(tmp_path / "configuration.png", {"carla:capture": SHIPPED_CAPTURE})
    assert _fields(CaptureMetadataValidator().check_png(image)) == {"scenario_id", "seed"}


def test_the_platform_block_is_allowed_by_name(tmp_path):
    """The platform's position is the navigation solution and the intrinsics are the sensor."""
    image = _png(tmp_path / "sensor.png", {"carla:sensor": SENSOR_POSE})
    assert CaptureMetadataValidator().check_png(image) == []
    assert "carla:sensor" in CaptureMetadataValidator().read_text_chunks(image)


def test_a_field_hidden_in_a_compressed_chunk_is_still_found(tmp_path):
    """A gate that only understood tEXt could be walked past by writing zTXt instead."""
    image = _png(tmp_path / "compressed.png", {"carla:capture": SHIPPED_CAPTURE},
                 compress_text=True)
    assert _fields(CaptureMetadataValidator().check_png(image)) == {"scenario_id", "seed"}


def test_an_unrecognised_chunk_is_reported(tmp_path):
    """The allow-list is a list of what may be there, not of what may not."""
    image = _png(tmp_path / "extra.png", {"carla:solar": SOLAR_STATE,
                                          "carla:supervision": '{"guard_no_show":true}'})
    findings = CaptureMetadataValidator().check_png(image)
    assert [finding.keyword for finding in findings] == ["carla:supervision"]
    assert "unrecognised metadata chunk" in str(findings[0])


def test_a_tree_of_images_is_checked_together(tmp_path):
    _png(tmp_path / "clean.png", {"carla:solar": SOLAR_STATE})
    _png(tmp_path / "leaking.png", {"carla:solar": SHIPPED_SOLAR})
    findings = CaptureMetadataValidator().check_tree(tmp_path)
    assert [path.name for path in CaptureMetadataValidator.images(findings)] == ["leaking.png"]


@pytest.mark.skipif(not _shipped_images(),
                    reason="no captures under Build/SCTMV_recordings; set CARLA_CAPTURE_IMAGE "
                           "to check one")
def test_a_capture_written_before_the_split_is_rejected_unmodified():
    """The gate against a real file, untouched: a check that has never rejected one is not a check.

    Measured across the 54 captures on the machine this was written on: every one of them carries
    `advancing`, `rate` and `seed`, and none carries `scenario_id`, which was dormant because nothing
    passed it -- which is why supplying it and stripping the chunk had to be one change.
    """
    image = _shipped_images()[0]
    findings = CaptureMetadataValidator().check_png(image)
    assert findings, f"{image.name} was expected to carry the sun policy and the run's seed"
    assert _fields(findings) >= {"advancing", "rate", "seed"}, (
        CaptureMetadataValidator.describe(findings))


@pytest.mark.skipif(not _shipped_images(), reason="no captures under Build/SCTMV_recordings")
def test_every_shipped_capture_carries_the_same_leak():
    """Named so a re-issue knows what it is re-issuing: this is a property of the whole set."""
    validator = CaptureMetadataValidator()
    leaking = [image for image in _shipped_images() if validator.check_png(image)]
    assert leaking == _shipped_images()
