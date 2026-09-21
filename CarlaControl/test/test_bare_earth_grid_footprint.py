"""The bare-earth grid must be held as the numbers it is, not as seven million objects.

A port-sized world package carries 7.6 million cells: 30.4 MB of little-endian float32 on disk.
Reading them with `struct.unpack_from` produces a tuple of Python floats -- 60.9 MB of pointers plus
182.7 MB of float objects, 243.6 MB resident -- in a process that is also holding a simulation.
Nothing ever reads them as objects: the only access is one cell at a time through `height_at`, which
indexes and returns. So the boxing buys nothing.

Two things are asserted, and they are not the same thing. The footprint is the reason for the change
and is checked against a real package. That the heights are unchanged is what makes it safe, and is
checked cell by cell against the previous reading of the same bytes -- a height that silently moved
would be a height reported under a vehicle that was measured somewhere else, which is the failure the
frame test next door exists to catch and which no footprint measurement would notice.

Load time is deliberately not asserted. It was measured at 0.163 s before and 0.048 s after on a warm
cache, and two earlier runs of the same measurement disagreed by a factor of thirty, which is the
signature of file-cache state rather than of the code. The footprint is not in dispute; the load time
would need a cold-cache measurement nobody has made.

A real package is needed for the footprint check, and packages are build output rather than
repository files. `CARLA_BARE_EARTH_GRID` names one -- a loose `bareearth.bin` or a world package --
and otherwise any under `Build/world-packages` are used. The check skips rather than pretending when
there is none.
"""
from __future__ import annotations

import math
import os
import struct
import sys
import zipfile
from array import array
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SumoCotBridge import BareEarthGrid  # noqa: E402

# What a grid of this size may occupy, and what the boxed form of the same data would. The gap
# between them is the whole point: 32.3 MB measured against 243.6 MB on the 7.6 M-cell package the
# plan's figures come from.
RESIDENT_LIMIT_BYTES = 40_000_000
BOXED_FLOOR_BYTES = 200_000_000

# Below this the comparison says nothing: a small grid fits inside either representation.
LARGE_ENOUGH_CELLS = 1_000_000


def _write_grid(path: Path, columns: int, rows: int) -> list[float]:
    """A grid file in the shipped layout, with a height per cell that is unique to that cell."""
    heights = [round(100.0 + index * 0.25 + math.sin(index), 4) for index in range(columns * rows)]
    count = columns * rows
    header = struct.pack(BareEarthGrid.HEADER, BareEarthGrid.MAGIC,
                         27.1, 56.2, 3.4, -50.0, -75.0, 2.0, columns, rows)
    path.write_bytes(header
                     + struct.pack(f"<{count}f", *([0.0] * count))
                     + struct.pack(f"<{count}f", *heights))
    # What comes back is float32, so the comparison is against the file's precision, not the input's.
    return list(struct.unpack(f"<{count}f", struct.pack(f"<{count}f", *heights)))


def _boxed_bytes(sample: array, count: int) -> float:
    """What the same heights would occupy as a tuple of Python floats.

    Measured from a sample of the real values rather than assumed: the per-object size is an
    interpreter detail, and a figure asserted from memory would be the thing this is checking.
    """
    boxed = tuple(sample)
    per_element = sys.getsizeof(boxed[0])
    container_overhead = sys.getsizeof(())
    pointer = (sys.getsizeof(boxed) - container_overhead) / len(boxed)
    return container_overhead + count * (pointer + per_element)


def _raw_bytes(package: Path) -> bytes:
    """The grid file's bytes, from a loose file or from inside a world package."""
    if zipfile.is_zipfile(package):
        with zipfile.ZipFile(package) as archive:
            return archive.read(BareEarthGrid.PACKAGE_ENTRY)
    return package.read_bytes()


def _packages() -> list[Path]:
    """Real bare-earth grids to measure, if this machine has any."""
    named = os.environ.get("CARLA_BARE_EARTH_GRID")
    if named:
        return [Path(named)]
    packages = _REPO / "Build" / "world-packages"
    return sorted(list(packages.glob("*.bareearth.bin")) + list(packages.glob("*.cwp")))


def _large_package() -> Path | None:
    """The first package big enough for the footprint difference to be worth measuring."""
    for path in _packages():
        try:
            grid = BareEarthGrid.from_file(path)
        except (ValueError, OSError):
            continue
        if grid.columns * grid.rows >= LARGE_ENOUGH_CELLS:
            return path
    return None


@pytest.fixture(scope="module")
def synthetic(tmp_path_factory) -> tuple[BareEarthGrid, list[float]]:
    directory = tmp_path_factory.mktemp("grid")
    path = directory / "bareearth.bin"
    written = _write_grid(path, columns=41, rows=37)
    return BareEarthGrid.from_file(path), written


def test_the_heights_are_held_as_float32(synthetic):
    grid, _ = synthetic
    assert isinstance(grid.heights, array)
    assert grid.heights.typecode == "f"
    assert grid.heights.itemsize == 4


def test_every_cell_reads_what_the_boxed_form_read(synthetic):
    """Cell for cell, not in aggregate: a single moved height is a vehicle put on the wrong ground."""
    grid, written = synthetic
    assert list(grid.heights) == written

    for row in range(grid.rows):
        for column in range(grid.columns):
            x = grid.min_x + (column + 0.5) * grid.cell_size
            y = grid.min_y + (row + 0.5) * grid.cell_size
            assert grid.height_at(x, y) == written[row * grid.columns + column]


def test_indexing_outside_the_grid_still_has_no_answer(synthetic):
    """The refusal survives the change of container; a clamped edge cell is a height from the rim."""
    grid, _ = synthetic
    assert grid.height_at(grid.min_x - grid.cell_size, grid.min_y) is None
    assert grid.height_at(grid.min_x, grid.min_y + grid.rows * grid.cell_size) is None


@pytest.mark.skipif(_large_package() is None,
                    reason="no bare-earth grid of a million cells or more; set "
                           "CARLA_BARE_EARTH_GRID to measure one")
def test_a_real_package_fits_in_the_stated_footprint():
    """The reason for the change, on the kind of package that made it worth making."""
    package = _large_package()
    grid = BareEarthGrid.from_file(package)
    count = grid.columns * grid.rows

    resident = sys.getsizeof(grid.heights)
    boxed = _boxed_bytes(grid.heights[:1000], count)

    assert resident < RESIDENT_LIMIT_BYTES, (
        f"{package.name}: {count:,} cells occupy {resident / 1e6:.1f} MB")
    # And the form this replaced does not, or the limit above is measuring nothing.
    assert boxed > BOXED_FLOOR_BYTES, (
        f"{package.name}: the boxed form would be {boxed / 1e6:.1f} MB, which is not large enough "
        "for this comparison to be evidence")


@pytest.mark.skipif(_large_package() is None, reason="no bare-earth grid of a million cells or more")
def test_known_cells_of_a_real_package_read_what_they_read_before():
    """The same bytes, read the way they used to be read, at the corners and the middle."""
    package = _large_package()
    grid = BareEarthGrid.from_file(package)
    count = grid.columns * grid.rows
    raw = _raw_bytes(package)
    start = struct.calcsize(BareEarthGrid.HEADER) + 4 * count

    for index in (0, 1, count // 2, count - 2, count - 1):
        (expected,) = struct.unpack_from("<f", raw, start + 4 * index)
        assert grid.heights[index] == expected

        row, column = divmod(index, grid.columns)
        x = grid.min_x + (column + 0.5) * grid.cell_size
        y = grid.min_y + (row + 0.5) * grid.cell_size
        assert grid.height_at(x, y) == expected
