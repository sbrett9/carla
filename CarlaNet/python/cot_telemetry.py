"""Reference CoT telemetry emitter — stream CARLA vehicle TRUTH as Cursor-on-Target
events to a TAK endpoint (e.g. WinTAK) over UDP. Implements Docs/CAT_Research/Findings/
09_Telemetry_CoT_Contract.md (v0).

Truth is exact (ce=le=0, how=m-g). The SAME to_cot() will later serve the YOLO detection feed —
only the record producer differs — so truth and detection are directly comparable in WinTAK / a
scoring harness.

Pull model: world.get_vehicle_telemetry() is a plain client call; this script is just a thin,
swappable transport. Any consumer can poll at its own rate and format/send however it likes.

Run order (separate terminals):
    1. RunCarlaServer.ps1
    2. test_digital_twin.py            # build the elevated, Cesium-aligned world (sets the georef origin)
    3. generate_traffic_carlanet.py --asynch -n 40 -w 0
    4. python cot_telemetry.py --tak-host 239.2.3.1 --tak-port 6969    # TAK default SA multicast

Usage:
    python cot_telemetry.py [--tak-host H --tak-port P] [--rate HZ] [--affiliation n]
        [--stale S] [--ttl N] [--print] [--host H --port P]
"""
import argparse
import socket
import sys
import time
from datetime import datetime, timedelta, timezone
import xml.etree.ElementTree as ET

import carlanet as carla


def _iso(dt: datetime) -> str:
    """CoT timestamp: ISO-8601 UTC with millisecond precision and a 'Z'."""
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"


def to_cot(rec, affiliation="n", stale_seconds=3.0, source="truth", uid_prefix="CARLA-TRUTH") -> str:
    """Render one get_vehicle_telemetry() dict as a CoT <event> XML string (contract §2)."""
    now = datetime.now(timezone.utc)
    stale = now + timedelta(seconds=stale_seconds)
    ev = ET.Element("event", {
        "version": "2.0",
        "uid": f"{uid_prefix}-{rec['id']}",
        "type": f"a-{affiliation}-G-E-V",                 # ground equipment vehicle (v0 single symbol)
        "how": "m-g" if source == "truth" else "m-f",
        "time": _iso(now), "start": _iso(now), "stale": _iso(stale),
    })
    ET.SubElement(ev, "point", {
        "lat": f"{rec['lat']:.7f}", "lon": f"{rec['lon']:.7f}", "hae": f"{rec['hae']:.2f}",
        "ce": "0.0" if source == "truth" else f"{float(rec.get('ce', 0.0)):.1f}",
        "le": "0.0" if source == "truth" else f"{float(rec.get('le', 0.0)):.1f}",
    })
    detail = ET.SubElement(ev, "detail")
    ET.SubElement(detail, "track",
                  {"course": f"{rec['course_deg']:.1f}", "speed": f"{rec['speed_mps']:.2f}"})
    ET.SubElement(detail, "contact", {"callsign": f"{rec['base_type']}-{rec['id']}"})
    # Truth extras (contract §5) — WinTAK ignores unknown detail children; the scoring harness reads them.
    ET.SubElement(detail, "_carla", {
        "source": source, "actor_id": str(rec["id"]), "type_id": rec["type_id"],
        "base_type": rec["base_type"], "special_type": rec["special_type"],
        "length_m": f"{rec['length_m']:.2f}", "width_m": f"{rec['width_m']:.2f}",
        "height_m": f"{rec['height_m']:.2f}", "color": rec["color"], "role_name": rec["role_name"],
        "vx": f"{rec['vx']:.2f}", "vy": f"{rec['vy']:.2f}", "vz": f"{rec['vz']:.2f}",
    })
    return ET.tostring(ev, encoding="unicode")


class CotUdpEmitter:
    """One CoT <event> per UDP datagram. Works for unicast or multicast (sets the multicast TTL)."""
    def __init__(self, host, port, ttl=1):
        self._addr = (host, int(port))
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        # Harmless for unicast; lets the TAK default SA multicast group (239.2.3.1:6969) work out of the box.
        self._sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, int(ttl))

    def send(self, cot_xml: str):
        self._sock.sendto(cot_xml.encode("utf-8"), self._addr)

    def close(self):
        self._sock.close()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1", help="CARLA RPC host")
    ap.add_argument("--port", type=int, default=2000, help="CARLA RPC port")
    ap.add_argument("--tak-host", default="239.2.3.1",
                    help="TAK CoT destination (default 239.2.3.1 = TAK SA multicast; set to a WinTAK IP for unicast)")
    ap.add_argument("--tak-port", type=int, default=6969, help="TAK CoT UDP port (default 6969)")
    ap.add_argument("--rate", type=float, default=5.0, help="emit rate Hz (>=5)")
    ap.add_argument("--affiliation", default="n", help="CoT standard-identity: n neutral / u unknown / f friend / h hostile")
    ap.add_argument("--stale", type=float, default=3.0, help="CoT stale seconds")
    ap.add_argument("--ttl", type=int, default=1, help="multicast TTL")
    ap.add_argument("--print", action="store_true", dest="echo", help="also print each CoT event")
    args = ap.parse_args()

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"server: {client.get_server_version()}")
    world = client.get_world()

    try:
        origin = world.get_cesium_origin()   # (lat, lon, height_m) — fetched ONCE, reused each tick
        print(f"georef origin: lat {origin[0]:.7f} lon {origin[1]:.7f} h {origin[2]:.1f} m")
    except Exception as e:
        print(f"ERROR: get_cesium_origin failed ({e!r}). Generate a digital-twin world first "
              f"(test_digital_twin.py).", file=sys.stderr)
        return 1

    emit = CotUdpEmitter(args.tak_host, args.tak_port, ttl=args.ttl)
    period = 1.0 / max(0.1, args.rate)
    print(f"emitting CoT (a-{args.affiliation}-G-E-V) -> udp://{args.tak_host}:{args.tak_port} "
          f"@ {args.rate} Hz; Ctrl+C to stop")
    ticks = 0
    try:
        while True:
            t0 = time.time()
            recs = world.get_vehicle_telemetry(origin)
            for r in recs:
                xml = to_cot(r, affiliation=args.affiliation, stale_seconds=args.stale)
                emit.send(xml)
                if args.echo:
                    print(xml)
            ticks += 1
            if ticks % max(1, int(args.rate)) == 0:
                print(f"\r[{ticks}] {len(recs)} vehicle track(s) emitted", end="", flush=True)
            time.sleep(max(0.0, period - (time.time() - t0)))
    except KeyboardInterrupt:
        print("\nstopping.")
    finally:
        emit.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
