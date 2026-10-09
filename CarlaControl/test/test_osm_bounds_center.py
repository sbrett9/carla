"""A world's origin is the center of its extract's bounds, rounded to OpenStreetMap's precision.

The rounding is what makes a rebuild of one extract reproduce the same network: the origin goes into
netconvert's projection string, and floating-point noise in it changes the network's fingerprint.
The bounds below are the <bounds> of the three example extracts in Import/.
"""
from carlacontrol.OsmClipper import OSM_COORDINATE_DECIMALS, BoundingBox

GARDNERVILLE = BoundingBox(min_lat=38.9069810, min_lon=-119.7742680,
                           max_lat=38.9151790, max_lon=-119.7549250)
ARAPAHOE = BoundingBox(min_lat=39.5855800, min_lon=-104.8900400,
                       max_lat=39.6030400, max_lon=-104.8789400)
BAHONAR = BoundingBox(min_lat=27.1311000, min_lon=56.1442600,
                      max_lat=27.1691400, max_lon=56.2170400)


def test_the_center_drops_floating_point_noise():
    # The plain floating-point midpoint of Gardnerville's longitudes is -119.76459650000001.
    assert (GARDNERVILLE.min_lon + GARDNERVILLE.max_lon) / 2.0 != -119.7645965
    lat, lon = GARDNERVILLE.center()
    assert (lat, lon) == (38.91108, -119.7645965)
    assert repr(lon) == "-119.7645965"


def test_an_exact_center_is_unchanged():
    # Arapahoe's and Bahonar's corners already average to a clean value.
    assert ARAPAHOE.center() == (39.59431, -104.88449)
    assert BAHONAR.center() == (27.15012, 56.18065)


def test_the_precision_is_the_one_osm_writes():
    assert OSM_COORDINATE_DECIMALS == 7
