"""A free view is told when the render region is smaller than the world, and given the one that is not.

A SUMO drive limited to a circle -- an optional performance control; by default every vehicle SUMO
has is drawn -- renders vehicles only inside it, in SUMO's frame, and a free camera can be flown
anywhere over the world's sandbox (`12_Operator_Control_Surface.md` section 9.6). The
sandbox is recorded in CARLA's frame in the world package, whose northing runs the other way, so
the conversion is checked against the shipped packages' own SUMO networks where they are on disk.
"""
from __future__ import annotations

import re
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.RenderRegionCoverage import RenderRegionCoverage  # noqa: E402

# Gardnerville's world.json, as its world build wrote it (Build/world-packages, 2026-09-28).
GARDNERVILLE = {
    "StagingMinXMeters": -838.9031372070312, "StagingMinYMeters": -455.0901184082031,
    "StagingMaxXMeters": 839.0968627929688, "StagingMaxYMeters": 456.9098815917969,
    "GridMinXMeters": -838.9031372070312, "GridMinYMeters": -455.0901184082031,
    "GridCellSizeMeters": 2, "GridNumCols": 840, "GridNumRows": 457,
}

PACKAGES = sorted((_REPO / "Build" / "world-packages").glob("*.cwp"))


def test_the_sandbox_is_taken_into_sumos_frame_with_the_northing_negated():
    coverage = RenderRegionCoverage.from_manifest(GARDNERVILLE)

    assert coverage == RenderRegionCoverage(min_x=-838.9031372070312, min_y=-456.9098815917969,
                                            max_x=839.0968627929688, max_y=455.0901184082031)


def test_the_whole_map_region_is_the_circle_through_the_sandboxs_corners():
    coverage = RenderRegionCoverage.from_manifest(GARDNERVILLE)

    centre_x, centre_y, radius = coverage.whole_map()

    assert (centre_x, centre_y, radius) == (0.0, -1.0, 956.0)
    assert coverage.covers(centre_x, centre_y, radius)
    assert not coverage.covers(centre_x, centre_y, radius - 1.0)
    assert coverage.advice(centre_x, centre_y, radius, capacity=None) is None


def test_a_smaller_region_is_said_with_the_arguments_that_take_in_the_whole_map():
    coverage = RenderRegionCoverage.from_manifest(GARDNERVILLE)

    advice = coverage.advice(0.0, 0.0, 400.0, capacity=None)

    assert advice is not None
    assert "fixed circle of 400 m around (0, 0)" in advice
    assert "reaches 955 m from that centre" in advice
    assert "sees roads with no vehicles on them" in advice
    assert "--region-x 0 --region-y -1 --region-radius 956" in advice
    assert "leave --render-set at 'all', which draws every vehicle SUMO has" in advice
    assert "--capacity" not in advice
    assert "--capacity (96) still bounds" in coverage.advice(0.0, 0.0, 400.0, capacity=96)


def test_the_grid_stands_in_for_a_package_that_records_no_sandbox():
    grid_only = {key: value for key, value in GARDNERVILLE.items() if not key.startswith("Staging")}

    coverage = RenderRegionCoverage.from_manifest(grid_only)

    assert coverage.min_x == pytest.approx(-838.9031372070312)
    assert coverage.max_x == pytest.approx(-838.9031372070312 + 2 * 839)
    assert coverage.min_y == pytest.approx(455.0901184082031 - 2 * 456)
    assert coverage.max_y == pytest.approx(455.0901184082031)


def test_a_package_recording_no_extent_is_not_checked():
    assert RenderRegionCoverage.from_manifest({"MapName": "Town10HD"}) is None


@pytest.mark.skipif(not PACKAGES, reason="no world package under Build/world-packages")
@pytest.mark.parametrize("package", PACKAGES, ids=[path.stem for path in PACKAGES])
def test_the_whole_map_region_takes_in_the_packages_own_sumo_network(package):
    import json

    with zipfile.ZipFile(package) as archive:
        manifest = json.loads(archive.read("world.json"))
        with archive.open("map.net.xml") as network:
            head = network.read(16384).decode("utf-8", "replace")
    boundary = re.search(r'convBoundary="([^"]+)"', head)
    assert boundary, f"{package.name}: no convBoundary in the network's head"
    min_x, min_y, max_x, max_y = (float(value) for value in boundary.group(1).split(","))

    coverage = RenderRegionCoverage.from_manifest(manifest)
    centre_x, centre_y, radius = coverage.whole_map()

    # Every road SUMO has, in its own frame, is inside the region offered for the whole map.
    for x in (min_x, max_x):
        for y in (min_y, max_y):
            assert ((x - centre_x) ** 2 + (y - centre_y) ** 2) ** 0.5 <= radius, (x, y)
