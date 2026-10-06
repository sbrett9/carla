"""How much of the supervision label an illumination band gives away: check 41, which states a number and never refuses.

In a pattern of life the correlation between time of day and label is structural -- the sizing
scenario's probes are a mid-morning phenomenon because that is when a probe would probe, and its guard
rota is a 07:00 / 15:00 / 23:00 phenomenon because that is when shifts change. Measured on it,
`I(hour; label) / H(label) = 0.600` (`07_Scenario_Authoring.md` §5.6.1). A model trained on imagery
from such a corpus can learn the light instead of the behaviour, and the failure this check exists for
is discovering that after training. So it computes the number, states it in the resolution report and
the lock, and **never refuses**: refusing would make doc 20's class 4 -- a pattern *defined* by its
hour -- unauthorable, and a check that fires on the only large scenario gets switched off (§5.6.4).

**Buckets are illumination bands, not clock hours** (§5.6.2): 07:00 is a different sun in December and
in June. The band is `IlluminationBand.of` applied to the declared sun (`WindowSun`), so the statistic
rests on the same sun the session will bind.

Two tables are computed:

* **over the windows** -- every entry each declared capture window captures, which is what a corpus is
  made of. An entry is taken as present from its departure for its free-flow route time plus its stop
  durations, and a flow from its begin to its end; it is captured by a window it overlaps, under the
  window's sun at the entry's first instant inside it. The presence span is an estimate, and says so:
  congestion lengthens it, and only the run can say exactly who was in frame;
* **over the span** -- every entry at its own departure, the scenario-wide picture §5.6.1 measured.

For each: the contingency table of band against the three-valued supervision state, the per-band
rates, `I(band; supervision) / H(supervision)` in bits to three decimals with the counts it rests on,
every band where one state alone occurs (the annotated rate is 0 or 1 there, so the band determines
the label; `degenerate_bands`), every band where both occur (`mixed_bands`), and the ways §5.6.3
names of changing the numbers. Every field is a count or a ratio over the declarations: the report
carries no word that judges what the numbers mean (the charter's §4b).

What it cannot see: what an operator's override of the illumination default or a window of their own
would capture, and anything about the imagery. It is a statistic over declarations, not over pixels.
"""
from __future__ import annotations

import math
from collections import Counter
from dataclasses import dataclass

from carlacontrol.IlluminationBand import BAND_SOURCE, IlluminationBand
from carlacontrol.WindowSun import WindowSun

STATES = ("annotated", "nominal", "unlabelled")

REMEDIES = [
    "pair an annotated behaviour with a displaced-in-time counterfactual, so the same annotation "
    "appears in a second band (07 §7.3)",
    "add a nominal twin inside the annotated band: a hard negative lit identically (07 §7.3, doc 20 "
    "§2.7)",
    "add a capture window in a band where the annotated class is absent, which turns a degenerate "
    "band into a populated one",
]


@dataclass(frozen=True)
class AssociationEntry:
    """One route entry, its supervision state, and when it is taken to be present."""

    entry_id: str
    kind: str
    state: str
    depart_s: float
    present_from_s: float
    present_to_s: float


class IlluminationLabelAssociation:
    """Computes check 41's statistic over a scenario's declared windows and over its span."""

    def __init__(self, window_sun: WindowSun) -> None:
        self.window_sun = window_sun

    def compute(self, windows: list[dict], entries: list[AssociationEntry]) -> dict:
        over_windows = self._over_windows(windows, entries)
        over_span = self._over_span(entries)
        headline = over_windows if windows else over_span
        return {
            "statistic": "I(band; supervision) / H(supervision), base 2",
            "band_source": BAND_SOURCE,
            "band_edges": IlluminationBand.edges(),
            "elevation_kind": WindowSun.ELEVATION_KIND,
            "presence_estimate": "a vehicle is present from its departure for its free-flow route "
                                 "time plus its stops; a flow from its begin to its end",
            "over_windows": over_windows,
            "over_span": over_span,
            "normalized_mutual_information": headline.get("normalized_mutual_information"),
            "bands": headline.get("table"),
            "entries": headline.get("entries"),
            "measured_over": "windows" if windows else "span",
            "remedies": REMEDIES,
        }

    def summary(self, result: dict) -> str:
        """The warning's text: the number, what it rests on, and where to read the rest."""
        headline = result["over_windows"] if result["measured_over"] == "windows" \
            else result["over_span"]
        if "not_computed" in headline:
            return f"illumination-label association not computed: {headline['not_computed']}"
        value = headline["normalized_mutual_information"]
        shown = "undefined (one supervision state only)" if value is None else f"{value:.3f}"
        degenerate = headline["degenerate_bands"]
        return (f"I(band; supervision) / H(supervision) = {shown} over {headline['entries']} "
                f"entr{'y' if headline['entries'] == 1 else 'ies'} "
                f"({'captured by the declared windows' if result['measured_over'] == 'windows' else 'at their departures; no capture window is declared'})"
                + (f"; bands where one state alone occurs, so the band determines the label: "
                   f"{', '.join(degenerate)}" if degenerate else "")
                + ". Expected in a pattern of life and never a refusal; the table, the bands where "
                  "both states occur and the ways of changing the numbers are in the resolution "
                  "report, and the statistic is in the lock")

    # -- the two tables ----------------------------------------------------------------------------

    def _over_windows(self, windows: list[dict], entries: list[AssociationEntry]) -> dict:
        if not windows:
            return {"not_computed": "no capture window is declared"}
        if not self.window_sun.binds_the_sun:
            return {"not_computed": "the illumination default binds no sun (ignore)"}
        pairs: list[tuple[str, str]] = []
        for window in windows:
            begin, end = window["begin"]["seconds"], window["end_s"]
            for entry in entries:
                if entry.present_to_s < begin or entry.present_from_s > end:
                    continue
                first = max(begin, entry.present_from_s)
                sun = self.window_sun.at(begin, first)
                pairs.append((IlluminationBand.of(sun.elevation_deg), entry.state))
        table = self._table(pairs)
        table["windows"] = [w["id"] for w in windows]
        return table

    def _over_span(self, entries: list[AssociationEntry]) -> dict:
        if not self.window_sun.binds_the_sun:
            return {"not_computed": "the illumination default binds no sun (ignore)"}
        pairs = []
        for entry in entries:
            sun = self.window_sun.at(entry.depart_s, entry.depart_s)
            pairs.append((IlluminationBand.of(sun.elevation_deg), entry.state))
        return self._table(pairs)

    @staticmethod
    def _table(pairs: list[tuple[str, str]]) -> dict:
        joint = Counter(pairs)
        order = [edge["band"] for edge in IlluminationBand.edges()]
        bands = sorted({band for band, _ in pairs},
                       key=lambda b: (order.index(b), "") if b in order else (len(order), b))
        total = len(pairs)
        table = {band: {state: joint[(band, state)] for state in STATES} for band in bands}
        for band in bands:
            table[band]["total"] = sum(table[band][s] for s in STATES)
        state_totals = {state: sum(joint[(b, state)] for b in bands) for state in STATES}
        result = {
            "entries": total,
            "table": table,
            "state_totals": state_totals,
            "annotated_rate": {band: (table[band]["annotated"] / table[band]["total"])
                               for band in bands if table[band]["total"]},
            "base_annotated_rate": (state_totals["annotated"] / total) if total else None,
        }
        result["normalized_mutual_information"] = IlluminationLabelAssociation._nmi(joint, total)
        any_annotated = state_totals["annotated"] > 0
        result["degenerate_bands"] = [band for band, rate in result["annotated_rate"].items()
                                      if any_annotated and rate in (0.0, 1.0)]
        result["mixed_bands"] = [band for band, rate in result["annotated_rate"].items()
                                 if 0.0 < rate < 1.0]
        return result

    @staticmethod
    def _nmi(joint: Counter, total: int) -> float | None:
        if total == 0:
            return None
        band_totals: Counter = Counter()
        state_totals: Counter = Counter()
        for (band, state), count in joint.items():
            band_totals[band] += count
            state_totals[state] += count
        entropy = -sum((n / total) * math.log2(n / total) for n in state_totals.values() if n)
        if entropy == 0.0:
            return None
        information = sum((n / total) * math.log2((n / total) / ((band_totals[b] / total)
                                                                  * (state_totals[s] / total)))
                          for (b, s), n in joint.items() if n)
        return round(information / entropy, 3)
