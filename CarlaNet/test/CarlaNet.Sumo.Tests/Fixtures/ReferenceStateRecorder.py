"""Record every SUMO vehicle's state, step by step, through SUMO's own reference TraCI client.

This is the oracle half of the agreement check in ReferenceClientAgreementTests. It drives the same
scenario with the same seed that CarlaNet.Sumo drives, and writes the same columns, so the two
decodings of the same frames can be differenced. Two independent decoders of a wire protocol do not
agree by accident.

It reads per vehicle rather than by subscription on purpose: the C# side reads by subscription, so
the comparison crosses both the client boundary and the read-path boundary at once.

Run it directly:

    python ReferenceStateRecorder.py --tools <SUMO_HOME>/tools --sumo <sumo binary>
                                     --config <a .sumocfg> --steps 200 --output states.csv
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

COLUMNS = "step,id,x,y,angle,speed,edge,lane,type,signals"


class ReferenceStateRecorder:
    """Steps a SUMO scenario and writes every vehicle's state at every step."""

    def __init__(self, tools: Path, sumo_binary: Path, config: Path, steps: int,
                 warmup_seconds: float = 0.0) -> None:
        self.tools = tools
        self.sumo_binary = sumo_binary
        self.config = config
        self.steps = steps
        self.warmup_seconds = warmup_seconds

    def record(self, output: Path) -> int:
        """Write one row per vehicle per step and return how many rows were written."""
        traci = self._import_traci()
        traci.start([str(self.sumo_binary), "-c", str(self.config), "--no-step-log", "true"])
        rows: list[str] = [COLUMNS]
        try:
            if self.warmup_seconds > 0:
                # One call, not a loop: a step command naming a target time advances straight to it,
                # which is what makes reaching a populated part of a scenario cheap.
                traci.simulationStep(self.warmup_seconds)
            for step in range(self.steps):
                traci.simulationStep()
                for vehicle_id in sorted(traci.vehicle.getIDList()):
                    rows.append(self._row(traci, step, vehicle_id))
        finally:
            traci.close()

        output.write_text("\n".join(rows) + "\n", encoding="utf-8")
        return len(rows) - 1

    def _import_traci(self):
        """Import SUMO's client from the installation the caller named.

        Imported here rather than at module scope because the tools directory it lives in is only
        on sys.path once the caller has said where it is.
        """
        if str(self.tools) not in sys.path:
            sys.path.insert(0, str(self.tools))
        import traci

        return traci

    @staticmethod
    def _row(traci, step: int, vehicle_id: str) -> str:
        x, y = traci.vehicle.getPosition(vehicle_id)
        fields = [
            str(step),
            vehicle_id,
            f"{x:.17g}",
            f"{y:.17g}",
            f"{traci.vehicle.getAngle(vehicle_id):.17g}",
            f"{traci.vehicle.getSpeed(vehicle_id):.17g}",
            traci.vehicle.getRoadID(vehicle_id),
            traci.vehicle.getLaneID(vehicle_id),
            traci.vehicle.getTypeID(vehicle_id),
            str(traci.vehicle.getSignals(vehicle_id)),
        ]
        return ",".join(fields)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools", type=Path, required=True,
                        help="the tools directory of a SUMO installation, holding traci")
    parser.add_argument("--sumo", type=Path, required=True, help="the sumo binary to run")
    parser.add_argument("--config", type=Path, required=True, help="the .sumocfg to run")
    parser.add_argument("--steps", type=int, required=True, help="how many steps to record")
    parser.add_argument("--warmup", type=float, default=0.0,
                        help="simulated seconds to advance to before recording starts")
    parser.add_argument("--output", type=Path, required=True, help="where to write the rows")
    arguments = parser.parse_args()

    recorder = ReferenceStateRecorder(arguments.tools, arguments.sumo, arguments.config,
                                      arguments.steps, arguments.warmup)
    written = recorder.record(arguments.output)
    print(f"{written} rows written to {arguments.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
