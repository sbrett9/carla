#!/usr/bin/env python3
"""Compile a scenario specification against its world package into a scenario package.

    python compile_scenario.py Import/MyScenario.scenario.json --out-dir Build/scenarios/MyScenario

Writes `<scenario_id>.rou.xml`, `.sumocfg`, the world's network, `.supervision.json`, `.lock.json` and
`.resolution.json` / `.resolution.md` into the output directory, or -- when any check refuses -- only
the resolution report, naming every refusal. Exit status 0 when compiled, 1 when refused.

    python compile_scenario.py --sweep Import/MySweep.sweep.json --out-dir Build/scenarios/MySweep

compiles every member of a sweep into its own directory and writes `<sweep_id>.sweep-index.json`.

`--write-checks`, `--write-schema` and `--write-sweep-schema` publish `checks.json`,
`scenario.schema.json` and `sweep.schema.json`, generated from the compiler itself, for the authoring
skill (`07_Scenario_Authoring.md` §8.3, §8.5).

The SUMO that routes the scenario is the one this repository stages (`Build/sumo-install`) unless
`--sumo-home` names another. A release other than the one that converted the world is refused (check
6): a different `duarouter` release can route the same demand differently. `--allow-sumo-version-mismatch`
compiles anyway, and the lock records that the mismatch was accepted.
"""
from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioSchema import ScenarioSchema  # noqa: E402
from carlacontrol.ScenarioSweep import ScenarioSweep  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402

STAGED_SUMO = _REPO / "Build" / "sumo-install"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("specification", nargs="?", type=Path,
                        help="the <Scenario>.scenario.json to compile")
    parser.add_argument("--out-dir", type=Path, help="where the scenario package is written")
    parser.add_argument("--sumo-home", type=Path, default=STAGED_SUMO if STAGED_SUMO.exists() else None,
                        help="SUMO installation providing duarouter (default: Build/sumo-install)")
    parser.add_argument("--allow-sumo-version-mismatch", action="store_true",
                        help="compile when that SUMO is not the release that converted the world; "
                             "the lock records the acceptance")
    parser.add_argument("--write-checks", type=Path, metavar="PATH",
                        help="write checks.json, generated from the compiler's check catalogue")
    parser.add_argument("--write-schema", type=Path, metavar="PATH",
                        help="write scenario.schema.json, the compiler's own schema")
    parser.add_argument("--write-sweep-schema", type=Path, metavar="PATH",
                        help="write sweep.schema.json, the schema a sweep is checked against")
    parser.add_argument("--sweep", type=Path, metavar="PATH",
                        help="compile the members of a <Sweep>.sweep.json instead of one scenario")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    if args.write_checks:
        logging.info("wrote %s", ScenarioCheckCatalogue.write_checks_json(args.write_checks))
    if args.write_schema:
        logging.info("wrote %s", ScenarioSchema.write(args.write_schema))
    if args.write_sweep_schema:
        logging.info("wrote %s", ScenarioSweep.write_schema(args.write_sweep_schema))
    if args.specification is None and args.sweep is None:
        return 0 if (args.write_checks or args.write_schema or args.write_sweep_schema) else 2
    if args.out_dir is None:
        logging.error("--out-dir is required to compile")
        return 2
    installation = SumoInstallation.locate(args.sumo_home)
    if args.sweep is not None:
        index = ScenarioSweep(installation, args.allow_sumo_version_mismatch).compile(
            args.sweep, args.out_dir)
        for finding in index["findings"]:
            (logging.error if finding["outcome"] == "refuse" else logging.warning)(
                "check %s %s %s: %s", finding["check"], finding["outcome"].upper(),
                finding["subject"], finding["message"])
        logging.info("%d members; index %s; %s", len(index["members"]), index["path"],
                     index["outcome"])
        return 1 if index["outcome"] == "refused" else 0
    result = ScenarioCompiler(installation, args.allow_sumo_version_mismatch).compile(
        args.specification, args.out_dir)
    for finding in result.findings.findings:
        (logging.error if finding.outcome == "refuse" else logging.warning)("%s", finding)
    for role, path in sorted(result.files.items()):
        logging.info("%-13s %s", role, path)
    logging.info("%s", "REFUSED" if result.refused else "compiled")
    return 1 if result.refused else 0


if __name__ == "__main__":
    sys.exit(main())
