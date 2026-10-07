"""What a capture run shows while it proceeds.

`12_Operator_Control_Surface.md` §7.1, §7.4.1, D12.14. **Every figure shown is one the run result
also carries, read from the same source**: the monitor is handed `RunCloseoutReport.snapshot()` and
formats it, and it holds no reference to the session or a recorder, so it cannot compute a number of
its own. A monitor that disagreed with the record would be believed over it.

On a terminal it redraws a panel in place; where standard output is not a terminal -- an unattended
caller's log -- it writes one line every `line_interval_s` through `logging`, so nothing depends on a
display (§3.10.1 M1). A loud condition is written at once, at `WARNING`, whatever the interval.

The panel is the capture operator's (§7.4.1): it shows the declared sun's elevation and the policy
because that is how a wrong sun is made visible, and the session's latest admission pass -- the
population, every vehicle of which is rendered unless an optional limit was chosen, the vehicles
admitted and released at it, and under a limit the eligible, the drawn and the shed -- because the
population is what sets the pace and a limit is what thins it. Until the recorders start it also shows where each channel's view stands in its
wait for its tiles, and for its picture where `capture.picture_settled_wait` is true (03 §9.5.1), because that wait is what the prewarm is spent on. An exercised operator's picture is a different display and never this one (D12.31).
"""
from __future__ import annotations

import logging
import sys
import time
from collections.abc import Callable
from typing import Any, TextIO

from carlacontrol.ViewReadiness import describe_view

PANEL_INTERVAL_S = 1.0
LINE_INTERVAL_S = 10.0


class SessionMonitor:
    """Formats run snapshots for a person or a log."""

    def __init__(self, stream: TextIO | None = None, is_terminal: bool | None = None,
                 clock: Callable[[], float] = time.monotonic, enabled: bool = True,
                 logger: logging.Logger | None = None,
                 panel_interval_s: float = PANEL_INTERVAL_S,
                 line_interval_s: float = LINE_INTERVAL_S) -> None:
        self.stream = stream if stream is not None else sys.stdout
        if is_terminal is None:
            is_terminal = bool(getattr(self.stream, "isatty", lambda: False)())
        self.is_terminal = is_terminal
        self.clock = clock
        self.enabled = enabled
        self.logger = logger or logging.getLogger(__name__)
        self.interval_s = panel_interval_s if is_terminal else line_interval_s
        self._last_shown: float | None = None
        self._panel_height = 0
        self._loud_seen: set[str] = set()

    def update(self, snapshot: dict, force: bool = False) -> None:
        """Show the snapshot, if the interval has passed or `force` is set."""
        if not self.enabled:
            return
        now = self.clock()
        if not force and self._last_shown is not None and now - self._last_shown < self.interval_s:
            return
        self._last_shown = now
        if self.is_terminal:
            self._draw(self.lines(snapshot))
        else:
            self.logger.info("%s", self.line(snapshot))

    def loud(self, condition: str, message: str) -> None:
        """Say a loud condition once, at once."""
        if condition in self._loud_seen:
            return
        self._loud_seen.add(condition)
        self._panel_height = 0
        self.logger.warning("LOUD %s: %s", condition, message)

    def close(self) -> None:
        if self.is_terminal and self._panel_height:
            self.stream.write("\n")
            self.stream.flush()
        self._panel_height = 0

    # -- formatting ---------------------------------------------------------------------------------

    @classmethod
    def lines(cls, snapshot: dict) -> list[str]:
        """The panel's rows, formatted from the snapshot alone."""
        rows = [f"{snapshot['scenario']} :: {snapshot['window_name']}"
                + cls._policy_flag(snapshot)]
        now = snapshot["sim_time_s"]
        window = snapshot["window"]
        progress = window.get("progress")
        illumination = snapshot["illumination"] or {}
        rows.append(f"sim   t={cls._n(now, ',.1f')} / {window['end_s']:,.0f}   window "
                    f"{'-' if progress is None else f'{100.0 * progress:.1f}%'}   |  civil "
                    f"{illumination.get('declared_civil') or '-'}  sun "
                    f"{cls._n(illumination.get('sun_elevation_deg'), '+.2f')} deg")
        pacing = snapshot["pacing"]
        if pacing is not None:
            requested = f"{pacing['declared_factor']:g}" if pacing["paced"] else "as available"
            rows.append(f"pace  requested {requested}  achieved "
                        f"{cls._n(pacing['achieved_factor'], '.3f')}  last window "
                        f"{cls._n(pacing['last_window_factor'], '.3f')}"
                        + (f"  behind {pacing['behind_schedule_s']:.1f} s"
                           if pacing["paced"] else ""))
        admission = snapshot.get("admission")
        if admission is not None and admission.get("limited"):
            rows.append(f"sumo  population {admission['population']}   eligible "
                        f"{admission['eligible']}   drawn {admission['admitted']}   shed "
                        f"{admission['shed']}   without a body {admission['left_out']}   admitted "
                        f"in all {admission['total_admissions']:,}")
        elif admission is not None:
            rows.append(f"sumo  population {admission['population']}, all rendered   admitted "
                        f"{admission['newly_admitted']}   released {admission['released']}   "
                        f"admitted in all {admission['total_admissions']:,}")
        render = snapshot["render"]
        if render is not None:
            rows.append(f"rend  rendered now {render['rendered_now']}   ticks {render['ticks']:,}"
                        f"   steps {render['sumo_steps']:,}   batch failures "
                        f"{render['batch_failures']}")
        for view in cls._views(snapshot):
            rows.append(f"view  {view['sensor_id']}   {describe_view(view)}")
        for channel in snapshot["channels"]:
            rows.append(f"chan  {channel['sensor_id']}   written {cls._n(channel['written'], 'd')}"
                        f"   recorder-dropped {cls._n(channel['recorder_dropped'], 'd')}"
                        f"   illumination unpaired {cls._n(channel['illumination_unpaired'], 'd')}"
                        f"   occlusion {cls._n(channel['occlusion_measured'], 'd')}/"
                        f"{cls._n(channel['occlusion_unmatched'], 'd')} unmatched")
        return rows

    @classmethod
    def line(cls, snapshot: dict) -> str:
        """One log line, formatted from the snapshot alone."""
        illumination = snapshot["illumination"] or {}
        pacing = snapshot["pacing"] or {}
        channels = ", ".join(f"{c['sensor_id']} {cls._n(c['written'], 'd')} written "
                             f"{cls._n(c['recorder_dropped'], 'd')} dropped"
                             for c in snapshot["channels"])
        progress = snapshot["window"].get("progress")
        admission = snapshot.get("admission")
        if admission is None:
            population = ""
        elif admission.get("limited"):
            population = (f"; population {admission['population']}, drawn {admission['admitted']}, "
                          f"{admission['left_out']} without a body")
        else:
            population = f"; population {admission['population']}, all rendered"
        views = "".join(f"; view {view['sensor_id']} {describe_view(view)}"
                        for view in cls._views(snapshot))
        return (f"t={cls._n(snapshot['sim_time_s'], '.1f')} "
                f"({'-' if progress is None else f'{100.0 * progress:.1f}%'}) civil "
                f"{illumination.get('declared_civil') or '-'} sun "
                f"{cls._n(illumination.get('sun_elevation_deg'), '+.2f')} deg; pace "
                f"{cls._n(pacing.get('achieved_factor'), '.3f')}{population}; "
                f"{channels or 'no channel'}{views}")

    @staticmethod
    def _views(snapshot: dict) -> list[dict]:
        """Each channel's wait for its view, while nothing records yet."""
        readiness = snapshot.get("readiness")
        if not readiness or snapshot["channels"]:
            return []
        return readiness["channels"]

    @staticmethod
    def _policy_flag(snapshot: dict) -> str:
        illumination = snapshot["illumination"]
        return "" if not illumination else f"   [{illumination['policy']}]"

    @staticmethod
    def _n(value: Any, spec: str) -> str:
        return "-" if value is None else format(value, spec)

    def _draw(self, rows: list[str]) -> None:
        if self._panel_height:
            self.stream.write(f"\x1b[{self._panel_height}F\x1b[J")
        self.stream.write("\n".join(rows) + "\n")
        self.stream.flush()
        self._panel_height = len(rows)
