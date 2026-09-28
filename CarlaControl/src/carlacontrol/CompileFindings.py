"""Every refusal and warning one compile produced, collected rather than raised one at a time.

A compile reports every failure it can see, not the first: one epoch error invalidates every instant
in a file, and a scenario with twelve broken routes needs twelve fixes, so stopping at the first would
turn one compile into a dozen (`07_Scenario_Authoring.md` §4.5, §5.5). Stages that depend on an
earlier one's output stop when it refused; independent checks inside a stage all run.
"""
from __future__ import annotations

from carlacontrol.CompileFinding import REFUSE, WARN, CompileFinding
from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue


class CompileFindings:
    """The findings of one compile, in the order they were made."""

    def __init__(self) -> None:
        self.findings: list[CompileFinding] = []

    def refuse(self, check_id: int, subject: str, message: str) -> None:
        self._add(check_id, REFUSE, subject, message)

    def warn(self, check_id: int, subject: str, message: str) -> None:
        self._add(check_id, WARN, subject, message)

    @property
    def refused(self) -> bool:
        return any(finding.outcome == REFUSE for finding in self.findings)

    @property
    def refusals(self) -> list[CompileFinding]:
        return [finding for finding in self.findings if finding.outcome == REFUSE]

    @property
    def warnings(self) -> list[CompileFinding]:
        return [finding for finding in self.findings if finding.outcome == WARN]

    def by_check(self, check_id: int) -> list[CompileFinding]:
        return [finding for finding in self.findings if finding.check_id == check_id]

    def to_list(self) -> list[dict]:
        return [finding.to_dict() for finding in self.findings]

    def _add(self, check_id: int, outcome: str, subject: str, message: str) -> None:
        check = ScenarioCheckCatalogue.get(check_id)
        if outcome not in check.outcomes:
            raise ValueError(f"check {check_id} ({check.title}) cannot {outcome}; it declares "
                             f"{', '.join(check.outcomes) or 'no outcome'}. That is a compiler bug")
        finding = CompileFinding(check_id, outcome, subject, message)
        if finding not in self.findings:
            self.findings.append(finding)
