"""Emit Cursor-on-Target telemetry for every vehicle in a SUMO scenario.

Runs the scenario through TraCI and writes one CoT event per vehicle per update to any combination
of three sinks, in a single pass:

  * a UDP socket -- unicast to a listener, or the TAK situational-awareness multicast group,
  * one XML file holding every event,
  * a CSV with one row per vehicle per update, for use as a plain dataset.

The events use the same formatter as the CARLA truth producer, so they follow the same CoT schema
and can be compared with CARLA truth directly. Positions are converted by the running simulation itself, and heights come from the world
package's bare-earth grid when one is found next to the scenario.

The XML and CSV are the truth sidecar and carry the whole record: the author's own names for its
vehicle types and flows, and which vehicles it planted, in a `marked` field of their own. A
vehicle's `special_type` is its kind and nothing else, planted or not: the kind the measured vehicle
catalogue (`--catalogue`) curates for the CARLA blueprint the vehicle's type names, as the capture
path's truth reports it, and empty for a type that names none. Its `base_type` is the catalogue's
for the same blueprint, and its vehicle class's only for a type that names none of the catalogue's.
The UDP feed is a moving-map display and carries what a display needs. A compiled scenario's labels are its
`*.supervision.json`, joined to the sidecar by vehicle id; a legacy scenario names its planted
vehicles in the `*.labels.json` given to `--labels`.

Each vehicle's CoT affiliation comes from the run's display convention, a file giving each vehicle
population the affiliation a TAK client draws it with: civilian traffic neutral and military
friendly, say. It is a run setting and not part of the scenario, and it names populations -- a
compiled scenario's vehicle classes, or a hand-written route file's vehicle types -- never vehicles.
`--display-convention` names one; without it the run uses `<scenario>.display.json` beside the
`.sumocfg` when there is one, as the Bahonar pattern of life's is, and says which file it used.
Either way the convention wins outright over a `--labels` file's affiliations. Without a convention,
a legacy `*.labels.json` supplies the display half of its own affiliation map; the `u` it gave every
anomaly type was the answer written into the CoT type, and is not applied. Without either, every
vehicle takes `--affiliation`.

An anomaly that is an *absence* -- a guard who never arrives -- has no vehicle to attach to either
channel, so the run writes the scenario's described gaps to a supervision sidecar beside its output,
with each window placed on the epoch this run stamped.

Examples (from a checkout, `python CarlaControl/scripts/sumo_cot_telemetry.py` is the same tool,
and `--config` defaults to the Gardnerville orbit under Import/; installed, it must be given):
    # dataset only: every vehicle, once a second, to XML and CSV
    carla-cot-telemetry --config orbit.sumocfg --xml orbit_cot.xml --csv orbit_cot.csv

    # live to the TAK multicast group while watching it in the GUI
    carla-cot-telemetry --config orbit.sumocfg --udp 239.2.3.1:6969 --gui --rate 2

    # live to one listener, and keep the dataset at the same time
    carla-cot-telemetry --config orbit.sumocfg --udp 127.0.0.1:6969 --csv orbit_cot.csv

    # the Bahonar port live, civilian traffic neutral and the naval base friendly: the convention
    # beside the configuration, Shahid_Bahonar_Port_PatternOfLife.display.json, is found by name
    carla-cot-telemetry --config Import/Shahid_Bahonar_Port_PatternOfLife.sumocfg \\
        --udp 239.2.3.1:6969
"""
import argparse
import json
import logging
import os
import sys
from datetime import UTC, datetime
from pathlib import Path

from carlacontrol.CotDisplayConvention import CotDisplayConvention
from carlacontrol.SumoCotBridge import (
    BareEarthGrid,
    CotOutputSettings,
    RunReport,
    SumoCotBridge,
)
from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.SupervisionSidecar import (
    LABELS_KEY,
    SupervisionSidecar,
)
from carlacontrol.ToolLayout import ToolLayout
from carlacontrol.VehicleCatalogue import VehicleCatalogue

LAYOUT = ToolLayout.current()
# The Gardnerville orbit is a checkout's example and its default; an installed command has no
# Import/ or Build/ to take it from, so there --config is required and no bare-earth grid is assumed.
DEFAULT_MAP = "Gardnerville_Centerville_Lane"
DEFAULT_CONFIG = (None if LAYOUT.installed
                  else LAYOUT.import_directory / f"{DEFAULT_MAP}_NeighborhoodOrbit.sumocfg")
DEFAULT_BARE_EARTH = (None if LAYOUT.installed
                      else LAYOUT.world_package_directory / f"{DEFAULT_MAP}.bareearth.bin")
DEFAULT_CATALOGUE = LAYOUT.catalogue
# Falls back to the SUMO built inside this repository when SUMO_HOME is not set; none when installed.
REPO_SUMO = [home for _, home in LAYOUT.repository_sumo_builds() if home.name == "sumo-src"]


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.path.basename(sys.argv[0]), description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG,
                        required=DEFAULT_CONFIG is None,
                        help="SUMO configuration to run (default from a checkout: the Gardnerville "
                             "orbit; installed, required)")
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
                        help="CoT affiliation for a vehicle population the display convention does "
                             "not name, and for every vehicle when there is none: n neutral, "
                             "f friend, h hostile, u unknown (default n)")
    parser.add_argument("--display-convention", type=Path, metavar="FILE",
                        help="the run's display convention: a JSON file giving each vehicle "
                             "population its CoT affiliation, so a TAK client draws, say, civilian "
                             "traffic neutral and military friendly. A population is a compiled "
                             "scenario's vehicle class, or a hand-written route file's vehicle "
                             "type. A run setting, not part of the scenario. Default: "
                             "<scenario>.display.json beside --config when there is one. Given or "
                             "found, it replaces the affiliations a --labels file carries")
    parser.add_argument("--marked-affiliation",
                        help="show the planted vehicles with a different affiliation in the live "
                             "feed, so an operator watching a TAK client can pick them out. It "
                             "reaches --udp only, never --xml or --csv: a recorded CoT type "
                             "carries the population's affiliation and nothing else, and the files "
                             "say which vehicles were planted in their marked field")
    parser.add_argument("--marked-vehicle", default="orbiter",
                        help="the vehicle this scenario planted. It is counted in the run summary "
                             "and recorded in the written files' marked field; its special_type, "
                             "and its CoT type in the files, are those of any vehicle of its kind "
                             "(default orbiter)")
    parser.add_argument("--labels", type=Path,
                        help="a legacy scenario's *.labels.json: names several planted vehicles at "
                             "once, and gives the display affiliation per vehicle type when no "
                             "display convention is given or found. The u it gives every anomaly "
                             "type is not applied: it is the answer written into the CoT type, not "
                             "a display affiliation, and those types take --affiliation")
    parser.add_argument("--supervision", type=Path,
                        help="write the scenario's described supervision gaps -- the anomalies that "
                             "are absences, with no vehicle to attach them to -- to this file, with "
                             "each window placed on the run's own clock. Defaults to a file beside "
                             "--xml or --csv. It is written beside the dataset and never into it: a "
                             "note saying which post stood unmanned between which hours is the "
                             "answer to the question the dataset asks")
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
                        help="world package bare-earth grid supplying ellipsoidal height (default "
                             "from a checkout: the Gardnerville world's under Build/world-packages; "
                             "installed, none)")
    parser.add_argument("--hae", type=float, default=0.0,
                        help="single height for every vehicle when no bare-earth grid is used")
    parser.add_argument("--no-bare-earth", action="store_true",
                        help="ignore the grid and use --hae for every vehicle")
    parser.add_argument("--catalogue", type=Path,
                        help="the measured vehicle catalogue: each vehicle's base_type and "
                             "special_type are the ones it curates for the CARLA blueprint the "
                             "vehicle's type names, as in the capture path's truth. Default: this "
                             "repository's catalogue from a checkout, the one installed with "
                             "carlacontrol otherwise; without one every special_type is empty and "
                             "every base_type its SUMO vehicle class's")
    parser.add_argument("--gui", action="store_true",
                        help="run sumo-gui so the traffic can be watched while it emits")
    parser.add_argument("--sumo-home",
                        help="SUMO installation providing traci and the sumo executable. "
                             "Defaults to $SUMO_HOME, then this repository's own build when run "
                             "from a checkout, then PATH")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    if args.marked_affiliation and not args.udp:
        logging.error("--marked-affiliation styles the live feed; give --udp, or drop it")
        return 2

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

    try:
        catalogue = vehicle_catalogue(args)
    except ValueError as error:
        logging.error("%s", error)
        return 1

    epoch = None
    if args.epoch:
        epoch = datetime.fromisoformat(args.epoch.replace("Z", "+00:00"))
        if epoch.tzinfo is None:
            epoch = epoch.replace(tzinfo=UTC)

    labels: dict = {}
    marked_ids: frozenset[str] = frozenset()
    if args.labels:
        labels = json.loads(args.labels.read_text(encoding="utf-8"))
        marked_ids = frozenset(labels.get("marked_ids", []))
        logging.info("labels %s: %d marked ids, %d described gap(s)",
                     args.labels.name, len(marked_ids), len(labels.get(LABELS_KEY) or []))
    try:
        convention = display_convention(args, labels)
    except (OSError, ValueError) as error:
        logging.error("%s", error)
        return 1

    try:
        installation = SumoInstallation.locate(args.sumo_home, extra_candidates=REPO_SUMO)
    except FileNotFoundError as error:
        logging.error("%s", error)
        return 1
    logging.info("SUMO from %s", installation.home)
    bridge = SumoCotBridge(installation, args.config, bare_earth=grid, constant_hae=args.hae,
                           use_gui=args.gui, catalogue=catalogue)
    settings = CotOutputSettings(
        udp_host=host, udp_port=port, udp_ttl=args.udp_ttl,
        xml_path=args.xml, csv_path=args.csv, rate_hz=args.rate, stale_seconds=args.stale,
        affiliation=args.affiliation, uid_prefix=args.uid_prefix,
        marked_vehicle=args.marked_vehicle,
        marked_affiliation=args.marked_affiliation,
        marked_ids=marked_ids, affiliation_by_type=convention.affiliation_by_type,
        display_convention=convention.source, epoch=epoch)

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
    _write_supervision(args, labels, report)
    return 0


def vehicle_catalogue(args: argparse.Namespace) -> VehicleCatalogue | None:
    """The vehicle catalogue each vehicle's kinds are read from, and which one it is, said in the log.

    `--catalogue` names one, and one that does not read stops the run. Without it this repository's
    catalogue is used when it is present, the one its scenario generators compile against. With
    neither, every vehicle's `special_type` is empty, because SUMO reports no kind, and its
    `base_type` is its vehicle class's.
    """
    path = args.catalogue
    if path is None and DEFAULT_CATALOGUE.is_file():
        path = DEFAULT_CATALOGUE
    if path is None:
        logging.info("no vehicle catalogue: every vehicle's special_type is empty and its base_type "
                     "its vehicle class's")
        return None
    try:
        catalogue = VehicleCatalogue.load(path)
    except (OSError, ValueError) as error:
        raise ValueError(f"the vehicle catalogue {path} cannot be read: {error}") from error
    logging.info("vehicle kinds from catalogue %s (%s)", path, catalogue.catalogue_id)
    return catalogue


def display_convention(args: argparse.Namespace, labels: dict) -> CotDisplayConvention:
    """The display convention this run draws with, and which one it is, said in the log.

    `--display-convention` names one. Without it, `<scenario>.display.json` beside the configuration
    is used when it exists, and is then treated exactly as though it had been named: a file that
    does not read is refused, and it wins outright over a labels file's affiliations. So a scenario
    kept with its convention draws the same whether or not the operator remembers the flag, and
    the log says which file drew it. Without a convention, a legacy labels file supplies the display
    half of its own affiliation map and never the anomaly half; without either, every vehicle takes
    `--affiliation`.
    """
    path = args.display_convention
    beside = CotDisplayConvention.beside(args.config)
    if path is None and beside.is_file():
        path = beside
    if path is not None:
        convention = CotDisplayConvention.from_file(path)
        logging.info("display convention %s%s: %d populations; any other takes %s", path,
                     "" if args.display_convention else ", found beside the configuration",
                     len(convention), args.affiliation)
        if args.labels and labels.get("affiliation_by_type"):
            logging.info("  it replaces the affiliations in %s", args.labels.name)
        return convention
    missing = f"no display convention: none given and no {beside.name} beside the configuration"
    if not args.labels:
        logging.info("%s; every vehicle takes %s", missing, args.affiliation)
        return CotDisplayConvention({})
    logging.info("%s", missing)
    convention = CotDisplayConvention.from_legacy_labels(labels, source=args.labels.name)
    logging.info("display affiliations from %s: %d types; any other takes %s",
                 convention.source, len(convention), args.affiliation)
    if convention.withheld:
        logging.warning(
            "  not applying the u %s gives %s: in a labels file u marks an anomaly type, and a "
            "CoT type carries display affiliation, not the answer. They take %s",
            convention.source, ", ".join(convention.withheld), args.affiliation)
    return convention


def _write_supervision(args: argparse.Namespace, labels: dict,
                       report: RunReport) -> None:
    """Carry the scenario's described absences out to a truth sidecar beside the run's output.

    An anomaly that is an absence has no vehicle, so nothing in the events or rows can carry it and
    nothing did: the labels held it and no consumer ever read it. Here it is written out with its
    window placed on the epoch this run stamped, so a consumer holding the dataset can find the hours
    the gap covers in it.
    """
    sidecar = SupervisionSidecar.from_labels(
        labels, scenario=args.config.stem, epoch=report.epoch, labels_path=args.labels)
    if not sidecar:
        return
    output = args.supervision or next(
        (SupervisionSidecar.default_path(path) for path in (args.xml, args.csv) if path), None)
    if output is None:
        # A live feed writes no files, so the only place left to put it is the operator's screen.
        logging.warning("%d described supervision gap(s) reached no file, because this run wrote "
                        "none; give --supervision to keep them:\n%s",
                        len(sidecar), sidecar.describe())
        return
    logging.info("  %s  (%d described supervision gap(s))", sidecar.write(output), len(sidecar))
