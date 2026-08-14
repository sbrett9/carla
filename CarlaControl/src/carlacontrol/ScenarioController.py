from __future__ import annotations

import logging
import os

import carlanet as carla


class ScenarioController:
    """Runs an OpenSCENARIO storyboard as a steppable subsystem."""

    def __init__(self, world: carla.World, path: str | None, traffic_manager) -> None:
        self.world = world
        self.path = path
        self.tm = traffic_manager
        self.available = path is not None
        self.reason = "" if self.available else "no scenario given (--scenario)"
        self.running = False
        self.want_enabled = False
        self._executor = None
        self.logger = logging.getLogger(__name__)

    def apply_want(self) -> None:
        if self.want_enabled and not self.running:
            if not self.available:
                self.logger.warning("scenario toggle ignored: %s", self.reason)
                self.want_enabled = False
                return
            try:
                self._executor = self.world.start_scenario(self.path, self.tm, report=self._report)
            except Exception as exc:
                self.logger.error("scenario failed to start: %r", exc)
                self._executor = None
            if self._executor is None:
                self.want_enabled = False
                return
            self.running = True
            self.logger.info("scenario ON -> %s", os.path.basename(self.path))
        elif not self.want_enabled and self.running:
            try:
                self.world.stop_scenario()
            finally:
                self._executor = None
                self.running = False
                self.logger.info("scenario OFF")

    def toggle_want(self, enabled: bool | None = None) -> None:
        if enabled is None:
            self.want_enabled = not self.want_enabled
        else:
            self.want_enabled = enabled

    def update(self, now: float) -> None:
        del now
        if self.running and self._executor is not None and not self._executor.Running:
            self._executor = None
            self.running = False
            self.want_enabled = False
            self.logger.info("scenario finished")

    def status(self) -> str:
        if self._executor is None:
            return "off"
        return f"{self._executor.ElapsedSeconds:.0f}s {self._executor.ActsComplete}/{self._executor.ActCount}"

    def _report(self, line: str) -> None:
        self.logger.info("scenario: %s", line)
