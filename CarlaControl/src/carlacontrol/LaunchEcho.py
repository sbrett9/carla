"""What a capture run is about to do, stated before it commits: the echo before commit.

`12_Operator_Control_Surface.md` §6.4, D12.24. One block, computed once after the offline checks and before
anything is acquired, containing what a reader has to be told to notice that the run is not the run
they meant: the simulated span and the captures it will make, the civil span, the sun at the window's
first and last captured instants and the policy holding it, the world, what is rendered and how far
from a camera, the disk it will cost, where it writes, the wait for every channel's view before the window opens, and the
warnings raised.

**One computation, two renderings.** `to_dict()` is the block, serialised into the resolution report
and the run result whether or not anyone reads it; `render()` is the same block laid out for a
terminal, and computes nothing -- it formats what `to_dict()` holds. An unattended caller's
expectations (`expect.<path>`, check 35) are checked against `values()`, the same block flattened to
dotted paths under `launch_echo.`.

**Every figure comes from the code that will act on it.** The civil instants are
`ScenarioEpoch.civil_instant_at` (the session's `SolarEpoch`); the sun is `WindowSun` (the session's
`DeclaredSun` and `SolarPositionModel`), evaluated with the window opening at its own begin -- the
instant `CaptureSession` hands the session as `window_opens_at`, where it pins a frozen sun and
anchors an advancing one, the prewarm before it lit by that same sun; the disk figure is check 19's;
the instant every view's wait begins is `ViewReadiness.wait_begins_s`, which `CaptureSession` and
check 51 read too.

What it does not predict, and says so: the wall-clock duration (no measured tick rate exists for a
configuration before it runs), how many vehicles will be rendered (by default every vehicle SUMO has,
which no profile published before the run counts, and under an optional limit fewer, by an amount
nothing before the run can count), and, where a stare is aimed at the rendered traffic, where it will
look.

**An optional render-set limit is said plainly.** Under `capture.render_set` `circle` or `cameras`,
or a `capture.render_cap`, the echo states the limit and that a vehicle outside it is simulated by
SUMO and is not in CARLA or the truth, so a reader who did not mean a limit sees one before anything
is acquired.

**So is an accepted skipped dry run.** The scenario block always says what the lock records of the
compiler's SUMO-only run; where that run was skipped and `scenario.accept_skipped_dry_run` let the
launch past check 54, the rendering adds a line saying so, because the run then starts unchecked for
a planned vehicle that never enters.
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
from carlacontrol.ViewReadiness import (
    PER_CAPTURE,
    PICTURE_BLOCK_PX,
    PICTURE_SPAN_TICKS,
    RULE,
    TILES_CEILING_S,
    VEHICLES,
    wait_begins_s,
)
from carlacontrol.WindowSun import WindowSun

LAUNCH_ECHO_VERSION = 1
NOT_PREDICTED = (
    "wall-clock duration: no measured tick rate exists for a configuration before it runs",
    "the rendered population: no population profile of the scenario is published to say how many "
    "vehicles SUMO has at once, so neither how many are drawn nor how many an optional render-set "
    "limit would leave out is known before the run",
)
NOT_PREDICTED_TRAFFIC_AIM = ("where a stare aimed at the rendered traffic will look: the point is "
                             "measured on the last frame before its camera holds for the window, "
                             "and the run result records it")


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
                         "window": window.name or "explicit",
                         "dry_run": cls._dry_run(effective)},
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
            "render": {**cls._render_set(effective),
                       "road_layer_visible": effective.value("capture.road_layer_visible"),
                       "signal_layer_visible": effective.value("capture.signal_layer_visible"),
                       "draw_distance_m": effective.value("capture.draw_distance_m"),
                       "draw_distance": cls._draw_distance(effective.value("capture.draw_distance_m"))},
            "cost": {"bytes_per_captured_second": bytes_per_captured_second,
                     "estimated_bytes": bytes_per_captured_second * window.length_s,
                     "free_bytes": free_bytes, "headroom_s": headroom_s,
                     "basis": "doc 10 §4.6's measured PNG size; content-dependent; the truth "
                              "sidecars, which grow with the traffic in frame, are not counted"},
            "writes": {"capture_directory": str(capture_directory),
                       "result_path": str(result_path)},
            "pacing": {"mode": effective.value("pacing.mode"),
                       "real_time_factor": effective.value("pacing.real_time_factor"),
                       "min_achieved_factor": effective.value("pacing.min_achieved_factor")},
            "readiness": cls._readiness(effective),
            "warnings": list(warning_codes),
            "not_predicted": list(NOT_PREDICTED) + (
                [NOT_PREDICTED_TRAFFIC_AIM] if any(
                    effective.channel_description(i).aims_at_rendered_traffic()
                    for i in range(channels)) else []),
        }
        return cls(block)

    @staticmethod
    def _dry_run(effective: EffectiveRunConfiguration) -> dict:
        """Whether the compile ran the scenario in SUMO alone, as the lock records it, and whether a
        run that did not is one this launch accepted (check 54 has passed, so one of the two holds)."""
        scenario = effective.scenario
        ran = scenario.dry_run_ran
        return {"ran": ran,
                "skipped_accepted": not ran,
                "statement": scenario.describe_dry_run()
                + ("" if ran else "; accepted by scenario.accept_skipped_dry_run, so the run "
                                  "starts unchecked for a planned vehicle that never enters")}

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
        opens_at = window.begin_s
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
                first = effective.first_rendered_s
                block["held_at_note"] = (f"the session pins the sun at the window's opening, "
                                         f"t={opens_at:g}"
                                         + (f", and the prewarm from t={first:g} is lit by it"
                                            if first < opens_at else ""))
        return block

    @staticmethod
    def _render_set(effective: EffectiveRunConfiguration) -> dict:
        """Which vehicles get a body: every vehicle SUMO has, or the optional limit chosen, its
        settings, and what it leaves out."""
        render_set = effective.value("capture.render_set")
        cap = effective.value("capture.render_cap")
        region = effective.value("capture.render_region")
        block: dict = {"set": render_set, "region": region,
                       "hysteresis_m": effective.value("capture.render_hysteresis_m"),
                       "min_pixels": effective.value("capture.render_min_pixels"),
                       "admit_lead_s": effective.value("capture.render_admit_lead_s"),
                       "release_lag_s": effective.value("capture.render_release_lag_s"),
                       "cap": cap, "limited": render_set != "all" or cap is not None}
        circle = None if region is None else (f"the circle of {region['radius_m']:g} m at "
                                              f"({region['x_m']:g}, {region['y_m']:g})")
        if render_set == "circle":
            chosen = f"the vehicles inside {circle}"
        elif render_set == "cameras":
            chosen = (f"the vehicles in or approaching a channel camera's view (lead "
                      f"{block['admit_lead_s']:g} s, lag {block['release_lag_s']:g} s, "
                      f"{block['min_pixels']:g} px), "
                      + (f"{circle} until the cameras are placed" if circle is not None
                         else "every vehicle until the cameras are placed"))
        else:
            chosen = "every vehicle SUMO has"
        if cap is not None:
            chosen += f", at most {cap} at once"
        block["vehicles"] = chosen
        block["left_out"] = (None if not block["limited"] else
                             "an optional limit: a vehicle outside it is simulated by SUMO and is not "
                             "in CARLA -- no body, no frame, no truth record")
        return block

    @staticmethod
    def _draw_distance(metres: float | None) -> str:
        """What the draw distance does to the run, in words: nothing, or what it changes and what it
        leaves alone."""
        if metres is None:
            return "none: every body is drawn at any range"
        return (f"{float(metres):g} m, rendering only: every vehicle keeps its body, its pose and its "
                "truth; a body farther than that from a channel's camera is not in that channel's "
                "images, and its sidecars mark it beyond_draw_distance")

    @staticmethod
    def _readiness(effective: EffectiveRunConfiguration) -> dict:
        """The wait for every channel's view inside the prewarm (03 §9.5.1), and where it begins."""
        window = effective.window
        follows = any(effective.channel_description(index).aims_at_rendered_traffic()
                      for index in range(effective.channel_count))
        delta = float(effective.value("capture.world_delta_s"))
        period = effective.ticks_per_frame
        ceiling_frames = int(effective.value("capture.picture_ceiling_frames"))
        tolerance = float(effective.value("capture.picture_tolerance_levels"))
        begins = wait_begins_s(window.begin_s, effective.first_rendered_s,
                               float(effective.value("scenario.sumo_step_s")), delta, period,
                               ceiling_frames, follows)
        return {"waits": True, "rule": RULE,
                "tiles": "world.get_view_readiness after every prewarm step: the camera published "
                         "on the last tick, every visible tileset at load progress 100, no failed "
                         "tile in view",
                "picture": f"the camera's frame within {tolerance:g} gray levels of its frame at "
                           f"least {PICTURE_SPAN_TICKS} ticks earlier in its worst judged "
                           f"{PICTURE_BLOCK_PX}-pixel block",
                "vehicles": VEHICLES,
                "tiles_ceiling_s": TILES_CEILING_S,
                # The camera's own frames from its tiles being in, and the simulated seconds they
                # are at the capture rate.
                "picture_ceiling_frames": ceiling_frames,
                "picture_ceiling_s": round(ceiling_frames * period * delta, 6),
                "picture_tolerance_levels": tolerance,
                "from_s": begins, "until_s": window.begin_s,
                "traffic_stare_holds_from_s": begins if follows else None,
                "not_ready": "refused at pre-roll (check 50); the window's first frame is not "
                             "moved",
                "per_capture": PER_CAPTURE}

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
                 f"        caller: {b['caller']}"]
        if b["scenario"]["dry_run"]["skipped_accepted"]:
            lines.append(f"  dry run     {b['scenario']['dry_run']['statement']}")
        lines += [f"  simulated   {sim['begin_s']:,.0f} - {sim['end_s']:,.0f} s      "
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
        lines.append(f"  render      {render['vehicles']}   road "
                     f"{'drawn' if render['road_layer_visible'] else 'hidden'}, signals "
                     f"{'drawn' if render['signal_layer_visible'] else 'hidden'}")
        if render["left_out"] is not None:
            lines.append(f"              {render['left_out']}")
        lines.append(f"              draw distance {render['draw_distance']}")
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
        readiness = b["readiness"]
        lines.append(f"  readiness   every view's tiles (ceiling {readiness['tiles_ceiling_s']:g} s) "
                     f"and picture (ceiling {readiness['picture_ceiling_frames']} of the camera's "
                     f"frames, {readiness['picture_ceiling_s']:g} s at {cap['capture_hz']:g} Hz, "
                     f"settled within {readiness['picture_tolerance_levels']:g} gray levels), from "
                     f"t={readiness['from_s']:,.0f} to t={readiness['until_s']:,.0f}; not ready by "
                     "then refuses at pre-roll")
        if readiness["traffic_stare_holds_from_s"] is not None:
            lines.append(f"              a stare aimed at the traffic follows it until "
                         f"t={readiness['traffic_stare_holds_from_s']:,.0f}, then holds")
        lines.append(f"  writes      {b['writes']['capture_directory']}")
        lines.append(f"              result {b['writes']['result_path']}")
        warnings = b["warnings"]
        lines.append(f"  warnings    {len(warnings)}"
                     + (f"   ({', '.join(warnings)})" if warnings else ""))
        return "\n".join(lines)
