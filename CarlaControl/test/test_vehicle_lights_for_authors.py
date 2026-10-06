"""Authors can see which bodies' lights work: in the skill's generated vehicle reference and in the
resolution report's vehicle section, per class and body, whether headlights, brake lights and turn
signals light up, read from the catalogue's optical pass (`lamp_capability`).

Information only (the owner's ruling, 2026-10-05): the co-simulation session drives every body's lights
by one rule, and a body whose lights are `unlit` is commanded and shows nothing, so an author choosing a
body for a night scenario needs the verdict in front of them. Nothing refuses or warns on it. The
reference is generated from the catalogue and the shipped file is held equal to it, as `checks.json`
and the schemas are (`07_Scenario_Authoring.md` §8.3).
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ResolutionReport import ResolutionReport  # noqa: E402
from carlacontrol.VehicleCatalogue import (  # noqa: E402
    LIGHT_GROUPS,
    LIGHT_NAMES,
    VehicleCatalogue,
    light_verdict,
)
from carlacontrol.VehicleReference import VehicleReference  # noqa: E402

SHIPPED_CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
SHIPPED_REFERENCE = (_REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios" / "references"
                     / "vehicles.md")

ALL_UNLIT = {lamp: "unlit" for _, lamps in LIGHT_GROUPS for lamp in lamps}


def body(blueprint: str, capability: dict | None, measured: bool = True) -> dict:
    entry = {"blueprint_id": blueprint, "measurement": "measured" if measured else "failed",
             "length_m": 4.5, "width_m": 2.0, "height_m": 1.5, "body_width_m": 1.8,
             "bbox_centre_m": [0.0, 0.0, 0.75]}
    if not measured:
        entry["measurement_note"] = "the body never settled"
    if capability is not None:
        entry["lamp_capability"] = capability
    return entry


def catalogue(*vehicles: dict, classes: list[dict]) -> VehicleCatalogue:
    return VehicleCatalogue({"catalogue_version": 1, "catalogue_id": "test", "catalogue_digest": "d",
                             "blueprint_set_digest": "b", "content_build_id": "build",
                             "vehicles": list(vehicles), "classes": classes})


def test_a_light_s_verdict_is_one_word_where_its_lamps_agree_and_each_lamp_s_where_they_do_not():
    assert light_verdict({"low_beam": "lit", "position": "lit"}, ("low_beam", "position")) == "lit"
    assert light_verdict({"low_beam": "unlit", "position": "unlit"}, ("low_beam", "position")) == "unlit"
    # Not measured is never summed into unlit: a lamp nobody measured is unknown.
    assert light_verdict(None, ("brake",)) == "unknown"
    assert light_verdict({}, ("left_blinker", "right_blinker")) == "unknown"
    assert light_verdict({"low_beam": "lit", "position": "unlit"}, ("low_beam", "position")) == \
        "low_beam lit, position unlit"
    assert light_verdict({"left_blinker": "lit"}, ("left_blinker", "right_blinker")) == \
        "left_blinker lit, right_blinker unknown"


def test_the_lights_of_a_body_come_from_its_lamp_capability_and_are_unknown_without_one():
    lit = dict(ALL_UNLIT, low_beam="lit", position="lit", brake="lit")
    held = catalogue(body("vehicle.a", lit), body("vehicle.b", ALL_UNLIT), body("vehicle.c", None),
                     classes=[])
    assert held.lights_of("vehicle.a") == {"headlights": "lit", "brake_lights": "lit",
                                           "turn_signals": "unlit"}
    assert held.lights_of("vehicle.b") == {light: "unlit" for light in LIGHT_NAMES}
    assert held.lights_of("vehicle.c") == {light: "unknown" for light in LIGHT_NAMES}
    assert held.lights_of("vehicle.nobody") == {light: "unknown" for light in LIGHT_NAMES}


def test_the_shipped_catalogue_s_bodies_show_no_headlight_brake_light_or_turn_signal_lit():
    """The fact an author needs: as measured, no body in this content build shows any of the three.
    Stated here so that a catalogue rebuilt with lit bodies changes this test and the reference
    together, never one without the other."""
    held = VehicleCatalogue.load(SHIPPED_CATALOGUE)
    assert held.blueprint_ids
    for blueprint in held.blueprint_ids:
        assert held.lights_of(blueprint) == {light: "unlit" for light in LIGHT_NAMES}, blueprint


def test_the_reference_states_each_class_s_bodies_with_their_lights_and_counts_the_lit_ones():
    lit = dict(ALL_UNLIT, low_beam="lit", position="lit")
    held = catalogue(
        body("vehicle.lit", lit), body("vehicle.dark", ALL_UNLIT), body("vehicle.lost", None, measured=False),
        classes=[{"class_id": "cars", "description": "Cars.", "sumo_vclass": "passenger",
                  "cot_base_type": "car", "members": [{"blueprint_id": "vehicle.lit", "weight": 1.0},
                                                      {"blueprint_id": "vehicle.dark", "weight": 1.0}]},
                 {"class_id": "vans", "sumo_vclass": "delivery", "cot_base_type": "van",
                  "cot_special_type": "emergency",
                  "members": [{"blueprint_id": "vehicle.lost", "weight": 1.0}]}])
    text = VehicleReference.render(held)
    assert "Catalogue `test`, digest `d`, content build `build`" in text
    assert "Of the 2 measured bodies, 1 show any of the three lights lit." in text
    assert "## `cars`\n\nCars.\n\nSUMO class `passenger`, base type `car`." in text
    assert "| `vehicle.lit` | 4.500 | 1.800 | 1.500 | lit | unlit | unlit |" in text
    assert "| `vehicle.dark` | 4.500 | 1.800 | 1.500 | unlit | unlit | unlit |" in text
    assert "## `vans`\n\nSUMO class `delivery`, base type `van`, kind `emergency`." in text
    # A body whose measurement failed is listed as such, its lights unknown, and refuses nothing.
    assert ("| `vehicle.lost` | not measured | not measured | not measured | unknown | unknown | "
            "unknown |") in text
    for word in ("refuse", "warning", "WARN"):
        assert word not in text


def test_the_shipped_reference_is_the_one_the_shipped_catalogue_generates(tmp_path):
    assert SHIPPED_REFERENCE.is_file(), "publish it with compile_scenario.py --write-vehicles-reference"
    held = VehicleCatalogue.load(SHIPPED_CATALOGUE)
    assert SHIPPED_REFERENCE.read_text(encoding="utf-8") == VehicleReference.render(held)
    written = VehicleReference.write(tmp_path / "references" / "vehicles.md", held)
    assert written.read_text(encoding="utf-8") == SHIPPED_REFERENCE.read_text(encoding="utf-8")
    # And it names the catalogue it came from, so a reference left behind by a rebuilt catalogue says so.
    document = json.loads(SHIPPED_CATALOGUE.read_text(encoding="utf-8"))
    assert document["catalogue_digest"] in SHIPPED_REFERENCE.read_text(encoding="utf-8")


def test_the_resolution_report_lists_every_type_s_body_with_its_lights():
    report = ResolutionReport()
    report.set("vehicle_types", {
        "catalogue": {"path": "vehicles.catalogue.json", "catalogue_id": "test",
                      "catalogue_digest": "d", "blueprint_set_digest": "b"},
        "classes": ["cars: 2 measured bodies, length 4.50-4.50 m, mean 4.50 m, share 1"],
        "mix": {"id": "", "probabilities": {}}, "mixes": [],
        "types": {"cars.vehicle.lit": {"blueprint": "vehicle.lit", "length_m": 4.5, "width_m": 2.0,
                                       "body_width_m": 1.8, "height_m": 1.5,
                                       "lights": {"headlights": "lit", "brake_lights": "unlit",
                                                  "turn_signals": "left_blinker lit, right_blinker unlit"}},
                  "cars.vehicle.old": {"blueprint": "vehicle.old", "length_m": 4.5, "width_m": 2.0,
                                       "body_width_m": None, "height_m": 1.5}}})
    text = report.to_markdown()
    assert "## Vehicle types" in text
    assert "Bodies from catalogue `test` (vehicles.catalogue.json, digest `d`)" in text
    assert "- cars: 2 measured bodies" in text
    assert ("| cars.vehicle.lit | vehicle.lit | 4.500 | 1.800 | 1.500 | lit | unlit | "
            "left_blinker lit, right_blinker unlit |") in text
    # A record written before the lights were carried reads unknown, never unlit.
    assert "| cars.vehicle.old | vehicle.old | 4.500 | - | 1.500 | unknown | unknown | unknown |" in text
    # A report with no vehicle section writes none.
    assert "## Vehicle types" not in ResolutionReport().to_markdown()
