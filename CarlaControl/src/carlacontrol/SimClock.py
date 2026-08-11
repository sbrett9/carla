from __future__ import annotations

import logging

import carlanet as carla


class SimClock:
    """Latest world tick and simulation time, cached from the world-observer stream."""

    def __init__(self, world: carla.World) -> None:
        self.frame = 0
        self.sim_time = 0.0
        self.logger = logging.getLogger(__name__)
        try:
            world.on_tick(self._on_tick)
        except Exception as e:
            self.logger.warning(
                "tick subscription failed; emitted telemetry will report tick 0: %r",
                e,
            )

    def _on_tick(self, ts) -> None:
        self.frame = int(ts.frame)
        self.sim_time = float(ts.elapsed_seconds)

    def attributes(self) -> dict[str, str]:
        """Return capture metadata for correlation with recorded artifacts."""
        return {"tick": str(self.frame)}
