"""A live CoT event names a pooled body by the SUMO vehicle it draws, as the recorded sidecar does.

During a SUMO drive the live pull (`world.get_vehicle_telemetry`) lists only the bodies the newest
frame drew, and gives each lent body the SUMO vehicle it was drawn for (`sumo_id`, `vtype_id`,
`admitted_tick`), from the render set the server carries on every world-observer snapshot
(`Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md` §5.2). A pooled body draws a succession of
vehicles over a run, so a track built on its actor id would jump from one real vehicle to the next.
Held here, against the formatter the live feed (`TelemetryController`) uses:

* **A record naming a SUMO vehicle is that vehicle's track.** Its uid is `CARLA-TRUTH-SUMO-<sumo_id>`
  and its callsign `<base_type>-<sumo_id>`, as `CotWriter` writes them into the sidecar, and the
  three SUMO attributes ride in `_carla` beside the actor id of the body that drew it.
* **A SUMO id that is a number is never read as an actor id.** The uid says which it is.
* **Every other record is written as it always was.** A vehicle no session named carries no SUMO
  attribute, and the standalone SUMO bridge's records -- keyed by their SUMO id under their own
  prefix -- are untouched.
"""
import sys
import xml.etree.ElementTree as ET
from datetime import UTC, datetime
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CotUdpEmitter import CotUdpEmitter  # noqa: E402  (needs the path above)

WHEN = datetime(2026, 10, 1, 12, 0, 0, tzinfo=UTC)


def record(**extra):
    """A live-pull record for actor 21, a car, with whatever the test adds."""
    rec = {
        "id": 21, "type_id": "vehicle.mercedes.sprinter", "base_type": "car", "special_type": "",
        "color": "10,20,30", "role_name": "sumo",
        "lat": 27.15012, "lon": 56.18065, "hae": 12.5, "hae_dtm": 11.0,
        "speed_mps": 8.0, "course_deg": 90.0, "vx": 8.0, "vy": 0.0, "vz": 0.0,
        "length_m": 5.9, "width_m": 2.0, "height_m": 2.6,
    }
    rec.update(extra)
    return rec


def event(rec, **kwargs):
    return ET.fromstring(CotUdpEmitter.vehicle_telemetry_to_cot(rec, when=WHEN, **kwargs))


def test_a_record_naming_a_sumo_vehicle_is_that_vehicles_track():
    ev = event(record(sumo_id="escort_0", vtype_id="military_truck", admitted_tick=4180))

    assert ev.get("uid") == "CARLA-TRUTH-SUMO-escort_0"
    assert ev.find("detail/contact").get("callsign") == "car-escort_0"
    extras = ev.find("detail/_carla")
    assert extras.get("actor_id") == "21"
    assert extras.get("sumo_id") == "escort_0"
    assert extras.get("vtype_id") == "military_truck"
    assert extras.get("admitted_tick") == "4180"
    assert extras.get("role_name") == "sumo"


def test_a_numeric_sumo_id_is_never_read_as_an_actor_id():
    named = event(record(sumo_id="17", vtype_id="passenger", admitted_tick=3))
    actor = event(record(id=17))

    assert named.get("uid") == "CARLA-TRUTH-SUMO-17"
    assert actor.get("uid") == "CARLA-TRUTH-17"
    assert named.get("uid") != actor.get("uid")


def test_a_vehicle_no_session_named_is_written_as_it_always_was():
    ev = event(record(role_name="autopilot"))

    assert ev.get("uid") == "CARLA-TRUTH-21"
    assert ev.find("detail/contact").get("callsign") == "car-21"
    extras = ev.find("detail/_carla")
    for attribute in ("sumo_id", "vtype_id", "admitted_tick"):
        assert extras.get(attribute) is None


def test_the_standalone_sumo_bridge_keeps_its_own_keying():
    # SumoCotBridge keys a record by its SUMO id under its own prefix and carries no `sumo_id`.
    ev = event(record(id="flow_3.12"), uid_prefix="SUMO-TRUTH")

    assert ev.get("uid") == "SUMO-TRUTH-flow_3.12"
    assert ev.find("detail/contact").get("callsign") == "car-flow_3.12"
    assert ev.find("detail/_carla").get("sumo_id") is None
