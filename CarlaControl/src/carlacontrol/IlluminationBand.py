"""The illumination band a sun elevation falls in, as `11_Time_And_Illumination.md` §4.4 defines it.

A coarse stratification key computed from the sun elevation, so a report and a corpus can speak in
regimes rather than degrees: `day` above +6 degrees, `golden` +6 to 0, `civil_twilight` 0 to -6,
`nautical_twilight` -6 to -12, `astronomical_twilight` -12 to -18, `night` below -18. Each band
includes its lower edge's upper side: `golden` is (0, 6], `civil_twilight` (-6, 0], and so on. The
elevation is the one a declaration is made against, refraction-corrected (`DeclaredSunElevation`).

It is derived context, never a label (`06_Truth_And_Annotation.md` §3.6 point 6): nothing here reads
or writes supervision. Doc 11 calls for one shared function and this is the only one in the tree. Doc 11
§4.4 is the single definition of the bands: `06_Truth_And_Annotation.md` names them from it, and the
annotation vocabulary's closed core takes its `illumination_band` terms from `names` here, so a band
spelled in a vocabulary and a band a statistic buckets by are one table (`07_Scenario_Authoring.md` §12
question 13).
"""
from __future__ import annotations

BAND_SOURCE = "11_Time_And_Illumination.md §4.4"

# (band, lowest elevation it holds, exclusive). Ordered from the highest band down.
BANDS: tuple[tuple[str, float], ...] = (
    ("day", 6.0),
    ("golden", 0.0),
    ("civil_twilight", -6.0),
    ("nautical_twilight", -12.0),
    ("astronomical_twilight", -18.0),
    ("night", float("-inf")),
)


class IlluminationBand:
    """Names the band of a sun elevation."""

    @staticmethod
    def of(elevation_deg: float) -> str:
        for band, floor in BANDS:
            if elevation_deg > floor:
                return band
        return BANDS[-1][0]

    @staticmethod
    def names() -> list[str]:
        """Every band, from the highest sun down."""
        return [band for band, _ in BANDS]

    @staticmethod
    def edges() -> list[dict]:
        return [{"band": band, "above_deg": None if floor == float("-inf") else floor}
                for band, floor in BANDS]
