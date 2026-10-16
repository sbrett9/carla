"""The CoT affiliation a run draws each vehicle population with: a display convention, and no more.

A Cursor-on-Target type carries an affiliation -- `a-n-G-E-V` neutral, `a-f-G-E-V` friendly -- and
a TAK client colours every track by it. Drawing a port's civilian traffic neutral and its naval
traffic friendly is what makes a live picture of the port readable at a glance. That is a choice
about how one run is displayed and says nothing about what the scenario asserts, so it is not part
of the specification, not covered by its digest and not checked by the compiler
(`06_Truth_And_Annotation.md` §9.1, `07_Scenario_Authoring.md` §3.4.1). It is a file the run is
given, kept beside the scenario it suits as `<scenario>.display.json` -- named for the stem of the
scenario's `.sumocfg`, so a run of that scenario finds it without being told (`beside`) -- and the
run records which one it drew with.

A convention names **populations**, as the telemetry can tell them apart: in a compiled scenario,
the vehicle class a vType carries in its `carla:class_id` parameter -- a class draws several
bodies, one vType each, and all of them are one population -- and in a hand-written route file,
whose types name no class, the vType id itself. A population the convention does not name takes
the run's default.

**It never names a vehicle.** Which vehicles a scenario planted is supervision, and the CoT type is
the one field every consumer of the feed reads, so a letter that picked the planted vehicles out
would put the answer there (doc 20 decision 9). A file carrying anything but populations -- a
`marked_ids`, say -- is refused rather than partly read.

The same map once travelled in a scenario's legacy `*.labels.json`, beside a second half that gave
every anomaly type `u`. That half was the label written into the CoT type, which D6.18 names a
defect wherever this code produces ground truth, and it is not a display convention:
`from_legacy_labels` keeps the display half and withholds the other.

A convention file is also checked against its published schema (`schema()`,
`display_convention.schema.json`), after the reader's own checks, so a value of the wrong type is
refused in the schema's words.
"""
from __future__ import annotations

import json
from collections.abc import Mapping
from pathlib import Path

from carlacontrol.SchemaPublication import SchemaPublication

CONVENTION_VERSION = 1

# A scenario's own convention is `<scenario>.display.json` beside its `<scenario>.sumocfg`.
SUFFIX = ".display.json"

# The keys a convention file carries. Anything else is refused.
VERSION_KEY = "convention_version"
DESCRIPTION_KEY = "description"
AFFILIATIONS_KEY = "affiliation_by_type"

# The affiliations the second atom of a CoT type can take: pending, unknown, assumed friend, friend,
# neutral, suspect, hostile, joker, faker, none specified and other.
COT_AFFILIATIONS = frozenset({"p", "u", "a", "f", "n", "s", "h", "j", "k", "o", "x"})

# In a legacy labels file, `u` is what the generator gave every anomaly type and no other: the
# supervision half of the map, written under the same key a convention uses.
LEGACY_ANOMALY_AFFILIATION = "u"

# The legacy labels key naming planted vehicles, refused by name in a convention file.
PLANTED_VEHICLES_KEY = "marked_ids"


class CotDisplayConvention:
    """How one run draws each vehicle population in Cursor-on-Target: an affiliation for each."""

    def __init__(self, affiliation_by_type: Mapping[str, str], source: str = "",
                 withheld: tuple[str, ...] = ()) -> None:
        wrong = [f"{population!r}: {affiliation!r}"
                 for population, affiliation in affiliation_by_type.items()
                 if not isinstance(population, str) or not population
                 or affiliation not in COT_AFFILIATIONS]
        if wrong:
            raise ValueError(
                f"{source or 'the display convention'} gives {', '.join(wrong)}: each population "
                f"is a type or class name and each affiliation one of "
                f"{', '.join(sorted(COT_AFFILIATIONS))}, as a CoT type spells it")
        self.affiliation_by_type = dict(affiliation_by_type)
        # Where the convention came from, which the run records beside what it wrote.
        self.source = source
        # Populations a legacy labels file gave an affiliation this convention does not apply,
        # because that affiliation was the file's supervision half.
        self.withheld = withheld

    def __len__(self) -> int:
        return len(self.affiliation_by_type)

    @staticmethod
    def beside(config_path: str | Path) -> Path:
        """Where a scenario's own convention sits: `<scenario>.display.json` beside its `.sumocfg`.

        The scenario is the configuration's stem, which for a compiled scenario is its
        `scenario_id`. The path is returned whether or not a file is there.
        """
        config_path = Path(config_path)
        return config_path.with_name(config_path.stem + SUFFIX)

    @classmethod
    def from_file(cls, path: str | Path) -> CotDisplayConvention:
        """Read a convention file, refusing one that carries anything but populations."""
        path = Path(path)
        document = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(document, dict):
            raise ValueError(f"{path.name} is not a display convention: it holds no JSON object")
        if PLANTED_VEHICLES_KEY in document:
            raise ValueError(
                f"{path.name} names planted vehicles ({PLANTED_VEHICLES_KEY}). A display "
                "convention names populations and never vehicles: an affiliation that picked the "
                "planted vehicles out would write the answer into the CoT type every consumer "
                "reads")
        unknown = sorted(set(document) - {VERSION_KEY, DESCRIPTION_KEY, AFFILIATIONS_KEY})
        if unknown:
            raise ValueError(f"{path.name} carries {', '.join(unknown)}, which a display "
                             f"convention does not have; it has {VERSION_KEY}, {DESCRIPTION_KEY} "
                             f"and {AFFILIATIONS_KEY}")
        if document.get(VERSION_KEY) != CONVENTION_VERSION:
            raise ValueError(f"{path.name} is {VERSION_KEY} {document.get(VERSION_KEY)!r}; this "
                             f"reader implements {CONVENTION_VERSION}")
        mapping = document.get(AFFILIATIONS_KEY)
        if not isinstance(mapping, dict):
            raise ValueError(f"{path.name} has no {AFFILIATIONS_KEY} object")
        convention = cls(mapping, source=path.name)
        schema = cls.schema()
        problems = SchemaPublication.problems(document, schema)
        if problems:
            raise ValueError(SchemaPublication.refusal(path.name, schema, problems))
        return convention

    @staticmethod
    def schema() -> dict:
        """The convention file's schema, as published, from the keys and affiliations this reader
        accepts."""
        return SchemaPublication.document(
            "display-convention", CONVENTION_VERSION, "CoT display convention",
            "How one run draws each vehicle population in Cursor-on-Target: the affiliation letter "
            "a TAK client colors its tracks by. A choice about display, not part of the scenario: "
            f"kept beside the scenario as <scenario>{SUFFIX}, and read by carla-cot-telemetry. It "
            "names populations and never single vehicles.",
            {"type": "object", "additionalProperties": False,
             "required": [VERSION_KEY, AFFILIATIONS_KEY],
             "properties": {
                 VERSION_KEY: {"const": CONVENTION_VERSION,
                               "description": "The format version of this file. A reader refuses "
                                              "any other."},
                 DESCRIPTION_KEY: {"type": "string",
                                   "description": "What the convention is for, in a sentence or "
                                                  "two. Not read."},
                 AFFILIATIONS_KEY: {
                     "type": "object",
                     "additionalProperties": {"enum": sorted(COT_AFFILIATIONS)},
                     "description": "Each population's affiliation: the second letter of a CoT "
                                    "type. A population is a vehicle class id (a vType's "
                                    "carla:class_id) in a compiled scenario, or a vType id in a "
                                    "hand-written route file. A population left out takes the "
                                    "run's default."}}})

    @classmethod
    def from_legacy_labels(cls, labels: Mapping, source: str = "") -> CotDisplayConvention:
        """The display half of a legacy labels file's affiliation map, its anomaly half withheld.

        The labels file wrote both halves under one key and told them apart only by the letter: its
        generator gave `u` to every anomaly type and to nothing else. So `u` is the supervision half
        here, and the types it named take whatever the run gives a population the convention does
        not name.
        """
        mapping = labels.get(AFFILIATIONS_KEY) or {}
        withheld = tuple(sorted(population for population, affiliation in mapping.items()
                                if affiliation == LEGACY_ANOMALY_AFFILIATION))
        return cls({population: affiliation for population, affiliation in mapping.items()
                    if population not in withheld}, source=source, withheld=withheld)
