"""CarlaControl - High-level control and automation tools for CARLA simulator.

This package provides advanced control systems, automation utilities, and
high-level interfaces for working with the CARLA simulator through CarlaNet.
"""

from carlacontrol.CotUdpEmitter import CotUdpEmitter
from carlacontrol.NativeRecorder import NativeRecorder
from carlacontrol.Pose import Pose
from carlacontrol.PyGameSensorController import PyGameSensorController
from carlacontrol.PygameInterface import PygameInterface
from carlacontrol.CarlaControlArgumentParser import CarlaControlArgumentParser
from carlacontrol.SensorController import SensorController
from carlacontrol.SensorRig import SensorRig
from carlacontrol.TelemetryController import TelemetryController
from carlacontrol.TrafficController import TrafficController
from carlacontrol.WorldBuilder import WorldBuilder
from carlacontrol.version import __version__

__author__ = "SNC Team"

__all__ = [
    "__version__",
    "CarlaControlArgumentParser",
    "CotUdpEmitter",
    "NativeRecorder",
    "Pose",
    "PyGameSensorController",
    "PygameInterface",
    "SensorController",
    "SensorRig",
    "TelemetryController",
    "TrafficController",
    "WorldBuilder",
]
