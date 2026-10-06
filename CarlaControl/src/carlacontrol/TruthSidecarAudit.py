"""Audit a capture's truth sidecars for vehicles that are not in the scene and tracks with no identity.

A SUMO drive renders its vehicles through a pool of CARLA bodies: a body is lent to a SUMO vehicle
while it is rendered, given back when it leaves, lent to the next, and parked out of sight about
300 m below the ground in between (`03_CoSimulation_Runtime.md` §8.2). Two things then go wrong in a
sidecar that lists the world's vehicle actors as they are:

* **parked bodies are listed as vehicles** -- records hundreds of metres underground, standing
  still, that no camera could see and no sensor should be told about; and
* **no record says which SUMO vehicle it is** -- the uid is built on the actor id, and the actor id
  names each vehicle its body carries in turn, so a record cannot be joined to the scenario's
  supervision (whose participants are SUMO vehicle ids) and a uid names different vehicles at
  different times.

This reads the sidecars back and counts both, from the files alone, so a capture made before the
recorder listed only the rendered set is shown to carry the defect and one made after is shown not
to (`06_Truth_And_Annotation.md` §8.2).

**And the supervision, where the run had a plan.** A sidecar of a frame a supervision plan was in force
on names the plan on its container and carries a world-scoped `<_supervision scope="world">`, and every
SUMO vehicle record in it carries a `<_supervision>` whose `state` is `annotated`, `nominal` or
`unlabelled` -- always written, because a missing state is a bug and not a negative. A sidecar whose
frame's supervision was not to be had says `supervision="unknown"` and carries none, which is counted
and not faulted here: the run's closeout gates it. The run had a plan when any of its sidecars names one
or says its supervision is unknown; then a SUMO vehicle record without the element, a state outside the
three, an annotated vehicle naming no instance, an unlabelled one naming any, a sidecar naming a plan
with no world element, and a sidecar saying nothing of supervision at all are each a defect.

**And whether each vehicle is in the picture.** Every vehicle record a recorder writes says where the
vehicle's box fell against the camera's picture, `in_frame` -- `wholly`, `partly`, `none` or
`behind_camera` -- and, where the five occlusion fields are absent, why, in one word of
`occlusion_unmeasured` (the owner's ruling of 2026-10-05: an absent fraction was being read as "not
hidden"). A record saying nothing of its place, one carrying occlusion fields while its place says the
picture has no view of the vehicle, one carrying neither occlusion nor a reason, one carrying a reason
beside a measurement, and a place or a reason outside the recorder's words are each a defect. A capture
made before the recorder wrote the place is shown to carry the first.

**A uid that changes vehicle is seen even where no record names one.** With SUMO ids on the records,
a uid carrying two of them over the capture is counted directly. Without them, a uid seen on the
road, then below the ground band, then on the road again is a body that was given back and lent again
under the same uid -- to whichever vehicle next needed that blueprint -- and is counted as such. It is
a lower bound, since a body handed from one vehicle to the next between two captures is not seen
parked at all.

**The ground band is taken from the moving vehicles.** A sidecar records the bare-earth height of
every vehicle but not the ground under it, so the band is measured rather than configured: every
vehicle moving at walking pace or faster is on a road, and the band runs from the lowest of them
less a margin to the highest plus the same margin. A parked body stands 300 m under the world's
origin with its velocity zeroed, so it falls far below any band a world's roads draw. A world whose
roads span more relief than the margin in one capture still bands correctly, because the band
follows the roads rather than a single height; `floor_hae` states the floor outright where a
capture has no moving vehicle to measure it from.

Counted, never corrected: the audit says what the sidecars hold and changes nothing.
"""
from __future__ import annotations

import xml.etree.ElementTree as ET
from collections import defaultdict
from collections.abc import Iterable
from dataclasses import dataclass, field
from pathlib import Path

# A vehicle moving at least this fast (m/s) is on a road, which is what the ground band is drawn from.
MOVING_MPS = 0.5

# How far (m) outside the moving vehicles' heights a record may stand and still be in the band: the
# spread of bare-earth heights along the roads a camera frames, with room for a bridge deck.
DEFAULT_MARGIN_M = 50.0

# The three states a vehicle's supervision is written in: the annotation vocabulary's core spellings.
SUPERVISION_STATES = ("annotated", "nominal", "unlabelled")

# Where a vehicle's box fell against the picture, as the recorder writes it (`CarlaNet.Recording.InFrame`),
# and the two places that put any of the vehicle in the picture.
IN_FRAME_VALUES = ("wholly", "partly", "none", "behind_camera")
IN_PICTURE = ("wholly", "partly")

# Why a record's occlusion went unmeasured, as the recorder writes it (`CarlaNet.Recording.OcclusionUnmeasured`).
OCCLUSION_UNMEASURED_REASONS = ("behind_camera", "outside_frame", "beyond_draw_distance", "no_depth_camera",
                                "no_depth_capture", "depth_out_of_step", "depth_pose_mismatch",
                                "beyond_depth_range", "no_sample")


@dataclass(frozen=True)
class VehicleRecord:
    """One vehicle event of one sidecar, as the audit needs it."""

    sidecar: str
    tick: int | None
    uid: str
    actor_id: str
    sumo_id: str | None
    hae: float
    speed_mps: float
    # The vehicle's <_supervision> state, None where it carries none, and how many instances it names.
    supervision_state: str | None = None
    annotations: int = 0
    # Whether its sidecar named a supervision plan, so the record ought to carry a state.
    in_supervised_sidecar: bool = False
    # Where its box fell against the picture, None where the record does not say; whether it carries
    # the occlusion fields; and why not, where it says.
    in_frame: str | None = None
    occlusion_measured: bool = False
    occlusion_unmeasured: str | None = None


@dataclass
class SidecarAuditResult:
    """What a capture's sidecars hold, counted."""

    sidecars: int = 0
    sidecars_with_truth: int = 0
    sidecars_listing_rendered: int = 0
    sidecars_listing_unknown: int = 0
    sidecars_truth_off_frame: int = 0
    records: list[VehicleRecord] = field(default_factory=list)
    floor_hae: float | None = None
    ceiling_hae: float | None = None
    below_band: list[VehicleRecord] = field(default_factory=list)
    without_sumo_id: list[VehicleRecord] = field(default_factory=list)
    uids_with_several_sumo_ids: dict[str, set[str]] = field(default_factory=dict)
    sumo_ids_with_several_uids: dict[str, set[str]] = field(default_factory=dict)
    actors_with_several_sumo_ids: dict[str, set[str]] = field(default_factory=dict)
    uids_lent_again_after_parking: list[str] = field(default_factory=list)
    sidecars_with_plan: int = 0
    sidecars_supervision_unknown: int = 0
    plans: set[str] = field(default_factory=set)
    sidecars_with_plan_without_world: list[str] = field(default_factory=list)
    sidecars_saying_nothing_of_supervision: list[str] = field(default_factory=list)
    records_without_supervision: list[VehicleRecord] = field(default_factory=list)
    records_with_unknown_state: list[VehicleRecord] = field(default_factory=list)
    records_annotated_naming_nothing: list[VehicleRecord] = field(default_factory=list)
    records_unlabelled_naming_something: list[VehicleRecord] = field(default_factory=list)
    records_without_in_frame: list[VehicleRecord] = field(default_factory=list)
    records_with_unknown_in_frame: list[VehicleRecord] = field(default_factory=list)
    records_occluded_outside_picture: list[VehicleRecord] = field(default_factory=list)
    records_without_occlusion_or_reason: list[VehicleRecord] = field(default_factory=list)
    records_with_reason_beside_measurement: list[VehicleRecord] = field(default_factory=list)
    records_with_unknown_reason: list[VehicleRecord] = field(default_factory=list)

    @property
    def had_plan(self) -> bool:
        """Whether the run had a supervision plan: a sidecar names one, or says its supervision was
        unknown, which only a capture of a frame a plan was in force on does."""
        return self.sidecars_with_plan > 0 or self.sidecars_supervision_unknown > 0

    @property
    def carries_sumo_ids(self) -> bool:
        """Whether any record names its SUMO vehicle, without which the identity checks measure
        nothing."""
        return len(self.without_sumo_id) < len(self.records)

    def defects(self, sumo_drive: bool = True) -> list[str]:
        """Each defect found, in a line. A traffic-manager run carries no SUMO id and is not faulted
        for it (`sumo_drive` false)."""
        found = []
        total = len(self.records)
        if self.below_band:
            found.append(f"{len(self.below_band)} of {total} vehicle records stand below the ground "
                         f"band (hae < {self.floor_hae:.2f} m): bodies parked out of sight, listed "
                         "as vehicles")
        if sumo_drive and self.without_sumo_id:
            found.append(f"{len(self.without_sumo_id)} of {total} vehicle records carry no SUMO id, "
                         "so they cannot be joined to the scenario's supervision")
        if self.uids_with_several_sumo_ids:
            found.append(f"{len(self.uids_with_several_sumo_ids)} uid(s) name more than one SUMO "
                         "vehicle over the capture")
        if self.uids_lent_again_after_parking:
            found.append(f"{len(self.uids_lent_again_after_parking)} uid(s) are seen on the road, "
                         "parked and on the road again: a body given back and lent again under the "
                         "same uid")
        if self.sumo_ids_with_several_uids:
            found.append(f"{len(self.sumo_ids_with_several_uids)} SUMO vehicle(s) appear under more "
                         "than one uid over the capture")
        if self.records_without_supervision:
            found.append(f"{len(self.records_without_supervision)} SUMO vehicle record(s) in sidecars "
                         "naming a supervision plan carry no <_supervision>: a missing state is a bug, "
                         "not a negative")
        if self.records_with_unknown_state:
            found.append(f"{len(self.records_with_unknown_state)} vehicle record(s) carry a supervision "
                         f"state other than {', '.join(SUPERVISION_STATES)}")
        if self.records_annotated_naming_nothing:
            found.append(f"{len(self.records_annotated_naming_nothing)} annotated vehicle record(s) name "
                         "no instance they execute")
        if self.records_unlabelled_naming_something:
            found.append(f"{len(self.records_unlabelled_naming_something)} unlabelled vehicle record(s) "
                         "name an instance")
        if self.sidecars_with_plan_without_world:
            found.append(f"{len(self.sidecars_with_plan_without_world)} sidecar(s) name a supervision "
                         "plan and carry no world-scoped <_supervision>")
        if self.had_plan and self.sidecars_saying_nothing_of_supervision:
            found.append(f"{len(self.sidecars_saying_nothing_of_supervision)} sidecar(s) of a run with a "
                         "supervision plan neither name it nor say their supervision was unknown")
        if self.records_without_in_frame:
            found.append(f"{len(self.records_without_in_frame)} of {total} vehicle records do not say "
                         "whether the vehicle is in the picture (no in_frame)")
        if self.records_with_unknown_in_frame:
            found.append(f"{len(self.records_with_unknown_in_frame)} vehicle record(s) carry an in_frame "
                         f"other than {', '.join(IN_FRAME_VALUES)}")
        if self.records_occluded_outside_picture:
            found.append(f"{len(self.records_occluded_outside_picture)} vehicle record(s) carry occlusion "
                         "fields while in_frame says the picture has no view of the vehicle")
        if self.records_without_occlusion_or_reason:
            found.append(f"{len(self.records_without_occlusion_or_reason)} vehicle record(s) carry neither "
                         "occlusion fields nor occlusion_unmeasured: an absent fraction with no reason")
        if self.records_with_reason_beside_measurement:
            found.append(f"{len(self.records_with_reason_beside_measurement)} vehicle record(s) carry "
                         "occlusion_unmeasured beside a measured occlusion")
        if self.records_with_unknown_reason:
            found.append(f"{len(self.records_with_unknown_reason)} vehicle record(s) carry an "
                         f"occlusion_unmeasured other than {', '.join(OCCLUSION_UNMEASURED_REASONS)}")
        return found


class TruthSidecarAudit:
    """Counts parked bodies listed as vehicles, records with no, or no stable, SUMO identity, and
    records that do not say whether their vehicle is in the picture or why its occlusion is absent."""

    def __init__(self, margin_m: float = DEFAULT_MARGIN_M, floor_hae: float | None = None) -> None:
        self.margin_m = float(margin_m)
        self.floor_hae = None if floor_hae is None else float(floor_hae)

    @staticmethod
    def sidecars(directory: Path) -> list[Path]:
        """Every truth sidecar under `directory`, one channel directory or a whole capture.

        A sidecar is known by its content, not its name: it is `<camera name>_<capture time>.xml`,
        or `SCTMV_<capture time>.xml` where it was written before cameras were named, and a
        directory may hold both, and the stills of several cameras."""
        return sorted(path for path in Path(directory).rglob("*.xml")
                      if TruthSidecarAudit._is_truth_sidecar(path))

    @staticmethod
    def _is_truth_sidecar(path: Path) -> bool:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError:
            return False
        return root.tag == "events" and root.get("source") == "truth"

    def audit(self, paths: Iterable[Path]) -> SidecarAuditResult:
        """Read the sidecars and count what they hold."""
        result = SidecarAuditResult()
        for path in paths:
            root = ET.parse(path).getroot()
            result.sidecars += 1
            tick = root.get("tick")
            described = root.get("telemetry_tick")
            if described is not None:
                result.sidecars_with_truth += 1
                if tick is not None and described != tick:
                    result.sidecars_truth_off_frame += 1
            vehicles = root.get("vehicles")
            if vehicles == "rendered":
                result.sidecars_listing_rendered += 1
            elif vehicles == "unknown":
                result.sidecars_listing_unknown += 1
            supervised = self._supervision(path, root, result)
            result.records.extend(self._records(path, root, None if tick is None else int(tick),
                                                supervised))

        self._band(result)
        uid_to_sumo: dict[str, set[str]] = defaultdict(set)
        sumo_to_uid: dict[str, set[str]] = defaultdict(set)
        actor_to_sumo: dict[str, set[str]] = defaultdict(set)
        for record in result.records:
            if result.floor_hae is not None and record.hae < result.floor_hae:
                result.below_band.append(record)
            self._check_supervision(record, result)
            self._check_in_frame(record, result)
            if record.sumo_id is None:
                result.without_sumo_id.append(record)
                continue
            uid_to_sumo[record.uid].add(record.sumo_id)
            sumo_to_uid[record.sumo_id].add(record.uid)
            actor_to_sumo[record.actor_id].add(record.sumo_id)
        result.uids_with_several_sumo_ids = {k: v for k, v in uid_to_sumo.items() if len(v) > 1}
        result.sumo_ids_with_several_uids = {k: v for k, v in sumo_to_uid.items() if len(v) > 1}
        result.actors_with_several_sumo_ids = {k: v for k, v in actor_to_sumo.items() if len(v) > 1}
        result.uids_lent_again_after_parking = self._lent_again_after_parking(result)
        return result

    @staticmethod
    def _supervision(path: Path, root: ET.Element, result: SidecarAuditResult) -> bool:
        """Count what one sidecar says of supervision; answer whether it names a plan, so its SUMO
        vehicle records ought to carry a state."""
        plan = root.get("plan_id")
        if root.get("supervision") == "unknown":
            result.sidecars_supervision_unknown += 1
            return False
        if plan is None:
            result.sidecars_saying_nothing_of_supervision.append(path.name)
            return False
        result.sidecars_with_plan += 1
        result.plans.add(plan)
        if not any(child.get("scope") == "world" for child in root.findall("_supervision")):
            result.sidecars_with_plan_without_world.append(path.name)
        return True

    @staticmethod
    def _check_supervision(record: VehicleRecord, result: SidecarAuditResult) -> None:
        """Hold one record's supervision to the three states, where its sidecar names a plan."""
        if record.supervision_state is None:
            if record.in_supervised_sidecar and record.sumo_id is not None:
                result.records_without_supervision.append(record)
            return
        if record.supervision_state not in SUPERVISION_STATES:
            result.records_with_unknown_state.append(record)
        elif record.supervision_state == "annotated" and record.annotations == 0:
            result.records_annotated_naming_nothing.append(record)
        elif record.supervision_state == "unlabelled" and record.annotations > 0:
            result.records_unlabelled_naming_something.append(record)

    @staticmethod
    def _check_in_frame(record: VehicleRecord, result: SidecarAuditResult) -> None:
        """Hold one record to saying where its box fell against the picture, to carrying occlusion
        fields only where the picture has a view of it, and to a reason wherever it carries none."""
        if record.in_frame is None:
            result.records_without_in_frame.append(record)
        elif record.in_frame not in IN_FRAME_VALUES:
            result.records_with_unknown_in_frame.append(record)
        elif record.occlusion_measured and record.in_frame not in IN_PICTURE:
            result.records_occluded_outside_picture.append(record)
        if record.occlusion_measured:
            if record.occlusion_unmeasured is not None:
                result.records_with_reason_beside_measurement.append(record)
        elif record.occlusion_unmeasured is None:
            if record.in_frame is not None:
                result.records_without_occlusion_or_reason.append(record)
        elif record.occlusion_unmeasured not in OCCLUSION_UNMEASURED_REASONS:
            result.records_with_unknown_reason.append(record)

    @staticmethod
    def _lent_again_after_parking(result: SidecarAuditResult) -> list[str]:
        """The uids seen on the road, below the ground band, then on the road again, in tick order."""
        if result.floor_hae is None:
            return []
        states: dict[str, list[bool]] = defaultdict(list)
        for record in sorted(result.records, key=lambda each: (each.tick or 0, each.sidecar)):
            on_road = record.hae >= result.floor_hae
            seen = states[record.uid]
            if not seen or seen[-1] != on_road:
                seen.append(on_road)
        return sorted(uid for uid, seen in states.items() if seen.count(True) >= 2)

    def _band(self, result: SidecarAuditResult) -> None:
        """Draw the ground band from the moving vehicles, or from the floor given."""
        moving = [record.hae for record in result.records if record.speed_mps >= MOVING_MPS]
        if moving:
            result.ceiling_hae = max(moving) + self.margin_m
        if self.floor_hae is not None:
            result.floor_hae = self.floor_hae
        elif moving:
            result.floor_hae = min(moving) - self.margin_m

    @staticmethod
    def _records(path: Path, root: ET.Element, tick: int | None,
                 supervised: bool = False) -> Iterable[VehicleRecord]:
        """The vehicle events of one sidecar: those carrying the `_carla` truth extras, which the
        collection platform's event does not."""
        for event in root.iter("event"):
            extras = event.find("detail/_carla")
            point = event.find("point")
            track = event.find("detail/track")
            if extras is None or point is None:
                continue
            supervision = event.find("detail/_supervision")
            yield VehicleRecord(
                sidecar=path.name, tick=tick, uid=event.get("uid", ""),
                actor_id=extras.get("actor_id", ""), sumo_id=extras.get("sumo_id"),
                hae=float(point.get("hae", "nan")),
                speed_mps=0.0 if track is None else float(track.get("speed", "0")),
                supervision_state=None if supervision is None else supervision.get("state", ""),
                annotations=0 if supervision is None else len(supervision.findall("annotation")),
                in_supervised_sidecar=supervised,
                in_frame=extras.get("in_frame"),
                occlusion_measured="occlusion" in extras.attrib,
                occlusion_unmeasured=extras.get("occlusion_unmeasured"))

    @staticmethod
    def describe(result: SidecarAuditResult) -> list[str]:
        """The counts, a line each, for a person."""
        total = len(result.records)
        lines = [f"{result.sidecars} truth sidecars, {result.sidecars_with_truth} with vehicle "
                 f"truth, {total} vehicle records"]
        lines.append(f"  listing their frame's rendered set: {result.sidecars_listing_rendered}; "
                     f"listing none because the frame's set was no longer held: "
                     f"{result.sidecars_listing_unknown}; truth from a neighbouring frame: "
                     f"{result.sidecars_truth_off_frame}")
        if result.floor_hae is None:
            lines.append("  ground band: no moving vehicle to draw it from and no floor given; "
                         "below-ground records not counted")
        else:
            ceiling = "" if result.ceiling_hae is None else f" to {result.ceiling_hae:.2f}"
            lines.append(f"  ground band: hae {result.floor_hae:.2f}{ceiling} m")
            still = sum(1 for record in result.below_band if record.speed_mps < MOVING_MPS)
            lines.append(f"  below the ground band: {len(result.below_band)} of {total} "
                         f"({_percent(len(result.below_band), total)}), {still} of them standing "
                         "still")
            if result.below_band:
                heights = sorted(record.hae for record in result.below_band)
                lines.append(f"    at hae {heights[0]:.2f} to {heights[-1]:.2f} m, from "
                             f"{len({record.actor_id for record in result.below_band})} actor(s)")
        lines.append(f"  without a SUMO id: {len(result.without_sumo_id)} of {total} "
                     f"({_percent(len(result.without_sumo_id), total)})")
        places = defaultdict(int)
        reasons = defaultdict(int)
        measured = 0
        for record in result.records:
            if record.in_frame is not None:
                places[record.in_frame] += 1
            if record.occlusion_measured:
                measured += 1
            elif record.occlusion_unmeasured is not None:
                reasons[record.occlusion_unmeasured] += 1
        lines.append("  in the picture: "
                     + (", ".join(f"{place} {places[place]}" for place in IN_FRAME_VALUES if places[place])
                        or "none said")
                     + f"; not said: {len(result.records_without_in_frame)} of {total}")
        lines.append(f"  occlusion measured: {measured} of {total}; unmeasured by reason: "
                     + (", ".join(f"{reason} {reasons[reason]}" for reason in sorted(reasons)) or "none")
                     + f"; neither measured nor a reason given: {len(result.records_without_occlusion_or_reason)}")
        if result.had_plan:
            states = defaultdict(int)
            for record in result.records:
                if record.supervision_state is not None:
                    states[record.supervision_state] += 1
            lines.append(f"  supervision: {result.sidecars_with_plan} sidecar(s) under plan "
                         f"{', '.join(sorted(result.plans)) or '-'}, {result.sidecars_supervision_unknown} "
                         f"with it unknown; vehicle states "
                         + (", ".join(f"{state} {states[state]}" for state in sorted(states)) or "none")
                         + f"; SUMO vehicle records with none: {len(result.records_without_supervision)}")
        else:
            lines.append("  supervision: no sidecar names a plan, so none is expected")
        if result.carries_sumo_ids:
            lines.append(f"  uids naming more than one SUMO vehicle: "
                         f"{len(result.uids_with_several_sumo_ids)}")
            lines.append(f"  SUMO vehicles under more than one uid: "
                         f"{len(result.sumo_ids_with_several_uids)}")
            lines.append(f"  bodies that rendered more than one SUMO vehicle (a pool reusing its "
                         f"bodies, expected): {len(result.actors_with_several_sumo_ids)}")
            for uid, names in list(result.uids_with_several_sumo_ids.items())[:5]:
                lines.append(f"    {uid}: {', '.join(sorted(names))}")
        else:
            lines.append("  uids naming more than one SUMO vehicle: not measurable directly -- no "
                         "record names its SUMO vehicle")
        if result.floor_hae is not None:
            lines.append(f"  uids on the road, parked and on the road again (a body lent again "
                         f"under one uid): {len(result.uids_lent_again_after_parking)}"
                         + (f" -- {', '.join(result.uids_lent_again_after_parking[:5])}"
                            if result.uids_lent_again_after_parking else ""))
        return lines


def _percent(part: int, whole: int) -> str:
    return "-" if whole == 0 else f"{100.0 * part / whole:.1f}%"
