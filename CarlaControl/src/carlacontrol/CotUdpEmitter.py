"""UDP emitter for CoT (Cursor-on-Target) XML events.

Provides UDP socket wrapper for sending CoT events to TAK servers or other
CoT-aware systems. Supports both unicast and multicast.
"""

import socket
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone


class CotUdpEmitter:
    """UDP socket wrapper for sending CoT XML events.

    Sends one CoT event per UDP datagram. Supports both unicast and multicast
    (automatically sets multicast TTL for TAK SA compatibility).

    Usage:
        emitter = CotUdpEmitter("239.2.3.1", 6969, ttl=1)
        xml = emitter.vehicle_telemetry_to_cot(telemetry_dict)
        emitter.send(xml)
        emitter.close()
    """

    def __init__(self, host: str, port: int, ttl: int = 1):
        """Initialize UDP emitter.

        Args:
            host: Destination hostname or IP address
            port: Destination UDP port
            ttl: Multicast TTL (harmless for unicast, enables TAK SA multicast)
        """
        self._addr = (host, int(port))
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        # Harmless for unicast; lets the TAK default SA multicast group work out of the box.
        self._sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, int(ttl))

    def send(self, cot_xml: str) -> None:
        """Send a CoT XML event as a UDP datagram.

        Args:
            cot_xml: CoT XML event string
        """
        self._sock.sendto(cot_xml.encode("utf-8"), self._addr)

    def close(self) -> None:
        """Close the UDP socket."""
        self._sock.close()

    @staticmethod
    def format_cot_timestamp(dt: datetime) -> str:
        """Format datetime as CoT timestamp: ISO-8601 UTC with millisecond precision.

        Args:
            dt: Datetime to format (should be UTC)

        Returns:
            ISO-8601 string with milliseconds and 'Z' suffix
        """
        return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"

    @staticmethod
    def vehicle_telemetry_to_cot(
        rec: dict,
        affiliation: str = "n",
        stale_seconds: float = 3.0,
        source: str = "truth",
        uid_prefix: str = "CARLA-TRUTH",
        when: datetime | None = None,
        solar: dict | None = None,
        capture=None,
    ) -> str:
        """Convert vehicle telemetry record to CoT XML event string.

        Args:
            rec: Telemetry dict from world.get_vehicle_telemetry()
            affiliation: CoT affiliation code (n=neutral, f=friendly, h=hostile)
            stale_seconds: How long until event is considered stale
            source: Source type ("truth" for ground truth, "m-f" for fusion)
            uid_prefix: Prefix for unique ID
            when: Optional timestamp to pin event time (for recorded data)
            solar: Optional solar state dict to embed sun position

        Returns:
            CoT XML event string
        """
        now = when or datetime.now(timezone.utc)
        stale = now + timedelta(seconds=stale_seconds)

        ev = ET.Element(
            "event",
            {
                "version": "2.0",
                "uid": f"{uid_prefix}-{rec['id']}",
                "type": f"a-{affiliation}-G-E-V",
                "how": "m-g" if source == "truth" else "m-f",
                "time": CotUdpEmitter.format_cot_timestamp(now),
                "start": CotUdpEmitter.format_cot_timestamp(now),
                "stale": CotUdpEmitter.format_cot_timestamp(stale),
            },
        )

        ET.SubElement(
            ev,
            "point",
            {
                "lat": f"{rec['lat']:.7f}",
                "lon": f"{rec['lon']:.7f}",
                "hae": f"{rec['hae']:.2f}",
                "ce": "0.0" if source == "truth" else f"{float(rec.get('ce', 0.0)):.1f}",
                "le": "0.0" if source == "truth" else f"{float(rec.get('le', 0.0)):.1f}",
            },
        )

        detail = ET.SubElement(ev, "detail")
        ET.SubElement(
            detail,
            "track",
            {
                "course": f"{rec['course_deg']:.1f}",
                "speed": f"{rec['speed_mps']:.2f}",
            },
        )
        ET.SubElement(
            detail,
            "contact",
            {
                "callsign": f"{rec['base_type']}-{rec['id']}",
            },
        )
        ET.SubElement(
            detail,
            "_carla",
            {
                "source": source,
                "actor_id": str(rec["id"]),
                "type_id": rec["type_id"],
                "base_type": rec["base_type"],
                "special_type": rec["special_type"],
                "length_m": f"{rec['length_m']:.2f}",
                "width_m": f"{rec['width_m']:.2f}",
                "height_m": f"{rec['height_m']:.2f}",
                "color": rec["color"],
                "role_name": rec["role_name"],
                "vx": f"{rec['vx']:.2f}",
                "vy": f"{rec['vy']:.2f}",
                "vz": f"{rec['vz']:.2f}",
            },
        )

        if capture is not None:
            ET.SubElement(detail, "_capture", capture.attributes())

        if solar:
            ET.SubElement(
                detail,
                "_solar",
                {
                    "solar_time": f"{solar['solar_time']:.4f}",
                    "date": f"{solar['year']:04d}-{solar['month']:02d}-{solar['day']:02d}",
                    "time_zone": f"{solar['time_zone']:.4f}",
                    "sun_elevation_deg": f"{solar['sun_elevation_deg']:.3f}",
                    "sun_azimuth_deg": f"{solar['sun_azimuth_deg']:.3f}",
                    "advancing": "true" if solar["advancing"] else "false",
                    "rate": f"{solar['rate']:g}",
                },
            )

        return ET.tostring(ev, encoding="unicode")
