"""Clip an OSM file's geometry to a lat/lon box, cutting ways exactly at the boundary.

This is the older entry point, kept because `test_digital_twin.py` and the distribution's scripts
call it by name. **The clip itself lives in `carlacontrol.OsmClipper`** and this file only forwards
to it, because for a while there were two copies of the algorithm and only one of them was being
maintained: the production world build went through `carlacontrol.WorldBuilder`, so the fixes for
clip determinism and for carrying relations (issue #12) landed there, and anything still calling
this module kept getting a clip that varied per process and dropped every turn restriction.

Runnable standalone:
    python osm_clip.py <in.osm> <out.osm> [minlat minlon maxlat maxlon]
(omit the box to use the file's own <bounds>).
"""
import sys
from pathlib import Path

_CARLACONTROL_SOURCE = Path(__file__).resolve().parents[2] / "CarlaControl" / "src"
if _CARLACONTROL_SOURCE.is_dir():
    sys.path.insert(0, str(_CARLACONTROL_SOURCE))

try:
    from carlacontrol.OsmClipper import BoundingBox, OsmClipper
except ImportError as error:  # pragma: no cover - a clear failure beats a second implementation
    raise ImportError(
        "the OSM clip lives in carlacontrol.OsmClipper, which is not importable from here. "
        f"Looked for it in {_CARLACONTROL_SOURCE}. Install the carlacontrol package or run from a "
        "checkout."
    ) from error


def read_bounds(in_path):
    """The (minlat, minlon, maxlat, maxlon) of the file's own <bounds>, or None."""
    bounds = OsmClipper.read_bounds(in_path)
    if bounds is None:
        return None
    return bounds.min_lat, bounds.min_lon, bounds.max_lat, bounds.max_lon


def clip_osm_to_bounds(in_path, out_path, b):
    """Clip `in_path` into `out_path`, returning (ways written, boundary nodes created).

    `carlacontrol.OsmClipper.clip_osm_to_bounds` returns a fuller summary -- relations carried,
    node-mapped features carried, runs renumbered. Call it directly for those.
    """
    if not isinstance(b, BoundingBox):
        b = BoundingBox.from_tuple(tuple(b))
    summary = OsmClipper.clip_osm_to_bounds(in_path, out_path, b)
    return summary.ways, summary.boundary_nodes


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    src, dst = sys.argv[1], sys.argv[2]
    box = (tuple(float(v) for v in sys.argv[3:7]) if len(sys.argv) >= 7 else read_bounds(src))
    if box is None:
        print("ERROR: no box given and no <bounds> in the OSM file", file=sys.stderr)
        sys.exit(1)
    nw, nn = clip_osm_to_bounds(src, dst, box)
    print(f"clipped -> {dst}: {nw} ways, {nn} new boundary nodes; box={box}")
