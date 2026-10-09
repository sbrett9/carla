"""`carla-validate` checks every file of a capture folder against its published schema and says which fail.

A capture folder is built here from recorded files -- the truth of a real run against a real SUMO, and
two stills from a capture of 2026-10-07 -- and checked whole: every file keeps its schema and the command
exits 0. Then one file at a time is broken the way a writer that drifted from its schema would break it,
and the command exits 1 naming the file, where in it, and what is wrong. A file that declares a newer
format version is named as written by a newer release; a manifest with no closing row is noted as an
interrupted run and is not a failure; a folder with no file of a capture exits 2.

`CARLA_VALIDATE_CAPTURE`, naming a real capture folder, validates it too.
"""
from __future__ import annotations

import json
import os
import shutil
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.commands.validate import main  # noqa: E402
from carlacontrol.PngTextChunks import PngTextChunks  # noqa: E402

RECORDED = Path(__file__).resolve().parent / "fixtures" / "capture_schemas"
CAMERA = "Check_Overhead_1"
STILL = "Check_Overhead_1_2026.10.07_10.34.51.318"


@pytest.fixture
def capture(tmp_path) -> Path:
    """A capture folder: one camera's two stills, and the truth of a drawn run."""
    folder = tmp_path / "cap-test"
    shutil.copytree(RECORDED / "legacy", folder)
    shutil.copytree(RECORDED / "drawn", folder / "truth")
    return folder


def run(folder: Path, caplog, *extra: str) -> tuple[int, str]:
    caplog.clear()
    with caplog.at_level("INFO"):
        status = main([str(folder), *extra])
    return status, caplog.text


def test_a_capture_whose_every_file_keeps_its_schema_passes(capture, caplog):
    status, text = run(capture, caplog)
    assert status == 0, text
    assert "truth sidecars" in text and "2 checked, 0 failed" in text
    assert "every file keeps its schema" in text


def test_a_sidecar_with_a_word_its_writer_never_writes_fails_naming_the_file_and_the_line(capture, caplog):
    sidecar = capture / CAMERA / f"{STILL}.xml"
    sidecar.write_text(sidecar.read_text(encoding="utf-8").replace('in_frame="wholly"', 'in_frame="maybe"', 1),
                       encoding="utf-8")
    status, text = run(capture, caplog)
    assert status == 1
    assert f"{STILL}.xml: line " in text and "maybe" in text


def test_a_chunk_of_a_newer_format_version_is_named_as_written_by_a_newer_release(capture, caplog):
    still = capture / CAMERA / f"{STILL}.png"
    chunks = [(keyword, json.dumps({**json.loads(text), "format_version": 2}) if keyword == "carla:capture"
               else text) for keyword, text in PngTextChunks.read(still)]
    PngTextChunks.write(still, chunks)
    status, text = run(capture, caplog)
    assert status == 1
    assert "carla:capture format_version 2" in text and "newer release" in text


def test_a_still_without_its_capture_chunk_or_with_a_chunk_no_schema_describes_fails(capture, caplog):
    still = capture / CAMERA / f"{STILL}.png"
    chunks = [(keyword, text) for keyword, text in PngTextChunks.read(still) if keyword != "carla:capture"]
    PngTextChunks.write(still, [*chunks, ("carla:weather", "{}")])
    status, text = run(capture, caplog)
    assert status == 1
    assert "carries no carla:capture chunk" in text and "carla:weather: is a chunk no published schema" in text


def test_a_manifest_row_with_a_field_its_writer_never_writes_fails_naming_the_line(capture, caplog):
    manifest = capture / "truth" / "manifest.jsonl"
    lines = manifest.read_text(encoding="utf-8").splitlines()
    second = json.loads(lines[1])
    second["seen_by"] = "nobody"
    lines[1] = json.dumps(second)
    manifest.write_text("\n".join(lines) + "\n", encoding="utf-8")
    status, text = run(capture, caplog)
    assert status == 1
    assert f"line 2 ({second['row']})" in text and "seen_by" in text


def test_a_manifest_cut_off_before_its_closing_row_is_noted_as_interrupted_and_does_not_fail(capture, caplog):
    manifest = capture / "truth" / "manifest.jsonl"
    lines = manifest.read_text(encoding="utf-8").splitlines()
    # Cut off mid-row, as a run killed while writing leaves it.
    manifest.write_text("\n".join(lines[:-1]) + "\n" + lines[-1][:40], encoding="utf-8")
    status, text = run(capture, caplog)
    assert status == 0, text
    assert "its run was interrupted" in text and "its last line has no line break" in text


def test_a_track_row_with_a_word_its_writer_never_writes_or_a_wrong_count_fails(capture, caplog):
    track = capture / "truth" / "world_truth_track.csv"
    lines = track.read_text(encoding="utf-8").splitlines()
    lines[1] = lines[1].replace(",rendered,", ",drawn,", 1)
    track.write_text("\n".join(lines[:-1]) + "\n", encoding="utf-8")
    status, text = run(capture, caplog)
    assert status == 1
    assert "line 2, render_state: 'drawn' is not one of" in text
    assert "and its summary, written when the run ended, says" in text


def test_a_folder_with_no_file_of_a_capture_is_not_a_capture(tmp_path, caplog):
    status, text = run(tmp_path, caplog)
    assert status == 2
    assert "no file a published schema describes" in text


def test_an_xml_file_beside_a_still_is_its_sidecar_and_any_other_is_not_a_capture_s(capture, caplog):
    """A SUMO route file in a capture folder is nobody's sidecar; a sidecar broken into another root
    is still the still's, and fails as one."""
    (capture / "traffic.rou.xml").write_text("<routes/>\n", encoding="utf-8")
    status, text = run(capture, caplog)
    assert status == 0, text
    assert "truth sidecars" in text and "2 checked, 0 failed" in text
    (capture / CAMERA / f"{STILL}.xml").write_text("<routes/>\n", encoding="utf-8")
    status, text = run(capture, caplog)
    assert status == 1
    assert "is not a truth sidecar: its root is <routes>, not <events>" in text


def test_a_real_capture_named_by_the_environment_validates(caplog):
    named = os.environ.get("CARLA_VALIDATE_CAPTURE")
    if not named:
        pytest.skip("CARLA_VALIDATE_CAPTURE names no capture folder")
    status, text = run(Path(named), caplog)
    assert status == 0, text
