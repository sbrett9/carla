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
import math as _math
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

# Wave 5 integration: CarlaNet.Nav provides the pedestrian-navigation
# subsystem. Same lazy-with-fallback pattern as the TM block above —
# users without walkers never load the navmesh, and a missing Nav
# assembly falls back to a no-op WalkerAIController stub.
try:
    _ref("CarlaNet.Nav")
    _CARLANET_NAV_AVAILABLE = True
except FileNotFoundError:
    _CARLANET_NAV_AVAILABLE = False

# Native recording (CarlaNet.Recording): encodes streamed camera frames to PNG + CoT-XML entirely in
# .NET. Also provides VehicleTelemetryService, the single source of truth that get_vehicle_telemetry
# delegates to. Optional — a missing assembly falls back to the in-Python telemetry path.
try:
    _ref("CarlaNet.Recording")
    _CARLANET_RECORDING_AVAILABLE = True
except FileNotFoundError:
    _CARLANET_RECORDING_AVAILABLE = False

# Scenario execution (CarlaNet.Scenario): parses an ASAM OpenSCENARIO storyboard and drives it from
# the world tick entirely in .NET, so scenario timing never depends on interpreter scheduling. Optional
# — a missing assembly simply leaves start_scenario unavailable.
try:
    _ref("CarlaNet.Scenario")
    _CARLANET_SCENARIO_AVAILABLE = True
except FileNotFoundError:
    _CARLANET_SCENARIO_AVAILABLE = False

# ── C# type imports ───────────────────────────────────────────────────────────
from CarlaNet.Transport import CarlaClient as _CarlaClient
from CarlaNet.Types.Geom import (Transform as _CSTransform,
                                  Location as _CSLocation,
                                  Rotation as _CSRotation,
                                  Vector2D as _CSVector2D,
                                  Vector3D as _CSVector3D,
                                  BoundingBox as _CSBoundingBox,
                                  GeoLocation)

# Mutable Python wrappers around the C# init-only geom record structs.
# Upstream scripts freely mutate (e.g. `transform.location.z += 2.0`); we
# mirror that by exposing snake_case attributes and rebuilding a fresh C#
# value at the RPC boundary via `_to_cs()`. Wrappers also restore upstream
# behavior the bare C# structs lack: Vector3D arithmetic, Location+Vector3D,
# `Rotation.get_forward_vector()`, and `Transform.transform(vec)`.
#
# Constructors accept either Python kwargs or duck-typed objects with
# `.x/.y/.z` / `.pitch/.yaw/.roll`, so they also act as the "wrap a value
# coming back from C#" path (CS record structs expose those lowercase
# aliases via `[IgnoreMember] public float x => X;`).

class Vector3D:
    __slots__ = ("x", "y", "z")
    def __init__(self, x=0.0, y=0.0, z=0.0):
        self.x = float(x); self.y = float(y); self.z = float(z)
    def _to_cs(self):
        return _CSVector3D(self.x, self.y, self.z)
    def length(self):
        return _math.sqrt(self.x*self.x + self.y*self.y + self.z*self.z)
    def squared_length(self):
        return self.x*self.x + self.y*self.y + self.z*self.z
    def make_unit_vector(self):
        n = self.length() or 1.0
        return Vector3D(self.x / n, self.y / n, self.z / n)
    def dot(self, o):
        return self.x*o.x + self.y*o.y + self.z*o.z
    def cross(self, o):
        return Vector3D(self.y*o.z - self.z*o.y,
                        self.z*o.x - self.x*o.z,
                        self.x*o.y - self.y*o.x)
    def __add__(self, o):  return Vector3D(self.x + o.x, self.y + o.y, self.z + o.z)
    def __sub__(self, o):  return Vector3D(self.x - o.x, self.y - o.y, self.z - o.z)
    def __mul__(self, s):  return Vector3D(self.x * s, self.y * s, self.z * s)
    __rmul__ = __mul__
    def __truediv__(self, s): return Vector3D(self.x / s, self.y / s, self.z / s)
    def __neg__(self):     return Vector3D(-self.x, -self.y, -self.z)
    def __eq__(self, o):
        return (hasattr(o, "x") and hasattr(o, "y") and hasattr(o, "z")
                and self.x == o.x and self.y == o.y and self.z == o.z)
    def __ne__(self, o):   return not self.__eq__(o)
    def __hash__(self):    return hash((self.x, self.y, self.z))
    def __repr__(self):    return f"Vector3D(x={self.x}, y={self.y}, z={self.z})"


class Vector2D:
    __slots__ = ("x", "y")
    def __init__(self, x=0.0, y=0.0):
        self.x = float(x); self.y = float(y)
    def _to_cs(self):
        return _CSVector2D(self.x, self.y)
    def length(self):         return _math.sqrt(self.x*self.x + self.y*self.y)
    def squared_length(self): return self.x*self.x + self.y*self.y
    def __add__(self, o):  return Vector2D(self.x + o.x, self.y + o.y)
    def __sub__(self, o):  return Vector2D(self.x - o.x, self.y - o.y)
    def __mul__(self, s):  return Vector2D(self.x * s, self.y * s)
    __rmul__ = __mul__
    def __truediv__(self, s): return Vector2D(self.x / s, self.y / s)
    def __eq__(self, o):
        return hasattr(o, "x") and hasattr(o, "y") and self.x == o.x and self.y == o.y
    def __ne__(self, o):   return not self.__eq__(o)
    def __hash__(self):    return hash((self.x, self.y))
    def __repr__(self):    return f"Vector2D(x={self.x}, y={self.y})"


class Location:
    """Mutable Location wrapper. In upstream libcarla, Location extends
    Vector3D; here we keep them as sibling classes that share the same fields
    and basic arithmetic. Supports Location + Vector3D (returns Location)."""
    __slots__ = ("x", "y", "z")
    def __init__(self, x=0.0, y=0.0, z=0.0):
        self.x = float(x); self.y = float(y); self.z = float(z)
    def _to_cs(self):
        return _CSLocation(self.x, self.y, self.z)
    def distance(self, other):
        dx = self.x - other.x; dy = self.y - other.y; dz = self.z - other.z
        return _math.sqrt(dx*dx + dy*dy + dz*dz)
    def __add__(self, o):  return Location(self.x + o.x, self.y + o.y, self.z + o.z)
    def __sub__(self, o):  return Location(self.x - o.x, self.y - o.y, self.z - o.z)
    def __eq__(self, o):
        return (hasattr(o, "x") and hasattr(o, "y") and hasattr(o, "z")
                and self.x == o.x and self.y == o.y and self.z == o.z)
    def __ne__(self, o):   return not self.__eq__(o)
    def __hash__(self):    return hash((self.x, self.y, self.z))
    def __repr__(self):    return f"Location(x={self.x}, y={self.y}, z={self.z})"


class Rotation:
    __slots__ = ("pitch", "yaw", "roll")
    def __init__(self, pitch=0.0, yaw=0.0, roll=0.0):
        self.pitch = float(pitch); self.yaw = float(yaw); self.roll = float(roll)
    def _to_cs(self):
        return _CSRotation(self.pitch, self.yaw, self.roll)
    def get_forward_vector(self):
        cp = _math.cos(_math.radians(self.pitch)); sp = _math.sin(_math.radians(self.pitch))
        cy = _math.cos(_math.radians(self.yaw));   sy = _math.sin(_math.radians(self.yaw))
        return Vector3D(cp * cy, cp * sy, sp)
    def get_right_vector(self):
        cp = _math.cos(_math.radians(self.pitch)); sp = _math.sin(_math.radians(self.pitch))
        cy = _math.cos(_math.radians(self.yaw));   sy = _math.sin(_math.radians(self.yaw))
        cr = _math.cos(_math.radians(self.roll));  sr = _math.sin(_math.radians(self.roll))
        return Vector3D(cy * sp * sr - sy * cr,
                        sy * sp * sr + cy * cr,
                        -cp * sr)
    def get_up_vector(self):
        cp = _math.cos(_math.radians(self.pitch)); sp = _math.sin(_math.radians(self.pitch))
        cy = _math.cos(_math.radians(self.yaw));   sy = _math.sin(_math.radians(self.yaw))
        cr = _math.cos(_math.radians(self.roll));  sr = _math.sin(_math.radians(self.roll))
        return Vector3D(-cy * sp * cr - sy * sr,
                        -sy * sp * cr + cy * sr,
                        cp * cr)
    def __eq__(self, o):
        return (hasattr(o, "pitch") and hasattr(o, "yaw") and hasattr(o, "roll")
                and self.pitch == o.pitch and self.yaw == o.yaw and self.roll == o.roll)
    def __ne__(self, o):   return not self.__eq__(o)
    def __hash__(self):    return hash((self.pitch, self.yaw, self.roll))
    def __repr__(self):
        return f"Rotation(pitch={self.pitch}, yaw={self.yaw}, roll={self.roll})"


def _as_location(v):
    if v is None:                  return Location()
    if isinstance(v, Location):    return v
    return Location(float(v.x), float(v.y), float(v.z))

def _as_rotation(v):
    if v is None:                  return Rotation()
    if isinstance(v, Rotation):    return v
    return Rotation(float(v.pitch), float(v.yaw), float(v.roll))

def _as_vector3d(v):
    if v is None:                  return Vector3D()
    if isinstance(v, Vector3D):    return v
    return Vector3D(float(v.x), float(v.y), float(v.z))


class Transform:
    """Mutable Transform wrapper. `.location` is a Location wrapper and
    `.rotation` is a Rotation wrapper — both are real fields (not properties
    that rebuild), so `transform.location.z += 2.0` mutates in place and the
    new value survives until `_to_cs()` is called at the RPC boundary."""
    __slots__ = ("location", "rotation")
    def __init__(self, location=None, rotation=None):
        self.location = _as_location(location)
        self.rotation = _as_rotation(rotation)

    def _to_cs(self):
        return _CSTransform(self.location._to_cs(), self.rotation._to_cs())

    def get_matrix(self):
        """4x4 transform matrix as a row-major flat list of 16 floats.
        Order matches carla::geom::Transform::GetMatrix() so downstream math
        (TransformPoint, TransformVector) ports directly."""
        cy = _math.cos(_math.radians(self.rotation.yaw))
        sy = _math.sin(_math.radians(self.rotation.yaw))
        cr = _math.cos(_math.radians(self.rotation.roll))
        sr = _math.sin(_math.radians(self.rotation.roll))
        cp = _math.cos(_math.radians(self.rotation.pitch))
        sp = _math.sin(_math.radians(self.rotation.pitch))
        loc = self.location
        return [
            cp*cy, cy*sp*sr - sy*cr, -cy*sp*cr - sy*sr, loc.x,
            cp*sy, sy*sp*sr + cy*cr, -sy*sp*cr + cy*sr, loc.y,
            sp,    -cp*sr,            cp*cr,            loc.z,
            0.0,   0.0,               0.0,              1.0,
        ]

    def transform(self, in_point):
        """In-place: apply rotation + translation to a Vector3D / Location-like
        point. Mutates `in_point.x/y/z` and returns it (upstream semantics)."""
        M = self.get_matrix()
        x, y, z = float(in_point.x), float(in_point.y), float(in_point.z)
        in_point.x = M[0]*x + M[1]*y + M[2]*z + M[3]
        in_point.y = M[4]*x + M[5]*y + M[6]*z + M[7]
        in_point.z = M[8]*x + M[9]*y + M[10]*z + M[11]
        return in_point

    def transform_vector(self, in_vec):
        """In-place: apply rotation only (no translation) to a Vector3D."""
        M = self.get_matrix()
        x, y, z = float(in_vec.x), float(in_vec.y), float(in_vec.z)
        in_vec.x = M[0]*x + M[1]*y + M[2]*z
        in_vec.y = M[4]*x + M[5]*y + M[6]*z
        in_vec.z = M[8]*x + M[9]*y + M[10]*z
        return in_vec

    def get_forward_vector(self): return self.rotation.get_forward_vector()
    def get_right_vector(self):   return self.rotation.get_right_vector()
    def get_up_vector(self):      return self.rotation.get_up_vector()

    def __eq__(self, o):
        return (isinstance(o, Transform)
                and self.location == o.location and self.rotation == o.rotation)
    def __ne__(self, o):   return not self.__eq__(o)
    def __repr__(self):    return f"Transform({self.location!r}, {self.rotation!r})"


class BoundingBox:
    __slots__ = ("location", "extent", "rotation")
    def __init__(self, location=None, extent=None, rotation=None):
        self.location = _as_location(location)
        self.extent   = _as_vector3d(extent)
        self.rotation = _as_rotation(rotation)
    def _to_cs(self):
        return _CSBoundingBox(self.location._to_cs(),
                              self.extent._to_cs(),
                              self.rotation._to_cs())
    def __repr__(self):
        return f"BoundingBox({self.location!r}, {self.extent!r}, {self.rotation!r})"


def _to_cs(obj):
    """Convert a Python wrapper (control or geom) to its C# value;
    pass other values through unchanged."""
    return obj._to_cs() if hasattr(obj, "_to_cs") else obj
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
        if hasattr(d, "_to_cs"):
            d = d._to_cs()
        elif not isinstance(d, _CSVector3D):
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
    """Backwards-compat alias for _to_cs (defined alongside the geom wrappers).
    Works for any Python wrapper exposing `_to_cs()` — control or geom."""
    return _to_cs(ctrl)


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
from CarlaNet.Types.Rpc.Environment import (EpisodeSettings, WeatherParameters,
                                            OpendriveGenerationParameters)
# OSM→OpenDRIVE→runtime-world: OsmConversionOptions lives in CarlaNet.Map (loaded
# via the guarded _ref("CarlaNet.Map") above). generate_world_from_osm() falls back
# to C# defaults when osm_options is None.
try:
    from CarlaNet.Map import OsmConversionOptions as _OsmConversionOptions
except Exception:
    _OsmConversionOptions = None  # type: ignore

def _default_opendrive_params():
    # OpendriveGenerationParameters is a C# record struct whose only ctor is the
    # 7-arg primary (no pythonnet-callable parameterless ctor), so construct it
    # explicitly with the upstream carla.Client.generate_opendrive_world defaults:
    # vertex_distance=2.0, max_road_length=50.0, wall_height=1.0, additional_width=0.6,
    # smooth_junctions=True, enable_mesh_visibility=True, enable_pedestrian_navigation=True.
    return OpendriveGenerationParameters(2.0, 50.0, 1.0, 0.6, True, True, True)

def _default_osm_opendrive_params():
    # OSM-specific mesh defaults (CARLA Docs/tuto_G_openstreetmap.md): wall_height=0.0
    # is strongly recommended — OSM encodes opposing lanes as separate roads, so walls
    # overlap and cause spurious collisions. Larger max_road_length (500) reduces mesh
    # fragmentation. (vertex_distance=2.0, max_road_length=500.0, wall_height=0.0,
    # additional_width=0.6, smooth_junctions, enable_mesh_visibility, pedestrian_nav.)
    return OpendriveGenerationParameters(2.0, 500.0, 0.0, 0.6, True, True, True)
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

# Wave 5: NavigationFactory hands out the process-wide WalkerNavigation
# instance lazily on first walker-related call. WalkerNavigation itself is
# the C# facade matching upstream carla.WalkerAIController + the three
# World.set_pedestrians_* / get_random_location_from_navigation entry points.
if _CARLANET_NAV_AVAILABLE:
    try:
        from CarlaNet.Nav import NavigationFactory as _CSNavigationFactory
    except Exception:
        _CARLANET_NAV_AVAILABLE = False
        _CSNavigationFactory = None  # type: ignore
else:
    _CSNavigationFactory = None  # type: ignore
from System import TimeSpan
from System.Collections.Generic import List
import struct


def _sync(task):
    """Block on a .NET Task and return its result."""
    return task.GetAwaiter().GetResult()


def _to_cs_geo(p):
    """Coerce a Python (lat, lon[, alt]) tuple / object to a C# GeoLocation."""
    if isinstance(p, GeoLocation):
        return p
    if hasattr(p, "latitude"):
        return GeoLocation(float(p.latitude), float(p.longitude),
                           float(getattr(p, "altitude", 0.0)))
    lat = float(p[0])
    lon = float(p[1])
    alt = float(p[2]) if len(p) > 2 else 0.0
    return GeoLocation(lat, lon, alt)


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
        # Wrap each C# Transform into the mutable Python Transform so callers
        # can safely do `sp.location.z += 2.0` without the C# init-only struct
        # silently swallowing the write.
        self._spawn_points = [
            Transform(spawn_points[i].location, spawn_points[i].rotation)
            for i in range(spawn_points.Count)
        ]

    def get_spawn_points(self):
        # Return fresh Transform wrappers each call so callers that mutate a
        # spawn point (e.g. raising z before respawning) don't affect later
        # callers — upstream libcarla's get_spawn_points() returns independent
        # Transform values.
        return [Transform(sp.location, sp.rotation) for sp in self._spawn_points]

    def __repr__(self):
        return f"Map(name={self.name!r}, spawn_points={len(self._spawn_points)})"


# ── Actor wrapper ─────────────────────────────────────────────────────────────

class Actor:
    """Python wrapper around a CARLA actor. Backed by world observer cache."""

    def __init__(self, cs_actor, client):
        self._actor  = cs_actor
        self._client = client   # _CarlaClient (C# object)
        self._sub    = None     # sensor stream subscription
        # Populated by World.spawn_actor when attach_to= is supplied; used by
        # WalkerAIController.start/stop/go_to_location to route operations to
        # the parent walker actor (matches upstream PythonAPI semantics).
        self._parent = None

    @property
    def id(self) -> int:
        return int(self._actor.Id)

    @property
    def type_id(self) -> str:
        return str(self._actor.Description.Id)

    @property
    def bounding_box(self) -> BoundingBox:
        cs = self._actor.BoundingBox
        return BoundingBox(cs.location, cs.extent, cs.rotation)

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
        cs = self._client.GetActorTransform(self._actor.Id)
        return Transform(cs.location, cs.rotation)

    def get_location(self) -> Location:
        cs = self._client.GetActorTransform(self._actor.Id)
        return Location(cs.location.x, cs.location.y, cs.location.z)

    def get_velocity(self) -> Vector3D:
        return _as_vector3d(self._client.GetActorVelocity(self._actor.Id))

    def get_angular_velocity(self) -> Vector3D:
        return _as_vector3d(self._client.GetActorAngularVelocity(self._actor.Id))

    def get_acceleration(self) -> Vector3D:
        return _as_vector3d(self._client.GetActorAcceleration(self._actor.Id))

    def get_control(self):
        cs = self._client.GetVehicleControl(self._actor.Id)
        # Return a mutable Python wrapper — upstream callers freely mutate this.
        return VehicleControl(cs.Throttle, cs.Steer, cs.Brake, cs.HandBrake,
                              cs.Reverse, cs.ManualGearShift, cs.Gear)

    def get_world(self):
        return World(self._client)

    # ── Commands ──────────────────────────────────────────────────────────────

    def set_transform(self, transform: Transform):
        _sync(self._client.SetActorTransformAsync(self._actor.Id, _to_cs(transform)))

    def set_location(self, location: Location):
        _sync(self._client.SetActorLocationAsync(self._actor.Id, _to_cs(location)))

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
        # Register/unregister with the in-process TM so its worker picks up the vehicle. The
        # server-side autopilot flag alone won't drive it. Hand over the actor record rather than
        # its id: registering by id makes the TM fetch the record back from the simulator, which
        # returns nothing for a vehicle spawned in this same frame, and the registration is lost
        # with the vehicle left sitting where it spawned.
        Client._tm_register_actors([self._actor], int(tm_port), bool(enabled))

    def set_light_state(self, state):
        # Accept Python int / VehicleLightState wrapper / C# VehicleLightStateFlags
        flags = VehicleLightStateFlags(int(state)) if not isinstance(state, VehicleLightStateFlags) else state
        _sync(self._client.SetVehicleLightStateAsync(self._actor.Id, flags))

    def get_light_state(self):
        flags = _sync(self._client.GetVehicleLightStateAsync(self._actor.Id))
        return VehicleLightState(int(flags))

    def set_simulate_physics(self, enabled: bool):
        _sync(self._client.SetActorSimulatePhysicsAsync(self._actor.Id, enabled))

    def set_target_velocity(self, velocity):
        """Set the actor's linear velocity directly (m/s, world axes).

        Used at spawn to start a vehicle at the speed of the traffic it is joining. Without it a
        vehicle is created at rest and has to accelerate from zero, which on a motorway means it
        spends its first seconds being overtaken by everything around it."""
        v = velocity._to_cs() if hasattr(velocity, "_to_cs") else velocity
        _sync(self._client.SetActorTargetVelocityAsync(self._actor.Id, v))

    def set_fade(self, hide: float):
        """Set the staging fade for this actor: 0.0 = fully visible, 1.0 = fully dissolved away.

        Drives a dithered opacity dissolve so boundary-aware traffic can fade in as it enters the
        scene and out as it leaves (see generate_traffic_carlanet staging). Applies to the whole
        vehicle (body, glass, lights, wheels) via Custom Primitive Data index 8.
        """
        _sync(self._client.SetActorFadeAsync(self._actor.Id, float(max(0.0, min(1.0, hide)))))

    def set_collisions(self, enabled: bool):
        """Toggle the actor's collision response. Used by
        WalkerAIController.start() to free the walker pose for Detour to drive
        (matches upstream WalkerAIController::Start lines 28-29)."""
        _sync(self._client.SetActorCollisionsAsync(self._actor.Id, enabled))

    def set_enable_gravity(self, enabled: bool):
        _sync(self._client.SetActorEnableGravityAsync(self._actor.Id, enabled))

    def get_physics_control(self):
        cs = _sync(self._client.GetVehiclePhysicsControlAsync(self._actor.Id))
        return _PhysicsControlWrapper(cs)

    def apply_physics_control(self, ctrl):
        cs = ctrl._to_cs() if hasattr(ctrl, "_to_cs") else ctrl
        _sync(self._client.ApplyPhysicsControlToVehicleAsync(self._actor.Id, cs))

    def enable_constant_velocity(self, velocity: Vector3D):
        _sync(self._client.EnableActorConstantVelocityAsync(self._actor.Id, _to_cs(velocity)))

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


class _NoOpWalkerNavigation:
    """Fallback returned by Client._get_walker_navigation() when the navmesh
    is unavailable (CarlaNet.Nav DLL missing, server has no navmesh for the
    current map, RPC failure). Calls no-op so the user script keeps running
    instead of crashing; reports a warning on first construction only."""
    def Start(self, *a, **kw): pass
    def Stop(self, *a, **kw): pass
    def GoToLocation(self, *a, **kw): pass
    def SetMaxSpeed(self, *a, **kw): pass
    def GetRandomLocationFromNavigation(self): return None
    def SetPedestriansCrossFactor(self, *a, **kw): pass
    def SetPedestriansSeed(self, *a, **kw): pass
    HasWalkers = False
    def IsWalkerAlive(self, *a, **kw): return True


class WalkerAIController(Actor):
    """Pedestrian AI controller — controls a parent walker via Detour.

    Mirrors upstream carla.WalkerAIController (PythonAPI/carla/src/Actor.cpp
    lines 226-232). Construction goes through World.spawn_actor with
    attach_to=walker, which populates self._parent so start/stop/etc. can
    dispatch operations to the underlying walker actor.
    """

    def _target_id(self) -> int:
        """The actor id passed to WalkerNavigation — always the PARENT walker,
        never the controller itself. Two paths:
          - spawn_actor(attach_to=walker) populates self._parent (preferred).
          - world.get_actors([ids]) lookup-by-id doesn't set _parent; fall
            back to the C# Actor.ParentId field which CARLA populates server-side.
        """
        if self._parent is not None:
            return int(self._parent.id)
        try:
            pid = int(self._actor.ParentId)
            if pid != 0:
                return pid
        except Exception:
            pass
        return int(self.id)

    def start(self):
        """Register the parent walker into the crowd and start driving it.
        Per upstream WalkerAIController::Start, also disables physics and
        collisions on the walker so Detour can drive the pose directly."""
        import sys
        nav = Client._get_walker_navigation()
        target_id = self._target_id()
        # Issue RPCs directly against the parent walker id; we may not have an
        # Actor wrapper for it (e.g., when the controller came from get_actors
        # by-id rather than spawn_actor(attach_to=...)).
        try:
            _sync(self._client.SetActorSimulatePhysicsAsync(target_id, False))
        except Exception as ex:
            print(f"[carlanet] WalkerAIController.start: set_simulate_physics failed for {target_id}: {ex}", file=sys.stderr)
        try:
            _sync(self._client.SetActorCollisionsAsync(target_id, False))
        except Exception as ex:
            print(f"[carlanet] WalkerAIController.start: set_collisions failed for {target_id}: {ex}", file=sys.stderr)
        try:
            # nav.Start expects a C# Location; .location on a CS Transform is
            # already a CS Location, so pass it straight through.
            loc = self._client.GetActorTransform(target_id).location
        except Exception:
            loc = _CSLocation(0.0, 0.0, 0.0)
        nav.Start(target_id, loc)

    def stop(self):
        """Unregister the parent walker from the crowd."""
        nav = Client._get_walker_navigation()
        nav.Stop(self._target_id())

    def go_to_location(self, location):
        """Route the parent walker to ``location``."""
        nav = Client._get_walker_navigation()
        # WalkerNavigation.GoToLocation expects a C# Location; accept either a
        # Python wrapper (preferred), a bare CS Location, or any duck-typed
        # x/y/z object.
        cs_loc = _to_cs(location)
        if not isinstance(cs_loc, _CSLocation):
            cs_loc = _CSLocation(float(cs_loc.x), float(cs_loc.y), float(cs_loc.z))
        nav.GoToLocation(self._target_id(), cs_loc)

    def set_max_speed(self, speed):
        """Set the parent walker's maximum speed in m/s (default upstream: 1.4)."""
        nav = Client._get_walker_navigation()
        nav.SetMaxSpeed(self._target_id(), float(speed))


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
    # Upstream CARLA's walker AI controller blueprint id is "controller.ai.walker"
    # (NOT "walker.controller"). Match both for safety.
    if type_id.startswith("controller.ai.walker"):           return WalkerAIController
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
            return SpawnActorCommand(desc, _to_cs(self._transform), parent_id, do_after_cs)

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
            return ApplyTransformCommand(self._id, _to_cs(self._tf))

    class ApplyLocation(_Cmd):
        def __init__(self, actor, location: Location):
            self._id  = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._loc = location

        def to_cs(self) -> Command:
            return ApplyLocationCommand(self._id, _to_cs(self._loc))

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

class PlannedRoute:
    """A route the Traffic Manager searched the road graph for, ready to be given to a vehicle.

    Returned by TrafficManager.plan_route() and consumed by TrafficManager.apply_route(). Treat it
    as opaque: it carries both the waypoints the vehicle will follow and the identity of every
    waypoint on the route, which is what lets the Traffic Manager tell whether the vehicle is still
    on it.
    """
    __slots__ = ("_route", "_locations")

    def __init__(self, cs_route):
        self._route = cs_route
        self._locations = None

    @property
    def destination(self) -> Location:
        d = self._route.Destination
        return Location(d.X, d.Y, d.Z)

    @property
    def length_m(self) -> float:
        """Distance along the route in metres."""
        return float(self._route.LengthMetres)

    @property
    def locations(self):
        """The route's waypoints as Locations, in travel order. Built on first access — a long route
        holds hundreds of points and most callers only need the length."""
        if self._locations is None:
            self._locations = [Location(p.X, p.Y, p.Z) for p in self._route.Path]
        return self._locations

    def __len__(self) -> int:
        return int(self._route.Path.Count)

    def __repr__(self):
        d = self.destination
        return (f"PlannedRoute({len(self)} waypoints, {self.length_m:.0f} m, "
                f"to ({d.x:.1f}, {d.y:.1f}))")


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

    def set_path(self, actor: Actor, path, empty_buffer: bool = True):
        """Give an autopilot vehicle a custom route: a list of Locations to drive toward, in order.

        Wraps the C# TrafficManager.SetCustomPath. The TM navigates the road graph junction by
        junction toward each point (it does NOT teleport or go off-road), so a single far-away
        destination makes the vehicle head there across the map. Call after set_autopilot(True).

        A single far-away destination is a bearing, not a route: each junction is chosen greedily
        from the point the vehicle is at, with no way to see past it. Use plan_route() +
        apply_route() to have the road graph searched first and the whole route handed over.
        """
        from System.Collections.Generic import List as _List
        cs_path = _List[_CSLocation]()
        for p in path:
            cs_path.Add(p._to_cs() if hasattr(p, "_to_cs") else p)
        self._tm.SetCustomPath(actor._actor, cs_path, bool(empty_buffer))

    def plan_route(self, origin, destination):
        """Search the road graph for a route from `origin` to `destination`. Returns a PlannedRoute,
        or None when no sequence of lanes connects them.

        The search runs on the calling thread and never on the Traffic Manager's tick, so call this
        BEFORE spawning the vehicle: it keeps the tick free, and it lets a spawn point with no route
        to the destination be rejected before a vehicle exists there. The result depends only on the
        two endpoints and the map, so a scenario replayed with the same seed produces the same
        routes. Speed, collision avoidance and traffic-signal response stay emergent.

        Hand the result to apply_route() to put a spawned vehicle on it.
        """
        o = origin._to_cs() if hasattr(origin, "_to_cs") else origin
        d = destination._to_cs() if hasattr(destination, "_to_cs") else destination
        cs_route = self._tm.PlanRoute(o, d)
        return None if cs_route is None else PlannedRoute(cs_route)

    def apply_route(self, actor: Actor, route):
        """Put a vehicle on a route returned by plan_route().

        The route's waypoints become the vehicle's path, and the vehicle is watched from then on: if
        it leaves the route — an automatic lane change past an obstacle, a shove from a collision, a
        junction taken differently from the plan — it is replanned from wherever it now is to the
        same destination. Every such event prints a '[route]' line naming the vehicle.
        """
        self._tm.ApplyRoute(actor._actor, route._route if isinstance(route, PlannedRoute) else route)

    def clear_route(self, actor: Actor):
        """Stop watching a vehicle's route. Its current path is left in place."""
        self._tm.ClearRoute(actor._actor)

    def set_event_log_path(self, path):
        """Also append the Traffic Manager's event lines to `path` (None to stop).

        Its '[route]' and '[traffic]' lines go to .NET's own handle on the console, so wrapping
        Python's streams cannot capture them; this asks the Traffic Manager itself to write them to
        the file as well. They still appear on the console either way."""
        self._tm.SetEventLogPath(path)

    def get_speed_limit_kph_at(self, location) -> float:
        """The speed limit posted on the lane nearest `location`, in km/h; 0 where the road declares
        none. The same figure the Traffic Manager governs a vehicle by, so a caller placing a
        vehicle can start it at the speed it is about to be driven at."""
        loc = location._to_cs() if hasattr(location, "_to_cs") else location
        return float(self._tm.GetSpeedLimitKphAt(loc))

    def get_routed_vehicle_count(self) -> int:
        """How many vehicles are currently following a planned route."""
        return int(self._tm.RoutedVehicleCount)

    def set_route_replan_attempt_limit(self, limit: int):
        """How many consecutive failed replans a vehicle may accumulate before the greedy fallback
        takes over. 0 means the fallback is never reached however often replanning fails. Default 3.
        Inert unless set_route_greedy_fallback_enabled(True) is also set."""
        self._tm.SetRouteReplanAttemptLimit(int(limit))

    def set_route_greedy_fallback_enabled(self, enabled: bool):
        """Whether a vehicle that cannot be replanned is eventually handed back to greedy steering
        toward its destination, rather than going on trying to find a real route. Off by default."""
        self._tm.SetRouteGreedyFallbackEnabled(bool(enabled))

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

    # ── CarlaNet digital-twin extensions (Cesium terrain elevation) ───────────
    # Not part of upstream carla. These back the OpenDRIVE <elevation> injection
    # pipeline (CarlaNet.Map.OpenDrive.ElevationInjector). Require a Cesium tileset
    # in the loaded world and the server to be ticking (async mode).

    def configure_cesium_georeference(self, latitude, longitude, height=0.0,
                                      ion_token="", ion_asset_id=0,
                                      ground_ion_asset_id=0, refresh=True):
        """Configure the layered Cesium globe at (latitude, longitude, height). `ion_asset_id`
        is the visual "photoreal" layer; `ground_ion_asset_id` (>0) adds a hidden collidable
        bare-earth "ground" layer (e.g. Cesium World Terrain asset 1), the height-sample source.
        Returns True on success (False/raises if no CesiumGeoreference is present)."""
        origin = GeoLocation(float(latitude), float(longitude), float(height))
        return bool(_sync(self._client.ConfigureCesiumGeoreferenceAsync(
            origin, str(ion_token), int(ion_asset_id), int(ground_ion_asset_id), bool(refresh))))

    def set_cesium_visible(self, visible: bool):
        """Show/hide the Cesium photogrammetry overlay, ALL tilesets (watch just the CARLA
        actors). For one layer use set_layer_visible('photoreal'|'ground', ...)."""
        return bool(_sync(self._client.SetCesiumVisibleAsync(bool(visible))))

    def set_cesium_collision(self, enabled: bool):
        """Enable/disable physics collision on the Cesium photogrammetry tilesets (ALL).
        Collision is ON by default; this toggle never changes spawn defaults."""
        return bool(_sync(self._client.SetCesiumCollisionAsync(bool(enabled))))

    def set_solar_time(self, hours: float):
        """Set the sun's local solar clock and refresh lighting. `hours` is 0-24 (wraps) in the
        time zone derived from the map longitude; noon (12.0) is local solar noon. Returns False
        if the world has no CesiumSunSky. See also set_solar_date, get_solar_state."""
        return bool(_sync(self._client.SetSolarTimeAsync(float(hours))))

    def set_solar_date(self, year: int, month: int, day: int):
        """Set the sun's calendar date (drives the seasonal sun angle) and refresh lighting.
        Returns False if the world has no CesiumSunSky."""
        return bool(_sync(self._client.SetSolarDateAsync(int(year), int(month), int(day))))

    def get_solar_state(self):
        """Current sun clock/date/origin/angles, or None if the world has no CesiumSunSky. Returns a
        dict: {solar_time, year, month, day, time_zone, lat, lon, sun_elevation_deg, sun_azimuth_deg,
        advancing, rate}. sun_elevation_deg is degrees above the horizon; sun_azimuth_deg is degrees
        clockwise from North. Reads the world-observer cache (paired to the latest tick, no RPC); falls
        back to an on-demand RPC if the observer cache isn't populated yet."""
        vals = None
        try:
            cached = self._client.GetCachedSolarState()
            if cached is not None and cached.Count >= 9:
                vals = cached
        except Exception:
            vals = None
        if vals is None:
            vals = _sync(self._client.GetSolarStateAsync())
        if vals is None or vals.Count < 9:
            return None
        return {"solar_time": float(vals[0]), "year": int(vals[1]), "month": int(vals[2]),
                "day": int(vals[3]), "time_zone": float(vals[4]),
                "lat": float(vals[5]), "lon": float(vals[6]),
                "sun_elevation_deg": float(vals[7]), "sun_azimuth_deg": float(vals[8]),
                "advancing": bool(vals[9]) if vals.Count > 9 else False,
                "rate": float(vals[10]) if vals.Count > 10 else 1.0}

    def set_time_advance(self, enabled: bool, rate: float = 1.0):
        """Enable/disable automatic advancement of the sun's solar clock (the sun moves as the scene
        runs). `rate` is sun-clock seconds per real second (1.0 = real time; >1 accelerates, e.g.
        3600 = one hour per second). Advances with the world tick, so it tracks wall-clock in
        asynchronous mode and simulation time under synchronous ticking. Returns False if the world
        has no CesiumSunSky."""
        return bool(_sync(self._client.SetTimeAdvanceAsync(bool(enabled), float(rate))))

    def set_road_rendered(self, rendered: bool):
        """Show/hide the CARLA OpenDRIVE road-mesh RENDERING (collision unaffected — cars
        still drive). Stops the road mesh z-fighting with the photoreal Cesium streets."""
        return bool(_sync(self._client.SetRoadRenderedAsync(bool(rendered))))

    def set_layer_visible(self, layer: str, visible: bool):
        """Per-layer visibility (08_Layer_Architecture). `layer` is a Cesium tileset tag
        ('photoreal'/'ground', '' = all tilesets), 'road' (the OpenDRIVE mesh) or 'signals'
        (the traffic lights and stop/yield/speed-limit signs generated from OpenDRIVE — signals
        matched to a hand-placed actor, as in the shipped towns, are not affected). Rendering
        only — collision is independent (see set_layer_collision), so hidden signals keep their
        stop-line trigger volumes and vehicles go on obeying them."""
        return bool(_sync(self._client.SetLayerVisibleAsync(str(layer), bool(visible))))

    def set_layer_collision(self, layer: str, enabled: bool):
        """Per-layer physics collision (08_Layer_Architecture). Same layer naming as
        set_layer_visible; independent of visibility."""
        return bool(_sync(self._client.SetLayerCollisionAsync(str(layer), bool(enabled))))

    def set_layer_offset(self, layer: str, offset_meters: float):
        """Move one Cesium tileset layer ('photoreal'/'ground') up/down by offset_meters (signed,
        +up) by giving it its own georeference, WITHOUT moving the main (truth) georeference, so
        get_cesium_origin and height sampling stay anchored to the real ground. Used by the
        constant-offset height-align modes ('area'/'origin') to drop the hidden bare-earth 'ground'
        layer by the same amount the roads were raised/lowered, so its collision lines up with the
        road (vehicles don't float on-road or fall through off-road). 0 returns the layer to the
        main georeference."""
        return bool(_sync(self._client.SetLayerOffsetAsync(str(layer), float(offset_meters))))

    def build_draped_terrain(self, origin_x, origin_y, cell_size, num_cols, num_rows, heights):
        """Build/replace the hidden, collision-only draped ground surface (a heightfield) vehicles
        drive on across the sandbox, on- and off-road. `heights` is a row-major sequence (or numpy
        array) of world Z in METRES, length num_cols*num_rows, indexed [row*num_cols + col]; grid
        corner (col 0,row 0) sits at world (origin_x, origin_y) metres, +col=+X, +row=+Y, spacing
        cell_size m. Returns True on success. The staging rectangle traffic uses is recorded
        separately — see set_staging_bounds."""
        from System import Array, Double
        try:
            flat = heights.ravel().tolist() if hasattr(heights, "ravel") else list(heights)
        except Exception:
            flat = list(heights)
        arr = Array[Double]([float(h) for h in flat])
        return bool(_sync(self._client.BuildDrapedTerrainAsync(
            float(origin_x), float(origin_y), float(cell_size),
            int(num_cols), int(num_rows), arr)))

    def set_staging_bounds(self, min_x, min_y, max_x, max_y, margin):
        """Record the sandbox extent (CARLA-local metres) and the inward staging-ring width reserved
        at its edge for traffic entry/exit. Written by the digital-twin build for every height-align
        mode; read back by get_staging_bounds. Returns True on success."""
        return bool(_sync(self._client.SetStagingBoundsAsync(
            float(min_x), float(min_y), float(max_x), float(max_y), float(margin))))

    def get_staging_bounds(self):
        """Boundary-aware-traffic staging bounds, or None for a world that was loaded rather than
        built from an OSM area. Returns a dict: {min_x, min_y, max_x, max_y, margin} in CARLA-local
        metres — the sandbox extent plus the inward staging-ring width. The scene perimeter (region
        of interest) is these bounds inset by `margin`; the staging ring is between the perimeter
        and the bounds."""
        vals = _sync(self._client.GetStagingBoundsAsync())
        if vals is None or vals.Count < 5:
            return None
        return {"min_x": float(vals[0]), "min_y": float(vals[1]),
                "max_x": float(vals[2]), "max_y": float(vals[3]), "margin": float(vals[4])}

    def get_cesium_origin(self):
        """Cesium georeference origin as (latitude, longitude, height_m). The true elevation
        of a local point at Unreal Z is height_m + Z."""
        g = _sync(self._client.GetCesiumOriginAsync())
        return (g.Latitude, g.Longitude, g.Altitude)

    def ground_z_below(self, x, y, z, search=4000.0):
        """Raycast straight down from (x, y, z) metres; return the surface Z (metres) hit
        below, or None if nothing was hit within `search` metres. Used for AGL readout."""
        from CarlaNet.Types.Geom import Location as _L, Vector3D as _V
        res = _sync(self._client.ProjectPointAsync(
            _L(float(x), float(y), float(z)), _V(0.0, 0.0, -1.0), float(search)))
        # res is a C# (bool Hit, LabelledPoint Point) value tuple.
        hit = res.Item1 if hasattr(res, "Item1") else res[0]
        pt = res.Item2 if hasattr(res, "Item2") else res[1]
        if not hit:
            return None
        return float(pt.Location.Z)

    def drape_ground_elevation(self, x, y):
        """Ground-surface elevation (ELLIPSOIDAL metres) under CARLA-local (x, y) from the drape
        terrain, or None when there is no active drape or (x, y) is outside the drape grid (e.g.
        beyond the OSM sandbox). A non-physics grid lookup — independent of Cesium streaming/LOD,
        unlike ground_z_below's raycast — so it stays valid at any altitude. For AGL, compute
        (origin_height + local_z) - drape_ground_elevation(x, y)."""
        v = self._client.SampleDrapeGroundElevation(float(x), float(y))
        return None if v is None else float(v)

    def sample_terrain_heights(self, points, timeout=120.0, selector=""):
        """Sample Cesium terrain heights. `points` is an iterable of (lat, lon[, alt])
        tuples / objects / GeoLocation. `selector` picks the layer to sample ('ground' =
        bare-earth World Terrain; '' = first tileset). Returns a list of (latitude, longitude,
        height) tuples; height is float('nan') where the tileset had no ground at that point."""
        from System import TimeSpan
        from System.Threading import CancellationToken
        cs_points = _cs_list([_to_cs_geo(p) for p in points], GeoLocation)
        results = _sync(self._client.SampleTerrainHeightsAsync(
            cs_points,
            str(selector),
            TimeSpan.FromSeconds(float(timeout)),
            TimeSpan.FromMilliseconds(50),
            CancellationToken(False)))
        return [(r.Latitude, r.Longitude, r.Altitude) for r in results]

    def _bare_earth_dtm_table(self):
        """Lazily build + cache the per-road-point bare-earth DTM table the last world build
        persisted on the C# client (LastGroundDtmSamples). Returns (lats, lons, alts) — numpy
        arrays when numpy is available, else parallel Python lists — or None when no elevated
        world was built (legacy/plain worlds). Pure local data: no Cesium sampling, 5 Hz-safe."""
        try:
            cs = self._client.LastGroundDtmSamples
            n = int(cs.Count)
        except Exception:
            return None
        if n == 0:
            return None
        cache = getattr(self, "_dtm_table_cache", None)
        if cache is not None and cache[0] == n:
            return cache[1]
        lats = [0.0] * n; lons = [0.0] * n; alts = [0.0] * n
        for i in range(n):
            s = cs[i]
            lats[i] = float(s.Latitude); lons[i] = float(s.Longitude); alts[i] = float(s.Altitude)
        try:
            import numpy as _np
            table = (_np.asarray(lats), _np.asarray(lons), _np.asarray(alts))
        except Exception:
            table = (lats, lons, alts)
        self._dtm_table_cache = (n, table)
        return table

    def _drape_grid(self):
        """Lazily build + cache the per-cell grids the last drape build stored on the C# client: the
        OFFSET field (draped-surface height minus bare-earth height) and the bare-earth ground height,
        as numpy 2-D arrays (row-major [row,col], ellipsoidal m) plus metadata (minx, miny, cell, nc,
        nr). Bulk float32 buffers are read via np.frombuffer (no per-element pythonnet marshalling).
        Returns None when the last build wasn't 'drape' mode (then the simpler path is used). Pure
        local data, 5 Hz-safe."""
        try:
            if not bool(self._client.LastDrapeActive):
                return None
            nc = int(self._client.LastDrapeNumCols); nr = int(self._client.LastDrapeNumRows)
        except Exception:
            return None
        if nc < 2 or nr < 2:
            return None
        cache = getattr(self, "_drape_grid_cache", None)
        if cache is not None and cache[0] == (nc, nr):
            return cache[1]
        try:
            import numpy as _np
            off = _np.frombuffer(bytes(self._client.LastDrapedOffsetBytes), dtype="<f4").reshape(nr, nc)
            dtm = _np.frombuffer(bytes(self._client.LastDrapedDtmBytes), dtype="<f4").reshape(nr, nc)
        except Exception:
            return None
        meta = {"minx": float(self._client.LastDrapeMinX), "miny": float(self._client.LastDrapeMinY),
                "cell": float(self._client.LastDrapeCellSize), "nc": nc, "nr": nr, "off": off, "dtm": dtm}
        self._drape_grid_cache = ((nc, nr), meta)
        return meta

    @staticmethod
    def _drape_surf(grid2d, meta, x, y):
        """Sample a cached drape grid (row-major [row,col]) at CARLA world (x, y) m using the SAME
        per-cell TRIANGULATION as Chaos::FHeightField, so the telemetry lookup matches the physics
        surface a vehicle actually rests on (bilinear would disagree on steep cells -> spurious
        negative 'pivot' off-road). Edge-clamped, O(1). FHeightField splits each cell on the v00-v11
        diagonal: triangle (v00,v01,v11) for ty<=tx, triangle (v00,v11,v10) for ty>tx."""
        nc = meta["nc"]; nr = meta["nr"]; cell = meta["cell"]
        fc = min(max((x - meta["minx"]) / cell, 0.0), nc - 1.0)
        fr = min(max((y - meta["miny"]) / cell, 0.0), nr - 1.0)
        c0 = int(fc); r0 = int(fr); c1 = min(c0 + 1, nc - 1); r1 = min(r0 + 1, nr - 1)
        tx = fc - c0; ty = fr - r0
        v00 = float(grid2d[r0, c0]); v01 = float(grid2d[r0, c1])
        v10 = float(grid2d[r1, c0]); v11 = float(grid2d[r1, c1])
        if ty <= tx:   # lower-right triangle (v00, v01, v11)
            return v00 + (v01 - v00) * tx + (v11 - v01) * ty
        else:          # upper-left triangle (v00, v11, v10)
            return v00 + (v11 - v10) * tx + (v10 - v00) * ty

    def get_vehicle_telemetry(self, origin=None):
        """Pull per-vehicle TRUTH telemetry for every vehicle in the world as plain dicts (the
        09_Telemetry_CoT_Contract field set). Cheap — positions/velocities are world-observer cache
        reads; one get_actors() RPC per call refreshes the vehicle set. `origin` is an optional
        (lat, lon, height_m) WGS84 tuple for the local->geodetic transform; if omitted it is fetched
        once via get_cesium_origin() (pass it in from a 5 Hz loop to avoid the per-tick RPC).

        `hae` is the vehicle's BARE-EARTH ellipsoidal-WGS84 altitude (the true ground datum),
        independent of how the vehicle was raised/lowered to sit on the photoreal imagery. The
        height-align modes that seat vehicles on the photoreal shift the whole drivable surface, so
        the vehicle's physical altitude is biased toward the photoreal; this method removes that bias
        and reports the real ground height instead:
          - constant-offset modes ('area'/'origin'): the surface is shifted by one fixed amount, so
            subtract it: hae = physical_altitude - that_offset (0 for 'none', i.e. no change).
          - point-by-point mode ('drape'): the shift varies across the map, so subtract the per-cell
            value looked up at the vehicle's position from the cached offset grid.
        Either way `hae` keeps the vehicle pivot (the actor origin sits ~CG/base above the surface),
        and `hae_dtm` is the bare-earth ground height at the vehicle (no pivot), so hae - hae_dtm is
        roughly the pivot. No live Cesium sampling happens here (it has multi-tick latency) — all data
        is read from grids cached at world-build time. `lat`/`lon` are always exact and untouched.

        Each dict: id, type_id, base_type, special_type, color, role_name, lat, lon, hae, hae_dtm,
        speed_mps, course_deg, vx, vy, vz, length_m, width_m, height_m. Heights are ELLIPSOIDAL
        WGS84 (HAE)."""
        if _CARLANET_RECORDING_AVAILABLE:
            # Single source of truth: the C# VehicleTelemetryService (also used by the native recorder).
            return self._vehicle_telemetry_native(origin)
        import math as _m
        from CarlaNet.Types.Geom import Geodesy, GeoLocation
        if origin is None:
            origin = self.get_cesium_origin()                      # (lat, lon, height_m)
        cs_origin = GeoLocation(float(origin[0]), float(origin[1]), float(origin[2]))
        try:
            offset = float(self._client.LastHeightAlignOffset)
        except Exception:
            offset = 0.0
        drape = self._drape_grid()                       # per-cell offset/ground grids (or None)
        table = None if drape is not None else self._bare_earth_dtm_table()
        out = []
        for v in self.get_actors().filter("vehicle.*"):
            tf = v.get_transform()
            vel = v.get_velocity()
            loc = tf.location
            geo = Geodesy.CarlaLocalToGeodetic(cs_origin, float(loc.x), float(loc.y), float(loc.z))
            physical_hae = float(geo.Altitude)
            if drape is not None:
                # 'drape' mode: the surface shift varies per location. Subtract the per-cell offset
                # looked up at the vehicle's position -> bare-earth ground height + vehicle pivot.
                off_local = self._drape_surf(drape["off"], drape, float(loc.x), float(loc.y))
                hae = physical_hae - off_local
                dtm_at_veh = self._drape_surf(drape["dtm"], drape, float(loc.x), float(loc.y))
            else:
                # constant-offset modes ('area'/'origin'): the whole surface is shifted by one fixed
                # amount, so subtract it (offset is 0 for 'none' -> no change).
                hae = physical_hae - offset
                dtm_at_veh, _ = self._nearest_dtm(table, geo.Latitude, geo.Longitude)
            vx, vy, vz = float(vel.x), float(vel.y), float(vel.z)
            speed = _m.hypot(vx, vy)
            # Course over ground, degrees true north (CARLA +X=East, -Y=North); yaw fallback ~stopped.
            if speed >= 0.5:
                course = _m.degrees(_m.atan2(vx, -vy)) % 360.0
            else:
                yaw = _m.radians(tf.rotation.yaw)
                course = _m.degrees(_m.atan2(_m.cos(yaw), -_m.sin(yaw))) % 360.0
            attrs = v.attributes
            ext = v.bounding_box.extent
            base = attrs.get("base_type", "") or (
                "motorcycle" if attrs.get("number_of_wheels", "4") == "2" else "car")
            out.append({
                "id": v.id, "type_id": v.type_id,
                "base_type": base, "special_type": attrs.get("special_type", ""),
                "color": attrs.get("color", ""), "role_name": attrs.get("role_name", ""),
                "lat": geo.Latitude, "lon": geo.Longitude, "hae": hae, "hae_dtm": dtm_at_veh,
                "speed_mps": speed, "course_deg": course,
                "vx": vx, "vy": vy, "vz": vz,
                "length_m": 2.0 * ext.x, "width_m": 2.0 * ext.y, "height_m": 2.0 * ext.z,
            })
        return out

    def _vehicle_telemetry_native(self, origin=None):
        """get_vehicle_telemetry via the C# VehicleTelemetryService — the single source of truth shared
        with the native recorder. Returns the same dict shape as the in-Python path."""
        from CarlaNet.Types.Geom import GeoLocation
        from CarlaNet.Recording import VehicleTelemetryService
        svc = getattr(self, "_telemetry_service", None)
        if svc is None:
            svc = VehicleTelemetryService(self._client)
            self._telemetry_service = svc
        if origin is None:
            origin = self.get_cesium_origin()
        cs_origin = GeoLocation(float(origin[0]), float(origin[1]), float(origin[2]))
        out = []
        for r in svc.Compute(cs_origin):
            out.append({
                "id": int(r.Id), "type_id": r.TypeId,
                "base_type": r.BaseType, "special_type": r.SpecialType,
                "color": r.Color, "role_name": r.RoleName,
                "lat": float(r.Lat), "lon": float(r.Lon),
                "hae": float(r.Hae), "hae_dtm": float(r.HaeDtm),
                "speed_mps": float(r.SpeedMps), "course_deg": float(r.CourseDeg),
                "vx": float(r.Vx), "vy": float(r.Vy), "vz": float(r.Vz),
                "length_m": float(r.LengthM), "width_m": float(r.WidthM), "height_m": float(r.HeightM),
            })
        return out

    def start_recording(self, camera, record_dir, hz=2.0, affiliation="n", stale=3.0,
                        fov=90.0, platform_type="uas-fixed", platform_affiliation="f",
                        platform_callsign="OVERWATCH", platform_uid=None, distortion="none",
                        run_id=None, scenario_id=None, seed=None):
        """Start native (C#) recording of `camera`'s imagery to `record_dir`: every 1/hz seconds a
        lossless PNG of the clean frame + a paired CoT-XML telemetry sidecar, encoded on the .NET thread
        pool (no Python/GIL in the hot path). Returns the FrameRecorder, or None if unavailable.

        The collection platform (the airborne camera) is recorded as a CoT air track: `fov` is the camera
        horizontal field of view (degrees, for the sensor field-of-view and pinhole intrinsics);
        `platform_type` is an airframe class ('uas-fixed', 'uas-rotary', 'manned-fixed', 'manned-rotary')
        or a raw CoT type string; `platform_affiliation` is the CoT standard identity (default 'f' friend,
        as the platform is our own collection asset); `platform_callsign`/`platform_uid` name the track
        (uid defaults to CARLA-SENSOR-<camera id>); `distortion` describes the lens model ('none' at CARLA
        defaults).

        Every capture records the simulation tick that produced it, so a still and its sidecar are bound
        to a simulation instant rather than to wall-clock time, which does not track the simulation
        clock. `run_id` groups the captures of one execution (generated from the start time when not
        given), `scenario_id` names the scenario driving the run, and `seed` records the integer the run
        was started with so it can be reproduced."""
        if not _CARLANET_RECORDING_AVAILABLE:
            print("native recording unavailable: CarlaNet.Recording assembly not loaded "
                  "(rebuild the wheel/DLLs).", file=sys.stderr)
            return None
        from CarlaNet.Recording import FrameRecorder, SensorPlatformOptions
        self.stop_recording()
        token = camera._actor.StreamToken
        uid = platform_uid or f"CARLA-SENSOR-{camera.id}"
        cot_type = SensorPlatformOptions.ResolveCotType(str(platform_type), str(platform_affiliation))
        opts = SensorPlatformOptions(float(fov), cot_type, str(platform_callsign), str(uid),
                                     "sensor.camera.rgb", str(distortion))
        # Positional through `workers` (0 = default worker count), since the run-identity arguments
        # follow it in the C# signature.
        self._recorder = FrameRecorder(self._client, token, str(record_dir), float(hz),
                                       str(affiliation), float(stale), opts, 0,
                                       None if run_id is None else str(run_id),
                                       None if scenario_id is None else str(scenario_id),
                                       None if seed is None else int(seed))
        return self._recorder

    def start_scenario(self, path, traffic_manager, report=None):
        """Run an ASAM OpenSCENARIO storyboard against the loaded world. Returns the executor, or None
        if unavailable.

        Parsing, entity placement, trigger evaluation and vehicle commands all happen in .NET, driven by
        the world tick. Python starts and stops a scenario and does not participate in its execution, so
        scenario timing does not depend on interpreter scheduling or on round trips through this client.

        `traffic_manager` is the TrafficManager the scenario's vehicles are driven by, as returned by
        `Client.get_trafficmanager()`. `report` receives one line per state change — an act starting, a
        vehicle stopping — and is called from the tick thread, so it must not block."""
        if not _CARLANET_SCENARIO_AVAILABLE:
            print("scenario execution unavailable: CarlaNet.Scenario assembly not loaded "
                  "(rebuild the wheel/DLLs).", file=sys.stderr)
            return None
        from CarlaNet.Scenario import OpenScenarioParser, RoadNetwork, ScenarioExecutor
        from System import Action, String

        self.stop_scenario()
        definition = OpenScenarioParser.LoadFile(str(path))

        # Resolve the storyboard's road-referenced positions against the network the server actually
        # has loaded, rather than against the file the scenario names.
        network = RoadNetwork.FromOpenDrive(_sync(self._client.GetMapDataAsync()))

        native_tm = getattr(traffic_manager, "_tm", None)
        if native_tm is None:
            raise RuntimeError(
                "a working TrafficManager is required to run a scenario; get_trafficmanager() "
                "returned a fallback, so the CarlaNet.Map / CarlaNet.TrafficManager assemblies are "
                "probably missing")

        callback = Action[String](report) if report is not None else None
        self._scenario = ScenarioExecutor(self._client, native_tm, definition, network, callback)
        return self._scenario

    def stop_scenario(self):
        """Stop a running scenario and remove the vehicles it placed."""
        s = getattr(self, "_scenario", None)
        if s is not None:
            try:
                s.Dispose()
            except Exception:
                pass
            self._scenario = None

    def stop_recording(self):
        """Stop native recording (flushes pending captures)."""
        r = getattr(self, "_recorder", None)
        if r is not None:
            try:
                r.Dispose()
            except Exception:
                pass
            self._recorder = None

    @staticmethod
    def _nearest_dtm(table, lat, lon):
        """Nearest bare-earth DTM sample to (lat, lon). Returns (dtm_altitude_m, distance_m), or
        (None, None) when there is no table. Distance is an equirectangular metres approximation
        (samples are dense ~10 m, so nearest-sample is fine)."""
        if table is None:
            return None, None
        import math as _m
        lats, lons, alts = table
        coslat = _m.cos(_m.radians(float(lat)))
        try:
            import numpy as _np
            if isinstance(lats, _np.ndarray):
                dx = (lons - float(lon)) * coslat
                dy = (lats - float(lat))
                i = int(_np.argmin(dx * dx + dy * dy))
                d2 = float(dx[i] * dx[i] + dy[i] * dy[i])
                return float(alts[i]), _m.sqrt(d2) * 111320.0
        except Exception:
            pass
        best_i, best_d2 = 0, float("inf")
        for i in range(len(lats)):
            dx = (lons[i] - float(lon)) * coslat
            dy = (lats[i] - float(lat))
            d2 = dx * dx + dy * dy
            if d2 < best_d2:
                best_d2, best_i = d2, i
        return float(alts[best_i]), _m.sqrt(best_d2) * 111320.0

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
        cs_tf = _to_cs(transform)
        if attach_to is not None:
            parent_id = int(attach_to.id) if isinstance(attach_to, Actor) else int(attach_to)
            at = attachment_type if attachment_type is not None else AttachmentType.Rigid
            cs_actor = _sync(self._client.SpawnActorWithParentAsync(desc, cs_tf, parent_id, at))
        else:
            cs_actor = _sync(self._client.SpawnActorAsync(desc, cs_tf))
        wrapped = _wrap_actor(cs_actor, self._client)
        # WalkerAIController.start() / stop() rely on locating the parent
        # walker actor (controllers are spawned with attach_to=walker per
        # upstream's generate_traffic.py pattern). Persist the attach link
        # on the Python wrapper so the controller can dispatch to it.
        if attach_to is not None:
            wrapped._parent = attach_to if isinstance(attach_to, Actor) else None
        return wrapped

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
        """Re-seed the pedestrian RNG. Mirrors upstream
        World.set_pedestrians_seed → WalkerNavigation::SetPedestriansSeed."""
        try:
            Client._get_walker_navigation().SetPedestriansSeed(int(seed))
        except Exception as ex:
            import sys
            print(f"[carlanet] set_pedestrians_seed failed: {ex}", file=sys.stderr)

    def set_pedestrians_cross_factor(self, percentage: float):
        """Probability [0..1] that a walker chooses to cross a road instead of
        staying on the sidewalk. Mirrors upstream
        World.set_pedestrians_cross_factor → WalkerNavigation::SetPedestriansCrossFactor."""
        try:
            Client._get_walker_navigation().SetPedestriansCrossFactor(float(percentage))
        except Exception as ex:
            import sys
            print(f"[carlanet] set_pedestrians_cross_factor failed: {ex}", file=sys.stderr)

    def get_random_location_from_navigation(self):
        """Returns a random reachable Location on the navmesh, or None if
        unavailable (no navmesh / RPC failure / DLL missing)."""
        try:
            wn = Client._get_walker_navigation()
            loc = wn.GetRandomLocationFromNavigation()
            # C# returns Location? (nullable); pythonnet maps None to Python None.
            if loc is None:
                return None
            return Location(loc.x, loc.y, loc.z)
        except Exception as ex:
            import sys
            print(f"[carlanet] get_random_location_from_navigation failed: {ex}", file=sys.stderr)
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
        # Stash the C# client reference so Client._get_walker_navigation()
        # (a @staticmethod by upstream contract — see TM cache below) can
        # find an inner client without scanning. Matches the way the TM
        # cache is keyed off the most-recently-constructed Client.
        Client._last_client_inner = self._inner

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

    def generate_opendrive_world(self, opendrive: str, parameters=None,
                                 reset_settings: bool = True) -> World:
        """Generate a runtime world from OpenDRIVE (.xodr) text.

        Mirrors carla.Client.generate_opendrive_world: copies the .xodr to the
        server then loads the special "OpenDriveMap" episode. When *parameters*
        is None the C# side substitutes the upstream OpendriveGenerationParameters
        defaults.
        """
        if parameters is None:
            _sync(self._inner.GenerateOpenDriveWorldAsync(
                opendrive, _default_opendrive_params(), reset_settings))
        else:
            _sync(self._inner.GenerateOpenDriveWorldAsync(
                opendrive, parameters, reset_settings))
        return World(self._inner)

    def generate_world_from_osm(self, osm_path: str, osm_options=None,
                                parameters=None, reset_settings: bool = True) -> World:
        """Drop an .osm file and fabricate the level at runtime.

        Converts OSM→OpenDRIVE via the native SUMO netconvert (CarlaNet.Map),
        then generates the OpenDRIVE world. *osm_options* may be a
        CarlaNet.Map.OsmConversionOptions (e.g. with OriginLatitude/OriginLongitude
        set to pin a world origin); when None, CARLA osm2odr defaults are used.
        *parameters* mirrors generate_opendrive_world.
        """
        params = _default_osm_opendrive_params() if parameters is None else parameters
        _sync(self._inner.GenerateWorldFromOsmAsync(
            osm_path, osm_options, params, reset_settings))
        return World(self._inner)

    def generate_world_from_osm_with_elevation(self, osm_path, ion_token, ion_asset_id,
                                               ground_ion_asset_id=1,
                                               osm_options=None, parameters=None,
                                               sample_step_meters=10.0,
                                               origin_height=None,
                                               outlier_threshold=4.0,
                                               height_align="none",
                                               ground_collision=True,
                                               cesium_settle_seconds=5.0,
                                               terrain_res=2.0,
                                               terrain_margin=30.48,
                                               drape_chunk_cells=64,
                                               drape_max_drape=5.0,
                                               drape_cache_dir=None):
        """Full headless digital-twin build (no editor): OSM -> elevated, Cesium-aligned
        OpenDRIVE world. Converts OSM->.xodr, samples Cesium terrain heights at the road
        reference line, injects them into the .xodr <elevationProfile>, generates the
        elevated world, and re-establishes the Cesium visual overlay. Returns the elevated
        .xodr string; call client.get_world() afterwards for the World.

        Requires ion_token + ion_asset_id (the photoreal imagery layer, spawned at runtime).
        ground_ion_asset_id (>0, default 1 = Cesium World Terrain) is the hidden bare-earth terrain
        (no buildings/trees) whose heights set the road elevations; pass 0 to take heights from the
        photoreal surface instead (legacy).

        height_align controls how the roads and drivable ground are matched to the photoreal imagery:
          "none" (default): leave them on the bare-earth terrain. Road and ground coincide, vehicles
            never float, and the whole drivable surface sits ~sub-meter above the photoreal (so it's
            invisible from high/nadir views).
          "area"/"origin": raise/lower the road AND the drivable ground by ONE constant height so
            vehicles sit on the photoreal — "area" uses the median photoreal-vs-ground gap over the
            map, "origin" uses the gap at the map origin. (Good on flat ground; a single height can't
            track hills, where the gap varies.)
          "drape": match the photoreal POINT-BY-POINT — sample the photoreal and bare-earth heights
            on a grid over the whole map area, clean it up (open ground follows the photoreal;
            buildings/tree-canopy fall back to bare earth so the surface never climbs onto rooftops),
            build a hidden collision surface vehicles drive on everywhere (on- AND off-road), and
            conform the roads to it. Vehicles seat on the photoreal across the whole map.
        In every mode the reported telemetry altitude stays true bare-earth (the matching shift is
        removed from what telemetry reports).

        ground_collision (default True) keeps the bare-earth ground collidable for off-road safety
        (under "drape" the draped surface owns collision, so the bare-earth tileset's collision is
        turned off). terrain_res = drape grid spacing in metres (smaller hugs the photoreal more
        closely but is slower; default 2.0); terrain_margin = how far past the map's edge to extend
        the drivable ground (m, default ~100 ft, enough for long vehicles near the boundary);
        drape_cache_dir caches the (slow) drape sampling per area so rebuilds are fast. osm_options
        should pin the origin; if origin_height is None the height sampled at the origin is the datum.
        """
        from System import TimeSpan
        from System.Threading import CancellationToken
        params = _default_osm_opendrive_params() if parameters is None else parameters
        settle = TimeSpan.FromSeconds(float(cesium_settle_seconds)) if cesium_settle_seconds else TimeSpan(0)
        oh = None if origin_height is None else float(origin_height)
        xodr = _sync(self._inner.GenerateWorldFromOsmWithElevationAsync(
            osm_path, str(ion_token), int(ion_asset_id), int(ground_ion_asset_id),
            osm_options, params, float(sample_step_meters), oh,
            float(outlier_threshold), str(height_align), bool(ground_collision), settle,
            float(terrain_res), float(terrain_margin), int(drape_chunk_cells),
            float(drape_max_drape), (None if drape_cache_dir is None else str(drape_cache_dir)),
            CancellationToken(False)))
        return str(xodr)

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

    # ── Walker navigation helper (Wave 5) ─────────────────────────────────────
    # Process-wide singleton matching the _tm_cache pattern above. First
    # access fetches the navmesh via the most-recently-created Client's
    # inner CarlaClient and constructs a WalkerNavigation via the C# factory.
    # On any failure (assemblies missing, RPC error, malformed blob) returns
    # _NoOpWalkerNavigation so user scripts keep running without crashing.
    _walker_nav_cache = None
    _last_client_inner = None  # set in Client.__init__

    @staticmethod
    def _get_walker_navigation():
        """Return the process-wide WalkerNavigation; lazily fetches the navmesh.

        Mirrors the lazy-with-fallback pattern of get_trafficmanager().
        """
        if Client._walker_nav_cache is not None:
            return Client._walker_nav_cache

        if not _CARLANET_NAV_AVAILABLE or _CSNavigationFactory is None:
            import sys
            print("[carlanet] CarlaNet.Nav assemblies unavailable; "
                  "WalkerAIController will be a no-op stub.", file=sys.stderr)
            Client._walker_nav_cache = _NoOpWalkerNavigation()
            return Client._walker_nav_cache

        inner = Client._last_client_inner
        if inner is None:
            import sys
            print("[carlanet] WalkerNavigation requested before any Client was "
                  "constructed; returning no-op stub.", file=sys.stderr)
            Client._walker_nav_cache = _NoOpWalkerNavigation()
            return Client._walker_nav_cache

        try:
            wn = _CSNavigationFactory.GetOrCreate(inner, None)
            Client._walker_nav_cache = wn
            return wn
        except Exception as ex:
            import sys
            print(f"[carlanet] WalkerNavigation unavailable: {ex}", file=sys.stderr)
            Client._walker_nav_cache = _NoOpWalkerNavigation()
            return Client._walker_nav_cache

    # ── TM registration helpers ───────────────────────────────────────────────
    @staticmethod
    def _tm_register_actors(actors, tm_port: int, enabled: bool):
        """Register/unregister actors with the cached TM on tm_port (no-op if none).

        Takes the actor records themselves, so the TM does not have to ask the simulator for them
        again. That round trip returns nothing for a vehicle spawned in the same frame — the
        simulator has not published it yet — and the registration is then dropped, leaving a vehicle
        that is never driven and never says so.
        """
        cs_tm = Client._tm_for_port(tm_port)
        if cs_tm is None:
            return
        from System.Collections.Generic import List as _List
        cs_actors = _List[_Actor]()
        for a in actors:
            cs_actors.Add(a)
        try:
            if enabled:
                cs_tm.RegisterVehicles(cs_actors)
            else:
                cs_tm.UnregisterVehicles(cs_actors)
        except Exception as ex:
            import sys
            print(f"traffic-manager registration failed: {ex!r}", file=sys.stderr)

    @staticmethod
    def _tm_for_port(tm_port: int):
        """The C# TrafficManager cached for this port, or None if nothing is using it."""
        cache = getattr(Client, "_tm_cache", None)
        if not cache:
            return None
        tm = cache.get(int(tm_port))
        return None if tm is None else getattr(tm, "_tm", None)

    @staticmethod
    def _tm_register_ids(actor_ids, tm_port: int, enabled: bool):
        """Register/unregister actor IDs with the cached TM on tm_port (no-op if none).

        Only for callers that have an id and nothing else; prefer _tm_register_actors."""
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
        cs_tf = sensor_frame.SensorTransform
        self.transform = Transform(cs_tf.location, cs_tf.rotation)


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
            self.normal_impulse = _as_vector3d(data.NormalImpulse)
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
            self.accelerometer = _as_vector3d(d.Accelerometer)
            self.gyroscope = _as_vector3d(d.Gyroscope)
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
        prim = PointPrimitive(_to_cs(location), float(size))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_line(self, begin, end, thickness: float = 0.1, color=None,
                  life_time: float = -1.0, persistent_lines: bool = True):
        prim = LinePrimitive(_to_cs(begin), _to_cs(end), float(thickness))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_arrow(self, begin, end, thickness: float = 0.1, arrow_size: float = 0.1,
                   color=None, life_time: float = -1.0, persistent_lines: bool = True):
        line = LinePrimitive(_to_cs(begin), _to_cs(end), float(thickness))
        prim = ArrowPrimitive(line, float(arrow_size))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_box(self, box, rotation, thickness: float = 0.1, color=None,
                 life_time: float = -1.0, persistent_lines: bool = True):
        prim = BoxPrimitive(_to_cs(box), _to_cs(rotation), float(thickness))
        shape = DebugShape(prim, _to_cs_color(color), float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))

    def draw_string(self, location, text: str, draw_shadow: bool = False,
                    color=None, life_time: float = -1.0, persistent_lines: bool = True):
        prim = StringPrimitive(_to_cs(location), str(text), bool(draw_shadow))
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
