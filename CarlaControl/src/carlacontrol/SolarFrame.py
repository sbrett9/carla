"""The solar frame: the facts about a world's sun that a scenario's epoch is checked against offline.

A scenario declares its epoch as a civil instant with a numeric UTC offset. Checking that declaration
needs two facts about the *world*, and neither needs a running server once they are written down:

  * **the origin latitude and longitude** -- the engine computes the sun from the georeference
    origin, so these are what `SolarPositionModel` must be given to say where the sun is at any
    civil instant the scenario can reach;
  * **the zone the engine derives** -- when a world's georeference is configured the bridge sets
    `TimeZone = clamp(longitude, -180, 180) / 15` (`ACesiumSunSky::EstimateTimeZoneForLongitude`,
    called from `UCesiumHeightSampler`'s configure path in `CesiumHeightSampler.cpp`), with daylight
    saving off and the clock at 12.00, whether it spawned the sun or found one. That is local mean
    solar time, not the site's civil offset: at Bahonar it is +03:44:43 against Iran's +03:30. The
    difference is what `04_Contracts.md` V9.13 bounds and `07_Scenario_Authoring.md` check 40
    reports, and it is computable from this file alone.

What it cannot see. The zone *during a run*: a SUMO drive session writes the declared civil offset
into the sun with `set_solar_epoch`, and an attached world holds whatever the last session left, so
the run's own zone is read from `get_solar_state` at run start (`04_Contracts.md` D4.19). Whether the
world has a sun at all: this is written from the package, not read from a server. The site's civil
zone: deriving one from a position needs a zone-boundary dataset and a time-zone database, and this
machine has neither (`zoneinfo` resolves zero zones), so the frame carries only what the engine
derives and never presents a civil zone as fact.
"""
from __future__ import annotations

from carlacontrol.SolarPositionModel import SolarPositionModel

SOLAR_FRAME_VERSION = 1

# What the bridge sets when it configures the georeference, besides the zone.
ENGINE_SOLAR_TIME_AT_CONFIGURE_HOURS = 12.0
ENGINE_DAYLIGHT_SAVING = False


class SolarFrame:
    """A world's solar frame, derived from its origin."""

    def __init__(self, origin_latitude: float, origin_longitude: float) -> None:
        if not -90.0 <= origin_latitude <= 90.0 or not -180.0 <= origin_longitude <= 180.0:
            raise ValueError(f"origin ({origin_latitude}, {origin_longitude}) is not a WGS84 "
                             "latitude and longitude")
        self.origin_latitude = float(origin_latitude)
        self.origin_longitude = float(origin_longitude)

    @classmethod
    def from_manifest(cls, manifest: dict) -> SolarFrame:
        return cls(manifest["OriginLatitude"], manifest["OriginLongitude"])

    @property
    def engine_time_zone_hours(self) -> float:
        """The engine's rule, from the one model of it. The clamp is inert for a valid longitude."""
        return SolarPositionModel.estimate_time_zone_for_longitude(self.origin_longitude)

    @staticmethod
    def format_offset(hours: float) -> str:
        """`+03:44:43` -- signed hours, minutes and seconds, rounded to the second."""
        sign = "-" if hours < 0 else "+"
        seconds = round(abs(hours) * 3600.0)
        return f"{sign}{seconds // 3600:02d}:{seconds % 3600 // 60:02d}:{seconds % 60:02d}"

    def to_dict(self) -> dict:
        zone = self.engine_time_zone_hours
        return {
            "solar_frame_version": SOLAR_FRAME_VERSION,
            "origin_latitude": self.origin_latitude,
            "origin_longitude": self.origin_longitude,
            "engine_time_zone_hours": zone,
            "engine_time_zone": self.format_offset(zone),
            "engine_time_zone_rule": "clamp(origin_longitude, -180, 180) / 15, set when the "
                                     "world's georeference is configured",
            "engine_daylight_saving": ENGINE_DAYLIGHT_SAVING,
            "engine_solar_time_at_configure_hours": ENGINE_SOLAR_TIME_AT_CONFIGURE_HOURS,
        }
