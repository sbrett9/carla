"""Every schema published in `CarlaControl/schemas/`, by file name, with what makes it.

Each schema is made by the code that writes or reads its file, in one of three ways:

* **generated here**, in Python (`GENERATED`, `all`):
  * the run configuration, a run's records (result, resolution report, lock, launch echo) and the
    site profile (`RUN_SCHEMAS`), which `carla-capture --write-schemas DIR` also writes;
  * the compiler's outputs (lock, resolution report, supervision plan, sweep index, checks list) and
    the inputs read beside a scenario: the epoch, a display convention and the areas of interest
    (`SCENARIO_SCHEMAS`), which `carla-compile-scenario --write-schemas DIR` also writes;
  * a world package's JSON entries (`WorldPackageSchemas`), a level package's manifest
    (`LevelPackageSchema`) and the vehicle catalogue's files (`VehicleCatalogueSchemas`)
    (`WORLD_SCHEMAS`);
  * the SUMO bridge's table and its summary, the gap sidecar and the legacy labels file
    (`TelemetrySchemas`, `TELEMETRY_SCHEMAS`);
* **generated in C#** by the CarlaNet writers of what a capture writes (`CAPTURE_SCHEMAS`): the truth
  sidecar, the PNG text chunks, the run manifest, the world truth track and its summary
  (`CarlaNet.CoSim.Schemas.CaptureSchemas`), read here from the CarlaNet this process loaded;
* **written by hand** (`HAND_WRITTEN`): the Cursor-on-Target XSDs and the vehicle types' XSD.

`carla-validate --write-schemas DIR` writes every generated one, both languages' (`write_every`). A
test holds every file in `CarlaControl/schemas/` to this list, and each generated one, byte for byte,
to what its generator makes now, so regenerate after changing a writer or a reader. CarlaNet's own test
(`CaptureSchemaPublicationTests`, `CARLANET_WRITE_CAPTURE_SCHEMAS=1`) holds and writes the capture
schemas from the C# side; the two agree because they run one generator.

The specification's and the sweep's schemas ship with the authoring skill instead
(`carla-compile-scenario --write-schema` and `--write-sweep-schema`).
"""
from __future__ import annotations

from collections.abc import Callable
from pathlib import Path

from carlacontrol.AreaOfInterestSource import AreaOfInterestSource
from carlacontrol.CotDisplayConvention import CotDisplayConvention
from carlacontrol.LevelPackageSchema import LEVEL_MANIFEST_SCHEMA, LevelPackageSchema
from carlacontrol.RunConfiguration import RunConfiguration
from carlacontrol.RunRecordSchemas import RunRecordSchemas
from carlacontrol.ScenarioEpoch import ScenarioEpoch
from carlacontrol.ScenarioOutputSchemas import ScenarioOutputSchemas
from carlacontrol.SchemaPublication import SchemaPublication
from carlacontrol.SiteProfile import SiteProfile
from carlacontrol.TelemetrySchemas import TelemetrySchemas
from carlacontrol.VehicleCatalogueSchemas import VehicleCatalogueSchemas
from carlacontrol.WorldPackageSchemas import WorldPackageSchemas

Generator = Callable[[], dict]


def _each(schemas: Callable[[], dict[str, dict]]) -> dict[str, Generator]:
    """One generator per schema a module's `schemas()` publishes, by file name."""
    return {name: (lambda name=name: schemas()[name]) for name in schemas()}


RUN_SCHEMAS: dict[str, Generator] = {
    "run_configuration.schema.json": RunConfiguration.schema,
    "run_result.schema.json": RunRecordSchemas.result,
    "run_resolution.schema.json": RunRecordSchemas.resolution,
    "run_lock.schema.json": RunRecordSchemas.lock,
    "launch_echo.schema.json": RunRecordSchemas.launch_echo,
    "site_profile.schema.json": SiteProfile.schema,
}

SCENARIO_SCHEMAS: dict[str, Generator] = {
    "scenario_lock.schema.json": ScenarioOutputSchemas.lock,
    "scenario_resolution.schema.json": ScenarioOutputSchemas.resolution,
    "supervision_plan.schema.json": ScenarioOutputSchemas.supervision_plan,
    "sweep_index.schema.json": ScenarioOutputSchemas.sweep_index,
    "scenario_checks.schema.json": ScenarioOutputSchemas.checks,
    "epoch.schema.json": ScenarioEpoch.schema,
    "display_convention.schema.json": CotDisplayConvention.schema,
    "area_of_interest.schema.json": AreaOfInterestSource.schema,
}

WORLD_SCHEMAS: dict[str, Generator] = {
    **_each(WorldPackageSchemas.schemas),
    LEVEL_MANIFEST_SCHEMA: LevelPackageSchema.schema,
    **_each(VehicleCatalogueSchemas.schemas),
}

TELEMETRY_SCHEMAS: dict[str, Generator] = _each(TelemetrySchemas.schemas)

# Every schema generated in Python, by the group it is generated in.
GENERATED: dict[str, dict[str, Generator]] = {
    "run records": RUN_SCHEMAS,
    "compiler outputs and scenario inputs": SCENARIO_SCHEMAS,
    "world and level packages, and the vehicle catalogue": WORLD_SCHEMAS,
    "telemetry": TELEMETRY_SCHEMAS,
}

# The schemas CarlaNet's writers generate (`CarlaNet.CoSim.Schemas.CaptureSchemas.Published`).
CAPTURE_SCHEMAS: tuple[str, ...] = (
    "truth_sidecar.xsd",
    "png_chunk_capture.schema.json",
    "png_chunk_solar.schema.json",
    "png_chunk_illumination.schema.json",
    "png_chunk_sensor.schema.json",
    "run_manifest.schema.json",
    "world_truth_track.tableschema.json",
    "world_truth_track_summary.schema.json",
)

# The schemas written by hand, with no generator: the Cursor-on-Target event's parts, the UDP
# datagram's and the bridge's event file's, and the vehicle types' route file.
HAND_WRITTEN: tuple[str, ...] = (
    "cot_event_body.xsd",
    "cot_telemetry.xsd",
    "sumo_cot_events.xsd",
    "vehicle_types.xsd",
)


class PublishedSchemas:
    """Names every schema the package publishes, and writes the generated ones into a directory."""

    @staticmethod
    def all() -> dict[str, Generator]:
        """Every schema generated in Python, by file name."""
        return {name: generate for group in GENERATED.values() for name, generate in group.items()}

    @staticmethod
    def files() -> tuple[str, ...]:
        """Every file `CarlaControl/schemas/` holds: generated in Python or C#, or written by hand."""
        return tuple(sorted({*PublishedSchemas.all(), *CAPTURE_SCHEMAS, *HAND_WRITTEN}))

    @staticmethod
    def capture_texts() -> dict[str, str]:
        """The capture schemas as the CarlaNet this process loaded generates them, by file name.

        Raises:
            RuntimeError: the loaded CarlaNet generates no capture schemas: it was built before them.
        """
        import carlanet  # noqa: PLC0415 -- loads the CarlaNet assemblies the next import names
        try:
            from CarlaNet.CoSim.Schemas import CaptureSchemas  # noqa: PLC0415
        except ImportError:
            raise RuntimeError(
                f"the CarlaNet this process loaded ({getattr(carlanet, '__version__', 'unknown')}) "
                "generates no capture schemas: it was built before they were. Build CarlaNet from "
                "this checkout, or name a publish of it in CARLANET_PUBLISH_DIR.") from None
        return {str(schema.FileName): str(schema.Text).replace("\r\n", "\n")
                for schema in CaptureSchemas.Published()}

    @staticmethod
    def write(directory: str | Path, schemas: dict[str, Generator]) -> list[Path]:
        """Write each schema of `schemas` into `directory`, byte-stable; return the paths written."""
        directory = Path(directory)
        return [SchemaPublication.write(directory / name, generate())
                for name, generate in schemas.items()]

    @classmethod
    def write_every(cls, directory: str | Path) -> list[Path]:
        """Write every generated schema into `directory` -- Python's, and the capture schemas from the
        loaded CarlaNet -- and return the paths written. The hand-written ones are left alone."""
        directory = Path(directory)
        written = cls.write(directory, cls.all())
        for name, text in cls.capture_texts().items():
            path = directory / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8", newline="\n")
            written.append(path)
        return written
