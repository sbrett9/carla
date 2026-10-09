"""The run result: how a capture run ended, and pointers to everything it wrote.

`12_Operator_Control_Surface.md` §3.10.2-§3.10.3, D12.22, D12.23. Written **in every terminal outcome
the tool survives** -- a refusal that produced no session included -- to the path `--result` names, or
by default beside the run's resolution report, and always outside the capture root. The process exit
status is read from `outcome` (`exit_status`), never computed beside it: two computations of one fact
eventually disagree, and a caller believes the cheaper one.

Eight outcomes, each a name that stands alone, whose one live distinction is *finished* against
*stopped*:

| status | outcome | what it says |
|---|---|---|
| 0 | `run_finished` | the run reached the end it was given: the window's end or the scenario's |
| 1 | `usage_error` | the invocation could not be resolved: an unknown key, an unreadable package, an override of a binding |
| 2 | `refused_offline` | the offline checks refused; no server was contacted |
| 3 | `refused_server` | the server checks refused, or the session refused at its `Validation` or `Launch` stage |
| 4 | `refused_authority` | the population lease is held by another, named in `authority_holder` |
| 5 | `refused_preroll` | the lease was taken and given back before the window: the session's `PreRoll` stage -- the fast-forward, the sun's read-back, a prewarm tick -- or the cameras, the prewarm's pace, the traffic a stare aims at, the render cap at the window's begin |
| 6 | `run_stopped` | the run ended before that end; `closed_by` says by what |
| 7 | `internal_error` | an unhandled fault -- not a signal |

It is a **pointer set over artifacts that exist**, never a second description of the corpus, and it
carries **no aggregate verdict**: the gate records are observations -- what was measured, against
what, whether it met it -- and a caller computes its own verdict from the subset it cares about.
Its absence means only that the tool was stopped before it could write one.

The file is written to a temporary name beside its final one and renamed into place, so a reader
never sees a partial result under the real name.

It says what made it (`producer`, `carlacontrol.ProducerRecord`): the tool and its release, the
carlanet release, the server's build identity where the run reached a server, and the SUMO release
where a session started one; `tool_version` beside it is the same release, kept for readers written
before the record.
"""
from __future__ import annotations

import json
import os
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from carlacontrol.FormatVersion import FormatVersion
from carlacontrol.ProducerRecord import ProducerRecord
from carlacontrol.version import __version__

RESULT_VERSION = 1
# The format versions of the two records written beside the result: the run's resolution report
# (`run.resolution.json`) and its lock (`run.lock.json`).
RUN_RESOLUTION_VERSION = 1
RUN_LOCK_VERSION = 1
# The component whose run the result records.
TOOL = "carlacontrol.CaptureSession"
OUTCOMES = {
    "run_finished": 0,
    "usage_error": 1,
    "refused_offline": 2,
    "refused_server": 3,
    "refused_authority": 4,
    "refused_preroll": 5,
    "run_stopped": 6,
    "internal_error": 7,
}
RESULT_SUFFIX = ".result.json"


class RunResult:
    """One run's terminal record, assembled as the launch proceeds and written once at its end."""

    def __init__(self, session_id: str, caller: str = "attended",
                 caller_label: str | None = None) -> None:
        self.session_id = session_id
        self.caller = caller
        self.caller_label = caller_label
        self.outcome: str | None = None
        self.closed_by: str | None = None
        self.detail: str | None = None
        self.authority_holder: str | None = None
        self.findings: list[dict] = []
        self.adjudications: list[dict] = []
        self.expectations_declared = 0
        self.effective_configuration_digest: str | None = None
        self.schema_version: int | None = None
        self.artifacts: dict[str, str | None] = {"resolution_report": None, "lock": None,
                                                 "effective_configuration": None}
        # What the run reached, for the record of what made the result: the server's build identity
        # once it connected, and the SUMO release once a session started one.
        self.server: dict | None = None
        self.sumo: str | None = None
        self.launch_echo: dict | None = None
        self.produced: dict | None = None
        self.started_wall_utc = self.now()
        self.ended_wall_utc: str | None = None
        self.path: Path | None = None

    # -- the outcome ------------------------------------------------------------------------------------

    def conclude(self, outcome: str, closed_by: str | None = None,
                 detail: str | None = None) -> RunResult:
        """Name how the run ended. The first conclusion stands; a later one is a bug upstream."""
        if outcome not in OUTCOMES:
            raise ValueError(f"'{outcome}' is not a run outcome; they are {', '.join(OUTCOMES)}")
        if self.outcome is None:
            self.outcome = outcome
            self.closed_by = closed_by
            self.detail = detail
        return self

    @property
    def exit_status(self) -> int:
        """The process exit status, read from the outcome."""
        if self.outcome is None:
            raise ValueError("the run has no outcome yet")
        return OUTCOMES[self.outcome]

    @property
    def refusals(self) -> list[dict]:
        return [f for f in self.findings if f.get("outcome") == "refuse"]

    @property
    def warnings(self) -> list[dict]:
        """Every warning raised, with its adjudication and the artifact that granted it."""
        by_code = {a["code"]: a for a in self.adjudications}
        warnings = []
        for finding in self.findings:
            if finding.get("outcome") != "warn":
                continue
            adjudication = by_code.get(finding.get("code"), {})
            warnings.append({**finding, "adjudication": adjudication.get("adjudication"),
                             "adjudicated_by": adjudication.get("adjudicated_by")})
        return warnings

    # -- the record -------------------------------------------------------------------------------------

    def to_dict(self) -> dict:
        return {
            "result_version": RESULT_VERSION,
            "producer": self.producer(),
            "outcome": self.outcome,
            "exit_status": self.exit_status,
            "closed_by": self.closed_by,
            "detail": self.detail,
            "caller": self.caller,
            "caller_label": self.caller_label,
            "session_id": self.session_id,
            "tool_version": __version__,
            "schema_version": self.schema_version,
            "effective_configuration_digest": self.effective_configuration_digest,
            **self.artifacts,
            "launch_echo": self.launch_echo,
            "authority_holder": self.authority_holder,
            "refusals": self.refusals,
            "warnings": self.warnings,
            "expectations_declared": self.expectations_declared,
            "produced": self.produced,
            "started_wall_utc": self.started_wall_utc,
            "ended_wall_utc": self.ended_wall_utc,
        }

    def producer(self) -> dict:
        """What made the result, as every file this run writes records it."""
        return ProducerRecord.record(TOOL, server=self.server, sumo=self.sumo)

    def write(self, path: str | Path) -> Path:
        """Write the result atomically, beside nothing it could be mistaken for."""
        self.ended_wall_utc = self.now()
        self.path = Path(path)
        self.write_json(self.path, self.to_dict())
        return self.path

    @staticmethod
    def write_json(path: Path, document: Any) -> Path:
        """Write a whole-file artifact under a temporary name and rename it into place."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_name(path.name + ".partial")
        temporary.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n",
                             encoding="utf-8", newline="\n")
        os.replace(temporary, path)
        return path

    @staticmethod
    def read(path: str | Path) -> dict:
        """A written result, refused where it is of a `result_version` newer than this reader's.

        Raises:
            FormatVersionError: the result declares a version this reader does not know.
        """
        document = json.loads(Path(path).read_text(encoding="utf-8"))
        FormatVersion.check(path, "result_version", document.get("result_version"), RESULT_VERSION)
        return document

    @staticmethod
    def sibling(result_path: str | Path, kind: str) -> Path:
        """The artifact of `kind` written beside a result: `<stem>.<kind>.json`."""
        result_path = Path(result_path)
        name = result_path.name
        stem = name[:-len(RESULT_SUFFIX)] if name.endswith(RESULT_SUFFIX) else result_path.stem
        return result_path.with_name(f"{stem}.{kind}.json")

    @staticmethod
    def now() -> str:
        return datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S.%fZ")
