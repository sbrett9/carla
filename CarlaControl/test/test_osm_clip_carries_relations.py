"""The clip decides which geometry is in the area, not which facts about it survive (issue #12).

Three things used to be lost and are checked here: relations, standalone nodes, and the identity of a
way the clip splits.

The third is the one that bites quietly. netconvert resolves a turn restriction's `from` and `to` way
reference by PREFIX match on the id string -- `RelationHandler::findEdgeRef` compares each candidate
edge's leading characters against the reference and against `-reference` -- and warns only when *two*
candidates match. A single wrong match is applied in silence. The clip used to number a split way's
runs from `max(id) + 1`, one decimal digit longer than the largest real id in the document, which is
exactly the shape that makes a real id a prefix of a synthetic one.

The netconvert-backed cases at the end are the round trip the issue asks for, over the two extracts
that bracket the outcome: San Francisco, where carrying relations changes the graph, and Arapahoe,
where it must not.
"""
from __future__ import annotations

import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.OsmClipper import BoundingBox, OsmClipper  # noqa: E402

NETCONVERT = _REPO / "Build" / "sumo-install" / "bin" / "netconvert.exe"
if not NETCONVERT.exists():
    NETCONVERT = NETCONVERT.with_suffix("")
PROJ_DATA = _REPO / "Build" / "sumo-install" / "share" / "proj"

# The world build's flag set, which is now also the scenario build's; see OsmConverter.BuildArguments.
NETCONVERT_FLAGS = [
    "--default.lanewidth", "3.35", "--default.sidewalk-width", "2.8", "--tls.guess", "true",
    "--geometry.remove", "--roundabouts.guess", "--osm.turn-lanes",
    "--offset.disable-normalization", "--junctions.join", "--tls.default-type", "actuated",
    "--junctions.join-dist", "25", "--output.street-names", "true",
    "--output.original-names", "true", "--keep-edges.by-vclass", "passenger",
    "--keep-edges.components", "1", "--remove-edges.isolated", "true",
]

BOUNDS = BoundingBox(min_lat=0.0, min_lon=0.0, max_lat=0.01, max_lon=0.04)

# Ids chosen so the rule that shipped -- number the runs from max(id) + 1 -- manufactures a prefix
# collision: the largest id in the document is 5000, so the first synthetic id is 5001, and the
# document also holds a way numbered 500.
LARGEST_ID = 5000
PREFIXED_WAY = "500"


def write_extract(path: Path) -> None:
    """An extract holding each category the clip used to drop, and a way it must split."""
    lines = ['<?xml version="1.0" encoding="utf-8"?>', '<osm version="0.6" generator="test">',
             f'  <bounds minlat="{BOUNDS.min_lat}" minlon="{BOUNDS.min_lon}" '
             f'maxlat="{BOUNDS.max_lat}" maxlon="{BOUNDS.max_lon}"/>']

    def node(nid: str, lat: float, lon: float, tags: str = "") -> None:
        if tags:
            lines.append(f'  <node id="{nid}" lat="{lat:.7f}" lon="{lon:.7f}" version="1">')
            lines.append(tags)
            lines.append('  </node>')
        else:
            lines.append(f'  <node id="{nid}" lat="{lat:.7f}" lon="{lon:.7f}" version="1"/>')

    # A way that leaves the box and comes back, so the clip splits it into two runs. The second run
    # is the longer one, which is the one that must keep the way's own id.
    node("101", 0.005, 0.002)
    node("102", 0.005, 0.008)
    node("103", 0.030, 0.012)      # north of the box
    node("104", 0.005, 0.018)
    node("105", 0.005, 0.024)
    node("106", 0.005, 0.030)
    # A second road meeting the first, and the node they share.
    node("201", 0.001, 0.030)
    node("202", 0.009, 0.036)
    # A node-mapped feature belonging to no way, inside the box.
    node("301", 0.007, 0.015, '    <tag k="highway" v="street_lamp"/>')
    # The via node of a restriction, on no surviving way and outside the box.
    node("401", 0.030, 0.030)
    # The largest id in the document, which is what the shipped numbering rule counted from.
    node(str(LARGEST_ID), 0.002, 0.020, '    <tag k="barrier" v="gate"/>')

    def way(wid: str, refs: list[str]) -> None:
        lines.append(f'  <way id="{wid}" version="1">')
        lines.extend(f'    <nd ref="{r}"/>' for r in refs)
        lines.append('    <tag k="highway" v="residential"/>')
        lines.append('  </way>')

    way(PREFIXED_WAY, ["101", "102", "103", "104", "105", "106"])
    way("600", ["201", "106", "202"])

    lines.append('  <relation id="900" version="1">')
    lines.append(f'    <member type="way" ref="{PREFIXED_WAY}" role="from"/>')
    lines.append('    <member type="node" ref="401" role="via"/>')
    lines.append('    <member type="way" ref="600" role="to"/>')
    lines.append('    <tag k="type" v="restriction"/>')
    lines.append('    <tag k="restriction" v="no_left_turn"/>')
    lines.append('  </relation>')
    lines.append('  <relation id="901" version="1">')
    lines.append('    <member type="way" ref="600" role="outer"/>')
    lines.append('    <tag k="type" v="multipolygon"/>')
    lines.append('    <tag k="amenity" v="parking"/>')
    lines.append('  </relation>')
    lines.append('</osm>')
    path.write_text("\n".join(lines), encoding="utf-8")


@pytest.fixture(scope="module")
def clipped(tmp_path_factory):
    work = tmp_path_factory.mktemp("clip_relations")
    source = work / "source.osm"
    write_extract(source)
    out = work / "clipped.osm"
    summary = OsmClipper.clip_osm_to_bounds(source, out, BOUNDS)
    return ET.parse(str(source)).getroot(), ET.parse(str(out)).getroot(), summary


def test_every_relation_survives(clipped):
    """Relations in equals relations out, verbatim -- members, roles and tags."""
    source, result, summary = clipped
    assert len(result.findall("relation")) == len(source.findall("relation")) == 2
    assert summary.relations == 2
    for original, carried in zip(source.findall("relation"), result.findall("relation"),
                                 strict=True):
        assert ET.tostring(original) == ET.tostring(carried)


def test_a_node_only_a_relation_names_is_carried(clipped):
    """The restriction's via node is on no surviving way and outside the box. Without it the
    relation that was carried would reference a node that is not there."""
    _, result, summary = clipped
    assert "401" in {n.get("id") for n in result.findall("node")}
    assert summary.relation_nodes == 1


def test_node_mapped_features_are_carried(clipped):
    """The second category the issue names: nodes inside the box that belong to no way."""
    _, result, summary = clipped
    carried = {n.get("id") for n in result.findall("node")}
    assert {"301", str(LARGEST_ID)} <= carried
    assert summary.standalone_nodes == 2


def test_a_split_way_keeps_its_own_id_on_its_longest_run(clipped):
    """So a relation naming that way still resolves, and to the largest piece of it."""
    source, result, summary = clipped
    assert summary.renumbered_runs == 1

    source_ways = {w.get("id") for w in source.findall("way")}
    kept = [w for w in result.findall("way") if w.get("id") == PREFIXED_WAY]
    renumbered = [w for w in result.findall("way") if w.get("id") not in source_ways]

    assert len(kept) == 1, "the original way id must appear exactly once"
    assert len(renumbered) == 1, "the split should have produced exactly one other run"
    assert len(kept[0].findall("nd")) > len(renumbered[0].findall("nd"))


def test_no_original_id_prefixes_a_synthetic_one(clipped):
    """The invariant that keeps findEdgeRef honest: nothing this clip invents can be reached by a
    prefix match on an id the source document used."""
    source, result, _ = clipped
    original = {e.get("id") for e in
                [*source.findall("node"), *source.findall("way"), *source.findall("relation")]}
    synthetic = {e.get("id") for e in [*result.findall("node"), *result.findall("way")]} - original

    assert synthetic, "this extract is supposed to force the clip to invent ids"
    for invented in synthetic:
        colliding = [i for i in original if invented.startswith(i)]
        assert not colliding, (
            f"synthetic id {invented} can be reached by a prefix match on {colliding}")


def test_the_shipped_numbering_rule_produced_such_a_prefix(clipped):
    """The control. The rule that shipped was `max(id) + 1`, one decimal digit longer than the
    largest id in the document, which is exactly when a real id is a prefix of a synthetic one.
    Without this the test above could pass on an extract where no collision was possible."""
    source, _, _ = clipped
    original = {e.get("id") for e in
                [*source.findall("node"), *source.findall("way"), *source.findall("relation")]}
    shipped_first_synthetic = str(max(int(i) for i in original) + 1)

    colliding = [i for i in original if shipped_first_synthetic.startswith(i)]
    assert colliding == [PREFIXED_WAY], (
        f"the shipped rule would have allocated {shipped_first_synthetic}, which "
        f"{colliding or 'nothing'} prefixes")


def test_the_synthetic_id_block_is_refused_when_no_block_is_free():
    """The failure path of the id allocator, which otherwise never runs."""
    every_leading_digit = {str(d) for d in range(1, 10)}
    with pytest.raises(ValueError, match="prefix collisions"):
        OsmClipper._synthetic_id_prefix(every_leading_digit)


# ── through netconvert, on the extracts the issue was measured against ──────────────────────────

requires_netconvert = pytest.mark.skipif(
    not NETCONVERT.exists(), reason=f"netconvert is not staged at {NETCONVERT}")


def clip_both_ways(extract: Path, work: Path) -> tuple[Path, Path, BoundingBox]:
    """The same extract clipped twice: once with its relations removed at source, which is what the
    clip used to produce whatever the source held, and once as it now is."""
    bounds = OsmClipper.read_bounds(extract)
    tree = ET.parse(str(extract))
    root = tree.getroot()
    for relation in list(root.findall("relation")):
        root.remove(relation)
    stripped = work / f"{extract.stem}_norelations.osm"
    tree.write(str(stripped), encoding="utf-8", xml_declaration=True)

    control, carried = work / "control.osm", work / "carried.osm"
    OsmClipper.clip_osm_to_bounds(stripped, control, bounds)
    OsmClipper.clip_osm_to_bounds(extract, carried, bounds)
    return control, carried, bounds


def convert(osm: Path, net: Path, bounds: BoundingBox) -> None:
    latitude = (bounds.min_lat + bounds.max_lat) / 2
    longitude = (bounds.min_lon + bounds.max_lon) / 2
    projection = (f"+proj=tmerc +lat_0={latitude} +lon_0={longitude} +k=1 +x_0=0 +y_0=0 "
                  "+ellps=WGS84 +units=m +no_defs")
    result = subprocess.run(
        [str(NETCONVERT), "--osm-files", str(osm), "--output-file", str(net),
         "--proj", projection, *NETCONVERT_FLAGS],
        capture_output=True, text=True, check=False,
        env={"PROJ_LIB": str(PROJ_DATA), "PROJ_DATA": str(PROJ_DATA), "PATH": ""})
    assert result.returncode == 0, result.stderr[-2000:]


def connections(net: Path) -> set:
    """Edge-to-edge movements, excluding the internal ones inside a junction."""
    root = ET.parse(str(net)).getroot()
    return {(c.get("from"), c.get("to"), c.get("fromLane"), c.get("toLane"))
            for c in root.findall("connection") if not (c.get("from") or "").startswith(":")}


@requires_netconvert
def test_san_francisco_gains_a_turn_restriction(tmp_path):
    """The positive case. 104 relations, 22 of them restrictions; carrying them removes exactly one
    movement from the network. Measured 416 -> 415 connections under the current flag set, and
    414 -> 413 under the one that preceded it; the flags move the absolute count, the restriction
    moves the difference."""
    extract = _REPO / "Import" / "SF_LaurelHeights.osm"
    if not extract.exists():
        pytest.skip(f"{extract} is not in this tree")
    control, carried, bounds = clip_both_ways(extract, tmp_path)
    assert (len(ET.parse(str(carried)).getroot().findall("relation"))
            == len(ET.parse(str(extract)).getroot().findall("relation")))

    convert(control, tmp_path / "control.net.xml", bounds)
    convert(carried, tmp_path / "carried.net.xml", bounds)
    before = connections(tmp_path / "control.net.xml")
    after = connections(tmp_path / "carried.net.xml")

    assert len(before - after) == 1, "one banned movement should have gone"
    assert after - before == set(), "carrying relations must not invent a movement"


@requires_netconvert
def test_arapahoe_is_unchanged(tmp_path):
    """The no-harm case, and the more important of the two. 42 relations in, 42 out, and not one
    movement different: netconvert warns about the restrictions it cannot resolve, ignores them,
    exits 0 and leaves the rest of the graph exactly as it was. Of Arapahoe's 22 restrictions, 8
    are via-way -- which netconvert does not implement at all -- 2 are incomplete in the Overpass
    extract itself, and 2 have via nodes outside the bounds."""
    extract = _REPO / "Import" / "Arapahoe_I25.osm"
    if not extract.exists():
        pytest.skip(f"{extract} is not in this tree")
    control, carried, bounds = clip_both_ways(extract, tmp_path)
    assert (len(ET.parse(str(carried)).getroot().findall("relation"))
            == len(ET.parse(str(extract)).getroot().findall("relation")) == 42)

    convert(control, tmp_path / "control.net.xml", bounds)
    convert(carried, tmp_path / "carried.net.xml", bounds)

    assert connections(tmp_path / "control.net.xml") == connections(tmp_path / "carried.net.xml")
    # And nothing else moved either: the whole graph fingerprints the same.
    assert (NetworkFingerprint.of_file(tmp_path / "control.net.xml")
            == NetworkFingerprint.of_file(tmp_path / "carried.net.xml"))
