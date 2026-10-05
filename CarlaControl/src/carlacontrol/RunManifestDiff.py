"""Compare two run manifests of one scenario by the supervision rows they name.

The supervision row set is fixed before a run starts: the `(instance_id, participant, phase)` triples
a run's manifest names are wholly determined by the compiled supervision plan, and the run may only
bind them -- fill in onsets, close intervals, record what was drawn. Two runs of one scenario must
therefore name identical triples, differing only in their times and their outcomes, and a diff of
their manifests is the regression test for that rule (`06_Truth_And_Annotation.md` §3.6 point 2,
D6.8). This reads two manifests and makes that diff.

**What a manifest names.** The session writes the manifest as JSON rows, one per line
(`CarlaNet.CoSim.RunManifestWriter`, `04_Contracts.md` §12.7): the plan as declared, one `instance`
row per pattern instance with the intervals it declares, then an `interval_opened` and an
`interval_closed` row as the binder opens and closes each interval, and last a `manifest_closed` row
that lists the intervals still open as the run ended (`open_intervals`, which the binder closes after
it) and those nothing in the run opened or closed (`never_opened`). The triples a closed manifest
names are those of its interval rows and those two lists, which together are every triple its plan
declares.

**A manifest with no terminal row was interrupted.** Its rows are each still whole -- a reader keeps
every line that ends in a line break and leaves off a last line without one -- and every triple it
opened and never closed was open at the interruption, which the writer cannot say and a reader infers
here. It names only the triples its run reached, so it can differ from a whole run's manifest by the
triples after the cut; the diff says it was interrupted beside the difference.

**What is a difference.** A triple named by one manifest and not the other; plans that declare
different triples; and, within either manifest, a triple named that its own plan does not declare
(a row minted at run time), a triple opened or closed twice, or, in a closed manifest, a planned
triple named nowhere. Times, onsets and how each interval closed may differ between runs and are
reported as such, never as a difference.

Read, never corrected: the diff says what the manifests hold and changes nothing. The world truth
track is not compared: its samples are per SUMO frame, and frames differ between runs whenever their
steps do.
"""
from __future__ import annotations

import json
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

OPENED_ROW = "manifest_opened"
CLOSED_ROW = "manifest_closed"
INSTANCE_ROW = "instance"
INTERVAL_OPENED_ROW = "interval_opened"
INTERVAL_CLOSED_ROW = "interval_closed"
DEFECT_ROW = "supervision_defect"

# The fields of an interval row a run binds: they may differ between runs and are reported, not faulted.
BOUND_FIELDS = ("committed_start_s", "observed_start_s", "begun_before_window", "closed_by", "closed_s",
                "committed_end_s")

Triple = tuple[str, "str | None", str]


class ManifestUnreadable(ValueError):
    """A file that is not a run manifest: no opening row, or a complete line that is not a JSON object."""


def describe_triple(triple: Triple) -> str:
    """A triple as one reads it: instance, participant -- none for an absence's vacancy -- and phase."""
    instance_id, participant, phase = triple
    return f"({instance_id}, {participant if participant is not None else 'no participant'}, {phase})"


def _triple(row: dict) -> Triple:
    return (str(row["instance_id"]), row.get("participant"), str(row["phase"]))


@dataclass
class ManifestSupervision:
    """What one manifest says of the supervision plan and its intervals."""

    path: Path
    scenario_id: str | None = None
    plan_id: str | None = None
    plan_sha256: str | None = None
    closed: bool = False
    ended: str | None = None
    planned: list[Triple] = field(default_factory=list)
    opened: Counter = field(default_factory=Counter)
    closed_rows: Counter = field(default_factory=Counter)
    open_at_close: list[Triple] = field(default_factory=list)
    never_opened: list[Triple] = field(default_factory=list)
    defects_found: list[str] = field(default_factory=list)
    bound: dict[Triple, dict] = field(default_factory=dict)

    @property
    def named(self) -> set[Triple]:
        """Every triple the manifest names for an interval: its rows', and its terminal row's lists."""
        return (set(self.opened) | set(self.closed_rows) | set(self.open_at_close)
                | set(self.never_opened))

    @property
    def open_at_interruption(self) -> list[Triple]:
        """For a manifest with no terminal row, every triple opened and never closed; none otherwise."""
        if self.closed:
            return []
        return sorted((triple for triple in self.opened if triple not in self.closed_rows),
                      key=_sort_key)

    def defects(self) -> list[str]:
        """What is wrong within this manifest alone, each in a line."""
        found = []
        planned = set(self.planned)
        for triple in sorted(self.named - planned, key=_sort_key):
            found.append(f"{self.path.name} names {describe_triple(triple)}, which its plan does not "
                         "declare: a row made at run time")
        for triple, count in sorted(self.opened.items(), key=lambda item: _sort_key(item[0])):
            if count > 1:
                found.append(f"{self.path.name} opens {describe_triple(triple)} {count} times")
        for triple, count in sorted(self.closed_rows.items(), key=lambda item: _sort_key(item[0])):
            if count > 1:
                found.append(f"{self.path.name} closes {describe_triple(triple)} {count} times")
        if self.closed:
            for triple in sorted(planned - self.named, key=_sort_key):
                found.append(f"{self.path.name} is closed and names {describe_triple(triple)} nowhere, "
                             "though its plan declares it")
        return found

    def summary(self) -> str:
        """One line: what the manifest is and what it names."""
        plan = (f"plan {self.plan_id} {self.plan_sha256[:12]}" if self.plan_sha256
                else f"plan {self.plan_id}" if self.plan_id else "no plan")
        ending = (f"ended {self.ended}" if self.closed
                  else f"no terminal row (interrupted; {len(self.open_at_interruption)} open at the "
                       "interruption)")
        return (f"{self.path.name}: scenario {self.scenario_id}, {plan}, {ending}; "
                f"{len(self.planned)} planned interval(s), {sum(self.opened.values())} opened, "
                f"{sum(self.closed_rows.values())} closed, {len(self.open_at_close)} open at the close, "
                f"{len(self.never_opened)} never opened")


def _sort_key(triple: Triple) -> tuple[str, str, str]:
    instance_id, participant, phase = triple
    return (instance_id, participant or "", phase)


def read_rows(path: str | Path) -> list[dict]:
    """The rows of a manifest: every line that ends in a line break, each one JSON object. A last line
    without one was cut off as it was written, and is left off."""
    data = Path(path).read_bytes()
    end = data.rfind(b"\n")
    if end < 0:
        return []
    rows = []
    for number, line in enumerate(data[:end].split(b"\n"), start=1):
        try:
            row = json.loads(line.decode("utf-8"))
        except (ValueError, UnicodeDecodeError) as failed:
            raise ManifestUnreadable(f"{path}: line {number} is not JSON ({failed})") from failed
        if not isinstance(row, dict) or "row" not in row:
            raise ManifestUnreadable(f"{path}: line {number} is not a manifest row")
        rows.append(row)
    return rows


def read_manifest(path: str | Path) -> ManifestSupervision:
    """Read what a manifest says of its plan and intervals."""
    path = Path(path)
    rows = read_rows(path)
    if not rows or rows[0]["row"] != OPENED_ROW:
        raise ManifestUnreadable(f"{path}: no {OPENED_ROW} row first; not a run manifest")
    opening = rows[0]
    scenario = opening.get("scenario") or {}
    plan = opening.get("plan") or {}
    read = ManifestSupervision(path=path, scenario_id=scenario.get("scenario_id"),
                               plan_id=plan.get("plan_id"), plan_sha256=plan.get("sha256"))
    for row in rows[1:]:
        kind = row["row"]
        if kind == INSTANCE_ROW:
            for interval in row.get("intervals") or []:
                read.planned.append((str(row["instance_id"]), interval.get("participant"),
                                     str(interval["phase"])))
        elif kind == INTERVAL_OPENED_ROW:
            triple = _triple(row)
            read.opened[triple] += 1
            bound = read.bound.setdefault(triple, {})
            for name in ("committed_start_s", "observed_start_s", "begun_before_window"):
                bound[name] = row.get(name)
        elif kind == INTERVAL_CLOSED_ROW:
            triple = _triple(row)
            read.closed_rows[triple] += 1
            bound = read.bound.setdefault(triple, {})
            for name in ("committed_start_s", "observed_start_s", "begun_before_window", "closed_by",
                         "committed_end_s"):
                bound[name] = row.get(name)
            bound["closed_s"] = row.get("sim_time_s")
        elif kind == DEFECT_ROW:
            read.defects_found.append(str(row.get("defect")))
        elif kind == CLOSED_ROW:
            read.closed = True
            read.ended = row.get("ended")
            close_as = row.get("open_intervals_close_as")
            for listed in row.get("open_intervals") or []:
                triple = _triple(listed)
                read.open_at_close.append(triple)
                bound = read.bound.setdefault(triple, {})
                bound["closed_by"] = close_as
            for listed in row.get("never_opened") or []:
                read.never_opened.append(_triple(listed))
    read.planned.sort(key=_sort_key)
    return read


@dataclass
class ManifestDiff:
    """Two manifests compared: the differences, which fail the rule, and what their runs bound
    differently, which does not."""

    first: ManifestSupervision
    second: ManifestSupervision
    differences: list[str] = field(default_factory=list)
    bound_differently: list[str] = field(default_factory=list)

    @property
    def same(self) -> bool:
        """Whether the two manifests name the same triples, as D6.8 requires."""
        return not self.differences

    def describe(self, show: int = 20) -> list[str]:
        """The comparison as lines to print: each manifest, then what their runs bound differently,
        at most `show` of them."""
        lines = [f"first:  {self.first.summary()}", f"second: {self.second.summary()}"]
        for manifest in (self.first, self.second):
            if manifest.open_at_interruption:
                lines.append(f"{manifest.path.name} was interrupted with "
                             + ", ".join(describe_triple(triple) for triple in manifest.open_at_interruption)
                             + " open")
        if self.same:
            lines.append(f"both name the same {len(self.first.named)} (instance_id, participant, phase) "
                         "triple(s), their plan's own")
        if self.bound_differently:
            lines.append(f"{len(self.bound_differently)} interval(s) bound differently, as runs may "
                         "(times and outcomes, D6.8):")
            lines.extend(f"  {line}" for line in self.bound_differently[:show])
            if len(self.bound_differently) > show:
                lines.append(f"  ... and {len(self.bound_differently) - show} more")
        return lines


def same_scenario(first: ManifestSupervision, second: ManifestSupervision) -> bool:
    """Whether two manifests are of one scenario, as their opening rows name it."""
    return first.scenario_id == second.scenario_id


def diff(first: ManifestSupervision, second: ManifestSupervision) -> ManifestDiff:
    """Compare two manifests of one scenario by the triples they name."""
    result = ManifestDiff(first=first, second=second)
    result.differences.extend(first.defects())
    result.differences.extend(second.defects())
    if first.plan_sha256 != second.plan_sha256:
        result.differences.append(f"the runs bound different plans: {first.plan_sha256} and "
                                  f"{second.plan_sha256}")
    first_planned, second_planned = set(first.planned), set(second.planned)
    for triple in sorted(first_planned - second_planned, key=_sort_key):
        result.differences.append(f"{describe_triple(triple)} is declared by {first.path.name}'s plan "
                                  f"rows only")
    for triple in sorted(second_planned - first_planned, key=_sort_key):
        result.differences.append(f"{describe_triple(triple)} is declared by {second.path.name}'s plan "
                                  f"rows only")
    first_named, second_named = first.named, second.named
    for triple in sorted(first_named - second_named, key=_sort_key):
        result.differences.append(f"{describe_triple(triple)} is named by {first.path.name} only"
                                  + _interrupted(second))
    for triple in sorted(second_named - first_named, key=_sort_key):
        result.differences.append(f"{describe_triple(triple)} is named by {second.path.name} only"
                                  + _interrupted(first))
    for triple in sorted(first_named & second_named, key=_sort_key):
        one, other = first.bound.get(triple, {}), second.bound.get(triple, {})
        changed = [f"{name} {_value(one.get(name))} / {_value(other.get(name))}" for name in BOUND_FIELDS
                   if one.get(name) != other.get(name)]
        if changed:
            result.bound_differently.append(f"{describe_triple(triple)}: " + "; ".join(changed))
    return result


def _interrupted(manifest: ManifestSupervision) -> str:
    return "" if manifest.closed else f" ({manifest.path.name} has no terminal row: its run was interrupted)"


def _value(value: object) -> str:
    if value is None:
        return "none"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float) and value.is_integer():
        return f"{value:g}"
    return str(value)
