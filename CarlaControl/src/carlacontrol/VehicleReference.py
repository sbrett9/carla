"""The authoring skill's vehicle reference, generated from the measured vehicle catalogue.

`07_Scenario_Authoring.md` §8.3: a file an author reads is generated from its source, never written
beside the skill, so it cannot drift from what the compiler holds. This one is
`skills/sumo-traffic-scenarios/references/vehicles.md`, written by `compile_scenario.py
--write-vehicles-reference` from `CarlaControl/catalogue/vehicles.catalogue.json`, and a test holds the
shipped file equal to what this module renders from the shipped catalogue.

What it states, per class and per body: the measured length, body width (SUMO's, without the mirrors)
and height, and whether the body's headlights, brake lights and turn signals light up when the
co-simulation session drives them -- headlights by the sun (`11_Time_And_Illumination.md` D11.9), brake
lights and turn signals from SUMO's signals (D11.8) -- read from the catalogue's optical pass
(`lamp_capability`). Information only: the session drives every body by the same rule, and a body whose
lamps are `unlit` is commanded and shows nothing. Nothing here refuses or warns.
"""
from __future__ import annotations

from pathlib import Path

from carlacontrol.VehicleCatalogue import LIGHT_GROUPS, VehicleCatalogue

LIGHT_HEADINGS = {"headlights": "Headlights", "brake_lights": "Brake lights",
                  "turn_signals": "Turn signals"}


class VehicleReference:
    """Renders and writes the vehicle reference from a catalogue."""

    @staticmethod
    def render(catalogue: VehicleCatalogue) -> str:
        """The reference as Markdown: the catalogue's identity, the light rule, one table per class."""
        lines = [
            "# Vehicles: the bodies a scenario may draw, and which of their lights work",
            "",
            "Generated from the measured vehicle catalogue by `compile_scenario.py "
            "--write-vehicles-reference`; do not edit by hand. Catalogue "
            f"`{catalogue.catalogue_id}`, digest `{catalogue.catalogue_digest}`, content build "
            f"`{catalogue.content_build_id}`. A test holds this file equal to what the catalogue "
            "generates.",
            "",
            "A vehicle class draws the bodies below it, one `vType` each, sized from the measurement: "
            "`length` and `height` are the body's measured box, `width` the body's without its wing "
            "mirrors. Declare a class by its `class_id` and the blueprints it draws; a blueprint not "
            "listed here has no measured body and is never rendered (checks 14, 15).",
            "",
            "**Lights.** The co-simulation session drives every rendered body's lights by one rule: "
            "headlights on below +3 degrees of the sun's geometric elevation and off above +6 by "
            "default, read from the sun the world reports, and brake lights and turn signals from the "
            "signals SUMO reports for the vehicle. Whether a light *shows* depends on the body's own lamp meshes, "
            "which the catalogue's optical pass measured against the dark: `lit` where switching the "
            "lamp changed the picture, `unlit` where it changed nothing, `unknown` where it was not "
            "measured. A body whose lights are `unlit` is commanded like every other and shows nothing, "
            "so a night scenario that wants visible headlights, brake lights or turn signals draws a "
            "body whose column reads `lit`. The run manifest's opening row records the rule each run "
            "ran under; per-vehicle light state is not in the truth record.",
            "",
        ]
        lit = sum(1 for blueprint in catalogue.blueprint_ids
                  if any(verdict == "lit" for verdict in catalogue.lights_of(blueprint).values()))
        lines += [f"Of the {len(catalogue.blueprint_ids)} measured bodies, {lit} show any of the three "
                  "lights lit.", ""]
        for class_id, entry in catalogue.classes.items():
            lines += [f"## `{class_id}`", ""]
            if entry.get("description"):
                lines += [str(entry["description"]), ""]
            lines += [f"SUMO class `{entry.get('sumo_vclass', '')}`, base type "
                      f"`{entry.get('cot_base_type', '')}`"
                      + (f", kind `{entry['cot_special_type']}`" if entry.get("cot_special_type")
                         else "") + ".", "",
                      "| Body | Length (m) | Body width (m) | Height (m) | "
                      + " | ".join(LIGHT_HEADINGS[light] for light, _ in LIGHT_GROUPS) + " |",
                      "|---|---|---|---|" + "---|" * len(LIGHT_GROUPS)]
            for member in entry.get("members", []):
                blueprint = member["blueprint_id"]
                lights = catalogue.lights_of(blueprint)
                try:
                    extent = catalogue.extent_of(blueprint)
                    length = f"{extent.length_m:.3f}"
                    width = "-" if extent.body_width_m is None else f"{extent.body_width_m:.3f}"
                    height = f"{extent.height_m:.3f}"
                except LookupError:
                    length = width = height = "not measured"
                lines.append(f"| `{blueprint}` | {length} | {width} | {height} | "
                             + " | ".join(lights[light] for light, _ in LIGHT_GROUPS) + " |")
            lines.append("")
        return "\n".join(lines).rstrip() + "\n"

    @classmethod
    def write(cls, path: str | Path, catalogue: VehicleCatalogue) -> Path:
        """Write the reference for `catalogue` to `path`."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(cls.render(catalogue), encoding="utf-8", newline="\n")
        return path
