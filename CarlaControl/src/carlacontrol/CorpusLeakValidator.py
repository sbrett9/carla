"""Check that a behavioural corpus does not carry its own answer key.

A corpus produced for a detection or pattern-of-life model has two channels. One is ground truth --
which vehicles a scenario planted, and what the author called them -- and it lives in the scenario's
`*.labels.json`. The other is what a consumer reads: the CoT events and the CSV rows written by
`SumoCotBridge`. The second channel is only useful if a planted vehicle is indistinguishable in it
from the traffic it moves among; if any field separates the two groups, a model can reach the answer
without looking at behaviour at all, and every number measured on that corpus is measured on the
wrong thing.

This turns that into a decidable check. Group every record by whether the labels call it planted,
then, field by field, compare the set of values each group takes. A value that occurs in one group
and not in the other is a way of telling them apart, and is reported as a finding with the vehicles
it came from. It catches the obvious cases -- a `special_type` that says `marked`, a column that says
`1` -- and equally the ones nobody wrote deliberately, such as a vehicle type whose length is unique
in the map.

The two directions are not equally serious, so they are reported separately and only one of them is
a defect. A value carried **only by planted vehicles** identifies them, and there is no corpus in
which that is acceptable: `identifying()` returns exactly those, and that is what a gate asserts is
empty. A value carried only by nominal vehicles rules those vehicles out instead, and some of it is
unavoidable -- nine planted vehicles cannot occupy all fourteen of a scenario's vehicle types, so
the types they do not use are nominal-only whatever anyone does. Those are reported for a person to
read, not for a gate to fail on. A field a corpus omits for planted vehicles and writes for the rest
is *not* in that forgiving direction: an absent field counts as a value of its own, so withholding
one shows up as a value only the planted vehicles carry.

The check is deliberately dumb. It knows nothing about what fields mean, it does not decide whether a
difference was intended, and it never inspects behaviour, which is the thing the corpus is *for*.
Deciding that a finding is acceptable is a person's job; not noticing it is what this prevents.
"""
from __future__ import annotations

import csv
import xml.etree.ElementTree as ET
from collections.abc import Iterable, Iterator, Sequence
from dataclasses import dataclass
from pathlib import Path

# Fields that say what a vehicle is called and what it looks like. A planted vehicle that takes a
# value here which nothing else takes has been labelled, whatever the scenario intended.
LABEL_FIELDS = ("type_id", "special_type", "role_name", "marked", "color", "cot_type")

# Physical dimensions. These belong with the cover population unless an author chose otherwise, and
# an author rarely chooses: a vehicle type written a metre longer than the population it hides in
# both labels it and changes its car-following gaps for no stated reason.
DIMENSION_FIELDS = ("length_m", "width_m", "height_m")

DEFAULT_FIELDS = LABEL_FIELDS + DIMENSION_FIELDS

ANNOTATED = "annotated"
NOMINAL = "nominal"

# Stands for a field a record does not carry, so that withholding a field from one group is itself
# comparable rather than invisible.
ABSENT = "<absent>"


@dataclass(frozen=True)
class LeakFinding:
    """One field value that occurs in only one of the two groups."""

    field: str
    value: str
    group: str
    vehicles: tuple[str, ...]
    records: int

    def __str__(self) -> str:
        shown = ", ".join(self.vehicles[:4])
        if len(self.vehicles) > 4:
            shown += f", +{len(self.vehicles) - 4} more"
        return (f"{self.field}={self.value!r} occurs only among {self.group} vehicles "
                f"({self.records} records: {shown})")


class CorpusLeakValidator:
    """Reports the fields by which a corpus's planted vehicles can be told from its nominal ones."""

    def __init__(self, marked_ids: Iterable[str], fields: Sequence[str] = DEFAULT_FIELDS,
                 uid_prefix: str = "SUMO-TRUTH"):
        self.marked_ids = frozenset(marked_ids)
        self.fields = tuple(fields)
        self.uid_prefix = uid_prefix

    # -- sources -----------------------------------------------------------------------------

    def check_csv(self, path: str | Path) -> list[LeakFinding]:
        """Findings over a CSV corpus, one row per vehicle per update."""
        with open(path, newline="", encoding="utf-8") as handle:
            return self.check_records(csv.DictReader(handle))

    def check_xml(self, path: str | Path) -> list[LeakFinding]:
        """Findings over an XML corpus of CoT events."""
        return self.check_records(self.records_from_xml(path))

    @classmethod
    def records_from_xml(cls, path: str | Path) -> Iterator[dict]:
        """Flatten each CoT event into one mapping of the fields this checks.

        The event's own `type` is the CoT type; everything else this looks at is an attribute of the
        `_carla` detail child, which is where the producer puts what it knows about the vehicle.

        Yielded one at a time and the tree emptied behind each: the largest event stream on hand is
        568 MB, which no whole-document parse survives, and neither would a list of its records.
        """
        parser = ET.iterparse(path, events=("start", "end"))
        _, root = next(parser)
        for kind, element in parser:
            if kind != "end" or element.tag != "event":
                continue
            record = {"uid": element.get("uid", ""), "cot_type": element.get("type", "")}
            carla = element.find("detail/_carla")
            if carla is not None:
                record.update(carla.attrib)
            yield record
            root.clear()

    # -- the check ---------------------------------------------------------------------------

    def check_records(self, records: Iterable[dict]) -> list[LeakFinding]:
        """Findings over already-parsed records, each carrying a `uid` and the fields to compare."""
        # Per field, per value: which group it was seen in, which vehicles, and how many records.
        seen: dict[str, dict[str, dict[str, tuple[set[str], int]]]] = {
            name: {ANNOTATED: {}, NOMINAL: {}} for name in self.fields}
        for record in records:
            vehicle = self.vehicle_id(record)
            group = ANNOTATED if vehicle in self.marked_ids else NOMINAL
            for name in self.fields:
                value = str(record[name]) if name in record else ABSENT
                vehicles, count = seen[name][group].get(value, (set(), 0))
                vehicles.add(vehicle)
                seen[name][group][value] = (vehicles, count + 1)

        findings = []
        for name in self.fields:
            annotated, nominal = seen[name][ANNOTATED], seen[name][NOMINAL]
            for group, values, other in ((ANNOTATED, annotated, nominal),
                                         (NOMINAL, nominal, annotated)):
                for value in sorted(set(values) - set(other)):
                    vehicles, count = values[value]
                    findings.append(LeakFinding(field=name, value=value, group=group,
                                                vehicles=tuple(sorted(vehicles)), records=count))
        return findings

    @staticmethod
    def identifying(findings: Iterable[LeakFinding]) -> list[LeakFinding]:
        """The findings that name a planted vehicle, which is the set a gate requires to be empty."""
        return [finding for finding in findings if finding.group == ANNOTATED]

    def vehicle_id(self, record: dict) -> str:
        """The SUMO vehicle id a record belongs to, as the labels sidecar spells it.

        A CSV row and a CoT event both carry it inside the uid; `actor_id` carries it bare where the
        producer publishes one.
        """
        if record.get("actor_id"):
            return str(record["actor_id"])
        uid = str(record.get("uid", ""))
        return uid.removeprefix(f"{self.uid_prefix}-")

    @staticmethod
    def describe(findings: Sequence[LeakFinding]) -> str:
        """One line per finding, for a log or a failing test."""
        if not findings:
            return "no field separates the annotated vehicles from the nominal ones"
        return "\n".join(str(finding) for finding in findings)
