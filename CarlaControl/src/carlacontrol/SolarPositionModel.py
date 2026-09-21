"""The NOAA solar-position algorithm the engine evaluates, in Python.

`USunPositionFunctionLibrary::GetSunPosition`
(`Engine/Plugins/Runtime/SunPosition/Source/SunPosition/Private/SunPosition.cpp:11-149`) is the NOAA
solar-position spreadsheet in pure scalar double arithmetic.
`ACesiumSunSky::UpdateSun_Implementation` (`CesiumSunSky.cpp:405-467`) calls it with the
georeference's latitude and longitude plus the sun's own `TimeZone`, `UseDaylightSavingTime`, `Year`,
`Month`, `Day` and the hour/minute/second derived from `SolarTime`, then stores
`Elevation = sunPosition.Elevation - 180` and `CorrectedElevation = sunPosition.CorrectedElevation
- 180`.

`get_solar_state` reports `Elevation`, the **geometric** angle. The sun's directional light is rotated
by `CorrectedElevation`, the angle with atmospheric refraction applied. Near the horizon the two
differ by roughly half a degree, which is a large fraction of a low sun's elevation, so this class
returns both and the caller chooses.

This is a model of what the engine computes, not a reading of it. `SolarAudit` is what reads the
engine and compares the two.
"""
from __future__ import annotations

import datetime as dt
import math
from dataclasses import dataclass

# ACesiumSunSky::GetHMSFromSolarTime truncates the clock to whole seconds, so a solar time that is
# not a whole number of seconds is evaluated at the second below it. One second of clock is 15
# arcseconds of hour angle, which is at most 0.0042 degrees of elevation at any latitude, and the
# angles come back over the wire as single-precision floats quantised at roughly 1e-5 degrees near
# 180 (the value the engine subtracts from). Agreement closer than the sum of those two is not
# resolvable, so a comparison against this model is meaningless below it.
CLOCK_TRUNCATION_DEGREES = 0.0042
FLOAT_QUANTISATION_DEGREES = 1.6e-5
RESOLUTION_FLOOR_DEGREES = 0.01


@dataclass(frozen=True)
class SunPosition:
    """Where the model puts the sun: both elevations, in degrees, and the azimuth."""

    elevation_deg: float
    corrected_elevation_deg: float
    azimuth_deg: float


class SolarPositionModel:
    """Evaluates the engine's solar algorithm without the engine."""

    @staticmethod
    def julian_day(year: int, month: int, day: int, hour: int, minute: int, second: int) -> float:
        """`FDateTime::GetJulianDay`: 1721425.5 plus whole days since 0001-01-01 plus the day
        fraction."""
        whole = (dt.date(year, month, day) - dt.date(1, 1, 1)).days
        fraction = (hour * 3600 + minute * 60 + second) / 86400.0
        return 1721425.5 + whole + fraction

    @staticmethod
    def hms_from_solar_time(solar_time: float) -> tuple[int, int, int]:
        """`ACesiumSunSky::GetHMSFromSolarTime` (`CesiumSunSky.cpp:575-585`), truncation included."""
        hour = int(solar_time) % 24
        minute = int((solar_time - hour) * 60) % 60
        second = round((solar_time - hour - minute // 60) * 3600) % 60
        return hour, minute, second

    @staticmethod
    def estimate_time_zone_for_longitude(longitude: float) -> float:
        """`ACesiumSunSky::EstimateTimeZoneForLongitude` (`CesiumSunSky.cpp:570-573`).

        This is the zone a generated world's sun is spawned with, and it is local mean solar time,
        not the site's civil offset.
        """
        return longitude / 15.0

    @classmethod
    def sun_position(cls, latitude: float, longitude: float, time_zone: float, is_dst: bool,
                     year: int, month: int, day: int,
                     hour: int, minute: int, second: int) -> SunPosition:
        """The sun's position for a clock reading in the given zone."""
        time_offset = time_zone + (1.0 if is_dst else 0.0)
        latitude_rad = math.radians(latitude)

        julian_day = cls.julian_day(year, month, day, hour, minute, second)
        centuries = (julian_day - 2451545.0) / 36525.0

        mean_longitude = math.fmod(
            280.46646 + centuries * (36000.76983 + centuries * 0.0003032), 360.0)
        mean_longitude_rad = math.radians(mean_longitude)
        mean_anomaly = 357.52911 + centuries * (35999.05029 - 0.0001537 * centuries)
        mean_anomaly_rad = math.radians(mean_anomaly)
        eccentricity = 0.016708634 - centuries * (0.000042037 + 0.0000001267 * centuries)

        equation_of_centre = (
            math.sin(mean_anomaly_rad) * (1.914602 - centuries * (0.004817 + 0.000014 * centuries))
            + math.sin(2 * mean_anomaly_rad) * (0.019993 - 0.000101 * centuries)
            + math.sin(3 * mean_anomaly_rad) * 0.000289)
        true_longitude = mean_longitude + equation_of_centre
        apparent_longitude = (true_longitude - 0.00569
                              - 0.00478 * math.sin(math.radians(125.04 - 1934.136 * centuries)))
        apparent_longitude_rad = math.radians(apparent_longitude)

        mean_obliquity = 23.0 + (26.0 + (21.448 - centuries * (
            46.815 + centuries * (0.00059 - centuries * 0.001813))) / 60.0) / 60.0
        obliquity = mean_obliquity + 0.00256 * math.cos(math.radians(125.04 - 1934.136 * centuries))
        obliquity_rad = math.radians(obliquity)
        declination_rad = math.asin(math.sin(obliquity_rad) * math.sin(apparent_longitude_rad))

        var_y = math.tan(obliquity_rad / 2.0) ** 2
        equation_of_time = 4.0 * math.degrees(
            var_y * math.sin(2 * mean_longitude_rad)
            - 2.0 * eccentricity * math.sin(mean_anomaly_rad)
            + 4.0 * eccentricity * var_y * math.sin(mean_anomaly_rad)
            * math.cos(2 * mean_longitude_rad)
            - 0.5 * var_y * var_y * math.sin(4 * mean_longitude_rad)
            - 1.25 * eccentricity * eccentricity * math.sin(2 * mean_anomaly_rad))

        true_solar_time = math.fmod(
            (hour * 60 + minute + second / 60.0) + equation_of_time + 4.0 * longitude
            - 60.0 * time_offset, 1440.0)
        hour_angle = (true_solar_time / 4.0 + 180.0 if true_solar_time < 0
                      else true_solar_time / 4.0 - 180.0)
        hour_angle_rad = math.radians(hour_angle)

        zenith_rad = math.acos(
            math.sin(latitude_rad) * math.sin(declination_rad)
            + math.cos(latitude_rad) * math.cos(declination_rad) * math.cos(hour_angle_rad))
        elevation = 90.0 - math.degrees(zenith_rad)

        tan_elevation = math.tan(math.radians(elevation))
        refraction = 0.0
        if elevation <= 85.0:
            if elevation > 5.0:
                refraction = (58.1 / tan_elevation - 0.07 / tan_elevation ** 3
                              + 0.000086 / tan_elevation ** 5 / 3600.0)
            elif elevation > -0.575:
                refraction = 1735.0 + elevation * (
                    -518.2 + elevation * (103.4 + elevation * (-12.79 + elevation * 0.711)))
            else:
                refraction = -20.772 / tan_elevation
            refraction /= 3600.0

        azimuth_base = math.degrees(math.acos(
            ((math.sin(latitude_rad) * math.cos(zenith_rad)) - math.sin(declination_rad))
            / (math.cos(latitude_rad) * math.sin(zenith_rad))))
        azimuth = (math.fmod(azimuth_base + 180.0, 360.0) if hour_angle > 0
                   else math.fmod(540.0 - azimuth_base, 360.0))

        return SunPosition(elevation, elevation + refraction, azimuth)

    @classmethod
    def at_solar_time(cls, latitude: float, longitude: float, time_zone: float, is_dst: bool,
                      year: int, month: int, day: int, solar_time: float) -> SunPosition:
        """The sun's position for a clock given as a fractional hour, truncated as the engine does."""
        hour, minute, second = cls.hms_from_solar_time(solar_time)
        return cls.sun_position(latitude, longitude, time_zone, is_dst,
                                year, month, day, hour, minute, second)

    @staticmethod
    def shadow_length_ratio(elevation_deg: float) -> float | None:
        """Shadow length per unit object height, `cot(elevation)`. None when the sun is down."""
        if elevation_deg <= 0.0:
            return None
        return 1.0 / math.tan(math.radians(elevation_deg))
