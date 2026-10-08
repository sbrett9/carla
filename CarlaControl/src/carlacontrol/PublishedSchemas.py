"""Every schema this package publishes in `CarlaControl/schemas/`, by file name, with its generator.

Two tools write them, each the schemas of the files it reads and writes:

* `carla-capture --write-schemas <dir>` -- the run configuration, a run's records (result,
  resolution report, lock, launch echo) and the site profile (`RUN_SCHEMAS`);
* `carla-compile-scenario --write-schemas <dir>` -- the compiler's outputs (lock, resolution report,
  supervision plan, sweep index, checks list) and the inputs read beside a scenario: the epoch, a
  display convention and the areas of interest (`SCENARIO_SCHEMAS`).

The specification's and the sweep's schemas ship with the authoring skill instead
(`carla-compile-scenario --write-schema` and `--write-sweep-schema`). A test holds every checked-in
file equal to its generator, so regenerate with these options after changing a writer or a reader.
"""
from __future__ import annotations

from collections.abc import Callable
from pathlib import Path

from carlacontrol.AreaOfInterestSource import AreaOfInterestSource
from carlacontrol.CotDisplayConvention import CotDisplayConvention
from carlacontrol.RunConfiguration import RunConfiguration
from carlacontrol.RunRecordSchemas import RunRecordSchemas
from carlacontrol.ScenarioEpoch import ScenarioEpoch
from carlacontrol.ScenarioOutputSchemas import ScenarioOutputSchemas
from carlacontrol.SchemaPublication import SchemaPublication
from carlacontrol.SiteProfile import SiteProfile

RUN_SCHEMAS: dict[str, Callable[[], dict]] = {
    "run_configuration.schema.json": RunConfiguration.schema,
    "run_result.schema.json": RunRecordSchemas.result,
    "run_resolution.schema.json": RunRecordSchemas.resolution,
    "run_lock.schema.json": RunRecordSchemas.lock,
    "launch_echo.schema.json": RunRecordSchemas.launch_echo,
    "site_profile.schema.json": SiteProfile.schema,
}

SCENARIO_SCHEMAS: dict[str, Callable[[], dict]] = {
    "scenario_lock.schema.json": ScenarioOutputSchemas.lock,
    "scenario_resolution.schema.json": ScenarioOutputSchemas.resolution,
    "supervision_plan.schema.json": ScenarioOutputSchemas.supervision_plan,
    "sweep_index.schema.json": ScenarioOutputSchemas.sweep_index,
    "scenario_checks.schema.json": ScenarioOutputSchemas.checks,
    "epoch.schema.json": ScenarioEpoch.schema,
    "display_convention.schema.json": CotDisplayConvention.schema,
    "area_of_interest.schema.json": AreaOfInterestSource.schema,
}


class PublishedSchemas:
    """Writes a tool's schemas into a directory, and names every schema the package publishes."""

    @staticmethod
    def all() -> dict[str, Callable[[], dict]]:
        """Every schema in `CarlaControl/schemas/`, by file name."""
        return {**RUN_SCHEMAS, **SCENARIO_SCHEMAS}

    @staticmethod
    def write(directory: str | Path, schemas: dict[str, Callable[[], dict]]) -> list[Path]:
        """Write each schema of `schemas` into `directory`, byte-stable; return the paths written."""
        directory = Path(directory)
        return [SchemaPublication.write(directory / name, generate())
                for name, generate in schemas.items()]
