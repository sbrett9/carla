#!/usr/bin/env python3
"""Emit Cursor-on-Target telemetry for every vehicle in a SUMO scenario.

Runs the scenario through TraCI and writes one CoT event per vehicle per update to any combination
of three sinks, in a single pass:

  * a UDP socket -- unicast to a listener, or the TAK situational-awareness multicast group,
  * one XML file holding every event,
  * a CSV with one row per vehicle per update, for use as a plain dataset.

The events use the same formatter as the CARLA truth producer, so they follow the schema in
`Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md` and can be compared with CARLA truth
directly. Positions are converted by the running simulation itself, and heights come from the world
package's bare-earth grid when one is found next to the scenario.

Examples:
    # dataset only: every vehicle, once a second, to XML and CSV
    python sumo_cot_telemetry.py --xml orbit_cot.xml --csv orbit_cot.csv

    # live to the TAK multicast group while watching it in the GUI
    python sumo_cot_telemetry.py --udp 239.2.3.1:6969 --gui --rate 2

    # live to one listener, and keep the dataset at the same time
    python sumo_cot_telemetry.py --udp 127.0.0.1:6969 --csv orbit_cot.csv
"""
import argparse
import logging
import sys
from datetime import UTC, datetime
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SumoCotBridge import (  # noqa: E402  (needs the path above)
    BareEarthGrid,
    CotOutputSettings,
    SumoCotBridge,
)
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402

DEFAULT_MAP = "Gardnerville_Centerville_Lane"
DEFAULT_CONFIG = _REPO / "Import" / f"{DEFAULT_MAP}_NeighborhoodOrbit.sumocfg"
DEFAULT_BARE_EARTH = _REPO / "Build" / "world-packages" / f"{DEFAULT_MAP}.bareearth.bin"
# Falls back to the SUMO built inside this repository when SUMO_HOME is not set.
REPO_SUMO = _REPO / "Build" / "sumo-src"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG,
                        help="SUMO configuration to run (default: the Gardnerville orbit)")
    parser.add_argument("--udp", metavar="HOST:PORT",
                        help="send each event to this address. Multicast works as-is; the TAK "
                             "situational-awareness group is 239.2.3.1:6969")
    parser.add_argument("--udp-ttl", type=int, default=1,
                        help="multicast time-to-live, ignored for unicast (default 1)")
    parser.add_argument("--xml", type=Path, help="write every event to this XML file")
    parser.add_argument("--csv", type=Path, help="write one row per vehicle per update here")
    parser.add_argument("--rate", type=float, default=1.0,
                        help="updates per vehicle per second (default 1.0)")
    parser.add_argument("--stale", type=float, default=3.0,
                        help="seconds before an event goes stale (default 3.0)")
    parser.add_argument("--affiliation", default="n",
                        help="CoT affiliation for ambient traffic: n neutral, f friend, h hostile, "
                             "u unknown (default n)")
    parser.add_argument("--marked-vehicle", default="orbiter",
                        help="vehicle to flag in the dataset's `marked` column (default orbiter)")
    parser.add_argument("--marked-affiliation",
                        help="give the marked vehicle a different affiliation so it stands out")
    parser.add_argument("--uid-prefix", default="SUMO-TRUTH",
                        help="prefix for every event's uid (default SUMO-TRUTH)")
    parser.add_argument("--epoch",
                        help="UTC instant that simulation time zero maps to, as ISO-8601, for a "
                             "reproducible dataset (default: the clock when the run starts)")
    parser.add_argument("--real-time-factor", type=float, default=0.0, metavar="FACTOR",
                        help="pace the simulation against the wall clock: 1 makes a second of "
                             "simulation take a second, 2 runs at twice that. The default, 0, runs "
                             "as fast as the machine allows (about 30x on this scenario). Use it "
                             "for a live feed; leave it off to write a dataset quickly")
    parser.add_argument("--end", type=float,
                        help="stop after this many seconds of simulation")
    parser.add_argument("--bare-earth", type=Path, default=DEFAULT_BARE_EARTH,
                        help="world package bare-earth grid supplying ellipsoidal height")
    parser.add_argument("--hae", type=float, default=0.0,
                        help="single height for every vehicle when no bare-earth grid is used")
    parser.add_argument("--no-bare-earth", action="store_true",
                        help="ignore the grid and use --hae for every vehicle")
    parser.add_argument("--gui", action="store_true",
                        help="run sumo-gui so the traffic can be watched while it emits")
    parser.add_argument("--sumo-home",
                        help="SUMO installation providing traci and the sumo executable. "
                             "Defaults to $SUMO_HOME, then this repository's own build, then PATH")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    if not (args.udp or args.xml or args.csv):
        logging.error("nothing to do: give at least one of --udp, --xml or --csv")
        return 1

    host, port = None, 6969
    if args.udp:
        host, _, text = args.udp.partition(":")
        port = int(text) if text else 6969

    grid = None
    if not args.no_bare_earth and args.bare_earth and args.bare_earth.exists():
        grid = BareEarthGrid.from_file(args.bare_earth)
        logging.info("bare-earth heights from %s (%d x %d at %.0f m, origin %.1f m)",
                     args.bare_earth.name, grid.columns, grid.rows, grid.cell_size,
                     grid.origin_height)
    else:
        logging.info("no bare-earth grid: every vehicle reported at %.1f m", args.hae)

    epoch = None
    if args.epoch:
        epoch = datetime.fromisoformat(args.epoch.replace("Z", "+00:00"))
        if epoch.tzinfo is None:
            epoch = epoch.replace(tzinfo=UTC)

    try:
        installation = SumoInstallation.locate(args.sumo_home, extra_candidates=[REPO_SUMO])
    except FileNotFoundError as error:
        logging.error("%s", error)
        return 1
    logging.info("SUMO from %s", installation.home)
    bridge = SumoCotBridge(installation, args.config, bare_earth=grid, constant_hae=args.hae,
                           use_gui=args.gui)
    settings = CotOutputSettings(
        udp_host=host, udp_port=port, udp_ttl=args.udp_ttl,
        xml_path=args.xml, csv_path=args.csv, rate_hz=args.rate, stale_seconds=args.stale,
        affiliation=args.affiliation, uid_prefix=args.uid_prefix,
        marked_vehicle=args.marked_vehicle, marked_affiliation=args.marked_affiliation,
        epoch=epoch)

    try:
        report = bridge.run(settings, end_time=args.end,
                            extra_sumo_args=["--start", "--quit-on-end"] if args.gui else None,
                            real_time_factor=args.real_time_factor)
    except (OSError, ValueError, ImportError) as error:
        logging.error("%s", error)
        return 1

    logging.info("wrote %d events across %d updates for %d vehicles",
                 report.events, report.updates, report.vehicles)
    for path in (args.xml, args.csv):
        if path:
            logging.info("  %s  (%.1f MB)", path, path.stat().st_size / 1e6)
    if host:
        logging.info("  sent to %s:%d", host, port)
    return 0


if __name__ == "__main__":
    sys.exit(main())
