"""The name a camera's recordings are known by, ruled as `CarlaNet.Recording.CameraName` rules it.

The recorder (`CarlaNet.Recording.FrameRecorder`) names every still `<camera name>_<local capture
time>` and writes the name as the callsign of the camera's platform track, and the shim
(`carlanet`) applies the same rule when a camera is spawned under a name. Both are .NET; this is the
rule again for the checks CarlaControl makes before anything reaches them -- `run_capture`'s offline
validation of a channel's `sensor_id`, and the `--camera-name` of `run_SCTMV.py` and
`run_free_move_camera.py` -- so a name that would be refused is refused at the command line rather
than after the world has been set up. The two are held to the same answers by the same cases, in
`CarlaNet.Tests/Recording/CameraNameTests.cs` and `test_camera_name.py`.
"""

from __future__ import annotations

import argparse
from typing import ClassVar


class CameraName:
    """The rule a camera's name must meet, and the name of a camera its client did not name.

    **Every camera has a name.** A client may name a camera; one it does not is
    `CARLA-SENSOR-<actor id>`, which no other camera on the server can hold.

    **A name is short and plain**: 1 to 63 characters, each an ASCII letter, digit, underscore or
    hyphen -- `Overwatch_1`, `Southeast_1700m_orbit`, `NapOfEarth_2` -- and nothing else. Such a name
    is the same file name on Windows and on Linux, the same PNG text and the same CoT callsign, so a
    chosen name is used as given or refused, never rewritten, and every refusal says what is allowed.
    Three names the characters allow are refused too: a name Windows keeps for a device (CON, PRN,
    AUX, NUL, COM0 to COM9, LPT0 to LPT9), a role name the server gives sensors (front, back, left,
    right, front_left, front_right, back_left, back_right -- every sensor spawned without a name
    carries the first), and the default form for any camera but the one it is the default of.

    **Case does not tell two names apart** (`same`), as a Windows file system does not.
    """

    MAX_LENGTH: ClassVar[int] = 63
    DEFAULT_PREFIX: ClassVar[str] = "CARLA-SENSOR-"
    # The characters a name may hold, as the run configuration's schema states them.
    PATTERN: ClassVar[str] = "^[A-Za-z0-9_-]{1,63}$"
    ALLOWED: ClassVar[str] = ("a camera name is 1 to 63 characters, each an ASCII letter, digit, "
                              "underscore or hyphen, such as Overwatch_1 or Southeast_1700m_orbit")
    WINDOWS_DEVICES: ClassVar[frozenset[str]] = frozenset(
        {"CON", "PRN", "AUX", "NUL"}
        | {f"COM{n}" for n in range(10)}
        | {f"LPT{n}" for n in range(10)})
    # The role names the server offers every sensor blueprint, the first of them its default.
    SENSOR_ROLE_NAMES: ClassVar[frozenset[str]] = frozenset(
        {"FRONT", "BACK", "LEFT", "RIGHT", "FRONT_LEFT", "FRONT_RIGHT", "BACK_LEFT", "BACK_RIGHT"})

    @classmethod
    def default(cls, camera_id: int) -> str:
        """The name of a camera its client did not name: `CARLA-SENSOR-<actor id>`."""
        return f"{cls.DEFAULT_PREFIX}{int(camera_id)}"

    @classmethod
    def is_default_form(cls, name: str | None) -> bool:
        """Whether `name` has the default form, `CARLA-SENSOR-<digits>`, in any case."""
        if name is None or len(name) <= len(cls.DEFAULT_PREFIX):
            return False
        digits = name[len(cls.DEFAULT_PREFIX):]
        return (name[:len(cls.DEFAULT_PREFIX)].upper() == cls.DEFAULT_PREFIX
                and all("0" <= c <= "9" for c in digits))

    @staticmethod
    def same(first: str | None, second: str | None) -> bool:
        """Whether two names are the same name, compared without regard to case."""
        if first is None or second is None:
            return first is second
        return first.upper() == second.upper()

    @classmethod
    def problem(cls, name: str | None, camera_id: int | None = None) -> str | None:
        """Why `name` cannot be a camera's name, or None when it can. Every refusal says what is
        allowed.

        `camera_id` is the camera it is to name, where known: a name of the default form is accepted
        only as that camera's own default.
        """
        if not name:
            return f"a camera name cannot be empty: {cls.ALLOWED}"
        for c in name:
            if not cls._allowed(c):
                return (f"camera name '{cls._shown(name)}' holds {cls._described(c)}, which a camera "
                        f"name cannot: {cls.ALLOWED}")
        if len(name) > cls.MAX_LENGTH:
            return f"camera name '{name}' is {len(name)} characters long: {cls.ALLOWED}"
        if name.upper() in cls.WINDOWS_DEVICES:
            return (f"camera name '{name}' is a name Windows keeps for a device, and a channel's "
                    "directory is named by its camera; choose another, such as Overwatch_1")
        if name.upper() in cls.SENSOR_ROLE_NAMES:
            return (f"camera name '{name}' is a role name the server gives sensors, and every sensor "
                    "spawned without a name carries one, so other cameras already hold it; choose "
                    "another, such as Overwatch_1")
        if cls.is_default_form(name) and not (camera_id is not None
                                              and cls.same(name, cls.default(camera_id))):
            whose = (f"is not camera {int(camera_id)}'s own" if camera_id is not None
                     else "names no camera of its own")
            return (f"camera name '{name}' has the form every unnamed camera's name takes, "
                    f"{cls.DEFAULT_PREFIX}<actor id>, and {whose}: it would be another camera's "
                    "name; choose another, such as Overwatch_1")
        return None

    @classmethod
    def argument(cls, text: str) -> str:
        """An argparse `type` for a camera's name: the name as given, or the reason it is refused."""
        refused = cls.problem(text)
        if refused is not None:
            raise argparse.ArgumentTypeError(refused)
        return text

    @staticmethod
    def _allowed(c: str) -> bool:
        return "A" <= c <= "Z" or "a" <= c <= "z" or "0" <= c <= "9" or c in "_-"

    @staticmethod
    def _shown(name: str) -> str:
        return "".join(c if " " <= c <= "~" else f"\\u{ord(c):04X}" for c in name)

    @staticmethod
    def _described(c: str) -> str:
        if c == " ":
            return "a space"
        if c < " " or c == "\x7f":
            return f"the control character U+{ord(c):04X}"
        if c > "~":
            return f"'{c}' (U+{ord(c):04X}), which is not ASCII"
        return f"'{c}'"
