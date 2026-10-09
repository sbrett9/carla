"""Resolve a capture run's configuration from its six layers, recording where every value came from.

`12_Operator_Control_Surface.md` §3.5-§3.6. In strictly increasing precedence:

1. **tool defaults** -- `RunConfiguration.FIELDS`;
2. **the site profile** -- `SiteProfile`, this machine's facts;
3. **the world package** -- its origin, network fingerprint, OpenDRIVE digest, map name and
   converter. **Bound**: a higher layer may restate one only with the value the package gives it;
   any other value is refused naming both (check 3);
4. **the scenario package** -- read from its lock: the epoch, the SUMO step and seed, the end, the
   catalogue digest and the lock's own digest (all bound), the mode it implies, and the illumination
   default, which a higher layer may override and whose override is recorded against the value it
   replaced;
5. **the run configuration** -- a `RunConfiguration` document;
6. **operator overrides** -- `--set <path>=<value>` and the short aliases, in the order given.

Three rules make the layering safe (§3.5):

* a field no layer supplies and no default covers resolves as `unsupplied`, and check 2 refuses it;
* a field that depends on another has no value where that other makes it meaningless -- the pacing
  factors under `as_available` resolve as `not_applicable`, and under `advance` the sun's rate is
  pinned to 1.0 (`pinned_by_policy`, D12.8);
* an illumination field that belongs to a policy a higher layer replaced is dropped and recorded as
  dropped, rather than carried into a policy that would refuse it or silently honoured.

The scenario package and the world package are chosen before they are read, from layers 2, 5 and 6:
`scenario_package` names the compiled scenario, and `world_package`, unset, is the package the
scenario lock names under `paths.world_package_root`.

Refusals here end the launch before anything is resolved (outcome `usage_error`), except a missing
`scenario_package`, which is check 2's.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from carlacontrol.ChannelDescription import ChannelDescription
from carlacontrol.EffectiveRunConfiguration import EffectiveRunConfiguration, FieldResolution
from carlacontrol.RunConfiguration import (
    BOUND,
    LAYER_DERIVED,
    LAYER_NOT_APPLICABLE,
    LAYER_OPERATOR_OVERRIDE,
    LAYER_PINNED_BY_POLICY,
    LAYER_RUN_CONFIGURATION,
    LAYER_SCENARIO_PACKAGE,
    LAYER_SITE_PROFILE,
    LAYER_TOOL_DEFAULT,
    LAYER_UNSUPPLIED,
    LAYER_WORLD_PACKAGE,
    LAYERS,
    RunConfiguration,
)
from carlacontrol.RunConfigurationFindings import (
    RunConfigurationFindings,
    RunConfigurationRefusedError,
)
from carlacontrol.ScenarioPackage import ScenarioPackage
from carlacontrol.SiteProfile import SiteProfile
from carlacontrol.version import __version__
from carlacontrol.WorldPackageReader import WorldPackageReader

_RANK = {layer: rank for rank, layer in enumerate(LAYERS)}

# Which illumination policies take each policy-specific field (`IlluminationPolicy.FromJson`
# refuses a field belonging to another policy).
_POLICY_FIELDS = {
    "solar.rate_sun_s_per_sim_s": ("advance",),
    "solar.freeze_at_civil_time": ("freeze_at",),
    "solar.freeze_date_advances": ("freeze_at_window_start", "freeze_at"),
}
PINNED_RATE = 1.0

# Where each world binding is read from in world.json.
_WORLD_KEYS = {
    "world.map_name": "MapName",
    "world.network_fingerprint": "NetworkFingerprint",
    "world.opendrive_sha256": "OpenDriveSha256",
    "world.origin_latitude": "OriginLatitude",
    "world.origin_longitude": "OriginLongitude",
    "world.netconvert_version": "NetconvertVersion",
}


class RunConfigurationResolver:
    """Applies the six layers in order and records provenance per field."""

    def __init__(self, site: SiteProfile) -> None:
        self.site = site

    def resolve(self, document: RunConfiguration | None,
                overrides: list[tuple[str, Any, str]]) -> EffectiveRunConfiguration:
        """The effective configuration, or a refusal naming every problem found.

        `overrides` are `(path, value, source)`, where `source` is the command-line text the value
        came from; a later override of a path replaces an earlier one, and the earlier is recorded.

        Raises:
            RunConfigurationRefusedError: outcome `usage_error`, or `refused_offline` for a missing
                scenario package.
        """
        findings = RunConfigurationFindings()
        run_values = dict(document.values) if document is not None else {}
        run_source = f"run configuration {document.source}" if document is not None else ""
        override_values, override_history = self._overrides(overrides)

        scenario = self._scenario_package(run_values, override_values, findings)
        world = self._world_package(scenario, run_values, override_values, findings) \
            if scenario is not None else None
        if findings.refused or scenario is None or world is None:
            outcome = "refused_offline" if findings.by_check(2) else "usage_error"
            raise RunConfigurationRefusedError(findings, outcome)

        layers = {
            LAYER_TOOL_DEFAULT: self._tool_defaults(),
            LAYER_SITE_PROFILE: self._site_layer(),
            LAYER_WORLD_PACKAGE: self._world_layer(world),
            LAYER_SCENARIO_PACKAGE: self._scenario_layer(scenario),
            LAYER_RUN_CONFIGURATION: {p: (v, run_source) for p, v in run_values.items()},
            LAYER_OPERATOR_OVERRIDE: dict(override_values),
        }
        resolved: dict[str, FieldResolution] = {}
        for path, spec in RunConfiguration.FIELDS.items():
            if path == "capture.channels":
                continue
            resolved[path] = self._resolve_field(path, spec, layers, override_history, findings)
        resolved["world_package"] = self._world_package_resolution(resolved["world_package"],
                                                                   world, scenario)
        self._apply_policy_rules(resolved)
        self._apply_pacing_rules(resolved)
        channels = self._channels(layers, run_source, override_values, findings)
        resolved["capture.channels"] = FieldResolution(
            [dict(c) for c in ({k: r.value for k, r in ch.items()} for ch in channels)] or None,
            *self._channel_list_origin(layers))
        if findings.refused:
            raise RunConfigurationRefusedError(findings, "usage_error")
        return EffectiveRunConfiguration(resolved, channels, scenario, world, self.site,
                                         __version__)

    # -- choosing the packages ----------------------------------------------------------------------

    def _chosen(self, path: str, run_values: dict, override_values: dict) -> Any:
        if path in override_values:
            return override_values[path][0]
        if path in run_values:
            return run_values[path]
        site = self.site.get(path)
        return site.value if site is not None else None

    def _scenario_package(self, run_values: dict, override_values: dict,
                          findings: RunConfigurationFindings) -> ScenarioPackage | None:
        reference = self._chosen("scenario_package", run_values, override_values)
        if not reference:
            findings.refuse(2, "scenario_package", "no scenario package is named. Give --scenario "
                            "with a scenario id, a package directory or its .lock.json")
            return None
        root = self._chosen("paths.scenario_root", run_values, override_values)
        try:
            package = ScenarioPackage.locate(str(reference), root)
        except (OSError, ValueError) as failure:
            findings.refuse(49, "scenario_package", str(failure))
            return None
        for problem in package.file_problems():
            findings.refuse(49, f"scenario {package.describe()}", problem)
        return package

    def _world_package(self, scenario: ScenarioPackage, run_values: dict, override_values: dict,
                       findings: RunConfigurationFindings) -> WorldPackageReader | None:
        explicit = self._chosen("world_package", run_values, override_values)
        if explicit:
            path = Path(str(explicit))
        else:
            named = scenario.world.get("package")
            root = self._chosen("paths.world_package_root", run_values, override_values)
            if not named or not root:
                findings.refuse(49, "world_package", f"scenario {scenario.describe()} names no world "
                                "package, or no paths.world_package_root is set; name the .cwp "
                                "with --set world_package=<path>")
                return None
            path = Path(str(root)) / str(named)
        try:
            return WorldPackageReader(path)
        except (OSError, ValueError) as failure:
            findings.refuse(49, "world_package", f"{failure}. The scenario was compiled against "
                            f"{scenario.world.get('package')}")
            return None

    # -- the layers -----------------------------------------------------------------------------------

    @staticmethod
    def _tool_defaults() -> dict[str, tuple[Any, str]]:
        source = f"run_capture {__version__}, schema v{RunConfiguration.VERSION}"
        return {path: (spec.default, source) for path, spec in RunConfiguration.FIELDS.items()
                if spec.has_default}

    def _site_layer(self) -> dict[str, tuple[Any, str]]:
        return {path: (value.value, value.provenance) for path, value in self.site.values.items()}

    @staticmethod
    def _world_layer(world: WorldPackageReader) -> dict[str, tuple[Any, str]]:
        layer = {}
        for path, key in _WORLD_KEYS.items():
            if key in world.manifest:
                layer[path] = (world.manifest[key],
                               f"world package {world.path.name} :: world.json {key}")
        return layer

    @staticmethod
    def _scenario_layer(scenario: ScenarioPackage) -> dict[str, tuple[Any, str]]:
        where = f"scenario {scenario.describe()}"
        traffic = scenario.traffic
        layer: dict[str, tuple[Any, str]] = {
            "mode": ("sumo_driven_playback", f"{where} :: a compiled scenario package is driven "
                                             "by SUMO"),
            "scenario.scenario_id": (scenario.scenario_id, f"{where} :: scenario_id"),
            "scenario.lock_sha256": (scenario.lock_sha256, f"{where} :: the lock's digest"),
        }
        optional = {
            "scenario.epoch": (scenario.epoch, "epoch"),
            "scenario.epoch_block_sha256": (scenario.epoch_block_sha256, "epoch_block_sha256"),
            "scenario.sumo_step_s": (traffic.get("step_length_s"), "traffic.step_length_s"),
            "scenario.sumo_seed": (traffic.get("sumo_seed"), "traffic.sumo_seed"),
            "scenario.end_s": (traffic.get("end_s"), "traffic.end_s"),
            "scenario.catalogue_digest": (scenario.catalogue_digest, "catalogue.catalogue_digest"),
        }
        for path, (value, key) in optional.items():
            if value is not None:
                layer[path] = (value, f"{where} :: {key}")
        illumination = scenario.illumination or {}
        for name, value in illumination.items():
            path = f"solar.{name}"
            if path in RunConfiguration.FIELDS:
                layer[path] = (value, f"{where} :: illumination.{name}")
        return layer

    @staticmethod
    def _overrides(overrides: list[tuple[str, Any, str]]
                   ) -> tuple[dict[str, tuple[Any, str]], dict[str, list[dict]]]:
        latest: dict[str, tuple[Any, str]] = {}
        history: dict[str, list[dict]] = {}
        for path, value, source in overrides:
            if path in latest:
                earlier_value, earlier_source = latest[path]
                history.setdefault(path, []).append(
                    {"layer": LAYER_OPERATOR_OVERRIDE, "value": earlier_value,
                     "provenance": earlier_source})
            latest[path] = (value, source)
        return latest, history

    # -- one field ----------------------------------------------------------------------------------

    def _resolve_field(self, path: str, spec, layers: dict, override_history: dict,
                       findings: RunConfigurationFindings) -> FieldResolution:
        tool_default = spec.default
        supplied = [(layer, layers[layer][path]) for layer in LAYERS if path in layers[layer]]
        if spec.mutability == BOUND:
            return self._resolve_bound(path, spec, supplied, findings)
        if not supplied:
            return FieldResolution(None, LAYER_UNSUPPLIED, "no layer supplies it", tool_default)
        layer, (value, provenance) = supplied[-1]
        replaced = [{"layer": lower, "value": lower_value, "provenance": lower_source}
                    for lower, (lower_value, lower_source) in supplied[:-1]
                    if lower != LAYER_TOOL_DEFAULT]
        replaced += override_history.get(path, [])
        site = self.site.get(path) if layer == LAYER_SITE_PROFILE else None
        return FieldResolution(value, layer, provenance, tool_default, tuple(replaced),
                               environment_variable=site.environment_variable if site else None)

    @staticmethod
    def _resolve_bound(path: str, spec, supplied: list, findings: RunConfigurationFindings
                       ) -> FieldResolution:
        """A bound field takes its binding's value; a restatement must equal it (check 3)."""
        binding_layers = (LAYER_WORLD_PACKAGE, LAYER_SCENARIO_PACKAGE)
        binding = next(((layer, entry) for layer, entry in supplied if layer in binding_layers),
                       None)
        restated = [(layer, entry) for layer, entry in supplied
                    if layer in (LAYER_RUN_CONFIGURATION, LAYER_OPERATOR_OVERRIDE)]
        if binding is None:
            # A null restates the absence a replayed configuration records, and sets nothing.
            restated = [(layer, entry) for layer, entry in restated if entry[0] is not None]
            if restated:
                layer, (value, source) = restated[-1]
                if path == "solar.rate_sun_s_per_sim_s":
                    findings.refuse(3, path, f"{source} sets the sun's rate, which is not "
                                    "operator-settable: under advance it is pinned to one "
                                    "sun-second per simulated second, and no other "
                                    "policy takes one")
                else:
                    findings.refuse(3, path, f"'{path}' is bound by a package, which supplies "
                                    f"none here; {source} cannot set it")
            if spec.has_default:
                return FieldResolution(spec.default, LAYER_TOOL_DEFAULT,
                                       "no package supplies it", spec.default)
            return FieldResolution(None, LAYER_UNSUPPLIED, "no package supplies it", spec.default)
        layer, (value, provenance) = binding
        note = None
        for _other, (restated_value, source) in restated:
            if RunConfigurationResolver._same(restated_value, value):
                note = f"restated, equal, by {source}"
                continue
            findings.refuse(3, path, f"'{path}' is bound by {provenance} "
                            f"({json.dumps(value)}); {source} gives {json.dumps(restated_value)}. "
                            "A binding cannot be overridden: rebuild the world or recompile the "
                            "scenario to change it")
        return FieldResolution(value, layer, provenance, spec.default, note=note)

    @staticmethod
    def _same(a: Any, b: Any) -> bool:
        if isinstance(a, bool) or isinstance(b, bool):
            return a is b
        if isinstance(a, int | float) and isinstance(b, int | float):
            return abs(float(a) - float(b)) <= 1e-9 * max(1.0, abs(float(b)))
        return a == b

    def _world_package_resolution(self, resolved: FieldResolution, world: WorldPackageReader,
                                  scenario: ScenarioPackage) -> FieldResolution:
        if resolved.value:
            return FieldResolution(str(world.path), resolved.layer, resolved.provenance,
                                   resolved.tool_default, resolved.overridden)
        root = self.site.get("paths.world_package_root")
        return FieldResolution(str(world.path), LAYER_DERIVED,
                               f"paths.world_package_root ({root.provenance if root else 'unset'}) "
                               f"and the package scenario {scenario.describe()} names",
                               resolved.tool_default)

    # -- dependent fields ----------------------------------------------------------------------------

    @staticmethod
    def _apply_policy_rules(resolved: dict[str, FieldResolution]) -> None:
        policy = resolved["solar.policy"]
        policy_rank = _RANK.get(policy.layer, -1)
        for path, policies in _POLICY_FIELDS.items():
            field = resolved[path]
            if field.value is None or policy.value in policies:
                continue
            if _RANK.get(field.layer, -1) < policy_rank:
                resolved[path] = FieldResolution(
                    None, LAYER_NOT_APPLICABLE,
                    f"{policy.provenance} set policy '{policy.value}', which does not take it",
                    field.tool_default,
                    dropped=({"layer": field.layer, "value": field.value,
                              "provenance": field.provenance},))
        if policy.value == "advance" and resolved["solar.rate_sun_s_per_sim_s"].value is None:
            rate = resolved["solar.rate_sun_s_per_sim_s"]
            resolved["solar.rate_sun_s_per_sim_s"] = FieldResolution(
                PINNED_RATE, LAYER_PINNED_BY_POLICY,
                "advance pins the rate to one sun-second per simulated second",
                rate.tool_default, dropped=rate.dropped)

    @staticmethod
    def _apply_pacing_rules(resolved: dict[str, FieldResolution]) -> None:
        if resolved["pacing.mode"].value != "as_available":
            return
        for path in ("pacing.real_time_factor", "pacing.min_achieved_factor"):
            field = resolved[path]
            if field.layer in (LAYER_TOOL_DEFAULT, LAYER_UNSUPPLIED):
                resolved[path] = FieldResolution(
                    None, LAYER_NOT_APPLICABLE, "pacing.mode is as_available", field.tool_default)

    # -- channels -------------------------------------------------------------------------------------

    @staticmethod
    def _channel_list_origin(layers: dict) -> tuple[str, str]:
        for layer in reversed(LAYERS):
            if "capture.channels" in layers[layer]:
                return layer, layers[layer]["capture.channels"][1]
        return LAYER_UNSUPPLIED, "no layer supplies it"

    def _channels(self, layers: dict, run_source: str, override_values: dict,
                  findings: RunConfigurationFindings) -> list[dict[str, FieldResolution]]:
        layer, source = self._channel_list_origin(layers)
        items = layers[layer]["capture.channels"][0] if layer != LAYER_UNSUPPLIED else []
        items = [dict(item) for item in items]
        indexed: dict[tuple[int, str], tuple[Any, str]] = {}
        for path, (value, override_source) in override_values.items():
            if not path.startswith("capture.channels["):
                continue
            index_text, name = path[len("capture.channels["):].split("].", 1)
            index = int(index_text)
            if index >= len(items):
                findings.refuse(1, path, f"{override_source}: there is no channel {index}; the "
                                f"configuration declares {len(items)}")
                continue
            indexed[(index, name)] = (value, override_source)
        fields = RunConfiguration.channel_fields()
        channels: list[dict[str, FieldResolution]] = []
        for index, item in enumerate(items):
            channel: dict[str, FieldResolution] = {}
            for name, spec in fields.items():
                if (index, name) in indexed:
                    value, override_source = indexed[(index, name)]
                    lower = [{"layer": layer, "value": item[name], "provenance": source}] \
                        if name in item else []
                    channel[name] = FieldResolution(value, LAYER_OPERATOR_OVERRIDE,
                                                    override_source, spec.default, tuple(lower))
                elif name in item:
                    channel[name] = FieldResolution(item[name], layer,
                                                    f"{source} :: capture.channels[{index}]",
                                                    spec.default)
                else:
                    origin = "ChannelDescription" if name in ChannelDescription.field_names()                         else f"run_capture {__version__} channel schema"
                    channel[name] = FieldResolution(spec.default, LAYER_TOOL_DEFAULT, origin,
                                                    spec.default)
            channels.append(channel)
        return channels
