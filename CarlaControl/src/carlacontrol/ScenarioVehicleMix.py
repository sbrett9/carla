"""Bind a scenario's declared vehicle classes to measured bodies, and write them as SUMO types.

A SUMO `vType` is a driving model and a body at once: its `length` and `width` decide the gaps the
car-following model reserves and the space a junction counts as occupied, and the co-simulation
bridge converts SUMO's front-bumper reference point to CARLA's actor origin using that same length.
The vehicle SUMO reserved road for and the vehicle CARLA draws therefore have to be the same one,
which is what the vehicle mapping contract fixes (contract `C1` in
`Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/04_Contracts.md`).

This is the authoring half of that contract. A scenario declares what kinds of vehicle its traffic is
made of and how each kind drives; this class supplies the bodies, from the measured catalogue and
from nowhere else:

  * one `<vType>` per named blueprint, its `length`, `width` and `height` copied verbatim from the
    sweep that measured that body, and the blueprint named in a `<param key="carla:blueprint"/>` so
    the bridge reads a binding rather than parsing a convention out of an id;
  * one `<vTypeDistribution>` per declared class, so the variety inside a class is SUMO's own seeded
    draw, recorded in the behavioural simulation, rather than a choice made at playback;
  * one `<vTypeDistribution>` over every class that takes part in the scenario's traffic mix, each
    body's probability being its class's share times its weight inside that class. SUMO has no
    nested distributions, so the mix is flat and the arithmetic happens here.

Everything else about a type is the author's and is copied through untouched: the dimensions are the
catalogue's, the driving model is the author's, and nothing is invented in between. A `length`,
`width` or `height` among the author's own attributes is refused rather than overwritten, because an
author who wrote a length believed it meant something and is owed the disagreement.

**A colour selects nothing.** `sumo-gui` reads `vType@color`; the rendered colour is drawn from the
blueprint's own measured palette instead, identically for interesting and ordinary vehicles of one
class, so that an author highlighting a vehicle on screen cannot make the highlight a property of
what they highlighted. A colour here is an authoring aid and is written `#RRGGBB`: SUMO re-reads a
comma triple whose components are all at most 1 as fractions of 255, so `"1,0,0"` is red rather than
near-black, and hex carries no such ambiguity.

**A body the catalogue does not hold is refused, never approximated.** There is no nearest match. A
substituted body makes the imagery and the behavioural record disagree while each stays internally
consistent, and nothing downstream can detect that. A scenario asking for a kind of vehicle this
content build has none of is an authoring decision to take in the open, which is why the refusal
names the catalogue and every body it measured.
"""
from __future__ import annotations

import logging
import re
import textwrap
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path
from xml.etree import ElementTree
from xml.sax.saxutils import escape, quoteattr

from carlacontrol.SumoVehicleTypeWriter import CATALOGUE_DIGEST_PARAM, CLASS_PARAM
from carlacontrol.VehicleCatalogue import BLUEPRINT_PARAM, VehicleCatalogue
from carlacontrol.VehicleClassAssignment import SUMO_GUI_SHAPES, SUMO_VEHICLE_CLASSES

# The dimensions a type takes from the measurement, and the attributes this class writes itself. An
# author's attribute set may name none of them: a scenario that restates a dimension has an opinion
# about the body that the measurement is going to contradict silently.
MEASURED_ATTRIBUTES = ("length", "width", "height")
GENERATED_ATTRIBUTES = ("id", "vClass", "guiShape", "color", *MEASURED_ATTRIBUTES)

# Vehicle classes outside the mapping contract entirely. A two-wheeler carries a rider, riders are
# not rendered, and this content build registers no two-wheeled blueprint for the sweep to measure,
# so there is nothing to draw and nothing to fall back to.
TWO_WHEELER_CLASSES = frozenset({"motorcycle", "moped", "bicycle"})

# Colour form every SUMO artifact uses, per the contract. Hex rather than a comma triple because
# SUMO re-reads an all-at-most-1 triple as fractions of 255.
GUI_COLOUR_PATTERN = re.compile(r"^#[0-9A-Fa-f]{6}$")

# How far a declared dimension may sit from the measurement it was copied from. This absorbs decimal
# serialisation and nothing else: it is not a licence to substitute a body of a different size.
DIMENSION_TOLERANCE_M = 0.01

# Elements that name a vehicle type through a `type` attribute.
TYPE_REFERRING_ELEMENTS = ("flow", "trip", "vehicle")

# Width the explanatory comments are wrapped to, so the written file stays readable beside the types.
COMMENT_WIDTH = 96

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class VehicleClassSpec:
    """One kind of vehicle a scenario asks for: which measured bodies it draws, and how they drive.

    `blueprints` names CARLA blueprints, which the catalogue must hold a successful measurement for.
    `behaviour` is the author's SUMO attributes, copied through verbatim -- `maxSpeed`, `speedFactor`,
    `speedDev`, `sigma`, `tau`, `minGap`, `accel`, `decel` and anything else `vType` accepts, except
    the dimensions, which come from the measurement.

    `share` is this class's weight in the scenario's traffic mix, as the author wrote it. It is not
    required to sum to anything across the classes: the emitted probabilities are normalised, so
    declaring the shares of a scenario whose classes no longer all exist redistributes the missing
    one in proportion rather than silently favouring whichever class was listed first. `share` of
    zero keeps the class out of the mix, for a class only a named vehicle uses.

    `gui_colour` and `gui_shape` are read by `sumo-gui` and by nothing else. Neither reaches a
    rendered body.
    """

    class_id: str
    blueprints: tuple[str, ...]
    sumo_vclass: str
    behaviour: dict[str, str] = field(default_factory=dict)
    share: float = 0.0
    weights: tuple[float, ...] = ()
    gui_shape: str = ""
    gui_colour: str = ""
    note: str = ""

    def member_weights(self) -> tuple[float, ...]:
        """The weight of each body inside this class, defaulting to an equal draw."""
        return self.weights if self.weights else tuple(1.0 for _ in self.blueprints)


class ScenarioVehicleMix:
    """The measured bodies a scenario's traffic is drawn from, as SUMO vehicle types.

    Built from a catalogue and a list of declared classes, and refuses at construction rather than at
    write: a scenario naming a body nothing measured is an authoring error, and the point of finding
    it here is that the alternative is finding it when the imagery does not match the truth record.
    """

    def __init__(self, catalogue: VehicleCatalogue, classes: Sequence[VehicleClassSpec],
                 mix_id: str = "", header: str = "") -> None:
        self.catalogue = catalogue
        self.classes = tuple(classes)
        self.mix_id = mix_id
        self.header = header
        self._check()

    def type_id(self, class_id: str, blueprint_id: str) -> str:
        """The `vType` id for one body inside one class.

        The class is part of the id because two classes may draw the same body with different driving
        models -- the same saloon ambling along a residential street and hurrying out of town. The id
        is for a person reading the file; what binds the type to a blueprint is its `<param>`.
        """
        return f"{class_id}.{blueprint_id}"

    def member_probabilities(self) -> dict[str, float]:
        """Each body's probability in the scenario mix, keyed by `vType` id, normalised to one.

        Empty when no class takes part in the mix.
        """
        weighted: dict[str, float] = {}
        for entry in self.classes:
            if entry.share <= 0.0:
                continue
            weights = entry.member_weights()
            total = sum(weights)
            for blueprint_id, weight in zip(entry.blueprints, weights, strict=True):
                weighted[self.type_id(entry.class_id, blueprint_id)] = entry.share * weight / total
        grand_total = sum(weighted.values())
        if not grand_total:
            return {}
        return {name: value / grand_total for name, value in weighted.items()}

    def summary(self) -> list[str]:
        """One line per class: the bodies it draws and the measured lengths they render at."""
        lines = []
        for entry in self.classes:
            extents = [self.catalogue.extent_of(blueprint) for blueprint in entry.blueprints]
            lengths = [extent.length_m for extent in extents]
            mean = sum(lengths) / len(lengths)
            lines.append(
                f"{entry.class_id}: {len(extents)} measured "
                f"{'body' if len(extents) == 1 else 'bodies'}, "
                f"length {min(lengths):.2f}-{max(lengths):.2f} m, mean {mean:.2f} m, "
                f"share {entry.share:g}")
        return lines

    def to_xml(self) -> str:
        """The `<vType>` and `<vTypeDistribution>` block, indented to sit inside a `<routes>`."""
        lines: list[str] = []
        if self.header:
            lines.extend(self._comment(self.header))
        for entry in self.classes:
            if entry.note:
                lines.extend(self._comment(entry.note))
            for blueprint_id in entry.blueprints:
                lines.extend(self._vtype(entry, blueprint_id))
        lines.append("")
        for entry in self.classes:
            members = [self.type_id(entry.class_id, blueprint) for blueprint in entry.blueprints]
            lines.append(self._distribution(entry.class_id, members, entry.member_weights()))
        probabilities = self.member_probabilities()
        if self.mix_id and probabilities:
            lines.append("")
            lines.append(self._distribution(self.mix_id, list(probabilities),
                                            tuple(probabilities.values())))
        return "\n".join(lines) + "\n"

    def _vtype(self, entry: VehicleClassSpec, blueprint_id: str) -> list[str]:
        """One `<vType>`: the measured box, the author's driving model, and the binding `<param>`."""
        extent = self.catalogue.extent_of(blueprint_id)
        declared = [
            ("id", self.type_id(entry.class_id, blueprint_id)),
            ("vClass", entry.sumo_vclass),
            ("length", self._number(extent.length_m)),
            ("width", self._number(extent.width_m)),
            ("height", self._number(extent.height_m)),
        ]
        declared.extend(sorted(entry.behaviour.items()))
        if entry.gui_shape:
            declared.append(("guiShape", entry.gui_shape))
        if entry.gui_colour:
            declared.append(("color", entry.gui_colour))
        attributes = " ".join(f"{name}={quoteattr(value)}" for name, value in declared)
        params = [(BLUEPRINT_PARAM, blueprint_id), (CLASS_PARAM, entry.class_id)]
        if self.catalogue.catalogue_digest:
            params.append((CATALOGUE_DIGEST_PARAM, self.catalogue.catalogue_digest))
        return ([f"    <vType {attributes}>"]
                + [f'        <param key="{escape(key)}" value={quoteattr(value)}/>'
                   for key, value in params]
                + ["    </vType>"])

    def _distribution(self, identifier: str, members: Sequence[str],
                      weights: Sequence[float]) -> str:
        """One `<vTypeDistribution>` over named types, with their weights."""
        return (f"    <vTypeDistribution id={quoteattr(identifier)} "
                f"vTypes={quoteattr(' '.join(members))} "
                f"probabilities={quoteattr(' '.join(self._number(w) for w in weights))}/>")

    @staticmethod
    def _comment(text: str) -> list[str]:
        """An XML comment block, wrapped and indented, with the sequence XML forbids removed.

        A pair of hyphens anywhere inside a comment makes SUMO reject the whole file, and an author
        writing a dash-separated aside has no reason to know that.
        """
        wrapped: list[str] = []
        for paragraph in text.strip().replace("--", "-").split("\n"):
            wrapped.extend(textwrap.wrap(paragraph.strip(), width=COMMENT_WIDTH) or [""])
        body = "\n         ".join(wrapped)
        return ["    <!-- " + body + " -->"]

    @staticmethod
    def _number(value: float) -> str:
        """A number SUMO reads back as the same number, without a trailing zero on a whole one."""
        return f"{float(value):.6g}"

    def _check(self) -> None:
        """Refuse a declaration that cannot produce a renderable type, naming everything wrong."""
        problems: list[str] = []
        seen_classes: set[str] = set()
        seen_type_ids: set[str] = set()
        for entry in self.classes:
            if entry.class_id in seen_classes:
                problems.append(f"class {entry.class_id!r} is declared twice")
            seen_classes.add(entry.class_id)
            if not entry.blueprints:
                problems.append(
                    f"class {entry.class_id!r} names no blueprint, so nothing would be rendered "
                    f"for it. The catalogue measured: {', '.join(self.catalogue.blueprint_ids)}")
            weights = entry.member_weights()
            if len(weights) != len(entry.blueprints):
                problems.append(
                    f"class {entry.class_id!r} declares {len(weights)} weights for "
                    f"{len(entry.blueprints)} blueprints")
            elif any(weight <= 0 for weight in weights):
                problems.append(f"class {entry.class_id!r} declares a weight that is not positive")
            if entry.share < 0:
                problems.append(f"class {entry.class_id!r} declares a negative share")
            problems.extend(self._class_vocabulary_problems(entry))
            for name in sorted(set(entry.behaviour) & set(GENERATED_ATTRIBUTES)):
                problems.append(
                    f"class {entry.class_id!r} declares {name!r} among its driving attributes. "
                    f"{'A dimension comes from the measurement' if name in MEASURED_ATTRIBUTES else 'That attribute is written from the class declaration'}, "
                    "so declaring it here would be overwritten or would contradict the body")
            for blueprint_id in entry.blueprints:
                type_id = self.type_id(entry.class_id, blueprint_id)
                if type_id in seen_type_ids:
                    problems.append(f"class {entry.class_id!r} names {blueprint_id!r} twice")
                seen_type_ids.add(type_id)
                try:
                    self.catalogue.extent_of(blueprint_id)
                except LookupError as refused:
                    problems.append(f"class {entry.class_id!r}: {refused}")
        if self.mix_id and not self.member_probabilities():
            problems.append(
                f"mix {self.mix_id!r} was asked for but no class declares a share above zero")
        if problems:
            raise ValueError(
                "this scenario's vehicle classes cannot be bound to measured bodies:\n  "
                + "\n  ".join(problems))

    def _class_vocabulary_problems(self, entry: VehicleClassSpec) -> list[str]:
        """Names SUMO would reject at load, and the two-wheeler classes the contract refuses."""
        problems = []
        if entry.sumo_vclass in TWO_WHEELER_CLASSES:
            problems.append(
                f"class {entry.class_id!r} declares vClass {entry.sumo_vclass!r}. Two-wheelers are "
                "outside the vehicle mapping contract: a rider would not be rendered, and this "
                "content build registers no two-wheeled blueprint to measure")
        elif entry.sumo_vclass not in SUMO_VEHICLE_CLASSES:
            problems.append(
                f"class {entry.class_id!r} declares vClass {entry.sumo_vclass!r}, which is not one "
                "of SUMO's own vehicle classes")
        if entry.gui_shape and entry.gui_shape not in SUMO_GUI_SHAPES:
            problems.append(
                f"class {entry.class_id!r} declares guiShape {entry.gui_shape!r}, which is not one "
                "of SUMO's own shapes")
        if entry.gui_colour and not GUI_COLOUR_PATTERN.match(entry.gui_colour):
            problems.append(
                f"class {entry.class_id!r} declares colour {entry.gui_colour!r}; a SUMO artifact "
                "writes #RRGGBB, because SUMO re-reads a comma triple of small numbers as "
                "fractions of 255")
        return problems

    @classmethod
    def check_route_file(cls, path: str | Path, catalogue: VehicleCatalogue) -> list[str]:
        """Read a written route file back and check every type against the catalogue.

        What it establishes, from the file and the catalogue alone:

          * every `<vType>` names a blueprint through `carla:blueprint`, and that blueprint has a
            successful measurement in this catalogue;
          * its `length`, `width` and `height` equal that measurement, to the tolerance that absorbs
            decimal serialisation;
          * every `type=` on a flow, trip or vehicle names a type or distribution the file declares;
          * no type declares a two-wheeler vehicle class.

        What it cannot see: whether the bodies suit the scenario, whether the shares are the ones the
        author meant, or whether the network permits a class on the edges its flows route over. It
        reports the types it checked so a caller can log a number rather than a claim.

        Raises `ValueError` listing every failure. Returns the `vType` ids it checked.
        """
        root = ElementTree.parse(str(path)).getroot()
        problems: list[str] = []
        checked: list[str] = []
        declared: set[str] = set()
        for element in root.iter("vTypeDistribution"):
            declared.add(element.get("id", ""))
        for element in root.iter("vType"):
            type_id = element.get("id", "")
            declared.add(type_id)
            checked.append(type_id)
            problems.extend(cls._vtype_problems(element, type_id, catalogue))
        for tag in TYPE_REFERRING_ELEMENTS:
            for element in root.iter(tag):
                named = element.get("type")
                if named is not None and named not in declared:
                    problems.append(
                        f"{tag} {element.get('id', '(unnamed)')!r} is of type {named!r}, which this "
                        "file declares as neither a vType nor a vTypeDistribution")
        if problems:
            raise ValueError(
                f"{path} does not satisfy the vehicle mapping contract:\n  " + "\n  ".join(problems))
        logger.info("%s: %d vehicle types, every one bound to a measured body", path, len(checked))
        return checked

    @classmethod
    def _vtype_problems(cls, element: ElementTree.Element, type_id: str,
                        catalogue: VehicleCatalogue) -> list[str]:
        """Everything wrong with one parsed `<vType>`, as sentences a reader can act on."""
        problems = []
        if element.get("vClass") in TWO_WHEELER_CLASSES:
            problems.append(
                f"vType {type_id!r} declares vClass {element.get('vClass')!r}, which is outside the "
                "vehicle mapping contract")
        blueprint_id = None
        for param in element.findall("param"):
            if param.get("key") == BLUEPRINT_PARAM:
                blueprint_id = param.get("value")
        if not blueprint_id:
            problems.append(
                f"vType {type_id!r} carries no {BLUEPRINT_PARAM} parameter, so it names no rendered "
                "body and every vehicle of that type would be simulated and never drawn")
            return problems
        try:
            extent = catalogue.extent_of(blueprint_id)
        except LookupError as refused:
            problems.append(f"vType {type_id!r}: {refused}")
            return problems
        measured = {"length": extent.length_m, "width": extent.width_m, "height": extent.height_m}
        for attribute, value in measured.items():
            declared = element.get(attribute)
            if declared is None:
                problems.append(
                    f"vType {type_id!r} declares no {attribute}, so SUMO would apply its vehicle "
                    f"class default instead of the body's measured {value:.4g} m")
                continue
            difference = abs(float(declared) - value)
            if difference > DIMENSION_TOLERANCE_M:
                extra = (f", which would place every such body {difference / 2:.2f} m from where "
                         "SUMO believes it is" if attribute == "length" else "")
                problems.append(
                    f"vType {type_id!r} declares {attribute} {declared} against a measured "
                    f"{value:.4g} m for {blueprint_id}{extra}")
        return problems
