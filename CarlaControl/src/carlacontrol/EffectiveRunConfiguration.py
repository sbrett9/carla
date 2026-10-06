"""The resolved configuration of one capture run: every field, its value, and where it came from.

`12_Operator_Control_Surface.md` §3.6-§3.7, D12.3. Every field the run is subject to is materialised,
including every value that came from a default, and carries:

* `value` -- what the run uses;
* `layer` -- which layer set it: `tool_default`, `site_profile`, `world_package`,
  `scenario_package`, `run_configuration` or `operator_override`, or `derived` (computed from other
  fields), `pinned_by_policy` (the advancing sun's rate), `not_applicable` (a field the chosen mode
  or policy does not take) and `unsupplied` (no layer gave a value, which a check refuses);
* `provenance` -- the artifact and key, the file, or the command-line text it was read from;
* `tool_default` -- carried beside the value even when the value came from elsewhere, so a
  deliberate match is told from an accident and a moved default shows in a diff;
* `overridden` -- what a higher layer replaced, so an override is an event in the record rather than
  an absence; and `dropped`, a lower layer's illumination field that belongs to a policy a higher
  layer replaced.

The document is written as a flat map keyed by the field's dotted path -- the same information as
§3.7's nested example, with one key per field.

**Reproducing a run is reading it back** (§3.6, R1). `to_run_configuration()` is a valid run
configuration: every resolved science field, the world and scenario bindings (so a replay against a
package that has changed is refused by check 3, naming both values), and the scenario by id rather
than by path. Machine facts -- the server, the paths, the SUMO installation -- are left out: they are
this machine's, recorded in the lock with their provenance, and a replay on another machine resolves
its own. `digest` is the SHA-256 of that document, so two machines resolving the same inputs report
the same digest.

The object is immutable once resolved: nothing during the run writes to it.
"""
from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass, field
from functools import cached_property
from typing import Any

from carlacontrol.ChannelDescription import ChannelDescription
from carlacontrol.RunConfiguration import (
    LAYER_DERIVED,
    LAYER_NOT_APPLICABLE,
    LAYER_PINNED_BY_POLICY,
    LAYER_UNSUPPLIED,
    NO_DEFAULT,
    SUPPLIED_BY_SITE,
    RunConfiguration,
)
from carlacontrol.ScenarioPackage import ScenarioPackage
from carlacontrol.SiteProfile import SiteProfile
from carlacontrol.WorldPackageReader import WorldPackageReader

EFFECTIVE_CONFIGURATION_VERSION = 1

# Fields a replayed configuration does not carry: this machine's facts, and where this run writes.
_NOT_REPLAYED = frozenset({"world_package", "result_path"})
# Values a replay derives again from the fields it does carry, so writing them would restate them.
_REDERIVED_LAYERS = frozenset({LAYER_DERIVED, LAYER_PINNED_BY_POLICY, LAYER_NOT_APPLICABLE,
                               LAYER_UNSUPPLIED})

_EXPLICIT_WINDOW = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*:\s*(\d+(?:\.\d+)?)?\s*$")

# The illumination object's fields, as `CarlaNet.CoSim.IlluminationPolicy` reads them.
_ILLUMINATION_FIELDS = ("policy", "rate_sun_s_per_sim_s", "freeze_at_civil_time",
                        "freeze_date_advances", "require_sun", "note")
ILLUMINATION_VERSION = 1


@dataclass(frozen=True)
class FieldResolution:
    """One resolved field."""

    value: Any
    layer: str
    provenance: str
    tool_default: Any = NO_DEFAULT
    overridden: tuple[dict, ...] = ()
    dropped: tuple[dict, ...] = ()
    note: str | None = None
    environment_variable: str | None = None

    def to_dict(self) -> dict:
        entry: dict = {"value": self.value, "layer": self.layer, "provenance": self.provenance}
        if self.tool_default is not NO_DEFAULT:
            entry["tool_default"] = self.tool_default
        entry["overridden"] = list(self.overridden) or None
        if self.dropped:
            entry["dropped"] = list(self.dropped)
        if self.note:
            entry["note"] = self.note
        if self.environment_variable:
            entry["environment_variable"] = self.environment_variable
        return entry


@dataclass(frozen=True)
class ResolvedWindow:
    """The simulated time a run renders: where it begins and ends, and where each came from."""

    name: str | None
    begin_s: float
    end_s: float
    end_source: str
    declared: dict = field(default_factory=dict)

    @property
    def length_s(self) -> float:
        return self.end_s - self.begin_s

    def to_dict(self) -> dict:
        return {"name": self.name, "begin_s": self.begin_s, "end_s": self.end_s,
                "length_s": self.length_s, "end_source": self.end_source}


class WindowResolutionError(ValueError):
    """`capture.window` does not resolve; the message says why and lists the declared names."""


class EffectiveRunConfiguration:
    """The resolved, immutable configuration of one run, and the packages it is bound to."""

    def __init__(self, fields: dict[str, FieldResolution],
                 channels: list[dict[str, FieldResolution]], scenario: ScenarioPackage,
                 world: WorldPackageReader, site: SiteProfile, tool_version: str) -> None:
        self._fields = dict(fields)
        self._channels = [dict(channel) for channel in channels]
        self.scenario = scenario
        self.world = world
        self.site = site
        self.tool_version = tool_version

    # -- fields ----------------------------------------------------------------------------------------

    def value(self, path: str) -> Any:
        return self._fields[path].value

    def resolution(self, path: str) -> FieldResolution:
        return self._fields[path]

    @property
    def paths(self) -> list[str]:
        return list(self._fields)

    def unsupplied(self) -> list[str]:
        """Fields no layer gave a value, in schema order."""
        return [path for path, resolved in self._fields.items()
                if resolved.layer == LAYER_UNSUPPLIED]

    @property
    def channel_count(self) -> int:
        return len(self._channels)

    def channel_values(self, index: int) -> dict[str, Any]:
        return {name: resolved.value for name, resolved in self._channels[index].items()}

    def channel_resolutions(self, index: int) -> dict[str, FieldResolution]:
        return dict(self._channels[index])

    def channel_description(self, index: int, unnamed: bool = False) -> ChannelDescription:
        """The channel as a `ChannelDescription`; under `unnamed`, without its `sensor_id`, for a
        check that has refused the name on its own and wants every other problem of the channel.

        Raises:
            ValueError: when the description is refused, naming every problem.
        """
        values = self.channel_values(index)
        names = ChannelDescription.field_names()
        return ChannelDescription(**{name: None if unnamed and name == "sensor_id" else values[name]
                                     for name in names})

    def channel_sensor_label(self, index: int) -> str:
        """The channel's `sensor_id`, or `channel-<n>` where it has none (a single channel): what a
        channel is called until its camera is spawned. An unnamed channel's camera is then named by
        the server, `Camera_<n>`, and that is the name its directory and its stills carry."""
        sensor_id = self.channel_values(index).get("sensor_id")
        return str(sensor_id) if sensor_id else f"channel-{index}"

    # -- what the session is handed ----------------------------------------------------------------

    def illumination_object(self) -> dict:
        """The `illumination` object the session reads, with only the fields that hold a value."""
        document: dict = {"illumination_version": ILLUMINATION_VERSION}
        for name in _ILLUMINATION_FIELDS:
            value = self.value(f"solar.{name}")
            if value is not None:
                document[name] = value
        return document

    def epoch_object(self) -> dict | None:
        return self.value("scenario.epoch")

    @cached_property
    def window(self) -> ResolvedWindow:
        """`capture.window` resolved against the scenario's declared windows and its span.

        Raises:
            WindowResolutionError: naming why, and the windows the scenario declares.
        """
        text = self.value("capture.window")
        end_of_scenario = float(self.value("scenario.end_s"))
        declared = self.scenario.windows
        names = ", ".join(sorted(declared)) or "none"
        if not isinstance(text, str) or not text.strip():
            raise WindowResolutionError(f"capture.window has no value. Name one of the scenario's "
                                        f"declared windows ({names}), or give <begin_s>:<end_s>")
        explicit = _EXPLICIT_WINDOW.match(text)
        if explicit is None:
            if text not in declared:
                raise WindowResolutionError(f"window '{text}' is not declared by scenario "
                                            f"{self.scenario.describe()}; it declares {names}. Or "
                                            "give <begin_s>:<end_s>")
            window = declared[text]
            resolved = ResolvedWindow(text, float(window["begin_s"]), float(window["end_s"]),
                                      f"declared by the scenario as window {text}", window)
        else:
            begin = float(explicit.group(1))
            if explicit.group(2) is None:
                resolved = ResolvedWindow(None, begin, end_of_scenario,
                                          "the scenario's own end: the window declares none")
            else:
                resolved = ResolvedWindow(None, begin, float(explicit.group(2)),
                                          "given explicitly")
        if resolved.begin_s >= end_of_scenario:
            raise WindowResolutionError(f"window {text} begins at t={resolved.begin_s:g}, at or "
                                        f"after the scenario's end time {end_of_scenario:g}")
        if resolved.end_s <= resolved.begin_s:
            raise WindowResolutionError(f"window {text} ends at t={resolved.end_s:g}, not after it "
                                        f"begins at t={resolved.begin_s:g}")
        if resolved.end_s > end_of_scenario:
            raise WindowResolutionError(f"window {text} ends after the scenario's end time "
                                        f"{end_of_scenario:g}")
        return resolved

    @property
    def prewarm_s(self) -> float:
        """The prewarm the run renders: capture.prewarm_s, clipped to the window's begin."""
        return min(float(self.value("capture.prewarm_s")), self.window.begin_s)

    @property
    def first_rendered_s(self) -> float:
        """The simulated second the session fast-forwards SUMO to, and the first frame it renders."""
        return self.window.begin_s - self.prewarm_s

    @property
    def ticks_per_frame(self) -> int:
        """World ticks per capture on every channel, which is each camera's own frame period: one
        capture is a whole number of them (check 9)."""
        return max(1, round(1.0 / (float(self.value("capture.capture_hz"))
                                   * float(self.value("capture.world_delta_s")))))

    # -- the record -------------------------------------------------------------------------------------

    def to_document(self) -> dict:
        """Every field with its provenance, for the lock and the resolution report."""
        return {
            "effective_configuration_version": EFFECTIVE_CONFIGURATION_VERSION,
            "tool_version": self.tool_version,
            "schema_version": RunConfiguration.VERSION,
            "digest": self.digest,
            "fields": {path: resolved.to_dict() for path, resolved in self._fields.items()},
            "channels": [{name: resolved.to_dict() for name, resolved in channel.items()}
                         for channel in self._channels],
        }

    def to_run_configuration(self) -> dict:
        """The effective configuration as a run configuration document: read back, it reproduces
        the run. Machine facts are left out; the scenario is named by id."""
        document: dict = {}
        for path, resolved in self._fields.items():
            spec = RunConfiguration.FIELDS[path]
            if spec.supplied_by == SUPPLIED_BY_SITE or path in _NOT_REPLAYED:
                continue
            if resolved.layer in _REDERIVED_LAYERS:
                continue
            value = resolved.value
            if path == "scenario_package":
                value = self.scenario.scenario_id
            node = document
            parts = path.split(".")
            for part in parts[:-1]:
                node = node.setdefault(part, {})
            node[parts[-1]] = value
        document.setdefault("capture", {})["channels"] = [
            {name: resolved.value for name, resolved in channel.items()}
            for channel in self._channels]
        return document

    @cached_property
    def digest(self) -> str:
        canonical = json.dumps(self.to_run_configuration(), sort_keys=True, separators=(",", ":"),
                               ensure_ascii=False)
        return hashlib.sha256(canonical.encode("utf-8")).hexdigest()
