"""What the tools show a user is plain American English and points at no internal design document.

The design documents the tools were built from stay internal and do not ship, so no text a user is
shown may cite them: no section sign, no plan document by number or file name, no decision or contract
code, no plan folder. Nor may it speak of the person who ruled on the design. And it is plain American
English: meters, color, gray, center, license; a dataset rather than a corpus; a schedule rather than
a rota; no "eligible" verdict, no "photon", no "kerb", no "landed" for arrived.

A field name, an option, an identifier or a file name keeps its spelling: a word joined to one by `_`,
`-`, `.`, `/` or `\\` (`orbit_centre_x_m`, `--orbit-centre`, `world-centre`) is not text, and neither
is a string with no space in it, which is a key, an enumerated value or a name. A schedule is declared
in a specification's `rotas`, so the field may be named -- `rotas[guard_posting]`, `(rotas)`, `` `rota` ``
-- where prose would say schedule.

Checked here, everywhere a user reads text the code produces:

* the `--help` of every `carla-*` command, run through its entry point as an installed command runs it;
* the published schemas and the authoring skill's `checks.json`, as shipped and as generated now, and
  the run checks' catalogue;
* a compiled scenario's resolution report, its lock and its supervision plan;
* every string the carlacontrol package and the checkout scripts print or write, with the module
  docstrings that are a command's or a script's `--help` -- the source of the launch echo, the
  closeout, view readiness, refusals, warnings and the run result's text;
* every string literal in CarlaNet's sources -- exception messages, the session report, refusals --
  for citations and owner wording.
"""
from __future__ import annotations

import ast
import importlib.metadata
import json
import re
import sys
import tomllib
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from ScenarioWorldFixture import ScenarioWorldFixture  # noqa: E402

from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationCheckCatalogue import RunConfigurationCheckCatalogue  # noqa: E402
from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioSchema import SCHEMA  # noqa: E402
from carlacontrol.ScenarioSweep import SWEEP_SCHEMA  # noqa: E402

PYPROJECT = _REPO / "CarlaControl" / "pyproject.toml"
SKILL = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios"
PUBLISHED = [_REPO / "CarlaControl" / "schemas" / "run_configuration.schema.json",
             SKILL / "schemas" / "scenario.schema.json", SKILL / "schemas" / "sweep.schema.json",
             SKILL / "checks.json",
             *sorted((_REPO / "CarlaControl" / "schemas").glob("*.tableschema.json")),
             *sorted(path for path in (_REPO / "CarlaControl" / "schemas").glob("*.schema.json")
                     if path.name != "run_configuration.schema.json")]
XML_SCHEMAS = sorted((_REPO / "CarlaControl" / "schemas").glob("*.xsd"))
# The vehicle classes' descriptions and curation reasons are the measured catalogue's own data: the
# catalogue digest covers them and every compiled scenario's lock records that digest, so their words
# change only with a rebuilt catalogue and recompiled scenarios.
CATALOGUE_DATA = {_REPO / "CarlaControl" / "src" / "carlacontrol" / "VehicleClassAssignment.py"}
PYTHON_SOURCES = [*sorted(p for p in (_REPO / "CarlaControl" / "src" / "carlacontrol").rglob("*.py")
                          if p not in CATALOGUE_DATA),
                  *sorted((_REPO / "CarlaControl" / "scripts").glob("*.py")),
                  _REPO / "CarlaNet" / "python" / "carlanet" / "__init__.py",
                  _REPO / "CarlaNet" / "python" / "run_sumo_drive.py"]
CSHARP_SOURCES = sorted(p for p in (_REPO / "CarlaNet" / "src").rglob("*.cs")
                        if not {"bin", "obj"} & set(p.parts))

# A word joined to an identifier character on either side is part of a name, not text.
_ALONE_BEFORE = r"(?<![\w\-./\\])"
_ALONE_AFTER = r"(?![\w\-/\\])"


def _words(*words: str) -> str:
    return _ALONE_BEFORE + "(?:" + "|".join(words) + ")" + _ALONE_AFTER


CITATIONS = {
    "a section sign": r"§",
    "a plan decision or contract code": r"\b\d{2} [CD]\d+(?:\.\d+)*\b|\bD\d+\.\d+\b",
    "a plan document by number": r"\bdoc(?:ument)?s? \d{1,2}\b",
    "a plan file": r"\b\d{2}_[A-Z][A-Za-z]*(?:_[A-Za-z]+)*(?:\.md)?\b",
    "the plans' folders": r"CAT_Research|Findings/|Plans/|SUMO_Behavioral_Capture",
    "a plan's open question": r"\bquestion \d+\b",
    "the owner": r"\b(?:the|by the|as the) owner\b(?! of)|\bowner's\b",
}
WORDING = {
    "British spelling": _words(
        r"metres?", r"centres?", r"centred", r"colour(?:s|ed|ing)?", r"greys?", r"licences?",
        r"artefacts?", r"behaviour(?:s|al)?", r"tyres?",
        r"programmes?", r"neighbour(?:s|hood|hoods|ing)?", r"honour(?:s|ed|ing)?",
        r"normalis(?:e|ed|es|ing|ation)", r"organis(?:e|ed|es|ing|ation)",
        r"recognis(?:e|ed|es|ing)", r"modell(?:ed|ing)", r"travell(?:ed|ing)", r"labell(?:ed|ing)",
        r"carriageways?", r"lorry", r"lorries", r"whilst", r"amongst"),
    "corpus": _words(r"corpus", r"corpora"),
    "landed for arrived": _words(r"landed", r"lands", r"landing"),
    "rota": r"(?<![\w\-./\\(\[`'\"])rotas?(?![\w\-/\\\[(:)`'\"])",
    "eligible": _words(r"eligible", r"eligibility"),
    "photon": _words(r"photons?"),
    "kerb": _words(r"kerbs?", r"kerbside"),
}
EVERY_RULE = {**CITATIONS, **WORDING}


def offences(text: str, rules: dict[str, str] = EVERY_RULE) -> list[str]:
    """Every breach in `text`, each with the rule it breaks and the words around it."""
    flat = " ".join(text.split())
    found = []
    for rule, pattern in rules.items():
        for match in re.finditer(pattern, flat, flags=re.IGNORECASE):
            found.append(f"{rule}: ...{flat[max(0, match.start() - 60):match.end() + 60]}...")
    return found


def prose(value) -> list[str]:
    """The strings of a JSON value a person reads: values with a space in them, not keys or names."""
    if isinstance(value, str):
        return [value] if " " in value.strip() else []
    if isinstance(value, dict):
        return [s for v in value.values() for s in prose(v)]
    if isinstance(value, list):
        return [s for v in value for s in prose(v)]
    return []


def strings_in(value) -> list[str]:
    if isinstance(value, str):
        return [value]
    if isinstance(value, dict):
        return [s for k, v in value.items() for s in (k, *strings_in(v))]
    if isinstance(value, list):
        return [s for v in value for s in strings_in(v)]
    return []


def assert_plain(texts, where: str, rules: dict[str, str] = EVERY_RULE) -> None:
    found = [offence for text in texts for offence in offences(text, rules)]
    assert not found, f"{where}:\n  " + "\n  ".join(found[:20])


# ---- the rules themselves ---------------------------------------------------------------------------

@pytest.mark.parametrize("text", [
    "a view not ready refuses the run (03 §9.5.1)", "see 12 D12.8", "as doc 10 measured",
    "in 07_Scenario_Authoring.md", "Docs/CAT_Research/Findings/x", "07 §12 question 12",
    "by the owner's ruling of 2026-10-06", "The owner checks supervision live", "in metres",
    "the orbit centre", "a flat grey ribbon", "a corpus with no hard negatives", "the guard rota",
    "eligible 12, drawn 4", "where the vehicle lands", "pulls to the kerb", "photon budget"])
def test_each_rule_finds_what_it_is_for(text):
    assert offences(text)


@pytest.mark.parametrize("text", [
    "--orbit-centre X Y", "orbit_centre_x_m", "'world-centre' the center", "rotas[guard_posting] entry",
    "Schedules (rotas)", "in its `rota`", "the vehicle catalogue", "is unlabelled", "run check 50",
    "the clock owner of the world", "an owner of the clock", "check 41", "kerb_dwell", "bbox_centre_m",
    "lighting_honours_no_epoch"])
def test_names_and_plain_text_pass(text):
    assert not offences(text)


# ---- --help ------------------------------------------------------------------------------------------

def declared_commands() -> dict[str, str]:
    return tomllib.loads(PYPROJECT.read_text(encoding="utf-8"))["project"]["scripts"]


@pytest.mark.parametrize("name", sorted(declared_commands()))
def test_every_command_s_help_is_plain(name, monkeypatch, capsys):
    main = importlib.metadata.EntryPoint(name, declared_commands()[name], "console_scripts").load()
    # Wide enough that argparse wraps nothing: a word broken at a hyphen would read as two.
    monkeypatch.setenv("COLUMNS", "10000")
    monkeypatch.setattr(sys, "argv", [name, "--help"])
    with pytest.raises(SystemExit) as exited:
        main()
    assert exited.value.code == 0
    text = capsys.readouterr().out
    assert "usage:" in text
    assert_plain([text], f"{name} --help")


# ---- what the generators publish -------------------------------------------------------------------

@pytest.mark.parametrize("path", PUBLISHED, ids=lambda p: p.name)
def test_every_published_schema_and_check_list_is_plain(path):
    assert_plain(prose(json.loads(path.read_text(encoding="utf-8"))), str(path.relative_to(_REPO)))


@pytest.mark.parametrize("path", XML_SCHEMAS, ids=lambda p: p.name)
def test_every_published_xml_schema_s_documentation_is_plain(path):
    namespace = "{http://www.w3.org/2001/XMLSchema}"
    texts = [element.text or "" for element in ET.parse(path).iter(namespace + "documentation")]
    assert texts
    assert_plain(texts, str(path.relative_to(_REPO)))


def test_what_the_generators_produce_now_is_plain():
    for name, document in (("run configuration schema", RunConfiguration.schema()),
                           ("run checks", RunConfigurationCheckCatalogue.to_document()),
                           ("scenario checks", ScenarioCheckCatalogue.to_document()),
                           ("scenario schema", SCHEMA), ("sweep schema", SWEEP_SCHEMA)):
        assert_plain(prose(document), name)
    assert_plain([RunConfiguration.help_text()], "run configuration help")


# ---- a compiled scenario ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def compiled(tmp_path_factory):
    try:
        installation = ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO with duarouter: {missing}")
    directory = tmp_path_factory.mktemp("compiled")
    world = ScenarioWorldFixture(directory / "world", installation)
    specification = world.specification()
    result = ScenarioCompiler(installation).compile(world.write(specification), directory / "out")
    assert not result.refused, [str(f) for f in result.findings.findings if f.outcome == "refuse"]
    return specification, result


def without_the_author_s_words(text: str, specification: dict) -> str:
    """`text` with what the specification's author wrote taken out: the fixture's own names and
    sentences are its author's, not the compiler's."""
    for authored in sorted({s for s in strings_in(specification) if len(s) >= 3}, key=len,
                           reverse=True):
        text = text.replace(authored, " ")
    return text


@pytest.mark.parametrize("role", ["resolution", "lock", "supervision"])
def test_a_compiled_scenario_s_records_are_plain(compiled, role):
    specification, result = compiled
    document = json.loads(result.files[role].read_text(encoding="utf-8"))
    assert_plain([without_the_author_s_words(s, specification) for s in prose(document)],
                 result.files[role].name)


def test_a_compiled_scenario_s_resolution_report_is_plain(compiled):
    specification, result = compiled
    text = result.files["resolution_md"].read_text(encoding="utf-8")
    assert "## " in text
    assert_plain([without_the_author_s_words(text, specification)], result.files["resolution_md"].name)


# ---- the sources of everything else a user reads ---------------------------------------------------

def _docstrings(tree: ast.Module) -> tuple[ast.Constant | None, set[int]]:
    """The module docstring, and the ids of every class and function docstring."""
    module, others = None, set()
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            body = node.body
            if body and isinstance(body[0], ast.Expr) and isinstance(body[0].value, ast.Constant) \
                    and isinstance(body[0].value.value, str):
                if isinstance(node, ast.Module):
                    module = body[0].value
                else:
                    others.add(id(body[0].value))
    return module, others


def user_visible_strings(path: Path) -> list[str]:
    """Every string a Python file can show a user: its literals, less class and function docstrings,
    and its module docstring where that is its `--help` (`description=__doc__`)."""
    source = path.read_text(encoding="utf-8")
    tree = ast.parse(source)
    module, docstrings = _docstrings(tree)
    shown = []
    for node in ast.walk(tree):
        if not (isinstance(node, ast.Constant) and isinstance(node.value, str)):
            continue
        if id(node) in docstrings:
            continue
        if node is module and "description=__doc__" not in source:
            continue
        if " " in node.value.strip():
            shown.append(node.value)
    return shown


@pytest.mark.parametrize("path", PYTHON_SOURCES, ids=lambda p: str(p.relative_to(_REPO)))
def test_every_string_the_python_tools_show_is_plain(path):
    assert_plain(user_visible_strings(path), str(path.relative_to(_REPO)))


_CSHARP_TOKEN = re.compile(r'//[^\n]*|/\*.*?\*/|\$?@"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\\n])*"|'
                           r"'(?:\\.|[^'\\\n])+'", re.S)


def csharp_string_literals(path: Path) -> list[str]:
    """A C# file's string literals, comments and documentation left out, one statement's literals
    joined so a sentence built across lines reads whole."""
    text = path.read_text(encoding="utf-8-sig")
    literals, statement, last = [], [], 0
    for match in _CSHARP_TOKEN.finditer(text):
        token = match.group(0)
        between = text[last:match.start()]
        last = match.end()
        if ";" in between and statement:
            literals.append(" ".join(statement))
            statement = []
        if token.startswith(("//", "/*", "'")):
            continue
        statement.append(token.lstrip("$@").strip('"'))
    if statement:
        literals.append(" ".join(statement))
    return literals


def test_no_string_in_carlanet_s_sources_cites_a_plan_or_its_owner():
    found = []
    for path in CSHARP_SOURCES:
        for literal in csharp_string_literals(path):
            found += [f"{path.relative_to(_REPO)}: {offence}" for offence in offences(literal, CITATIONS)]
    assert not found, "\n  ".join(found[:20])
