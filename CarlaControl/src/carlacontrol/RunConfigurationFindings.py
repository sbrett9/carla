"""Every refusal and warning one launch produced, in the scenario compiler's vocabulary.

A finding is a `CompileFinding` -- the same record, with the same two outcomes, that the scenario
compiler writes (`07_Scenario_Authoring.md` §5.2): **refuse** means nothing is emitted and no session
starts; **warn** means the launch goes on and the warning is carried in full. What differs is the
catalogue the check numbers belong to, which is `RunConfigurationCheckCatalogue`'s, and a warning's
code, which is what an `on_warning.<code>` adjudication names.

Every failure a stage can see is collected rather than raised at the first, so an operator fixes a
configuration once rather than once per field.
"""
from __future__ import annotations

from carlacontrol.CompileFinding import REFUSE, WARN, CompileFinding
from carlacontrol.RunConfigurationCheckCatalogue import (
    CATALOGUE_NAME,
    RunConfigurationCheckCatalogue,
)


class RunConfigurationFindings:
    """The findings of one launch, in the order they were made."""

    def __init__(self) -> None:
        self.findings: list[CompileFinding] = []

    def refuse(self, check_id: int, subject: str, message: str) -> None:
        self._add(check_id, REFUSE, subject, message)

    def warn(self, check_id: int, subject: str, message: str) -> None:
        self._add(check_id, WARN, subject, message)

    def extend(self, other: RunConfigurationFindings) -> None:
        for finding in other.findings:
            if finding not in self.findings:
                self.findings.append(finding)

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

    @staticmethod
    def warning_code(finding: CompileFinding) -> str | None:
        """The code an adjudication names this warning by; None for a refusal."""
        if finding.outcome != WARN:
            return None
        return RunConfigurationCheckCatalogue.get(finding.check_id).warning_code

    @classmethod
    def finding_dict(cls, finding: CompileFinding) -> dict:
        """The compiler's form of a finding, with the catalogue it cites and a warning's code."""
        entry = {**finding.to_dict(), "catalogue": CATALOGUE_NAME}
        code = cls.warning_code(finding)
        if code is not None:
            entry["code"] = code
        return entry

    def to_list(self) -> list[dict]:
        return [self.finding_dict(finding) for finding in self.findings]

    def _add(self, check_id: int, outcome: str, subject: str, message: str) -> None:
        check = RunConfigurationCheckCatalogue.get(check_id)
        if outcome not in check.outcomes:
            raise ValueError(f"run check {check_id} ({check.title}) cannot {outcome}; it declares "
                             f"{', '.join(check.outcomes) or 'no outcome'}. That is a bug")
        finding = CompileFinding(check_id, outcome, subject, message)
        if finding not in self.findings:
            self.findings.append(finding)


class RunConfigurationRefusedError(Exception):
    """A launch stopped by refusals: the findings, and the outcome the refusal ends the run with."""

    def __init__(self, findings: RunConfigurationFindings, outcome: str) -> None:
        self.findings = findings
        self.outcome = outcome
        super().__init__("; ".join(str(finding) for finding in findings.refusals))
