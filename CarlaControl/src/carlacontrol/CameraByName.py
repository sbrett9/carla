"""The live camera in a world that holds a name, found by asking the server only about actors it has not
examined before."""
from __future__ import annotations

# Every camera blueprint's id begins with this: RGB, depth and the segmentation cameras alike.
CAMERA_PREFIX = "sensor.camera."


class CameraByName:
    """Finds the live camera that holds one name, compared without regard to case, as the server compares
    camera names when it refuses a duplicate.

    Any client may have spawned the camera: a carla-camera-follower under its `--sensor-id`, or a drive
    under its `--camera-name`. The name is used as given, so the server's own `Camera_<n>` can be looked
    for too.

    Each look reads the actor ids of the world's latest snapshot, which costs no round trip, and asks
    the server once for the actors among them it has not examined before. An actor examined once is
    never asked about again, so a run that looks after every step asks only about the actors spawned
    since its last look. A camera that leaves the world is not found again; one spawned under the same
    name afterwards has a new id, and is.
    """

    def __init__(self, world, name: str) -> None:
        self.world = world
        self.name = name
        self.examined: set[int] = set()

    def find(self):
        """The live camera holding the name among the actors not examined before, or None."""
        new = self.world.get_actor_ids() - self.examined
        if not new:
            return None
        self.examined |= new
        wanted = self.name.lower()
        for actor in self.world.get_actors(sorted(new)):
            if actor.type_id.startswith(CAMERA_PREFIX) and self.world.camera_name(actor).lower() == wanted:
                return actor
        return None
