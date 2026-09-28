"""What one scenario compile produced: its findings, and the files it wrote."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from carlacontrol.CompileFindings import CompileFindings


@dataclass
class CompileResult:
    """The outcome of a compile.

    `refused` compiles write only their resolution report, marked refused, so the refusals can be
    read; no scenario file is written. `files` maps each written artifact's role -- `routes`,
    `config`, `network`, `supervision`, `resolution`, `resolution_md`, `lock` -- to its path.
    """

    findings: CompileFindings
    files: dict[str, Path] = field(default_factory=dict)
    report: dict = field(default_factory=dict)
    lock: dict = field(default_factory=dict)
    plan: dict = field(default_factory=dict)

    @property
    def refused(self) -> bool:
        return self.findings.refused
