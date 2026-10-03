"""The Arapahoe underpass dwell in `Import/` is exactly what its generator compiles today.

`make_arapahoe_scenario.py` writes the dwell as a specification and compiles it
(`07_Scenario_Authoring.md` §3.4.2). The compiled package it writes into `Import/` is what the owner
runs and watches, so it must be the generator's output and not a hand-edited or stale copy: rerun
against the same world package, the generator must give the same route file, configuration, lane
closures and supervision plan byte for byte, and the same lock but for the digest of the
specification, whose relative paths name another output directory.

It also holds what the port promised: every vehicle type in the route file names a measured body,
and the incident is in the package rather than lost on the way to it.

What it cannot see: whether the scenario still behaves as intended. That is measured in SUMO when the
scenario changes (07 §3.4.2), not asserted here.
"""
from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.ScenarioVehicleMix import ScenarioVehicleMix  # noqa: E402
from carlacontrol.VehicleCatalogue import BLUEPRINT_PARAM, VehicleCatalogue  # noqa: E402

GENERATOR = _REPO / "CarlaControl" / "scripts" / "make_arapahoe_scenario.py"
PACKAGE = _REPO / "Build" / "world-packages" / "Arapahoe_I25.cwp"
STAGED_SUMO = _REPO / "Build" / "sumo-install"
CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
IMPORT = _REPO / "Import"
SCENARIO = "Arapahoe_I25_UnderpassDwell"
BYTE_FOR_BYTE = (f"{SCENARIO}.rou.xml", f"{SCENARIO}.sumocfg", f"{SCENARIO}.add.xml",
                 f"{SCENARIO}.supervision.json")
# The network is the world package's, copied beside the scenario, and compared by its canonical
# fingerprint, as the session compares it: its bytes carry netconvert's build stamp.
NETWORK = "Arapahoe_I25.net.xml"


def test_the_shipped_dwell_is_what_its_generator_compiles(tmp_path):
    if not PACKAGE.exists() or not STAGED_SUMO.exists():
        pytest.skip("the Arapahoe world package or the staged SUMO is not here")
    if not (IMPORT / f"{SCENARIO}.lock.json").exists():
        pytest.fail(f"Import/ carries no compiled {SCENARIO}: run make_arapahoe_scenario.py")
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
        lock["files"]["network"].pop("sha256")
    assert regenerated == shipped
    specification = json.loads((tmp_path / f"{SCENARIO}.scenario.json").read_text(encoding="utf-8"))
    shipped_specification = json.loads((IMPORT / f"{SCENARIO}.scenario.json").read_text(
        encoding="utf-8"))
    for spec in (specification, shipped_specification):
        spec["world"].pop("package")
        spec.pop("catalogue")
    assert specification == shipped_specification


def test_every_vehicle_type_the_shipped_dwell_draws_is_a_measured_body():
    """A type naming no measured blueprint is simulated and never rendered, so a drive of a
    scenario carrying one would draw an empty road where it should draw that traffic."""
    routes = IMPORT / f"{SCENARIO}.rou.xml"
    catalogue = VehicleCatalogue.load(CATALOGUE)
    checked = ScenarioVehicleMix.check_route_file(routes, catalogue)
    root = ET.parse(routes).getroot()
    bound = {v.get("id"): next(p.get("value") for p in v.iter("param")
                               if p.get("key") == BLUEPRINT_PARAM) for v in root.iter("vType")}
    assert set(bound) == set(checked)
    drawn = set()
    for distribution in root.iter("vTypeDistribution"):
        drawn |= set(distribution.get("vTypes").split())
    drawn |= {v.get("type") for v in root.iter("vehicle")}
    assert drawn <= set(bound) | {d.get("id") for d in root.iter("vTypeDistribution")}
    assert bound["marked.vehicle.jeep.wrangler_rubicon"] == "vehicle.jeep.wrangler_rubicon"


def test_the_shipped_dwell_carries_its_incident_and_its_marked_vehicle():
    config = ET.parse(IMPORT / f"{SCENARIO}.sumocfg").getroot()
    assert config.find("input/additional-files").get("value") == f"{SCENARIO}.add.xml"
    interval = ET.parse(IMPORT / f"{SCENARIO}.add.xml").getroot().find("rerouter/interval")
    assert (float(interval.get("begin")), float(interval.get("end"))) == (900.0, 1080.0)
    assert [c.get("id") for c in interval] == [f"1001791386_{lane}" for lane in range(5)]
    marked = next(v for v in ET.parse(IMPORT / f"{SCENARIO}.rou.xml").getroot().iter("vehicle")
                  if v.get("id") == "marked")
    stop = marked.find("stop")
    assert (stop.get("lane"), stop.get("duration"), stop.get("parking")) == (
        "218965860#0_0", "1800.00", "true")
    lock = json.loads((IMPORT / f"{SCENARIO}.lock.json").read_text(encoding="utf-8"))
    assert lock["files"]["additional"]["path"] == f"{SCENARIO}.add.xml"
    # The supervision plan is bound to the closures as the lock is, by the file's digest.
    digest = hashlib.sha256((IMPORT / f"{SCENARIO}.add.xml").read_bytes()).hexdigest()
    plan = json.loads((IMPORT / f"{SCENARIO}.supervision.json").read_text(encoding="utf-8"))
    assert plan["additional_digest"] == lock["files"]["additional"]["sha256"] == digest


def test_sumo_runs_the_shipped_incident_on_the_one_lane_it_leaves_open(tmp_path):
    """Read back from SUMO: northbound I-25 past the interchange carries traffic on its other lanes
    before the incident and after it, and on its leftmost lane alone once the closure has held for
    30 s -- the vehicles already on a closed lane have left it by then -- until it lifts. Run to
    1 200 s at a 0.25 s step to keep the test short; what is asserted does not depend on the step."""
    sumo = STAGED_SUMO / "bin" / ("sumo.exe" if os.name == "nt" else "sumo")
    if not sumo.exists():
        pytest.skip("the staged SUMO is not here")
    edges = tmp_path / "edges.txt"
    edges.write_text("edge:1001791386\n", encoding="utf-8")
    fcd = tmp_path / "fcd.xml"
    completed = subprocess.run(
        [str(sumo), "-c", str(IMPORT / f"{SCENARIO}.sumocfg"), "--end", "1200", "--step-length",
         "0.25", "--fcd-output", str(fcd), "--fcd-output.filter-edges.input-file", str(edges),
         "--no-step-log", "true", "--no-warnings", "true"],
        capture_output=True, text=True, timeout=600, check=False)
    assert completed.returncode == 0, completed.stderr[-3000:]
    lanes: dict[str, set[str]] = {"before": set(), "closed": set(), "after": set()}
    for _, step in ET.iterparse(fcd):
        if step.tag != "timestep":
            continue
        t = float(step.get("time"))
        window = "before" if t < 900 else "after" if t >= 1080 else "closed" if t >= 930 else None
        if window:
            lanes[window] |= {v.get("lane") for v in step.iter("vehicle")
                              if v.get("lane").startswith("1001791386_")}
        step.clear()
    assert lanes["closed"] == {"1001791386_5"}
    assert len(lanes["before"]) > 1 and len(lanes["after"]) > 1
