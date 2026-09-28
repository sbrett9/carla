"""Compile a sweep: one base specification, axes varied over it, and counterfactual pairs, each member
compiled in full.

A corpus is many runs of one scenario with parameters varied, and that only means something if two
members differ only where they were meant to (`07_Scenario_Authoring.md` §7). A sweep is a file,
`<Sweep>.sweep.json`:

```jsonc
{"sweep_version": 1, "sweep_id": "probe_dwell",
 "base": "probe.scenario.json",
 "axes": [{"path": "actors.probe.stops[0].duration", "values": ["5m", "10m", "20m"]},
          {"path": "seeds.sumo", "values": [42, 43]}],
 "pairing": "cross",                    // cross | zip
 "illumination": "hold",                // hold (default) | vary | factorial
 "counterfactuals": [{"actor": "probe", "mode": "nominal", "remove": ["stops"]}]}
```

**Every member is a full compile** -- its own routes, supervision plan, resolution report and lock -- so
a member that would not route fails here, not at run seventeen of forty. Member ids are derived from
the base scenario id and the member's axis values, with no counter and no timestamp, so members are
joinable across a rebuild. The swept values are recorded per member, beside the member's epoch, its
windows' civil dates and the sun each opens under, in `<sweep_id>.sweep-index.json`.

**Illumination is an axis in its own right, and the compiler decides which axes it is** (§7.2, §7.4,
check 43): any axis whose path touches `epoch`, `illumination` or a capture window's `begin` changes
the light, whether or not it says so. `hold` refuses such an axis, `vary` refuses any other, and
`factorial` warns, naming the cells, so a crossed design cannot be read as a controlled comparison.
`epoch.date` sweeps the date the epoch falls on -- the one illumination axis that holds every authored
behaviour fixed, because behaviour is indexed on the clock and the sun on both (§7.2.1): it moves
`civil_datetime` and `utc_datetime` together, so the epoch stays valid.

**A counterfactual pair holds the inputs fixed except one actor** (§7.3, D7.12, D7.24): `absent`
removes the actor; `nominal` keeps its type, route and timing, removes what the author names as its
anomalous element, and states it `nominal`; `displaced` moves it by a declared shift in time and/or a
declared place substitution. A car-following model reacts to what is in front of it, so the pair is
*identical inputs except one vehicle*, never identical trajectories, and the index says so. `absent`
and `nominal` inherit the base's epoch, windows and illumination verbatim; `displaced` in time moves the
sun and is recorded `illumination_differs: true`.
"""
from __future__ import annotations

import copy
import hashlib
import itertools
import json
import re
from datetime import datetime
from pathlib import Path

from carlacontrol.CivilTimeResolver import CivilTimeResolver
from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.ScenarioCompiler import ScenarioCompiler
from carlacontrol.ScenarioEpoch import ScenarioEpoch, ScenarioEpochRefusedError
from carlacontrol.ScenarioSchema import ScenarioSchema

SWEEP_VERSION = 1
SWEEP_CHECK = 43

SWEEP_SCHEMA: dict = {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "$id": "https://carla.local/schemas/sweep.schema.json",
    "title": "Scenario sweep",
    "type": "object", "additionalProperties": False,
    "required": ["sweep_version", "sweep_id", "base"],
    "properties": {
        "sweep_version": {"const": SWEEP_VERSION},
        "sweep_id": {"type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9_.-]*$"},
        "base": {"type": "string", "minLength": 1},
        "axes": {"type": "array", "items": {
            "type": "object", "additionalProperties": False, "required": ["path", "values"],
            "properties": {"path": {"type": "string", "minLength": 1},
                           "values": {"type": "array", "minItems": 1},
                           "kind": {"enum": ["illumination", "behaviour"]}}}},
        "pairing": {"enum": ["cross", "zip"]},
        "illumination": {"enum": ["hold", "vary", "factorial"]},
        "counterfactuals": {"type": "array", "items": {
            "type": "object", "additionalProperties": False, "required": ["actor", "mode"],
            "properties": {"actor": {"type": "string", "minLength": 1},
                           "mode": {"enum": ["absent", "nominal", "displaced"]},
                           "remove": {"type": "array", "items": {"enum": ["stops", "via"]}},
                           "labels": {"type": "array", "items": {"type": "string"}},
                           "shift": {"anyOf": [{"type": "number"}, {"type": "string"}]},
                           "places": {"type": "object",
                                      "additionalProperties": {"type": "string"}}}}},
    },
}

_ILLUMINATION_PATH = re.compile(r"^(epoch(\.|$)|illumination(\.|$)|capture_windows\.[^.]+\.begin$)")
_SEGMENT = re.compile(r"^(?P<key>[^\[\]]+)(?:\[(?P<index>\d+)\])?$")
_LIST_KEYS = ("id", "name", "class_id", "series_id", "flow")


class ScenarioSweep:
    """Expands a sweep into members, compiles each, and writes the sweep index."""

    def __init__(self, installation) -> None:
        self.installation = installation

    @staticmethod
    def write_schema(path: str | Path) -> Path:
        """Publish `sweep.schema.json` for the authoring skill, from the schema this class checks."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(SWEEP_SCHEMA, indent=2) + "\n", encoding="utf-8", newline="\n")
        return path

    def compile(self, sweep_path: str | Path, out_dir: str | Path) -> dict:
        """Compile every member; return and write the index. Refusals are in `index['findings']`."""
        self.sweep_path = Path(sweep_path).resolve()
        self.out_dir = Path(out_dir)
        self.findings = CompileFindings()
        sweep = json.loads(self.sweep_path.read_text(encoding="utf-8"))
        index = {"sweep_version": SWEEP_VERSION, "sweep_id": sweep.get("sweep_id"),
                 "members": [], "pairs": [], "findings": []}
        for problem in ScenarioSchema.validate_against(sweep, SWEEP_SCHEMA):
            self.findings.refuse(53, "sweep", problem)
        if self.findings.refused:
            return self._finish(index)
        base_path = (self.sweep_path.parent / sweep["base"]).resolve()
        base = json.loads(base_path.read_text(encoding="utf-8"))
        index.update({"base": base_path.name,
                      "base_sha256": hashlib.sha256(base_path.read_bytes()).hexdigest(),
                      "pairing": sweep.get("pairing", "cross"),
                      "illumination": sweep.get("illumination", "hold")})
        axes = [dict(axis, illumination_axis=bool(_ILLUMINATION_PATH.match(axis["path"])))
                for axis in sweep.get("axes", [])]
        index["axes"] = [{"path": a["path"], "values": a["values"],
                          "illumination_axis": a["illumination_axis"]} for a in axes]
        self._check_illumination_rule(axes, index["illumination"])
        assignments = self._assignments(axes, index["pairing"])
        if self.findings.refused:
            return self._finish(index)
        members = []
        for assignment in assignments:
            document = copy.deepcopy(base)
            for path, value in assignment:
                self._apply(document, path, value)
            members.append((self._member_id(base["scenario_id"], assignment), assignment, document))
        if self.findings.refused:
            return self._finish(index)
        for member_id, assignment, document in members:
            index["members"].append(self._compile_member(member_id, assignment, document, base_path))
            for counterfactual in sweep.get("counterfactuals", []):
                pair = self._counterfactual(member_id, assignment, document, counterfactual,
                                            base_path)
                if pair is not None:
                    index["members"].append(pair["member"])
                    index["pairs"].append(pair["pair"])
        return self._finish(index)

    # -- axes ---------------------------------------------------------------------------------------

    def _check_illumination_rule(self, axes: list[dict], rule: str) -> None:
        lighting = [a["path"] for a in axes if a["illumination_axis"]]
        behaviour = [a["path"] for a in axes if not a["illumination_axis"]]
        for axis in axes:
            declared = axis.get("kind")
            if declared == "behaviour" and axis["illumination_axis"]:
                self.findings.refuse(SWEEP_CHECK, f"axis {axis['path']}",
                                     "is declared a behaviour axis, and it changes the light: its "
                                     "path touches the epoch, the illumination policy or a "
                                     "window's begin")
        if rule == "hold" and lighting:
            self.findings.refuse(SWEEP_CHECK, "sweep", f"holds illumination and varies "
                                 f"{', '.join(lighting)}, which changes the light; declare "
                                 "'vary' or 'factorial'")
        if rule == "vary" and behaviour:
            self.findings.refuse(SWEEP_CHECK, "sweep", f"varies only illumination and also varies "
                                 f"{', '.join(behaviour)}")
        if rule == "factorial" and lighting and behaviour:
            cells = 1
            for axis in axes:
                cells *= len(axis["values"])
            self.findings.warn(SWEEP_CHECK, "sweep", f"crosses behaviour ({', '.join(behaviour)}) "
                               f"with illumination ({', '.join(lighting)}) in {cells} cells; it is "
                               "not a controlled comparison of either, and the index records it so")

    def _assignments(self, axes: list[dict], pairing: str) -> list[list[tuple[str, object]]]:
        if not axes:
            return [[]]
        if pairing == "zip":
            lengths = {len(a["values"]) for a in axes}
            if len(lengths) != 1:
                self.findings.refuse(53, "sweep", f"zips axes of different lengths {sorted(lengths)}")
                return []
            return [[(a["path"], a["values"][i]) for a in axes] for i in range(lengths.pop())]
        return [list(zip([a["path"] for a in axes], values, strict=True))
                for values in itertools.product(*[a["values"] for a in axes])]

    @staticmethod
    def _member_id(base_id: str, assignment: list[tuple[str, object]]) -> str:
        if not assignment:
            return f"{base_id}.base"
        canonical = json.dumps(assignment, sort_keys=True, separators=(",", ":"))
        return f"{base_id}.m{hashlib.sha256(canonical.encode('utf-8')).hexdigest()[:10]}"

    def _apply(self, document: dict, path: str, value: object) -> None:
        if path == "epoch.date":
            self._set_epoch_date(document, value)
            return
        segments = path.split(".")
        node = document
        for position, segment in enumerate(segments):
            match = _SEGMENT.match(segment)
            last = position == len(segments) - 1
            if match is None:
                self.findings.refuse(53, f"axis {path}", f"segment '{segment}' is not a key")
                return
            key, index = match["key"], match["index"]
            if isinstance(node, list):
                chosen = next((item for item in node if isinstance(item, dict)
                               and any(item.get(k) == key for k in _LIST_KEYS)), None)
                if chosen is None:
                    self.findings.refuse(8, f"axis {path}", f"names '{key}', which the base does not "
                                         "declare in that list")
                    return
                node = chosen
                if index is not None or last:
                    self.findings.refuse(53, f"axis {path}", "replaces a whole list entry; name a "
                                         "field of it")
                    return
                continue
            if not isinstance(node, dict) or (key not in node and not last):
                self.findings.refuse(8, f"axis {path}", f"'{key}' is not in the base specification")
                return
            if index is not None:
                items = node.get(key)
                if not isinstance(items, list) or int(index) >= len(items):
                    self.findings.refuse(8, f"axis {path}", f"'{key}[{index}]' is not in the base")
                    return
                if last:
                    items[int(index)] = value
                    return
                node = items[int(index)]
            elif last:
                node[key] = value
            else:
                node = node[key]

    def _set_epoch_date(self, document: dict, value: object) -> None:
        epoch = document.get("epoch") or {}
        try:
            date = datetime.strptime(str(value), "%Y-%m-%d")
            civil = datetime.fromisoformat(epoch["civil_datetime"])
            utc = datetime.fromisoformat(epoch["utc_datetime"].replace("Z", "+00:00"))
        except (KeyError, ValueError) as problem:
            self.findings.refuse(53, "axis epoch.date", f"cannot move the epoch to {value!r}: "
                                 f"{problem}")
            return
        shift = date.date() - civil.date()
        moved_civil = civil + shift
        moved_utc = utc + shift
        epoch["civil_datetime"] = moved_civil.isoformat()
        epoch["utc_datetime"] = moved_utc.strftime("%Y-%m-%dT%H:%M:%SZ")
        document["epoch"] = epoch

    # -- members ------------------------------------------------------------------------------------

    def _compile_member(self, member_id: str, assignment: list, document: dict, base_path: Path,
                        pair: dict | None = None) -> dict:
        directory = self.out_dir / member_id
        directory.mkdir(parents=True, exist_ok=True)
        document = copy.deepcopy(document)
        document["scenario_id"] = member_id
        world = document.get("world", {})
        if "package" in world:
            world["package"] = str((base_path.parent / world["package"]).resolve())
        if "catalogue" in document:
            document["catalogue"] = str((base_path.parent / document["catalogue"]).resolve())
        vocabulary = document.get("vocabulary", {})
        if "import" in vocabulary:
            vocabulary["import"] = [str((base_path.parent / p).resolve())
                                    for p in vocabulary["import"]]
        spec = directory / f"{member_id}.scenario.json"
        spec.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8", newline="\n")
        result = ScenarioCompiler(self.installation).compile(spec, directory)
        member = {"member_id": member_id, "assignments": [{"path": p, "value": v}
                                                          for p, v in assignment],
                  "outcome": "refused" if result.refused else "compiled",
                  "specification": str(spec.relative_to(self.out_dir)),
                  "findings": result.findings.to_list()}
        if not result.refused:
            lock = result.lock
            member.update({
                "epoch_block_sha256": lock["epoch_block_sha256"],
                "illumination": lock["illumination"],
                "files": {role: entry["sha256"] for role, entry in lock["files"].items()},
                "windows": [{"id": w["id"], "civil_begin": w["begin"]["civil"],
                             "civil_date": w["civil_date"],
                             "sun_open": w.get("sun_open")} for w in
                            result.report.get("capture_windows", [])]})
        else:
            for finding in result.findings.refusals:
                self.findings.refuse(finding.check_id, f"member {member_id}: {finding.subject}",
                                     finding.message)
        if pair is not None:
            member["counterfactual"] = pair
        return member

    def _counterfactual(self, member_id: str, assignment: list, document: dict, declared: dict,
                        base_path: Path) -> dict | None:
        actor_id = declared["actor"]
        mode = declared["mode"]
        where = f"counterfactual {actor_id} {mode}"
        actors = document.get("actors", [])
        actor = next((a for a in actors if a.get("id") == actor_id), None)
        if actor is None:
            self.findings.refuse(8, where, f"names actor '{actor_id}', which the base does not "
                                 "declare in actors")
            return None
        twin = copy.deepcopy(document)
        twin_actor = next(a for a in twin["actors"] if a["id"] == actor_id)
        supervision = twin.setdefault("supervision", {})
        illumination_differs = False
        displaced_in: list[str] = []
        if mode == "absent":
            twin["actors"] = [a for a in twin["actors"] if a["id"] != actor_id]
            supervision["instances"] = self._without_actor(supervision.get("instances", []),
                                                           actor_id)
        elif mode == "nominal":
            for field in declared.get("remove", []):
                twin_actor.pop(field, None)
            supervision["instances"] = self._without_actor(supervision.get("instances", []),
                                                           actor_id)
            supervision["instances"].append({
                "name": f"{actor_id}_nominal_twin", "supervision": "nominal",
                "labels": list(declared.get("labels", [])),
                "participants": [{"actor": actor_id, "role": "subject"}]})
        else:
            shift = self._shift_seconds(document, declared.get("shift"), where)
            if shift is None:
                return None
            if shift:
                displaced_in.append("time")
                illumination_differs = True
                twin_actor["depart"] = self._shifted(document, actor["depart"], shift, where)
                for instance in supervision.get("instances", []):
                    if any(p.get("actor") == actor_id for p in instance.get("participants", [])):
                        for interval in instance.get("intervals", []):
                            for key in ("begin", "end"):
                                if key in interval:
                                    interval[key] = self._shifted(document, interval[key], shift,
                                                                  where)
            substitutions = declared.get("places", {})
            if substitutions:
                displaced_in.append("space")
                for key in ("from", "to"):
                    if key in twin_actor:
                        twin_actor[key] = substitutions.get(twin_actor[key], twin_actor[key])
                twin_actor["via"] = [substitutions.get(v, v) for v in twin_actor.get("via", [])]
                for stop in twin_actor.get("stops", []):
                    stop["place"] = substitutions.get(stop["place"], stop["place"])
            if not displaced_in:
                self.findings.refuse(SWEEP_CHECK, where, "displaces the actor neither in time nor in "
                                     "space")
                return None
        if mode != "displaced" and (twin.get("epoch") != document.get("epoch")
                                    or twin.get("illumination") != document.get("illumination")
                                    or twin.get("capture_windows") != document.get("capture_windows")):
            self.findings.refuse(SWEEP_CHECK, where, "differs from its base in epoch, windows or "
                                 "illumination")
            return None
        pair_id = f"{member_id}.cf.{actor_id}.{mode}"
        pair = {"counterfactual_pair_id": pair_id, "base_member": member_id,
                "counterfactual_member": pair_id, "actor": actor_id, "mode": mode,
                "displaced_in": displaced_in, "illumination_differs": illumination_differs,
                "trajectories_expected_to_match": False,
                "statement": "identical inputs except one vehicle; a car-following model reacts to "
                             "what is in front of it, so downstream trajectories are not expected "
                             "to match"}
        member = self._compile_member(pair_id, assignment, twin, base_path, pair=pair)
        return {"member": member, "pair": pair}

    @staticmethod
    def _without_actor(instances: list[dict], actor_id: str) -> list[dict]:
        kept = []
        for instance in instances:
            participants = [p for p in instance.get("participants", []) if p.get("actor") != actor_id]
            if instance.get("participants") and not participants:
                continue
            instance = dict(instance, participants=participants,
                            intervals=[i for i in instance.get("intervals", [])
                                       if i.get("participant") != actor_id])
            kept.append(instance)
        return kept

    def _shift_seconds(self, document: dict, shift: object, where: str) -> float | None:
        if shift is None:
            return 0.0
        if isinstance(shift, (int, float)) and not isinstance(shift, bool):
            return float(shift)
        resolver = self._resolver(document, where)
        if resolver is None:
            return None
        return resolver.duration(shift, f"{where} shift")

    def _shifted(self, document: dict, authored: object, shift: float, where: str) -> object:
        resolver = self._resolver(document, where)
        resolved = None if resolver is None else resolver.instant(authored, where)
        if resolved is None:
            return authored
        return resolved.seconds + shift

    def _resolver(self, document: dict, where: str) -> CivilTimeResolver | None:
        try:
            epoch = ScenarioEpoch.read(document.get("epoch"))
        except ScenarioEpochRefusedError as refused:
            for check, text in refused.problems:
                self.findings.refuse(check, where, text)
            return None
        return CivilTimeResolver(epoch, self.findings, instants=document.get("instants", {}))

    def _finish(self, index: dict) -> dict:
        index["findings"] = self.findings.to_list()
        index["outcome"] = "refused" if self.findings.refused else "compiled"
        self.out_dir.mkdir(parents=True, exist_ok=True)
        name = index.get("sweep_id") or self.sweep_path.stem
        path = self.out_dir / f"{name}.sweep-index.json"
        path.write_text(json.dumps(index, indent=2, ensure_ascii=False) + "\n", encoding="utf-8",
                        newline="\n")
        index["path"] = str(path)
        return index
