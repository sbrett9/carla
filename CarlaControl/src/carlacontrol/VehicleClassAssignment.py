"""What kind of vehicle each blueprint is, derived from its measured box and curated where a box cannot say.

The content build declares a `BaseType` per vehicle and it is not reliable: seven of the seventeen
blueprints call themselves `bus` and none of them is one, an eighth declares nothing at all, and every
one of the seventeen leaves `SpecialType` empty, so the ambulance, the fire truck and the police car
are indistinguishable from a hatchback in the truth record. The catalogue therefore carries its own
answer and the truth producer takes it from there rather than from the blueprint.

Two sources, in this order:

  * **Derived from the measurement.** The bounding box measured by the sweep separates a car from a
    van from a truck cleanly on this content set -- the measured heights fall into three groups with
    wide gaps between them (1.30-1.77 m, 2.06-2.73 m, 3.83-4.24 m), so the band edges sit in empty
    space rather than through a cluster. The derivation never produces `bus`, because no measurement
    distinguishes a bus from a lorry of the same size and this content build contains neither a bus
    nor anything shaped like one.
  * **A curated override**, for the things a box cannot see: that a tall estate car is a sport utility
    and not a van, that a van with a red cross on it is an ambulance. Every override is validated
    against the swept blueprint set and the published vocabularies, and one that contradicts the
    derivation must carry a reason, so the file cannot quietly become a second, unexplained truth.

An override that merely restates what the measurement already says is rejected. Left unchecked, that
is how a curation file grows into a full table that nobody re-derives and everybody trusts.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

# The truth record's `base_type` vocabulary, as the CoT contract publishes it
# (Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md, section 4).
COT_BASE_TYPES = frozenset({"car", "van", "truck", "bus", "motorcycle", "bicycle"})

# The truth record's `special_type` vocabulary from the same source: emergency covers fire, ambulance
# and police. The empty string is the normal case and is spelled out so it cannot be mistaken for a
# missing value.
COT_SPECIAL_TYPES = frozenset({"", "emergency", "taxi", "electric"})

# SUMO's own vehicle-class names, read from its source table
# (Build/sumo-src/src/utils/common/SUMOVehicleClass.cpp, sumoVehicleClassStringInitializer). The
# route schema types this attribute as a bare string, so the schema will accept a misspelling that
# SUMO itself rejects at load; this list is what closes that gap.
SUMO_VEHICLE_CLASSES = frozenset({
    "ignoring", "private", "public_emergency", "emergency", "public_authority", "authority",
    "public_army", "army", "vip", "pedestrian", "passenger", "hov", "taxi", "public_transport",
    "bus", "coach", "delivery", "transport", "truck", "trailer", "motorcycle", "moped", "bicycle",
    "evehicle", "lightrail", "tram", "cityrail", "rail_urban", "rail_slow", "rail", "rail_electric",
    "rail_fast", "ship", "container", "cable_car", "subway", "aircraft", "wheelchair", "scooter",
    "drone", "custom1", "custom2",
})

# SUMO's own shape names, from the shape table in the same file. Read only by `sumo-gui`.
SUMO_GUI_SHAPES = frozenset({
    "pedestrian", "bicycle", "moped", "motorcycle", "passenger", "passenger/sedan",
    "passenger/hatchback", "passenger/wagon", "passenger/van", "taxi", "delivery", "transport",
    "truck", "transport/semitrailer", "truck/semitrailer", "transport/trailer", "truck/trailer",
    "bus/city", "bus", "bus/overland", "bus/coach", "bus/flexible", "bus/trolley", "evehicle",
    "ship", "emergency", "firebrigade", "police", "rickshaw", "scooter", "aircraft",
})

# Where the derivation cuts, in metres of measured height. Both edges sit in a gap in the measured
# distribution rather than inside a cluster: the tallest car measures 1.77 m and the shortest van
# 2.06 m; the tallest van measures 2.73 m and the shortest truck 3.83 m.
CAR_HEIGHT_CEILING_M = 2.0
VAN_HEIGHT_CEILING_M = 3.0

# A van is distinguished from a truck by height, but a long enough body is a lorry whatever its roof
# line. No blueprint in the measured set is affected by this edge; it exists so a future long, low
# trailer is not filed as a van.
VAN_LENGTH_CEILING_M = 7.0


@dataclass(frozen=True)
class VehicleClassOverride:
    """A curated correction to what the measurement derives for one blueprint."""

    blueprint_id: str
    reason: str
    base_type: str | None = None
    special_type: str | None = None


@dataclass(frozen=True)
class VehicleClassRecord:
    """What the catalogue publishes about one blueprint's kind, and where each part came from."""

    blueprint_id: str
    derived_base_type: str
    base_type: str
    special_type: str
    override_reason: str = ""

    @property
    def base_type_is_derived(self) -> bool:
        """True when the published base type is the measurement's own answer, uncorrected."""
        return self.base_type == self.derived_base_type


class VehicleClassAssignment:
    """Assigns a base and special type to every swept blueprint, and builds the authoring classes.

    Holds the project's default curation. A caller may supply its own in place of it, which is
    validated by exactly the same rules -- an override file is not a way around the checks.
    """

    # The corrections this content build needs. Everything not named here takes the derivation
    # unchanged, which is eleven of the seventeen blueprints.
    DEFAULT_OVERRIDES: tuple[VehicleClassOverride, ...] = (
        VehicleClassOverride(
            "vehicle.nissan.patrol", base_type="car",
            reason="sport utility on a car chassis; its 2.06 m roof reads as a van to the height "
                   "band, and it is the only measured body within 0.06 m of that edge"),
        VehicleClassOverride(
            "vehicle.ambulance.ford", special_type="emergency",
            reason="ambulance; the box measures as a van and the emergency role is not visible in it"),
        VehicleClassOverride(
            "vehicle.dodgecop.charger", special_type="emergency",
            reason="police car; identical in shape to the civilian Charger it is built from"),
        VehicleClassOverride(
            "vehicle.firetruck.actors", special_type="emergency",
            reason="fire appliance; the box measures as a truck and the emergency role is not "
                   "visible in it"),
        VehicleClassOverride(
            "vehicle.taxi.ford", special_type="taxi",
            reason="taxi; identical in shape to the Crown saloon it is built from"),
    )

    # The authoring classes. A scenario author asks for a class; SUMO draws a member from it; the
    # member is the blueprint. Every SUMO parameter is stated rather than defaulted, because SUMO's
    # own per-class defaults are large, invisible in the artifact, and differ between classes -- a
    # catalogue that leaves `speedDev` implicit is not reproducible. The values are SUMO's own
    # defaults for the class (Build/sumo-src/src/utils/vehicle/SUMOVTypeParameter.cpp), written out,
    # except `max_speed_mps`, which is set for a mixed urban and motorway network.
    DEFAULT_CLASSES: tuple[dict, ...] = (
        {
            "class_id": "civ_car",
            "description": "Ordinary civilian passenger cars. The default for background traffic.",
            "sumo_vclass": "passenger",
            "cot_base_type": "car",
            "members": [
                "vehicle.dodge.charger", "vehicle.lincoln.mkz", "vehicle.mini.cooper",
                "vehicle.nissan.patrol", "vehicle.ue4.audi.tt", "vehicle.ue4.bmw.grantourer",
                "vehicle.ue4.chevrolet.impala", "vehicle.ue4.ford.crown",
                "vehicle.ue4.ford.mustang", "vehicle.ue4.mercedes.ccc",
            ],
            "max_speed_mps": 35.0, "accel_mps2": 2.6, "decel_mps2": 4.5, "sigma": 0.5,
            "speed_factor_mean": 1.0, "speed_factor_dev": 0.1, "min_gap_m": 2.5,
            "gui_shape": "passenger", "gui_colour": "#CCCCD1",
        },
        {
            "class_id": "civ_van",
            "description": "Panel vans and light commercial bodies on a van chassis.",
            "sumo_vclass": "delivery",
            "cot_base_type": "van",
            "members": ["vehicle.sprinter.mercedes"],
            "max_speed_mps": 30.0, "accel_mps2": 2.6, "decel_mps2": 4.5, "sigma": 0.5,
            "speed_factor_mean": 1.0, "speed_factor_dev": 0.05, "min_gap_m": 2.5,
            "gui_shape": "delivery", "gui_colour": "#9AA0A6",
        },
        {
            "class_id": "civ_truck",
            "description": "Rigid lorries. Slower, longer and taller than anything civilian.",
            "sumo_vclass": "truck",
            "cot_base_type": "truck",
            "members": ["vehicle.carlacola.actors", "vehicle.fuso.mitsubishi"],
            "max_speed_mps": 25.0, "accel_mps2": 1.3, "decel_mps2": 4.0, "sigma": 0.5,
            "speed_factor_mean": 1.0, "speed_factor_dev": 0.05, "min_gap_m": 3.0,
            "gui_shape": "truck", "gui_colour": "#8C7B5A",
        },
        {
            "class_id": "taxi",
            "description": "Licensed taxis. Same body as a civilian saloon, different livery policy.",
            "sumo_vclass": "taxi",
            "cot_base_type": "car",
            "cot_special_type": "taxi",
            "members": ["vehicle.taxi.ford"],
            "max_speed_mps": 35.0, "accel_mps2": 2.6, "decel_mps2": 4.5, "sigma": 0.5,
            "speed_factor_mean": 1.0, "speed_factor_dev": 0.05, "min_gap_m": 2.5,
            "gui_shape": "taxi", "gui_colour": "#E8B93B",
        },
        {
            "class_id": "police",
            "description": "Marked police cars.",
            "sumo_vclass": "authority",
            "cot_base_type": "car",
            "cot_special_type": "emergency",
            "members": ["vehicle.dodgecop.charger"],
            "max_speed_mps": 40.0, "accel_mps2": 2.6, "decel_mps2": 4.5, "sigma": 0.4,
            "speed_factor_mean": 1.0, "speed_factor_dev": 0.05, "min_gap_m": 2.5,
            "gui_shape": "police", "gui_colour": "#3C6EDB",
        },
        {
            "class_id": "ambulance",
            "description": "Ambulances on a van chassis.",
            "sumo_vclass": "emergency",
            "cot_base_type": "van",
            "cot_special_type": "emergency",
            "members": ["vehicle.ambulance.ford"],
            "max_speed_mps": 35.0, "accel_mps2": 2.6, "decel_mps2": 4.5, "sigma": 0.4,
            "speed_factor_mean": 1.0, "speed_factor_dev": 0.05, "min_gap_m": 2.5,
            "gui_shape": "emergency", "gui_colour": "#D94F4F",
        },
        {
            "class_id": "fire_appliance",
            "description": "Fire appliances. The largest emergency body in the content build.",
            "sumo_vclass": "emergency",
            "cot_base_type": "truck",
            "cot_special_type": "emergency",
            "members": ["vehicle.firetruck.actors"],
            "max_speed_mps": 25.0, "accel_mps2": 1.3, "decel_mps2": 4.0, "sigma": 0.4,
            "speed_factor_mean": 1.0, "speed_factor_dev": 0.05, "min_gap_m": 3.0,
            "gui_shape": "firebrigade", "gui_colour": "#C62828",
        },
    )

    def __init__(self, overrides: tuple[VehicleClassOverride, ...] | None = None,
                 classes: tuple[dict, ...] | None = None) -> None:
        self.overrides = tuple(self.DEFAULT_OVERRIDES if overrides is None else overrides)
        self.classes = tuple(self.DEFAULT_CLASSES if classes is None else classes)

    @classmethod
    def from_file(cls, path: str | Path) -> VehicleClassAssignment:
        """Read a curation file: `{"overrides": [...], "classes": [...]}`, either key optional."""
        document = json.loads(Path(path).read_text(encoding="utf-8"))
        overrides = None
        if "overrides" in document:
            overrides = tuple(
                VehicleClassOverride(
                    blueprint_id=entry["blueprint_id"],
                    reason=entry.get("reason", ""),
                    base_type=entry.get("base_type"),
                    special_type=entry.get("special_type"))
                for entry in document["overrides"])
        classes = tuple(document["classes"]) if "classes" in document else None
        return cls(overrides, classes)

    @staticmethod
    def derive_base_type(length_m: float, width_m: float, height_m: float) -> str:
        """The kind of body a measured box is, from the box alone.

        `width_m` takes no part in the decision on this content set -- the widths of the cars and of
        the vans overlap -- and is accepted so the signature states everything the derivation was
        offered rather than everything it happened to use.
        """
        del width_m
        if height_m < CAR_HEIGHT_CEILING_M:
            return "car"
        if height_m < VAN_HEIGHT_CEILING_M and length_m < VAN_LENGTH_CEILING_M:
            return "van"
        return "truck"

    def assign(self, measurements: dict[str, tuple[float, float, float]]
               ) -> dict[str, VehicleClassRecord]:
        """Derive a kind for every measured blueprint and apply the curated overrides to it.

        `measurements` maps a blueprint id to its measured `(length_m, width_m, height_m)`. A
        blueprint whose measurement failed has no entry and takes no class: nothing is derived from a
        box that was never read.
        """
        records = {
            blueprint_id: VehicleClassRecord(
                blueprint_id=blueprint_id,
                derived_base_type=self.derive_base_type(*box),
                base_type=self.derive_base_type(*box),
                special_type="")
            for blueprint_id, box in measurements.items()
        }
        seen: set[str] = set()
        for override in self.overrides:
            self._check_override(override, records, seen)
            current = records[override.blueprint_id]
            records[override.blueprint_id] = VehicleClassRecord(
                blueprint_id=current.blueprint_id,
                derived_base_type=current.derived_base_type,
                base_type=override.base_type or current.base_type,
                special_type=(current.special_type if override.special_type is None
                              else override.special_type),
                override_reason=override.reason)
        return records

    def _check_override(self, override: VehicleClassOverride,
                        records: dict[str, VehicleClassRecord], seen: set[str]) -> None:
        """Refuse an override that names nothing, says nothing, or changes a type without saying why."""
        blueprint_id = override.blueprint_id
        if blueprint_id not in records:
            raise ValueError(
                f"curation overrides {blueprint_id!r}, which the sweep did not measure; the "
                f"measured blueprints are {', '.join(sorted(records))}")
        if blueprint_id in seen:
            raise ValueError(f"curation overrides {blueprint_id!r} more than once")
        seen.add(blueprint_id)
        if override.base_type is None and override.special_type is None:
            raise ValueError(f"curation entry for {blueprint_id!r} changes nothing")
        if override.base_type is not None and override.base_type not in COT_BASE_TYPES:
            raise ValueError(
                f"curation gives {blueprint_id!r} base type {override.base_type!r}, which is not "
                f"one of {', '.join(sorted(COT_BASE_TYPES))}")
        if override.special_type is not None and override.special_type not in COT_SPECIAL_TYPES:
            raise ValueError(
                f"curation gives {blueprint_id!r} special type {override.special_type!r}, which is "
                f"not one of {', '.join(sorted(t for t in COT_SPECIAL_TYPES if t))} or empty")
        derived = records[blueprint_id].derived_base_type
        if override.base_type is not None and override.base_type == derived:
            raise ValueError(
                f"curation restates {blueprint_id!r} as {derived!r}, which the measurement already "
                "derives; an override that changes nothing is a table growing a second copy of the "
                "derivation")
        if not override.reason.strip():
            raise ValueError(f"curation entry for {blueprint_id!r} carries no reason")

    def build_classes(self, records: dict[str, VehicleClassRecord]) -> list[dict]:
        """Turn the curated class templates into catalogue `classes[]` entries.

        Checks that every member was measured, that no blueprint is in two classes, that every
        measured blueprint reaches one, and that the SUMO vocabularies are SUMO's own.
        """
        built: list[dict] = []
        claimed: dict[str, str] = {}
        for template in self.classes:
            class_id = template["class_id"]
            if template["sumo_vclass"] not in SUMO_VEHICLE_CLASSES:
                raise ValueError(
                    f"class {class_id!r} declares SUMO vehicle class "
                    f"{template['sumo_vclass']!r}, which SUMO does not define")
            if template["gui_shape"] not in SUMO_GUI_SHAPES:
                raise ValueError(
                    f"class {class_id!r} declares guiShape {template['gui_shape']!r}, which SUMO "
                    "does not define")
            if template["cot_base_type"] not in COT_BASE_TYPES:
                raise ValueError(
                    f"class {class_id!r} declares base type {template['cot_base_type']!r}, which "
                    "the truth record's vocabulary does not carry")
            members = list(template["members"])
            if not members:
                raise ValueError(f"class {class_id!r} has no members")
            for blueprint_id in members:
                if blueprint_id not in records:
                    raise ValueError(
                        f"class {class_id!r} claims {blueprint_id!r}, which the sweep did not "
                        "measure")
                if blueprint_id in claimed:
                    raise ValueError(
                        f"{blueprint_id!r} is claimed by both {claimed[blueprint_id]!r} and "
                        f"{class_id!r}; the draw would then depend on which class an author named")
                claimed[blueprint_id] = class_id
                record = records[blueprint_id]
                special = template.get("cot_special_type", "")
                if (record.base_type, record.special_type) != (template["cot_base_type"], special):
                    raise ValueError(
                        f"class {class_id!r} publishes {blueprint_id!r} as "
                        f"{template['cot_base_type']!r}/{special or 'no special type'} while the "
                        f"blueprint itself resolves to {record.base_type!r}/"
                        f"{record.special_type or 'no special type'}; truth would then carry two "
                        "different answers for one vehicle")
            entry = {key: value for key, value in template.items() if key != "members"}
            entry["members"] = [{"blueprint_id": b, "weight": 1.0} for b in members]
            entry["render_colour_policy"] = template.get("render_colour_policy", "palette")
            built.append(entry)
        unclaimed = sorted(set(records) - set(claimed))
        if unclaimed:
            raise ValueError(
                "measured blueprints reach no class and are therefore unauthorable: "
                + ", ".join(unclaimed))
        return built
