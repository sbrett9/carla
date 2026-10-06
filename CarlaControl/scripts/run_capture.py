#!/usr/bin/env python3
"""Capture a window of a compiled SUMO scenario in a generated CARLA world: the operator's front end.

    run_capture --scenario gardnerville_morning --window morning
    run_capture --scenario gardnerville_morning --window morning --solar advance
    run_capture --run configs/gardnerville_morning.run.json --set capture.prewarm_s=600
    run_capture --run <runs>/<session>/run.effective.json          (reproduce a run)
    run_capture --run configs/g.run.json --caller unattended --result out/g.result.json

A run binds a **compiled** scenario package (`compile_scenario.py`) and the world package it was
compiled against; it never compiles a scenario or builds a world. Everything the scenario already
knows -- its epoch, its windows, its SUMO step and seed, its illumination default -- comes from its
lock; everything about this machine comes from the site profile; the run configuration and the
options below say the rest. Every field of the effective configuration records the layer that set it
(12_Operator_Control_Surface.md §3.5-§3.7).

Before anything is acquired the configuration is checked offline and the launch echo is
printed: the civil span, the sun the session will bind and where, the captures and their cost, and
the warnings. An attended launch stops for a person only when a warning was raised that no
`on_warning.<code>` adjudicates; an unattended one never stops -- it refuses instead. `--validate-only`
stops after the offline checks.

The world must already be running and loaded (RunCarlaServer, then the world built and loaded with
run_SCTMV.py). The session takes its clock, hides the generated road and signal layers, drives the
vehicles from SUMO, and gives everything back when the run ends however it ends.

Each channel's camera is named by its `sensor_id` -- or, a single channel given none, by the server,
`Camera_<n>` -- and every capture in the channel's directory is `<name>_<local capture time>`, with
the name as its platform track's callsign. The server refuses a name a live camera in the world
already holds, which refuses the run at pre-roll.

Exit status, read from the run result's outcome:
  0 run_finished       the window's end, or the scenario's, was reached
  1 usage_error        the invocation could not be resolved
  2 refused_offline    the offline checks refused; no server was contacted
  3 refused_server     the server checks refused, or the session did before its lease
  4 refused_authority  another holds the world's population lease; the result names it
  5 refused_preroll    refused after the lease, before the window: the fast-forward, the sun,
                       a prewarm tick, the cameras, the pace, the traffic a stare aims at
  6 run_stopped        stopped before its end: a signal, a loud condition, write headroom
  7 internal_error     an unhandled fault

To stop a run cleanly send SIGINT or SIGTERM on Linux, and Ctrl+C or CTRL_BREAK_EVENT on Windows; a
second signal abandons the shutdown and exits at once. The result, the resolution report, the lock
and the replayable effective configuration are written beside --result, or under paths.runs_root.
"""
from __future__ import annotations

import argparse
import json
import logging
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402

LAYOUT_ROOT = _REPO


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    """The command line. Every option that sets a field is generated from the field table."""
    parser = argparse.ArgumentParser(
        prog="run_capture", description=__doc__, epilog=RunConfiguration.help_text(),
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--run", metavar="FILE",
                        help="a run configuration document (layer 5)")
    fields = parser.add_argument_group(
        "fields", "Short forms of --set for the fields an operator changes most; applied before "
                  "--set, which is applied in the order given.")
    for path, spec in RunConfiguration.FIELDS.items():
        if spec.alias is None:
            continue
        choices = spec.schema.get("enum")
        default = "no default" if not spec.has_default else f"default {json.dumps(spec.default)}"
        fields.add_argument(spec.alias, dest=path.replace(".", "__"), choices=choices,
                            metavar=None if choices else "VALUE",
                            help=f"sets {path} ({default}). {spec.help}")
    parser.add_argument("--set", dest="set_fields", action="append", default=[],
                        metavar="PATH=VALUE", help="set any field, e.g. capture.prewarm_s=600")
    parser.add_argument("--site-profile", metavar="FILE",
                        help="this machine's site profile; unset, it is derived from the layout "
                             "run_capture runs from")
    parser.add_argument("--validate-only", action="store_true",
                        help="stop after the offline checks: resolve, check, print the echo, write "
                             "the resolution report and the lock")
    parser.add_argument("--write-schema", metavar="PATH",
                        help="write the run configuration's JSON schema and exit")
    parser.add_argument("--write-site-profile", metavar="PATH",
                        help="write this machine's site profile as a file to edit, and exit")
    parser.add_argument("--log-level", default="INFO",
                        choices=("DEBUG", "INFO", "WARNING", "ERROR"))
    return parser.parse_args(argv)


def override_texts(args: argparse.Namespace) -> list[tuple[str, str]]:
    """Each alias given, then each --set, as `path=value` with the text it came from."""
    texts = []
    for path, spec in RunConfiguration.FIELDS.items():
        value = getattr(args, path.replace(".", "__"), None) if spec.alias else None
        if value is not None:
            texts.append((f"{path}={value}", f"{spec.alias} {value}"))
    texts += [(text, f"--set {text}") for text in args.set_fields]
    return texts


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=args.log_level, format="%(message)s", stream=sys.stdout)
    if args.write_schema:
        logging.info("wrote %s", RunConfiguration.write_schema(args.write_schema))
        return 0
    try:
        site = SiteProfile.discover(LAYOUT_ROOT, args.site_profile)
    except ValueError as problem:
        logging.error("usage_error: %s", problem)
        return 1
    if args.write_site_profile:
        logging.info("wrote %s", site.write_template(args.write_site_profile))
        return 0
    result = CaptureSession(site, args.run, override_texts(args)).run(
        validate_only=args.validate_only)
    return 0 if result is None else result.exit_status


if __name__ == "__main__":
    sys.exit(main())
