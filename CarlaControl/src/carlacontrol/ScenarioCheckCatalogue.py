"""Every check the scenario compiler knows, by stable id, with what it compares and concludes.

The ids are identifiers, not positions (`07_Scenario_Authoring.md` §5.2): a resolution report cites
them and a corpus filtered on "members that warned on check 41" has to mean the same thing a year
later. So an id is assigned once and never reused, and a check that is removed stays here as retired.
The list is in pipeline order, which is why ids appear out of numeric order.

This is the source `checks.json` is generated from (`write_checks_json`), and a test compares it with
the table in the plan, so the plan, the shipped list and the compiler name the same checks.
"""
from __future__ import annotations

import json
from pathlib import Path

from carlacontrol.CompileFinding import REFUSE, WARN
from carlacontrol.ScenarioCheck import COMPILER, RETIRED, WORLD_BUILD, ScenarioCheck

CHECKS_VERSION = 1

_R = (REFUSE,)
_W = (WARN,)
_RW = (REFUSE, WARN)

_CHECKS: tuple[ScenarioCheck, ...] = (
    # -- the specification itself ------------------------------------------------------------------
    ScenarioCheck(53, "specification", "The specification is well formed at a spec_version this "
                  "compiler implements: known fields only, each of its declared type",
                  "the specification schema", _R, COMPILER,
                  "A field nobody reads, which its author will later believe was honoured"),
    # -- world binding -----------------------------------------------------------------------------
    ScenarioCheck(1, "world_binding", "The network fingerprint the specification names equals the "
                  "world package's", "the world package", _R, COMPILER,
                  "Authoring against a road graph other than the one that renders"),
    ScenarioCheck(2, "world_binding", "The package's network and OpenDRIVE came from one netconvert "
                  "invocation, and the network it carries is the one it records",
                  "world.json NetconvertArgv and NetworkFingerprint", _R, COMPILER,
                  "A network and a map from two runs, which disagree about lanes"),
    ScenarioCheck(3, "world_binding", "The network's convBoundary equals the OpenDRIVE header's "
                  "north, south, east and west", "map.net.xml and map.xodr", _R, COMPILER,
                  "A network in a different frame from the map"),
    ScenarioCheck(4, "world_binding", "The network's projParameter equals world.json's "
                  "GeoReferenceString", "map.net.xml and world.json", _R, COMPILER,
                  "Origin drift between the network and the world"),
    ScenarioCheck(5, "world_binding", "The network's netOffset is zero", "map.net.xml", _R, COMPILER,
                  "A network whose metres are displaced from the world's geographic frame"),
    ScenarioCheck(6, "world_binding", "The SUMO release routing the scenario is the one that built "
                  "the world", "world.json NetconvertVersion and the SUMO installation", _W, COMPILER,
                  "Routes validated by one release against a network another built"),
    # -- references --------------------------------------------------------------------------------
    ScenarioCheck(7, "references", "Every declared place resolves, and to exactly one thing",
                  "the network, the place index and the area table", _R, COMPILER,
                  "An authored place that silently becomes a different place"),
    ScenarioCheck(8, "references", "Every reference names what the specification declares: places, "
                  "instants, rotas, series, flows and counterfactuals", "the specification", _R,
                  COMPILER, "A typo becoming a valid-looking identifier"),
    ScenarioCheck(54, "references", "Every actor, rota entry, flow and capture window id is unique",
                  "the specification", _R, COMPILER,
                  "Two vehicles SUMO would read as one, or a window cited ambiguously"),
    ScenarioCheck(9, "references", "Every stop position lies within its lane's length",
                  "map.net.xml", _R, COMPILER,
                  "A dwell clamped to somewhere other than where it was authored"),
    ScenarioCheck(10, "references", "Every vehicle's class may drive every edge of its route",
                  "lane allow and disallow in map.net.xml", _R, COMPILER,
                  "A route through a fence the vehicle cannot pass"),
    # -- routes ------------------------------------------------------------------------------------
    ScenarioCheck(11, "routes", "Every origin, destination and via routes", "duarouter", _R,
                  COMPILER, "No valid route at SUMO load, after a capture was scheduled"),
    ScenarioCheck(12, "routes", "Each routed result ends on the requested destination and passes "
                  "every via and stop edge in order", "the routed output", _R, COMPILER,
                  "duarouter's false accept: a trip to a nonexistent edge routes as one edge"),
    ScenarioCheck(13, "routes", "An explicit edge list is connected end to end", "map.net.xml "
                  "connections", _R, COMPILER, "A break that appears only where the list is used"),
    # -- vehicles ----------------------------------------------------------------------------------
    ScenarioCheck(14, "vehicles", "Every vehicle class names only blueprints the catalogue measured",
                  "the vehicle catalogue", _R, COMPILER,
                  "A type with no body, discovered at spawn"),
    ScenarioCheck(15, "vehicles", "Every emitted vType's length, width and height are its "
                  "blueprint's measured box", "the vehicle catalogue", _R, COMPILER,
                  "SUMO's gaps and CARLA's rendering disagreeing by the difference"),
    ScenarioCheck(16, "vehicles", "Every type a flow or actor names is a declared class, one of its "
                  "member types, or the mix", "the specification", _R, COMPILER,
                  "A vehicle drawn from a type nothing declares"),
    ScenarioCheck(17, "vehicles", "A vehicle class draws from more than one body",
                  "the specification", _W, COMPILER,
                  "Appearance becoming the label: every member of the class is the same car"),
    # -- annotation --------------------------------------------------------------------------------
    ScenarioCheck(18, "annotation", "Every label is a declared term and every role but subject a "
                  "declared role; an annotation carries a label; the declarations resolve inside "
                  "the published vocabulary", "the vocabulary block", _R, COMPILER,
                  "A label spelled three ways in one corpus, or one no consumer can read"),
    ScenarioCheck(45, "annotation", "Every label's applies_to includes the subject kind, and its "
                  "realisation the instance's realisation", "the vocabulary block", _R, COMPILER,
                  "A per-member term on a flow, or an absence term on a vehicle"),
    ScenarioCheck(46, "annotation", "Every namespace in a label, role, phase or area kind was "
                  "declared or imported", "the vocabulary block", _R, COMPILER,
                  "A term no published vocabulary defines"),
    ScenarioCheck(19, "annotation", "Every participant names a declared actor",
                  "the specification", _R, COMPILER, "An instance whose participant never exists"),
    ScenarioCheck(20, "annotation", "Every aoi_ref names an area in the world's area table",
                  "areas.resolved.json", _R, COMPILER, "An annotation naming a place only its author "
                  "can see"),
    ScenarioCheck(21, "annotation", "Instance ids are derived from the scenario id and the authored "
                  "name, and unique", "the specification", _R, COMPILER,
                  "Sweep members that cannot be joined"),
    ScenarioCheck(22, "annotation", "No annotated interval begins before its participant departs",
                  "the resolved departures", _W, COMPILER,
                  "An interval no vehicle could have been in"),
    ScenarioCheck(23, "annotation", "A cohort carries only a whole-life annotation, never an "
                  "interval", "06 D6.2", _R, COMPILER,
                  "A phase asserted over a generator whose members are unknown until the run"),
    ScenarioCheck(49, "annotation", "No cohort is nominal", "06 D6.2", _R, COMPILER,
                  "A negative asserted of vehicles nobody authored one by one"),
    ScenarioCheck(50, "annotation", "A one-participant instance names its participant subject; "
                  "the phase vacancy is never authored", "06 §3.7 reserved words", _R, COMPILER,
                  "A consumer guessing which track an instance is about"),
    ScenarioCheck(24, "annotation", "Some subject is nominal when any is annotated",
                  "the specification", _W, COMPILER, "A corpus with no hard negatives"),
    # -- areas -------------------------------------------------------------------------------------
    ScenarioCheck(25, "areas", "Area ids unique, rings closed and not self-intersecting, radius "
                  "positive", "04 C5 V5.1-V5.4", _R, WORLD_BUILD,
                  "Undefined containment; the compiler reads only a table the world build validated"),
    ScenarioCheck(26, "areas", "An area's envelope intersects the world", "04 C5", _R, WORLD_BUILD,
                  "An area outside the world entirely"),
    ScenarioCheck(27, "areas", "A referenced area is not in the staging ring", "the area table's "
                  "V5.5 and V5.6 warnings", _W, COMPILER,
                  "A pattern sited where traffic spawns and despawns"),
    ScenarioCheck(28, "areas", "A referenced area can be reached by a road vehicle", "the area "
                  "table's V5.7 warning", _W, COMPILER, "An area no vehicle can reach"),
    # -- epoch and illumination --------------------------------------------------------------------
    ScenarioCheck(33, "epoch_and_illumination", "The epoch is present and accepted by "
                  "CarlaNet.CoSim.SolarEpoch under every 04 C9 epoch rule", "the specification",
                  _R, COMPILER, "A scenario whose seconds mean no civil time"),
    ScenarioCheck(34, "epoch_and_illumination", "utc_offset_hours is a whole number of quarter "
                  "hours in [-12, +14]", "the specification, through SolarEpoch", _R, COMPILER,
                  "An offset no civil zone uses, or one the engine would clamp"),
    ScenarioCheck(35, "epoch_and_illumination", "The zone name's offset agrees with utc_offset_hours",
                  "none: time_zone_id is carried and never resolved", (), RETIRED,
                  "Retired: 04 §11.3 and 11 D11.1 make the zone name provenance only"),
    ScenarioCheck(47, "epoch_and_illumination", "Every authored time is in an accepted form, names "
                  "its day on a multi-day scenario, and carries the epoch's offset when absolute",
                  "the specification", _R, COMPILER,
                  "A clock-shaped literal that is really an offset"),
    ScenarioCheck(48, "epoch_and_illumination", "Every rota expands to entries, and every skip "
                  "matches an entry it would have produced", "the rota", _R, COMPILER,
                  "An absence that was never planted"),
    ScenarioCheck(37, "epoch_and_illumination", "Every resolved instant lies in [0, end]",
                  "the resolved instants", _R, COMPILER,
                  "An instant that silently falls outside the run"),
    ScenarioCheck(36, "epoch_and_illumination", "A window's civil date differs from the date its "
                  "sun is written with", "the epoch's calendar_advances and the policy", _W,
                  COMPILER, "A window rendered under another date's seasonal sun without notice"),
    ScenarioCheck(38, "epoch_and_illumination", "Every capture window lies in the span and cuts no "
                  "supervision interval", "the specification and the supervision plan", _R,
                  COMPILER, "A partially observed positive teaching a truncated pattern"),
    ScenarioCheck(39, "epoch_and_illumination", "The illumination default is accepted by "
                  "CarlaNet.CoSim.IlluminationPolicy; an advancing sun's arc per window is named",
                  "the specification", _RW, COMPILER,
                  "A window authored as a constant that is not one"),
    ScenarioCheck(40, "epoch_and_illumination", "The declared offset is within an hour of the zone "
                  "the world's georeference configures", "solar.json engine_time_zone_hours", _W,
                  COMPILER, "A declared offset that is not this place's"),
    ScenarioCheck(42, "epoch_and_illumination", "A window whose sun is below -6 degrees is named as "
                  "not corpus-eligible", "11 D11.7", _W, COMPILER,
                  "A night window captured by accident"),
    ScenarioCheck(41, "epoch_and_illumination", "The illumination-label association over the "
                  "declared windows", "the resolved instants, the supervision plan and the sun",
                  _W, COMPILER, "Discovering after training that the light carried the label"),
    ScenarioCheck(43, "epoch_and_illumination", "A sweep holds illumination while it varies "
                  "behaviour, varies only illumination when it says so, and names a crossed design; "
                  "a counterfactual pair shares its base's epoch, windows and illumination unless "
                  "displaced in time", "the sweep", _RW, COMPILER,
                  "A behaviour sweep captured under different light"),
    # -- emission ----------------------------------------------------------------------------------
    ScenarioCheck(29, "emission", "The route file is departure-sorted", "the emitted file", _R,
                  COMPILER, "SUMO dropping out-of-order entries with only a warning"),
    ScenarioCheck(30, "emission", "No XML comment contains --", "the emitted files", _R, COMPILER,
                  "SUMO rejecting the file"),
    ScenarioCheck(44, "emission", "No emitted SUMO file carries an H:M:S time", "the emitted files",
                  _R, COMPILER, "An offset wearing a clock's clothes"),
    ScenarioCheck(51, "emission", "The route file is valid against SUMO's routes_file.xsd",
                  "the emitted file", _R, COMPILER, "A route file SUMO will not load"),
    ScenarioCheck(52, "emission", "The route file carries no parameter but the vehicle-type "
                  "binding's", "the emitted file", _R, COMPILER,
                  "Supervision leaking into the route file, the channel it must never use"),
    ScenarioCheck(31, "emission", "The run ends after every declared interval and departure",
                  "the resolved instants", _W, COMPILER,
                  "A behaviour truncated by the run ending"),
    ScenarioCheck(32, "emission", "Every flow's window lies inside the run", "the specification", _W,
                  COMPILER, "A flow that never fires"),
)

class ScenarioCheckCatalogue:
    """The checks, looked up by id."""

    _BY_ID = {check.check_id: check for check in _CHECKS}

    @classmethod
    def all(cls) -> tuple[ScenarioCheck, ...]:
        """Every check, retired ones included, in pipeline order."""
        return _CHECKS

    @classmethod
    def get(cls, check_id: int) -> ScenarioCheck:
        """The check with this id. An id nobody assigned is a compiler bug, and says so."""
        try:
            return cls._BY_ID[check_id]
        except KeyError:
            raise KeyError(f"check {check_id} is not in the catalogue; a finding may only cite a "
                           "check that exists") from None

    @classmethod
    def active_ids(cls) -> set[int]:
        """Ids of every check that is not retired."""
        return {check.check_id for check in _CHECKS if check.status != RETIRED}

    @classmethod
    def to_document(cls) -> dict:
        return {"checks_version": CHECKS_VERSION,
                "outcomes": {REFUSE: "the compile fails and nothing is emitted",
                             WARN: "the compile goes on and the warning is carried in full into "
                                   "the resolution report"},
                "checks": [check.to_dict() for check in _CHECKS]}

    @classmethod
    def write_checks_json(cls, path: str | Path) -> Path:
        """Write `checks.json`, the list the authoring skill ships, generated from this catalogue."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(cls.to_document(), indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")
        return path
