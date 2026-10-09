#!/usr/bin/env python3
"""Compare the running server's sun against the algorithm the capture windows are placed with.

A capture window is placed by computing where the sun will be at a clock time on a date at a site.
That computation is a model of `ACesiumSunSky`; this reads the sun instead. For each date and hour it
sets the epoch with `set_solar_epoch`, reads `get_solar_state` back, and differences the returned
elevation and azimuth against `carlacontrol.SolarPositionModel`.

**The six epoch inputs are checked before any angle is.** A world that carries its own
`ACesiumSunSky` can reach a session holding a time zone, a date or an advancing clock that nobody
asked for, and a model evaluated at inputs the engine does not have produces a residual that says
nothing about either algorithm. Run it at more than one site: at Arapahoe the gap between the sun's
spawned zone (longitude/15) and the site's civil offset is 0.46 minutes and hides inside the noise,
while at the Bahonar port it is 14.72 minutes, which at the terminator is the difference between a
sun above the horizon and one below it.

The site is named by a world package, which is where the origin latitude and longitude come from.
`--delivered-world` loads that world into the server; without it the sun is audited on whatever map
is loaded, after `--georeference` moves the globe to the package's origin.

Examples:
    python probe_solar_audit.py --package ../../Build/world-packages/Arapahoe_I25.cwp \
        --delivered-world Arapahoe_I25
    python probe_solar_audit.py --package ../../Build/world-packages/Shahid_Bahonar_Port.cwp \
        --georeference --utc-offset 3.5
"""
import argparse
import logging
import os
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

import carlanet  # noqa: E402  (needs the path above)

from carlacontrol.SolarAudit import SolarAudit  # noqa: E402
from carlacontrol.SolarPositionModel import (  # noqa: E402
    RESOLUTION_FLOOR_DEGREES,
    SolarPositionModel,
)
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

# The dates and hours 10_Scale_And_Performance.md section 9 specifies for this measurement: three
# dates across the seasonal swing, and the four window hours the sizing scenario's population
# regimes fall on. Twelve points, one RPC pair each.
AUDIT_DATES = ((2026, 12, 21), (2026, 3, 21), (2026, 6, 21))
AUDIT_HOURS = (6.0, 7.0, 17.0, 23.0)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--package", type=Path, required=True,
                        help="the world package naming the site (its origin is the sun's position)")
    parser.add_argument("--host", default="127.0.0.1", help="CARLA server host")
    parser.add_argument("--port", type=int, default=2000, help="CARLA server port")
    parser.add_argument("--timeout", type=float, default=120.0, help="RPC timeout in seconds")
    parser.add_argument("--delivered-world",
                        help="load this delivered world before auditing (the site's own world)")
    parser.add_argument("--georeference", action="store_true",
                        help="move the globe to the package origin on the currently loaded map, "
                             "for a site whose world is not delivered into this server. Spawns a "
                             "CesiumSunSky if the map has none")
    parser.add_argument("--utc-offset", type=float,
                        help="the site's civil UTC offset in hours, for the epoch the audit sets. "
                             "Defaults to the zone the sun is spawned with, longitude/15")
    parser.add_argument("--report-clock-gap", action="store_true",
                        help="also report what the sun reads at the site's civil clock versus at "
                             "the longitude/15 clock a generated world spawns its sun with")
    return parser.parse_args()


def report_clock_gap(audit: SolarAudit, civil_offset: float, logger: logging.Logger) -> None:
    """What the sun's spawned zone costs, read from the engine rather than modelled.

    A generated world spawns its sun at `longitude / 15`, which is local mean solar time. A scenario
    that declares a civil clock and never sets the zone is therefore rendered at a different instant
    than the one it declares. This sets the same wall reading under both zones and reports the two
    elevations.
    """
    mean_solar_offset = SolarPositionModel.estimate_time_zone_for_longitude(audit.longitude)
    gap_minutes = abs(mean_solar_offset - civil_offset) * 60.0
    logger.info("   civil offset UTC%+.2f against the spawned zone UTC%+.6f -- %.2f minutes",
                civil_offset, mean_solar_offset, gap_minutes)
    logger.info("   %-24s%14s%14s%12s", "epoch", "civil clock", "spawned zone", "difference")
    for year, month, day in AUDIT_DATES:
        for hour in AUDIT_HOURS:
            civil, _ = audit.audit_point(year, month, day, hour, civil_offset)
            spawned, _ = audit.audit_point(year, month, day, hour, mean_solar_offset)
            logger.info("   %04d-%02d-%02d %05.2f       %13.2f %13.2f %11.2f",
                        year, month, day, hour, civil.engine_elevation_deg,
                        spawned.engine_elevation_deg,
                        spawned.engine_elevation_deg - civil.engine_elevation_deg)


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("probe_solar_audit")

    package = WorldPackageReader(args.package)
    latitude, longitude = package.origin
    civil_offset = (args.utc_offset if args.utc_offset is not None
                    else SolarPositionModel.estimate_time_zone_for_longitude(longitude))

    client = carlanet.Client(args.host, args.port)
    client.set_timeout(args.timeout)
    logger.info("server %s, client %s", client.get_server_version(), client.get_client_version())

    if args.delivered_world:
        delivered = client.list_delivered_worlds()
        logger.info("delivered worlds: %s", delivered)
        client.load_delivered_world(args.delivered_world)
    world = client.get_world()
    logger.info("map %s", world.get_map().name)

    if args.georeference:
        token = os.environ.get("CESIUM_ION_TOKEN", "")
        # No tileset asset ids: the sun is computed from the georeference origin alone, and asking
        # for imagery here would stream a site's worth of tiles to measure an angle.
        if not world.configure_cesium_georeference(latitude, longitude, ion_token=token,
                                                   ion_asset_id=0, ground_ion_asset_id=0,
                                                   refresh=False):
            logger.error("could not establish a georeference at %.5f, %.5f", latitude, longitude)
            return 2

    audit = SolarAudit(world, package.map_name, latitude, longitude, logger=logger)
    result = audit.run(list(AUDIT_DATES), list(AUDIT_HOURS), civil_offset)
    logger.info("%s", SolarAudit.describe(result))

    if args.report_clock_gap:
        report_clock_gap(audit, civil_offset, logger)

    if not result.inputs_agree:
        logger.error("the sun did not hold the epoch it was given; the angles measure nothing")
        return 1
    if result.worst_elevation_residual_deg > RESOLUTION_FLOOR_DEGREES:
        logger.error("the engine's sun and the model disagree by %.4f deg, above the %.4f deg "
                     "floor the comparison can resolve",
                     result.worst_elevation_residual_deg, RESOLUTION_FLOOR_DEGREES)
        return 1
    logger.info("the engine's sun agrees with the model to within the resolution floor")
    return 0


if __name__ == "__main__":
    sys.exit(main())
