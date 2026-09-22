"""Read side of the vehicle catalogue: vehicle type to measured extent, and the bumper shift.

SUMO reports a vehicle's position at the centre of its front bumper; CARLA places an actor at the
body's origin. Converting one to the other needs the vehicle's measured length and the offset of its
bounding-box centre from that origin, and SUMO holds neither: its `length` is a declared number in a
route file, not a measurement of the rendered body. So the co-simulation bridge loads this catalogue
at run start and asks it for the extent of every vehicle type it is about to place.

A vehicle type this catalogue cannot answer for is **refusable, and must be refused**. Falling back to
SUMO's declared length would place a body of unknown size at a pose computed from a number nobody
measured, and the error is invisible afterwards because the imagery and the truth record would agree
with each other while both were wrong. Three ways a type is unanswerable, and one response to all
three:

  * it carries no `carla:blueprint` parameter, so nothing names a rendered body (`no_blueprint`);
  * it names a blueprint the catalogue does not hold (`unknown_extent`);
  * it names one whose measurement failed during the sweep (`unknown_extent`).

`resolve` raises on all three; `refusal` returns the reason without raising, for a caller that wants
to record the vehicle as simulated-but-unrendered and carry on.
"""
from __future__ import annotations

import hashlib
import json
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path

# The `<param>` on a SUMO `<vType>` that names the CARLA blueprint the type is measured from. A flat,
# un-namespaced key: SUMO's own devices read `<param>` the same way and never interpret this one.
BLUEPRINT_PARAM = "carla:blueprint"

# Schema shape this reader implements. A catalogue declaring anything else is refused rather than
# read on a best-effort basis, because a field that moved silently is worse than one that is absent.
SUPPORTED_CATALOGUE_VERSION = 1

NO_BLUEPRINT = "no_blueprint"
UNKNOWN_EXTENT = "unknown_extent"

# The eleven lamps a CARLA vehicle light state can command, in snake case, with the bit each one
# occupies in the mask. The bits mirror `carla::rpc::VehicleLightState`; `NONE` and `All` are the
# empty and full masks rather than lamps and are not listed. Published here because both the optical
# probe that measures them and the checks that read the result need the same eleven names, and a
# lamp missing from one side of that pair is indistinguishable from a lamp measured as absent.
LAMP_BITS: tuple[tuple[str, int], ...] = (
    ("position", 0x1),
    ("low_beam", 0x2),
    ("high_beam", 0x4),
    ("brake", 0x8),
    ("right_blinker", 0x10),
    ("left_blinker", 0x20),
    ("reverse", 0x40),
    ("fog", 0x80),
    ("interior", 0x100),
    ("special_1", 0x200),
    ("special_2", 0x400),
)

LAMP_NAMES: tuple[str, ...] = tuple(name for name, _ in LAMP_BITS)

# What the optical pass may conclude about one lamp. `unknown` is the value for "not measured" and
# must never be confused with `unlit`, which is a measurement that the lamp changed nothing.
LAMP_VERDICTS = frozenset({"lit", "unlit", "unknown"})


class UnrenderableVehicleTypeError(LookupError):
    """A vehicle type whose rendered extent the catalogue cannot supply.

    Carries the reason verbatim so the caller can record it in the run's accounting of vehicles that
    were simulated but never rendered, rather than re-deriving it from the message text.
    """

    def __init__(self, vtype_id: str, reason: str, detail: str) -> None:
        super().__init__(f"vehicle type {vtype_id!r} cannot be rendered ({reason}): {detail}")
        self.vtype_id = vtype_id
        self.reason = reason
        self.detail = detail


@dataclass(frozen=True)
class VehicleExtent:
    """One blueprint's measured box, and the two offsets a pose conversion needs from it.

    `length_m`, `width_m` and `height_m` are twice the measured bounding-box extent, and
    `bbox_centre_m` is that box's centre in the actor's own frame -- so a body whose mesh is not
    centred on its origin is placed correctly rather than silently shifted.
    """

    blueprint_id: str
    length_m: float
    width_m: float
    height_m: float
    bbox_centre_m: tuple[float, float, float]

    @property
    def bumper_to_origin_m(self) -> float:
        """Along-heading distance from the front-bumper centre back to the actor origin.

        Half the body's length places the box centre; the box centre's own forward offset places the
        actor origin under it. A blueprint whose mesh sits forward of its origin has a positive
        `bbox_centre_m[0]` and therefore a longer shift than half its length.
        """
        return self.length_m / 2.0 + self.bbox_centre_m[0]

    @property
    def lateral_offset_m(self) -> float:
        """Sideways offset of the box centre from the actor origin, in the actor's own frame.

        Measured at or near zero for every blueprint in the shipped content, which is why it is
        exposed rather than assumed: a future blueprint whose mesh is offset sideways would otherwise
        be mis-seated laterally with nothing to show for it.
        """
        return self.bbox_centre_m[1]


class VehicleCatalogue:
    """The measured vehicle catalogue, indexed for lookup at simulation rate.

    Built once at run start and read on every vehicle the bridge places, so the index is a plain dict
    built at load time rather than a scan of the document.
    """

    def __init__(self, document: Mapping) -> None:
        version = document.get("catalogue_version")
        if version != SUPPORTED_CATALOGUE_VERSION:
            raise ValueError(
                f"catalogue_version {version!r} is not version {SUPPORTED_CATALOGUE_VERSION}, "
                "which is the only shape this reader implements")
        self.document = document
        self.catalogue_id: str = document.get("catalogue_id", "")
        self.catalogue_digest: str = document.get("catalogue_digest", "")
        self.blueprint_set_digest: str = document.get("blueprint_set_digest", "")
        self.content_build_id: str = document.get("content_build_id", "")
        self._extents: dict[str, VehicleExtent] = {}
        self._failed: dict[str, str] = {}
        for entry in document.get("vehicles", []):
            blueprint_id = entry["blueprint_id"]
            if entry.get("measurement") != "measured":
                self._failed[blueprint_id] = entry.get("measurement_note", "measurement failed")
                continue
            centre = entry["bbox_centre_m"]
            self._extents[blueprint_id] = VehicleExtent(
                blueprint_id=blueprint_id,
                length_m=float(entry["length_m"]),
                width_m=float(entry["width_m"]),
                height_m=float(entry["height_m"]),
                bbox_centre_m=(float(centre[0]), float(centre[1]), float(centre[2])),
            )
        self.classes: dict[str, dict] = {c["class_id"]: c for c in document.get("classes", [])}

    @classmethod
    def load(cls, path: str | Path) -> VehicleCatalogue:
        """Read a catalogue from disk. The file is UTF-8 JSON as the sweep writes it."""
        return cls(json.loads(Path(path).read_text(encoding="utf-8")))

    @staticmethod
    def canonical_json(document: Mapping) -> str:
        """The document's one canonical serialisation: sorted keys, two-space indent, UTF-8, `\\n`.

        Two implementations that disagree about whitespace disagree about identity, so the digest is
        defined over this form and the sweep writes the file in it. Kept beside the reader rather
        than with the writer so that checking a digest costs a consumer nothing but this module.
        """
        return json.dumps(document, sort_keys=True, indent=2, ensure_ascii=False) + "\n"

    @classmethod
    def digest_of(cls, document: Mapping) -> str:
        """SHA-256 of the canonical document with its own digest field emptied."""
        without = dict(document)
        without["catalogue_digest"] = ""
        return hashlib.sha256(cls.canonical_json(without).encode("utf-8")).hexdigest()

    @property
    def blueprint_ids(self) -> list[str]:
        """Every blueprint the catalogue measured successfully, in document order."""
        return list(self._extents)

    def extent_of(self, blueprint_id: str) -> VehicleExtent:
        """The measured extent of one blueprint, by its CARLA definition id."""
        extent = self._extents.get(blueprint_id)
        if extent is None:
            raise UnrenderableVehicleTypeError(blueprint_id, UNKNOWN_EXTENT, self._why(blueprint_id))
        return extent

    def resolve(self, vtype_id: str, params: Mapping[str, str]) -> VehicleExtent:
        """The extent for a realised SUMO vehicle type, or raise `UnrenderableVehicleTypeError`."""
        blueprint_id = params.get(BLUEPRINT_PARAM)
        if not blueprint_id:
            raise UnrenderableVehicleTypeError(
                vtype_id, NO_BLUEPRINT,
                f"no {BLUEPRINT_PARAM} parameter, so no rendered body is named")
        extent = self._extents.get(blueprint_id)
        if extent is None:
            raise UnrenderableVehicleTypeError(vtype_id, UNKNOWN_EXTENT, self._why(blueprint_id))
        return extent

    def refusal(self, vtype_id: str, params: Mapping[str, str]) -> str | None:
        """The refusal reason for a vehicle type, or None when it can be rendered.

        The same decision `resolve` makes, without the exception, for the caller that reports one
        warning per distinct vehicle type and keeps the run going.
        """
        try:
            self.resolve(vtype_id, params)
        except UnrenderableVehicleTypeError as refused:
            return refused.reason
        return None

    def _why(self, blueprint_id: str) -> str:
        """Why a blueprint has no extent: measured and failed, or never in the catalogue at all."""
        note = self._failed.get(blueprint_id)
        if note is not None:
            return f"blueprint {blueprint_id!r} was swept but its measurement failed: {note}"
        return (f"blueprint {blueprint_id!r} is not in catalogue {self.catalogue_id!r}, "
                f"which holds {len(self._extents)} measured blueprints")
