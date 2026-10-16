"""`carla-validate` checks the files that live beside a capture as well as the capture's own.

A run's records, a compiled scenario's files and the inputs a user writes each have a published schema
(`RecordValidator`). `carla-validate` given the folder holding them checks each, reports it as its own
kind, and exits 1 naming the file, where in it and what is wrong when one breaks its schema.
"""
from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.commands.validate import main  # noqa: E402

IMPORT = _REPO / "Import"
SCENARIO = "Arapahoe_I25_SupervisionCheck"


def run(folder: Path, caplog) -> tuple[int, str]:
    caplog.clear()
    with caplog.at_level("INFO"):
        status = main([str(folder)])
    return status, caplog.text


def compiled(tmp_path: Path) -> Path:
    folder = tmp_path / SCENARIO
    folder.mkdir()
    for suffix in (".lock.json", ".resolution.json", ".supervision.json", ".scenario.json",
                   ".run.json"):
        shutil.copy(IMPORT / f"{SCENARIO}{suffix}", folder)
    return folder


def test_a_compiled_scenario_s_files_keep_their_schemas(tmp_path, caplog):
    status, text = run(compiled(tmp_path), caplog)
    assert status == 0, text
    for kind in ("scenario locks", "scenario resolution reports", "supervision plans",
                 "scenario specifications", "run configurations"):
        assert f"{kind}" in text and "1 checked, 0 failed" in text
    assert "every file keeps its schema" in text


def test_a_record_that_breaks_its_schema_fails_naming_the_file_and_where(tmp_path, caplog):
    folder = compiled(tmp_path)
    lock = folder / f"{SCENARIO}.lock.json"
    document = json.loads(lock.read_text(encoding="utf-8"))
    document["traffic"]["sumo_seed"] = "forty-two"
    lock.write_text(json.dumps(document), encoding="utf-8")
    status, text = run(folder, caplog)
    assert status == 1
    assert f"FAILED: {SCENARIO}.lock.json: $.traffic.sumo_seed: must be integer" in text
