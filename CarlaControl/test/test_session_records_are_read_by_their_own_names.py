"""What run_capture reads off the session is read by the names the session's own types give it.

The capture tests drive `CaptureSession` against stand-ins whose members imitate
`CarlaNet.CoSim`'s: a pose record, an admission pass, the compile lock and teleporting checks. A
stand-in that drifted from the real type would let a misspelt member pass every test and fail on the
first live run. So here the readers -- `RenderedTrafficCentre`, `WindowAdmissions`,
`RunCloseoutReport.scenario_checks_of` -- are fed the real types, built through pythonnet: pose records
and passes constructed directly, and the lock and teleporting checks made by the session's own
`ScenarioLockCheck.Require` and `TeleportingCheck.Require` on the fixture's compiled package.

The behaviour is pinned at the same time: the traffic centre is one frame's bodies, not a step's and
not a body-less pose; the window's opening pass is the first at or after its begin; and a pass is
counted for the window only once the step it governs has been rendered.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import (  # noqa: E402
    AdmissionPass,
    CoSimPoseRecord,
    LaneInterpolationCase,
    ScenarioLockCheck,
    TeleportingCheck,
    VehicleCatalogue,
    VehiclePose,
)
from RunCaptureFixture import CATALOGUE, SCENARIO_ID, Layout  # noqa: E402

from carlacontrol.RenderedTrafficCentre import RenderedTrafficCentre  # noqa: E402
from carlacontrol.RunCloseoutReport import RunCloseoutReport  # noqa: E402
from carlacontrol.WindowAdmissions import WindowAdmissions  # noqa: E402


def pose_record(tick: int, actor: int, x: float, y: float, z: float) -> CoSimPoseRecord:
    pose = VehiclePose(f"v{actor}", "vehicle.lincoln.mkz", x, y, z, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                       False)
    return CoSimPoseRecord(tick, 0.05 * tick, tick % 10 == 0, actor, pose,
                           LaneInterpolationCase.SameLane, 0.0, 0.0, 0.0)


def admission(frame_s: float, eligible: int, capacity: int = 4, tick: int = 0) -> AdmissionPass:
    admitted = min(eligible, capacity)
    return AdmissionPass(tick, frame_s, eligible + 5, eligible + 2, eligible, admitted,
                         eligible - admitted, capacity, 0, 0, 0, 0)


# -- the rendered traffic's centre ------------------------------------------------------------------

def test_the_centre_is_the_latest_frame_s_bodies_height_included():
    centre = RenderedTrafficCentre()
    centre.begin_step()
    for tick in (40, 41):
        for actor, x in ((7, 10.0 + tick), (8, 30.0 + tick)):
            centre.collect(pose_record(tick, actor, x, -5.0, 2.0 + actor))
    centre.end_step()
    assert centre.centre() == pytest.approx((61.0, -5.0, 9.5))
    assert (centre.vehicles, centre.frame_tick, centre.frame_s) == (2, 41, pytest.approx(2.05))


def test_a_pose_written_to_no_body_is_left_out():
    centre = RenderedTrafficCentre()
    centre.begin_step()
    centre.collect(pose_record(3, 7, 10.0, 0.0, 0.0))
    centre.collect(pose_record(3, 0, 9000.0, 9000.0, 900.0))
    assert centre.centre() == pytest.approx((10.0, 0.0, 0.0)) and centre.vehicles == 1


def test_nothing_is_taken_outside_a_step_and_a_new_step_forgets_the_last():
    centre = RenderedTrafficCentre()
    centre.collect(pose_record(1, 7, 10.0, 0.0, 0.0))
    assert centre.centre() is None and centre.to_dict() is None
    centre.begin_step()
    centre.collect(pose_record(2, 7, 10.0, 0.0, 0.0))
    centre.end_step()
    centre.collect(pose_record(3, 7, 500.0, 0.0, 0.0))
    assert centre.centre() == pytest.approx((10.0, 0.0, 0.0))
    centre.begin_step()
    assert centre.centre() is None


def test_an_earlier_frame_arriving_late_does_not_replace_the_latest():
    centre = RenderedTrafficCentre()
    centre.begin_step()
    centre.collect(pose_record(9, 7, 10.0, 0.0, 0.0))
    centre.collect(pose_record(8, 7, 99.0, 0.0, 0.0))
    assert centre.centre() == pytest.approx((10.0, 0.0, 0.0))


# -- the window's admission passes ------------------------------------------------------------------

def test_the_opening_pass_is_the_first_at_or_after_the_begin():
    admissions = WindowAdmissions(begin_s=100.0, end_s=110.0, step_s=1.0)
    for frame in (98.0, 99.0, 100.0, 101.0):
        admissions.observe(admission(frame, eligible=int(frame) - 90))
    opening = admissions.at_window_open
    assert (opening["sim_time_s"], opening["eligible"], opening["shed"]) == (100.0, 10, 6)
    assert opening["population"] == 15 and opening["subscribed"] == 12


def test_only_passes_whose_step_was_rendered_inside_the_window_are_counted():
    admissions = WindowAdmissions(begin_s=100.0, end_s=103.0, step_s=1.0)
    # Frames 100 to 105 arrive; 101, 102 and 103 govern steps inside 100-103. The pass for 100
    # governs the step before the window, 104 the step after it, and 105 is never followed.
    for frame, eligible in ((100.0, 9), (101.0, 2), (102.0, 6), (103.0, 3), (104.0, 9),
                            (105.0, 9)):
        admissions.observe(admission(frame, eligible))
    window = admissions.to_dict()["window"]
    assert (window["passes"], window["passes_shedding"]) == (3, 1)
    assert (window["most_eligible"], window["most_shed"], window["capacity"]) == (6, 2, 4)
    assert (window["first_frame_s"], window["last_frame_s"]) == (101.0, 103.0)


def test_the_newest_pass_is_held_back_until_the_next_one_shows_its_step_rendered():
    admissions = WindowAdmissions(begin_s=100.0, end_s=200.0, step_s=1.0)
    admissions.observe(admission(101.0, 9))
    assert admissions.passes == 0
    admissions.observe(admission(102.0, 1))
    assert (admissions.passes, admissions.passes_shedding) == (1, 1)


def test_no_pass_is_described_as_nothing():
    assert WindowAdmissions.describe(None) is None


# -- the compile lock and teleporting, as the session's own checks report them -------------------

def test_the_scenario_checks_are_read_by_the_session_s_names(tmp_path):
    layout = Layout(tmp_path)
    configuration = str(layout.lock.parent / f"{SCENARIO_ID}.sumocfg")

    class Report:
        CompileLock = ScenarioLockCheck.Require(configuration,
                                                VehicleCatalogue.Load(str(CATALOGUE)), None)
        Teleporting = TeleportingCheck.Require(configuration, False)

    checks = RunCloseoutReport.scenario_checks_of(Report)
    lock = checks["compile_lock"]
    assert lock["compiled"] is True
    assert Path(lock["lock_path"]) == layout.lock.resolve()
    assert lock["statement"].startswith(f"{SCENARIO_ID}, compiled by carlacontrol.ScenarioCompiler")
    assert lock["routed_by"].startswith("duarouter 1.27.0 against the world converter")
    assert "Gardnerville_Fixture.cwp" in lock["compiled_for"]
    assert checks["teleporting"] == {"enabled": False, "accepted": False, "seconds": -1.0,
                                     "declared": "-1",
                                     "statement": "disabled (time-to-teleport '-1')"}
