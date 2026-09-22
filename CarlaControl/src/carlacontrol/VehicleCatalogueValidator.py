"""The checks a generated vehicle catalogue has to pass before it is written.

Each check reports what it can establish from the document in front of it. None of them decides what a
field *means*: that a blueprint is really a van, that a lamp really ought to light, that a class's
acceleration is sensible -- those are a person's calls, made where the value is written, with the
reasoning beside them.

**What this validator cannot see**, said plainly so its silence is not mistaken for approval:

  * whether the measurements are of the content the catalogue claims to describe. Only the digests and
    a re-run of the sweep can say that;
  * whether the sweep leaked an actor and so measured a later blueprint against a colliding spawn. The
    actor counts are a fact about the sweep, not about the document, and the builder refuses on them;
  * whether a lamp recorded `unlit` is a content defect or a correct reading of a vehicle with no such
    lamp. The probe records what the pixels did and nothing more.
"""
from __future__ import annotations

import re

from carlacontrol.VehicleCatalogue import LAMP_NAMES, LAMP_VERDICTS

BLUEPRINT_ID_PATTERN = re.compile(r"vehicle\.[a-z0-9_.]+$")
CLASS_ID_PATTERN = re.compile(r"[a-z][a-z0-9_]{0,31}$")
SUMO_COLOUR_PATTERN = re.compile(r"#[0-9A-Fa-f]{6}$")
CARLA_COLOUR_PATTERN = re.compile(r"\d{1,3},\d{1,3},\d{1,3}$")

# A measured body outside this range is not a vehicle, it is a measurement that went wrong -- a box
# taken before the mesh loaded, or one that swallowed an attached actor.
MINIMUM_DIMENSION_M = 0.2
MAXIMUM_DIMENSION_M = 30.0

# Every SUMO driving parameter a class must state rather than leave to SUMO's per-class default. The
# defaults are large, differ between classes and are invisible in the artifact, so a catalogue that
# omits one is not reproducible from what it contains.
REQUIRED_CLASS_PARAMETERS = (
    "accel_mps2", "decel_mps2", "sigma", "speed_factor_mean", "speed_factor_dev", "min_gap_m",
    "max_speed_mps",
)

COLOUR_APPLIED_VALUES = frozenset({"true", "false", "unknown"})


class VehicleCatalogueValidator:
    """Runs every generation-time check over a catalogue document and reports all failures at once.

    All at once rather than on the first: a sweep takes minutes against a live server, and a
    validator that stops at the first fault makes the operator pay that cost once per fault.
    """

    def __init__(self, document: dict) -> None:
        self.document = document

    def failures(self) -> list[str]:
        """Every rule this document breaks, each named with the rule and the offending value."""
        found: list[str] = []
        found += self._check_vehicles()
        found += self._check_colours()
        found += self._check_lamps()
        found += self._check_classes()
        return found

    def validate(self) -> None:
        """Raise `ValueError` naming every failure, or return quietly when there are none."""
        found = self.failures()
        if found:
            raise ValueError(
                f"the catalogue breaks {len(found)} rule(s):\n  " + "\n  ".join(found))

    def _check_vehicles(self) -> list[str]:
        """V1.1 identity and V1.2 dimensions, including the box centre the pose conversion needs."""
        found: list[str] = []
        seen: set[str] = set()
        for entry in self.document.get("vehicles", []):
            blueprint_id = entry.get("blueprint_id", "")
            if not BLUEPRINT_ID_PATTERN.match(blueprint_id):
                found.append(f"V1.1: {blueprint_id!r} is not a vehicle blueprint id")
            if blueprint_id in seen:
                found.append(f"V1.1: {blueprint_id!r} appears more than once")
            seen.add(blueprint_id)
            measurement = entry.get("measurement")
            if measurement not in ("measured", "failed"):
                found.append(
                    f"V1.1: {blueprint_id!r} has measurement {measurement!r}, which is neither "
                    "'measured' nor 'failed'")
            if measurement != "measured":
                if not entry.get("measurement_note"):
                    found.append(f"V1.1: {blueprint_id!r} failed to measure but gives no reason")
                continue
            for field in ("length_m", "width_m", "height_m"):
                value = entry.get(field)
                if not isinstance(value, int | float):
                    found.append(f"V1.2: {blueprint_id!r} has no {field}")
                elif not MINIMUM_DIMENSION_M < float(value) < MAXIMUM_DIMENSION_M:
                    found.append(
                        f"V1.2: {blueprint_id!r} {field} is {value} m, outside "
                        f"{MINIMUM_DIMENSION_M}-{MAXIMUM_DIMENSION_M} m")
            centre = entry.get("bbox_centre_m")
            if not isinstance(centre, list | tuple) or len(centre) != 3:
                found.append(
                    f"V1.2: {blueprint_id!r} has no three-component bbox_centre_m, without which "
                    "the bumper-to-origin shift cannot be computed")
        return found

    def _check_colours(self) -> list[str]:
        """V1.3 palette form, V1.4 the colour verdict, and V1.15 the two colour spellings."""
        found: list[str] = []
        for entry in self.document.get("vehicles", []):
            blueprint_id = entry.get("blueprint_id", "")
            for colour in entry.get("colour_palette", []):
                if not CARLA_COLOUR_PATTERN.match(str(colour)):
                    found.append(
                        f"V1.15: {blueprint_id!r} palette entry {colour!r} is not three "
                        "comma-separated integers")
                    continue
                if any(not 0 <= int(part) <= 255 for part in str(colour).split(",")):
                    found.append(
                        f"V1.3: {blueprint_id!r} palette entry {colour!r} has a channel outside "
                        "0-255")
            applied = entry.get("colour_applied")
            if applied not in COLOUR_APPLIED_VALUES:
                found.append(
                    f"V1.4: {blueprint_id!r} colour_applied is {applied!r}, not one of "
                    f"{', '.join(sorted(COLOUR_APPLIED_VALUES))}")
        for class_entry in self.document.get("classes", []):
            colour = class_entry.get("gui_colour", "")
            if not SUMO_COLOUR_PATTERN.match(str(colour)):
                found.append(
                    f"V1.15: class {class_entry.get('class_id')!r} gui_colour {colour!r} is not "
                    "#RRGGBB; a SUMO comma triple whose components are all at most 1 is re-read as "
                    "fractions of 255 and changes colour silently")
        return found

    def _check_lamps(self) -> list[str]:
        """V1.17: the probe's own conditions, and all eleven verdicts on every measured entry."""
        found: list[str] = []
        probe = self.document.get("lamp_probe")
        if not isinstance(probe, dict) or "ran" not in probe:
            found.append("V1.17: lamp_probe is absent or does not say whether the pass ran")
        elif not probe["ran"] and not probe.get("reason"):
            found.append("V1.17: lamp_probe did not run and gives no reason")
        for entry in self.document.get("vehicles", []):
            if entry.get("measurement") != "measured":
                continue
            blueprint_id = entry.get("blueprint_id", "")
            capability = entry.get("lamp_capability")
            if not isinstance(capability, dict):
                found.append(f"V1.17: {blueprint_id!r} carries no lamp_capability")
                continue
            for lamp in LAMP_NAMES:
                verdict = capability.get(lamp)
                if verdict is None:
                    found.append(
                        f"V1.17: {blueprint_id!r} is missing lamp {lamp!r}; an absent key is "
                        "indistinguishable from an unmeasured one")
                elif verdict not in LAMP_VERDICTS:
                    found.append(
                        f"V1.17: {blueprint_id!r} lamp {lamp!r} is {verdict!r}, not one of "
                        f"{', '.join(sorted(LAMP_VERDICTS))}")
            for lamp in set(capability) - set(LAMP_NAMES):
                found.append(f"V1.17: {blueprint_id!r} carries lamp {lamp!r}, which is not a lamp")
        return found

    def _check_classes(self) -> list[str]:
        """V1.5 membership, V1.6 explicit driving parameters, V1.7 a class that draws from nothing."""
        found: list[str] = []
        measured = {entry.get("blueprint_id") for entry in self.document.get("vehicles", [])
                    if entry.get("measurement") == "measured"}
        for class_entry in self.document.get("classes", []):
            class_id = class_entry.get("class_id", "")
            if not CLASS_ID_PATTERN.match(str(class_id)):
                found.append(f"V1.7: class id {class_id!r} is not a lower-case identifier")
            members = class_entry.get("members", [])
            if not members:
                found.append(f"V1.7: class {class_id!r} has no members to draw from")
            for member in members:
                blueprint_id = member.get("blueprint_id")
                if blueprint_id not in measured:
                    found.append(
                        f"V1.5: class {class_id!r} draws {blueprint_id!r}, which has no measured "
                        "entry")
                if not isinstance(member.get("weight"), int | float) or member["weight"] <= 0:
                    found.append(
                        f"V1.7: class {class_id!r} gives {blueprint_id!r} weight "
                        f"{member.get('weight')!r}, which cannot be drawn")
            for parameter in REQUIRED_CLASS_PARAMETERS:
                if not isinstance(class_entry.get(parameter), int | float):
                    found.append(
                        f"V1.6: class {class_id!r} does not state {parameter}; SUMO's own default "
                        "would apply and it is not visible in the artifact")
        return found
