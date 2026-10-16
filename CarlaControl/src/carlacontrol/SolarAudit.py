"""Reads the engine's sun and compares it against the algorithm the engine is meant to be running.

Every window in a capture plan is placed by computing where the sun will be at a civil clock time on
a date at a site. That computation is a **model**. This class is what turns it into a reading: it
sets a known epoch on the server, reads `get_solar_state` back, and differences the returned
elevation against `SolarPositionModel` evaluated on the same inputs.

**The inputs are checked before the angles are.** A sun can disagree with a model because the
algorithm differs, or because the sun was never given the epoch the model was evaluated at — a
different time zone, a stale date, an advancing clock that moved between the write and the read. The
second failure is far more likely than the first and looks identical in an elevation residual, so
`check_inputs` runs first and an input mismatch is reported as an input mismatch.

Two solar reads exist and they are not the same read. `world.get_solar_state()` prefers the
world-observer cache, which is pushed with each tick; the on-demand RPC computes the sun when asked.
The RPC answers with twelve values, the twelfth being the refraction-corrected elevation the sun's
directional light is actually rotated by. The cache carries the same twelve from a server whose
header was widened to hold it, and eleven from one built before that. This class reads both and
reports where they differ.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass, field

import carlanet

from carlacontrol.SolarPositionModel import (
    RESOLUTION_FLOOR_DEGREES,
    SolarPositionModel,
)

# The order `get_solar_state` packs its values in (`CarlaServer.cpp`, `set_solar_epoch`'s neighbour).
# A world-observer header from a server built before the corrected elevation was carried stops at
# `rate`, so a cached read from one never carries the last value; `OBSERVER_CACHE_FIELD_COUNT` is
# that older width, and the fewest values a reading can have.
SOLAR_STATE_FIELDS = (
    "solar_time", "year", "month", "day", "time_zone", "lat", "lon",
    "sun_elevation_deg", "sun_azimuth_deg", "advancing", "rate",
    "sun_corrected_elevation_deg",
)
OBSERVER_CACHE_FIELD_COUNT = 11


@dataclass
class SolarPoint:
    """One audited instant: what was asked for, what the engine returned, what the model says."""

    year: int
    month: int
    day: int
    solar_time: float
    utc_offset_hours: float
    engine_elevation_deg: float
    engine_corrected_elevation_deg: float | None
    engine_azimuth_deg: float
    model_elevation_deg: float
    model_corrected_elevation_deg: float
    model_azimuth_deg: float

    @property
    def elevation_residual_deg(self) -> float:
        return self.engine_elevation_deg - self.model_elevation_deg

    @property
    def corrected_residual_deg(self) -> float | None:
        if self.engine_corrected_elevation_deg is None:
            return None
        return self.engine_corrected_elevation_deg - self.model_corrected_elevation_deg

    @property
    def azimuth_residual_deg(self) -> float:
        return self.engine_azimuth_deg - self.model_azimuth_deg


@dataclass
class InputCheck:
    """One of the epoch fields the sun was asked to hold, and what it holds."""

    name: str
    asked: float | None
    read: float | None
    agrees: bool
    note: str = ""


@dataclass
class SolarAuditResult:
    """Everything one site's audit established."""

    site: str
    latitude: float
    longitude: float
    input_checks: list[InputCheck] = field(default_factory=list)
    points: list[SolarPoint] = field(default_factory=list)
    cached_field_count: int | None = None
    on_demand_field_count: int | None = None

    @property
    def inputs_agree(self) -> bool:
        return all(check.agrees for check in self.input_checks)

    @property
    def worst_elevation_residual_deg(self) -> float:
        if not self.points:
            return 0.0
        return max(abs(point.elevation_residual_deg) for point in self.points)

    @property
    def worst_azimuth_residual_deg(self) -> float:
        if not self.points:
            return 0.0
        return max(abs(point.azimuth_residual_deg) for point in self.points)


class SolarAudit:
    """Audits one world's sun against `SolarPositionModel`."""

    def __init__(self, world, site: str, latitude: float, longitude: float,
                 logger: logging.Logger | None = None) -> None:
        self.world = world
        self.site = site
        self.latitude = latitude
        self.longitude = longitude
        self.logger = logger or logging.getLogger(__name__)

    def read_on_demand(self) -> dict | None:
        """The server's own computation of the sun, now.

        This goes past `World.get_solar_state`, which prefers the world-observer cache. The cache is
        paired to a tick rather than to the last thing written, and from an older server it is
        eleven values wide, so it can neither be relied on to reflect an epoch set microseconds ago
        nor to carry the refraction-corrected elevation. An audit needs both.
        """
        # `_sync` and `_client` are carlanet's own plumbing; the public shim exposes no way to ask
        # for the uncached read, and asking for it is the whole point of this function.
        values = carlanet._sync(self.world._client.GetSolarStateAsync())
        if values is None or values.Count < OBSERVER_CACHE_FIELD_COUNT:
            return None
        state = {name: (values[index] if index < values.Count else None)
                 for index, name in enumerate(SOLAR_STATE_FIELDS)}
        for name in ("year", "month", "day"):
            state[name] = int(state[name])
        state["advancing"] = bool(state["advancing"])
        state["_field_count"] = values.Count
        return state

    def read_cached(self) -> dict | None:
        """What `World.get_solar_state` returns, cache path included — what every other reader sees."""
        return self.world.get_solar_state()

    def check_inputs(self, state: dict, year: int, month: int, day: int, solar_time: float,
                     utc_offset_hours: float) -> list[InputCheck]:
        """Whether the sun holds the epoch and the place it was asked to hold.

        Reported before any angle is looked at. A model evaluated at inputs the engine does not have
        is not a comparison of algorithms, and its residual says nothing about either one.
        """
        checks = [
            InputCheck("time_zone", utc_offset_hours, state["time_zone"],
                       abs(state["time_zone"] - utc_offset_hours) < 1e-6,
                       "hours; a generated world spawns its sun at longitude/15, not at the "
                       "site's civil offset"),
            InputCheck("year", year, state["year"], state["year"] == year),
            InputCheck("month", month, state["month"], state["month"] == month),
            InputCheck("day", day, state["day"], state["day"] == day),
            InputCheck("solar_time", solar_time, state["solar_time"],
                       abs(state["solar_time"] - solar_time) < 1e-6, "hours on the sun's own clock"),
            InputCheck("lat", self.latitude, state["lat"],
                       abs(state["lat"] - self.latitude) < 1e-6),
            InputCheck("lon", self.longitude, state["lon"],
                       abs(state["lon"] - self.longitude) < 1e-6),
            InputCheck("advancing", 0.0, float(state["advancing"]), not state["advancing"],
                       "an advancing sun moves between the write and the read"),
        ]
        return checks

    def audit_point(self, year: int, month: int, day: int, solar_time: float,
                    utc_offset_hours: float) -> tuple[SolarPoint, list[InputCheck]]:
        """Set one epoch, read it back, and difference the engine against the model."""
        if not self.world.set_solar_epoch(year, month, day, solar_time, utc_offset_hours):
            raise RuntimeError(
                f"{self.site}: set_solar_epoch refused {year:04d}-{month:02d}-{day:02d} "
                f"{solar_time:05.2f} at UTC{utc_offset_hours:+.2f}. The world has no CesiumSunSky, "
                "or the date is not a calendar date.")
        state = self.read_on_demand()
        if state is None:
            raise RuntimeError(f"{self.site}: the world reports no sun to audit")
        checks = self.check_inputs(state, year, month, day, solar_time, utc_offset_hours)
        modelled = SolarPositionModel.at_solar_time(
            state["lat"], state["lon"], state["time_zone"], False, year, month, day, solar_time)
        point = SolarPoint(
            year=year, month=month, day=day, solar_time=solar_time,
            utc_offset_hours=utc_offset_hours,
            engine_elevation_deg=state["sun_elevation_deg"],
            engine_corrected_elevation_deg=state["sun_corrected_elevation_deg"],
            engine_azimuth_deg=state["sun_azimuth_deg"],
            model_elevation_deg=modelled.elevation_deg,
            model_corrected_elevation_deg=modelled.corrected_elevation_deg,
            model_azimuth_deg=modelled.azimuth_deg)
        return point, checks

    def run(self, dates: list[tuple[int, int, int]], hours: list[float],
            utc_offset_hours: float) -> SolarAuditResult:
        """Audit every date/hour pair at one declared UTC offset."""
        result = SolarAuditResult(self.site, self.latitude, self.longitude)

        as_found = self.read_on_demand()
        cached = self.read_cached()
        result.on_demand_field_count = as_found.get("_field_count") if as_found else None
        result.cached_field_count = len(
            [name for name, value in cached.items() if value is not None]) if cached else None
        if as_found:
            self.logger.info(
                "%s: sun as found -- %04d-%02d-%02d %05.2f in zone %+0.6f at %.5f, %.5f; "
                "advancing=%s", self.site, as_found["year"], as_found["month"], as_found["day"],
                as_found["solar_time"], as_found["time_zone"], as_found["lat"], as_found["lon"],
                as_found["advancing"])

        for year, month, day in dates:
            for hour in hours:
                point, checks = self.audit_point(year, month, day, hour, utc_offset_hours)
                result.points.append(point)
                for check in checks:
                    if not check.agrees:
                        result.input_checks.append(check)
        if not result.input_checks:
            # Record the agreement rather than only its absence, so a run that checked nothing and a
            # run whose checks all passed do not read the same.
            _, checks = self.audit_point(dates[0][0], dates[0][1], dates[0][2], hours[0],
                                         utc_offset_hours)
            result.input_checks = checks
        return result

    @staticmethod
    def describe(result: SolarAuditResult) -> str:
        """The audit as a table: inputs first, then the angles."""
        lines = [f"== {result.site}  {result.latitude:.5f}, {result.longitude:.5f}"]
        lines.append("   inputs the sun was asked to hold:")
        for check in result.input_checks:
            mark = "ok  " if check.agrees else "MISS"
            lines.append(f"     {mark} {check.name:<12} asked {check.asked!s:>14}  "
                         f"read {check.read!s:>18}{('  -- ' + check.note) if check.note else ''}")
        lines.append(f"   solar state fields: {result.on_demand_field_count} on demand, "
                     f"{result.cached_field_count} through the cache "
                     f"(the observer header carries {OBSERVER_CACHE_FIELD_COUNT})")
        if not result.inputs_agree:
            lines.append("   inputs disagree; the angle residuals below do not measure the "
                         "algorithm")
        lines.append(f"   {'epoch':<22}{'engine elev':>12}{'model elev':>12}{'residual':>11}"
                     f"{'engine corr':>13}{'model corr':>12}{'resid':>9}{'azim resid':>12}")
        for point in result.points:
            corrected = ("       n/a" if point.engine_corrected_elevation_deg is None
                         else f"{point.engine_corrected_elevation_deg:>13.5f}")
            corrected_residual = ("      n/a" if point.corrected_residual_deg is None
                                  else f"{point.corrected_residual_deg:>9.2e}")
            lines.append(
                f"   {point.year:04d}-{point.month:02d}-{point.day:02d} {point.solar_time:05.2f} "
                f"UTC{point.utc_offset_hours:+05.2f}"
                f"{point.engine_elevation_deg:>12.5f}{point.model_elevation_deg:>12.5f}"
                f"{point.elevation_residual_deg:>11.2e}{corrected}"
                f"{point.model_corrected_elevation_deg:>12.5f}{corrected_residual}"
                f"{point.azimuth_residual_deg:>12.2e}")
        lines.append(f"   worst |elevation residual| {result.worst_elevation_residual_deg:.3e} deg, "
                     f"worst |azimuth residual| {result.worst_azimuth_residual_deg:.3e} deg, "
                     f"against a resolution floor of {RESOLUTION_FLOOR_DEGREES} deg")
        return "\n".join(lines)
