"""The illumination band a sun elevation falls in, as `11_Time_And_Illumination.md` §4.4 defines it.

A coarse stratification key computed from the sun elevation, so a report and a corpus can speak in
regimes rather than degrees: `day` above +6 degrees, `golden` +6 to 0, `civil_twilight` 0 to -6,
`nautical_twilight` -6 to -12, `astronomical_twilight` -12 to -18, `night` below -18. Each band
includes its lower edge's upper side: `golden` is (0, 6], `civil_twilight` (-6, 0], and so on. The
elevation is the one a declaration is made against, refraction-corrected (`DeclaredSunElevation`), and
an elevation that is not a sun's -- outside -90 to +90 degrees, like the -180 the engine reports for a
sun it could not compute -- has no band.

It is derived context, never a label (`06_Truth_And_Annotation.md` §3.6 point 6): nothing here reads
or writes supervision. Doc 11 calls for one shared function, and it is
`CarlaNet.Types.Illumination.IlluminationBands`, the table the recorder writes every capture's band
from. This class reaches it through `carlanet` rather than restating it, so a band in a capture's
truth, a band a statistic buckets by and a band the annotation vocabulary's closed core spells in its
`illumination_band` terms (`names` here) are one table (`07_Scenario_Authoring.md` §12 question 13).
"""
from __future__ import annotations

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.Types.Illumination import IlluminationBand as _Band
from CarlaNet.Types.Illumination import IlluminationBands

BAND_SOURCE = str(IlluminationBands.Source)


class IlluminationBand:
    """Names the band of a sun elevation."""

    @staticmethod
    def of(elevation_deg: float) -> str:
        """The band's name. An elevation that is not a sun's is refused, never named `night`."""
        found, band = IlluminationBands.TryOf(float(elevation_deg), _Band.Night)
        if not found:
            raise ValueError(f"{elevation_deg} is not a sun's elevation; a band is defined for -90 "
                             "to +90 degrees")
        return str(IlluminationBands.Name(band))

    @staticmethod
    def names() -> list[str]:
        """Every band, from the highest sun down."""
        return [str(name) for name in IlluminationBands.Names]

    @staticmethod
    def edges() -> list[dict]:
        return [{"band": str(edge.Name),
                 "above_deg": None if edge.AboveDegrees is None else float(edge.AboveDegrees)}
                for edge in IlluminationBands.Table]
