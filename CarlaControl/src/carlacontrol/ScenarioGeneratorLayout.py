"""Where an example scenario's generator reads its inputs and writes what it makes.

The generators under `CarlaControl/scripts/` -- `make_sumo_scenario.py`, `make_arapahoe_scenario.py`,
`make_supervision_check_scenario.py` and `make_bahonar_scenario.py` -- each write one scenario's
specification and compile it. A generator runs in one of two places, and its defaults follow from
which:

* **In a source checkout**, under `CarlaControl/scripts/`. The specification and the compiled
  scenario go to `Import/`, flat, beside each other. The world package is
  `Build/world-packages/<MapName>.cwp`, the vehicle catalogue is
  `CarlaControl/catalogue/vehicles.catalogue.json` and SUMO is the staged build under
  `Build/sumo-install`.
* **Beside the scenario it writes**, in a distribution's `Scenarios/<Place>/` folder, where
  `MakeDistribution` copies it next to the scenario's specification and world package. The
  specification goes beside the script and the compiled scenario into `<folder>/<scenario id>/`,
  the layout the shipped scenarios have and their READMEs' compile command writes. The world package
  is `<MapName>.cwp` beside the script. The catalogue is the distribution's
  `catalogue/vehicles.catalogue.json`, two folders up. SUMO is the distribution's `tools/sumo` when it
  is there, else whatever the environment names, which the distribution's `carla-env` step sets.

Which place is decided from the script's own folder, never from the current folder: a checkout keeps
the package sources at `<root>/CarlaControl/src/carlacontrol`, two folders above the script, with the
catalogue beside them (`ToolLayout.find_checkout`); a distribution has nothing of the kind there. A
script copied anywhere else is treated as beside its scenario, and names what is missing by path.
"""
from __future__ import annotations

from pathlib import Path

from carlacontrol.ToolLayout import CATALOGUE, ToolLayout

#: The checkout folders, relative to the repository root.
CHECKOUT_IMPORT = "Import"
CHECKOUT_WORLD_PACKAGES = ("Build", "world-packages")
CHECKOUT_STAGED_SUMO = ("Build", "sumo-install")
CHECKOUT_CATALOGUE = ("CarlaControl", *CATALOGUE)
#: The distribution folders, relative to its root, which is two folders above `Scenarios/<Place>/`.
DISTRIBUTION_CATALOGUE = ("catalogue", CATALOGUE[-1])
DISTRIBUTION_SUMO = ("tools", "sumo")


class ScenarioGeneratorLayout:
    """The default paths of one generator: a checkout's, or those beside the scenario it writes."""

    def __init__(self, script: str | Path, map_name: str, scenario_id: str,
                 checkout: str | Path | None) -> None:
        """
        Args:
            script: the generator's own file.
            map_name: the world the scenario runs on; its package is `<map_name>.cwp`.
            scenario_id: the scenario the generator writes; its compiled folder beside the script.
            checkout: the repository root when the script runs from a checkout, else None.
        """
        self.script = Path(script).resolve()
        self.map_name = map_name
        self.scenario_id = scenario_id
        self.checkout = Path(checkout).resolve() if checkout is not None else None

    @classmethod
    def of(cls, script: str | Path, map_name: str, scenario_id: str) -> ScenarioGeneratorLayout:
        """The layout of the generator at `script`, decided from the folders around it."""
        root = Path(script).resolve().parents[2]
        checkout = ToolLayout.find_checkout(root / "CarlaControl" / "src" / "carlacontrol")
        return cls(script, map_name, scenario_id, checkout)

    # -- which place, said in words -----------------------------------------------------------------

    @property
    def beside_scenario(self) -> bool:
        """True when the script sits beside the scenario it writes, as a distribution has it."""
        return self.checkout is None

    def describe(self) -> str:
        """Where the defaults come from, for a log line."""
        if self.checkout is not None:
            return f"the source checkout at {self.checkout}"
        return f"the scenario folder {self.script.parent} (the script sits beside its scenario)"

    # -- folders and files --------------------------------------------------------------------------

    @property
    def distribution_root(self) -> Path:
        """Two folders above the script: the distribution's folder when the script sits beside its
        scenario under `Scenarios/<Place>/`."""
        return self.script.parents[2]

    @property
    def out_dir(self) -> Path:
        """Where the specification goes: the checkout's `Import/`, or the script's own folder."""
        if self.checkout is not None:
            return self.checkout / CHECKOUT_IMPORT
        return self.script.parent

    def compile_dir(self, out_dir: str | Path) -> Path:
        """Where the compiled scenario goes for a specification written to `out_dir`: the same
        folder from a checkout, as `Import/` is laid out; `<out_dir>/<scenario id>/` beside a
        scenario, as the shipped scenarios are laid out."""
        if self.checkout is not None:
            return Path(out_dir)
        return Path(out_dir) / self.scenario_id

    @property
    def world_package(self) -> Path:
        """The world package: the checkout's build of it, or the one beside the script."""
        if self.checkout is not None:
            return self.checkout.joinpath(*CHECKOUT_WORLD_PACKAGES, f"{self.map_name}.cwp")
        return self.script.parent / f"{self.map_name}.cwp"

    @property
    def catalogue(self) -> Path:
        """The measured vehicle catalogue: the checkout's, or the distribution's `catalogue/`."""
        if self.checkout is not None:
            return self.checkout.joinpath(*CHECKOUT_CATALOGUE)
        return self.distribution_root.joinpath(*DISTRIBUTION_CATALOGUE)

    @property
    def sumo_home(self) -> Path | None:
        """The SUMO installation to route and dry-run with: the checkout's staged build or the
        distribution's `tools/sumo`, each only when it is there; else None, which leaves the choice
        to `SUMO_HOME` and `PATH` (`SumoInstallation.locate`)."""
        if self.checkout is not None:
            staged = self.checkout.joinpath(*CHECKOUT_STAGED_SUMO)
        else:
            staged = self.distribution_root.joinpath(*DISTRIBUTION_SUMO)
        return staged if staged.is_dir() else None

    # -- a run file's reference to the scenario ---------------------------------------------------

    @property
    def run_scenario_package(self) -> str:
        """How a run file written beside the scenario names it in `scenario_package`: the lock,
        relative to the repository root where a checkout's commands run; or the scenario id, which a
        capture resolves under `paths.scenario_root`, the folder the READMEs set to `Scenarios/<Place>`."""
        if self.checkout is not None:
            return f"{CHECKOUT_IMPORT}/{self.scenario_id}.lock.json"
        return self.scenario_id

    # -- for a help text ----------------------------------------------------------------------------

    @staticmethod
    def help_out_dir() -> str:
        return ("where the specification goes, with the compiled scenario beside it from a checkout "
                "or in <out-dir>/<scenario id>/ when this script sits beside its scenario "
                f"(default: {CHECKOUT_IMPORT}/ in a checkout, else this script's folder)")

    @staticmethod
    def help_sumo_home() -> str:
        return ("SUMO installation providing duarouter (default: the checkout's "
                f"{'/'.join(CHECKOUT_STAGED_SUMO)}, a distribution's {'/'.join(DISTRIBUTION_SUMO)}, "
                "else SUMO_HOME or PATH)")
