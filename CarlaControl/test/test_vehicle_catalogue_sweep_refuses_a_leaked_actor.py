"""The sweep refuses to publish anything if it did not leave the world as it found it.

The sweep spawns one blueprint at a time precisely because concurrent spawns collide, and a collision
is reported the same way as a blueprint that cannot spawn at all. An actor left behind breaks that:
the next spawn may land on it, and the measurement taken from the result would be a fiction that looks
exactly like a measurement. So the count of vehicle actors at the end has to equal the count at the
start, and nothing is written when it does not.

This is the one generation-time rule the catalogue validator cannot check, because it is a fact about
the sweep rather than about the document, and it is exercised here against a world that keeps one
vehicle the sweep did not create.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

_builder_module = pytest.importorskip(
    "carlacontrol.VehicleCatalogueBuilder",
    reason="the sweep needs carlanet, which needs the CarlaNet assemblies on this machine")
VehicleCatalogueBuilder = _builder_module.VehicleCatalogueBuilder


class _Vector:
    def __init__(self, x: float, y: float, z: float) -> None:
        self.x, self.y, self.z = x, y, z


class _BoundingBox:
    def __init__(self) -> None:
        self.extent = _Vector(2.2763, 1.0478, 0.8862)
        self.location = _Vector(-0.034, 0.0, 0.8884)


class _Attribute:
    """One attribute as the server's definition record carries it, flags and all."""

    def __init__(self, identifier: str, value: str, recommended: list[str], modifiable: bool,
                 restricted: bool) -> None:
        self.Id = identifier
        self.Type = "String"
        self.Value = value
        self.RecommendedValues = recommended
        self.IsModifiable = modifiable
        self.RestrictToRecommended = restricted


class _Attributes:
    """The definition's attribute list, which the sweep indexes and counts rather than iterates."""

    def __init__(self, attributes: list[_Attribute]) -> None:
        self._attributes = attributes
        self.Count = len(attributes)

    def __getitem__(self, index: int) -> _Attribute:
        return self._attributes[index]


class _Definition:
    def __init__(self, attributes: _Attributes) -> None:
        self.Attributes = attributes


class _Blueprint:
    """As much of a blueprint as the dimension pass reads off one."""

    def __init__(self, blueprint_id: str) -> None:
        self.id = blueprint_id
        self.tags = blueprint_id.replace(".", ",")
        self._uid = 1
        self._def = _Definition(_Attributes([
            _Attribute("color", "0,0,0", ["0,0,0"], True, False),
            _Attribute("base_type", "car", [], False, False),
        ]))
        self._attrs = {
            "base_type": {"type": 4, "value": "car", "recommended": [], "modifiable": False},
            "special_type": {"type": 4, "value": "", "recommended": [], "modifiable": False},
            "number_of_wheels": {"type": 1, "value": "4", "recommended": [], "modifiable": False},
            "generation": {"type": 1, "value": "3", "recommended": [], "modifiable": False},
            "has_lights": {"type": 2, "value": "true", "recommended": [], "modifiable": False},
            "color": {"type": 5, "value": "0,0,0", "recommended": ["0,0,0"], "modifiable": True},
        }

    def set_attribute(self, name: str, value: str) -> None:
        self._attrs[name]["value"] = value


class _Actor:
    def __init__(self, type_id: str, world: _World) -> None:
        self.type_id = type_id
        self.id = 1
        self.bounding_box = _BoundingBox()
        self._world = world

    def set_simulate_physics(self, enabled: bool) -> None:
        del enabled

    def destroy(self) -> bool:
        return True


class _Map:
    def get_spawn_points(self):
        class _Point:
            location = _Vector(0.0, 0.0, 0.0)
        return [_Point()]


class _World:
    """A world that quietly gains a vehicle while the sweep is running."""

    def __init__(self, leak_after: int | None) -> None:
        self.leak_after = leak_after
        self.spawns = 0
        self.blueprints = [_Blueprint("vehicle.mini.cooper"), _Blueprint("vehicle.dodge.charger")]

    def get_blueprint_library(self):
        return self.blueprints

    def get_map(self):
        return _Map()

    def get_actors(self):
        leaked = self.leak_after is not None and self.spawns > self.leak_after
        return [_Actor("vehicle.ghost.leaked", self)] if leaked else []

    def spawn_actor(self, blueprint, transform):
        del transform
        self.spawns += 1
        return _Actor(blueprint.id, self)


class _Client:
    @staticmethod
    def get_server_version() -> str:
        return "0.10.0"


def test_a_sweep_that_leaves_an_extra_vehicle_behind_writes_nothing():
    world = _World(leak_after=1)
    builder = VehicleCatalogueBuilder(_Client(), world)
    with pytest.raises(RuntimeError, match="leaked"):
        builder.build(probe_lamps=False)


def test_a_sweep_that_leaves_the_world_as_it_found_it_gets_past_the_leak_check():
    """The control: the same sweep over the same world runs on and stops somewhere else entirely.

    This fake world offers two blueprints, so the curation -- which names seventeen -- is the next
    thing to object. That it objects at all is the point: the leak check is what the first case
    exercises, and without a leak it lets the sweep through.
    """
    world = _World(leak_after=None)
    builder = VehicleCatalogueBuilder(_Client(), world)
    with pytest.raises(ValueError, match="which the sweep did not measure"):
        builder.build(probe_lamps=False)
