"""One ordered path out of a capture run, for a stop, a signal and a fault alike.

`12_Operator_Control_Surface.md` §3.10.2, D12.33. The external caller may stop us at any instant, so
termination is the expected path and there is one order to get right. That order is the inverse of
the one the interactive viewer uses, which despawns the world before it drains the recorder
(`run_SCTMV.py`): **the corpus is made safe before anything is spent on the world.**

| phase | what happens |
|---|---|
| `STOP_WORK` | no new tick is cued -- the capture loop reads `stop_requested` between SUMO steps, so the step in flight finishes and the world is never left half-applied |
| `DRAIN_CAPTURE` | every recorder unsubscribes and drains its encode queue, under the recorder's own ten-second bound |
| `CLOSE_RECORD` | the run's closing observations are taken -- the gate records as of now |
| `WRITE_RESULT` | the run result is written |
| `RELEASE_WORLD` | only now is the world touched: cameras destroyed, the session disposed (bodies, lease, layers, sun, synchronous mode given back) -- each best-effort, each time-boxed, each tolerating a server that is already gone |
| `CLOSE_EXTERNAL` | anything reaching outside the process, closed without draining |

Steps are registered with their phase and run in phase order whatever order they were registered in.

**Installed as the handler for every stop signal the platform delivers** -- `SIGINT` and `SIGTERM`, and
`SIGBREAK` on Windows, where `taskkill` without `/F` delivers no signal a Python process can catch and
a caller stops a console process with `CTRL_BREAK_EVENT` -- **and run as the session's `finally`**. The
handler does no shutdown work itself: it sets the stop flag and names the signal as `closed_by`, and
the capture loop leaves at its next step boundary into the same `finally`. A Python handler runs
between bytecodes, so a signal that arrives while a .NET call is blocking is acted on when the call
returns -- one SUMO step at most, bounded by the client's own frame-wait timeout.

**A second signal abandons the remaining steps and exits at once**, with the artifacts as they are and
exit status 6 (`run_stopped`): a caller sending a second signal is saying *now*, and a shutdown that
ignores it is a hang.

Idempotent -- running it twice runs each step once -- and re-entrant: a call from inside a step
returns at once.

What it must never do (§3.10.2): delete or rewrite anything already written; stage an artifact for
publication at the end; block the corpus on the server, SUMO or the external chain; report a signal
as a fault.
"""
from __future__ import annotations

import logging
import os
import signal
import threading
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

STOP_WORK = 0
DRAIN_CAPTURE = 1
CLOSE_RECORD = 2
WRITE_RESULT = 3
RELEASE_WORLD = 4
CLOSE_EXTERNAL = 5
PHASE_NAMES = {STOP_WORK: "stop work", DRAIN_CAPTURE: "drain capture",
               CLOSE_RECORD: "close the record", WRITE_RESULT: "write the result",
               RELEASE_WORLD: "release the world", CLOSE_EXTERNAL: "close the external chain"}

STOP_SIGNALS = ("SIGINT", "SIGTERM", "SIGBREAK")
EXIT_STATUS_ABANDONED = 6
DEFAULT_STEP_TIMEOUT_S = 15.0


@dataclass
class TerminationStep:
    """One step of the sequence and what happened when it ran."""

    phase: int
    name: str
    action: Callable[[], Any]
    timeout_s: float | None
    order: int = 0
    ran: bool = False
    completed: bool = False
    failure: str | None = None


class RunTerminationSequence:
    """The stop flag, the signal handlers, and the ordered shutdown they lead to."""

    def __init__(self, logger: logging.Logger | None = None,
                 exit_now: Callable[[int], Any] = os._exit,
                 step_timeout_s: float = DEFAULT_STEP_TIMEOUT_S) -> None:
        self.logger = logger or logging.getLogger(__name__)
        self.exit_now = exit_now
        self.step_timeout_s = step_timeout_s
        self.stop_requested = threading.Event()
        self.closed_by: str | None = None
        self.abandoned = False
        self.signals_received = 0
        self.steps: list[TerminationStep] = []
        self._ran = False
        self._running = False
        self._previous_handlers: dict[int, Any] = {}

    # -- stopping -----------------------------------------------------------------------------------------

    def request_stop(self, closed_by: str) -> None:
        """Stop starting work. The first reason given is the one recorded."""
        if self.closed_by is None:
            self.closed_by = closed_by
        self.stop_requested.set()

    def install(self) -> None:
        """Take over every stop signal this platform delivers to a Python process."""
        for name in STOP_SIGNALS:
            number = getattr(signal, name, None)
            if number is None:
                continue
            try:
                self._previous_handlers[number] = signal.signal(number, self._on_signal)
            except (ValueError, OSError):
                # Not the main thread, or a signal this platform will not hand over.
                continue

    def uninstall(self) -> None:
        for number, handler in self._previous_handlers.items():
            try:
                signal.signal(number, handler)
            except (ValueError, OSError, TypeError):
                continue
        self._previous_handlers.clear()

    def _on_signal(self, number: int, _frame: Any) -> None:
        name = signal.Signals(number).name
        self.signals_received += 1
        if self.signals_received > 1:
            self.abandoned = True
            self.logger.warning("second signal (%s): abandoning the remaining shutdown steps; the "
                                "artifacts on disk are as they are", name)
            self.exit_now(EXIT_STATUS_ABANDONED)
            return
        self.logger.warning("%s received: stopping at the next step boundary", name)
        self.request_stop(f"signal:{name}")

    # -- the shutdown ---------------------------------------------------------------------------------

    def add_step(self, phase: int, name: str, action: Callable[[], Any],
                 timeout_s: float | None = None, order: int = 0) -> None:
        """Register a step. `timeout_s` bounds a step that may block on something already gone;
        None runs it inline, for a step that only touches the local disk. Within a phase, steps run
        by `order` and then in the order they were registered."""
        if phase not in PHASE_NAMES:
            raise ValueError(f"{phase} is not a termination phase")
        self.steps.append(TerminationStep(phase, name, action, timeout_s, order))

    def run(self) -> list[TerminationStep]:
        """Run every registered step once, in phase order, each bounded."""
        if self._ran or self._running:
            return self.steps
        self._running = True
        self.stop_requested.set()
        try:
            for step in self.ordered():
                if self.abandoned:
                    break
                self._run_step(step)
        finally:
            self._running = False
            self._ran = True
        return self.steps

    def _run_step(self, step: TerminationStep) -> None:
        step.ran = True
        if step.timeout_s is None:
            try:
                step.action()
                step.completed = True
            except Exception as failure:
                step.failure = repr(failure)
                self.logger.error("shutdown step '%s' failed: %r", step.name, failure)
            return
        outcome: dict[str, Any] = {}

        def target() -> None:
            try:
                step.action()
                outcome["done"] = True
            except Exception as failure:
                outcome["failure"] = repr(failure)

        worker = threading.Thread(target=target, name=f"termination: {step.name}", daemon=True)
        worker.start()
        worker.join(step.timeout_s)
        if worker.is_alive():
            step.failure = f"did not finish within {step.timeout_s:g} s; left behind"
            self.logger.error("shutdown step '%s' did not finish within %g s; going on without "
                              "it", step.name, step.timeout_s)
        elif "failure" in outcome:
            step.failure = outcome["failure"]
            self.logger.error("shutdown step '%s' failed: %s", step.name, step.failure)
        else:
            step.completed = True

    def ordered(self) -> list[TerminationStep]:
        """The steps in the order they run: by phase, then by order, then as registered."""
        return sorted(self.steps, key=lambda s: (s.phase, s.order))

    def report(self) -> list[dict]:
        """Each step as it ran, for the result."""
        return [{"phase": PHASE_NAMES[step.phase], "step": step.name, "ran": step.ran,
                 "completed": step.completed, "failure": step.failure}
                for step in self.ordered()]
