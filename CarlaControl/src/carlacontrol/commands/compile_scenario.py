"""Compile a scenario specification against its world package into a scenario package.

    carla-compile-scenario Import/MyScenario.scenario.json --out-dir Build/scenarios/MyScenario

(From a checkout, `python CarlaControl/scripts/compile_scenario.py` is the same tool.)

Writes `<scenario_id>.rou.xml`, `.sumocfg`, the world's network, `.supervision.json`, `.lock.json` and
`.resolution.json` / `.resolution.md` into the output directory, or -- when any check refuses -- only
the resolution report, naming every refusal. Exit status 0 when compiled, 1 when refused.

    carla-compile-scenario --sweep Import/MySweep.sweep.json --out-dir Build/scenarios/MySweep

compiles every member of a sweep into its own directory and writes `<sweep_id>.sweep-index.json`.

`--write-checks`, `--write-schema` and `--write-sweep-schema` publish `checks.json`,
`scenario.schema.json` and `sweep.schema.json`, generated from the compiler itself, for the authoring
skill (`07_Scenario_Authoring.md` §8.3, §8.5); `--write-vehicles-reference` publishes
`references/vehicles.md`, the classes and bodies of the measured vehicle catalogue (`--catalogue`,
this repository's from a checkout, the one installed with carlacontrol otherwise) with whether each
body's headlights, brake lights and turn signals light up.

The SUMO that routes the scenario is the one this repository stages (`Build/sumo-install`) unless
`--sumo-home` names another; installed, with no repository, it is the one `SUMO_HOME` or PATH names.
A release other than the one that converted the world is refused (check 6): a different `duarouter`
release can route the same demand differently. `--allow-sumo-version-mismatch` compiles anyway, and
the lock records that the mismatch was accepted.
"""
from __future__ import annotations

import argparse
import logging
import os
import sys
from pathlib import Path

from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue
from carlacontrol.ScenarioCompiler import ScenarioCompiler
from carlacontrol.ScenarioSchema import ScenarioSchema
from carlacontrol.ScenarioSweep import ScenarioSweep
from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.ToolLayout import ToolLayout
from carlacontrol.VehicleCatalogue import VehicleCatalogue
from carlacontrol.VehicleReference import VehicleReference

LAYOUT = ToolLayout.current()
STAGED_SUMO = LAYOUT.staged_sumo
SHIPPED_CATALOGUE = LAYOUT.catalogue


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog=os.path.basename(sys.argv[0]), description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("specification", nargs="?", type=Path,
                        help="the <Scenario>.scenario.json to compile")
    parser.add_argument("--out-dir", type=Path, help="where the scenario package is written")
    staged = STAGED_SUMO if STAGED_SUMO is not None and STAGED_SUMO.exists() else None
    parser.add_argument("--sumo-home", type=Path, default=staged,
                        help="SUMO installation providing duarouter (default: Build/sumo-install "
                             "from a checkout; installed, SUMO_HOME, then PATH)")
    parser.add_argument("--allow-sumo-version-mismatch", action="store_true",
                        help="compile when that SUMO is not the release that converted the world; "
                             "the lock records the acceptance")
    parser.add_argument("--skip-dry-run", action="store_true",
                        help="skip the SUMO-only run that refuses a planned vehicle SUMO never "
                             "inserts (check 59), for quick iteration on a draft; the lock records "
                             "that it was skipped, and a capture run refuses the scenario unless "
                             "it accepts that (run check 54)")
    parser.add_argument("--write-checks", type=Path, metavar="PATH",
                        help="write checks.json, generated from the compiler's check catalogue")
    parser.add_argument("--write-schema", type=Path, metavar="PATH",
                        help="write scenario.schema.json, the compiler's own schema")
    parser.add_argument("--write-sweep-schema", type=Path, metavar="PATH",
                        help="write sweep.schema.json, the schema a sweep is checked against")
    parser.add_argument("--write-vehicles-reference", type=Path, metavar="PATH",
                        help="write references/vehicles.md, the catalogue's classes and bodies with "
                             "whether each body's headlights, brake lights and turn signals light up")
    parser.add_argument("--catalogue", type=Path, default=SHIPPED_CATALOGUE,
                        help="the measured vehicle catalogue the vehicles reference is written from "
                             "(default: CarlaControl/catalogue/vehicles.catalogue.json from a "
                             "checkout, the one installed with carlacontrol otherwise)")
    parser.add_argument("--sweep", type=Path, metavar="PATH",
                        help="compile the members of a <Sweep>.sweep.json instead of one scenario")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    if args.write_checks:
        logging.info("wrote %s", ScenarioCheckCatalogue.write_checks_json(args.write_checks))
    if args.write_schema:
        logging.info("wrote %s", ScenarioSchema.write(args.write_schema))
    if args.write_sweep_schema:
        logging.info("wrote %s", ScenarioSweep.write_schema(args.write_sweep_schema))
    if args.write_vehicles_reference:
        logging.info("wrote %s", VehicleReference.write(args.write_vehicles_reference,
                                                        VehicleCatalogue.load(args.catalogue)))
    if args.specification is None and args.sweep is None:
        return 0 if (args.write_checks or args.write_schema or args.write_sweep_schema
                     or args.write_vehicles_reference) else 2
    if args.out_dir is None:
        logging.error("--out-dir is required to compile")
        return 2
    installation = SumoInstallation.locate(args.sumo_home)
    if args.sweep is not None:
        index = ScenarioSweep(installation, args.allow_sumo_version_mismatch,
                              args.skip_dry_run).compile(
            args.sweep, args.out_dir)
        for finding in index["findings"]:
            (logging.error if finding["outcome"] == "refuse" else logging.warning)(
                "check %s %s %s: %s", finding["check"], finding["outcome"].upper(),
                finding["subject"], finding["message"])
        logging.info("%d members; index %s; %s", len(index["members"]), index["path"],
                     index["outcome"])
        return 1 if index["outcome"] == "refused" else 0
    result = ScenarioCompiler(installation, args.allow_sumo_version_mismatch,
                              args.skip_dry_run).compile(
        args.specification, args.out_dir)
    for finding in result.findings.findings:
        (logging.error if finding.outcome == "refuse" else logging.warning)("%s", finding)
    for role, path in sorted(result.files.items()):
        logging.info("%-13s %s", role, path)
    logging.info("%s", "REFUSED" if result.refused else "compiled")
    return 1 if result.refused else 0
