"""What made a file: the record every file our tools write carries.

One shape everywhere, so any single file -- a run record, a compiled scenario, a still's sidecar -- can be
traced to the major.minor release of the distribution that made it. In a JSON file it is a `producer`
object:

    {"tool": "carlacontrol.CaptureSession", "tool_version": "0.10.0+g1a2b3c4d5",
     "carlanet": "0.10.0+g1a2b3c4d5", "server": {...} | null, "sumo": "1.27.0" | null,
     "written_utc": "2026-10-07T12:00:00.000Z"}

* `tool` -- the component that wrote the file, and `tool_version` the release of carlacontrol it is from
  (`carlacontrol.version`);
* `carlanet` -- the release of the CarlaNet the process loaded: its assemblies' informational version
  (`CarlaNet.Types.Provenance.Producer.CarlaNetVersion`), null in a process that never loaded carlanet;
* `server` -- the CARLA server's build identity where a server was used (`server_identity`), null
  where none was;
* `sumo` -- the SUMO release where SUMO ran, null where it did not;
* `written_utc` -- when the file was written, left out of a file two runs of one input must write byte
  for byte; the file that binds that one by digest carries the time.

In an XML file it is a `<_producer>` element with those values as attributes, one not used left out,
and the server a `<_server>` child (`xml_element`).

The C# writers -- the truth sidecar, the PNG's `carla:capture` chunk, the run manifest, the world truth
track's summary, the world package -- write the same object (`CarlaNet.Types.Provenance.ProducerRecord`).
A component that drives those writers declares itself (`declare_tool`), so a still a capture session's
recorder writes names the session.
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from datetime import UTC, datetime

from carlacontrol.version import __version__

UNKNOWN = "unknown"
# The record's element in an XML file, and the server's inside it, as the C# writers name them.
PRODUCER_ELEMENT = "_producer"
SERVER_ELEMENT = "_server"


class ProducerRecord:
    """Builds the record of what made a file, and tells the CarlaNet writers which tool is running."""

    @staticmethod
    def record(tool: str, *, server: dict | None = None, sumo: str | None = None,
               timed: bool = True) -> dict:
        """The record for a file `tool` is writing now.

        `server` is the server's build identity where a server was used (`server_identity`), `sumo`
        the SUMO release where SUMO ran. `timed` false leaves out the time, for a file two runs of one
        input must write byte for byte.
        """
        document = {
            "tool": tool,
            "tool_version": __version__,
            "carlanet": ProducerRecord.carlanet_version(),
            "server": None if server is None else dict(server),
            "sumo": str(sumo) if sumo else None,
        }
        if timed:
            document["written_utc"] = ProducerRecord.now()
        return document

    @staticmethod
    def xml_element(record: dict) -> ET.Element:
        """The record as a `<_producer>` element, in the shape the C# sidecar writer gives it: each
        value an attribute, one not used left out, and the server a `<_server>` child."""
        element = ET.Element(PRODUCER_ELEMENT)
        for name in ("tool", "tool_version", "carlanet", "sumo", "written_utc"):
            if record.get(name) is not None:
                element.set(name, str(record[name]))
        server = record.get("server")
        if server is not None:
            child = ET.SubElement(element, SERVER_ELEMENT)
            for name, value in server.items():
                child.set(name, ("true" if value else "false") if isinstance(value, bool) else str(value))
        return element

    @staticmethod
    def declare_tool(tool: str) -> None:
        """Declare the tool this process runs as, so every CarlaNet writer in it names `tool` and
        carlacontrol's release. A process that has not loaded carlanet has no such writer to tell."""
        producer = ProducerRecord._carlanet_producer()
        if producer is not None:
            producer.DeclareTool(tool, __version__)

    @staticmethod
    def carlanet_version() -> str | None:
        """The release of the CarlaNet this process loaded, or None where it loaded none."""
        producer = ProducerRecord._carlanet_producer()
        if producer is not None:
            return str(producer.CarlaNetVersion)
        shim = sys.modules.get("carlanet")
        if shim is not None:
            return str(getattr(shim, "__version__", None) or UNKNOWN)
        return None

    @staticmethod
    def server_identity(client: object) -> dict | None:
        """The build identity of the server `client` is connected to, None for no client.

        Never raises: a client or server that cannot say gives `available` false and the reason, with
        the release and world interface where the older calls still answer, and the run carries on.
        """
        if client is None:
            return None
        getter = getattr(client, "get_build_identity", None)
        if getter is not None:
            try:
                return dict(getter())
            except Exception as failure:  # noqa: BLE001 -- recorded as unavailable, never fatal
                return ProducerRecord.unavailable(f"get_build_identity failed: {failure}")
        return ProducerRecord.unavailable(
            "the carlanet client offers no get_build_identity: it was built before the call",
            ProducerRecord._ask(client, "get_server_version"),
            ProducerRecord._ask(client, "get_world_interface_version"))

    @staticmethod
    def unavailable(reason: str, release: str | None = None,
                    world_interface: str | None = None) -> dict:
        """A server identity that could not be had, in the shape `get_build_identity` gives one."""
        return {"available": False, "release": release or UNKNOWN,
                "world_interface": world_interface or UNKNOWN, "reason": reason}

    @staticmethod
    def now() -> str:
        """ISO 8601 UTC to the millisecond, as the C# writers stamp a record."""
        return datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"

    @staticmethod
    def _ask(client: object, method: str) -> str | None:
        call = getattr(client, method, None)
        if call is None:
            return None
        try:
            return str(call())
        except Exception:  # noqa: BLE001 -- an older call that fails leaves that value unknown
            return None

    @staticmethod
    def _carlanet_producer():
        """The CarlaNet writers' `Producer`, where this process has loaded the CarlaNet assemblies."""
        if "clr" not in sys.modules:
            return None
        try:  # optional: only a process that loaded carlanet has the assemblies
            from CarlaNet.Types.Provenance import Producer
        except ImportError:
            return None
        return Producer
