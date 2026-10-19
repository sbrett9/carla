"""Check which SUMO a tool on this machine resolves, and that the staged toolchain is complete.

Two separate questions, both of which have been answered wrongly here before:

  * **Which SUMO is this?** More than one can be installed. CarlaSetup builds and stages a pinned one
    inside the repository; an operating-system-wide install sets `SUMO_HOME`, which wins by design
    (it is what `traci` itself reads). This prints the resolved path, its version and which rule
    matched, so the answer is never a guess. Run it once with the inherited environment and once with
    `--sumo-home Build/sumo-install` and compare: if the two disagree, every world built by one is
    being authored against by the other.
  * **Is the toolchain complete?** `netconvert` alone is enough to convert a map, so a half-built
    installation looks healthy right up to the point something asks for `sumo`, `duarouter` or the
    `traci` module. This runs each of them.

Usage (from a checkout, `python CarlaControl/scripts/test_sumo_toolchain.py` is the same tool):
    carla-check-sumo                                   # whatever this machine resolves
    carla-check-sumo --sumo-home Build/sumo-install    # the repository's pinned build
    carla-check-sumo --expect-version 1.27.0           # refuse anything else
    carla-check-sumo --expect-version 1.27.0 --allow-version-mismatch

Installed, there is no repository build to fall back on: the installation is the one `--sumo-home`,
`SUMO_HOME` or PATH names. A CARLA distribution's environment step sets `SUMO_HOME` to its bundled
toolchain.

Exits non-zero when the resolved installation is incomplete or fails an expected-version check.
"""
import argparse
import logging
import os
import subprocess
import sys

from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.ToolLayout import ToolLayout

# The SUMO built inside this repository, used when nothing else declares one; none when installed.
# The source tree is offered as well as the staged install because SUMO's own build leaves bin/,
# data/ and tools/ side by side there, so it resolves as a complete installation even before staging
# has run.
REPO_CANDIDATES = [str(home) for _, home in ToolLayout.current().repository_sumo_builds()]


def check_executable(installation: SumoInstallation, name: str) -> bool:
    """Run one staged tool's --version, reporting rather than raising when it is missing."""
    try:
        path = installation.executable(name)
    except FileNotFoundError as error:
        logging.error("%s", error)
        return False
    completed = subprocess.run([str(path), "--version"], capture_output=True, text=True,
                               timeout=60, check=False)
    first_line = (completed.stdout or completed.stderr or "").splitlines()
    logging.info("  %-12s %s", name, first_line[0] if first_line else "(no output)")
    if completed.returncode != 0:
        logging.error("%s exited %s", path, completed.returncode)
        return False
    return True


def check_traci(installation: SumoInstallation) -> bool:
    """Import traci and confirm it came from this installation and not another one on PYTHONPATH."""
    try:
        traci = installation.import_traci()
    except ImportError as error:
        logging.error("%s", error)
        return False
    origin = getattr(traci, "__file__", "(unknown location)")
    logging.info("  %-12s %s", "traci", origin)
    return str(installation.tools) in origin


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog=os.path.basename(sys.argv[0]),
                                     description=__doc__.splitlines()[0])
    parser.add_argument("--sumo-home", default=None,
                        help="the SUMO installation to check; otherwise the SUMO_HOME environment "
                             "variable, then this repository's own build when run from a "
                             "checkout, then PATH")
    parser.add_argument("--expect-version", default=None,
                        help="the SUMO release this installation must be, e.g. 1.27.0 (normally the "
                             "version a world package records for the netconvert that built it)")
    parser.add_argument("--allow-version-mismatch", action="store_true",
                        help="warn instead of refusing when --expect-version does not match")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    try:
        installation = SumoInstallation.locate(args.sumo_home, extra_candidates=REPO_CANDIDATES)
    except FileNotFoundError as error:
        logging.error("%s", error)
        return 1

    try:
        installation.require_version(args.expect_version,
                                     allow_mismatch=args.allow_version_mismatch)
    except RuntimeError as error:
        logging.error("%s", error)
        return 1

    logging.info("Staged tools:")
    complete = all([check_executable(installation, "netconvert"),
                    check_executable(installation, "sumo"),
                    check_executable(installation, "duarouter"),
                    check_traci(installation)])
    if not complete:
        logging.error("The installation at %s is incomplete. Run CarlaSetup to build and stage the "
                      "whole toolchain, or point --sumo-home at one that is complete.",
                      installation.home)
        return 1

    logging.info("SUMO toolchain at %s is complete.", installation.home)
    return 0
