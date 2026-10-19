#!/usr/bin/env python3
"""Bind the sun to a declared epoch on a running server, audit it tick by tick, and read it back --
at both the Arapahoe and the Bahonar port sites.

Why both sites: a sun left at the zone a generated world configures it with, longitude/15, is local
mean solar time. At Arapahoe that is 0.46 minutes from civil time and moves nothing a check can see;
at the Bahonar port it is 14.72 minutes, which at 17:00 on the December solstice is the difference
between a sun below the horizon and one above it. A check at one site passes whether or not the
civil-time binding works.

What it does, per site, on whatever map the server has loaded:

  1. moves the Cesium georeference to the site's origin, as `probe_solar_audit.py --georeference`
     does (no tilesets are streamed; the sun is computed from the origin alone), and records the sun
     it finds -- the date is not reset by that, so it is whatever the last session left;
  2. takes the world's clock (synchronous, 0.05 s) and binds the sun with `SolarLease` to 17:00 on
     2026-12-21 at the site's civil offset under `freeze_at_window_start`, exactly as a SUMO drive
     session does when its window opens;
  3. audits the sun with `SolarAudit` when the window opens and on each of --ticks ticks, from the
     world-observer snapshot;
  4. reads `get_solar_state` back through the public API and compares the date, the clock, the zone
     and both elevations against what was declared and against the engine's readings taken on
     2026-09-21;
  5. gives the sun and the clock back.

--advance also opens three advancing windows at each site and requires every one to hold under the
audit for four seconds of simulated time: one two seconds before 07:01, across the minute, and two
two seconds before civil midnight, one on an epoch whose calendar advances and one on an epoch whose
calendar is held. Under `advance` the session writes the sun for every frame before its tick cue, at
the whole second nearest the frame's declared instant and a millisecond past it, with the engine's
own advance off. The minute is the case that matters: `ACesiumSunSky::GetHMSFromSolarTime` rounds
the seconds to sixty and drops the minute they carry into, so a sun the engine advanced itself is
lit by the sun of 07:00:00 from 07:00:59.5 and the audit stops it there. The midnight windows check
the date the session writes: the next day when the calendar advances, the epoch's own date when it
is held.

--capture spawns a camera, records a few frames of the frozen window and checks that each sidecar's
<_solar> block carries the refraction-corrected elevation the widened observer header delivers. The
<_illumination> block is written by a SUMO drive session (run_sumo_drive.py), not by this script.

Requires the carlanet wheel built from this tree (the CarlaNet.CoSim sun types) and, for the per-tick
corrected elevation, a server built with the widened episode-state header. Both run on an older
server; they then say the corrected elevation was compared only when the window opened.

Usage:
    python test_sun_binding.py [--host 127.0.0.1] [--port 2000] [--ticks 40] [--advance] [--capture]
"""
import argparse
import json
import logging
import os
import sys
import tempfile
import time
import xml.etree.ElementTree as ElementTree

import carlanet

try:  # The sun types ship in the rebuilt wheel; an older one lacks them.
    from CarlaNet.CoSim import (
        CarlaClientWorld,
        DeclaredSun,
        IlluminationPolicy,
        SolarAudit,
        SolarAuditFailedException,
        SolarEpoch,
        SolarLease,
        SolarPositionModel,
        SolarReading,
        WorldSettingsLease,
    )
except ImportError as missing:
    sys.exit(f"the installed carlanet wheel has no sun binding ({missing}); rebuild it from this tree")

WORLD_DELTA = 0.05
WINDOW_OPENS = 17 * 3600.0          # 17:00 civil, simulated seconds after the epoch's midnight

# The advancing windows: what each crosses, where it opens (simulated seconds after the epoch's
# midnight, two seconds before the crossing), whether the epoch's calendar advances, and the date the
# sun should hold once the crossing is passed.
ADVANCE_WINDOWS = (
    ("07:01", 7 * 3600.0 + 58.0, True, (2026, 12, 21)),
    ("midnight, calendar advancing", 86_400.0 - 2.0, True, (2026, 12, 22)),
    ("midnight, calendar held", 86_400.0 - 2.0, False, (2026, 12, 21)),
)
ADVANCE_TICKS = 80                  # four seconds of simulated time

# The origins stage C audited, and what the engine returned there at 17:00 on 2026-12-21 at the
# civil offset: geometric and refraction-corrected elevation, degrees.
SITES = (
    ("Arapahoe", 39.59431, -104.88449,
     '{"epoch_version": 1, "civil_datetime": "2026-12-21T00:00:00-07:00", "utc_offset_hours": -7,'
     ' "utc_datetime": "2026-12-21T07:00:00Z", "calendar_advances": true, "dst_in_effect": false,'
     ' "time_zone_id": "America/Denver"}',
     -4.43510, -4.36070),
    ("Bahonar", 27.15012, 56.18065,
     '{"epoch_version": 1, "civil_datetime": "2026-12-21T00:00:00+03:30", "utc_offset_hours": 3.5,'
     ' "utc_datetime": "2026-12-20T20:30:00Z", "calendar_advances": true, "dst_in_effect": false,'
     ' "time_zone_id": "Asia/Tehran"}',
     -1.58296, -1.37418),
)

# Stage C's resolution floor: a reading nearer the engine's measured value than this is agreement.
AGREEMENT_DEGREES = 0.01


def unwrap(nullable):
    """A .NET nullable struct as its value, whichever way the bridge handed it over."""
    return nullable.Value if hasattr(nullable, "HasValue") else nullable


class SunBindingCheck:
    """Binds, audits and reads back the sun at one site."""

    def __init__(self, world, logger: logging.Logger, ticks: int) -> None:
        self.world = world
        self.logger = logger
        self.ticks = ticks
        self.adapter = CarlaClientWorld.Attach(world._client, True)

    def georeference(self, latitude: float, longitude: float) -> None:
        token = os.environ.get("CESIUM_ION_TOKEN", "")
        if not self.world.configure_cesium_georeference(latitude, longitude, ion_token=token,
                                                        ion_asset_id=0, ground_ion_asset_id=0,
                                                        refresh=False):
            raise RuntimeError(f"could not establish a georeference at {latitude}, {longitude}")

    def run_frozen(self, site: str, latitude: float, longitude: float, epoch_json: str,
                   measured_elevation: float, measured_corrected: float) -> bool:
        """The frozen terminator window: bind, audit, read back, give back. True where it all agreed."""
        self.georeference(latitude, longitude)
        # On demand: the observer cache still holds the snapshot from before the georeference moved.
        found = SolarReading.From(self.adapter.ReadSolarState())
        self.logger.info("%s: sun as found after configuring the georeference: %s", site, found)

        epoch = SolarEpoch.FromJson(epoch_json)
        declared = DeclaredSun(epoch, IlluminationPolicy.FreezeAtWindowStart(False, True, None),
                               WINDOW_OPENS)
        settings = WorldSettingsLease.Take(self.adapter, WORLD_DELTA)
        lease = None
        agreed = True
        try:
            lease = SolarLease.Take(self.adapter, declared)
            audit = SolarAudit(declared, latitude, longitude)
            opened = audit.AuditWindowOpen(unwrap(lease.AtWindowOpen))
            for tick in range(self.ticks):
                if self.adapter.Tick() is None:
                    raise RuntimeError(f"{site}: tick {tick} produced no frame")
                audit.AuditTick(tick, WINDOW_OPENS + tick * WORLD_DELTA,
                                self.adapter.ObservedSolarState())

            read = self.world.get_solar_state()
            self.logger.info("%s: bound %s", site, lease)
            self.logger.info("%s: get_solar_state reads back %s", site, read)
            self.logger.info(
                "%s: audited %d ticks, corrected elevation carried on %d of them; worst direction "
                "%.3e deg, worst clock %+.6f s; window open corrected residual %s", site,
                audit.AuditedTicks, audit.TicksWithCorrectedElevation,
                audit.WorstAngle.AngleResidualDegrees, audit.WorstClock.ClockResidualSeconds,
                opened.CorrectedResidualDegrees)

            checks = {
                "date": (read["year"], read["month"], read["day"]) == (2026, 12, 21),
                "clock": abs(read["solar_time"] - (17.0 + 0.001 / 3600.0)) < 1e-9,
                "zone": abs(read["time_zone"] - epoch.UtcOffsetHours) < 1e-9,
                "not advancing": not read["advancing"],
                "geometric elevation": abs(read["sun_elevation_deg"] - measured_elevation)
                < AGREEMENT_DEGREES,
            }
            if read["sun_corrected_elevation_deg"] is not None:
                checks["corrected elevation"] = (
                    abs(read["sun_corrected_elevation_deg"] - measured_corrected)
                    < AGREEMENT_DEGREES)
            for name, passed in checks.items():
                self.logger.info("%s:   %-20s %s", site, name, "agrees" if passed else "DISAGREES")
                agreed = agreed and passed

            # What the civil offset is worth here, from the model the audit is pinned to.
            mean_solar = SolarPositionModel.AtEngineClock(latitude, longitude, longitude / 15.0,
                                                          2026, 12, 21, 17.0)
            self.logger.info("%s: at the longitude/15 zone the same clock would be %.4f deg "
                             "geometric -- %.4f deg from the declared sun", site,
                             mean_solar.ElevationDegrees,
                             mean_solar.ElevationDegrees - measured_elevation)
        except SolarAuditFailedException as failed:
            self.logger.error("%s: the audit stopped the window: %s", site, failed.Message)
            agreed = False
        finally:
            if lease is not None:
                lease.Dispose()
            settings.Dispose()
        return agreed

    def run_advancing(self, site: str, latitude: float, longitude: float, epoch_json: str,
                      crossing: str, opens: float, calendar_advances: bool,
                      date_after: tuple[int, int, int]) -> bool:
        """An advancing window across a crossing. True where the audit held on every tick and the
        sun crossed it onto the declared date."""
        self.georeference(latitude, longitude)
        epoch = json.loads(epoch_json)
        epoch["calendar_advances"] = calendar_advances
        declared = DeclaredSun(SolarEpoch.FromJson(json.dumps(epoch)),
                               IlluminationPolicy.Advance(1.0, True, None), opens)
        settings = WorldSettingsLease.Take(self.adapter, WORLD_DELTA)
        lease = None
        try:
            lease = SolarLease.Take(self.adapter, declared)
            audit = SolarAudit(declared, latitude, longitude)
            audit.AuditWindowOpen(unwrap(lease.AtWindowOpen))
            held = []
            for tick in range(ADVANCE_TICKS):
                # Written before the cue, as SumoDriveSession.Advance writes it beside the poses.
                rendered = opens + tick * WORLD_DELTA
                lease.WriteForFrame(rendered)
                if self.adapter.Tick() is None:
                    raise RuntimeError(f"{site}: advancing tick {tick} produced no frame")
                sample = audit.AuditTick(tick, rendered, self.adapter.ObservedSolarState())
                held.append(unwrap(sample.Observed.LocalInstant))

            first, last = held[0], held[-1]
            checks = {
                "held every tick": audit.AuditedTicks == ADVANCE_TICKS,
                "crossed": (first.Minute, last.Minute) == (0, 1) if crossing == "07:01"
                else (first.Hour, last.Hour) == (23, 0),
                "date after": (last.Year, last.Month, last.Day) == date_after,
            }
            self.logger.info(
                "%s: advancing across %s, %d ticks: the sun held %s to %s; worst clock %+.4f s, "
                "worst direction %.2e deg, tolerances %.3f s and %.3f deg", site, crossing,
                audit.AuditedTicks, first.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                last.ToString("yyyy-MM-dd HH:mm:ss.fff"), audit.WorstClock.ClockResidualSeconds,
                audit.WorstAngle.AngleResidualDegrees, audit.ToleranceSeconds,
                audit.ToleranceDegrees)
            for name, passed in checks.items():
                self.logger.info("%s:   %-20s %s", site, name, "agrees" if passed else "DISAGREES")
            return all(checks.values())
        except SolarAuditFailedException as failed:
            self.logger.error("%s: the audit stopped the window advancing across %s: %s", site,
                              crossing, failed.Message)
            return False
        finally:
            if lease is not None:
                lease.Dispose()
            settings.Dispose()

    def run_capture(self, site: str, latitude: float, longitude: float, epoch_json: str) -> bool:
        """Record a few frames of a frozen window and check each sidecar's sun block."""
        self.georeference(latitude, longitude)
        declared = DeclaredSun(SolarEpoch.FromJson(epoch_json),
                               IlluminationPolicy.FreezeAtWindowStart(False, True, None), WINDOW_OPENS)
        settings = WorldSettingsLease.Take(self.adapter, WORLD_DELTA)
        lease = camera = None
        corrected = None
        directory = tempfile.mkdtemp(prefix=f"sun-binding-{site.lower()}-")
        try:
            lease = SolarLease.Take(self.adapter, declared)
            corrected = unwrap(lease.AtWindowOpen).CorrectedElevationDegrees
            blueprint = self.world.get_blueprint_library().find("sensor.camera.rgb")
            blueprint.set_attribute("image_size_x", "320")
            blueprint.set_attribute("image_size_y", "180")
            camera = self.world.spawn_actor(blueprint, carlanet.Transform(
                carlanet.Location(x=0.0, y=0.0, z=300.0),
                carlanet.Rotation(pitch=-90.0, yaw=0.0, roll=0.0)))
            self.world.start_recording(camera, directory, 20.0)
            for _ in range(40):
                self.adapter.Tick()
            self.world.stop_recording()
            time.sleep(0.5)
        finally:
            if camera is not None:
                camera.destroy()
            if lease is not None:
                lease.Dispose()
            settings.Dispose()

        if corrected is None:
            self.logger.error("%s: the world reported no corrected elevation to compare", site)
            return False
        sidecars = sorted(name for name in os.listdir(directory) if name.endswith(".xml"))
        carried = 0
        for name in sidecars:
            block = ElementTree.parse(os.path.join(directory, name)).getroot().find("_solar")
            value = None if block is None else block.get("sun_corrected_elevation_deg")
            if value is not None and abs(float(value) - corrected) < 0.001:
                carried += 1
        self.logger.info("%s: %d captures in %s, %d with the refraction-corrected elevation in "
                         "<_solar>", site, len(sidecars), directory, carried)
        return bool(sidecars) and carried == len(sidecars)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--timeout", type=float, default=60.0)
    parser.add_argument("--ticks", type=int, default=40, help="ticks audited per frozen window")
    parser.add_argument("--advance", action="store_true",
                        help="also run advancing windows across a minute and across midnight")
    parser.add_argument("--capture", action="store_true",
                        help="also record a few frames and check the sidecar's sun block")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("test_sun_binding")

    client = carlanet.Client(args.host, args.port)
    client.set_timeout(args.timeout)
    world = client.get_world()
    logger.info("server %s, map %s", client.get_server_version(), world.get_map().name)

    check = SunBindingCheck(world, logger, args.ticks)
    agreed = True
    for site, latitude, longitude, epoch_json, elevation, corrected in SITES:
        agreed = check.run_frozen(site, latitude, longitude, epoch_json, elevation, corrected) and agreed
        if args.advance:
            for crossing, opens, calendar_advances, date_after in ADVANCE_WINDOWS:
                agreed = check.run_advancing(site, latitude, longitude, epoch_json, crossing, opens,
                                             calendar_advances, date_after) and agreed
        if args.capture:
            agreed = check.run_capture(site, latitude, longitude, epoch_json) and agreed

    logger.info("the sun held what was declared at both sites" if agreed
                else "the sun did NOT hold what was declared at every site")
    return 0 if agreed else 1


if __name__ == "__main__":
    sys.exit(main())
