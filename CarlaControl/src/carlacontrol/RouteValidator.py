"""Validate every authored route with SUMO's own router, and refuse what `duarouter` falsely accepts.

`duarouter` is the authority on whether a trip routes (`07_Scenario_Authoring.md` §5.5): `sumolib`'s
shortest path traverses one-way edges the real router refuses. It is run once over every trip and flow
of a scenario, with `--ignore-errors` so one compile reports every broken route rather than the first,
and `--keep-flows` so a flow stays one flow with one route.

**A `<vehicle>` coming out is not the test** (D7.7). Measured, again on the staged `duarouter` 1.27.0
against the fixture network: a trip and a flow whose destination edge does not exist each come back
with a one-edge route -- `900` -- exit 0, and only a warning on stderr. So a routed result is accepted
only when it **starts on the requested origin, ends on the requested destination, and passes every via
edge and every stop's edge in the order given** (check 12). A request with no routed result at all is
check 11.

The routed edges are what the compiler writes, so no routing happens when the scenario runs (D7.8):
two runs of one scenario cannot diverge because of the router, and a run does not fail at load for a
reason the compile already checked. `duarouter` is given the scenario's SUMO seed so the one choice it
makes per flow -- which member of a type distribution to route a flow with -- is reproducible too.
"""
from __future__ import annotations

import logging
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from xml.sax.saxutils import quoteattr

from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.SumoInstallation import SumoInstallation

ROUTES_CHECK = 11
GUARD_CHECK = 12

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class RouteRequest:
    """One trip or flow to route: where it starts and ends, what it must pass, and in what order."""

    kind: str
    request_id: str
    type_id: str
    depart: float
    from_edge: str
    to_edge: str
    via: tuple[str, ...] = ()
    stops: tuple[tuple[str, float], ...] = ()
    flow_end: float | None = None
    vehs_per_hour: float | None = None
    where: str = ""

    def to_xml(self) -> str:
        via = f" via={quoteattr(' '.join(self.via))}" if self.via else ""
        if self.kind == "flow":
            return (f"    <flow id={quoteattr(self.request_id)} type={quoteattr(self.type_id)} "
                    f"begin=\"{self.depart!r}\" end=\"{self.flow_end!r}\" "
                    f"vehsPerHour=\"{self.vehs_per_hour!r}\" from={quoteattr(self.from_edge)} "
                    f"to={quoteattr(self.to_edge)}{via}/>")
        stops = "".join(f"\n        <stop lane={quoteattr(lane)} endPos=\"{position!r}\" "
                        "duration=\"0\"/>" for lane, position in self.stops)
        close = f">{stops}\n    </trip>" if stops else "/>"
        return (f"    <trip id={quoteattr(self.request_id)} type={quoteattr(self.type_id)} "
                f"depart=\"{self.depart!r}\" from={quoteattr(self.from_edge)} "
                f"to={quoteattr(self.to_edge)}{via}{close}")


@dataclass
class RoutingResult:
    """The routed edge list of every accepted request, and how the router was run."""

    routes: dict[str, tuple[str, ...]] = field(default_factory=dict)
    duarouter: str = ""
    duarouter_version: str = ""
    stderr: list[str] = field(default_factory=list)


class RouteValidator:
    """Runs `duarouter` over a scenario's requests and checks each result against its request."""

    def __init__(self, installation: SumoInstallation, findings: CompileFindings) -> None:
        self.installation = installation
        self.findings = findings

    def route(self, network_path: Path, vehicle_types_xml: str, requests: list[RouteRequest],
              seed: int) -> RoutingResult:
        """Route every request; record check 11 and check 12 refusals; return what was accepted."""
        result = RoutingResult(duarouter=str(self.installation.duarouter),
                               duarouter_version=self.installation.version or "")
        if not requests:
            return result
        ordered = sorted(requests, key=lambda r: (r.depart, r.request_id))
        with tempfile.TemporaryDirectory(prefix="carlacontrol_routes_") as scratch:
            trips = Path(scratch) / "requests.rou.xml"
            routed = Path(scratch) / "routed.rou.xml"
            trips.write_text("<routes>\n" + vehicle_types_xml + "\n"
                             + "\n".join(r.to_xml() for r in ordered) + "\n</routes>\n",
                             encoding="utf-8")
            command = [str(self.installation.duarouter), "-n", str(network_path), "-r", str(trips),
                       "-o", str(routed), "--ignore-errors", "--keep-flows", "--no-step-log",
                       "--seed", str(seed)]
            completed = subprocess.run(command, capture_output=True, text=True, timeout=600,
                                       check=False)
            result.stderr = [line for line in (completed.stderr or "").splitlines() if line.strip()]
            if completed.returncode != 0 or not routed.exists():
                self.findings.refuse(ROUTES_CHECK, "routes",
                                     f"duarouter exited {completed.returncode}: "
                                     + " | ".join(result.stderr[-5:]))
                return result
            produced = self._read(routed)
        for request in ordered:
            where = request.where or f"{request.kind} {request.request_id}"
            edges = produced.get(request.request_id)
            if edges is None:
                said = [line for line in result.stderr if f"'{request.request_id}'" in line]
                self.findings.refuse(ROUTES_CHECK, where,
                                     f"no route from {request.from_edge} to {request.to_edge}"
                                     + (f" via {' '.join(request.via)}" if request.via else "")
                                     + (f"; duarouter said: {' | '.join(said)}" if said else ""))
                continue
            problems = self._guard(request, edges)
            if problems:
                self.findings.refuse(GUARD_CHECK, where,
                                     f"duarouter returned the route {' '.join(edges)}, which "
                                     + "; and ".join(problems)
                                     + ". A route coming out is not a route that was asked for")
                continue
            result.routes[request.request_id] = edges
        return result

    @staticmethod
    def _read(path: Path) -> dict[str, tuple[str, ...]]:
        produced = {}
        for element in ET.parse(str(path)).getroot():
            if element.tag not in ("vehicle", "flow"):
                continue
            route = element.find("route")
            if route is not None and route.get("edges"):
                produced[element.get("id")] = tuple(route.get("edges").split())
        return produced

    @staticmethod
    def _guard(request: RouteRequest, edges: tuple[str, ...]) -> list[str]:
        problems = []
        if edges[0] != request.from_edge:
            problems.append(f"starts on {edges[0]} rather than {request.from_edge}")
        if edges[-1] != request.to_edge:
            problems.append(f"ends on {edges[-1]} rather than the requested {request.to_edge}")
        for label, wanted in (("via", list(request.via)),
                              ("stop", [lane.rsplit("_", 1)[0] for lane, _ in request.stops])):
            missing = RouteValidator._first_missing_in_order(edges, wanted)
            if missing is not None:
                problems.append(f"does not pass {label} edge {missing} in the order given")
        return problems

    @staticmethod
    def _first_missing_in_order(edges: tuple[str, ...], wanted: list[str]) -> str | None:
        position = 0
        for edge in wanted:
            try:
                position = edges.index(edge, position) + 1
            except ValueError:
                return edge
        return None
