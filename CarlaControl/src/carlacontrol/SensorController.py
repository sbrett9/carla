from abc import ABC, abstractmethod

from .Pose import Pose
import carlanet as carla


class SensorController(ABC):
    def __init__(self, controlled_object):
        self.controlled_object = controlled_object

    @abstractmethod
    def move_object_to_position(self, position : Pose):
        """Move the controlled object to the specified position.

        Args:
            position: Position specification (implementation-defined)
        """
        pass

    @abstractmethod
    def set_object_transform(self, tf : carla.Transform):
        """Set the controlled object's transform.

        Args:
            tf: CARLA Transform object
        """
        pass
