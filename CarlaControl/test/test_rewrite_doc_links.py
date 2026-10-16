"""The distribution's link rewriter: a link that leaves its page's folder is pointed at the bundle's
copy of its target, dropped to its text when the target does not ship, and left alone inside the
folder, inside code, and outside the repository. It covers the documentation and the example
scenarios' READMEs, which link to the documentation from where it is in the repository.
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
REWRITER = _REPO / "Scripts" / "Distribution" / "rewrite_doc_links.py"


def load_rewriter():
    spec = importlib.util.spec_from_file_location("rewrite_doc_links", REWRITER)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.encode("utf-8"))


def staged_bundle(root: Path) -> Path:
    """A bundle as the distribution scripts stage it: docs/, Scenarios/ and skills/."""
    write(root / "docs" / "Guides" / "Getting_Started.md", "# Getting started\n")
    write(root / "docs" / "EPOL" / "Behavioral_Annotations.md", "# Labels\n")
    write(root / "skills" / "sumo-traffic-scenarios" / "SKILL.md", "# Skill\n")
    write(root / "Scenarios" / "Bahonar" / "README.md",
          "# Bahonar\n"
          "[Getting started](../../../../Docs/CAT_Research/Guides/Getting_Started.md#the-site-profile)\\\n"
          "[Labels](../../../../Docs/CAT_Research/EPOL/Behavioral_Annotations.md)\\\n"
          "[The other example](../Arapahoe/README.md)\\\n"
          "[The skill](../../../skills/sumo-traffic-scenarios/SKILL.md)\n")
    write(root / "Scenarios" / "Arapahoe" / "README.md", "# Arapahoe\n")
    write(root / "docs" / "Guides" / "Writing_A_Scenario.md",
          "# Writing\n"
          "[Started](Getting_Started.md) and [labels](../EPOL/Behavioral_Annotations.md).\\\n"
          "[The skill](../../../CarlaControl/skills/sumo-traffic-scenarios/SKILL.md)\\\n"
          "[Engine notes](../../../CarlaControl/skills/third-party/ue/README.md)\\\n"
          "[The Bahonar example](../../../CarlaControl/Examples/Scenarios/Bahonar/README.md)\\\n"
          "[Elsewhere](../../../LICENSE) and [the web](https://example.org/x.md)\\\n"
          "`[not a link](../../../CarlaControl/skills/x.md)`\n"
          "```\n[fenced](../../../CarlaControl/skills/x.md)\n```\n")
    return root


def test_a_scenario_readme_is_pointed_at_the_shipped_docs(tmp_path: Path) -> None:
    rewriter = load_rewriter()
    root = staged_bundle(tmp_path / "bundle")
    notes, pages = rewriter.rewrite_bundle(str(root))
    text = (root / "Scenarios" / "Bahonar" / "README.md").read_text(encoding="utf-8")
    assert "[Getting started](../../docs/Guides/Getting_Started.md#the-site-profile)" in text
    assert "[Labels](../../docs/EPOL/Behavioral_Annotations.md)" in text
    assert "[The other example](../Arapahoe/README.md)" in text          # inside its own folder
    assert "[The skill](../../skills/sumo-traffic-scenarios/SKILL.md)" in text
    assert pages == 5
    assert any("Scenarios/Bahonar/README.md:2:" in note for note in notes)


def test_a_documentation_page_keeps_its_own_links_and_maps_the_rest(tmp_path: Path) -> None:
    rewriter = load_rewriter()
    root = staged_bundle(tmp_path / "bundle")
    rewriter.rewrite_bundle(str(root))
    text = (root / "docs" / "Guides" / "Writing_A_Scenario.md").read_text(encoding="utf-8")
    assert "[Started](Getting_Started.md) and [labels](../EPOL/Behavioral_Annotations.md)." in text
    assert "[The skill](../../skills/sumo-traffic-scenarios/SKILL.md)" in text
    assert "Engine notes\\" in text and "[Engine notes]" not in text     # dropped: it does not ship
    assert "[The Bahonar example](../../Scenarios/Bahonar/README.md)" in text
    assert "[Elsewhere](../../../LICENSE)" in text                        # outside the repository
    assert "[the web](https://example.org/x.md)" in text
    assert "`[not a link](../../../CarlaControl/skills/x.md)`" in text   # a code span
    assert "[fenced](../../../CarlaControl/skills/x.md)" in text         # a fenced block


def test_a_page_with_nothing_to_change_is_left_byte_for_byte(tmp_path: Path) -> None:
    rewriter = load_rewriter()
    root = staged_bundle(tmp_path / "bundle")
    page = root / "Scenarios" / "Arapahoe" / "README.md"
    before = page.stat().st_mtime_ns
    rewriter.rewrite_bundle(str(root))
    assert page.read_bytes() == b"# Arapahoe\n"
    assert page.stat().st_mtime_ns == before


def test_a_bundle_without_scenarios_rewrites_its_docs_alone(tmp_path: Path) -> None:
    rewriter = load_rewriter()
    root = staged_bundle(tmp_path / "bundle")
    import shutil
    shutil.rmtree(root / "Scenarios")
    notes, pages = rewriter.rewrite_bundle(str(root))
    assert pages == 3
    text = (root / "docs" / "Guides" / "Writing_A_Scenario.md").read_text(encoding="utf-8")
    assert "The Bahonar example\\" in text and "[The Bahonar example]" not in text
