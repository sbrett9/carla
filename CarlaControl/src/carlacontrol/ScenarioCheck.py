"""One check the scenario compiler runs, as a record a reader can cite.

`07_Scenario_Authoring.md` §5.2 lists them; `ScenarioCheckCatalogue` holds the same list in code and
generates the `checks.json` the authoring skill ships, so the two cannot drift silently -- a test reads
the table in the plan and compares it with the catalogue.
"""
from __future__ import annotations

from dataclasses import dataclass

# Where a check is carried out. A check the compiler does not run says so, rather than letting its
# presence in the list read as a guarantee.
COMPILER = "compiler"
WORLD_BUILD = "world_build"
NOT_BUILT = "not_built"
RETIRED = "retired"
STATUSES = (COMPILER, WORLD_BUILD, NOT_BUILT, RETIRED)


@dataclass(frozen=True)
class ScenarioCheck:
    """A check: its stable id, the group it runs in, what it compares, and what it can conclude.

    `outcomes` is the subset of `refuse` and `warn` the check can produce; a check that refuses past a
    tolerance and warns inside it names both. `status` says where it is carried out, and a retired
    check keeps its id so no id is ever reused.
    """

    check_id: int
    group: str
    title: str
    against: str
    outcomes: tuple[str, ...]
    status: str
    prevents: str

    def __post_init__(self) -> None:
        if self.status not in STATUSES:
            raise ValueError(f"check {self.check_id}: status {self.status!r} is not one of {STATUSES}")

    def to_dict(self) -> dict:
        return {"id": self.check_id, "group": self.group, "title": self.title,
                "against": self.against, "outcomes": list(self.outcomes), "status": self.status,
                "prevents": self.prevents}
