"""The typed description of one camera channel, and the one place its defaults are defined.

Field names and defaults follow the per-channel camera-rig rows of
`Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/12_Operator_Control_Surface.md` section 5.2. A
front end that offers one of these fields reads its default from here (`default_of`) rather than
writing the number again, because a default written twice is a default that drifts (the same
document's section 1.5 measured three that already had).
"""

from __future__ import annotations

import math
import re
from dataclasses import MISSING, dataclass, fields
from typing import Any, ClassVar


@dataclass(frozen=True)
class ChannelDescription:
    """One camera channel: who it is, how it moves, what it sees.

    **Patterns.** `stare` holds one pose for the whole session; `orbit` flies a circle at a fixed
    height above a centre with the boresight held on that centre, run by `OrbitSensorController`.
    A transit pattern is not built and is refused.

    **Declaring a stare.** Exactly one of two forms:

    * *aimed at a point* -- `stare_look_at_x_m` and `stare_look_at_y_m` (with `stare_look_at_z_m`,
      default 0.0) name the point the boresight passes through, in CARLA's frame: metres, x east,
      y south, so north is -y. The camera stands `stare_standoff_m` back from that point, opposite
      `stare_bearing_deg`, and `stare_altitude_m` above it. The bearing is the compass direction the
      camera looks along, in degrees clockwise from north. A standoff of 0 looks straight down, with
      the bearing at the top of the picture. `StareAim` turns this into a pose.
    * *an explicit pose* -- `stare_x_m`, `stare_y_m`, `stare_z_m`, `stare_pitch_deg` and
      `stare_yaw_deg`, all five, in CARLA's frame and CARLA's angle convention (yaw 0 faces east,
      -90 faces north; negative pitch looks down). This is the form for a view found by flying
      there first.

    **Declaring an orbit.** `orbit_centre_x_m` and `orbit_centre_y_m` are required, in the same
    frame; `orbit_centre_z_m` (default 0.0) is the height the orbit's altitude is measured from.

    A field that the chosen pattern would ignore -- a stare's point or pose under an orbit, an
    orbit's centre under a stare -- is refused rather than silently dropped. Only fields with no
    default can be recognised as supplied, so a defaulted field such as `orbit_radius_m` is simply
    unused by a stare.

    **What is not here yet.** Section 5.2 also lists `capture_rgb`, `capture_depth`,
    `capture_segmentation`, `depth_max_range_m`, `sensor_tick` and `post_process_profile` per
    channel. They belong with the recorder and the depth camera, and are added when `SensorRig`
    and `run_SCTMV.py`'s argument parser are made to build their rig from this class -- work that
    belongs to the operator control surface (section 9.2 of the same document) and has not been
    done. Until then `SensorRig` still reads its own `args`, and nothing here changes what
    `run_SCTMV.py` does. The one consumer today is `CameraFollower`.

    Raises:
        ValueError: when the description is invalid. Every problem found is named in the one
            message, so a caller fixes them together rather than one per attempt.
    """

    STARE: ClassVar[str] = "stare"
    ORBIT: ClassVar[str] = "orbit"
    PATTERNS: ClassVar[tuple[str, ...]] = (STARE, ORBIT)

    # The grammar 04_Contracts.md section 6.3 gives an authored sensor_id.
    SENSOR_ID_GRAMMAR: ClassVar[re.Pattern[str]] = re.compile(r"[A-Za-z0-9_.:-]{1,63}")

    sensor_id: str | None = None
    pattern: str = STARE
    fov: float = 90.0
    width: int = 1280
    height: int = 720

    orbit_centre_x_m: float | None = None
    orbit_centre_y_m: float | None = None
    orbit_centre_z_m: float = 0.0
    orbit_radius_m: float = 200.0
    orbit_altitude_m: float = 518.2
    orbit_period_s: float = 240.0

    stare_look_at_x_m: float | None = None
    stare_look_at_y_m: float | None = None
    stare_look_at_z_m: float = 0.0
    stare_altitude_m: float = 304.8
    stare_standoff_m: float = 0.0
    stare_bearing_deg: float = 0.0

    stare_x_m: float | None = None
    stare_y_m: float | None = None
    stare_z_m: float | None = None
    stare_pitch_deg: float | None = None
    stare_yaw_deg: float | None = None

    ORBIT_CENTRE_FIELDS: ClassVar[tuple[str, ...]] = ("orbit_centre_x_m", "orbit_centre_y_m")
    STARE_LOOK_AT_FIELDS: ClassVar[tuple[str, ...]] = ("stare_look_at_x_m", "stare_look_at_y_m")
    STARE_POSE_FIELDS: ClassVar[tuple[str, ...]] = (
        "stare_x_m", "stare_y_m", "stare_z_m", "stare_pitch_deg", "stare_yaw_deg",
    )
    INTEGER_FIELDS: ClassVar[tuple[str, ...]] = ("width", "height")

    def __post_init__(self) -> None:
        problems = self._problems()
        if problems:
            raise ValueError("channel description refused: " + "; ".join(problems))

    @classmethod
    def default_of(cls, name: str) -> Any:
        """The default this class gives field `name`, or None where the field has no default.

        This is what a front end's `--help` and its parser read, so the number exists once.

        Raises:
            KeyError: when `name` is not a field of this class.
        """
        for field in fields(cls):
            if field.name == name:
                return None if field.default is MISSING else field.default
        raise KeyError(name)

    @classmethod
    def field_names(cls) -> tuple[str, ...]:
        """Every field, in declaration order."""
        return tuple(field.name for field in fields(cls))

    def declares_look_at(self) -> bool:
        """Whether a look-at point was supplied (either coordinate counts as supplied)."""
        return any(getattr(self, name) is not None for name in self.STARE_LOOK_AT_FIELDS)

    def declares_pose(self) -> bool:
        """Whether any part of an explicit stare pose was supplied."""
        return any(getattr(self, name) is not None for name in self.STARE_POSE_FIELDS)

    def declares_orbit_centre(self) -> bool:
        """Whether any part of an orbit centre was supplied."""
        return any(getattr(self, name) is not None for name in self.ORBIT_CENTRE_FIELDS)

    def _problems(self) -> list[str]:
        """Every reason this description cannot be used, in field order; empty when it can."""
        problems = self._type_problems()
        if problems:
            # Range and shape checks on a value of the wrong type would only add noise.
            return problems

        if self.sensor_id is not None and not self.SENSOR_ID_GRAMMAR.fullmatch(self.sensor_id):
            problems.append(
                f"sensor_id {self.sensor_id!r} must be 1 to 63 characters from A-Z a-z 0-9 _ . : -")
        if self.pattern not in self.PATTERNS:
            if self.pattern == "transit":
                problems.append("pattern 'transit' is not built; use 'stare' or 'orbit'")
            else:
                problems.append(f"pattern {self.pattern!r} is not one of 'stare', 'orbit'")
        if not 0.0 < self.fov < 180.0:
            problems.append(f"fov must be above 0 and below 180 degrees, got {self.fov}")
        for name in self.INTEGER_FIELDS:
            if getattr(self, name) <= 0:
                problems.append(
                    f"{name} must be a positive number of pixels, got {getattr(self, name)}")
        for name in ("orbit_radius_m", "orbit_altitude_m", "orbit_period_s", "stare_altitude_m"):
            if getattr(self, name) <= 0.0:
                problems.append(f"{name} must be above 0, got {getattr(self, name)}")
        if self.stare_standoff_m < 0.0:
            problems.append(f"stare_standoff_m must not be negative, got {self.stare_standoff_m}")
        if self.stare_pitch_deg is not None and not -90.0 <= self.stare_pitch_deg <= 90.0:
            problems.append(f"stare_pitch_deg must lie in -90..90, got {self.stare_pitch_deg}")

        if self.pattern == self.STARE:
            problems.extend(self._stare_problems())
        elif self.pattern == self.ORBIT:
            problems.extend(self._orbit_problems())
        return problems

    def _type_problems(self) -> list[str]:
        """Values of the wrong kind: text where a number belongs, a flag where a count belongs."""
        problems: list[str] = []
        if self.sensor_id is not None and not isinstance(self.sensor_id, str):
            problems.append(f"sensor_id must be text, got {type(self.sensor_id).__name__}")
        if not isinstance(self.pattern, str):
            problems.append(f"pattern must be text, got {type(self.pattern).__name__}")
        for name in self.field_names():
            if name in ("sensor_id", "pattern"):
                continue
            value = getattr(self, name)
            if value is None and self.default_of(name) is None:
                continue
            if name in self.INTEGER_FIELDS:
                if isinstance(value, bool) or not isinstance(value, int):
                    problems.append(f"{name} must be a whole number of pixels, got {value!r}")
                continue
            if isinstance(value, bool) or not isinstance(value, int | float):
                problems.append(f"{name} must be a number, got {value!r}")
            elif not math.isfinite(value):
                problems.append(f"{name} must be finite, got {value!r}")
        return problems

    def _stare_problems(self) -> list[str]:
        problems: list[str] = []
        look_at = self.declares_look_at()
        pose = self.declares_pose()
        if self.declares_orbit_centre():
            problems.append(
                "an orbit centre was given for a stare; set pattern to 'orbit' or drop it")
        if look_at and pose:
            problems.append("a stare takes a look-at point or an explicit pose, not both")
        elif not look_at and not pose:
            problems.append(
                "a stare needs somewhere to look: give stare_look_at_x_m and stare_look_at_y_m, "
                "or all of stare_x_m, stare_y_m, stare_z_m, stare_pitch_deg, stare_yaw_deg")
        missing_look_at = [n for n in self.STARE_LOOK_AT_FIELDS if getattr(self, n) is None]
        if look_at and missing_look_at:
            problems.append(
                f"a look-at point needs both coordinates; missing {', '.join(missing_look_at)}")
        missing_pose = [n for n in self.STARE_POSE_FIELDS if getattr(self, n) is None]
        if pose and missing_pose:
            problems.append(
                f"an explicit stare pose needs all five values; missing {', '.join(missing_pose)}")
        return problems

    def _orbit_problems(self) -> list[str]:
        problems: list[str] = []
        if self.declares_look_at() or self.declares_pose():
            problems.append("a stare look-at point or pose was given for an orbit; "
                            "set pattern to 'stare' or drop it")
        missing = [n for n in self.ORBIT_CENTRE_FIELDS if getattr(self, n) is None]
        if missing:
            problems.append(f"an orbit needs a centre; missing {', '.join(missing)}")
        return problems
