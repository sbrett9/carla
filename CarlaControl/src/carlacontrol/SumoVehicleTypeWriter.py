"""Project the measured vehicle catalogue into SUMO vehicle types, and check them against SUMO's schema.

One `<vType>` per measured blueprint, with `length`, `width` and `height` copied verbatim from the
sweep, and one `<vTypeDistribution>` per authoring class. An author asks for a class, SUMO draws a
member from it, and the member *is* a blueprint -- so the body SUMO reserves road space for and the
body CARLA renders are the same body by construction, with no tolerance to argue about.

The blueprint id travels in a `<param>` rather than in the type id alone, because a `<param>` is what
the bridge can read back from a realised vehicle without parsing conventions out of a name. `<param>`
is a first-class child of `vTypeType` in SUMO's own schema, and this module proves it by validating
what it writes against that schema rather than by asserting it.

**A vType's colour is a `sumo-gui` property and selects nothing.** It is written so `sumo-gui` draws
a legible network and so an author can make an interesting vehicle conspicuous on screen; it never
reaches a blueprint, and the rendered colour comes from the blueprint's own measured palette. Colour
that followed the vType through to the imagery would make the author's on-screen highlighting a
covariate of whatever they highlighted.
"""
from __future__ import annotations

import logging
from pathlib import Path
from xml.etree import ElementTree
from xml.sax.saxutils import escape, quoteattr

from lxml import etree

from carlacontrol.VehicleCatalogue import BLUEPRINT_PARAM

# SUMO's route-file schema, relative to a SUMO installation's `data` directory.
ROUTES_SCHEMA_RELATIVE_PATH = Path("xsd") / "routes_file.xsd"

CLASS_PARAM = "carla:class_id"
CATALOGUE_DIGEST_PARAM = "carla:catalogue_digest"

logger = logging.getLogger(__name__)


class SumoVehicleTypeWriter:
    """Writes the catalogue's vehicle types as a SUMO route file and validates them against its schema."""

    def __init__(self, document: dict) -> None:
        self.document = document
        self.measured = {entry["blueprint_id"]: entry for entry in document.get("vehicles", [])
                         if entry.get("measurement") == "measured"}

    def to_xml(self) -> str:
        """The `<routes>` document: every measured type, then every class distribution over them."""
        digest = self.document.get("catalogue_digest", "")
        lines = [
            '<?xml version="1.0" encoding="UTF-8"?>',
            "<!-- Generated from vehicles.catalogue.json by carlacontrol.VehicleCatalogueBuilder.",
            "     Every length, width and height is a measurement of the rendered body; edit the",
            "     catalogue and re-run the sweep, never this file.",
            "     A vType's colour is read by sumo-gui alone and never reaches the rendered vehicle. -->",
            "<routes>",
        ]
        for class_entry in self.document.get("classes", []):
            for member in class_entry["members"]:
                lines.extend(self._vtype(class_entry, member["blueprint_id"], digest))
        for class_entry in self.document.get("classes", []):
            members = class_entry["members"]
            identifier = quoteattr(class_entry["class_id"])
            drawn = quoteattr(" ".join(m["blueprint_id"] for m in members))
            weights = quoteattr(" ".join(self._number(m["weight"]) for m in members))
            lines.append(
                f"    <vTypeDistribution id={identifier} vTypes={drawn} "
                f"probabilities={weights}/>")
        lines.append("</routes>")
        return "\n".join(lines) + "\n"

    def _vtype(self, class_entry: dict, blueprint_id: str, digest: str) -> list[str]:
        """One `<vType>`, with the dimensions copied verbatim and the blueprint named in a `<param>`."""
        vehicle = self.measured.get(blueprint_id)
        if vehicle is None:
            raise KeyError(
                f"class {class_entry['class_id']!r} names {blueprint_id!r}, which has no measured "
                "entry in the catalogue")
        # The dimensions come from the measurement and everything else from the class the blueprint
        # belongs to, so a type and the body it renders as cannot disagree about size.
        declared = [
            ("id", blueprint_id),
            ("vClass", class_entry["sumo_vclass"]),
            ("length", self._number(vehicle["length_m"])),
            ("width", self._number(vehicle["width_m"])),
            ("height", self._number(vehicle["height_m"])),
            ("minGap", self._number(class_entry["min_gap_m"])),
            ("maxSpeed", self._number(class_entry["max_speed_mps"])),
            ("accel", self._number(class_entry["accel_mps2"])),
            ("decel", self._number(class_entry["decel_mps2"])),
            ("sigma", self._number(class_entry["sigma"])),
            ("speedFactor", self._number(class_entry["speed_factor_mean"])),
            ("speedDev", self._number(class_entry["speed_factor_dev"])),
            ("guiShape", class_entry["gui_shape"]),
            ("color", class_entry["gui_colour"]),
        ]
        attributes = " ".join(f"{name}={quoteattr(value)}" for name, value in declared)
        params = [(BLUEPRINT_PARAM, blueprint_id), (CLASS_PARAM, class_entry["class_id"])]
        if digest:
            params.append((CATALOGUE_DIGEST_PARAM, digest))
        return ([f"    <vType {attributes}>"]
                + [f'        <param key="{escape(key)}" value={quoteattr(value)}/>'
                   for key, value in params]
                + ["    </vType>"])

    @staticmethod
    def _number(value: float) -> str:
        """A number SUMO reads back as the same number, without a trailing `.0` on a whole one."""
        text = f"{float(value):.6g}"
        return text

    def write(self, path: str | Path) -> Path:
        """Write the route file, UTF-8 with newline endings, and return where it landed."""
        destination = Path(path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(self.to_xml(), encoding="utf-8", newline="\n")
        return destination

    @staticmethod
    def validate(xml_path: str | Path, schema_path: str | Path) -> None:
        """Check a route file against SUMO's own `routes_file.xsd`. Raises with every error found.

        The schema types `vClass` and `guiShape` as bare strings, so it establishes the document's
        shape and not its vocabulary; `VehicleClassAssignment` checks the vocabulary against SUMO's
        own tables, and this docstring says so rather than letting a passing validation be read as
        approval of the names inside.
        """
        schema = etree.XMLSchema(etree.parse(str(schema_path)))
        document = etree.parse(str(xml_path))
        if not schema.validate(document):
            errors = "; ".join(f"line {e.line}: {e.message}" for e in schema.error_log)
            raise ValueError(f"{xml_path} is not valid against {schema_path}: {errors}")
        logger.info("%s is schema-valid against %s", xml_path, schema_path)

    @staticmethod
    def blueprint_of(vtype_element: ElementTree.Element) -> str | None:
        """The blueprint a parsed `<vType>` names, or None when it names none.

        Offered so a consumer reading a route file back -- a check, or the bridge's own load -- takes
        the blueprint from the same place the writer put it instead of re-deriving the convention.
        """
        for param in vtype_element.findall("param"):
            if param.get("key") == BLUEPRINT_PARAM:
                return param.get("value")
        return None
