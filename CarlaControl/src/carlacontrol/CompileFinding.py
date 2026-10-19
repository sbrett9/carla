"""One thing a scenario compile established: which check, what it concluded, and about what.

The compiler has one vocabulary for what a check concludes, and a resolution report, a lock file and
a corpus filter all read it (`07_Scenario_Authoring.md` §5.2). **Refuse** means the compile fails and
nothing is emitted. **Warn** means the compile goes on and the warning is carried, in full, into the
resolution report, because a warning is a failure a person has to adjudicate. There is no third
outcome: what a check measured without concluding anything is a fact in the report, not a finding.
"""
from __future__ import annotations

from dataclasses import dataclass

REFUSE = "refuse"
WARN = "warn"
OUTCOMES = (REFUSE, WARN)


@dataclass(frozen=True)
class CompileFinding:
    """A refusal or a warning, attributed to the check that produced it.

    `check_id` is a stable identifier from `ScenarioCheckCatalogue`, never a position: a report cites
    it and a corpus is filtered on it, so it has to mean the same thing a year later. `subject` says
    what the finding is about in the author's own names -- `epoch`, `place guard_base`, `actor
    probe_d2` -- so a reader can find it in the specification without reading the message.
    """

    check_id: int
    outcome: str
    subject: str
    message: str

    def __post_init__(self) -> None:
        if self.outcome not in OUTCOMES:
            raise ValueError(f"a finding is {' or '.join(OUTCOMES)}, not {self.outcome!r}")

    def to_dict(self) -> dict:
        return {"check": self.check_id, "outcome": self.outcome, "subject": self.subject,
                "message": self.message}

    def __str__(self) -> str:
        return f"check {self.check_id} {self.outcome.upper()} {self.subject}: {self.message}"
