"""Whether a SUMO drive's render region takes in the whole world it drives, and the region that would."""

from __future__ import annotations

import math
from dataclasses import dataclass


@dataclass(frozen=True)
class RenderRegionCoverage:
    """A world's extent in SUMO's frame, measured against a drive's circular render region.

    A drive limited to a circle -- `--render-set circle`, an optional performance control; by default
    every vehicle SUMO has is drawn -- renders vehicles only inside it (`--region-x`, `--region-y`,
    `--region-radius`, in SUMO's projected metres), so a camera that stays over the circle sees the
    traffic and one flown beyond it sees roads with nothing on them, while SUMO goes on simulating
    the vehicles it is not rendering. The extent here is the world package's sandbox -- the OSM area
    the world was built from, the ground the camera can be flown over -- converted to SUMO's frame,
    which is CARLA's with the northing negated. A circle through the sandbox's corners renders
    everywhere a vehicle can be.
    """

    min_x: float
    min_y: float
    max_x: float
    max_y: float

    @classmethod
    def from_manifest(cls, manifest: dict) -> RenderRegionCoverage | None:
        """The extent a world package's `world.json` records, or None where it records none."""
        keys = ("StagingMinXMeters", "StagingMinYMeters", "StagingMaxXMeters", "StagingMaxYMeters")
        if all(isinstance(manifest.get(key), int | float) for key in keys):
            min_x, min_y, max_x, max_y = (float(manifest[key]) for key in keys)
        else:
            grid = ("GridMinXMeters", "GridMinYMeters", "GridCellSizeMeters", "GridNumCols",
                    "GridNumRows")
            if not all(isinstance(manifest.get(key), int | float) for key in grid):
                return None
            min_x, min_y = float(manifest["GridMinXMeters"]), float(manifest["GridMinYMeters"])
            cell = float(manifest["GridCellSizeMeters"])
            max_x = min_x + cell * (int(manifest["GridNumCols"]) - 1)
            max_y = min_y + cell * (int(manifest["GridNumRows"]) - 1)
        if max_x <= min_x or max_y <= min_y:
            return None
        # CARLA's y runs south and SUMO's north.
        return cls(min_x=min_x, min_y=-max_y, max_x=max_x, max_y=-min_y)

    def farthest_m(self, x: float, y: float) -> float:
        """How far the extent's farthest corner is from a point in SUMO's frame, metres."""
        return max(math.hypot(corner_x - x, corner_y - y)
                   for corner_x in (self.min_x, self.max_x)
                   for corner_y in (self.min_y, self.max_y))

    def covers(self, x: float, y: float, radius: float) -> bool:
        """Whether a region of `radius` around (x, y) takes in the whole extent."""
        return self.farthest_m(x, y) <= radius

    def whole_map(self) -> tuple[float, float, float]:
        """The smallest region taking in the whole extent: its centre and radius, whole metres."""
        centre_x = round((self.min_x + self.max_x) / 2.0)
        centre_y = round((self.min_y + self.max_y) / 2.0)
        return float(centre_x), float(centre_y), float(math.ceil(self.farthest_m(centre_x, centre_y)))

    def advice(self, x: float, y: float, radius: float, capacity: int | None) -> str | None:
        """What to tell an operator flying a free camera over a region smaller than the world.

        None where the region already takes in the whole extent.
        """
        if self.covers(x, y, radius):
            return None
        centre_x, centre_y, whole = self.whole_map()
        return (f"the render set is a fixed circle of {radius:g} m around ({x:g}, {y:g}) in SUMO "
                f"meters, and this world reaches {self.farthest_m(x, y):.0f} m from that center: a "
                f"free camera flown outside the circle sees roads with no vehicles on them. To render "
                f"the whole map: --region-x {centre_x:g} --region-y {centre_y:g} --region-radius "
                f"{whole:g}, or leave --render-set at 'all', which draws every vehicle SUMO has"
                + ("" if capacity is None else
                   f". --capacity ({capacity}) still bounds how many vehicles are rendered at once, "
                   "and the admission line logged with each pacing window shows any it sheds"))
