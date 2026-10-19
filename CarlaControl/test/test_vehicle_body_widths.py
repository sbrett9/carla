"""The body width without mirrors: measured from an exported mesh, merged into the catalogue as a
measured input with its method, and refused where it does not describe the catalogue's meshes.

The catalogue's box is the whole mesh, wing mirrors included, and SUMO's width is the body's: with its
mirrors the Fuso bus measured 3.93 m on 3.35 m lanes and deadlocked the Bahonar pattern of life under
three-second lane changes, where its body is 3.23 m.
"""
from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
import sys  # noqa: E402

sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.VehicleCatalogueBuilder import VehicleCatalogueBuilder  # noqa: E402
from carlacontrol.VehicleCatalogueValidator import VehicleCatalogueValidator  # noqa: E402

CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
BODY_WIDTHS = _REPO / "CarlaControl" / "catalogue" / "vehicle_body_widths.json"
MEASURER = _REPO / "CarlaControl" / "scripts" / "measure_vehicle_body_widths.py"


def load_measurer():
    spec = importlib.util.spec_from_file_location("measure_vehicle_body_widths", MEASURER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def ascii_fbx(points: list[tuple[float, float, float]]) -> str:
    """The vertex block of an ASCII FBX, which is all the measurement reads."""
    flat = ",".join(f"{value:g}" for point in points for value in point)
    return f"Geometry: 1, \"Geometry::body\", \"Mesh\" {{\n\tVertices: *{3 * len(points)} {{\n\t\ta: {flat}\n\t}}\n}}\n"


def test_a_mirror_shorter_than_the_opening_is_left_out_of_the_body(tmp_path):
    """A 4 m by 1.8 m body with mirrors 20 cm long reaching 0.25 m out each side: the full width is
    2.3 m and the body's 1.8 m."""
    points = []
    for x in range(0, 401, 5):
        for y in (-90.0, 90.0):
            points.append((float(x), y, 0.0))
            points.append((float(x), y, 140.0))
    for x in (100.0, 110.0, 120.0):
        points.append((x, -115.0, 100.0))
        points.append((x, 115.0, 100.0))
    fbx = tmp_path / "vehicle.test.car.fbx"
    fbx.write_text(ascii_fbx(points), encoding="utf-8")

    widths = load_measurer().measure(fbx)

    assert widths["full_width_m"] == pytest.approx(2.30)
    assert widths["body_width_m"] == pytest.approx(1.80)
    assert widths["length_m"] == pytest.approx(4.00)
    assert widths["height_m"] == pytest.approx(1.40)


def test_the_table_in_the_tree_covers_every_measured_blueprint_and_its_full_widths_are_the_boxes():
    table = VehicleCatalogueBuilder.load_body_widths(BODY_WIDTHS)
    document = json.loads(CATALOGUE.read_text(encoding="utf-8"))
    measured = {e["blueprint_id"]: e for e in document["vehicles"] if e["measurement"] == "measured"}
    assert set(table["vehicles"]) == set(measured)
    for blueprint_id, row in table["vehicles"].items():
        assert row["full_width_m"] == pytest.approx(measured[blueprint_id]["width_m"], abs=1e-3)
        assert row["body_width_m"] == measured[blueprint_id]["body_width_m"]
    assert document["body_width"]["method"] == table["method"]
    assert VehicleCatalogue.digest_of(document) == document["catalogue_digest"]


def test_merging_the_table_gives_every_measured_body_its_width_and_the_header_its_method():
    document = json.loads(CATALOGUE.read_text(encoding="utf-8"))
    for entry in document["vehicles"]:
        entry.pop("body_width_m", None)
    del document["body_width"]
    assert VehicleCatalogueValidator(document).failures()

    VehicleCatalogueBuilder.apply_body_widths(document, VehicleCatalogueBuilder.load_body_widths(BODY_WIDTHS))

    assert VehicleCatalogueValidator(document).failures() == []
    fuso = next(e for e in document["vehicles"] if e["blueprint_id"] == "vehicle.fuso.mitsubishi")
    assert (fuso["width_m"], fuso["body_width_m"]) == (3.9277, 3.2328)
    assert document["body_width"]["source"] == "vehicle_body_widths.json"


def test_a_table_of_another_mesh_is_refused_rather_than_merged():
    """Its full width is the same vertex extent the sweep's box is, so a disagreement means the two
    measured different meshes."""
    document = json.loads(CATALOGUE.read_text(encoding="utf-8"))
    table = copy.deepcopy(VehicleCatalogueBuilder.load_body_widths(BODY_WIDTHS))
    table["vehicles"]["vehicle.fuso.mitsubishi"]["full_width_m"] = 3.5

    with pytest.raises(ValueError, match="vehicle.fuso.mitsubishi: the table's full width 3.5 m"):
        VehicleCatalogueBuilder.apply_body_widths(document, table)
