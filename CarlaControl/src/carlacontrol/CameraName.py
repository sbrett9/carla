"""The name a camera's recordings are known by, ruled as `CarlaNet.Recording.CameraName` rules it.

The recorder (`CarlaNet.Recording.FrameRecorder`) names every still `<camera name>_<local capture
time>` and writes the name as the callsign of the camera's platform track, and the shim
(`carlanet`) applies the same rule when a camera is spawned under a name. Both are .NET; this is the
rule again for the checks CarlaControl makes before anything reaches them -- `run_capture`'s offline
validation of a channel's `sensor_id`, and `run_SCTMV.py`'s `--camera-name` -- so a name that would be
refused is refused at the command line rather than after the world has been set up. The two are held
to the same answers by the same cases, in `CarlaNet.Tests/Recording/CameraNameTests.cs` and
`test_camera_name.py`.
"""

from __future__ import annotations

import argparse
from typing import ClassVar


class CameraName:
    """The rule a camera's name must meet, and the name of a camera its client did not name.

    **Every camera has a name.** A client may name a camera; one it does not is
    `CARLA-SENSOR-<actor id>`, which no other camera on the server can hold.

    **A chosen name is used as given or refused, never rewritten.** It is a file name on Windows and
    on Linux, text in the image's PNG chunks and a CoT callsign, so a name that would not reach all of
    them unchanged is refused with the reason: anything but 1 to 63 printable ASCII characters; any of
    ``< > : " / \\ | ? *``; a space at either end or a dot at the end, which Windows drops from a file
    name; a name Windows keeps for a device (CON, PRN, AUX, NUL, COM0 to COM9, LPT0 to LPT9) alone or
    before a dot; and the default form for any camera but the one it is the default of.

    **Case does not tell two names apart** (`same`), as a Windows file system does not.
    """

    MAX_LENGTH: ClassVar[int] = 63
    DEFAULT_PREFIX: ClassVar[str] = "CARLA-SENSOR-"
    NOT_IN_FILE_NAMES: ClassVar[str] = '<>:"/\\|?*'
    WINDOWS_DEVICES: ClassVar[frozenset[str]] = frozenset(
        {"CON", "PRN", "AUX", "NUL"}
        | {f"COM{n}" for n in range(10)}
        | {f"LPT{n}" for n in range(10)})

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
        """Why `name` cannot be a camera's name, or None when it can.

        `camera_id` is the camera it is to name, where known: a name of the default form is accepted
        only as that camera's own default.
        """
        if not name:
            return "a camera name cannot be empty"
        for c in name:
            if not " " <= c <= "~":
                return (f"camera name '{cls._shown(name)}' holds {cls._described(c)}: a camera name is "
                        "printable ASCII, because it is written into the image's PNG text, which holds "
                        "Latin-1 only, and into a CoT callsign")
            if c in cls.NOT_IN_FILE_NAMES:
                return (f"camera name '{name}' holds '{c}', which a Windows file name cannot hold (none "
                        "of < > : \" / \\ | ? *): every still's file name begins with the camera's name")
        if len(name) > cls.MAX_LENGTH:
            return (f"camera name '{name}' is {len(name)} characters long; a camera name is at most "
                    f"{cls.MAX_LENGTH}")
        if name[0] == " " or name[-1] in (" ", "."):
            return (f"camera name '{name}' begins or ends with a space, or ends with a dot, which "
                    "Windows drops from a file name, so the files would not carry the name given")
        device = name.split(".")[0].rstrip(" ").upper()
        if device in cls.WINDOWS_DEVICES:
            return (f"camera name '{name}' is {device}, a name Windows keeps for a device, and a "
                    "channel's directory is named by its camera")
        if cls.is_default_form(name) and not (camera_id is not None
                                              and cls.same(name, cls.default(camera_id))):
            whose = (f"is not camera {int(camera_id)}'s own" if camera_id is not None
                     else "names no camera of its own")
            return (f"camera name '{name}' has the form every unnamed camera's name takes, "
                    f"{cls.DEFAULT_PREFIX}<actor id>, and {whose}: it would be another camera's name")
        return None

    @classmethod
    def argument(cls, text: str) -> str:
        """An argparse `type` for a camera's name: the name as given, or the reason it is refused."""
        refused = cls.problem(text)
        if refused is not None:
            raise argparse.ArgumentTypeError(refused)
        return text

    @staticmethod
    def _shown(name: str) -> str:
        return "".join(c if " " <= c <= "~" else f"\\u{ord(c):04X}" for c in name)

    @staticmethod
    def _described(c: str) -> str:
        if c < " " or c == "\x7f":
            return f"the control character U+{ord(c):04X}"
        return f"'{c}' (U+{ord(c):04X})"
