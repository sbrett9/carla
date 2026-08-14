"""CoT (Cursor-on-Target) telemetry streaming controller for CARLA.

Provides high-level controller for streaming vehicle telemetry in CoT XML format
over UDP to TAK (Team Awareness Kit) and other CoT-aware systems.
"""

from __future__ import annotations

import logging

import carlanet as carla

from .CotUdpEmitter import CotUdpEmitter
from .SimClock import SimClock


class TelemetryController:
    """CoT-over-UDP emitter as a steppable subsystem. Needs the georeference origin; if it is
    missing the toggle is disabled. Off by default."""

    def __init__(
        self,
        world: carla.World,
        origin,
        args,
        clock: SimClock | None = None,
    ):
        self.world = world
        self.origin = origin
        self.args = args
        self.clock = clock
        self.logger = logging.getLogger(__name__)
        self.available = self.origin is not None
        self.reason = "" if self.available else "no georeference origin (get_cesium_origin failed)"
        self.enabled = False
        self.want_enabled = False  # desired state, flipped by the hotkey on the main thread
        self.emit: CotUdpEmitter | None = None
        self.period = 1.0 / max(0.1, args.rate)
        self.last = 0.0
        self.last_count = 0
        
        if self.available:
            self.logger.info(f"telemetry controller initialized: {args.tak_host}:{args.tak_port} @ {args.rate} Hz")
        else:
            self.logger.warning(f"telemetry unavailable: {self.reason}")

    def apply_want(self) -> None:
        """Reconcile actual on/off with the hotkey's desired state. Called on whichever thread owns
        the RPCs (the background worker in async, the tick loop in sync)."""
        if self.want_enabled and not self.enabled:
            if not self.enable():
                self.want_enabled = False  # unavailable -> drop the desire so we don't retry-spam
        elif not self.want_enabled and self.enabled:
            self.disable()

    def toggle_want(self, enabled: bool | None = None) -> None:
        """
        Toggle the want_enabled state.

        Args:
            enabled: If None, toggle the current state. If a boolean, set the state to this value.
        """
        if enabled is None:
            self.want_enabled = not self.want_enabled
        else:
            self.want_enabled = enabled

    def enable(self) -> bool:
        if not self.available:
            self.logger.warning(f"telemetry toggle ignored: {self.reason}")
            return False
        if self.emit is None:
            self.emit = CotUdpEmitter(self.args.tak_host, self.args.tak_port, ttl=self.args.ttl)
        self.enabled = True
        self.logger.info(f"telemetry ON -> udp://{self.args.tak_host}:{self.args.tak_port} @ {self.args.rate} Hz")
        return True

    def disable(self) -> None:
        self.enabled = False
        self.logger.info("telemetry OFF")

    def update(self, now: float) -> None:
        if not self.enabled or (now - self.last) < self.period:
            return
        self.last = now
        try:
            recs = self.world.get_vehicle_telemetry(self.origin)
        except Exception as e:
            self.logger.error(f"get_vehicle_telemetry failed: {e!r}")
            return
        try:
            solar = self.world.get_solar_state()  # cache read (paired to the latest tick)
        except Exception as e:
            self.logger.debug(f"get_solar_state failed: {e}")
            solar = None
        for r in recs:
            xml = CotUdpEmitter.vehicle_telemetry_to_cot(
                r,
                affiliation=self.args.affiliation,
                stale_seconds=self.args.stale,
                solar=solar,
                capture=self.clock,
            )
            self.emit.send(xml)
            if self.args.echo:
                self.logger.info(xml)
        self.last_count = len(recs)

    def close(self) -> None:
        if self.emit is not None:
            self.emit.close()
            self.logger.info("telemetry controller closed")
