"""Patching a dataset's heights must repair the file it was made from, and refuse every other one.

A forward patch is only safe because the thing being repaired can be recognised. The check is that
the old lookup, reproduced exactly, returns what each row already holds: a dataset written from this
grid reproduces itself, and a dataset from another world, or one already patched, does not. Both
directions are asserted here, because a gate that has only ever been shown accepting is not a gate.
"""
from __future__ import annotations

import csv
import struct
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SumoCotBridge import BareEarthGrid  # noqa: E402
from carlacontrol.TelemetryHeightPatcher import TelemetryHeightPatcher  # noqa: E402

CELL_SIZE_M = 2.0
COLUMNS = ROWS = 201
MIN_X = MIN_Y = -200.0
ORIGIN_HEIGHT_M = 1500.0

CSV_COLUMNS = ["time_utc", "uid", "callsign", "cot_type", "lat", "lon", "hae_m",
               "sumo_x", "sumo_y", "carla_x", "carla_y"]


def _terrain(x: float, y: float, tilt: float = 0.05) -> float:
    """A surface that is not symmetric in y, so reading the mirrored row is detectable."""
    return 1400.0 + 0.02 * x + tilt * y


def _write_grid(path: Path, tilt: float = 0.05) -> None:
    heights = [_terrain(MIN_X + col * CELL_SIZE_M, MIN_Y + row * CELL_SIZE_M, tilt)
               for row in range(ROWS) for col in range(COLUMNS)]
    header = struct.pack(BareEarthGrid.HEADER, BareEarthGrid.MAGIC, 39.0, -105.0, ORIGIN_HEIGHT_M,
                         MIN_X, MIN_Y, CELL_SIZE_M, COLUMNS, ROWS)
    count = COLUMNS * ROWS
    path.write_bytes(header + struct.pack(f"<{count}f", *([0.0] * count))
                     + struct.pack(f"<{count}f", *heights))


def _positions() -> list[tuple[str, float, float]]:
    """A few hundred vehicle positions spread over the grid, well clear of cell boundaries."""
    out = []
    for index in range(300):
        x = -180.0 + (index * 1.19) % 360.0
        y = -180.0 + (index * 2.31) % 360.0
        out.append((f"SUMO-TRUTH-v{index % 7}", round(x, 2), round(y, 2)))
    return out


def _write_dataset(directory: Path, patcher: TelemetryHeightPatcher,
                   positions, corrected: bool = False) -> tuple[Path, Path]:
    """Write a CSV and its matching event stream, with heights from one lookup or the other."""
    csv_path, xml_path = directory / "corpus.csv", directory / "corpus.xml"
    with open(csv_path, "w", newline="", encoding="utf-8") as handle, \
            open(xml_path, "w", encoding="utf-8", newline="") as events:
        writer = csv.DictWriter(handle, fieldnames=CSV_COLUMNS)
        writer.writeheader()
        events.write('<?xml version="1.0" encoding="UTF-8"?>\n<events source="sumo">\n')
        for uid, x, y in positions:
            height = (patcher.corrected_height(x, y) if corrected
                      else patcher.recorded_height(x, y))
            writer.writerow({
                "time_utc": "2026-01-01T00:00:00.000Z", "uid": uid, "callsign": f"car-{uid}",
                "cot_type": "a-n-G-E-V", "lat": "39.0000000", "lon": "-105.0000000",
                "hae_m": f"{height:.2f}", "sumo_x": f"{x:.2f}", "sumo_y": f"{y:.2f}",
                "carla_x": f"{x:.2f}", "carla_y": f"{-y:.2f}"})
            events.write(f'  <event version="2.0" uid="{uid}" type="a-n-G-E-V">'
                         f'<point lat="39.0000000" lon="-105.0000000" hae="{height:.2f}" '
                         f'ce="0.0" le="0.0" /><detail /></event>\n')
        events.write("</events>\n")
    return csv_path, xml_path


@pytest.fixture
def world(tmp_path):
    grid_path = tmp_path / "bareearth.bin"
    _write_grid(grid_path)
    grid = BareEarthGrid.from_file(grid_path)
    patcher = TelemetryHeightPatcher(grid)
    csv_path, xml_path = _write_dataset(tmp_path, patcher, _positions())
    return patcher, csv_path, xml_path, tmp_path


def test_the_dataset_being_repaired_is_recognised(world):
    patcher, csv_path, _, _ = world
    report = patcher.verify_csv(csv_path)
    assert report.records == 300
    assert report.verified == 300
    assert report.worst_verification_m <= patcher.tolerance_m


def test_patching_writes_the_height_under_the_vehicle(world):
    patcher, csv_path, _, tmp_path = world
    patched = tmp_path / "patched.csv"
    report = patcher.patch_csv(csv_path, patched)
    assert report.rewritten == 300

    with open(patched, newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    assert len(rows) == 300
    for row in rows:
        x, y = float(row["sumo_x"]), float(row["sumo_y"])
        assert float(row["hae_m"]) == pytest.approx(patcher.corrected_height(x, y), abs=0.005)
    # Everything but the height is carried across untouched.
    with open(csv_path, newline="", encoding="utf-8") as handle:
        originals = list(csv.DictReader(handle))
    for before, after in zip(originals, rows, strict=True):
        assert {k: v for k, v in before.items() if k != "hae_m"} == \
               {k: v for k, v in after.items() if k != "hae_m"}


def test_the_correction_is_the_mirror_it_was_meant_to_undo(world):
    """The heights move by twice the terrain's tilt about the centre line, not by a little."""
    patcher, csv_path, _, _ = world
    report = patcher.verify_csv(csv_path)
    assert max(abs(value) for value in report.corrections_m) > 10.0


def test_a_patched_dataset_is_refused_a_second_time(world):
    patcher, csv_path, _, tmp_path = world
    patched = tmp_path / "patched.csv"
    patcher.patch_csv(csv_path, patched)
    with pytest.raises(ValueError, match="already been patched"):
        patcher.patch_csv(patched, tmp_path / "twice.csv")


def test_a_dataset_from_another_world_is_refused(tmp_path):
    """The same map shape, a different terrain: the rows no longer reproduce themselves."""
    _write_grid(tmp_path / "ours.bin", tilt=0.05)
    _write_grid(tmp_path / "theirs.bin", tilt=-0.03)
    theirs = TelemetryHeightPatcher(BareEarthGrid.from_file(tmp_path / "theirs.bin"))
    csv_path, _ = _write_dataset(tmp_path, theirs, _positions())

    ours = TelemetryHeightPatcher(BareEarthGrid.from_file(tmp_path / "ours.bin"))
    with pytest.raises(ValueError, match="not produced by this grid"):
        ours.patch_csv(csv_path, tmp_path / "wrong.csv")


def test_a_dataset_without_coordinates_cannot_be_patched(world, tmp_path):
    patcher, csv_path, _, _ = world
    stripped = tmp_path / "stripped.csv"
    with open(csv_path, newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    with open(stripped, "w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=[c for c in CSV_COLUMNS if c != "sumo_x"])
        writer.writeheader()
        for row in rows:
            row.pop("sumo_x")
            writer.writerow(row)
    with pytest.raises(ValueError, match="sumo_x"):
        patcher.verify_csv(stripped)


def test_the_event_stream_is_patched_against_its_own_csv(world):
    patcher, csv_path, xml_path, tmp_path = world
    patched = tmp_path / "patched.xml"
    report = patcher.patch_xml(xml_path, csv_path, patched)
    assert report.records == 300
    assert report.rewritten == 300

    heights = [float(line.split('hae="')[1].split('"')[0])
               for line in patched.read_text(encoding="utf-8").splitlines() if "<event " in line]
    with open(csv_path, newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    for height, row in zip(heights, rows, strict=True):
        expected = patcher.corrected_height(float(row["sumo_x"]), float(row["sumo_y"]))
        assert height == pytest.approx(expected, abs=0.005)
    # The rest of each event is untouched.
    assert patched.read_text(encoding="utf-8").count("<event ") == 300
    assert patched.read_text(encoding="utf-8").startswith('<?xml version="1.0"')


def test_an_event_stream_from_a_different_run_is_refused(world, tmp_path):
    """The two files are walked together, so a mismatched pair is caught at the first record."""
    patcher, csv_path, _, _ = world
    other = tmp_path / "other"
    other.mkdir()
    shifted = [(uid.replace("v", "w"), x, y) for uid, x, y in _positions()]
    _, other_xml = _write_dataset(other, patcher, shifted)
    with pytest.raises(ValueError, match="not from the same run"):
        patcher.patch_xml(other_xml, csv_path, tmp_path / "mismatched.xml")


def test_a_vehicle_outside_the_grid_keeps_its_recorded_height(tmp_path):
    """There is nothing truer to write there, so the row is left alone and counted."""
    _write_grid(tmp_path / "bareearth.bin")
    patcher = TelemetryHeightPatcher(BareEarthGrid.from_file(tmp_path / "bareearth.bin"))
    positions = _positions()[:290] + [(f"SUMO-TRUTH-out{i}", 0.0, -900.0 - i) for i in range(10)]
    csv_path, _ = _write_dataset(tmp_path, patcher, positions)

    patched = tmp_path / "patched.csv"
    report = patcher.patch_csv(csv_path, patched)
    assert report.off_grid == 10
    assert report.rewritten == 290

    with open(csv_path, newline="", encoding="utf-8") as handle:
        before = list(csv.DictReader(handle))
    with open(patched, newline="", encoding="utf-8") as handle:
        after = list(csv.DictReader(handle))
    assert [row["hae_m"] for row in before[290:]] == [row["hae_m"] for row in after[290:]]
