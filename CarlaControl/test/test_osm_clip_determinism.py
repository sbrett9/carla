"""Clipping the same OSM extract twice must produce the same bytes, not merely the same graph.

`OsmClipper` collects the original node ids a clip keeps in a `set` and used to emit them in
set-iteration order. Python randomises the string hash seed once per process, so that order is a
property of the process rather than of the input: four clips of one extract gave four different
files holding one identical graph. Everything that identifies an extract by digest depends on this
-- the world package's `SourceOsmSha256`, any build cache keyed on the clip, any comparison of two
builds -- and every one of them was silently useless.

The defect only shows across processes, so the check runs real subprocesses. Repeating the clip
inside one interpreter proves nothing: the seed is drawn once at start-up, so a single process
iterates its sets the same way all day.

The pre-fix behaviour is exercised rather than described. The runner below loads `OsmClipper.py` as
text, substitutes the ordered emission back to the bare set iteration, and executes that -- so the
control is the code that shipped, and if the fix is ever reworded the substitution stops matching
and this test fails loudly rather than passing vacuously.
"""
from __future__ import annotations

import hashlib
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
_SRC = _REPO / "CarlaControl" / "src"
sys.path.insert(0, str(_SRC))

from carlacontrol.OsmClipper import BoundingBox, OsmClipper  # noqa: E402

# Enough nodes that set iteration order is comfortably unstable between processes. A handful would
# fit in one small hash table and could come out in the same order by luck.
NODES_PER_WAY = 40
WAY_COUNT = 24
# Independent clips to run. Four different digests were measured on the shipped code; six runs make
# an accidental agreement vanishingly unlikely without making the test slow.
CLIP_RUNS = 6

# The ordered emission the fix introduced, and the bare set iteration it replaced.
ORDERED_EMISSION = "for nid in sorted(used_orig, key=int):"
SET_ORDER_EMISSION = "for nid in used_orig:"

# The clip is run by a separate interpreter per measurement. The emission forms are passed in as
# arguments rather than baked in, so this file holds exactly one copy of each.
RUNNER = """import hashlib
import sys
import types
from pathlib import Path

MODE, SOURCE, OUT, DIGEST = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
SRC, ORDERED, SET_ORDER = Path(sys.argv[5]), sys.argv[6], sys.argv[7]
sys.path.insert(0, str(SRC))

module_path = SRC / "carlacontrol" / "OsmClipper.py"
source_text = module_path.read_text(encoding="utf-8")
if MODE == "set_order":
    if source_text.count(ORDERED) != 1:
        raise SystemExit("cannot restore the pre-fix emission: " + repr(ORDERED)
                         + " not found exactly once")
    source_text = source_text.replace(ORDERED, SET_ORDER)

module = types.ModuleType("OsmClipperUnderTest")
module.__file__ = str(module_path)
# @dataclass resolves annotations through sys.modules, so the module has to be registered before
# its body runs.
sys.modules[module.__name__] = module
exec(compile(source_text, module.__file__, "exec"), module.__dict__)

clipper = module.OsmClipper
clipper.clip_osm_to_bounds(SOURCE, OUT, clipper.read_bounds(SOURCE))
Path(DIGEST).write_text(hashlib.sha256(Path(OUT).read_bytes()).hexdigest(), encoding="utf-8")
"""


def write_extract(path: Path) -> BoundingBox:
    """Write an OSM whose ways run well outside the bounds, so the clip has real work to do."""
    bounds = BoundingBox(min_lat=10.0, min_lon=20.0, max_lat=10.01, max_lon=20.01)
    lines = ['<?xml version="1.0" encoding="utf-8"?>', '<osm version="0.6" generator="test">',
             f'  <bounds minlat="{bounds.min_lat}" minlon="{bounds.min_lon}" '
             f'maxlat="{bounds.max_lat}" maxlon="{bounds.max_lon}"/>']
    node_id = 1000
    ways: list[tuple[int, list[int]]] = []
    for way_index in range(WAY_COUNT):
        # Each way runs west to east across the whole box and beyond it at both ends, at its own
        # latitude, so every way is split and every way contributes interior nodes.
        lat = bounds.min_lat + (way_index + 0.5) * (bounds.max_lat - bounds.min_lat) / WAY_COUNT
        refs = []
        for step in range(NODES_PER_WAY):
            lon = bounds.min_lon - 0.005 + step * 0.02 / (NODES_PER_WAY - 1)
            node_id += 1
            lines.append(f'  <node id="{node_id}" lat="{lat:.7f}" lon="{lon:.7f}" version="1"/>')
            refs.append(node_id)
        ways.append((900000 + way_index, refs))
    for way_id, refs in ways:
        lines.append(f'  <way id="{way_id}" version="1">')
        lines += [f'    <nd ref="{ref}"/>' for ref in refs]
        lines.append('    <tag k="highway" v="residential"/>')
        lines.append('  </way>')
    lines.append('</osm>')
    path.write_text("\n".join(lines), encoding="utf-8")
    return bounds


def clip_in_subprocesses(mode: str, source: Path, work: Path) -> list[str]:
    """Clip `source` `CLIP_RUNS` times, each in its own interpreter, and return the digests."""
    runner = work / "clip_runner.py"
    runner.write_text(RUNNER, encoding="utf-8")
    environment = dict(os.environ)
    # A pinned hash seed would make the pre-fix code look deterministic, which is precisely the
    # illusion this test exists to break.
    environment.pop("PYTHONHASHSEED", None)
    digests = []
    for run in range(CLIP_RUNS):
        out = work / f"{mode}_{run}.osm"
        digest = work / f"{mode}_{run}.sha256"
        result = subprocess.run(
            [sys.executable, str(runner), mode, str(source), str(out), str(digest), str(_SRC),
             ORDERED_EMISSION, SET_ORDER_EMISSION],
            env=environment, capture_output=True, text=True, check=False)
        assert result.returncode == 0, f"{mode} run {run} failed:\n{result.stdout}\n{result.stderr}"
        digests.append(digest.read_text(encoding="utf-8").strip())
    return digests


def graph_of(path: Path) -> tuple[set[str], list[tuple[str, tuple[str, ...]]]]:
    """The clipped extract's content, independent of the order it was written in."""
    root = ET.parse(str(path)).getroot()
    nodes = {f'{n.get("id")}:{n.get("lat")}:{n.get("lon")}' for n in root.findall("node")}
    ways = sorted((w.get("id"), tuple(nd.get("ref") for nd in w.findall("nd")))
                  for w in root.findall("way"))
    return nodes, ways


@pytest.fixture(scope="module")
def extract(tmp_path_factory) -> tuple[Path, Path]:
    work = tmp_path_factory.mktemp("clip_determinism")
    source = work / "source.osm"
    write_extract(source)
    return source, work


def test_clip_is_byte_identical_across_processes(extract):
    """Six clips of one extract, six separate interpreters, one digest."""
    source, work = extract
    digests = clip_in_subprocesses("current", source, work)
    assert len(set(digests)) == 1, (
        f"{len(set(digests))} distinct digests across {CLIP_RUNS} processes: {sorted(set(digests))}")


def test_set_iteration_order_gave_a_different_file_every_time(extract):
    """The shipped code, restored, produces more than one file. Without this the test above could
    pass on an input too small to expose the defect, or on a build with a pinned hash seed."""
    source, work = extract
    digests = clip_in_subprocesses("set_order", source, work)
    assert len(set(digests)) > 1, (
        "restoring the pre-fix set-iteration emission produced one digest across "
        f"{CLIP_RUNS} processes; the check cannot distinguish the fix from the defect")


def test_the_two_orders_describe_the_same_graph(extract):
    """What the defect changed was the byte order, never the content -- which is why it survived."""
    source, work = extract
    clip_in_subprocesses("current", source, work)
    clip_in_subprocesses("set_order", source, work)
    fixed = graph_of(work / "current_0.osm")
    for run in range(CLIP_RUNS):
        assert graph_of(work / f"set_order_{run}.osm") == fixed


def test_repeating_the_clip_in_one_process_cannot_see_the_defect(extract):
    """Recorded so nobody replaces the subprocess machinery above with a loop. The hash seed is
    drawn once per interpreter, so the pre-fix code is perfectly reproducible within a process."""
    source, work = extract
    digests = []
    for run in range(3):
        out = work / f"same_process_{run}.osm"
        OsmClipper.clip_osm_to_bounds(source, out, OsmClipper.read_bounds(source))
        digests.append(hashlib.sha256(out.read_bytes()).hexdigest())
    assert len(set(digests)) == 1
