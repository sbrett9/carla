"""The scenario side must identify a network the same way the world build does.

`CarlaNet.Map.NetworkFingerprint` computes a world's network fingerprint in C# when the world is
built and writes it into the world package; `carlacontrol.NetworkFingerprint` recomputes it here
when a scenario opens that package. If the two disagree by so much as a separator byte, every
correct package is refused. So this file asserts the same digests, over the same fixture files, as
`CarlaNet/test/CarlaNet.Tests/Map/NetworkFingerprintTests.cs`, and the two constants lists are the
contract between them.

The fixtures are real netconvert 1.27.0 output; the C# test file documents the exact commands that
produced them. What each pair demonstrates is asserted below rather than described.
"""
from __future__ import annotations

import copy
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402

FIXTURES = _REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network"

# The values both implementations must produce. NetworkFingerprintTests.cs asserts the same four.
OMITTED_DIGEST = "b65c5ca3320b753c76145ddd8b1389708388c4d5d5ab40fdb2381c6a7ab19485"
STREET_NAMES_DIGEST = "44194b18d22c91a25e85babe0469d43d064f3fb9aabe15fa62a486cd8fb4e2e0"
ACTUATED_DIGEST = "f3906afa5e66210ad4f8660cffdd1041f284a641110da70931a3f9a2bcd896b0"
CLIP_ORDER_DIGEST = "f0e00487547612acdd41daedcacb04f6e9aa0e6b71a5610085062f3d2a3a91c4"


def text(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def fingerprint(name: str) -> str:
    return NetworkFingerprint.of_file(FIXTURES / name)


def test_the_encoding_matches_the_one_the_world_build_records():
    """The cross-language contract. Both halves pin these; neither can drift alone."""
    assert fingerprint("street_names_omitted.net.xml") == OMITTED_DIGEST
    assert fingerprint("street_names_true.net.xml") == STREET_NAMES_DIGEST
    assert fingerprint("street_names_false.net.xml") == STREET_NAMES_DIGEST
    assert fingerprint("signals_actuated.net.xml") == ACTUATED_DIGEST
    assert fingerprint("clip_node_order_a.net.xml") == CLIP_ORDER_DIGEST
    assert fingerprint("clip_node_order_b.net.xml") == CLIP_ORDER_DIGEST


def test_setting_street_names_changes_the_graph():
    """36 edges without the option, 40 with it: NBEdge::expandableBy will not merge two edges
    carrying different street names."""
    assert fingerprint("street_names_omitted.net.xml") != fingerprint("street_names_true.net.xml")


def test_street_names_set_to_false_is_the_same_graph_as_true():
    """NBEdge::expandableBy guards on whether the option was given at all, not on its value, so
    `false` is indistinguishable from `true` and only omitting it gives the merged graph back."""
    assert fingerprint("street_names_true.net.xml") == fingerprint("street_names_false.net.xml")
    assert fingerprint("street_names_omitted.net.xml") != fingerprint("street_names_false.net.xml")


def test_signal_programme_type_is_part_of_the_identity():
    assert fingerprint("street_names_true.net.xml") != fingerprint("signals_actuated.net.xml")


def test_two_clip_orders_of_one_extract_give_one_fingerprint():
    assert text("clip_node_order_a.net.xml") != text("clip_node_order_b.net.xml")
    assert fingerprint("clip_node_order_a.net.xml") == fingerprint("clip_node_order_b.net.xml")


def test_reordering_the_document_does_not_change_the_fingerprint():
    root = ET.parse(str(FIXTURES / "street_names_true.net.xml")).getroot()
    reversed_root = ET.Element(root.tag, dict(root.attrib))
    for child in reversed(list(root)):
        reversed_root.append(copy.deepcopy(child))
    assert (NetworkFingerprint.of_text(ET.tostring(reversed_root, encoding="unicode"))
            == fingerprint("street_names_true.net.xml"))


def test_the_generation_comment_is_not_part_of_the_identity():
    """netconvert's leading comment carries the moment it ran and the temporary paths the world
    build hands it, so a byte digest of a network never reproduces."""
    original = text("street_names_true.net.xml")
    start, end = original.index("<!--"), original.index("-->")
    without_comment = original[:start] + original[end + 3:]
    assert original != without_comment
    assert NetworkFingerprint.of_text(without_comment) == NetworkFingerprint.of_text(original)


def test_street_name_values_are_not_part_of_the_identity():
    root = ET.parse(str(FIXTURES / "street_names_true.net.xml")).getroot()
    renamed = [edge for edge in root.findall("edge") if edge.get("name") is not None]
    assert renamed
    for edge in renamed:
        edge.set("name", "Renamed Avenue")
    assert (NetworkFingerprint.of_text(ET.tostring(root, encoding="unicode"))
            == fingerprint("street_names_true.net.xml"))


def test_removing_an_internal_edge_changes_the_fingerprint():
    """The lanes inside a junction are where a dropped turn restriction shows up."""
    root = ET.parse(str(FIXTURES / "street_names_true.net.xml")).getroot()
    internal = next(e for e in root.findall("edge") if e.get("function") == "internal")
    root.remove(internal)
    assert (NetworkFingerprint.of_text(ET.tostring(root, encoding="unicode"))
            != fingerprint("street_names_true.net.xml"))


def test_removing_a_connection_changes_the_fingerprint():
    root = ET.parse(str(FIXTURES / "street_names_true.net.xml")).getroot()
    root.remove(root.findall("connection")[0])
    assert (NetworkFingerprint.of_text(ET.tostring(root, encoding="unicode"))
            != fingerprint("street_names_true.net.xml"))


def test_a_missing_attribute_is_not_an_empty_one():
    with_width = ('<net><edge id="a"><lane id="a_0" width=""/></edge><location netOffset="0,0" '
                  'convBoundary="0,0,1,1" origBoundary="0,0,1,1" projParameter="!"/></net>')
    without_width = ('<net><edge id="a"><lane id="a_0"/></edge><location netOffset="0,0" '
                     'convBoundary="0,0,1,1" origBoundary="0,0,1,1" projParameter="!"/></net>')
    assert NetworkFingerprint.of_text(with_width) != NetworkFingerprint.of_text(without_width)


def test_the_projection_is_part_of_the_identity():
    root = ET.parse(str(FIXTURES / "street_names_true.net.xml")).getroot()
    location = root.find("location")
    location.set("projParameter", location.get("projParameter").replace("lat_0=39.5", "lat_0=39.6"))
    assert (NetworkFingerprint.of_text(ET.tostring(root, encoding="unicode"))
            != fingerprint("street_names_true.net.xml"))


def test_an_empty_document_is_rejected():
    with pytest.raises(ET.ParseError):
        NetworkFingerprint.of_text("")
