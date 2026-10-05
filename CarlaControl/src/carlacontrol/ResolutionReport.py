"""What a scenario compile resolved, stated so a person or an assistant can check it against intent.

`sumo-gui` is the only preview of a SUMO scenario, and it knows nothing about supervision, areas,
catalogue entries, dates or the sun: its clock reads elapsed seconds (`07_Scenario_Authoring.md` §3.6,
§5.3). So this report is **the only place an annotation or an epoch can be checked** before a capture
is spent. It states every place and what it became, the epoch in one sentence, every declared instant
with its second and its civil time, every rota and every skip with its reason, every route as
`duarouter` produced it, every lane closure and its window, every vehicle type and the body it binds,
every supervision instance with its intervals in seconds and civil time, every capture window with its
civil date and the sun the session will declare, the illumination-label association in full, every
warning in full, what a SUMO-only run of the compiled files showed, the SUMO options the compiler fixed,
and the lock.

`<scenario>.resolution.json` is the record; `<scenario>.resolution.md` renders it for reading. A
refused compile writes the report too, marked refused, with every refusal.
"""
from __future__ import annotations

import json

RESOLUTION_VERSION = 1

# Section order: what a reader checks first comes first.
SECTIONS = ("resolution_version", "outcome", "scenario", "findings", "epoch", "zone",
            "illumination_default", "capture_windows", "illumination_label_association", "world",
            "instants", "places", "rotas", "routes", "lane_closures", "vehicle_types",
            "supervision", "dry_run", "lock")


class ResolutionReport:
    """Collects the report's sections during a compile and renders them."""

    def __init__(self) -> None:
        self._sections: dict[str, object] = {"resolution_version": RESOLUTION_VERSION}

    def set(self, section: str, value: object) -> None:
        if section not in SECTIONS:
            raise KeyError(f"'{section}' is not a section of the resolution report")
        self._sections[section] = value

    @property
    def document(self) -> dict:
        return {name: self._sections[name] for name in SECTIONS if name in self._sections}

    # -- Markdown -----------------------------------------------------------------------------------

    def to_markdown(self) -> str:
        d = self.document
        scenario = d.get("scenario", {}) or {}
        lines = [f"# Resolution report: {scenario.get('scenario_id') or 'unnamed scenario'}", "",
                 f"**Outcome:** {d.get('outcome', 'unknown')}", ""]
        if scenario.get("description"):
            lines += [str(scenario["description"]), ""]
        lines += self._findings(d.get("findings", []))
        if "epoch" in d:
            epoch = d["epoch"]
            lines += ["## Epoch", "", epoch["statement"] + ".", "",
                      f"`epoch_block_sha256` {epoch['epoch_block_sha256']}. The zone name, when "
                      "declared, is carried and never resolved.", ""]
        if "zone" in d:
            zone = d["zone"]
            lines += ["## The sun's zone", "",
                      f"Declared offset {zone['declared_offset_hours']:+g} h; the world's "
                      f"georeference configures {self._num(zone['engine_time_zone_hours'])} h. "
                      f"{zone['written_by_the_session']}.", ""]
        if "illumination_default" in d:
            default = d["illumination_default"]
            lines += ["## Illumination default", "",
                      f"Policy `{default['policy']}`: {default['status']}.", ""]
        lines += self._windows(d.get("capture_windows"))
        lines += self._association(d.get("illumination_label_association"))
        lines += self._instants(d.get("instants"))
        lines += self._places(d.get("places"))
        lines += self._rotas(d.get("rotas"))
        lines += self._routes(d.get("routes"))
        lines += self._lane_closures(d.get("lane_closures"))
        lines += self._supervision(d.get("supervision"))
        lines += self._dry_run(d.get("dry_run"))
        if "lock" in d:
            lines += self._traffic(d["lock"].get("traffic"))
            lines += ["## Lock", "", "```json", json.dumps(d["lock"].get("files", {}), indent=2),
                      "```", ""]
        return "\n".join(lines).rstrip() + "\n"

    @staticmethod
    def _traffic(traffic) -> list[str]:
        """The seed, the step and the SUMO options the compiler fixed, as the lock records them."""
        if not traffic:
            return []
        lines = ["## Traffic", "",
                 f"SUMO seed {traffic['sumo_seed']}, step {traffic['step_length_s']:g} s, end "
                 f"{traffic['end_s']:g} s. The processing options, each written into the configuration "
                 "rather than left to SUMO's default:", "", "| Option | Value |", "|---|---|"]
        lines += [f"| `{name}` | {value} |" for name, value in traffic.get("processing", {}).items()]
        return [*lines, ""]

    @staticmethod
    def _num(value) -> str:
        return "unknown" if value is None else f"{value:+.6g}"

    @staticmethod
    def _findings(findings: list[dict]) -> list[str]:
        if not findings:
            return ["## Findings", "", "None.", ""]
        lines = ["## Findings", "", "| Check | Outcome | Subject | Finding |", "|---|---|---|---|"]
        for finding in findings:
            message = str(finding["message"]).replace("|", "\\|").replace("\n", " ")
            lines.append(f"| {finding['check']} | {finding['outcome']} | {finding['subject']} | "
                         f"{message} |")
        return [*lines, ""]

    @staticmethod
    def _windows(windows) -> list[str]:
        if not windows:
            return []
        lines = ["## Capture windows (authored candidates)", "",
                 "| Window | Opens | Closes | Civil date | Sun date | Elevation at open | "
                 "Elevation at close |", "|---|---|---|---|---|---|---|"]
        for w in windows:
            opens, closes = w.get("sun_open"), w.get("sun_close")
            sun_date = opens["sun_date"] if opens else "-"
            at_open = format(opens["elevation_deg"], ".2f") if opens else "-"
            at_close = format(closes["elevation_deg"], ".2f") if closes else "-"
            lines.append(f"| {w['id']} | {w['begin']['civil']} | {w['end_civil']} | "
                         f"{w['civil_date']} | {sun_date} | {at_open} | {at_close} |")
        return [*lines, ""]

    @staticmethod
    def _association(association) -> list[str]:
        if not association:
            return []
        lines = ["## Illumination-label association (check 41)", "",
                 f"{association['statistic']}, bands from {association['band_source']}, "
                 f"elevation {association['elevation_kind']}. Presence: "
                 f"{association['presence_estimate']}.", ""]
        for key, title in (("over_windows", "Over the declared windows"),
                           ("over_span", "Over the span, at each departure")):
            table = association[key]
            lines += [f"### {title}", ""]
            if "not_computed" in table:
                lines += [f"Not computed: {table['not_computed']}.", ""]
                continue
            value = table["normalized_mutual_information"]
            lines += [f"Normalized mutual information: "
                      f"{'undefined' if value is None else f'{value:.3f}'} over {table['entries']} "
                      "entries.", "", "| Band | annotated | nominal | unlabelled | total |",
                      "|---|---|---|---|---|"]
            for band, row in table["table"].items():
                lines.append(f"| {band} | {row['annotated']} | {row['nominal']} | "
                             f"{row['unlabelled']} | {row['total']} |")
            lines += ["", f"Degenerate bands: {', '.join(table['degenerate_bands']) or 'none'}. "
                      f"Usable bands: {', '.join(table['usable_bands']) or 'none'}.", ""]
        lines += ["Remedies:", ""] + [f"- {r}" for r in association["remedies"]] + [""]
        return lines

    @staticmethod
    def _instants(instants) -> list[str]:
        if not instants:
            return []
        lines = ["## Instants", "", "| Name | Authored | Seconds | Civil |", "|---|---|---|---|"]
        for name, r in instants.items():
            lines.append(f"| {name} | `{json.dumps(r['authored'])}` | {r['seconds']:g} | "
                         f"{r['civil']} |")
        return [*lines, ""]

    @staticmethod
    def _places(places) -> list[str]:
        if not places:
            return []
        lines = ["## Places", "", "| Place | Authored | Became | Street |", "|---|---|---|---|"]
        for name, p in places.items():
            became = p["lane"] + (f" at {p['end_pos']:g} m" if p["end_pos"] is not None else "") \
                if p["lane"] else " ".join(p["edges"])
            lines.append(f"| {name} | `{json.dumps(p['authored'])}` | {became} | {p['street']} |")
        return [*lines, ""]

    @staticmethod
    def _rotas(rotas) -> list[str]:
        if not rotas:
            return []
        lines = ["## Rotas", ""]
        for rota in rotas:
            lines.append(f"- **{rota['id']}**: {rota['entries']} entries")
            for skip in rota["skips"]:
                lines.append(f"  - skipped `{skip['entry']}` at {skip['civil']}: {skip['because']}")
        return [*lines, ""]

    @staticmethod
    def _routes(routes) -> list[str]:
        if not routes:
            return []
        lines = ["## Routes", "", "| Id | Type | Departs | Route | Length | Free-flow |",
                 "|---|---|---|---|---|---|"]
        for r in routes:
            when = r["depart"]["civil"] if "depart" in r else \
                f"{r['begin']['civil']} to {r['end']['civil']}"
            lines.append(f"| {r['id']} | {r['type']} | {when} | {' '.join(r['route'])} | "
                         f"{r['route_length_m']:g} m | {r['free_flow_s']:g} s |")
        return [*lines, ""]

    @staticmethod
    def _lane_closures(closures) -> list[str]:
        if not closures:
            return []
        lines = ["## Lane closures", "", "| Id | Edge | Closed lanes | Open lanes | From | To | "
                 "Notified on |", "|---|---|---|---|---|---|---|"]
        for c in closures:
            lines.append(f"| {c['id']} | {c['edge']} {c['street']} | {' '.join(c['lanes'])} | "
                         f"{c['open_lanes']} | {c['begin']['civil']} | {c['end']['civil']} | "
                         f"{' '.join(c['notify'])} |")
        return [*lines, ""]

    @staticmethod
    def _supervision(supervision) -> list[str]:
        if not supervision:
            return []
        lines = ["## Supervision", ""]
        for instance in supervision["instances"]:
            who = ", ".join(f"{p['entity_id']} ({p['role']})" for p in instance["participants"]) \
                or "no participant"
            lines.append(f"- **{instance['instance_id']}**: {instance['supervision']}, "
                         f"{instance['realisation']}, labels {', '.join(instance['labels']) or '-'}; "
                         f"{who}" + ResolutionReport._declared(instance))
            for interval in instance["intervals"]:
                lines.append(f"  - {interval['phase']}: " + ResolutionReport._bounds(interval))
        for cohort in supervision["cohorts"]:
            if cohort["supervision"] != "unlabelled":
                lines.append(f"- cohort **{cohort['flow_id']}**: {cohort['supervision']}, labels "
                             f"{', '.join(cohort['labels'])}")
        for series in supervision["series"]:
            lines.append(f"- series **{series['series_id']}**: {series['slots']} slots, members "
                         f"{series['supervision']}" + ResolutionReport._declared(series))
        return [*lines, ""]

    @staticmethod
    def _dry_run(run) -> list[str]:
        """What a SUMO-only run of the compiled files showed (check 59): every planned vehicle's wait
        at its entrance, the other vehicles that never got in, and every collision."""
        if not run:
            return []
        if not run["ran"]:
            return ["## Dry run", "", f"Not run: {run['reason']}.", ""]
        vehicles, planned = run["vehicles"], run["planned_vehicles"]
        lines = ["## Dry run (check 59)", "",
                 f"SUMO {run['sumo_release']} alone over {run['end_s']:g} s: {vehicles['loaded']} "
                 f"vehicles loaded, {vehicles['inserted']} inserted, {vehicles['discarded']} discarded "
                 f"after waiting max-depart-delay, {vehicles['waiting_at_end']} still waiting at the "
                 f"end; {run['collisions']} collisions, {run['teleports']} teleports, "
                 f"{run['emergency_stops']} emergency stops, {run['emergency_braking']} emergency "
                 f"braking. {planned['inserted']} of the {planned['total']} vehicles the plan names "
                 f"entered; of the others, {run['other_vehicles_discarded']} were discarded and "
                 f"{run['other_vehicles_waiting_at_end']} were still waiting at the end.", ""]
        if run["planned"]:
            lines += ["| Planned vehicle | Named by | Declared departure | Entered | Waited (s) |",
                      "|---|---|---|---|---|"]
            for row in run["planned"]:
                entered = row["depart_s"] if row["inserted"] else row["outcome"].replace("_", " ")
                lines.append(f"| {row['vehicle_id']} | {', '.join(row['refs'])} | "
                             f"{row['declared_depart_civil']} | {entered} | {row['waited_s']:g} |")
            lines.append("")
        if run["collision_list"]:
            lines += ["| Collision at | Type | Collider | Victim | Lane |", "|---|---|---|---|---|"]
            lines += [f"| {c['civil']} ({c['time_s']:g} s) | {c['type']} | {c['collider']} | "
                      f"{c['victim']} | {c['lane']} |" for c in run["collision_list"]]
            lines.append("")
        return lines

    @staticmethod
    def _bounds(interval: dict) -> str:
        """An interval's declared bounds, and the events that commit them where it is anchored."""
        start, end = interval["declared_start_civil"], interval["declared_end_civil"]
        anchor = interval.get("anchor")
        if not anchor:
            return f"{start} to {end or 'open'}"
        text = f"from {anchor['start']['event']}" + (f" ({start})" if start else "")
        text += " to " + ("open" if anchor["end"] is None
                          else anchor["end"]["event"] + (f" ({end})" if end else ""))
        if start is None and interval["declared_duration_s"] is not None:
            text += f", {interval['declared_duration_s']:g} s declared"
        return text

    @staticmethod
    def _declared(row: dict) -> str:
        """A row's parameters and the terms it is a matched negative for, where it has any."""
        text = ""
        if row.get("parameters"):
            text += "; parameters " + ", ".join(f"{key} = {json.dumps(value)}"
                                                for key, value in row["parameters"].items())
        if row.get("hard_negative_for"):
            text += "; a hard negative for " + ", ".join(row["hard_negative_for"])
        return text
