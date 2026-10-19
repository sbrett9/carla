"""The line carla-drive logs for each camera footprint its session follows."""
from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


class CameraFootprints:
    """Says each camera's footprint and range cap once, the first time the session follows it.

    The session measures a camera's footprint at the first step after it is registered, so this reads
    the report after every step; a camera it has already said is not said again. Only a render set
    that follows the cameras measures one.
    """

    def __init__(self) -> None:
        self.said: set[int] = set()

    def after_step(self, session) -> None:
        footprints = session.Report.CameraFootprints
        if footprints.Count == len(self.said):
            return
        for footprint in footprints.Values:
            if int(footprint.Actor) not in self.said:
                self.said.add(int(footprint.Actor))
                logger.info("render set follows %s", footprint)
