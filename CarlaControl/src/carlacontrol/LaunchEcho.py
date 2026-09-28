"""What a capture run is about to do, stated before it commits: the echo before commit.

`12_Operator_Control_Surface.md` §6.4, D12.24. One block, computed once after the offline checks and before
anything is acquired, containing what a reader has to be told to notice that the run is not the run
they meant: the simulated span and the captures it will make, the civil span, the sun at the window's
first and last captured instants and the policy holding it, the world, the render region, the disk it
will cost, where it writes, and the warnings raised.

**One computation, two renderings.** `to_dict()` is the block, serialised into the resolution report
and the run result whether or not anyone reads it; `render()` is the same block laid out for a
terminal, and computes nothing -- it formats what `to_dict()` holds. An unattended caller's
expectations (`expect.<path>`, check 35) are checked against `values()`, the same block flattened to
dotted paths under `launch_echo.`.

**Every figure comes from the code that will act on it.** The civil instants are
`ScenarioEpoch.civil_instant_at` (the session's `SolarEpoch`); the sun is `WindowSun` (the session's
`DeclaredSun` and `SolarPositionModel`), evaluated with the window opening at the first frame the
session renders -- which is where it pins a frozen sun, the prewarm's first frame, so the echo states
that instant rather than the window's begin; the disk figure is check 19's.

What it does not predict, and says so: the wall-clock duration (no measured tick rate exists for a
configuration before it runs) and the in-region population (no population profile is published,
check 21).
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import IlluminationPolicy

from carlacontrol.EffectiveRunConfiguration import EffectiveRunConfiguration
from carlacontrol.IlluminationBand import IlluminationBand
from carlacontrol.ScenarioEpoch import ScenarioEpoch
from carlacontrol.WindowSun import WindowSun

LAUNCH_ECHO_VERSION = 1
NOT_PREDICTED = (
    "wall-clock duration: no measured tick rate exists for a configuration before it runs",
    "in-region population: no population profile of the scenario is published (check 21)",
)


class LaunchEcho:
    """The pre-commit statement of one launch."""

    def __init__(self, block: dict) -> None:
        self.block = block

    @classmethod
    def compute(cls, effective: EffectiveRunConfiguration, session_id: str,
                capture_directory: Path, result_path: Path, free_bytes: float,
                headroom_s: float | None, bytes_per_captured_second: float,
                warning_codes: list[str]) -> LaunchEcho:
        """Compute the block. The configuration must have passed the offline checks."""
        window = effective.window
        epoch = ScenarioEpoch.read(effective.epoch_object())
        hz = float(effective.value("capture.capture_hz"))
        channels = effective.channel_count
        per_channel = int(window.length_s * hz)
        block: dict = {
            "launch_echo_version": LAUNCH_ECHO_VERSION,
            "session_id": session_id,
            "caller": effective.value("caller"),
            "scenario": {"scenario_id": effective.scenario.scenario_id,
                         "lock": effective.scenario.describe(),
                         "window": window.name or "explicit"},
            "simulated": {"begin_s": window.begin_s, "end_s": window.end_s,
                          "length_s": window.length_s, "end_source": window.end_source,
                          "prewarm_s": effective.prewarm_s,
                          "first_rendered_s": effective.first_rendered_s},
            "captures": {"capture_hz": hz, "channels": channels, "per_channel": per_channel,
                         "total": per_channel * channels,
                         "frames_per_hour": int(round(3600.0 * hz * channels))},
            "civil": {"begin": epoch.civil_instant_at(window.begin_s),
                      "end": epoch.civil_instant_at(window.end_s),
                      "first_rendered": epoch.civil_instant_at(effective.first_rendered_s),
                      "epoch": epoch.describe()},
            "sun": cls._sun(effective, epoch),
            "world": {"map_name": effective.value("world.map_name"),
                      "package": effective.world.path.name,
                      "network_fingerprint": effective.value("world.network_fingerprint"),
                      "origin": [effective.value("world.origin_latitude"),
                                 effective.value("world.origin_longitude")]},
            "render": {"region": effective.value("capture.render_region"),
                       "hysteresis_m": effective.value("capture.render_hysteresis_m"),
                       "cap": effective.value("capture.render_cap"),
                       "cap_hard": effective.value("capture.render_cap_hard"),
                       "road_layer_visible": effective.value("capture.road_layer_visible"),
                       "signal_layer_visible": effective.value("capture.signal_layer_visible")},
            "cost": {"bytes_per_captured_second": bytes_per_captured_second,
                     "estimated_bytes": bytes_per_captured_second * window.length_s,
                     "free_bytes": free_bytes, "headroom_s": headroom_s,
                     "basis": "doc 10 §4.6's measured capture sizes; content-dependent"},
            "writes": {"capture_directory": str(capture_directory),
                       "result_path": str(result_path)},
            "pacing": {"mode": effective.value("pacing.mode"),
                       "real_time_factor": effective.value("pacing.real_time_factor"),
                       "min_achieved_factor": effective.value("pacing.min_achieved_factor")},
            "warnings": list(warning_codes),
            "not_predicted": list(NOT_PREDICTED),
        }
        return cls(block)

    @staticmethod
    def _sun(effective: EffectiveRunConfiguration, epoch: ScenarioEpoch) -> dict:
        policy = IlluminationPolicy.FromJson(json.dumps(effective.illumination_object()))
        name = str(policy.Name)
        if not bool(policy.BindsTheSun):
            return {"policy": name, "binds": False,
                    "statement": "left as the world holds it; the run's lighting honours no epoch"}
        window = effective.window
        origin = (float(effective.value("world.origin_latitude")),
                  float(effective.value("world.origin_longitude")))
        sun = WindowSun(epoch, policy, origin[0], origin[1])
        opens_at = effective.first_rendered_s
        at_begin = sun.at(opens_at, window.begin_s)
        at_end = sun.at(opens_at, window.end_s)
        block = {"policy": name, "binds": True, "advances": bool(policy.Advances),
                 "rate": float(policy.Rate) if bool(policy.Advances) else None,
                 "elevation_kind": WindowSun.ELEVATION_KIND,
                 "window_open_s": opens_at,
                 "at_begin": dict(at_begin.to_dict(),
                                  band=IlluminationBand.of(at_begin.elevation_deg)),
                 "at_end": dict(at_end.to_dict(), band=IlluminationBand.of(at_end.elevation_deg))}
        if not bool(policy.Advances):
            block["held_at"] = f"{at_begin.sun_date} {at_begin.sun_clock}"
            if name == "freeze_at_window_start":
                block["held_at_note"] = ("the session pins the sun at the first frame it renders, "
                                         f"the prewarm's first, t={opens_at:g}")
        return block

    # -- reading ---------------------------------------------------------------------------------------

    def to_dict(self) -> dict:
        return json.loads(json.dumps(self.block))

    def values(self) -> dict[str, Any]:
        """The block flattened to `launch_echo.<a>.<b>` paths, for expectations."""
        flat: dict[str, Any] = {}

        def walk(node: Any, prefix: str) -> None:
            if isinstance(node, dict):
                for key, value in node.items():
                    walk(value, f"{prefix}.{key}")
            else:
                flat[prefix] = node

        walk(self.to_dict(), "launch_echo")
        return flat

    def render(self) -> str:
        """The block laid out for a person. Formats; computes nothing."""
        b = self.block
        sim, civil, cap, sun = b["simulated"], b["civil"], b["captures"], b["sun"]
        lines = [f"{b['scenario']['lock']}  ::  window {b['scenario']['window']}"
                 f"        caller: {b['caller']}",
                 f"  simulated   {sim['begin_s']:,.0f} - {sim['end_s']:,.0f} s      "
                 f"({sim['length_s']:,.0f} s, {cap['per_channel']:,} captures per channel at "
                 f"{cap['capture_hz']:g} Hz x {cap['channels']})",
                 f"              end: {sim['end_source']}; prewarm {sim['prewarm_s']:g} s from "
                 f"t={sim['first_rendered_s']:,.0f}",
                 f"  civil       {civil['begin']}  ->  {civil['end']}"]
        if sun["binds"]:
            begin, end = sun["at_begin"], sun["at_end"]
            lines.append(f"  sun         elevation {begin['elevation_deg']:+.2f} deg -> "
                         f"{end['elevation_deg']:+.2f} deg ({sun['elevation_kind']}), "
                         f"{begin['band']} -> {end['band']}    policy: {sun['policy']}"
                         + (f", rate {sun['rate']:g}" if sun["advances"] else ""))
            if "held_at" in sun:
                lines.append(f"              held at {sun['held_at']}"
                             + (f": {sun['held_at_note']}" if "held_at_note" in sun else ""))
        else:
            lines.append(f"  sun         policy {sun['policy']}: {sun['statement']}")
        world = b["world"]
        lines.append(f"  world       {world['map_name']} ({world['package']})   net "
                     f"{str(world['network_fingerprint'])[:12]}   origin "
                     f"{world['origin'][0]}, {world['origin'][1]}")
        render = b["render"]
        region = render["region"] or {}
        lines.append(f"  render      region {region.get('radius_m', '?')} m at "
                     f"({region.get('x_m', '?')}, {region.get('y_m', '?')})   cap {render['cap']}"
                     f" (hard {render['cap_hard']})   road "
                     f"{'drawn' if render['road_layer_visible'] else 'hidden'}, signals "
                     f"{'drawn' if render['signal_layer_visible'] else 'hidden'}")
        cost = b["cost"]
        headroom = cost["headroom_s"]
        lines.append(f"  cost        ~{cost['estimated_bytes'] / 1e9:.1f} GB estimated; "
                     f"{cost['free_bytes'] / 1e9:.1f} GB free is "
                     + (f"{headroom / 3600:.2f} h of capture" if headroom is not None
                        else "unmeasured"))
        pacing = b["pacing"]
        lines.append(f"  pacing      {pacing['mode']}" + (
            f", {pacing['real_time_factor']:g}x real time, floor {pacing['min_achieved_factor']:g}"
            if pacing["mode"] == "wall_clock" else ""))
        lines.append(f"  writes      {b['writes']['capture_directory']}")
        lines.append(f"              result {b['writes']['result_path']}")
        warnings = b["warnings"]
        lines.append(f"  warnings    {len(warnings)}"
                     + (f"   ({', '.join(warnings)})" if warnings else ""))
        return "\n".join(lines)
