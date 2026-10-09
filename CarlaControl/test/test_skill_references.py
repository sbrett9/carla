"""The authoring skill's references cite code that exists, checks that exist, and the forms and bands
the compiler actually has.

`07_Scenario_Authoring.md` §8.5: a gotcha carries its enforcement site as a citation, and a test holds
every citation to the tree, so a reference cannot keep naming a function after it is renamed or a check
after it is retired. The same discipline holds the place forms `references/resolution.md` documents to
the schema's, and the bands `references/illumination.md` tabulates to the function that assigns them.

What it cannot see: whether the prose around a citation is still true. It establishes that the thing
named exists, not that it does what the sentence says.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.IlluminationBand import IlluminationBand  # noqa: E402
from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue  # noqa: E402
from carlacontrol.ScenarioSchema import SCHEMA  # noqa: E402

REFERENCES = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios" / "references"
CITATION = re.compile(r"`([A-Za-z0-9_./-]+\.(?:py|cs))::([A-Za-z0-9_.-]+)`")
CHECK = re.compile(r"\bchecks? (\d+(?:(?:, | and )\d+)*)")
NUMBER = re.compile(r"\d+")


def reference_texts() -> dict[str, str]:
    return {path.name: path.read_text(encoding="utf-8") for path in sorted(REFERENCES.glob("*.md"))}


def test_the_five_references_the_plan_names_are_there():
    assert set(reference_texts()) == {"gotchas.md", "resolution.md", "time.md", "illumination.md",
                                      "vehicles.md"}


def test_every_cited_site_exists():
    citations = [(name, path, symbol) for name, text in reference_texts().items()
                 for path, symbol in CITATION.findall(text)]
    assert len([c for c in citations if c[0] == "gotchas.md"]) >= 10
    for name, path, symbol in citations:
        source = _REPO / path
        assert source.is_file(), f"{name} cites {path}, which is not in the tree"
        assert symbol in source.read_text(encoding="utf-8"), f"{name} cites {path}::{symbol}"


def test_every_cited_check_is_a_live_check():
    live = ScenarioCheckCatalogue.active_ids()
    for name, text in reference_texts().items():
        for match in CHECK.finditer(text):
            for number in NUMBER.findall(match.group(1)):
                assert int(number) in live, f"{name} cites check {number}, which is not live"


def test_the_place_forms_documented_are_the_schema_s():
    """Every place form the schema accepts is in `references/resolution.md`, by its keys."""
    text = reference_texts()["resolution.md"]
    for variant in SCHEMA["$defs"]["place"]["anyOf"]:
        for key in variant["properties"]:
            assert f'"{key}"' in text or f"`{key}`" in text, f"place key '{key}' is undocumented"


def test_the_bands_tabulated_are_the_function_s_in_its_order():
    """`references/illumination.md`'s band table is doc 11's six, as `IlluminationBand` names them."""
    text = reference_texts()["illumination.md"]
    section = text[text.index("## The bands"):text.index("## Night")]
    assert re.findall(r"^\| `([a-z_]+)` \|", section, flags=re.M) == IlluminationBand.names()
