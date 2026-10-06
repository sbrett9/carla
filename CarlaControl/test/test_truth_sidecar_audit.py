"""What the truth-sidecar audit counts in a SUMO drive's capture, before and after the render set.

Sidecars are written here in the two shapes the recorder has produced: the world's every vehicle
actor keyed by actor id, parked pool bodies included (as the Gardnerville capture of 2026-09-28
holds, measured at 1,385 of 2,608 records 300 m underground), and the frame's rendered set keyed by
SUMO vehicle (`CarlaNet.Recording.CotWriter` with a render-set source). Asserted:

  * the parked bodies, the missing SUMO ids and a uid whose body was lent again after parking are
    each counted and each a defect in the first shape;
  * the second shape has none, and a body the pool reused for another vehicle is counted as the
    expected reuse it is, not as a defect;
  * a uid carrying two SUMO vehicles is a defect whatever else is right;
  * a traffic-manager capture is not faulted for carrying no SUMO id;
  * a capture with no moving vehicle draws no band unless a floor is stated; and
  * every sidecar is read whatever it is named: the recorder names each after its camera,
    `<camera name>_<capture time>.xml`, and those written before cameras were named are
    `SCTMV_<capture time>.xml`, so a directory may hold both; and
  * where the run had a supervision plan, every SUMO vehicle record carries a `<_supervision>` in
    one of the three states, consistent with what it names, no `<_supervision>` stands outside a
    vehicle's record (a label with no vehicle; 06 §3.5), a sidecar whose supervision was unknown says
    so and is counted, and a run with no plan expects none; and
  * every vehicle record says where its box fell against the picture (`in_frame`) and, where its
    occlusion fields are absent, why (`occlusion_unmeasured`): a record saying neither, occlusion
    fields on a vehicle the picture has no view of, a reason beside a measurement, and a place or a
    reason outside the recorder's words are each a defect, and a capture written before the recorder
    said where each vehicle fell shows the first.
"""
from __future__ import annotations

import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.TruthSidecarAudit import TruthSidecarAudit  # noqa: E402  (needs the path above)

SENSOR = """  <event version="2.0" uid="CARLA-SENSOR-32" type="a-f-A-M-F-Q" how="m-g" time="t" start="t" stale="t">
    <point lat="38.9119624" lon="-119.7587182" hae="1872.04" ce="0.0" le="0.0" />
    <detail>
      <contact callsign="OVERWATCH-1" />
      <track course="0.0" speed="0.00" />
      <_carla_intrinsics width="1280" height="720" fx="640" fy="640" cx="640" cy="360" />
    </detail>
  </event>
"""


def vehicle(uid: str, actor: int, hae: float, speed: float, sumo_id: str | None = None,
            in_frame: str | None = "wholly", occlusion: float | None = None,
            unmeasured: str | None = "no_depth_camera") -> str:
    """A vehicle record as `CotWriter` writes one: where its box fell against the picture, and either
    the five occlusion fields (`occlusion` given) or the reason they are absent (`unmeasured`); None
    for either leaves it out, as a recorder written before 2026-10-05 did."""
    identity = "" if sumo_id is None else (
        f' sumo_id="{sumo_id}" vtype_id="passenger" admitted_tick="100"')
    place = "" if in_frame is None else f' in_frame="{in_frame}"'
    if occlusion is not None:
        picture = (f' occlusion="{occlusion:.3f}" occlusion_level="1" occlusion_samples="64" '
                   'apparent_width_px="12" apparent_height_px="5"')
    elif in_frame == "behind_camera":
        picture = ""
    else:
        picture = ' apparent_width_px="12" apparent_height_px="5"'
    if occlusion is None and unmeasured is not None:
        picture += f' occlusion_unmeasured="{unmeasured}"'
    return (f'  <event version="2.0" uid="{uid}" type="a-n-G-E-V" how="m-g" time="t" start="t" '
            f'stale="t">\n'
            f'    <point lat="38.9051631" lon="-119.7526194" hae="{hae:.2f}" ce="0.0" le="0.0" />\n'
            f'    <detail>\n'
            f'      <track course="90.0" speed="{speed:.2f}" />\n'
            f'      <contact callsign="car-{actor}" />\n'
            f'      <_carla source="truth" actor_id="{actor}" type_id="vehicle.audi.tt" '
            f'base_type="car" role_name="autopilot"{place}{picture}{identity} />\n'
            f'    </detail>\n'
            f'  </event>\n')


def sidecar(directory: Path, tick: int, events: list[str], vehicles: str | None = None,
            stem: str | None = None) -> None:
    marker = "" if vehicles is None else f' vehicles="{vehicles}"'
    (directory / f"{stem or f'SCTMV_{tick}'}.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<events captured="t" count="{len(events)}" source="truth" tick="{tick}" '
        f'sim_time_s="{tick * 0.05:.3f}" telemetry_tick="{tick}"{marker}>\n'
        + SENSOR + "".join(events) + "</events>\n", encoding="utf-8")


def audit(directory: Path, **options):
    auditor = TruthSidecarAudit(**options)
    return auditor.audit(auditor.sidecars(directory))


ROAD, PARKED = 1421.6, 1120.8


def test_a_capture_listing_every_vehicle_actor_is_counted_for_all_three_defects(tmp_path):
    # Body 62 carries a vehicle, is given back and parked, then carries another; body 34 stays
    # parked throughout; body 40 drives the whole capture.
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-62", 62, ROAD, 12.0),
                            vehicle("CARLA-TRUTH-34", 34, PARKED, 0.0),
                            vehicle("CARLA-TRUTH-40", 40, ROAD + 2, 9.0)])
    sidecar(tmp_path, 110, [vehicle("CARLA-TRUTH-62", 62, PARKED, 0.0),
                            vehicle("CARLA-TRUTH-34", 34, PARKED, 0.0),
                            vehicle("CARLA-TRUTH-40", 40, ROAD + 3, 9.0)])
    sidecar(tmp_path, 120, [vehicle("CARLA-TRUTH-62", 62, ROAD - 1, 0.0),
                            vehicle("CARLA-TRUTH-34", 34, PARKED, 0.0),
                            vehicle("CARLA-TRUTH-40", 40, ROAD + 4, 9.0)])

    result = audit(tmp_path)

    assert result.sidecars == 3 and len(result.records) == 9
    assert result.floor_hae == ROAD - 50.0
    assert [record.actor_id for record in result.below_band] == ["34", "62", "34", "34"]
    assert len(result.without_sumo_id) == 9
    assert not result.carries_sumo_ids
    # A vehicle standing still on the road is on the ground; the body lent again is seen.
    assert result.uids_lent_again_after_parking == ["CARLA-TRUTH-62"]
    defects = result.defects()
    assert len(defects) == 3
    assert defects[0].startswith("4 of 9 vehicle records stand below the ground band")
    assert any("not measurable directly" in line for line in TruthSidecarAudit.describe(result))


def test_a_capture_listing_the_rendered_set_by_sumo_vehicle_has_no_defect(tmp_path):
    # The same pool: body 62 renders escort_0, is parked (and so absent), then renders
    # corridor_d0_p0_h6.12. Each vehicle keeps its own uid; the body's reuse is expected.
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-escort_0", 62, ROAD, 12.0, "escort_0"),
                            vehicle("CARLA-TRUTH-SUMO-flow.3", 40, ROAD + 2, 9.0, "flow.3")],
            vehicles="rendered")
    sidecar(tmp_path, 110, [vehicle("CARLA-TRUTH-SUMO-flow.3", 40, ROAD + 3, 9.0, "flow.3")],
            vehicles="rendered")
    sidecar(tmp_path, 120, [vehicle("CARLA-TRUTH-SUMO-corridor_d0_p0_h6.12", 62, ROAD - 1, 0.0,
                                    "corridor_d0_p0_h6.12"),
                            vehicle("CARLA-TRUTH-SUMO-flow.3", 40, ROAD + 4, 9.0, "flow.3")],
            vehicles="rendered")
    sidecar(tmp_path, 130, [], vehicles="unknown")

    result = audit(tmp_path)

    assert result.defects() == []
    assert result.sidecars_listing_rendered == 3 and result.sidecars_listing_unknown == 1
    assert result.below_band == [] and result.without_sumo_id == []
    assert result.uids_with_several_sumo_ids == {}
    assert result.sumo_ids_with_several_uids == {}
    assert result.actors_with_several_sumo_ids == {"62": {"escort_0", "corridor_d0_p0_h6.12"}}


def test_a_uid_that_names_two_sumo_vehicles_is_a_defect(tmp_path):
    # SUMO ids on the records, but the uid still built on the reusable actor.
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-62", 62, ROAD, 12.0, "escort_0")])
    sidecar(tmp_path, 120, [vehicle("CARLA-TRUTH-62", 62, ROAD, 12.0, "flow.3")])

    result = audit(tmp_path)

    assert result.uids_with_several_sumo_ids == {"CARLA-TRUTH-62": {"escort_0", "flow.3"}}
    assert result.defects() == ["1 uid(s) name more than one SUMO vehicle over the capture"]


def test_a_traffic_manager_capture_is_not_faulted_for_carrying_no_sumo_id(tmp_path):
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-7", 7, ROAD, 12.0),
                            vehicle("CARLA-TRUTH-8", 8, ROAD + 1, 0.0)])

    result = audit(tmp_path)

    assert result.defects(sumo_drive=False) == []
    assert len(result.defects(sumo_drive=True)) == 1


def test_a_capture_with_no_moving_vehicle_draws_no_band_unless_a_floor_is_stated(tmp_path):
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 0.0, "a"),
                            vehicle("CARLA-TRUTH-SUMO-b", 2, PARKED, 0.0, "b")])

    unbanded = audit(tmp_path)
    assert unbanded.floor_hae is None and unbanded.below_band == []
    assert any("no moving vehicle" in line for line in TruthSidecarAudit.describe(unbanded))

    floored = audit(tmp_path, floor_hae=1400.0)
    assert [record.sumo_id for record in floored.below_band] == ["b"]


def test_sidecars_named_after_their_camera_and_those_from_before_cameras_had_names_are_all_read(
        tmp_path):
    # One directory, as a record directory used before and after cameras were named holds: the
    # old stem, and two cameras' stills under the new one.
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a")],
            stem="SCTMV_2026.09.16_14.44.31.487")
    sidecar(tmp_path, 110, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD + 1, 9.0, "a")],
            stem="DECK-I25_2026.10.02_14.07.22.481")
    sidecar(tmp_path, 110, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD + 1, 9.0, "a")],
            stem="NapOfEarth_2_2026.10.02_14.07.22.493")
    (tmp_path / "DECK-I25_2026.10.02_14.07.22.481.png").write_bytes(b"not a sidecar")

    auditor = TruthSidecarAudit()
    found = auditor.sidecars(tmp_path)
    result = auditor.audit(found)

    assert sorted(path.name for path in found) == ["DECK-I25_2026.10.02_14.07.22.481.xml",
                                                   "NapOfEarth_2_2026.10.02_14.07.22.493.xml",
                                                   "SCTMV_2026.09.16_14.44.31.487.xml"]
    assert result.sidecars == 3 and len(result.records) == 3
    assert result.defects() == []


# A supervised drive's sidecars, as `CotWriter` writes them from the supervision the server carried on
# the frame (06 §8.2): the plan on the container, every SUMO vehicle's state. STRAY is the world-scoped
# element a recorder wrote before the owner's ruling of 2026-10-05 (06 §3.5): a label with no vehicle,
# which the audit faults.
PLAN = "Shahid_Bahonar_Port_PatternOfLife"
DIGEST = "e3571085c17731122253518d85beb667865035305952f7c4e380d1b9e8f4a7ad"
STRAY = (f'  <_supervision scope="world" vocabulary="3" vocabulary_digest="{DIGEST}">\n'
         f'    <absence instance="{PLAN}/pi_tower_relief_d4_h7_t3_unmanned" '
         'labels="bahonar:post_unmanned" areas="tower_03" phase="vacancy" />\n'
         '  </_supervision>\n')


def supervised_vehicle(uid: str, actor: int, sumo_id: str, state: str | None,
                       instances: tuple[str, ...] = ()) -> str:
    """A SUMO vehicle record carrying the state given, or no <_supervision> where it is None."""
    record = vehicle(uid, actor, ROAD, 9.0, sumo_id)
    if state is None:
        return record
    element = (f'      <_supervision state="{state}" vocabulary="3" vocabulary_digest="{DIGEST}"'
               + (" />\n" if not instances else ">\n" + "".join(
                   f'        <annotation instance="{PLAN}/{name}" labels="bahonar:term" />\n'
                   for name in instances) + "      </_supervision>\n"))
    return record.replace("    </detail>\n", element + "    </detail>\n")


def supervised_sidecar(directory: Path, tick: int, events: list[str], stray: bool = False,
                       unknown: bool = False) -> None:
    attributes = (' supervision="unknown"' if unknown
                  else f' plan_id="{PLAN}" vocabulary="3" vocabulary_digest="{DIGEST}"')
    (directory / f"SUPERVISED_{tick}.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<events captured="t" count="{len(events)}" source="truth" tick="{tick}" '
        f'sim_time_s="{tick * 0.05:.3f}" telemetry_tick="{tick}" vehicles="rendered"{attributes}>\n'
        + (STRAY if stray and not unknown else "") + SENSOR + "".join(events) + "</events>\n",
        encoding="utf-8")


def test_a_supervised_capture_whose_every_sumo_vehicle_carries_a_state_has_no_defect(tmp_path):
    supervised_sidecar(tmp_path, 100, [
        supervised_vehicle("CARLA-TRUTH-SUMO-escort_0", 21, "escort_0", "annotated", ("pi_escort",)),
        supervised_vehicle("CARLA-TRUTH-SUMO-guard", 22, "guard", "nominal", ("pi_posting",)),
        supervised_vehicle("CARLA-TRUTH-SUMO-flow.3", 23, "flow.3", "unlabelled"),
        # A nominal vehicle need not name the ordinary behaviour it is.
        supervised_vehicle("CARLA-TRUTH-SUMO-hauler", 25, "hauler", "nominal"),
        # A vehicle actor no session named is no subject of the plan.
        vehicle("CARLA-TRUTH-24", 24, ROAD, 9.0)])
    # A frame whose supervision was not to be had: counted, and gated at closeout, not faulted here.
    supervised_sidecar(tmp_path, 110, [vehicle("CARLA-TRUTH-SUMO-escort_0", 21, ROAD, 9.0, "escort_0")],
                       unknown=True)

    result = audit(tmp_path)

    assert result.had_plan
    assert (result.sidecars_with_plan, result.sidecars_supervision_unknown) == (1, 1)
    assert result.plans == {PLAN}
    # The unnamed actor carries no SUMO id, which a SUMO drive is faulted for, and that is the only defect.
    assert len(result.defects()) == 1 and "carry no SUMO id" in result.defects()[0]
    assert result.defects(sumo_drive=False) == []
    assert any("annotated 1, nominal 2, unlabelled 1" in line for line in TruthSidecarAudit.describe(result))


def test_a_sumo_vehicle_with_no_state_in_a_supervised_capture_is_a_defect(tmp_path):
    supervised_sidecar(tmp_path, 100, [
        supervised_vehicle("CARLA-TRUTH-SUMO-escort_0", 21, "escort_0", "annotated", ("pi_escort",)),
        supervised_vehicle("CARLA-TRUTH-SUMO-flow.3", 23, "flow.3", None)])

    result = audit(tmp_path)

    assert [record.sumo_id for record in result.records_without_supervision] == ["flow.3"]
    assert any("carry no <_supervision>" in defect for defect in result.defects())


def test_a_state_outside_the_three_and_states_contradicting_their_annotations_are_defects(tmp_path):
    supervised_sidecar(tmp_path, 100, [
        supervised_vehicle("CARLA-TRUTH-SUMO-a", 1, "a", "anomalous", ("pi_a",)),
        supervised_vehicle("CARLA-TRUTH-SUMO-b", 2, "b", "annotated"),
        supervised_vehicle("CARLA-TRUTH-SUMO-c", 3, "c", "unlabelled", ("pi_c",))])

    defects = audit(tmp_path).defects()

    assert any("state other than annotated, nominal, unlabelled" in defect for defect in defects)
    assert any("annotated vehicle record(s) name no instance" in defect for defect in defects)
    assert any("unlabelled vehicle record(s) name an instance" in defect for defect in defects)


def test_a_supervision_element_outside_a_vehicle_record_and_a_sidecar_saying_nothing_are_defects(tmp_path):
    # A sidecar written before the ruling of 2026-10-05, carrying a world-scoped element: a label with
    # no vehicle to follow, which no recorder writes now and the audit faults.
    supervised_sidecar(tmp_path, 100, [supervised_vehicle("CARLA-TRUTH-SUMO-a", 1, "a", "unlabelled")],
                       stray=True)
    # A sidecar of the same run that names no plan and does not say its supervision was unknown.
    sidecar(tmp_path, 110, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a")], vehicles="rendered")

    result = audit(tmp_path)

    assert result.sidecars_with_world_supervision == ["SUPERVISED_100.xml"]
    assert result.sidecars_saying_nothing_of_supervision == ["SCTMV_110.xml"]
    assert any("outside any vehicle record" in defect for defect in result.defects())
    assert any("neither name it nor say" in defect for defect in result.defects())


def test_a_capture_of_a_run_with_no_plan_expects_no_supervision(tmp_path):
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a")], vehicles="rendered")

    result = audit(tmp_path)

    assert not result.had_plan
    assert result.defects() == []
    assert any("no sidecar names a plan" in line for line in TruthSidecarAudit.describe(result))


# Where each vehicle fell against the picture and why its occlusion is absent, as `CotWriter` writes
# them for every record (09 §5.1): the in-picture fact for all, the five fields where measured, one
# reason otherwise.
def test_a_capture_whose_every_record_says_where_it_fell_and_why_has_no_defect(tmp_path):
    sidecar(tmp_path, 100, [
        # Measured against a paired depth capture, wholly and partly in the picture.
        vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a", in_frame="wholly", occlusion=0.18),
        vehicle("CARLA-TRUTH-SUMO-b", 2, ROAD, 9.0, "b", in_frame="partly", occlusion=0.5),
        # In the picture and unmeasured, for each reason a depth capture gives.
        vehicle("CARLA-TRUTH-SUMO-c", 3, ROAD, 9.0, "c", in_frame="wholly", unmeasured="beyond_depth_range"),
        vehicle("CARLA-TRUTH-SUMO-d", 4, ROAD, 9.0, "d", in_frame="wholly", unmeasured="no_sample"),
        vehicle("CARLA-TRUTH-SUMO-e", 5, ROAD, 9.0, "e", in_frame="wholly", unmeasured="beyond_draw_distance"),
        # Outside the picture, and behind the camera, which has no footprint.
        vehicle("CARLA-TRUTH-SUMO-f", 6, ROAD, 9.0, "f", in_frame="none", unmeasured="outside_frame"),
        vehicle("CARLA-TRUTH-SUMO-g", 7, ROAD, 9.0, "g", in_frame="behind_camera", unmeasured="behind_camera")],
        vehicles="rendered")
    # A capture of the same run whose depth capture did not pair, each way it can fail.
    sidecar(tmp_path, 110, [
        vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a", unmeasured="no_depth_capture"),
        vehicle("CARLA-TRUTH-SUMO-b", 2, ROAD, 9.0, "b", unmeasured="depth_out_of_step"),
        vehicle("CARLA-TRUTH-SUMO-c", 3, ROAD, 9.0, "c", unmeasured="depth_pose_mismatch")],
        vehicles="rendered")

    result = audit(tmp_path)

    assert result.defects() == []
    assert result.records_without_in_frame == []
    assert [record.sumo_id for record in result.records if record.occlusion_measured] == ["a", "b"]
    lines = TruthSidecarAudit.describe(result)
    assert any("in the picture: wholly 7, partly 1, none 1, behind_camera 1; not said: 0 of 10" in line
               for line in lines)
    assert any("occlusion measured: 2 of 10; unmeasured by reason: behind_camera 1, beyond_depth_range 1, "
               "beyond_draw_distance 1, depth_out_of_step 1, depth_pose_mismatch 1, no_depth_capture 1, "
               "no_sample 1, outside_frame 1; neither measured nor a reason given: 0" in line
               for line in lines)


def test_a_record_that_does_not_say_whether_it_is_in_the_picture_is_a_defect(tmp_path):
    # A capture written before the recorder said where each vehicle fell: the fields are simply absent,
    # which is exactly the silence the owner ruled out.
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a", in_frame=None, unmeasured=None),
                            vehicle("CARLA-TRUTH-SUMO-b", 2, ROAD, 9.0, "b")],
            vehicles="rendered")

    result = audit(tmp_path)

    assert [record.sumo_id for record in result.records_without_in_frame] == ["a"]
    # Saying nothing of its place, the record is faulted for that alone, not twice.
    assert result.records_without_occlusion_or_reason == []
    assert result.defects() == ["1 of 2 vehicle records do not say whether the vehicle is in the picture "
                                "(no in_frame)"]
    assert any("not said: 1 of 2" in line for line in TruthSidecarAudit.describe(result))


def test_occlusion_fields_on_a_vehicle_the_picture_has_no_view_of_are_a_defect(tmp_path):
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a", in_frame="none", occlusion=0.0),
                            vehicle("CARLA-TRUTH-SUMO-b", 2, ROAD, 9.0, "b", in_frame="behind_camera", occlusion=0.0),
                            vehicle("CARLA-TRUTH-SUMO-c", 3, ROAD, 9.0, "c", in_frame="partly", occlusion=0.0)],
            vehicles="rendered")

    result = audit(tmp_path)

    assert [record.sumo_id for record in result.records_occluded_outside_picture] == ["a", "b"]
    assert result.defects() == ["2 vehicle record(s) carry occlusion fields while in_frame says the picture "
                                "has no view of the vehicle"]


def test_a_record_with_neither_occlusion_nor_a_reason_is_a_defect(tmp_path):
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a", in_frame="wholly", unmeasured=None),
                            vehicle("CARLA-TRUTH-SUMO-b", 2, ROAD, 9.0, "b", in_frame="none", unmeasured=None),
                            vehicle("CARLA-TRUTH-SUMO-c", 3, ROAD, 9.0, "c", in_frame="wholly", occlusion=0.2)],
            vehicles="rendered")

    result = audit(tmp_path)

    assert [record.sumo_id for record in result.records_without_occlusion_or_reason] == ["a", "b"]
    assert result.defects() == ["2 vehicle record(s) carry neither occlusion fields nor occlusion_unmeasured: "
                                "an absent fraction with no reason"]


def test_a_reason_beside_a_measurement_is_a_defect(tmp_path):
    measured = vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a", in_frame="wholly", occlusion=0.2)
    sidecar(tmp_path, 100, [measured.replace('occlusion_samples="64"',
                                             'occlusion_samples="64" occlusion_unmeasured="no_sample"')],
            vehicles="rendered")

    result = audit(tmp_path)

    assert [record.sumo_id for record in result.records_with_reason_beside_measurement] == ["a"]
    assert result.defects() == ["1 vehicle record(s) carry occlusion_unmeasured beside a measured occlusion"]


def test_a_place_or_a_reason_outside_the_recorders_words_is_a_defect(tmp_path):
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-SUMO-a", 1, ROAD, 9.0, "a", in_frame="inside"),
                            vehicle("CARLA-TRUTH-SUMO-b", 2, ROAD, 9.0, "b", unmeasured="too_small")],
            vehicles="rendered")

    defects = audit(tmp_path).defects()

    assert len(defects) == 2
    assert any("in_frame other than wholly, partly, none, behind_camera" in defect for defect in defects)
    assert any("occlusion_unmeasured other than behind_camera, outside_frame" in defect for defect in defects)


def test_the_in_picture_checks_apply_to_traffic_manager_captures_too(tmp_path):
    sidecar(tmp_path, 100, [vehicle("CARLA-TRUTH-7", 7, ROAD, 12.0, in_frame=None, unmeasured=None),
                            vehicle("CARLA-TRUTH-8", 8, ROAD + 1, 0.0)])

    result = audit(tmp_path)

    assert result.defects(sumo_drive=False) == ["1 of 2 vehicle records do not say whether the vehicle is in "
                                                "the picture (no in_frame)"]
