"""Run a compiled scenario in SUMO alone, and refuse one whose planned vehicles do not all get in.

SUMO inserts a vehicle at its departure only when its first lane has room for it; otherwise the vehicle
waits at its entrance, and once it has waited `max-depart-delay` -- 900 s in every compiled
configuration -- SUMO discards it. Both are deterministic: the same configuration and seed insert the
same vehicles at the same steps in every run, and a drive steps SUMO through the same configuration. So
one SUMO-only run of the compiled scenario over its whole span shows exactly what a drive of it would
(`07_Scenario_Authoring.md` check 59): a vehicle the supervision plan names that never enters is an
interval with no vehicle, and it is found here rather than after a capture is spent.

**SUMO's own outputs, not TraCI.** The run writes three files into a scratch directory, and nothing
else is asked of SUMO while it runs:

* the trip information, with unfinished trips written: every vehicle SUMO inserted, with the step it
  departed and how long it waited past its declared departure (`departDelay`), including those still
  driving when the run ends. A vehicle missing from it was never inserted;
* the statistic output: how many vehicles SUMO loaded, inserted and still held waiting at the end. It
  names no discards, so the count discarded is loaded less inserted less waiting -- measured on a fixture
  flow too heavy for its entrance: 21 603 loaded, 3 158 inserted, 1 800 waiting, 16 645 discarded;
* the collision output: every collision SUMO registered, with its time, lane, collider and victim.
  `collision.action` is `warn` in every compiled configuration, so a collision removes nothing and is
  as deterministic as an insertion.

Reading through TraCI would make each simulated step a round trip, where SUMO alone runs at its own
pace, and would learn nothing these files do not say. Measured with the staged SUMO 1.27.0: Bahonar's
week in 160 s, the Arapahoe dwell in 176 s, the Gardnerville orbit in 5 s.

A planned vehicle missing from the trips was discarded after waiting `max-depart-delay`, when its
declared departure lies that far before the end; otherwise it was still waiting when the run ended.
Either way a drive never draws it, and the compile is refused naming it, its declared departure and how
long it waited. Every other measurement -- each planned vehicle's wait, the other vehicles discarded,
every collision -- is reported and refuses nothing.
"""
from __future__ import annotations

import logging
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

from carlacontrol.CivilTimeResolver import ResolvedInstant
from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.SumoInstallation import SumoInstallation

DRY_RUN_CHECK = 59

# The longest a SUMO-only run of one compiled scenario is let run. Bahonar's week, the longest shipped,
# takes under three minutes.
TIMEOUT_S = 3600

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class PlannedVehicle:
    """A vehicle the supervision plan names, and what the compile declared for it."""

    vehicle_id: str
    where: str
    depart: ResolvedInstant
    entrance: str
    refs: tuple[str, ...]


@dataclass
class DryRunResult:
    """What the run showed: every count the lock records, and what the report states in full."""

    sumo_release: str
    end_s: float
    loaded: int = 0
    inserted: int = 0
    waiting_at_end: int = 0
    discarded: int = 0
    teleports: int = 0
    emergency_stops: int = 0
    emergency_braking: int = 0
    planned: list[dict] = field(default_factory=list)
    collisions: list[dict] = field(default_factory=list)
    wall_s: float = 0.0

    @property
    def planned_not_inserted(self) -> list[dict]:
        return [row for row in self.planned if not row["inserted"]]

    def lock_record(self) -> dict:
        """What the lock records: that it ran, with which SUMO, and the counts. No wall time, which
        would make two compiles of one specification differ."""
        missing = self.planned_not_inserted
        return {"ran": True, "sumo_release": self.sumo_release, "begin_s": 0.0,
                "end_s": self.end_s,
                "vehicles": {"loaded": self.loaded, "inserted": self.inserted,
                             "discarded": self.discarded, "waiting_at_end": self.waiting_at_end},
                "planned_vehicles": {"total": len(self.planned),
                                     "inserted": len(self.planned) - len(missing)},
                "collisions": len(self.collisions)}

    def report(self) -> dict:
        """The report's section: the lock's record, every planned vehicle's wait, the other vehicles
        that never got in, and every collision."""
        missing = self.planned_not_inserted
        other_discarded = self.discarded - sum(1 for row in missing if row["outcome"] == DISCARDED)
        other_waiting = self.waiting_at_end - sum(1 for row in missing
                                                  if row["outcome"] == WAITING_AT_END)
        return {**self.lock_record(), "teleports": self.teleports,
                "emergency_stops": self.emergency_stops,
                "emergency_braking": self.emergency_braking,
                "other_vehicles_discarded": other_discarded,
                "other_vehicles_waiting_at_end": other_waiting,
                "planned": self.planned, "collision_list": self.collisions}


INSERTED = "inserted"
DISCARDED = "discarded"
WAITING_AT_END = "waiting_at_end"


class SumoDryRun:
    """Runs one compiled configuration in SUMO alone and holds the planned vehicles to it."""

    def __init__(self, installation: SumoInstallation, findings: CompileFindings) -> None:
        self.installation = installation
        self.findings = findings

    def run(self, config: Path, scratch: Path, planned: list[PlannedVehicle], end_s: float,
            max_depart_delay_s: float, civil_at) -> DryRunResult | None:
        """Run `config` over its span with its own seed and step, writing SUMO's outputs into
        `scratch`, and refuse every planned vehicle that never entered. `civil_at` names a second's
        civil instant, for the collisions the report lists. None when SUMO did not run to the end."""
        result = DryRunResult(sumo_release=self.installation.version or "", end_s=end_s)
        trips, stats, collisions = (scratch / "tripinfo.xml", scratch / "statistics.xml",
                                    scratch / "collisions.xml")
        command = [str(self.installation.sumo), "-c", str(config),
                   "--tripinfo-output", str(trips), "--tripinfo-output.write-unfinished", "true",
                   "--statistic-output", str(stats), "--collision-output", str(collisions),
                   "--no-step-log", "true", "--duration-log.statistics", "false"]
        started = time.perf_counter()
        try:
            completed = subprocess.run(command, capture_output=True, text=True, timeout=TIMEOUT_S,
                                       check=False)
        except subprocess.TimeoutExpired:
            self.findings.refuse(DRY_RUN_CHECK, "dry run", f"SUMO did not finish the scenario "
                                 f"within {TIMEOUT_S} s, so nothing establishes that every planned "
                                 "vehicle is inserted")
            return None
        result.wall_s = time.perf_counter() - started
        if completed.returncode != 0 or not trips.exists() or not stats.exists():
            said = [line for line in (completed.stderr or "").splitlines() if line.strip()]
            self.findings.refuse(DRY_RUN_CHECK, "dry run", f"SUMO exited {completed.returncode} "
                                 "running the compiled scenario: " + " | ".join(said[-5:]))
            return None
        self._read_statistics(stats, result)
        departed = self._read_trips(trips)
        for vehicle in planned:
            result.planned.append(self._account(vehicle, departed.get(vehicle.vehicle_id), end_s,
                                                max_depart_delay_s))
        result.collisions = self._read_collisions(collisions, civil_at) if collisions.exists() else []
        # How long the run took is said here and nowhere persisted: it would make two compiles of one
        # specification differ.
        logger.info("dry run: SUMO ran %s over %g s in %.1f s; %d loaded, %d inserted, %d "
                    "discarded, %d collisions", config.name, end_s, result.wall_s, result.loaded,
                    result.inserted, result.discarded, len(result.collisions))
        return result

    def _account(self, vehicle: PlannedVehicle, trip: dict | None, end_s: float,
                 max_depart_delay_s: float) -> dict:
        """One planned vehicle's row, refusing it when it never entered."""
        declared = vehicle.depart.seconds
        row = {"vehicle_id": vehicle.vehicle_id, "refs": list(vehicle.refs),
               "declared_depart_s": declared, "declared_depart_civil": vehicle.depart.civil,
               "entrance": vehicle.entrance}
        if trip is not None:
            return {**row, "inserted": True, "outcome": INSERTED, "depart_s": trip["depart"],
                    "waited_s": trip["depart_delay"]}
        if declared + max_depart_delay_s < end_s:
            outcome, waited = DISCARDED, max_depart_delay_s
            told = (f"waited {waited:g} s to enter at edge {vehicle.entrance}, and SUMO discarded "
                    f"it (max-depart-delay {max_depart_delay_s:g} s)")
        else:
            outcome, waited = WAITING_AT_END, end_s - declared
            told = (f"was still waiting to enter at edge {vehicle.entrance} when the run ended, "
                    f"{waited:g} s after its departure")
        self.findings.refuse(DRY_RUN_CHECK, vehicle.where,
                             f"'{vehicle.vehicle_id}', named by {', '.join(vehicle.refs)}, is "
                             f"declared to depart at {vehicle.depart.civil} ({declared:g} s) and "
                             f"never enters the run: it {told}. A drive of this scenario never "
                             "draws it. Lighten the traffic entering there, depart it at another "
                             "time or place, or take it out of the plan")
        return {**row, "inserted": False, "outcome": outcome, "depart_s": None, "waited_s": waited}

    @staticmethod
    def _read_statistics(path: Path, result: DryRunResult) -> None:
        root = ET.parse(str(path)).getroot()
        vehicles = root.find("vehicles")
        if vehicles is not None:
            result.loaded = int(vehicles.get("loaded", 0))
            result.inserted = int(vehicles.get("inserted", 0))
            result.waiting_at_end = int(vehicles.get("waiting", 0))
            result.discarded = result.loaded - result.inserted - result.waiting_at_end
        teleports = root.find("teleports")
        if teleports is not None:
            result.teleports = int(teleports.get("total", 0))
        safety = root.find("safety")
        if safety is not None:
            result.emergency_stops = int(safety.get("emergencyStops", 0))
            result.emergency_braking = int(safety.get("emergencyBraking", 0))

    @staticmethod
    def _read_trips(path: Path) -> dict[str, dict]:
        departed = {}
        for trip in ET.parse(str(path)).getroot().iter("tripinfo"):
            departed[trip.get("id")] = {"depart": float(trip.get("depart")),
                                        "depart_delay": float(trip.get("departDelay"))}
        return departed

    @staticmethod
    def _read_collisions(path: Path, civil_at) -> list[dict]:
        rows = []
        for collision in ET.parse(str(path)).getroot().iter("collision"):
            seconds = float(collision.get("time"))
            rows.append({"time_s": seconds, "civil": civil_at(seconds),
                         "type": collision.get("type"), "collider": collision.get("collider"),
                         "victim": collision.get("victim"), "lane": collision.get("lane"),
                         "pos_m": float(collision.get("pos", 0.0))})
        return rows

    @staticmethod
    def remove(scratch: Path) -> None:
        """Remove the scratch directory a run wrote its inputs and outputs into."""
        shutil.rmtree(scratch, ignore_errors=True)
