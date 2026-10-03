"""Compile a specification's supervision block into the supervision plan, the sole annotation channel.

`06_Truth_And_Annotation.md` D6.1 makes the companion file `<Scenario>.supervision.json` the only place
supervision travels: a SUMO route file is generated, validated against SUMO's own schema, and its
`<param>` is a flat store SUMO's own devices read, so no label, no marked flag and no supervision state
is ever written into it (the compiler's check 52 re-reads the emitted route file to confirm). This
class produces the plan's content, in the shape of 06 §8.1, from what the author declared and from
nothing else: **the set of rows is fixed here, before any run, and the runtime may only bind them**
(06 §3.6). No geometric or photometric predicate writes supervision, and nothing here computes one.

What an author declares, and what it compiles to:

| Declared | Compiles to |
|---|---|
| `instances[]` -- annotated or nominal, over actors | a pattern instance with participants, roles and intervals |
| `cohorts[]` -- a flow, annotated whole-life or unlabelled | a cohort row; `nominal` and intervals refuse (D6.2) |
| `series[]` -- a rota read as a recurring series | a series with one slot per occasion, each realised slot's vehicle an entity in the declared state |
| `absences[]` -- a skipped rota occasion, annotated | an instance with `realisation: absent`, no participant, one `vacancy` interval, and the route and site its vehicle would have had |

Every actor not named in an instance and every flow not named in a cohort is written explicitly as
`unlabelled`, because absence of an element must not stand for an asserted negative (06 §3.1).

**A row says nothing its terms do not define.** A row's `parameters` are keys its labels' terms
declare, each of the declared type (check 56), so a magnitude reaches a consumer with its unit and
meaning. A nominal row carries a copy of its terms' `hard_negative_for`, which an author may restate
and never vary (check 57): the term is the authority and the row a projection of it (06 §3.9(d)), and
`null` there means unspecified, not a negative for nothing. A term's `exemplar_instances` and its
`counterfactual` name subjects of this plan, by the authored names an instance's own counterfactual
uses, and each must resolve (check 8).

The plan carries no solar field and no epoch (06 §8.1): intervals carry their declared seconds and the
civil instant the epoch names them, which is a statement of *when*, not of what the light was.
"""
from __future__ import annotations

from dataclasses import dataclass, field

from carlacontrol.AnnotationVocabulary import SUBJECT_ROLE, VACANCY_PHASE, AnnotationVocabulary
from carlacontrol.CivilTimeResolver import CivilTimeResolver, ResolvedInstant
from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.RotaExpander import RotaEntry, RotaSkip

SUPERVISION_PLAN_VERSION = 1


@dataclass
class PlannedInterval:
    """One declared interval, as a capture window check and the association statistic read it."""

    instance_id: str
    entity_id: str | None
    phase: str
    supervision: str
    begin: ResolvedInstant
    end: ResolvedInstant | None


@dataclass
class SupervisionInputs:
    """What the rest of the compile resolved, which supervision references."""

    scenario_id: str
    actor_departures: dict[str, ResolvedInstant]
    flow_ids: list[str]
    rota_entries: dict[str, list[RotaEntry]] = field(default_factory=dict)
    rota_skips: dict[str, list[RotaSkip]] = field(default_factory=dict)
    rota_templates: dict[str, dict] = field(default_factory=dict)
    areas: dict[str, dict] = field(default_factory=dict)
    skip_routes: dict[str, dict] = field(default_factory=dict)
    skip_sites: dict[str, dict] = field(default_factory=dict)


class SupervisionPlanCompiler:
    """Builds the supervision plan's rows, recording every refusal and warning on the way."""

    def __init__(self, vocabulary: AnnotationVocabulary, resolver: CivilTimeResolver,
                 findings: CompileFindings) -> None:
        self.vocabulary = vocabulary
        self.resolver = resolver
        self.findings = findings
        self.intervals: list[PlannedInterval] = []
        self.referenced_areas: set[str] = set()

    def compile(self, block: dict | None, inputs: SupervisionInputs) -> dict:
        """The plan's rows: instances, series, cohorts and every entity's state."""
        block = block or {}
        self.intervals = []
        self._inputs = inputs
        self._instance_names: dict[str, str] = {}
        series_rows = [self._series(entry) for entry in block.get("series", [])]
        series_by_id = {row["series_id"]: row for row in series_rows if row}
        instances = [self._instance(entry) for entry in block.get("instances", [])]
        instances += [self._absence(entry, series_by_id) for entry in block.get("absences", [])]
        instances = [row for row in instances if row]
        cohorts = self._cohorts(block.get("cohorts", []))
        self._check_counterfactuals(block, instances, series_by_id)
        self._check_exemplars(instances)
        entities = self._entities(instances, series_rows)
        self._check_hard_negatives(instances, series_rows, cohorts)
        return {
            "instances": instances,
            "series": [row for row in series_rows if row],
            "cohorts": cohorts,
            "entities": entities,
        }

    # -- instances --------------------------------------------------------------------------------

    def _instance_id(self, name: str, where: str) -> str | None:
        instance_id = f"{self._inputs.scenario_id}/{name}"
        if instance_id in self._instance_names:
            self.findings.refuse(21, where, f"instance '{name}' is declared twice; an instance id is "
                                 f"{instance_id!r} and must be unique")
            return None
        self._instance_names[instance_id] = where
        return instance_id

    def _instance(self, entry: dict) -> dict | None:
        name = entry["name"]
        where = f"instance {name}"
        instance_id = self._instance_id(name, where)
        supervision = entry["supervision"]
        participants = entry.get("participants", [])
        labels = list(entry.get("labels", []))
        self.vocabulary.check_labels(labels, "entity", "present", where)
        if supervision == "annotated" and not labels:
            self.findings.refuse(18, where, "is annotated and carries no label; an annotation "
                                 "names what the author asserts")
        parameters = dict(entry.get("parameters", {}))
        self.vocabulary.check_parameters(parameters, labels, where)
        hard_negative_for = self._hard_negatives(entry, supervision, labels, where)

        rows = []
        for participant in participants:
            actor = participant["actor"]
            role = participant["role"]
            if actor not in self._inputs.actor_departures:
                self.findings.refuse(19, where, f"participant '{actor}' is not an actor of this "
                                     "scenario" + self._flow_hint(actor))
            self.vocabulary.check_role(role, where)
            rows.append({"entity_id": actor, "role": role, "sumo_id": actor})
        if not participants:
            self.findings.refuse(19, where, "has no participant; only an absence has none, and an "
                                 "absence is declared under 'absences'")
        if len(participants) == 1 and participants[0]["role"] != SUBJECT_ROLE:
            self.findings.refuse(50, where, f"has one participant, whose role is "
                                 f"'{participants[0]['role']}'; a one-participant instance names "
                                 f"it '{SUBJECT_ROLE}', so a consumer never guesses which track "
                                 "the instance is about")
        actors = {p["actor"] for p in participants}
        intervals = [row for row in (self._interval(i, instance_id, supervision, actors, where)
                                     for i in entry.get("intervals", [])) if row]
        aoi_refs = self._aoi_refs(entry.get("aoi_refs", []), where)
        if instance_id is None:
            return None
        return {
            "instance_id": instance_id,
            "supervision": supervision,
            "realisation": "present",
            "labels": labels,
            "parameters": parameters,
            "hard_negative_for": hard_negative_for,
            "counterfactual": entry.get("counterfactual"),
            "series_ref": None,
            "slot_ref": None,
            "aoi_refs": aoi_refs,
            "participants": rows,
            "intervals": intervals,
        }

    def _interval(self, entry: dict, instance_id: str | None, supervision: str, actors: set[str],
                  where: str) -> dict | None:
        participant = entry["participant"]
        phase = entry["phase"]
        if participant not in actors:
            self.findings.refuse(19, where, f"interval participant '{participant}' is not a "
                                 "participant of this instance")
        if phase == VACANCY_PHASE:
            self.findings.refuse(50, where, f"authors the phase '{VACANCY_PHASE}', which the absence "
                                 "writer emits and no author writes")
        self.vocabulary.check_namespaced(phase, "phase", where)
        begin = self.resolver.instant(entry["begin"], f"{where} {phase} begin")
        end = None
        if "end" in entry and "duration" in entry:
            self.findings.refuse(47, where, f"interval {phase} gives both end and duration")
        elif "end" in entry:
            end = self.resolver.instant(entry["end"], f"{where} {phase} end")
        elif "duration" in entry:
            length = self.resolver.duration(entry["duration"], f"{where} {phase} duration")
            if begin is not None and length is not None:
                seconds = begin.seconds + length
                end = ResolvedInstant(seconds, self.resolver.epoch.civil_instant_at(seconds),
                                      entry["duration"], "duration")
        if begin is None:
            return None
        self.resolver.require_in_span(begin, f"{where} {phase} begin")
        if end is not None and end.seconds < begin.seconds:
            self.findings.refuse(47, where, f"interval {phase} ends at {end.civil}, before it "
                                 f"begins at {begin.civil}")
        span_end = self.resolver.span_end_s
        if end is not None and span_end is not None and end.seconds > span_end:
            self.findings.warn(31, where, f"interval {phase} ends at {end.civil}, after the run "
                               f"ends at {self.resolver.epoch.civil_instant_at(span_end)}; it will "
                               "close on scenario_end")
        departure = self._inputs.actor_departures.get(participant)
        if departure is not None and begin.seconds < departure.seconds:
            self.findings.warn(22, where, f"interval {phase} of '{participant}' begins at "
                               f"{begin.civil}, before the vehicle departs at {departure.civil}")
        if instance_id is not None:
            self.intervals.append(PlannedInterval(instance_id, participant, phase, supervision,
                                                  begin, end))
        return {
            "entity_id": participant,
            "phase": phase,
            "declared_start_s": begin.seconds,
            "declared_start_civil": begin.civil,
            "declared_end_s": None if end is None else end.seconds,
            "declared_end_civil": None if end is None else end.civil,
            "declared_duration_s": None if end is None else end.seconds - begin.seconds,
        }

    # -- series and absences ----------------------------------------------------------------------

    def _series(self, entry: dict) -> dict | None:
        series_id = entry["series_id"]
        where = f"series {series_id}"
        rota_id = entry["rota"]
        if rota_id not in self._inputs.rota_templates:
            self.findings.refuse(8, where, f"names rota '{rota_id}', which the specification does "
                                 "not declare")
            return None
        member_role = entry["member_role"]
        self.vocabulary.check_role(member_role, where)
        supervision = entry["supervision"]
        labels = list(entry.get("labels", []))
        if supervision != "unlabelled":
            self.vocabulary.check_labels(labels, "entity", "present", where)
        elif labels:
            self.findings.refuse(18, where, "is unlabelled and carries labels; an unlabelled "
                                 "subject asserts nothing")
        parameters = dict(entry.get("parameters", {}))
        self.vocabulary.check_parameters(parameters, labels, where)
        hard_negative_for = self._hard_negatives(entry, supervision, labels, where)
        length = self.resolver.duration(entry["slot_length"], f"{where} slot_length")
        aoi_by_subject = entry["slot_aoi_refs"]
        entries = self._inputs.rota_entries.get(rota_id, [])
        skips = self._inputs.rota_skips.get(rota_id, [])
        slots = []
        for occasion, realised_by in [(e, e.entry_id) for e in entries] + [(s, None) for s in skips]:
            aoi = aoi_by_subject.get(occasion.subject)
            if aoi is None:
                self.findings.refuse(20, where, f"gives no area for subject '{occasion.subject}'; "
                                     "a slot is sited at an area (06 §3.4)")
            elif aoi not in self._inputs.areas:
                self.findings.refuse(20, where, f"sites subject '{occasion.subject}' at area "
                                     f"'{aoi}', which the world's area table does not hold")
            else:
                self.referenced_areas.add(aoi)
            end = None if length is None else occasion.depart.seconds + length
            slots.append({
                "slot_key": occasion.entry_id,
                "aoi_ref": aoi,
                "declared_start_s": occasion.depart.seconds,
                "declared_start_civil": occasion.depart.civil,
                "declared_end_s": end,
                "declared_end_civil": None if end is None else self.resolver.epoch.civil_instant_at(end),
                "expected_entity_id": occasion.entry_id,
                "realised_by": realised_by,
            })
        slots.sort(key=lambda s: (s["declared_start_s"], s["slot_key"]))
        return {
            "series_id": series_id,
            "rota_ref": rota_id,
            "cadence": "enumerated",
            "member_role": member_role,
            "supervision": supervision,
            "labels": labels,
            "parameters": parameters,
            "hard_negative_for": hard_negative_for,
            "slots": slots,
        }

    def _absence(self, entry: dict, series_by_id: dict[str, dict]) -> dict | None:
        name = entry["name"]
        where = f"absence {name}"
        instance_id = self._instance_id(name, where)
        series = series_by_id.get(entry["series"])
        if series is None:
            self.findings.refuse(8, where, f"names series '{entry['series']}', which the "
                                 "supervision block does not declare")
            return None
        slot = next((s for s in series["slots"] if s["slot_key"] == entry["entry"]), None)
        if slot is None or slot["realised_by"] is not None:
            skipped = [s["slot_key"] for s in series["slots"] if s["realised_by"] is None]
            self.findings.refuse(8, where, f"names occasion '{entry['entry']}', which rota "
                                 f"'{series['rota_ref']}' does not skip; an absence is a slot the "
                                 f"rota leaves unrealised (skipped: {skipped or 'none'})")
            return None
        self.vocabulary.check_labels(entry["labels"], "slot", "absent", where)
        parameters = dict(entry.get("parameters", {}))
        self.vocabulary.check_parameters(parameters, entry["labels"], where)
        aoi_refs = self._aoi_refs(entry.get("aoi_refs") or ([slot["aoi_ref"]] if slot["aoi_ref"]
                                                             else []), where)
        site = self._inputs.skip_sites.get(slot["slot_key"], {})
        begin = self.resolver.instant(slot["declared_start_s"], f"{where} vacancy begin")
        end_s = slot["declared_end_s"]
        end = None if end_s is None else self.resolver.instant(end_s, f"{where} vacancy end")
        if instance_id is not None and begin is not None:
            self.intervals.append(PlannedInterval(instance_id, None, VACANCY_PHASE, "annotated",
                                                  begin, end))
        realised = sum(1 for s in series["slots"] if s["realised_by"] is not None)
        return {
            "instance_id": instance_id,
            "supervision": "annotated",
            "realisation": "absent",
            "labels": list(entry["labels"]),
            "parameters": parameters,
            "hard_negative_for": None,
            "counterfactual": entry.get("counterfactual"),
            "series_ref": series["series_id"],
            "slot_ref": slot["slot_key"],
            "aoi_refs": aoi_refs,
            "participants": [],
            "expected": {
                "role": series["member_role"],
                "expected_entity_id": slot["expected_entity_id"],
                "route": self._inputs.skip_routes.get(slot["slot_key"], {}),
                "site_lane": site.get("site_lane"),
                "site_pos_m": site.get("site_pos_m"),
                "declared_start_s": slot["declared_start_s"],
                "declared_end_s": slot["declared_end_s"],
            },
            "intervals": [{
                "entity_id": None, "phase": VACANCY_PHASE,
                "declared_start_s": slot["declared_start_s"],
                "declared_start_civil": slot["declared_start_civil"],
                "declared_end_s": slot["declared_end_s"],
                "declared_end_civil": slot["declared_end_civil"],
                "declared_duration_s": (None if slot["declared_end_s"] is None
                                        else slot["declared_end_s"] - slot["declared_start_s"]),
            }],
            "counter_evidence": {
                "series_slots_total": len(series["slots"]),
                "series_slots_realised": realised,
            },
        }

    # -- cohorts ----------------------------------------------------------------------------------

    def _cohorts(self, declared: list[dict]) -> list[dict]:
        by_flow: dict[str, dict] = {}
        for entry in declared:
            flow = entry["flow"]
            where = f"cohort {flow}"
            if flow not in self._inputs.flow_ids:
                self.findings.refuse(8, where, "names a flow the specification does not declare")
                continue
            if flow in by_flow:
                self.findings.refuse(21, where, "is declared twice")
                continue
            supervision = entry["supervision"]
            if supervision == "nominal":
                self.findings.refuse(49, where, "is nominal. A flow authors a population, not each "
                                     "member's behaviour, so nothing can assert that every member is "
                                     "executing no target pattern (06 D6.2)")
            if entry.get("intervals"):
                self.findings.refuse(23, where, "carries intervals. A cohort's members are unknown "
                                     "until the run, so it carries only a whole-life annotation "
                                     "(06 D6.2)")
            labels = list(entry.get("labels", []))
            if supervision == "annotated":
                if not labels:
                    self.findings.refuse(18, where, "is annotated and carries no label")
                self.vocabulary.check_labels(labels, "cohort", "present", where)
            elif labels:
                self.findings.refuse(18, where, "is unlabelled and carries labels")
            parameters = dict(entry.get("parameters", {}))
            self.vocabulary.check_parameters(parameters, labels, where)
            by_flow[flow] = {"flow_id": flow, "supervision": supervision, "labels": labels,
                             "parameters": parameters}
        return [by_flow.get(flow, {"flow_id": flow, "supervision": "unlabelled", "labels": [],
                                   "parameters": {}})
                for flow in self._inputs.flow_ids]

    # -- entities ---------------------------------------------------------------------------------

    def _entities(self, instances: list[dict], series_rows: list[dict | None]) -> list[dict]:
        states: dict[str, set[str]] = {actor: set() for actor in self._inputs.actor_departures}
        refs: dict[str, list[str]] = {actor: [] for actor in self._inputs.actor_departures}
        for row in instances:
            for participant in row["participants"]:
                if participant["entity_id"] in states:
                    states[participant["entity_id"]].add(row["supervision"])
                    refs[participant["entity_id"]].append(row["instance_id"])
        for series in series_rows:
            if not series or series["supervision"] == "unlabelled":
                continue
            for slot in series["slots"]:
                if slot["realised_by"] in states:
                    states[slot["realised_by"]].add(series["supervision"])
                    refs[slot["realised_by"]].append(f"series:{series['series_id']}")
        return [{"entity_id": actor,
                 "supervision": sorted(states[actor]) or ["unlabelled"],
                 "refs": refs[actor]}
                for actor in sorted(states)]

    # -- cross-references -------------------------------------------------------------------------

    def _check_counterfactuals(self, block: dict, instances: list[dict],
                               series_by_id: dict[str, dict]) -> None:
        """Check 8: every counterfactual, a row's or a term's, names what this scenario declares.

        A term's counterfactual of kind `term` resolves inside the vocabulary, which checks it
        (check 18); one naming a series, a cohort or an instance points at a subject of this plan.
        """
        names = {row["instance_id"].split("/", 1)[1] for row in instances}
        known = {"series": set(series_by_id), "instance": names,
                 "cohort": set(self._inputs.flow_ids), "term": set(self.vocabulary.terms)}
        declared = [(f"{group[:-1]} {entry['name']}", entry.get("counterfactual"))
                    for group in ("instances", "absences") for entry in block.get(group, [])]
        declared += [(f"namespace {spelled.split(':', 1)[0]}", term["counterfactual"])
                     for spelled, term in sorted(self.vocabulary.terms.items())
                     if term.get("counterfactual") and term["counterfactual"]["kind"] != "term"]
        for where, counterfactual in declared:
            if not counterfactual:
                continue
            kind, ref = counterfactual["kind"], counterfactual["ref"]
            if ref not in known[kind]:
                self.findings.refuse(8, where, f"counterfactual {kind} '{ref}' names nothing this "
                                     "scenario declares; a counterfactual is a resolved reference "
                                     "(06 §3.9(c))")

    def _check_exemplars(self, instances: list[dict]) -> None:
        """Check 8: every exemplar a term names is an instance of this plan (06 §3.8).

        An exemplar is named as an instance's counterfactual names one, by its authored name, which
        the plan's id prefixes with the scenario id: so it resolves in every member of a sweep,
        whose ids carry the member's own scenario id. Prose about a term is unverifiable; an
        exemplar is checkable because it resolves.
        """
        names = {row["instance_id"].split("/", 1)[1] for row in instances}
        for spelled, term in sorted(self.vocabulary.terms.items()):
            for ref in term.get("exemplar_instances", []):
                if ref in names:
                    continue
                named = ref.rsplit("/", 1)[-1]
                hint = (f"; an exemplar names the instance as the specification does, '{named}', "
                        "not by its id in one plan, which the members of a sweep do not share"
                        if named in names else "")
                self.findings.refuse(8, f"namespace {spelled.split(':', 1)[0]}",
                                     f"'{spelled}' exemplar_instances names '{ref}', which is no "
                                     f"instance or absence of this scenario{hint}. An exemplar "
                                     "resolves or it is prose (06 §3.8)")

    def _hard_negatives(self, entry: dict, supervision: str, labels: list[str],
                        where: str) -> list[str] | None:
        """Check 57: a nominal subject carries its terms' `hard_negative_for`, and only that.

        The term is the authority and the row a copy of it (06 §3.9(d)), so a nominal row carries the
        union of its labels' declarations, an author who restates the set restates it exactly, and a
        subject that is not nominal declares none: the field narrows an authored negative. `None`
        where nothing narrows it, which means unspecified, not a negative for nothing.
        """
        projected = self.vocabulary.hard_negatives_of(labels) if supervision == "nominal" else []
        declared = entry.get("hard_negative_for")
        if declared is not None and set(declared) != set(projected):
            if supervision != "nominal":
                self.findings.refuse(57, where, f"is {supervision} and declares hard_negative_for "
                                     f"{declared}. The field narrows a nominal subject's negative "
                                     f"(06 §3.9(d)), and an {supervision} subject asserts none")
            else:
                self.findings.refuse(57, where, f"declares hard_negative_for {declared}, and its "
                                     f"labels' terms declare {projected or 'none'}. The term is the "
                                     "authority and the row a copy of it (06 §3.9(d)): declare the "
                                     "set on the term, and restate it here exactly or not at all")
        return projected or None

    def _check_hard_negatives(self, instances: list[dict], series_rows: list[dict | None],
                              cohorts: list[dict]) -> None:
        """Check 24: a scenario asserting any positive asserts some negative too."""
        states = {row["supervision"] for row in instances}
        states |= {row["supervision"] for row in series_rows if row}
        states |= {row["supervision"] for row in cohorts}
        if "annotated" in states and "nominal" not in states:
            self.findings.warn(24, "supervision", "annotates subjects and declares none nominal; "
                               "hard negatives come only from authored entities (06 D6.2, "
                               "doc 20 §2.7)")

    def _aoi_refs(self, refs: list[str], where: str) -> list[str]:
        for ref in refs:
            if ref not in self._inputs.areas:
                known = sorted(self._inputs.areas)
                self.findings.refuse(20, where, f"aoi_ref '{ref}' is not an area of this world"
                                     + (f"; the area table holds {', '.join(known)}" if known
                                        else "; the world's area table holds none"))
            else:
                self.referenced_areas.add(ref)
                kind = self._inputs.areas[ref].get("kind") or ""
                self.vocabulary.check_namespaced(kind, "area kind", where)
        return list(refs)

    def _flow_hint(self, name: str) -> str:
        if name in self._inputs.flow_ids:
            return f"; '{name}' is a flow, whose members are a cohort and are declared under 'cohorts'"
        return ""
