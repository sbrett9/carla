"""The Gardnerville orbit in `Import/` is exactly what its generator compiles today.

`make_sumo_scenario.py` writes the orbit as a specification and compiles it. The compiled package it
writes into `Import/` is what the owner runs and watches, so it must be the generator's output and
not a hand-edited or stale copy: rerun against the same world package, the generator must give the
same route file, configuration, network and supervision plan byte for byte, and the same lock but
for the digest of the specification, whose relative paths name another output directory. This is
the canary the authoring skill names for shared-code edits.

What it cannot see: whether the scenario still behaves as intended. That is measured in SUMO when the
scenario changes, not asserted here.
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402

GENERATOR = _REPO / "CarlaControl" / "scripts" / "make_sumo_scenario.py"
PACKAGE = _REPO / "Build" / "world-packages" / "Gardnerville_Centerville_Lane.cwp"
STAGED_SUMO = _REPO / "Build" / "sumo-install"
IMPORT = _REPO / "Import"
SCENARIO = "Gardnerville_Centerville_Lane_NeighborhoodOrbit"
BYTE_FOR_BYTE = (f"{SCENARIO}.rou.xml", f"{SCENARIO}.sumocfg", f"{SCENARIO}.supervision.json",
                 f"{SCENARIO}.run.json")
# The network is the world package's, copied beside the scenario: its bytes carry netconvert's
# "generated on" stamp from the world build, so rebuilding the world changes them while the
# network stays the same. It is compared by its canonical fingerprint, as the session compares it.
NETWORK = "Gardnerville_Centerville_Lane.net.xml"


def test_the_shipped_orbit_is_what_its_generator_compiles(tmp_path):
    if not PACKAGE.exists() or not STAGED_SUMO.exists():
        pytest.skip("the Gardnerville world package or the staged SUMO is not here")
    if not (IMPORT / f"{SCENARIO}.lock.json").exists():
        pytest.fail(f"Import/ carries no compiled {SCENARIO}: run make_sumo_scenario.py")
    done = subprocess.run([sys.executable, str(GENERATOR), "--out-dir", str(tmp_path)],
                          capture_output=True, text=True, env=dict(os.environ), timeout=600)
    assert done.returncode == 0, done.stdout[-3000:] + done.stderr[-3000:]
    for name in BYTE_FOR_BYTE:
        assert (tmp_path / name).read_bytes() == (IMPORT / name).read_bytes(), name
    assert (NetworkFingerprint.of_file(tmp_path / NETWORK)
            == NetworkFingerprint.of_file(IMPORT / NETWORK)), NETWORK
    shipped = json.loads((IMPORT / f"{SCENARIO}.lock.json").read_text(encoding="utf-8"))
    regenerated = json.loads((tmp_path / f"{SCENARIO}.lock.json").read_text(encoding="utf-8"))
    for lock in (shipped, regenerated):
        lock.pop("specification_sha256")
        lock.pop("producer", None)  # the record of what made the file names its own time of writing
        lock["files"]["network"].pop("sha256")
    assert regenerated == shipped
    specification = json.loads((tmp_path / f"{SCENARIO}.scenario.json").read_text(encoding="utf-8"))
    shipped_specification = json.loads((IMPORT / f"{SCENARIO}.scenario.json").read_text(
        encoding="utf-8"))
    for spec in (specification, shipped_specification):
        spec["world"].pop("package")
        spec.pop("catalogue")
    assert specification == shipped_specification


def load_generator():
    spec = importlib.util.spec_from_file_location("make_sumo_scenario", GENERATOR)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_the_run_file_orbits_the_middle_of_the_block_the_orbiter_laps():
    """The run file beside the scenario flies one camera round the block, centred on the middle of
    the box around the loop's corners, and names the scenario as a checkout's run file does."""
    if not (IMPORT / f"{SCENARIO}.run.json").exists():
        pytest.fail(f"Import/ carries no {SCENARIO}.run.json: run make_sumo_scenario.py")
    document = json.loads((IMPORT / f"{SCENARIO}.run.json").read_text(encoding="utf-8"))
    parsed = RunConfiguration.from_document(document, f"{SCENARIO}.run.json")
    assert parsed.values["capture.window"] == "600:840"
    assert parsed.values["scenario_package"] == f"Import/{SCENARIO}.lock.json"
    (orbit,) = document["capture"]["channels"]
    assert (orbit["sensor_id"], orbit["pattern"]) == ("Block_Orbit_1", "orbit")
    generator = load_generator()
    network = (IMPORT / NETWORK).read_text(encoding="utf-8")
    centre = generator.block_centre(network)
    assert (orbit["orbit_centre_x_m"], orbit["orbit_centre_y_m"]) == centre
    # The centre lies inside the block: between the loop's corners on both axes.
    from carlacontrol.LanePoint import LanePoint
    corners = [LanePoint(network).at_edge(edge, "end") for edge in generator.ORBIT_ROUTE.loop]
    assert min(x for x, _ in corners) < centre[0] < max(x for x, _ in corners)
    assert min(y for _, y in corners) < centre[1] < max(y for _, y in corners)
