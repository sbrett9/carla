"""Paths as a compiled scenario records them: never a folder of the machine that compiled it.

A compiled scenario is copied to other machines and read there, so what it records about a file says
which file it was, not where the compiling machine kept it. Three cases cover every path it records:

* a file inside the folder of the record that names it is named relative to that folder;
* a file of a SUMO installation is named under `${SUMO_HOME}`, SUMO's own spelling of that variable:
  the installation is identified by its release, which the record carries beside the path;
* any other file is named by its file name alone, since the folder it was in was the building
  machine's.

A path that is not absolute -- a file name, a placeholder such as `<output-file>`, or any value that
is not a path at all -- is recorded as it is.

A path a generated file must still follow -- a sweep member's specification naming its world package
-- is written relative to that file instead (`relative`), so the two can be moved together.
"""
from __future__ import annotations

import os
from collections.abc import Iterable
from pathlib import Path, PurePath, PurePosixPath, PureWindowsPath

SUMO_HOME_VARIABLE = "${SUMO_HOME}"


class PortablePath:
    """Names an absolute path the way a compiled scenario records it."""

    @staticmethod
    def is_absolute(text: str) -> bool:
        """Whether `text` is an absolute path on Windows (a drive or a share) or on POSIX."""
        return PureWindowsPath(text).is_absolute() or text.startswith("/")

    @staticmethod
    def sumo_home_of(executable: str) -> list[Path]:
        """The installation an executable recorded as `<home>/bin/<tool>` belongs to; none if unrecorded."""
        if not executable:
            return []
        return [Path(PortablePath._pure(executable).parent.parent)]

    @classmethod
    def of(cls, path: str, beside: Path, sumo_homes: Iterable[Path] = ()) -> str:
        """`path` as a record written in the folder `beside` names it."""
        if not cls.is_absolute(path):
            return path
        pure = cls._pure(path)
        for home in sumo_homes:
            inside = cls._inside(pure, home)
            if inside is not None:
                return f"{SUMO_HOME_VARIABLE}/{inside}" if inside else SUMO_HOME_VARIABLE
        inside = cls._inside(pure, Path(beside).resolve())
        if inside:
            return inside
        return pure.name

    @classmethod
    def argv(cls, argv: Iterable[str], beside: Path, sumo_homes: Iterable[Path] = ()) -> list[str]:
        """A command line with every absolute path in it named by `of`, file by file in a
        comma-separated list such as netconvert's `--type-files`."""
        homes = list(sumo_homes)
        named = []
        for argument in argv:
            parts = argument.split(",")
            if any(cls.is_absolute(part) for part in parts):
                argument = ",".join(cls.of(part, beside, homes) for part in parts)
            named.append(argument)
        return named

    @staticmethod
    def relative(target: Path, beside: Path) -> str:
        """`target` relative to the folder `beside`, in forward slashes; absolute only where no
        relative path exists, a target on another drive."""
        target = Path(target).resolve()
        try:
            return Path(os.path.relpath(target, Path(beside).resolve())).as_posix()
        except ValueError:
            return str(target)

    @staticmethod
    def _pure(text: str) -> PurePath:
        return PurePosixPath(text) if text.startswith("/") else PureWindowsPath(text)

    @staticmethod
    def _inside(path: PurePath, folder: Path) -> str | None:
        """`path` relative to `folder`, in forward slashes, or None where it is not inside it."""
        kind = type(path)
        base = kind(str(folder))
        if not base.is_absolute() or not path.is_relative_to(base):
            return None
        return path.relative_to(base).as_posix()
