"""A vehicle's bare-earth height must come from the ground under it, not the ground mirrored.

The grid is built in the CARLA world frame, where north is -y; SUMO reports the projection's own
metres, where north is +y. Feeding one to the other reads the row mirrored about the map's centre
line, and the number that comes back is a real height measured somewhere else, so nothing about it
looks wrong.

The check is a road: a generated world's `.xodr` carries an elevation profile that was fitted to the
same terrain the grid holds, so sampling the profile and differencing it against the grid gives a
residual that collapses to the road deck's offset above bare earth when the frames agree and does
not when they do not. That makes the frame measurable rather than arguable, and the gate is on the
spread of the residual rather than its mean, since the mean is the deck.

A world is built here so the check runs anywhere, and any generated world packages found under
`Build/world-packages` are checked as well -- they are build output rather than repository files, so
they may not be there. `CARLA_WORLD_PACKAGE` names one `.cwp` instead of looking for them.
"""
from __future__ import annotations

import math
import os
import statistics
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SumoCotBridge import BareEarthGrid, SumoCotBridge  # noqa: E402

# The gate the plan sets on the road-profile residual, and the world it was measured on: 8,132
# points at 5 m spacing give 0.632 m under CARLA-frame indexing against 13.038 m under SUMO's.
RESIDUAL_STDEV_LIMIT_M = 1.5
MEASURED_WORLD_NAME = "Arapahoe_I25"
SAMPLE_SPACING_M = 5.0

# How much looser the mirrored lookup has to be than the correct one before a world counts as
# evidence about the frame. A map whose roads happen to run east-west through the middle would show
# less; every map measured so far shows several times this.
MIRRORED_SPREAD_RATIO = 3.0


class _TerrainField:
    """A closed-form bare-earth surface, in the CARLA world frame.

    Deliberately asymmetric about y: a surface that mirrored onto itself would let the defect this
    checks for pass unnoticed, which is the whole reason the real map's residual had to be measured
    rather than assumed.
    """

    BASE_HEIGHT_M = 1700.0

    def height(self, x: float, y: float) -> float:
        return (self.BASE_HEIGHT_M + 0.030 * x - 0.060 * y
                + 2.0 * math.sin(x / 70.0) + 1.5 * math.cos(y / 55.0))


class _SyntheticWorld:
    """A bare-earth grid and an OpenDRIVE profile built from one terrain field, as a world is.

    The grid is written in the CARLA frame the generator uses; the roads are written in the
    projected frame netconvert emits, where north is +y. Everything else about the two files is what
    a real world package holds, including the road deck sitting a fixed height above bare earth.
    """

    EXTENT_M = 400.0
    CELL_SIZE_M = 2.0
    DECK_HEIGHT_M = 0.35
    ORIGIN_LAT, ORIGIN_LON = 39.59431, -104.88449
    ORIGIN_HEIGHT_M = 1747.4

    def __init__(self, directory: Path):
        self.directory = directory
        self.field = _TerrainField()
        self.min_x = self.min_y = -self.EXTENT_M
        self.columns = self.rows = int(2 * self.EXTENT_M / self.CELL_SIZE_M) + 1
        self.grid_path = directory / "bareearth.bin"
        self.xodr_path = directory / "map.xodr"
        self._write_grid()
        self._write_roads()

    def _write_grid(self) -> None:
        heights = []
        for row in range(self.rows):
            y = self.min_y + row * self.CELL_SIZE_M
            for col in range(self.columns):
                x = self.min_x + col * self.CELL_SIZE_M
                heights.append(self.field.height(x, y))
        header = struct.pack(BareEarthGrid.HEADER, BareEarthGrid.MAGIC,
                             self.ORIGIN_LAT, self.ORIGIN_LON, self.ORIGIN_HEIGHT_M,
                             self.min_x, self.min_y, self.CELL_SIZE_M, self.columns, self.rows)
        count = self.columns * self.rows
        # The drape-offset plane comes first and is not what this reads; the bare-earth plane
        # follows it. Both are written so the file is the shape the reader expects.
        self.grid_path.write_bytes(header
                                   + struct.pack(f"<{count}f", *([0.0] * count))
                                   + struct.pack(f"<{count}f", *heights))

    def _write_roads(self) -> None:
        """Twelve straight roads across the map, each carrying the terrain under it plus the deck."""
        roads = []
        span = 700.0
        for index, offset in enumerate((-300.0, -180.0, -60.0, 60.0, 180.0, 300.0)):
            roads.append(self._road(f"{index}0", -span / 2, offset, 0.0, span))
            roads.append(self._road(f"{index}1", offset, -span / 2, math.pi / 2, span))
        self.xodr_path.write_text(
            '<?xml version="1.0" standalone="yes"?>\n<OpenDRIVE>\n'
            f'  <header revMajor="1" revMinor="4" north="{self.EXTENT_M}" '
            f'south="{-self.EXTENT_M}" east="{self.EXTENT_M}" west="{-self.EXTENT_M}"/>\n'
            + "\n".join(roads) + "\n</OpenDRIVE>\n", encoding="utf-8")

    def _road(self, road_id: str, x: float, y: float, heading: float, length: float) -> str:
        """One straight road, its elevation profile sampled from the field along its own course.

        The profile is a step per sample rather than a fitted cubic, which is what keeps this
        fixture about the coordinate frame and not about how well a polynomial follows terrain.
        """
        records = []
        station = 0.0
        while station <= length:
            px = x + station * math.cos(heading)
            py = y + station * math.sin(heading)
            # The roads are in the projected frame; the field is in CARLA's.
            height = self.field.height(px, -py) + self.DECK_HEIGHT_M
            records.append(f'      <elevation s="{station:.8f}" '
                           f'a="{height - self.ORIGIN_HEIGHT_M:.8f}" b="0" c="0" d="0"/>')
            station += SAMPLE_SPACING_M
        return (f'  <road name="road_{road_id}" length="{length:.8f}" id="{road_id}" '
                f'junction="-1">\n'
                f'    <planView>\n'
                f'      <geometry s="0.00000000" x="{x:.8f}" y="{y:.8f}" hdg="{heading:.8f}" '
                f'length="{length:.8f}">\n        <line/>\n      </geometry>\n'
                f'    </planView>\n'
                f'    <elevationProfile>\n' + "\n".join(records) + "\n"
                '    </elevationProfile>\n  </road>')


class _RoadProfileSampler:
    """Points along a generated world's roads: projected-frame position and ellipsoidal height.

    Reads the two geometry kinds netconvert writes -- straight lines and parametric cubics -- and
    the cubic elevation profile, evaluating both at a fixed spacing along each road.
    """

    def __init__(self, origin_height_m: float, spacing_m: float = SAMPLE_SPACING_M):
        self.origin_height_m = origin_height_m
        self.spacing_m = spacing_m

    def sample(self, xodr: str) -> list[tuple[float, float, float]]:
        points = []
        for road in ET.fromstring(xodr).iter("road"):
            profile = road.find("elevationProfile")
            plan = road.find("planView")
            if profile is None or plan is None:
                continue
            geometries = plan.findall("geometry")
            elevations = [(float(e.get("s")), (float(e.get("a")), float(e.get("b")),
                                               float(e.get("c")), float(e.get("d"))))
                          for e in profile.findall("elevation")]
            if not geometries or not elevations:
                continue
            length = float(road.get("length"))
            station = 0.0
            while station <= length:
                placed = self._position(geometries, station)
                if placed is None:
                    break
                x, y = placed
                start, coefficients = self._active(elevations, station)
                height = self.origin_height_m + self._cubic(coefficients, station - start)
                points.append((x, y, height))
                station += self.spacing_m
        return points

    @staticmethod
    def _cubic(coefficients: tuple[float, float, float, float], ds: float) -> float:
        a, b, c, d = coefficients
        return a + b * ds + c * ds * ds + d * ds ** 3

    @staticmethod
    def _active(records, station: float):
        chosen = records[0]
        for record in records:
            if record[0] <= station + 1e-9:
                chosen = record
            else:
                break
        return chosen

    def _position(self, geometries, station: float) -> tuple[float, float] | None:
        chosen = None
        for geometry in geometries:
            if float(geometry.get("s")) <= station + 1e-9:
                chosen = geometry
            else:
                break
        if chosen is None:
            return None
        start = float(chosen.get("s"))
        x, y = float(chosen.get("x")), float(chosen.get("y"))
        heading, length = float(chosen.get("hdg")), float(chosen.get("length"))
        ds = station - start
        if chosen.find("line") is not None:
            return (x + ds * math.cos(heading), y + ds * math.sin(heading))
        curve = chosen.find("paramPoly3")
        if curve is None:
            return None
        p = ds / length if curve.get("pRange") == "normalized" else ds
        u = self._cubic(tuple(float(curve.get(name)) for name in ("aU", "bU", "cU", "dU")), p)
        v = self._cubic(tuple(float(curve.get(name)) for name in ("aV", "bV", "cV", "dV")), p)
        return (x + u * math.cos(heading) - v * math.sin(heading),
                y + u * math.sin(heading) + v * math.cos(heading))


def _residual_stdev(grid: BareEarthGrid, points, negate_y: bool) -> tuple[float, int]:
    """Spread of road height minus grid height, and how many points the grid could answer."""
    residuals = []
    for x, y, height in points:
        under = grid.height_at(x, -y if negate_y else y)
        if under is not None:
            residuals.append(height - under)
    if len(residuals) < 2:
        return (float("inf"), len(residuals))
    return (statistics.stdev(residuals), len(residuals))


@pytest.fixture(scope="module")
def synthetic(tmp_path_factory) -> _SyntheticWorld:
    return _SyntheticWorld(tmp_path_factory.mktemp("world"))


@pytest.fixture(scope="module")
def synthetic_grid(synthetic) -> BareEarthGrid:
    return BareEarthGrid.from_file(synthetic.grid_path)


def test_height_outside_the_grid_has_no_answer(synthetic_grid):
    """An edge cell is a height from somewhere else, so the reader refuses rather than clamps."""
    inside = synthetic_grid.height_at(0.0, 0.0)
    assert inside is not None
    far = _SyntheticWorld.EXTENT_M + 10.0
    for x, y in ((-far, 0.0), (far, 0.0), (0.0, -far), (0.0, far), (-far, far)):
        assert synthetic_grid.height_at(x, y) is None


def test_a_vehicle_off_the_grid_reports_the_stated_constant(synthetic_grid):
    """The fallback is the configured height, counted, rather than the rim's height unannounced."""
    bridge = SumoCotBridge(installation=None, config_path="unused.sumocfg",
                           bare_earth=synthetic_grid, constant_hae=12.5)
    assert bridge._height_at(0.0, 0.0) != 12.5
    assert bridge.off_grid_heights == 0
    assert bridge._height_at(0.0, _SyntheticWorld.EXTENT_M + 500.0) == 12.5
    assert bridge.off_grid_heights == 1


def test_the_road_profile_agrees_with_the_grid_under_carla_frame_indexing(synthetic, synthetic_grid):
    points = _RoadProfileSampler(_SyntheticWorld.ORIGIN_HEIGHT_M).sample(
        synthetic.xodr_path.read_text(encoding="utf-8"))
    assert len(points) > 1000

    stdev, answered = _residual_stdev(synthetic_grid, points, negate_y=True)
    assert answered == len(points)
    assert stdev < RESIDUAL_STDEV_LIMIT_M


def test_the_same_check_rejects_the_unnegated_lookup(synthetic, synthetic_grid):
    """Feeding SUMO's y straight in must fail the gate, or the gate is not measuring the frame."""
    points = _RoadProfileSampler(_SyntheticWorld.ORIGIN_HEIGHT_M).sample(
        synthetic.xodr_path.read_text(encoding="utf-8"))
    stdev, _ = _residual_stdev(synthetic_grid, points, negate_y=False)
    assert stdev > RESIDUAL_STDEV_LIMIT_M


def _world_packages() -> list[Path]:
    """Generated worlds to check, if any are at hand. They are build output, not repository files."""
    named = os.environ.get("CARLA_WORLD_PACKAGE")
    if named:
        return [Path(named)]
    return sorted((_REPO / "Build" / "world-packages").glob("*.cwp"))


def _measured_world() -> Path | None:
    """The world the plan's residual figures were measured on, when it is present.

    Only that world: the absolute limit is a statement about its terrain as much as about the
    lookup, so applying it to a different map would be asserting something nobody measured.
    """
    return next((p for p in _world_packages() if p.stem == MEASURED_WORLD_NAME), None)


def _load(package: Path) -> tuple[BareEarthGrid, list[tuple[float, float, float]]]:
    with zipfile.ZipFile(package) as archive:
        grid = BareEarthGrid.from_file(package)
        xodr = archive.read("map.xodr").decode("utf-8", errors="replace")
    return grid, _RoadProfileSampler(grid.origin_height).sample(xodr)


@pytest.mark.skipif(_measured_world() is None,
                    reason=f"no {MEASURED_WORLD_NAME} world package; set CARLA_WORLD_PACKAGE "
                           "to check another")
def test_the_measured_world_agrees_with_its_own_grid():
    """The plan's gate, on the world the plan measured: 0.63 m against a limit of 1.5 m."""
    package = _measured_world()
    grid, points = _load(package)
    assert len(points) > 1000

    stdev, answered = _residual_stdev(grid, points, negate_y=True)
    assert answered > 0.95 * len(points)
    assert stdev < RESIDUAL_STDEV_LIMIT_M, f"{package.name}: residual stdev {stdev:.3f} m"


@pytest.mark.skipif(not _world_packages(),
                    reason="no generated world packages; set CARLA_WORLD_PACKAGE to check one")
@pytest.mark.parametrize("package", _world_packages(), ids=lambda p: p.stem)
def test_a_generated_world_reads_the_ground_under_its_roads_not_the_mirror(package: Path):
    """The frame, on whatever worlds are at hand, without assuming their terrain.

    How tight the residual gets is a property of the map: Arapahoe's roads sit 0.63 m from bare
    earth and Shahid Bahonar's 1.91 m, the port being quays and a drydock at sea level rather than
    a suburban arterial. How much *looser* the mirrored lookup is, is a property of the code, and
    that is what this gates -- 20.6x on Arapahoe, 3.7x on Shahid Bahonar.
    """
    grid, points = _load(package)
    assert len(points) > 1000

    correct, answered = _residual_stdev(grid, points, negate_y=True)
    mirrored, _ = _residual_stdev(grid, points, negate_y=False)
    assert answered > 0.95 * len(points)
    assert correct * MIRRORED_SPREAD_RATIO < mirrored, (
        f"{package.name}: {correct:.3f} m in the CARLA frame against {mirrored:.3f} m in SUMO's")
