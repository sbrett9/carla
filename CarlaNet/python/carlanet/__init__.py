"""
carlanet.py — Python shim matching the libcarla client API.

Drop-in replacement for 'carla' Python module using CarlaNet (.NET 10) instead
of native libcarla.  Exposes snake_case classes and attributes that match the
original PythonAPI so existing scripts work with minimal modification.

Usage:
    import carlanet as carla          # instead of 'import carla'
    client = carla.Client("localhost", 2000)
    world  = client.get_world()

Environment:
    CARLANET_PUBLISH_DIR  Overrides DLL discovery.  By default DLLs are loaded
                          from the '<package>/dlls/' subdirectory (pip-installed
                          layout).  If that subdirectory is missing, the loader
                          falls back to DLLs sitting alongside this script
                          (legacy publish/ layout).

§12: sys.path bootstrapper — MUST remove this file's directory before importing clr.
§13.8: Python namespace package finder can shadow CLR imports otherwise.
"""
import sys
import os
import time
import threading
import fnmatch

_this_dir = os.path.dirname(os.path.abspath(__file__))
if _this_dir in sys.path:
    sys.path.remove(_this_dir)

# ── .NET runtime selection ────────────────────────────────────────────────────
import clr_loader
import pythonnet as _pythonnet

def _find_runtime(prefix="10."):
    specs = list(clr_loader.find_runtimes())
    matches = [s for s in specs
               if s.name == "Microsoft.NETCore.App" and s.version.startswith(prefix)]
    if not matches and prefix:
        matches = [s for s in specs if s.name == "Microsoft.NETCore.App"]
    return sorted(matches, key=lambda s: s.version, reverse=True)[0] if matches else None

_spec = _find_runtime("10.") or _find_runtime("")
if _spec is not None:
    _pythonnet.load(clr_loader.get_coreclr(runtime_spec=_spec))
else:
    _pythonnet.load("coreclr")

import clr

# Prefer DLLs bundled inside the package (pip-installed layout).
# Fall back to DLLs sitting alongside this script (legacy publish/ layout).
# Either can be overridden by the CARLANET_PUBLISH_DIR env var.
_DEFAULT_DLL_DIR = os.path.join(_this_dir, "dlls")
if not os.path.isdir(_DEFAULT_DLL_DIR):
    _DEFAULT_DLL_DIR = _this_dir
_PUBLISH_DIR = os.path.normpath(
    os.environ.get("CARLANET_PUBLISH_DIR", _DEFAULT_DLL_DIR))

def _ref(name):
    path = os.path.join(_PUBLISH_DIR, name + ".dll")
    if not os.path.exists(path):
        raise FileNotFoundError(
            f"CarlaNet assembly not found: {path}\n"
            f"Run python/build_wheel.ps1 or set CARLANET_PUBLISH_DIR.")
    clr.AddReference(path)

_ref("CarlaNet.Types")
_ref("CarlaNet.Transport")
_ref("CarlaNet.Sensors")
# Wave 4 integration: CarlaNet.Map + CarlaNet.TrafficManager provide the
# in-process TrafficManager. Failure to load these is non-fatal — the shim
# falls back to _NoOpTrafficManager in get_trafficmanager() if the assemblies
# aren't available.
try:
    _ref("CarlaNet.Map")
    _ref("CarlaNet.TrafficManager")
    _CARLANET_TM_AVAILABLE = True
except FileNotFoundError:
    _CARLANET_TM_AVAILABLE = False

# ── C# type imports ───────────────────────────────────────────────────────────
from CarlaNet.Transport import CarlaClient as _CarlaClient
from CarlaNet.Types.Geom import (Transform as _CSTransform,
                                  Location as _CSLocation,
                                  Rotation as _CSRotation,
                                  Vector2D as _CSVector2D,
                                  Vector3D as _CSVector3D,
                                  BoundingBox as _CSBoundingBox,
                                  GeoLocation)

# Python factory wrappers so upstream kwarg construction (e.g.
# `carla.Location(x=1.0, z=2.8)`) works — pythonnet won't translate Python
# lowercase kwargs to C# PascalCase parameter names.
def Location(x=0.0, y=0.0, z=0.0):
    return _CSLocation(float(x), float(y), float(z))

def Rotation(pitch=0.0, yaw=0.0, roll=0.0):
    return _CSRotation(float(pitch), float(yaw), float(roll))

def Vector3D(x=0.0, y=0.0, z=0.0):
    return _CSVector3D(float(x), float(y), float(z))

def Vector2D(x=0.0, y=0.0):
    return _CSVector2D(float(x), float(y))

def Transform(location=None, rotation=None):
    return _CSTransform(location if location is not None else _CSLocation(),
                        rotation if rotation is not None else _CSRotation())

def BoundingBox(location=None, extent=None, rotation=None):
    return _CSBoundingBox(location if location is not None else _CSLocation(),
                          extent if extent is not None else _CSVector3D(),
                          rotation if rotation is not None else _CSRotation())
from CarlaNet.Types.Rpc.Actors import (Actor as _Actor, ActorDefinition,
                                        ActorDescription, ActorAttributeValue)
from CarlaNet.Types.Rpc.Control import (
    VehicleControl as _CSVehicleControl,
    VehicleAckermannControl as _CSVehicleAckermannControl,
    AckermannControllerSettings as _CSAckermannControllerSettings,
    WalkerControl as _CSWalkerControl,
)


# Mutable Python wrappers for the C# record-struct control types.
# The C# types have init-only properties, but manual_control.py and similar
# scripts mutate fields between RPC calls (`control.throttle = 0.0`).
# Each wrapper exposes the upstream snake_case attributes and a `_to_cs()`
# method that produces a fresh C# value when sent over the wire.

class VehicleControl:
    def __init__(self, throttle=0.0, steer=0.0, brake=0.0,
                 hand_brake=False, reverse=False,
                 manual_gear_shift=False, gear=0):
        self.throttle = float(throttle)
        self.steer = float(steer)
        self.brake = float(brake)
        self.hand_brake = bool(hand_brake)
        self.reverse = bool(reverse)
        self.manual_gear_shift = bool(manual_gear_shift)
        self.gear = int(gear)
    def _to_cs(self):
        return _CSVehicleControl(self.throttle, self.steer, self.brake,
                                 self.hand_brake, self.reverse,
                                 self.manual_gear_shift, self.gear)
    def __repr__(self):
        return (f"VehicleControl(throttle={self.throttle}, steer={self.steer}, "
                f"brake={self.brake}, hand_brake={self.hand_brake}, "
                f"reverse={self.reverse}, manual_gear_shift={self.manual_gear_shift}, "
                f"gear={self.gear})")


class VehicleAckermannControl:
    def __init__(self, steer=0.0, steer_speed=0.0, speed=0.0,
                 acceleration=0.0, jerk=0.0):
        self.steer = float(steer)
        self.steer_speed = float(steer_speed)
        self.speed = float(speed)
        self.acceleration = float(acceleration)
        self.jerk = float(jerk)
    def _to_cs(self):
        return _CSVehicleAckermannControl(self.steer, self.steer_speed,
                                          self.speed, self.acceleration,
                                          self.jerk)


class WalkerControl:
    def __init__(self, direction=None, speed=0.0, jump=False):
        self.direction = direction if direction is not None else Vector3D()
        self.speed = float(speed)
        self.jump = bool(jump)
    def _to_cs(self):
        d = self.direction
        if not isinstance(d, _CSVector3D):
            d = _CSVector3D(float(d.x), float(d.y), float(d.z))
        return _CSWalkerControl(d, self.speed, self.jump)


class AckermannControllerSettings:
    def __init__(self, speed_kp=0.0, speed_ki=0.0, speed_kd=0.0,
                 accel_kp=0.0, accel_ki=0.0, accel_kd=0.0):
        self.speed_kp = float(speed_kp)
        self.speed_ki = float(speed_ki)
        self.speed_kd = float(speed_kd)
        self.accel_kp = float(accel_kp)
        self.accel_ki = float(accel_ki)
        self.accel_kd = float(accel_kd)
    def _to_cs(self):
        return _CSAckermannControllerSettings(self.speed_kp, self.speed_ki,
                                              self.speed_kd, self.accel_kp,
                                              self.accel_ki, self.accel_kd)


def _control_to_cs(ctrl):
    """Convert a Python control wrapper to its C# value; pass others through."""
    return ctrl._to_cs() if hasattr(ctrl, "_to_cs") else ctrl


class _PhysicsControlWrapper:
    """Mutable wrapper around a C# VehiclePhysicsControl record struct.

    Manual scripts read the current physics, mutate fields like
    `use_sweep_wheel_collision = True`, then call `apply_physics_control`.
    The C# record struct is init-only, so we copy the 30 snake_case fields
    into a Python object and rebuild a fresh C# value on `_to_cs()`.
    """
    _FIELDS = (
        "torque_curve", "max_torque", "max_rpm", "idle_rpm", "brake_effect",
        "rev_up_moi", "rev_down_rate", "differential_type", "front_rear_split",
        "use_automatic_gears", "gear_change_time", "final_ratio",
        "forward_gear_ratios", "reverse_gear_ratios", "change_up_rpm",
        "change_down_rpm", "transmission_efficiency", "mass", "drag_coefficient",
        "center_of_mass", "chassis_width", "chassis_height",
        "downforce_coefficient", "drag_area", "inertia_tensor_scale",
        "sleep_threshold", "sleep_slope_limit", "steering_curve", "wheels",
        "use_sweep_wheel_collision",
    )
    def __init__(self, cs):
        # Cache the original C# lists so we hand them back unchanged on apply.
        for f in self._FIELDS:
            setattr(self, f, getattr(cs, f))
    def _to_cs(self):
        from CarlaNet.Types.Rpc.Physics import VehiclePhysicsControl as _CSVPC
        return _CSVPC(*[getattr(self, f) for f in self._FIELDS])
from CarlaNet.Types.Rpc.Environment import EpisodeSettings, WeatherParameters
from CarlaNet.Types.Rpc.Enums import (TrafficLightState, MapLayer,
                                       AttachmentType, VehicleDoor,
                                       ActorAttributeType)
from CarlaNet.Types.Rpc.Lighting import VehicleLightStateFlags
from CarlaNet.Types.Rpc import Color as _CSColor
from CarlaNet.Types.Rpc.Debug import (
    PointPrimitive, LinePrimitive, ArrowPrimitive,
    BoxPrimitive, StringPrimitive, DebugShape)
from CarlaNet.Types.Rpc.Commands import (
    Command, SpawnActorCommand, DestroyActorCommand, SetAutopilotCommand,
    ApplyVehicleControlCommand, ApplyTransformCommand, ApplyLocationCommand,
    ConsoleCommandCommand, SetVehicleLightStateCommand, CommandResponse)
# Wave 4 integration: in-process TrafficManager. The import is guarded
# because the Map / TrafficManager assemblies may be missing in stripped-down
# deployments — get_trafficmanager() falls back to a no-op stub if so.
if _CARLANET_TM_AVAILABLE:
    try:
        from CarlaNet.TrafficManager import TrafficManager as _CSTrafficManager
    except Exception:
        _CARLANET_TM_AVAILABLE = False
        _CSTrafficManager = None  # type: ignore
else:
    _CSTrafficManager = None  # type: ignore
from System import TimeSpan
from System.Collections.Generic import List
import struct
import math as _math


def _sync(task):
    """Block on a .NET Task and return its result."""
    return task.GetAwaiter().GetResult()


def _cs_list(items, cs_type=None):
    """Convert a Python iterable to a C# List.  If cs_type is None, uses object."""
    from System.Collections.Generic import List as _List
    from System import Object
    t = cs_type if cs_type is not None else Object
    try:
        result = _List[t]()
        for item in items:
            result.Add(item)
        return result
    except Exception:
        # Fallback: return Python list (pythonnet can often coerce it)
        return list(items)


# ── Attribute value wrapper ───────────────────────────────────────────────────

class _Attribute:
    def __init__(self, name, info):
        self.id = name
        self._info = info

    @property
    def type(self):
        return self._info['type']

    @property
    def recommended_values(self):
        return self._info['recommended']

    def as_color(self):
        parts = self._info['value'].split(',')
        return tuple(int(p) for p in parts)

    def __str__(self):
        return self._info['value']

    def __repr__(self):
        return f"Attribute(id={self.id!r}, value={self._info['value']!r})"


# ── Actor blueprint (wraps ActorDefinition with mutable attributes) ───────────

class ActorBlueprint:
    def __init__(self, definition):
        self._def  = definition
        self._uid  = int(definition.Uid)
        self._id   = str(definition.Id)
        self._tags = str(definition.Tags)
        # Cache attributes as a Python dict for fast modification
        self._attrs = {}
        for i in range(definition.Attributes.Count):
            a = definition.Attributes[i]
            self._attrs[str(a.Id)] = {
                'type':        int(a.Type),
                'value':       str(a.Value),
                'recommended': [str(v) for v in a.RecommendedValues],
                'modifiable':  bool(a.IsModifiable),
            }

    @property
    def id(self):
        return self._id

    @property
    def tags(self):
        return self._tags

    def has_attribute(self, name: str) -> bool:
        return name in self._attrs

    def get_attribute(self, name: str) -> _Attribute:
        if name not in self._attrs:
            raise KeyError(f"Blueprint '{self._id}' has no attribute '{name}'")
        return _Attribute(name, self._attrs[name])

    def set_attribute(self, name: str, value: str):
        if name not in self._attrs:
            raise KeyError(f"Blueprint '{self._id}' has no attribute '{name}'")
        self._attrs[name]['value'] = str(value)

    def has_tag(self, tag: str) -> bool:
        return tag in self._tags.split(',')

    def to_description(self) -> ActorDescription:
        attrs = _cs_list(
            [ActorAttributeValue(k, ActorAttributeType(v['type']), v['value'])
             for k, v in self._attrs.items()],
            ActorAttributeValue)
        return ActorDescription(self._uid, self._id, attrs)

    def __repr__(self):
        return f"ActorBlueprint(id={self._id!r})"


# ── Blueprint library (wraps IReadOnlyList[ActorDefinition]) ─────────────────

class BlueprintLibrary:
    def __init__(self, definitions):
        self._bps = [ActorBlueprint(definitions[i])
                     for i in range(definitions.Count)]

    def filter(self, pattern: str):
        """Return a new BlueprintLibrary containing blueprints matching glob pattern."""
        matched = [bp for bp in self._bps
                   if fnmatch.fnmatch(bp.id, pattern)]
        lib = BlueprintLibrary.__new__(BlueprintLibrary)
        lib._bps = matched
        return lib

    def find(self, id: str) -> ActorBlueprint:
        for bp in self._bps:
            if bp.id == id:
                return bp
        raise KeyError(f"Blueprint '{id}' not found")

    def __iter__(self):
        return iter(self._bps)

    def __len__(self):
        return len(self._bps)

    def __getitem__(self, idx):
        return self._bps[idx]

    def __repr__(self):
        return f"BlueprintLibrary({len(self._bps)} blueprints)"


# ── Map wrapper ───────────────────────────────────────────────────────────────

class Map:
    def __init__(self, name: str, spawn_points):
        self.name = name
        self._spawn_points = [spawn_points[i]
                               for i in range(spawn_points.Count)]

    def get_spawn_points(self):
        return list(self._spawn_points)

    def __repr__(self):
        return f"Map(name={self.name!r}, spawn_points={len(self._spawn_points)})"


# ── Actor wrapper ─────────────────────────────────────────────────────────────

class Actor:
    """Python wrapper around a CARLA actor. Backed by world observer cache."""

    def __init__(self, cs_actor, client):
        self._actor  = cs_actor
        self._client = client   # _CarlaClient (C# object)
        self._sub    = None     # sensor stream subscription

    @property
    def id(self) -> int:
        return int(self._actor.Id)

    @property
    def type_id(self) -> str:
        return str(self._actor.Description.Id)

    @property
    def bounding_box(self) -> BoundingBox:
        return self._actor.BoundingBox

    @property
    def attributes(self) -> dict:
        out = {}
        attrs = self._actor.Description.Attributes
        for i in range(attrs.Count):
            a = attrs[i]
            out[str(a.Id)] = str(a.Value)
        return out

    # ── State queries (from world observer cache) ─────────────────────────────

    def get_transform(self) -> Transform:
        return self._client.GetActorTransform(self._actor.Id)

    def get_location(self) -> Location:
        return self.get_transform().Location

    def get_velocity(self) -> Vector3D:
        return self._client.GetActorVelocity(self._actor.Id)

    def get_angular_velocity(self) -> Vector3D:
        return self._client.GetActorAngularVelocity(self._actor.Id)

    def get_acceleration(self) -> Vector3D:
        return self._client.GetActorAcceleration(self._actor.Id)

    def get_control(self):
        cs = self._client.GetVehicleControl(self._actor.Id)
        # Return a mutable Python wrapper — upstream callers freely mutate this.
        return VehicleControl(cs.Throttle, cs.Steer, cs.Brake, cs.HandBrake,
                              cs.Reverse, cs.ManualGearShift, cs.Gear)

    def get_world(self):
        return World(self._client)

    # ── Commands ──────────────────────────────────────────────────────────────

    def set_transform(self, transform: Transform):
        _sync(self._client.SetActorTransformAsync(self._actor.Id, transform))

    def set_location(self, location: Location):
        _sync(self._client.SetActorLocationAsync(self._actor.Id, location))

    def apply_control(self, control):
        """Apply a control to a vehicle (VehicleControl) or walker (WalkerControl)."""
        cs = _control_to_cs(control)
        if isinstance(control, WalkerControl) or isinstance(cs, _CSWalkerControl):
            _sync(self._client.ApplyControlToWalkerAsync(self._actor.Id, cs))
        else:
            _sync(self._client.ApplyControlToVehicleAsync(self._actor.Id, cs))

    def apply_ackermann_control(self, control):
        _sync(self._client.ApplyAckermannControlToVehicleAsync(self._actor.Id,
                                                               _control_to_cs(control)))

    def set_autopilot(self, enabled: bool, tm_port: int = 8000):
        _sync(self._client.SetActorAutopilotAsync(self._actor.Id, enabled))
        # Register/unregister with the in-process TM so its worker picks
        # up the vehicle. Server-side autopilot flag alone won't drive it.
        Client._tm_register_ids([int(self._actor.Id)], int(tm_port), bool(enabled))

    def set_light_state(self, state):
        # Accept Python int / VehicleLightState wrapper / C# VehicleLightStateFlags
        flags = VehicleLightStateFlags(int(state)) if not isinstance(state, VehicleLightStateFlags) else state
        _sync(self._client.SetVehicleLightStateAsync(self._actor.Id, flags))

    def get_light_state(self):
        flags = _sync(self._client.GetVehicleLightStateAsync(self._actor.Id))
        return VehicleLightState(int(flags))

    def set_simulate_physics(self, enabled: bool):
        _sync(self._client.SetActorSimulatePhysicsAsync(self._actor.Id, enabled))

    def set_enable_gravity(self, enabled: bool):
        _sync(self._client.SetActorEnableGravityAsync(self._actor.Id, enabled))

    def get_physics_control(self):
        cs = _sync(self._client.GetVehiclePhysicsControlAsync(self._actor.Id))
        return _PhysicsControlWrapper(cs)

    def apply_physics_control(self, ctrl):
        cs = ctrl._to_cs() if hasattr(ctrl, "_to_cs") else ctrl
        _sync(self._client.ApplyPhysicsControlToVehicleAsync(self._actor.Id, cs))

    def enable_constant_velocity(self, velocity: Vector3D):
        _sync(self._client.EnableActorConstantVelocityAsync(self._actor.Id, velocity))

    def disable_constant_velocity(self):
        _sync(self._client.DisableActorConstantVelocityAsync(self._actor.Id))

    def show_debug_telemetry(self, enabled: bool):
        _sync(self._client.ShowVehicleDebugTelemetryAsync(self._actor.Id, enabled))

    def open_door(self, door):
        _sync(self._client.OpenVehicleDoorAsync(self._actor.Id, door))

    def close_door(self, door):
        _sync(self._client.CloseVehicleDoorAsync(self._actor.Id, door))

    # ── Sensor streaming ──────────────────────────────────────────────────────

    def listen(self, callback):
        """Subscribe to this sensor's data stream.

        The callback receives a high-level SensorData wrapper (Image,
        CollisionEvent, RadarMeasurement, ...) matched on this actor's type_id.
        For unknown sensor types, the raw SensorFrame is passed through.
        """
        token = self._actor.StreamToken
        if len(token) != 24:
            raise RuntimeError(f"Actor {self.id} ({self.type_id}) has no sensor stream")
        from System import Action
        from CarlaNet.Transport.Streaming import SensorFrame as _SF

        type_id = self.type_id
        parser = _SENSOR_PARSERS.get(type_id)
        actor_proxy = self
        if parser is None:
            cs_cb = Action[_SF](callback)
        else:
            def _dispatch(sf):
                try:
                    data = parser(sf, actor_proxy)
                    callback(data)
                except Exception:
                    import traceback
                    traceback.print_exc()
            cs_cb = Action[_SF](_dispatch)
        self._sub = self._client.SubscribeToStream(token, cs_cb)

    def stop(self):
        """Unsubscribe from sensor stream."""
        if self._sub is not None:
            self._sub.Dispose()
            self._sub = None

    def is_listening(self) -> bool:
        return self._sub is not None

    def destroy(self) -> bool:
        self.stop()
        return bool(_sync(self._client.DestroyActorAsync(self._actor.Id)))

    def __repr__(self):
        return f"Actor(id={self.id}, type={self.type_id!r})"


class Vehicle(Actor):
    """Marker subclass for `isinstance(actor, carla.Vehicle)` checks.
    All methods live on the base Actor; the subclass exists for type identity."""
    pass


class Walker(Actor):
    """Marker subclass for `isinstance(actor, carla.Walker)` checks."""
    pass


class WalkerAIController(Actor):
    """Marker subclass for walker.controller.ai.* actors."""
    pass


class Sensor(Actor):
    """Marker subclass for sensor.* actors. listen/stop/is_listening live on Actor."""
    pass


class TrafficSign(Actor):
    """Marker subclass for traffic.* actors (excluding traffic_light)."""
    pass


class TrafficLight(TrafficSign):
    """Traffic-light subclass for isinstance + state queries."""
    pass


def _actor_cls_for(type_id: str):
    if type_id.startswith("vehicle."):                       return Vehicle
    if type_id.startswith("walker.pedestrian"):              return Walker
    if type_id.startswith("walker.controller"):              return WalkerAIController
    if type_id.startswith("sensor."):                        return Sensor
    if type_id == "traffic.traffic_light":                   return TrafficLight
    if type_id.startswith("traffic."):                       return TrafficSign
    return Actor


def _wrap_actor(cs_actor, client) -> Actor:
    type_id = str(cs_actor.Description.Id)
    cls = _actor_cls_for(type_id)
    return cls(cs_actor, client)


def _wrap_actors(cs_list, client):
    return [_wrap_actor(cs_list[i], client) for i in range(cs_list.Count)]


# ── ActorList wrapper — Pythonnet returns a Python list from get_actors,
#    but upstream offers filter()/find() and iteration; emulate that. ──────────

class ActorList:
    """Wraps the result of World.get_actors().  Supports filter, find, iter, len, getitem."""

    def __init__(self, actors):
        self._actors = list(actors)

    def filter(self, pattern: str):
        return ActorList([a for a in self._actors
                          if fnmatch.fnmatch(a.type_id, pattern)])

    def find(self, actor_id: int):
        for a in self._actors:
            if a.id == int(actor_id):
                return a
        raise KeyError(f"Actor id {actor_id} not in list")

    def __iter__(self):     return iter(self._actors)
    def __len__(self):      return len(self._actors)
    def __getitem__(self, i): return self._actors[i]
    def __repr__(self):     return f"ActorList({len(self._actors)} actors)"


# ── command module ────────────────────────────────────────────────────────────

class command:
    """Namespace matching carla.command.* for apply_batch use."""

    FutureActor = 0  # sentinel: resolved to just-spawned actor id by server

    class Response:
        def __init__(self, cs_resp: CommandResponse):
            self.error    = str(cs_resp.Error) if cs_resp.HasError else None
            self.actor_id = int(cs_resp.ActorId)

        @property
        def has_error(self) -> bool:
            return self.error is not None

        def __repr__(self):
            if self.error:
                return f"Response(error={self.error!r})"
            return f"Response(actor_id={self.actor_id})"

    class _Cmd:
        def to_cs(self) -> Command:
            raise NotImplementedError

    class SpawnActor(_Cmd):
        def __init__(self, blueprint, transform, parent=None):
            self._bp        = blueprint
            self._transform = transform
            self._parent    = int(parent) if parent is not None and parent != command.FutureActor else None
            self._do_after  = []

        def then(self, *cmds):
            out = command.SpawnActor(self._bp, self._transform)
            out._parent   = self._parent
            out._do_after = list(self._do_after) + list(cmds)
            return out

        def to_cs(self) -> Command:
            desc = (self._bp.to_description()
                    if isinstance(self._bp, ActorBlueprint)
                    else self._bp)
            parent_id = self._parent  # None → optional not set
            do_after_cs = _cs_list([c.to_cs() for c in self._do_after], Command)
            return SpawnActorCommand(desc, self._transform, parent_id, do_after_cs)

    class DestroyActor(_Cmd):
        def __init__(self, actor):
            self._id = int(actor.id) if isinstance(actor, Actor) else int(actor)

        def to_cs(self) -> Command:
            return DestroyActorCommand(self._id)

    class SetAutopilot(_Cmd):
        def __init__(self, actor, enabled: bool, tm_port: int = 8000):
            self._id      = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._enabled = bool(enabled)
            self._tm_port = int(tm_port)

        def to_cs(self) -> Command:
            return SetAutopilotCommand(self._id, self._enabled)

    class ApplyVehicleControl(_Cmd):
        def __init__(self, actor, control: VehicleControl):
            self._id   = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._ctrl = control

        def to_cs(self) -> Command:
            return ApplyVehicleControlCommand(self._id, _control_to_cs(self._ctrl))

    class ApplyTransform(_Cmd):
        def __init__(self, actor, transform: Transform):
            self._id = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._tf = transform

        def to_cs(self) -> Command:
            return ApplyTransformCommand(self._id, self._tf)

    class ApplyLocation(_Cmd):
        def __init__(self, actor, location: Location):
            self._id  = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._loc = location

        def to_cs(self) -> Command:
            return ApplyLocationCommand(self._id, self._loc)

    class SetVehicleLightState(_Cmd):
        def __init__(self, actor, light_state):
            self._id    = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._state = light_state

        def to_cs(self) -> Command:
            return SetVehicleLightStateCommand(self._id, self._state)

    class ConsoleCommand(_Cmd):
        def __init__(self, cmd_str: str):
            self._cmd = cmd_str

        def to_cs(self) -> Command:
            return ConsoleCommandCommand(self._cmd)


def _cmds_to_cs(cmds):
    cs = _cs_list([c.to_cs() if isinstance(c, command._Cmd) else c for c in cmds], Command)
    return cs


# ── Traffic Manager ───────────────────────────────────────────────────────────

class TrafficManager:
    """Wrapper around the in-process C# CarlaNet.TrafficManager.TrafficManager
    facade. Mirrors the upstream `carla.TrafficManager` Python API (snake_case
    method names).

    Construction is synchronous and fairly heavy (~300-500 ms) because it
    fetches the OpenDRIVE XML from the server and builds the dense waypoint
    graph. Subsequent constructions reusing the same map name are cheap
    (the C# side caches the parsed Map + InMemoryMap).
    """

    def __init__(self, tm, port: int = 8000):
        # `tm` is a CarlaNet.TrafficManager.TrafficManager instance.
        self._tm   = tm
        self._port = int(getattr(tm, "Port", port))

    def get_port(self) -> int:
        return self._port

    def start(self):
        self._tm.Start()

    def set_percentage_speed_difference(self, actor: Actor, pct: float):
        self._tm.SetPercentageSpeedDifference(actor._actor, float(pct))

    def set_desired_speed(self, actor: Actor, speed: float):
        self._tm.SetDesiredSpeed(actor._actor, float(speed))

    def set_global_percentage_speed_difference(self, pct: float):
        self._tm.SetGlobalPercentageSpeedDifference(float(pct))

    def set_global_distance_to_leading_vehicle(self, distance: float):
        self._tm.SetGlobalDistanceToLeadingVehicle(float(distance))

    def set_distance_to_leading_vehicle(self, actor: Actor, distance: float):
        self._tm.SetDistanceToLeadingVehicle(actor._actor, float(distance))

    def set_collision_detection(self, reference: Actor, other: Actor, detect: bool):
        self._tm.SetCollisionDetection(reference._actor, other._actor, bool(detect))

    def set_lane_offset(self, actor: Actor, offset: float):
        self._tm.SetLaneOffset(actor._actor, float(offset))

    def set_global_lane_offset(self, offset: float):
        self._tm.SetGlobalLaneOffset(float(offset))

    def set_auto_lane_change(self, actor: Actor, enable: bool):
        self._tm.SetAutoLaneChange(actor._actor, bool(enable))

    def set_force_lane_change(self, actor: Actor, to_left: bool):
        self._tm.SetForceLaneChange(actor._actor, bool(to_left))

    def set_percentage_ignore_walkers(self, actor: Actor, pct: float):
        self._tm.SetPercentageIgnoreWalkers(actor._actor, float(pct))

    def set_percentage_ignore_vehicles(self, actor: Actor, pct: float):
        self._tm.SetPercentageIgnoreVehicles(actor._actor, float(pct))

    def set_percentage_running_light(self, actor: Actor, pct: float):
        self._tm.SetPercentageRunningLight(actor._actor, float(pct))

    def set_percentage_running_sign(self, actor: Actor, pct: float):
        self._tm.SetPercentageRunningSign(actor._actor, float(pct))

    def set_percentage_keep_slow_lane_rule(self, actor: Actor, pct: float):
        self._tm.SetKeepSlowLanePercentage(actor._actor, float(pct))

    def set_percentage_random_left_lanechange(self, actor: Actor, pct: float):
        self._tm.SetRandomLeftLaneChangePercentage(actor._actor, float(pct))

    def set_percentage_random_right_lanechange(self, actor: Actor, pct: float):
        self._tm.SetRandomRightLaneChangePercentage(actor._actor, float(pct))

    def set_hybrid_physics_mode(self, enabled: bool):
        self._tm.SetHybridPhysicsMode(bool(enabled))

    def set_hybrid_physics_radius(self, radius: float):
        self._tm.SetHybridPhysicsRadius(float(radius))

    def set_random_device_seed(self, seed: int):
        self._tm.SetRandomDeviceSeed(int(seed))

    def set_respawn_dormant_vehicles(self, enabled: bool):
        self._tm.SetRespawnDormantVehicles(bool(enabled))

    def set_boundaries_respawn_dormant_vehicles(self, lower: float, upper: float):
        self._tm.SetBoundariesRespawnDormantVehicles(float(lower), float(upper))

    def set_synchronous_mode(self, enabled: bool):
        self._tm.SetSynchronousMode(bool(enabled))

    def set_synchronous_mode_timeout_in_milisecond(self, ms: float):
        self._tm.SetSynchronousModeTimeOutInMiliSecond(float(ms))

    def set_osm_mode(self, enabled: bool):
        self._tm.SetOsmMode(bool(enabled))

    def update_vehicle_lights(self, actor: Actor, enabled: bool):
        self._tm.SetUpdateVehicleLights(actor._actor, bool(enabled))

    def global_percentage_speed_difference(self, pct: float):
        self._tm.SetGlobalPercentageSpeedDifference(float(pct))

    def synchronous_tick(self) -> bool:
        return bool(self._tm.SynchronousTick())

    def shut_down(self):
        self._tm.ShutDown()


class _NoOpTrafficManager:
    """Returned by Client.get_trafficmanager() when no TM server is running.

    Lets scripts that only use the TM for ambient AI continue without crashing.
    Every method is a no-op; getters return sensible defaults.
    """
    def __init__(self, port: int = 8000):
        self._port = port
    def get_port(self) -> int: return self._port
    def __getattr__(self, name):
        # Any unknown method becomes a callable no-op.
        return lambda *a, **kw: None


# ── World ─────────────────────────────────────────────────────────────────────

class World:
    def __init__(self, client):
        self._client = client  # _CarlaClient (C# object)

    @property
    def debug(self):
        return DebugHelper(self._client)

    def get_settings(self):
        cs = _sync(self._client.GetEpisodeSettingsAsync())
        return _WorldSettings(cs)

    def apply_settings(self, settings) -> int:
        cs = settings._to_cs() if isinstance(settings, _WorldSettings) else settings
        return int(_sync(self._client.SetEpisodeSettingsAsync(cs)))

    def get_map(self) -> Map:
        info = _sync(self._client.GetMapInfoAsync())
        return Map(str(info.Name), info.RecommendedSpawnPoints)

    def get_spectator(self) -> Actor:
        return _wrap_actor(_sync(self._client.GetSpectatorAsync()), self._client)

    def get_actors(self, actor_ids=None):
        from System import UInt32
        if actor_ids is None:
            # Get all cached actor IDs from the world observer
            cached = self._client.GetCachedActorIds()
            if cached.Count == 0:
                return ActorList([])
            ids = _cs_list([int(cached[i]) for i in range(cached.Count)], UInt32)
        else:
            ids = _cs_list([int(i) for i in actor_ids], UInt32)
        cs_actors = _sync(self._client.GetActorsByIdAsync(ids))
        return ActorList(_wrap_actors(cs_actors, self._client))

    def get_blueprint_library(self) -> BlueprintLibrary:
        defs = _sync(self._client.GetActorDefinitionsAsync())
        return BlueprintLibrary(defs)

    def spawn_actor(self, blueprint, transform: Transform,
                    attach_to=None, attachment_type=None) -> Actor:
        desc = (blueprint.to_description()
                if isinstance(blueprint, ActorBlueprint) else blueprint)
        if attach_to is not None:
            parent_id = int(attach_to.id) if isinstance(attach_to, Actor) else int(attach_to)
            at = attachment_type if attachment_type is not None else AttachmentType.Rigid
            cs_actor = _sync(self._client.SpawnActorWithParentAsync(desc, transform, parent_id, at))
        else:
            cs_actor = _sync(self._client.SpawnActorAsync(desc, transform))
        return _wrap_actor(cs_actor, self._client)

    def try_spawn_actor(self, blueprint, transform: Transform,
                        attach_to=None, attachment_type=None):
        try:
            return self.spawn_actor(blueprint, transform, attach_to, attachment_type)
        except Exception:
            return None

    def destroy_actor(self, actor) -> bool:
        actor_id = int(actor.id) if isinstance(actor, Actor) else int(actor)
        return bool(_sync(self._client.DestroyActorAsync(actor_id)))

    def tick(self) -> int:
        return int(_sync(self._client.SendTickCueAsync()))

    def get_weather(self) -> WeatherParameters:
        return _sync(self._client.GetWeatherParametersAsync())

    def set_weather(self, weather: WeatherParameters):
        _sync(self._client.SetWeatherParametersAsync(weather))

    def set_pedestrians_seed(self, seed: int):
        # Not a direct RPC; uses console command to set navigation seed
        pass  # Best-effort; server handles this internally in newer builds

    def set_pedestrians_cross_factor(self, percentage: float):
        pass  # Best-effort

    def get_random_location_from_navigation(self):
        # Returns None if not available; caller should handle None
        return None

    def load_map_layer(self, layer: MapLayer):
        _sync(self._client.LoadLevelLayerAsync(layer))

    def unload_map_layer(self, layer: MapLayer):
        _sync(self._client.UnloadLevelLayerAsync(layer))

    def get_available_maps(self):
        return list(_sync(self._client.GetAvailableMapsAsync()))

    def reload_world(self, reset_settings: bool = True):
        # Reload current map
        mi = _sync(self._client.GetMapInfoAsync())
        map_name = str(mi.Name)
        _sync(self._client.LoadEpisodeAsync(map_name, reset_settings))
        return World(self._client)

    def on_tick(self, callback):
        """Register a callback fired once per world tick.

        Calls `callback(Timestamp)` where Timestamp has `.frame`,
        `.elapsed_seconds`, `.delta_seconds`, `.platform_timestamp` attributes.
        Returns an opaque id usable with `remove_on_tick`.

        Requires the world observer subscription to be active (auto-started by
        Client.get_world()).
        """
        from System import Action
        from CarlaNet.Transport import TickTimestamp as _CSTickTs

        def _handler(cs_ts):
            try:
                callback(Timestamp(
                    int(cs_ts.Frame),
                    float(cs_ts.ElapsedSeconds),
                    float(cs_ts.DeltaSeconds),
                    float(cs_ts.PlatformTimestamp)))
            except Exception:
                import traceback
                traceback.print_exc()

        cs_action = Action[_CSTickTs](_handler)
        # Use the C# event API — pythonnet maps += to add_OnTick.
        self._client.add_OnTick(cs_action)
        # Track for remove_on_tick. Use id() of cs_action — but pythonnet may
        # produce a different wrapper each call, so we cache the delegate too.
        if not hasattr(self, "_tick_callbacks"):
            self._tick_callbacks = {}
        key = id(cs_action)
        self._tick_callbacks[key] = cs_action
        return key

    def remove_on_tick(self, callback_id):
        if not hasattr(self, "_tick_callbacks"):
            return
        cs_action = self._tick_callbacks.pop(callback_id, None)
        if cs_action is not None:
            try:
                self._client.remove_OnTick(cs_action)
            except Exception:
                pass

    def wait_for_tick(self, seconds: float = 10.0):
        """Block until the next world-observer tick fires (or `seconds` elapse).

        Falls back to a brief sleep if the observer isn't running.
        """
        try:
            ev = threading.Event()
            holder = {}
            cid = self.on_tick(lambda ts: ev.set())
            holder['cid'] = cid
            if ev.wait(timeout=seconds):
                # Build a synthetic Timestamp from the latest observer state
                return Timestamp(0, 0.0, 0.0, time.time())
        finally:
            try:
                if 'cid' in holder:
                    self.remove_on_tick(holder['cid'])
            except Exception:
                pass
        time.sleep(0.05)
        return Timestamp(0, 0.0, 0.0, time.time())

    def __repr__(self):
        return f"World(client={self._client})"


# ── Client ────────────────────────────────────────────────────────────────────

class Client:
    def __init__(self, host: str, port: int = 2000, worker_threads: int = 2):
        self._host    = host
        self._port    = port
        self._timeout = TimeSpan.FromMilliseconds(5000)
        self._inner   = _CarlaClient(host, port, self._timeout)
        # World observer is opt-in. Auto-starting it from __init__ races with
        # the first RPC calls and can cause them to time out. Call
        # start_observer() explicitly if you need actor-transform caching.
        self._observer_started = False

    def start_observer(self):
        """Subscribe to the world observer stream so actor transforms are cached."""
        if self._observer_started:
            return
        _sync(self._inner.StartWorldObserverAsync())
        self._observer_started = True

    def set_timeout(self, timeout_s: float):
        """Update the RPC timeout in seconds (matches upstream Client.set_timeout)."""
        self._timeout = TimeSpan.FromMilliseconds(int(timeout_s * 1000))
        # Propagate to the C# MsgPackRpcClient via CarlaClient.SetTimeout
        self._inner.SetTimeout(self._timeout)

    def get_client_version(self) -> str:
        return self._inner.GetClientVersion()

    def get_server_version(self) -> str:
        return str(_sync(self._inner.GetServerVersionAsync()))

    def get_world(self) -> World:
        # Auto-start the world observer stream so that actor transforms,
        # velocities, on_tick events, and wait_for_tick all function out
        # of the box (per spec §11.3).
        if not self._observer_started:
            try:
                self.start_observer()
            except Exception:
                # Don't block get_world if observer subscription fails.
                pass
        return World(self._inner)

    def reload_world(self, reset_settings: bool = True) -> World:
        return self.get_world().reload_world(reset_settings)

    def get_available_maps(self):
        return list(_sync(self._inner.GetAvailableMapsAsync()))

    def load_world(self, map_name: str, reset_settings: bool = True) -> World:
        _sync(self._inner.LoadEpisodeAsync(map_name, reset_settings))
        return World(self._inner)

    def get_trafficmanager(self, port: int = 8000):
        """Return an in-process TrafficManager bound to the given port.

        Behaviour:
          1. If a process-wide instance already exists for this port, reuse it.
          2. Otherwise instantiate a new C# CarlaNet.TrafficManager.TrafficManager
             (fetches OpenDRIVE XML, builds InMemoryMap, starts RPC server +
             worker thread). Construction takes ~300-500 ms on first call
             against a given map.
          3. On any failure (assemblies missing, server not responding, map
             parse failure) fall back to _NoOpTrafficManager so ambient-AI-only
             scripts keep working with a warning.
        """
        port = int(port)
        # Process-wide cache so repeat calls return the same instance.
        cache = getattr(Client, "_tm_cache", None)
        if cache is None:
            cache = {}
            Client._tm_cache = cache
        cached = cache.get(port)
        if cached is not None:
            return cached

        if not _CARLANET_TM_AVAILABLE or _CSTrafficManager is None:
            import sys
            print(f"[carlanet] CarlaNet.TrafficManager assemblies unavailable; "
                  f"using no-op stub on port {port}.", file=sys.stderr)
            stub = _NoOpTrafficManager(port)
            cache[port] = stub
            return stub

        # Ensure the world observer is running so the TM's ALSM can pull
        # cached actor IDs without bootstrapping its own subscription.
        if not self._observer_started:
            try: self.start_observer()
            except Exception: pass

        try:
            cs_tm = _CSTrafficManager(self._inner, port)
            cs_tm.Start()
            tm = TrafficManager(cs_tm, port=port)
            cache[port] = tm
            return tm
        except Exception as ex:
            import sys, traceback
            print(f"[carlanet] traffic manager construction failed on port "
                  f"{port} ({type(ex).__name__}: {ex}); using no-op stub.",
                  file=sys.stderr)
            traceback.print_exc()
            stub = _NoOpTrafficManager(port)
            cache[port] = stub
            return stub

    def apply_batch(self, cmds, do_tick_cue: bool = False):
        cs_cmds = _cmds_to_cs(cmds)
        _sync(self._inner.ApplyBatchAsync(cs_cmds, do_tick_cue))
        # Best-effort registration for standalone SetAutopilot commands; can't
        # handle SpawnActor.then(SetAutopilot) without responses.
        Client._apply_tm_registration_from_batch(cmds, responses=None)

    def apply_batch_sync(self, cmds, do_tick_cue: bool = False):
        cs_cmds = _cmds_to_cs(cmds)
        cs_resp = _sync(self._inner.ApplyBatchSyncAsync(cs_cmds, do_tick_cue))
        responses = [command.Response(cs_resp[i]) for i in range(cs_resp.Count)]
        Client._apply_tm_registration_from_batch(cmds, responses)
        return responses

    # ── TM registration helpers ───────────────────────────────────────────────
    @staticmethod
    def _tm_register_ids(actor_ids, tm_port: int, enabled: bool):
        """Register/unregister actor IDs with the cached TM on tm_port (no-op if none)."""
        cache = getattr(Client, "_tm_cache", None)
        if not cache:
            return
        tm = cache.get(int(tm_port))
        if tm is None:
            return
        cs_tm = getattr(tm, "_tm", None)
        if cs_tm is None:
            return
        from System import UInt32
        from System.Collections.Generic import List as _List
        cs_ids = _List[UInt32]()
        for i in actor_ids:
            cs_ids.Add(UInt32(int(i)))
        try:
            if enabled:
                cs_tm.RegisterVehicleIds(cs_ids)
            else:
                cs_tm.UnregisterVehicleIds(cs_ids)
        except Exception as ex:
            import sys
            print(f"[carlanet] TM register failed (port {tm_port}): {ex}", file=sys.stderr)

    @staticmethod
    def _apply_tm_registration_from_batch(cmds, responses):
        """Inspect a batch (and optional responses) and register spawned vehicles with TMs."""
        # Group (tm_port, enabled) -> [actor_ids]
        groups: dict = {}
        for i, c in enumerate(cmds):
            if not isinstance(c, command._Cmd):
                continue
            if isinstance(c, command.SpawnActor):
                if responses is None:
                    continue  # can't resolve FutureActor → real ID without responses
                if i >= len(responses):
                    continue
                resp = responses[i]
                if resp.error is not None:
                    continue
                spawned_id = resp.actor_id
                for after in c._do_after:
                    if isinstance(after, command.SetAutopilot):
                        key = (after._tm_port, after._enabled)
                        groups.setdefault(key, []).append(int(spawned_id))
            elif isinstance(c, command.SetAutopilot):
                key = (c._tm_port, c._enabled)
                groups.setdefault(key, []).append(c._id)
        for (tm_port, enabled), ids in groups.items():
            Client._tm_register_ids(ids, tm_port, enabled)

    def start_recorder(self, name: str, additional_data: bool = False) -> str:
        return str(_sync(self._inner.StartRecorderAsync(name, additional_data)))

    def stop_recorder(self):
        _sync(self._inner.StopRecorderAsync())

    def show_recorder_file_info(self, name: str, show_all: bool = False) -> str:
        return str(_sync(self._inner.ShowRecorderFileInfoAsync(name, show_all)))

    def replay_file(self, name: str, start: float = 0.0, duration: float = 0.0,
                    follow_id: int = 0, replay_sensors: bool = False) -> str:
        return str(_sync(self._inner.ReplayFileAsync(
            name, start, duration, follow_id, replay_sensors)))

    def stop_replayer(self, keep_actors: bool = False):
        _sync(self._inner.StopReplayerAsync(keep_actors))

    def set_replayer_time_factor(self, factor: float):
        _sync(self._inner.SetReplayerTimeFactorAsync(factor))

    def set_replayer_ignore_hero(self, ignore: bool):
        _sync(self._inner.SetReplayerIgnoreHeroAsync(ignore))

    def __del__(self):
        try:
            self._inner.DisposeAsync().GetAwaiter().GetResult()
        except Exception:
            pass

    def __repr__(self):
        return f"Client({self._host}:{self._port})"


# ─────────────────────────────────────────────────────────────────────────────
# §1, §4 — VehicleLightState  (Python int subclass with named class attributes)
# ─────────────────────────────────────────────────────────────────────────────

class VehicleLightState(int):
    """Vehicle-light bitmask. Subclasses int so |, &, ^, ~ work naturally.

    Constructible from any int — `carla.VehicleLightState(5)` works.
    Class attributes mirror the boost.python enum_<VehicleLightState> names.
    """
    NONE         = 0
    Position     = 0x1
    LowBeam      = 0x2
    HighBeam     = 0x4
    Brake        = 0x8
    RightBlinker = 0x10
    LeftBlinker  = 0x20
    Reverse      = 0x40
    Fog          = 0x80
    Interior     = 0x100
    Special1     = 0x200
    Special2     = 0x400
    All          = 0xFFFFFFFF


# ─────────────────────────────────────────────────────────────────────────────
# §1 — Color  (lightweight Python wrapper compatible with C# Color)
# ─────────────────────────────────────────────────────────────────────────────

class Color:
    """RGB color tuple.  `carla.Color(r, g, b)` matches upstream signature.

    Coerced to C# Color (byte R, G, B) when handed to a C# API.
    """
    __slots__ = ("r", "g", "b", "a")

    def __init__(self, r: int = 0, g: int = 0, b: int = 0, a: int = 255):
        self.r = int(r) & 0xFF
        self.g = int(g) & 0xFF
        self.b = int(b) & 0xFF
        self.a = int(a) & 0xFF

    def to_cs(self):
        return _CSColor(self.r, self.g, self.b)

    def __repr__(self):
        return f"Color({self.r},{self.g},{self.b},{self.a})"


def _to_cs_color(c):
    """Coerce a Python carla.Color or C# Color to a C# Color."""
    if c is None:
        return _CSColor(255, 0, 0)
    if isinstance(c, _CSColor):
        return c
    if isinstance(c, Color):
        return c.to_cs()
    # Tuple / list fallback
    try:
        return _CSColor(int(c[0]), int(c[1]), int(c[2]))
    except Exception:
        return _CSColor(255, 0, 0)


# ─────────────────────────────────────────────────────────────────────────────
# §1, §11.7 — _WorldSettings  (mutable Python wrapper for EpisodeSettings)
# ─────────────────────────────────────────────────────────────────────────────

class _WorldSettings:
    """Mutable mirror of the C# EpisodeSettings record struct.

    manual_control.py does `settings.synchronous_mode = True`; the C# record is
    immutable, so we cache field values on this wrapper and rebuild on apply.
    """
    __slots__ = ("_fields",)

    def __init__(self, cs):
        # Snapshot all 11 fields. Lowercase names match upstream's Python API.
        self._fields = {
            "synchronous_mode":       bool(cs.SynchronousMode),
            "no_rendering_mode":      bool(cs.NoRenderingMode),
            "fixed_delta_seconds":    float(cs.FixedDeltaSeconds) if cs.FixedDeltaSeconds is not None else None,
            "substepping":            bool(cs.Substepping),
            "max_substep_delta_time": float(cs.MaxSubstepDeltaTime),
            "max_substeps":           int(cs.MaxSubsteps),
            "max_culling_distance":   float(cs.MaxCullingDistance),
            "deterministic_ragdolls": bool(cs.DeterministicRagdolls),
            "tile_stream_distance":   float(cs.TileStreamDistance),
            "actor_active_distance":  float(cs.ActorActiveDistance),
            "spectator_as_ego":       bool(cs.SpectatorAsEgo),
        }

    def __getattr__(self, name):
        if name in object.__getattribute__(self, "_fields"):
            return self._fields[name]
        raise AttributeError(name)

    def __setattr__(self, name, value):
        if name == "_fields":
            object.__setattr__(self, name, value)
        elif name in self._fields:
            self._fields[name] = value
        else:
            raise AttributeError(f"EpisodeSettings has no attribute '{name}'")

    def _to_cs(self):
        f = self._fields
        return EpisodeSettings(
            f["synchronous_mode"], f["no_rendering_mode"],
            f["fixed_delta_seconds"], f["substepping"],
            f["max_substep_delta_time"], f["max_substeps"],
            f["max_culling_distance"], f["deterministic_ragdolls"],
            f["tile_stream_distance"], f["actor_active_distance"],
            f["spectator_as_ego"])

    def __repr__(self):
        return f"WorldSettings({self._fields})"


# ─────────────────────────────────────────────────────────────────────────────
# §1 — Timestamp  (returned by World.on_tick callback)
# ─────────────────────────────────────────────────────────────────────────────

class Timestamp:
    """Per-tick timestamp matching upstream carla.Timestamp."""
    __slots__ = ("frame", "elapsed_seconds", "delta_seconds", "platform_timestamp")

    def __init__(self, frame, elapsed_seconds, delta_seconds, platform_timestamp):
        self.frame = int(frame)
        self.elapsed_seconds = float(elapsed_seconds)
        self.delta_seconds = float(delta_seconds)
        self.platform_timestamp = float(platform_timestamp)

    def __repr__(self):
        return (f"Timestamp(frame={self.frame}, elapsed={self.elapsed_seconds:.3f}, "
                f"delta={self.delta_seconds:.4f})")


# ─────────────────────────────────────────────────────────────────────────────
# §5 — ColorConverter + in-place image conversions
# ─────────────────────────────────────────────────────────────────────────────

class ColorConverter:
    """Pixel-space transforms applied by Image.convert(...).

    Matches upstream carla.ColorConverter enum.
    """
    Raw               = 0
    Depth             = 1
    LogarithmicDepth  = 2
    CityScapesPalette = 3


# 30-entry palette mirroring `OBJECT_TO_COLOR` in manual_control.py lines 125-155
# and upstream `CityScapesPalette.h`.
_CITYSCAPES_PALETTE = (
    (0, 0, 0), (128, 64, 128), (244, 35, 232), (70, 70, 70), (102, 102, 156),
    (190, 153, 153), (153, 153, 153), (250, 170, 30), (220, 220, 0), (107, 142, 35),
    (152, 251, 152), (70, 130, 180), (220, 20, 60), (255, 0, 0), (0, 0, 142),
    (0, 0, 70), (0, 60, 100), (0, 80, 100), (0, 0, 230), (119, 11, 32),
    (110, 190, 160), (170, 120, 50), (55, 90, 80), (45, 60, 150), (157, 234, 50),
    (81, 0, 81), (150, 100, 100), (230, 150, 140), (180, 165, 180), (180, 130, 70),
)


def _convert_depth_inplace(buf):
    """Apply upstream's Depth converter in-place over a BGRA bytearray."""
    try:
        import numpy as np
    except ImportError:
        return
    arr = np.frombuffer(buf, dtype=np.uint8).reshape(-1, 4)
    depth = (arr[:, 2].astype(np.float32)
             + arr[:, 1].astype(np.float32) * 256.0
             + arr[:, 0].astype(np.float32) * 65536.0)
    normalized = depth / (256.0 ** 3 - 1.0)
    gray = (normalized * 255.0).astype(np.uint8)
    arr[:, 0] = gray
    arr[:, 1] = gray
    arr[:, 2] = gray


def _convert_log_depth_inplace(buf):
    try:
        import numpy as np
    except ImportError:
        return
    arr = np.frombuffer(buf, dtype=np.uint8).reshape(-1, 4)
    depth = (arr[:, 2].astype(np.float32)
             + arr[:, 1].astype(np.float32) * 256.0
             + arr[:, 0].astype(np.float32) * 65536.0)
    normalized = depth / (256.0 ** 3 - 1.0)
    normalized = np.maximum(normalized, 1e-10)
    value = 1.0 + np.log(normalized) / 5.70378
    clamped = np.clip(value, 0.005, 1.0)
    gray = (clamped * 255.0).astype(np.uint8)
    arr[:, 0] = gray
    arr[:, 1] = gray
    arr[:, 2] = gray


def _convert_cityscapes_inplace(buf):
    try:
        import numpy as np
    except ImportError:
        return
    arr = np.frombuffer(buf, dtype=np.uint8).reshape(-1, 4)
    palette = np.array(_CITYSCAPES_PALETTE, dtype=np.uint8)
    tags = arr[:, 2] % len(palette)
    rgb = palette[tags]
    arr[:, 0] = rgb[:, 2]   # B
    arr[:, 1] = rgb[:, 1]   # G
    arr[:, 2] = rgb[:, 0]   # R


# ─────────────────────────────────────────────────────────────────────────────
# §2 — SensorData base + concrete wrappers
# ─────────────────────────────────────────────────────────────────────────────

class SensorData:
    """Common base for all parsed sensor data objects.

    Holds the 48-byte SensorHeader fields (frame, timestamp, transform).
    Subclasses parse their specific payload.
    """
    __slots__ = ("frame", "timestamp", "transform")

    def __init__(self, sensor_frame):
        h = sensor_frame.Header
        self.frame = int(h.Frame)
        self.timestamp = float(h.Timestamp)
        self.transform = sensor_frame.SensorTransform


class Image(SensorData):
    """RGB / depth / segmentation / normals camera frame."""

    __slots__ = ("width", "height", "fov", "_raw")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        payload = bytes(sensor_frame.PayloadBytes)
        if len(payload) < 12:
            self.width = 0; self.height = 0; self.fov = 0.0
            self._raw = bytearray()
            return
        w, h, fov = struct.unpack_from("<IIf", payload, 0)
        self.width = int(w)
        self.height = int(h)
        self.fov = float(fov)
        self._raw = bytearray(payload[12:])

    @property
    def raw_data(self):
        # Return bytes (numpy.frombuffer accepts bytes/bytearray/memoryview).
        return bytes(self._raw)

    def convert(self, color_converter):
        cc = int(color_converter)
        if cc == ColorConverter.Raw:
            return
        if cc == ColorConverter.Depth:
            _convert_depth_inplace(self._raw)
        elif cc == ColorConverter.LogarithmicDepth:
            _convert_log_depth_inplace(self._raw)
        elif cc == ColorConverter.CityScapesPalette:
            _convert_cityscapes_inplace(self._raw)

    def save_to_disk(self, path):
        """Write a PNG to `path`. Falls back to raw .bin if PIL is missing."""
        import os
        d = os.path.dirname(path)
        if d:
            os.makedirs(d, exist_ok=True)
        try:
            import numpy as np
            from PIL import Image as _PIL
        except ImportError:
            with open(path + ".bin", "wb") as f:
                f.write(self.raw_data)
            return
        arr = np.frombuffer(self.raw_data, dtype=np.uint8)
        if len(arr) < self.width * self.height * 4:
            return
        arr = arr.reshape((self.height, self.width, 4))
        rgb = arr[:, :, [2, 1, 0]]  # BGRA → RGB
        _PIL.fromarray(rgb).save(path if path.lower().endswith(".png") else path + ".png")

    def __len__(self):
        return self.width * self.height

    def __getitem__(self, i):
        idx = int(i) * 4
        s = bytes(self._raw)
        if idx + 4 > len(s):
            raise IndexError(i)
        return (s[idx], s[idx + 1], s[idx + 2], s[idx + 3])

    def __repr__(self):
        return f"Image(frame={self.frame}, {self.width}x{self.height}, fov={self.fov:.1f})"


class OpticalFlowPixel:
    __slots__ = ("x", "y")
    def __init__(self, x, y):
        self.x = float(x); self.y = float(y)
    def __repr__(self):
        return f"OpticalFlowPixel({self.x:.3f},{self.y:.3f})"


class OpticalFlowImage(SensorData):
    """Optical flow frame — 8 bytes/pixel (float X, float Y)."""

    __slots__ = ("width", "height", "fov", "_raw")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        payload = bytes(sensor_frame.PayloadBytes)
        if len(payload) < 12:
            self.width = 0; self.height = 0; self.fov = 0.0
            self._raw = b""
            return
        w, h, fov = struct.unpack_from("<IIf", payload, 0)
        self.width = int(w)
        self.height = int(h)
        self.fov = float(fov)
        self._raw = payload[12:]

    @property
    def raw_data(self):
        return self._raw

    def get_color_coded_flow(self):
        """HSV-coded flow → returns a fake Image with BGRA raw_data."""
        try:
            import numpy as np
        except ImportError:
            class _Fake:
                width = self.width; height = self.height
                raw_data = self._raw
            return _Fake()
        flow = np.frombuffer(self._raw, dtype=np.float32)
        if flow.size < self.width * self.height * 2:
            class _Empty:
                width = self.width; height = self.height
                raw_data = b""
            return _Empty()
        flow = flow.reshape((self.height, self.width, 2))
        # Magnitude and angle
        mag = np.hypot(flow[..., 0], flow[..., 1])
        ang = np.arctan2(flow[..., 1], flow[..., 0])
        # Map angle [-pi,pi] → hue [0,1], magnitude → value [0,1] (clipped at 1.0)
        h = (ang / (2.0 * _math.pi) + 0.5) % 1.0
        v = np.clip(mag, 0.0, 1.0)
        s = np.ones_like(v)
        # HSV → RGB conversion (vectorised).
        i = (h * 6.0).astype(np.int32) % 6
        f = h * 6.0 - i.astype(np.float32)
        p = v * (1.0 - s)
        q = v * (1.0 - s * f)
        t = v * (1.0 - s * (1.0 - f))
        r = np.where(i == 0, v, np.where(i == 1, q, np.where(i == 2, p,
                np.where(i == 3, p, np.where(i == 4, t, v)))))
        g = np.where(i == 0, t, np.where(i == 1, v, np.where(i == 2, v,
                np.where(i == 3, q, np.where(i == 4, p, p)))))
        b = np.where(i == 0, p, np.where(i == 1, p, np.where(i == 2, t,
                np.where(i == 3, v, np.where(i == 4, v, q)))))
        rgb = np.stack([b, g, r, np.zeros_like(r)], axis=-1)  # BGRA
        rgb = (rgb * 255.0).astype(np.uint8)
        flat = rgb.tobytes()

        flow_w, flow_h = self.width, self.height
        class _FlowImage:
            width = flow_w
            height = flow_h
            raw_data = flat
        return _FlowImage()


class CollisionEvent(SensorData):
    """A single collision event.  Payload is msgpack [self, other, impulse]."""

    __slots__ = ("actor", "other_actor", "normal_impulse")

    def __init__(self, sensor_frame, listener_actor=None):
        super().__init__(sensor_frame)
        # Parse the msgpack payload via the C# CollisionSensorData type.
        try:
            from CarlaNet.Sensors import CollisionSensorData
            from MessagePack import MessagePackSerializer
            data = MessagePackSerializer.Deserialize[CollisionSensorData](
                sensor_frame.PayloadBytes)
            self.actor = _WrappedSensorActor(data.SelfActor, listener_actor._client if listener_actor else None) if listener_actor else None
            self.other_actor = _WrappedSensorActor(data.OtherActor, listener_actor._client if listener_actor else None)
            self.normal_impulse = data.NormalImpulse
        except Exception:
            self.actor = listener_actor
            self.other_actor = _StubActor("unknown")
            self.normal_impulse = Vector3D(0.0, 0.0, 0.0)


class _StubActor:
    """Stand-in for an actor we couldn't fully resolve (no msgpack roundtrip)."""
    def __init__(self, type_id: str = "unknown", actor_id: int = 0):
        self.type_id = type_id
        self.id = actor_id
    def __repr__(self):
        return f"StubActor(type_id={self.type_id!r})"


class _WrappedSensorActor:
    """Light Actor-like wrapper for actors decoded inside a sensor payload."""
    def __init__(self, cs_actor, client):
        self._actor = cs_actor
        self._client = client
        try:
            self.id = int(cs_actor.Id)
        except Exception:
            self.id = 0
        try:
            self.type_id = str(cs_actor.Description.Id)
        except Exception:
            self.type_id = "unknown"

    def __repr__(self):
        return f"Actor(id={self.id}, type={self.type_id!r})"


class LaneInvasionEvent(SensorData):
    """Lane-invasion event.

    Spec §11.1: server-side payload is empty (NoopSerializer); upstream's
    client-side lane-marking computation is not implemented in CarlaNet, so
    `crossed_lane_markings` is always [].
    """
    __slots__ = ("actor", "crossed_lane_markings")

    def __init__(self, sensor_frame, listener_actor=None):
        super().__init__(sensor_frame)
        self.actor = listener_actor
        self.crossed_lane_markings = []


class GnssMeasurement(SensorData):
    __slots__ = ("latitude", "longitude", "altitude")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        try:
            from CarlaNet.Sensors import GnssSensorData
            from MessagePack import MessagePackSerializer
            d = MessagePackSerializer.Deserialize[GnssSensorData](sensor_frame.PayloadBytes)
            self.latitude = float(d.Latitude)
            self.longitude = float(d.Longitude)
            self.altitude = float(d.Altitude)
        except Exception:
            self.latitude = 0.0; self.longitude = 0.0; self.altitude = 0.0


class IMUMeasurement(SensorData):
    __slots__ = ("accelerometer", "gyroscope", "compass")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        try:
            from CarlaNet.Sensors import ImuSensorData
            from MessagePack import MessagePackSerializer
            d = MessagePackSerializer.Deserialize[ImuSensorData](sensor_frame.PayloadBytes)
            self.accelerometer = d.Accelerometer
            self.gyroscope = d.Gyroscope
            self.compass = float(d.Compass)
        except Exception:
            self.accelerometer = Vector3D(0.0, 0.0, 0.0)
            self.gyroscope = Vector3D(0.0, 0.0, 0.0)
            self.compass = 0.0


class RadarDetection:
    __slots__ = ("velocity", "azimuth", "altitude", "depth")
    def __init__(self, velocity, azimuth, altitude, depth):
        self.velocity = float(velocity)
        self.azimuth = float(azimuth)
        self.altitude = float(altitude)
        self.depth = float(depth)
    def __repr__(self):
        return f"RadarDetection(v={self.velocity:.2f},az={self.azimuth:.2f},alt={self.altitude:.2f},d={self.depth:.2f})"


class RadarMeasurement(SensorData):
    """Radar measurement — iterable over `RadarDetection` instances."""

    __slots__ = ("_raw", "_detections")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        payload = bytes(sensor_frame.PayloadBytes)
        self._raw = payload
        count = len(payload) // 16
        self._detections = [
            RadarDetection(*struct.unpack_from("<ffff", payload, i * 16))
            for i in range(count)
        ]

    @property
    def raw_data(self):
        return self._raw

    def __iter__(self):     return iter(self._detections)
    def __len__(self):      return len(self._detections)
    def __getitem__(self, i): return self._detections[i]


class LidarDetection:
    __slots__ = ("point", "intensity")
    def __init__(self, x, y, z, intensity):
        self.point = Location(float(x), float(y), float(z))
        self.intensity = float(intensity)


class LidarMeasurement(SensorData):
    """Lidar measurement.

    `raw_data` exposes ONLY the point bytes (no variable header) so callers can
    `np.frombuffer(...).reshape(-1, 4)` directly per upstream behaviour.
    """
    __slots__ = ("horizontal_angle", "channels", "_raw", "_points")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        payload = bytes(sensor_frame.PayloadBytes)
        if len(payload) < 8:
            self.horizontal_angle = 0.0
            self.channels = 0
            self._raw = b""
            self._points = []
            return
        # uint32 horizontal_angle (bit-reinterpreted), uint32 channel_count, uint32[]
        ha_u32, cc = struct.unpack_from("<II", payload, 0)
        ha_bytes = struct.pack("<I", ha_u32)
        (ha_f,) = struct.unpack("<f", ha_bytes)
        self.horizontal_angle = float(ha_f)
        self.channels = int(cc)
        header_bytes = (2 + cc) * 4
        self._raw = payload[header_bytes:]
        # 16-byte points: float x,y,z,intensity
        n = len(self._raw) // 16
        self._points = [LidarDetection(*struct.unpack_from("<ffff", self._raw, i * 16))
                        for i in range(n)]

    @property
    def raw_data(self):
        return self._raw

    def __iter__(self):     return iter(self._points)
    def __len__(self):      return len(self._points)
    def __getitem__(self, i): return self._points[i]


class SemanticLidarDetection:
    __slots__ = ("point", "cos_inc_angle", "object_idx", "object_tag")
    def __init__(self, x, y, z, cos_inc_angle, object_idx, object_tag):
        self.point = Location(float(x), float(y), float(z))
        self.cos_inc_angle = float(cos_inc_angle)
        self.object_idx = int(object_idx)
        self.object_tag = int(object_tag)


class SemanticLidarMeasurement(SensorData):
    """Semantic lidar — points are 24 bytes: float x,y,z,cos_inc_angle, u32 idx, u32 tag."""
    __slots__ = ("horizontal_angle", "channels", "_raw", "_points")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        payload = bytes(sensor_frame.PayloadBytes)
        if len(payload) < 8:
            self.horizontal_angle = 0.0
            self.channels = 0
            self._raw = b""
            self._points = []
            return
        ha_u32, cc = struct.unpack_from("<II", payload, 0)
        ha_bytes = struct.pack("<I", ha_u32)
        (ha_f,) = struct.unpack("<f", ha_bytes)
        self.horizontal_angle = float(ha_f)
        self.channels = int(cc)
        header_bytes = (2 + cc) * 4
        self._raw = payload[header_bytes:]
        n = len(self._raw) // 24
        out = []
        for i in range(n):
            x, y, z, cosa, oidx, otag = struct.unpack_from("<ffffII", self._raw, i * 24)
            out.append(SemanticLidarDetection(x, y, z, cosa, oidx, otag))
        self._points = out

    @property
    def raw_data(self):
        return self._raw

    def __iter__(self):     return iter(self._points)
    def __len__(self):      return len(self._points)
    def __getitem__(self, i): return self._points[i]


class ObstacleDetectionEvent(SensorData):
    """Obstacle detection — msgpack [self, other, distance]."""
    __slots__ = ("actor", "other_actor", "distance")

    def __init__(self, sensor_frame, listener_actor=None):
        super().__init__(sensor_frame)
        try:
            from CarlaNet.Sensors import ObstacleSensorData
            from MessagePack import MessagePackSerializer
            d = MessagePackSerializer.Deserialize[ObstacleSensorData](
                sensor_frame.PayloadBytes)
            client = listener_actor._client if listener_actor is not None else None
            self.actor = _WrappedSensorActor(d.SelfActor, client)
            self.other_actor = _WrappedSensorActor(d.OtherActor, client)
            self.distance = float(d.Distance)
        except Exception:
            self.actor = listener_actor
            self.other_actor = _StubActor("unknown")
            self.distance = 0.0


class DVSEvent:
    __slots__ = ("x", "y", "t", "pol")
    def __init__(self, x, y, t, pol):
        self.x = int(x); self.y = int(y); self.t = int(t); self.pol = bool(pol)


class DVSEventArray(SensorData):
    """DVS camera event array."""
    __slots__ = ("width", "height", "fov", "_events", "_raw")

    def __init__(self, sensor_frame):
        super().__init__(sensor_frame)
        payload = bytes(sensor_frame.PayloadBytes)
        if len(payload) < 12:
            self.width = 0; self.height = 0; self.fov = 0.0
            self._events = []; self._raw = b""
            return
        w, h, fov = struct.unpack_from("<IIf", payload, 0)
        self.width = int(w); self.height = int(h); self.fov = float(fov)
        body = payload[12:]
        self._raw = body
        n = len(body) // 20
        out = []
        for i in range(n):
            x, y, t, pol = struct.unpack_from("<HHqB", body, i * 20)
            out.append(DVSEvent(x, y, t, pol))
        self._events = out

    @property
    def raw_data(self):
        return self._raw

    def __iter__(self):     return iter(self._events)
    def __len__(self):      return len(self._events)
    def __getitem__(self, i): return self._events[i]


# ─────────────────────────────────────────────────────────────────────────────
# §8 — type_id -> parser dispatch table for Actor.listen()
# ─────────────────────────────────────────────────────────────────────────────

def _p_image(sf, a):           return Image(sf)
def _p_optical(sf, a):         return OpticalFlowImage(sf)
def _p_lidar(sf, a):           return LidarMeasurement(sf)
def _p_lidar_sem(sf, a):       return SemanticLidarMeasurement(sf)
def _p_radar(sf, a):           return RadarMeasurement(sf)
def _p_collision(sf, a):       return CollisionEvent(sf, a)
def _p_gnss(sf, a):            return GnssMeasurement(sf)
def _p_imu(sf, a):             return IMUMeasurement(sf)
def _p_lane(sf, a):            return LaneInvasionEvent(sf, a)
def _p_obstacle(sf, a):        return ObstacleDetectionEvent(sf, a)
def _p_dvs(sf, a):             return DVSEventArray(sf)

_SENSOR_PARSERS = {
    "sensor.camera.rgb":                   _p_image,
    "sensor.camera.depth":                 _p_image,
    "sensor.camera.semantic_segmentation": _p_image,
    "sensor.camera.instance_segmentation": _p_image,
    "sensor.camera.normals":               _p_image,
    "sensor.camera.optical_flow":          _p_optical,
    "sensor.camera.dvs":                   _p_dvs,
    "sensor.lidar.ray_cast":               _p_lidar,
    "sensor.lidar.ray_cast_semantic":      _p_lidar_sem,
    "sensor.other.radar":                  _p_radar,
    "sensor.other.collision":              _p_collision,
    "sensor.other.gnss":                   _p_gnss,
    "sensor.other.imu":                    _p_imu,
    "sensor.other.lane_invasion":          _p_lane,
    "sensor.other.obstacle":               _p_obstacle,
}


# ─────────────────────────────────────────────────────────────────────────────
# §7 — DebugHelper (world.debug.draw_*)
# ─────────────────────────────────────────────────────────────────────────────

class DebugHelper:
    """Wraps the C# DrawDebugShape RPC."""

    def __init__(self, client):
        self._client = client

    def draw_point(self, location, size: float = 0.1, color=None,
                   life_time: float = -1.0, persistent_lines: bool = True):
        prim = PointPrimitive(location, float(size))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_line(self, begin, end, thickness: float = 0.1, color=None,
                  life_time: float = -1.0, persistent_lines: bool = True):
        prim = LinePrimitive(begin, end, float(thickness))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_arrow(self, begin, end, thickness: float = 0.1, arrow_size: float = 0.1,
                   color=None, life_time: float = -1.0, persistent_lines: bool = True):
        line = LinePrimitive(begin, end, float(thickness))
        prim = ArrowPrimitive(line, float(arrow_size))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_box(self, box, rotation, thickness: float = 0.1, color=None,
                 life_time: float = -1.0, persistent_lines: bool = True):
        prim = BoxPrimitive(box, rotation, float(thickness))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_string(self, location, text: str, draw_shadow: bool = False,
                    color=None, life_time: float = -1.0, persistent_lines: bool = True):
        prim = StringPrimitive(location, str(text), bool(draw_shadow))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))


# ─────────────────────────────────────────────────────────────────────────────
# §6 — WeatherParameters preset attributes
# ─────────────────────────────────────────────────────────────────────────────
# `WeatherParameters` is the C# record struct (already imported). Attach the
# named preset class attributes via setattr — the underlying type is a CLR
# metaclass, but pythonnet allows attribute assignment on it.

def _wp(*v):
    return WeatherParameters(*v)


# Each list is (cloudiness, precipitation, precipitation_deposits, wind_intensity,
#               sun_azimuth_angle, sun_altitude_angle, fog_density, fog_distance,
#               fog_falloff, wetness, scattering_intensity, mie_scattering_scale,
#               rayleigh_scattering_scale, dust_storm)
_WEATHER_PRESETS = {
    "Default":         (-1.0, -1.0, -1.0, -1.0, -1.0, -1.0, -1.0, -1.0, -1.0, -1.0, 1.0, 0.03, 0.0331, 0.0),
    "ClearNoon":       ( 5.0,  0.0,  0.0, 10.0, -1.0, 45.0,  2.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "CloudyNoon":      (60.0,  0.0,  0.0, 10.0, -1.0, 45.0,  3.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "WetNoon":         ( 5.0,  0.0, 50.0, 10.0, -1.0, 45.0,  3.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "WetCloudyNoon":   (60.0,  0.0, 50.0, 10.0, -1.0, 45.0,  3.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "MidRainyNoon":    (60.0, 60.0, 60.0, 60.0, -1.0, 45.0,  3.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "HardRainNoon":    (100.0,100.0,90.0,100.0, -1.0, 45.0,  7.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "SoftRainNoon":    (20.0, 30.0, 50.0, 30.0, -1.0, 45.0,  3.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "ClearSunset":     ( 5.0,  0.0,  0.0, 10.0, -1.0, 15.0,  2.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "CloudySunset":    (60.0,  0.0,  0.0, 10.0, -1.0, 15.0,  3.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "WetSunset":       ( 5.0,  0.0, 50.0, 10.0, -1.0, 15.0,  2.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "WetCloudySunset": (60.0,  0.0, 50.0, 10.0, -1.0, 15.0,  2.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "MidRainSunset":   (60.0, 60.0, 60.0, 60.0, -1.0, 15.0,  3.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "HardRainSunset":  (100.0,100.0,90.0,100.0, -1.0, 15.0,  7.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "SoftRainSunset":  (20.0, 30.0, 50.0, 30.0, -1.0, 15.0,  2.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "ClearNight":      ( 5.0,  0.0,  0.0, 10.0, -1.0, -90.0, 60.0, 75.0, 1.0,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "CloudyNight":     (60.0,  0.0,  0.0, 10.0, -1.0, -90.0, 60.0,  0.75,0.1,  0.0, 1.0, 0.03, 0.0331, 0.0),
    "WetNight":        ( 5.0,  0.0, 50.0, 10.0, -1.0, -90.0, 60.0, 75.0, 1.0, 60.0, 1.0, 0.03, 0.0331, 0.0),
    "WetCloudyNight":  (60.0,  0.0, 50.0, 10.0, -1.0, -90.0, 60.0,  0.75,0.1, 60.0, 1.0, 0.03, 0.0331, 0.0),
    "SoftRainNight":   (60.0, 30.0, 50.0, 30.0, -1.0, -90.0, 60.0,  0.75,0.1, 60.0, 1.0, 0.03, 0.0331, 0.0),
    "MidRainyNight":   (80.0, 60.0, 60.0, 60.0, -1.0, -90.0, 60.0,  0.75,0.1, 80.0, 1.0, 0.03, 0.0331, 0.0),
    "HardRainNight":   (100.0,100.0,90.0,100.0, -1.0, -90.0, 100.0, 0.75,0.1,100.0, 1.0, 0.03, 0.0331, 0.0),
    "DustStorm":       (100.0,  0.0, 0.0,100.0, -1.0, 45.0,  2.0,  0.75, 0.1,  0.0, 1.0, 0.03, 0.0331, 100.0),
}

# Attach presets to the C# WeatherParameters type so that
# `carla.WeatherParameters.ClearNoon` returns an instance, mirroring upstream.
for _name, _vals in _WEATHER_PRESETS.items():
    try:
        setattr(WeatherParameters, _name, _wp(*_vals))
    except Exception:
        # CLR metaclass may reject attribute assignment in some pythonnet versions;
        # fall back to a side-table accessible via WeatherParameters.preset(name).
        pass

def _weather_preset(name: str):
    vals = _WEATHER_PRESETS.get(name)
    if vals is None:
        raise KeyError(f"Unknown weather preset: {name!r}")
    return _wp(*vals)


# ─────────────────────────────────────────────────────────────────────────────
# §1 — MapLayer.NONE alias  (Python reserves `None`; expose both names)
# ─────────────────────────────────────────────────────────────────────────────
try:
    MapLayer.NONE = getattr(MapLayer, "None")
except Exception:
    pass


# ─────────────────────────────────────────────────────────────────────────────
# Convenience re-exports matching carla.* names
# ─────────────────────────────────────────────────────────────────────────────

WorldSettings = EpisodeSettings   # alias — same type, different name in Python API
