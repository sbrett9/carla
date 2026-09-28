"""The world's geographic frame: WGS84 latitude and longitude against CARLA-local metres.

This is `CarlaNet.Types.Geom.Geodesy` -- the transform the truth telemetry, the bare-earth grid, the
sign injector and the drape all use -- held at one world's origin. It converts through a local
east-north-up tangent plane at the origin, which is also how the Cesium georeference seats the
photoreal imagery, so a position placed with it sits where the imagery shows that place.

It is a second implementation of the relation between geography and the road network, not the
first: the network was placed by netconvert's PROJ transverse Mercator. The two are compared rather
than trusted -- measured over the three shipped worlds' whole staging rectangles, SUMO's projection
with its y negated and this frame agree to 0.005 mm (Gardnerville), 0.010 mm (Arapahoe) and 0.397 mm
(Bahonar, whose corners are 4 km from the origin). `AreaOfInterestResolver` holds every area vertex
to that agreement.
"""
from __future__ import annotations

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.Types.Geom import Geodesy, GeoLocation


class GeodeticFrame:
    """WGS84 against CARLA-local metres at one world origin. CARLA local: +x east, -y north."""

    def __init__(self, origin_latitude: float, origin_longitude: float) -> None:
        self.origin_latitude = float(origin_latitude)
        self.origin_longitude = float(origin_longitude)
        self._origin = GeoLocation(self.origin_latitude, self.origin_longitude, 0.0)

    def to_carla(self, *, longitude: float, latitude: float) -> tuple[float, float]:
        """CARLA-local `(x, y)` metres of a WGS84 position. Keyword-only: GeoJSON orders longitude
        first and `GeoLocation` orders latitude first, and a positional call would hide the swap."""
        local = Geodesy.GeodeticToCarlaLocal(
            self._origin, GeoLocation(float(latitude), float(longitude), 0.0))
        return float(local.X), float(local.Y)

    def to_geodetic(self, x: float, y: float) -> tuple[float, float]:
        """WGS84 `(longitude, latitude)` of a CARLA-local position."""
        geo = Geodesy.CarlaLocalToGeodetic(self._origin, float(x), float(y), 0.0)
        return float(geo.Longitude), float(geo.Latitude)
