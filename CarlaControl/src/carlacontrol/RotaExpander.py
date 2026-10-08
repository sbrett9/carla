"""A rota: the one repetition form a specification has, written as days x civil clocks x subjects.

The sizing scenario's guard rota is a triple loop over seven days, three shift changes and sixteen
towers with one iteration skipped to leave a posting unmanned (`CarlaControl/scripts/make_bahonar_scenario.py`,
`tower_postings`). As a rota it is one block:

```jsonc
{"id": "guard_posting", "days": "0..6", "at": ["07:00", "15:00", "23:00"],
 "subjects": {"place_set": "guard_towers"},
 "id_pattern": "guard_d{day}_h{hour}_t{subject_index}",
 "template": { ...an actor, with "$subject" wherever the subject's place goes... },
 "skip": [{"day": 4, "at": "07:00", "subject_index": 3,
           "because": "the no-show: this post is not manned this shift"}]}
```

**It is deliberately not a loop** (`07_Scenario_Authoring.md` §3.5.1, §12 question 6): no body, no
variables, no conditionals -- a cross product of declared days, declared clocks and declared subjects,
with an explicit exclusion list. Each entry departs at `d<day> <clock>` under the epoch, resolved by
`CivilTimeResolver`, so the civil meaning of every departure is recoverable from the artifact.

**A skip removes an occasion, so it must hit something** (check 48). A skip matching no entry is an
anomaly that was never planted -- the whole of the sizing scenario's no-show rests on one `continue`
firing -- and a skip carries its reason as a field, so the report can state it. A skipped occasion
writes no trip and no supervision row: there is no vehicle for a label to follow (06 §3.5), so the
gap it makes in the schedule is stated by the report, and an author who wants it in the record labels
the vehicle that deviates; nothing is written per frame for the gap.

`id_pattern` fields: `{day}`, `{hour}` and `{minute}` of the clock as integers, `{subject_index}`,
and `{subject}`, the subject's place name.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from carlacontrol.CivilTimeResolver import CivilTimeResolver, ResolvedInstant
from carlacontrol.CompileFindings import CompileFindings

ROTA_CHECK = 48

_DAY_RANGE = re.compile(r"^(\d+)\.\.(\d+)$")
_CLOCK = re.compile(r"^(\d{1,2}):(\d{2})(?::(\d{2}))?$")
_PATTERN_FIELDS = frozenset({"day", "hour", "minute", "subject_index", "subject"})


@dataclass(frozen=True)
class RotaEntry:
    """One occasion of a rota: which day, clock and subject, the id it gets, and when it departs."""

    rota_id: str
    entry_id: str
    day: int
    clock: str
    subject_index: int
    subject: str
    depart: ResolvedInstant

    @property
    def slot_key(self) -> str:
        """The occasion's key within its rota, stable across a rebuild: `d<day>_<clock>_s<index>`."""
        return f"d{self.day}_{self.clock.replace(':', '')}_s{self.subject_index}"


@dataclass(frozen=True)
class RotaSkip:
    """An occasion the rota deliberately does not produce, and why."""

    rota_id: str
    entry_id: str
    day: int
    clock: str
    subject_index: int
    subject: str
    depart: ResolvedInstant
    because: str

    @property
    def slot_key(self) -> str:
        return f"d{self.day}_{self.clock.replace(':', '')}_s{self.subject_index}"


class RotaExpander:
    """Expands rotas into their entries and skips, recording what refuses under check 48."""

    def __init__(self, resolver: CivilTimeResolver, findings: CompileFindings) -> None:
        self.resolver = resolver
        self.findings = findings

    def expand(self, rota: dict, subjects: list[str]) -> tuple[list[RotaEntry], list[RotaSkip]]:
        """The rota's entries and skips. Empty lists when it refuses."""
        rota_id = str(rota.get("id", ""))
        where = f"rotas[{rota_id}]"
        days = self._days(rota.get("days"), where)
        clocks = self._clocks(rota.get("at"), where)
        pattern = rota.get("id_pattern")
        if days is None or clocks is None or not self._pattern_ok(pattern, where):
            return [], []
        if not subjects:
            self.findings.refuse(ROTA_CHECK, where, "has no subjects, so it expands to nothing")
            return [], []

        skips = list(rota.get("skip") or [])
        matched = [0] * len(skips)
        entries: list[RotaEntry] = []
        skipped: list[RotaSkip] = []
        seen: dict[str, str] = {}
        for day in days:
            for clock in clocks:
                depart = self.resolver.instant(f"d{day} {clock}", f"{where} d{day} {clock}")
                if depart is None:
                    continue
                hour, minute = (int(part) for part in clock.split(":")[:2])
                for index, subject in enumerate(subjects):
                    entry_id = pattern.format(day=day, hour=hour, minute=minute,
                                              subject_index=index, subject=subject)
                    occasion = f"d{day} {clock} subject {index} ({subject})"
                    if entry_id in seen:
                        self.findings.refuse(
                            ROTA_CHECK, where,
                            f"id_pattern gives {entry_id!r} to both {seen[entry_id]} and "
                            f"{occasion}; an id names one vehicle")
                        continue
                    seen[entry_id] = occasion
                    hit = [n for n, skip in enumerate(skips)
                           if self._skip_matches(skip, day, clock, index, subject)]
                    for n in hit:
                        matched[n] += 1
                    if hit:
                        skipped.append(RotaSkip(rota_id, entry_id, day, clock, index, subject,
                                                depart, str(skips[hit[0]].get("because", ""))))
                    else:
                        entries.append(RotaEntry(rota_id, entry_id, day, clock, index, subject,
                                                 depart))

        for n, skip in enumerate(skips):
            if not str(skip.get("because", "")).strip():
                self.findings.refuse(ROTA_CHECK, where, f"skip {skip} gives no 'because'; a "
                                     "deliberate skip states its reason")
            if matched[n] == 0:
                self.findings.refuse(ROTA_CHECK, where,
                                     f"skip {skip} matches no occasion the schedule produces, so it "
                                     "removes nothing")
            elif matched[n] > 1:
                self.findings.refuse(ROTA_CHECK, where,
                                     f"skip {skip} matches {matched[n]} occasions; a skip names "
                                     "one day, one clock and one subject")
        if not entries and not skipped:
            self.findings.refuse(ROTA_CHECK, where, "expands to no entries")
        return entries, skipped

    @staticmethod
    def _skip_matches(skip: dict, day: int, clock: str, index: int, subject: str) -> bool:
        if skip.get("day") != day:
            return False
        if RotaExpander._normal_clock(str(skip.get("at", ""))) != clock:
            return False
        if "subject_index" in skip and skip["subject_index"] != index:
            return False
        if "subject" in skip and skip["subject"] != subject:
            return False
        return "subject_index" in skip or "subject" in skip

    def _days(self, value: object, where: str) -> list[int] | None:
        if isinstance(value, str) and _DAY_RANGE.match(value.strip()):
            first, last = (int(n) for n in _DAY_RANGE.match(value.strip()).groups())
            days = list(range(first, last + 1))
        elif isinstance(value, list) and all(isinstance(d, int) and not isinstance(d, bool)
                                             and d >= 0 for d in value):
            days = list(value)
        else:
            self.findings.refuse(ROTA_CHECK, where, f"days {value!r} is neither 'first..last' nor a "
                                 "list of day numbers")
            return None
        if not days:
            self.findings.refuse(ROTA_CHECK, where, "declares no days, so it expands to nothing")
            return None
        if len(set(days)) != len(days):
            self.findings.refuse(ROTA_CHECK, where, f"names a day twice in {value!r}")
            return None
        return days

    def _clocks(self, value: object, where: str) -> list[str] | None:
        if not isinstance(value, list) or not value:
            self.findings.refuse(ROTA_CHECK, where, "declares no 'at' clocks, so it expands to "
                                 "nothing")
            return None
        clocks = []
        for item in value:
            clock = self._normal_clock(str(item))
            if clock is None:
                self.findings.refuse(47, where, f"'at' entry {item!r} is not a civil clock "
                                     "HH:MM[:SS]; a schedule's day comes from 'days'")
                return None
            clocks.append(clock)
        if len(set(clocks)) != len(clocks):
            self.findings.refuse(ROTA_CHECK, where, f"names a clock twice in {value!r}")
            return None
        return clocks

    @staticmethod
    def _normal_clock(text: str) -> str | None:
        """`7:00` and `07:00:00` both as `07:00`; a clock with seconds keeps them."""
        match = _CLOCK.match(text.strip())
        if not match:
            return None
        hour, minute, second = int(match[1]), int(match[2]), int(match[3] or 0)
        if hour > 23 or minute > 59 or second > 59:
            return None
        return f"{hour:02d}:{minute:02d}" + (f":{second:02d}" if second else "")

    def _pattern_ok(self, pattern: object, where: str) -> bool:
        if not isinstance(pattern, str) or not pattern:
            self.findings.refuse(ROTA_CHECK, where, "declares no id_pattern")
            return False
        fields = set(re.findall(r"\{(\w+)\}", pattern))
        unknown = fields - _PATTERN_FIELDS
        if unknown:
            self.findings.refuse(ROTA_CHECK, where, f"id_pattern names {sorted(unknown)}; it may use "
                                 f"{sorted(_PATTERN_FIELDS)}")
            return False
        return True
