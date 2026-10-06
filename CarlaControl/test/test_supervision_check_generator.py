"""The supervision check in `Import/`: what its generator compiles today, every kind of supervision a
vehicle can carry, and -- with no CARLA -- the whole path from the plan to the run's manifest.

`make_supervision_check_scenario.py` writes a six-minute scenario on the Arapahoe world, the world the
owner checks supervision on live, and compiles it with the scenario compiler. Held here:

* **`Import/` is the generator's output**, byte for byte, as the shipped scenarios are
  (`07_Scenario_Authoring.md` §8.5), the run configuration written beside it included.
* **The plan carries an annotated dwell, an annotated transit and a nominal matched negative**, each
  interval anchored to the event that commits it, and no series, because the Arapahoe package
  publishes no area of interest a slot could be sited at.
* **Its two capture windows lie in two illumination bands**, and every planned vehicle passes the
  compile's SUMO-only run.
* **The plan binds in a session**: driven world-less by a real SUMO, as the binder's own tests are, the
  binder opens and closes each interval on the event it names, with the closing reason each one
  should have; and two runs at different SUMO steps write manifests `diff_run_manifests.py` finds name
  the same supervision rows (06 D6.8).

What these cannot see: the imagery. That is the owner's live check, which the run configuration sets
up.
"""
from __future__ import annotations

import importlib.util
import json
import math
import os
import subprocess
import sys
import uuid
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next imports name
from CarlaNet.CoSim import SumoDriveSession, SumoDriveSessionOptions  # noqa: E402
from CarlaNet.Types.Supervision import CoreVocabulary  # noqa: E402
from System import Func  # noqa: E402

from carlacontrol.IlluminationBand import IlluminationBand  # noqa: E402
from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402

GENERATOR = _REPO / "CarlaControl" / "scripts" / "make_supervision_check_scenario.py"
DIFF = _REPO / "CarlaControl" / "scripts" / "diff_run_manifests.py"
PACKAGE = _REPO / "Build" / "world-packages" / "Arapahoe_I25.cwp"
STAGED_SUMO = _REPO / "Build" / "sumo-install"
CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
IMPORT = _REPO / "Import"
SCENARIO = "Arapahoe_I25_SupervisionCheck"
BYTE_FOR_BYTE = (f"{SCENARIO}.rou.xml", f"{SCENARIO}.sumocfg", f"{SCENARIO}.supervision.json",
                 f"{SCENARIO}.run.json")
NETWORK = "Arapahoe_I25.net.xml"


def load_generator():
    spec = importlib.util.spec_from_file_location("make_supervision_check_scenario", GENERATOR)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def require_world() -> None:
    if not PACKAGE.exists() or not STAGED_SUMO.exists():
        pytest.skip("the Arapahoe world package or the staged SUMO is not here")


def shipped(suffix: str) -> dict:
    return json.loads((IMPORT / f"{SCENARIO}.{suffix}").read_text(encoding="utf-8"))


def intervals_by_phase(plan: dict) -> dict[str, dict]:
    return {interval["phase"]: interval for instance in plan["instances"]
            for interval in instance["intervals"]}


# ---- the scenario in Import/ ------------------------------------------------------------------------

def test_the_shipped_check_is_what_its_generator_compiles(tmp_path):
    require_world()
    if not (IMPORT / f"{SCENARIO}.lock.json").exists():
        pytest.fail(f"Import/ carries no compiled {SCENARIO}: run make_supervision_check_scenario.py")
    done = subprocess.run([sys.executable, str(GENERATOR), "--out-dir", str(tmp_path)],
                          capture_output=True, text=True, env=dict(os.environ), timeout=600)
    assert done.returncode == 0, done.stdout[-3000:] + done.stderr[-3000:]
    for name in BYTE_FOR_BYTE:
        assert (tmp_path / name).read_bytes() == (IMPORT / name).read_bytes(), name
    assert (NetworkFingerprint.of_file(tmp_path / NETWORK)
            == NetworkFingerprint.of_file(IMPORT / NETWORK)), NETWORK
    regenerated = json.loads((tmp_path / f"{SCENARIO}.lock.json").read_text(encoding="utf-8"))
    lock = shipped("lock.json")
    for each in (lock, regenerated):
        each.pop("specification_sha256")
        each["files"]["network"].pop("sha256")
    assert regenerated == lock
    specification = json.loads((tmp_path / f"{SCENARIO}.scenario.json").read_text(encoding="utf-8"))
    shipped_specification = shipped("scenario.json")
    for spec in (specification, shipped_specification):
        spec["world"].pop("package")
        spec.pop("catalogue")
    assert specification == shipped_specification


def test_the_plan_carries_a_dwell_a_transit_and_a_nominal_matched_negative():
    plan = shipped("supervision.json")
    instances = {row["instance_id"].split("/", 1)[1]: row for row in plan["instances"]}
    assert {name: row["supervision"] for name, row in instances.items()} == {
        "kerbside_dwell": "annotated", "through_transit": "annotated", "brief_stop": "nominal"}
    phases = intervals_by_phase(plan)
    dwell = phases["dwell"]
    assert (dwell["anchor"]["start"]["event"], dwell["anchor"]["end"]["event"]) == (
        "stop:0", "stop_end:0")
    # A duration stop declares its length and no instant (06 D6.4).
    assert (dwell["declared_start_s"], dwell["declared_duration_s"]) == (None, 120.0)
    assert phases["transit"]["anchor"] == {"start": {"event": "depart"}, "end": None}
    assert phases["transit"]["declared_start_s"] == 90.0
    assert (phases["past_the_kerb"]["anchor"]["start"]["event"],
            phases["past_the_kerb"]["anchor"]["end"]["event"]) == ("phase:1", "phase:2")
    assert instances["brief_stop"]["hard_negative_for"] == ["check:kerbside_dwell"]
    assert instances["kerbside_dwell"]["parameters"] == {"dwell_s": 120}
    # No series: a slot is sited at an area, and the world publishes none. Every row is a vehicle's.
    assert plan["series"] == [] and all(row["participants"] for row in plan["instances"])
    if PACKAGE.exists():
        areas = json.loads(zipfile.ZipFile(PACKAGE).read("areas.resolved.json"))["areas"]
        assert areas == []


def test_its_two_windows_lie_in_two_illumination_bands_and_every_planned_vehicle_enters():
    report = shipped("resolution.json")
    bands = {window["id"]: IlluminationBand.of(window["sun_open"]["elevation_deg"])
             for window in report["capture_windows"]}
    assert bands == {"dwell_golden": "golden", "stop_day": "day"}
    dry_run = shipped("lock.json")["dry_run"]
    assert dry_run["ran"] and dry_run["planned_vehicles"] == {"total": 3, "inserted": 3}
    assert dry_run["vehicles"]["discarded"] == 0 and dry_run["collisions"] == 0


def test_the_run_configuration_captures_the_dwell_s_window_through_a_camera_over_the_kerb():
    document = shipped("run.json")
    parsed = RunConfiguration.from_document(document, f"{SCENARIO}.run.json")
    assert parsed.values["capture.window"] == "dwell_golden"
    assert parsed.values["scenario_package"] == f"Import/{SCENARIO}.lock.json"
    (channel,) = document["capture"]["channels"]
    assert channel["sensor_id"] == "Check_Overhead_1"
    report = shipped("resolution.json")
    kerb = report["places"]["kerb_dwell"]
    assert (kerb["lane"], kerb["end_pos"]) == ("427819553#0_0", 70.0)
    # The camera looks at the kerb midway between the two stops, 15 m from each.
    network = (IMPORT / NETWORK).read_text(encoding="utf-8")
    generator = load_generator()
    look_at = (channel["stare_look_at_x_m"], channel["stare_look_at_y_m"])
    assert look_at == generator.kerb_point(network, 55.0)
    assert math.dist(look_at, generator.kerb_point(network, 70.0)) == pytest.approx(15.0, abs=0.2)
    assert math.dist(look_at, generator.kerb_point(network, 40.0)) == pytest.approx(15.0, abs=0.2)


# ---- the plan, bound in a session with no world ------------------------------------------------------

def drive(tmp_path: Path, step_s: float) -> tuple[list, list[str], Path]:
    """The compiled scenario run to its end by a real SUMO with no CARLA, writing its manifest."""
    manifest = tmp_path / f"manifest_{step_s:g}.jsonl"
    options = SumoDriveSessionOptions(str(IMPORT / f"{SCENARIO}.sumocfg"), str(PACKAGE),
                                      str(CATALOGUE), "test://" + uuid.uuid4().hex)
    options.TickWorld = Func[bool](lambda: True)
    options.SumoStepOverrideSeconds = step_s
    options.SumoHome = str(STAGED_SUMO)
    options.RunManifestPath = str(manifest)
    session = SumoDriveSession.Start(options)
    try:
        while session.Advance():
            pass
        intervals = list(session.SupervisionBinder.Intervals)
        defects = [str(defect) for defect in session.SupervisionBinder.Defects]
    finally:
        session.Dispose()
    return intervals, defects, manifest


def test_the_plan_binds_and_two_runs_at_different_steps_name_the_same_rows(tmp_path):
    """Each interval opens on the event its anchor names and closes as it should: the two stops on
    their ends, the stretch past the kerb on entering the next phase, the transit when the car
    leaves the map. A run at a 0.1 s step binds the same rows as one at the compiled 0.05 s."""
    require_world()
    bound = {}
    manifests = []
    for step_s in (0.05, 0.1):
        intervals, defects, manifest = drive(tmp_path, step_s)
        assert defects == []
        bound[step_s] = {interval.Phase: interval for interval in intervals}
        manifests.append(manifest)
    for step_s, intervals in bound.items():
        closing = {phase: CoreVocabulary.Name(interval.ClosedBy)
                   for phase, interval in intervals.items()}
        assert closing == {"dwell": "trigger", "transit": "entity_arrived",
                           "past_the_kerb": "trigger", "stop": "trigger"}, step_s
        dwell = intervals["dwell"]
        assert dwell.CommittedEndSeconds - dwell.CommittedStartSeconds == pytest.approx(120.0)
        stop = intervals["stop"]
        assert stop.CommittedEndSeconds - stop.CommittedStartSeconds == pytest.approx(20.0)
        # The transit passes the kerb while the car is waiting at it.
        assert dwell.CommittedStartSeconds < intervals["past_the_kerb"].CommittedStartSeconds \
            < dwell.CommittedEndSeconds
        assert intervals["transit"].CommittedStartSeconds == pytest.approx(90.0, abs=step_s + 1e-6)
    compared = subprocess.run([sys.executable, str(DIFF), *map(str, manifests)],
                              capture_output=True, text=True, env=dict(os.environ), timeout=120)
    assert compared.returncode == 0, compared.stdout[-3000:] + compared.stderr[-3000:]
    assert "the two runs name the same supervision rows" in compared.stderr + compared.stdout
