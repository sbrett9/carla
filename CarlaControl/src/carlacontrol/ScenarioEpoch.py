"""The scenario epoch on the authoring side, read by the reader the co-simulation session uses.

A scenario declares what simulated second zero means in civil time at the site: the `epoch` object of
`04_Contracts.md` §11.3 -- a civil instant with an explicit numeric offset, the same instant in UTC, the
offset again as signed hours, whether the calendar advances, whether the offset includes daylight
saving, and optionally the IANA zone name and a note. The session reads that object with
`CarlaNet.CoSim.SolarEpoch` and binds the sun from it.

**This class does not re-implement the rules.** It hands the object to the same `SolarEpoch` the
session uses, so the compiler and the session cannot disagree about whether an epoch is valid, what
its digest is, or what civil instant a simulated second is. A second copy of the rules in Python is how
a scenario comes to compile and then be refused at run start -- or, worse, to be accepted by both with
two different meanings. What is added here is only what the authoring side needs beyond the session:
the refusal's problems attributed to checks 33 and 34, so a report can cite them, and the inverse of
`CivilInstantAt` -- the simulated second a civil instant is -- which the time resolver checks against
`CivilInstantAt` itself.

**The offset is normative; the zone name is not.** `time_zone_id` is carried for a reader and never
resolved: `zoneinfo` resolves zero zones on this machine, and two hosts with different databases would
disagree about a corpus (`07_Scenario_Authoring.md` §2.8, §9.6).
"""
from __future__ import annotations

import json
import re
from datetime import datetime, timedelta, timezone

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import CoSimSessionRefusedException, SolarEpoch

PRESENT_CHECK = 33
OFFSET_CHECK = 34

# SolarEpoch refuses with "The scenario epoch is refused. (1) ...; (2) ... ." A problem's own text may
# contain "; ", so the split is on a separator followed by the next number in brackets.
_PROBLEM_MARKER = re.compile(r"(?:^|;\s)\((\d+)\)\s")
_REFUSAL_PREFIX = "The scenario epoch is refused. "

# A problem SolarEpoch states about the offset field opens with its name.
_OFFSET_FIELD = "utc_offset_hours"

_TICKS_PER_MICROSECOND = 10


class ScenarioEpochRefusedError(ValueError):
    """The epoch was refused; `problems` holds each problem with the check it belongs to."""

    def __init__(self, problems: list[tuple[int, str]]) -> None:
        self.problems = problems
        super().__init__("; ".join(f"check {check}: {text}" for check, text in problems))


class ScenarioEpoch:
    """A validated epoch: what simulated second zero is, and the civil instant of any second."""

    def __init__(self, solar_epoch: SolarEpoch) -> None:
        self.solar_epoch = solar_epoch
        self.civil_datetime = self._to_python(solar_epoch.CivilDateTime)

    @classmethod
    def read(cls, declaration: object) -> ScenarioEpoch:
        """Validate an epoch object with `SolarEpoch`, refusing it whole if any rule is broken.

        `declaration` is the specification's `epoch` value as parsed JSON. None -- no epoch at all --
        is refused under check 33: a default would be a silent assertion about what the scenario
        means (`07_Scenario_Authoring.md` §4.5).
        """
        if declaration is None:
            raise ScenarioEpochRefusedError([(PRESENT_CHECK, "the specification declares no epoch; every "
                                         "scenario declares what civil instant t = 0 is, with its "
                                         "UTC offset (04_Contracts.md §11.3)")])
        try:
            return cls(SolarEpoch.FromJson(json.dumps(declaration)))
        except CoSimSessionRefusedException as refusal:
            raise ScenarioEpochRefusedError(cls._attribute(str(refusal.Message))) from None

    @staticmethod
    def _attribute(message: str) -> list[tuple[int, str]]:
        """Split SolarEpoch's refusal into its problems, each under the check it belongs to.

        A problem about `utc_offset_hours` is check 34's; every other is check 33's. A message this
        cannot split is kept whole under check 33, so a change in SolarEpoch's wording can only make
        an attribution coarser, never lose a refusal.
        """
        body = message[len(_REFUSAL_PREFIX):] if message.startswith(_REFUSAL_PREFIX) else message
        body = body.rstrip(".")
        parts = _PROBLEM_MARKER.split(body)
        # re.split with one group yields ["", "1", text1, "2", text2, ...] when the body opens with
        # a marker; anything else was not a numbered list.
        if len(parts) < 3 or parts[0].strip():
            return [(PRESENT_CHECK, message)]
        problems = []
        for text in parts[2::2]:
            text = text.strip()
            check = OFFSET_CHECK if text.startswith(_OFFSET_FIELD) else PRESENT_CHECK
            problems.append((check, text))
        return problems

    # -- what the epoch declares ----------------------------------------------------------------

    @property
    def canonical_json(self) -> str:
        """The object as `json.dumps(epoch, sort_keys=True, indent=2)` writes it -- SolarEpoch's form."""
        return str(self.solar_epoch.CanonicalJson)

    @property
    def declaration(self) -> dict:
        """The object as declared, parsed back from its canonical form."""
        return json.loads(self.canonical_json)

    @property
    def digest(self) -> str:
        """SHA-256 of the canonical form: `epoch_block_sha256`, the name every record gives the epoch."""
        return str(self.solar_epoch.Digest)

    @property
    def utc_offset_hours(self) -> float:
        return float(self.solar_epoch.UtcOffsetHours)

    @property
    def calendar_advances(self) -> bool:
        return bool(self.solar_epoch.CalendarAdvances)

    @property
    def dst_in_effect(self) -> bool:
        return bool(self.solar_epoch.DstInEffect)

    @property
    def time_zone_id(self) -> str | None:
        zone = self.solar_epoch.TimeZoneId
        return None if zone is None else str(zone)

    @property
    def note(self) -> str | None:
        note = self.solar_epoch.Note
        return None if note is None else str(note)

    def describe(self) -> str:
        """One line a person can read without arithmetic: SolarEpoch's own statement of itself."""
        return str(self.solar_epoch.ToString())

    # -- simulated seconds and civil instants ------------------------------------------------------

    def civil_instant_at(self, seconds: float) -> str:
        """The civil instant of a simulated second, as `SolarEpoch.CivilInstantAt` gives it."""
        return str(SolarEpoch.FormatCivil(self.solar_epoch.CivilInstantAt(float(seconds))))

    def civil_date_at(self, seconds: float) -> str:
        """The civil calendar date of a simulated second, `YYYY-MM-DD`, from `CivilInstantAt`."""
        return self.civil_instant_at(seconds)[:10]

    def seconds_at(self, instant: datetime) -> float:
        """The simulated second a civil instant is: the inverse of `civil_instant_at`.

        Kept to the arithmetic of two aware datetimes; the time resolver confirms every result by
        asking `civil_instant_at` for it back, so the session's function stays the authority.
        """
        if instant.tzinfo is None:
            raise ValueError("a civil instant without an offset is not a civil instant")
        return (instant - self.civil_datetime).total_seconds()

    def civil_on_day(self, day: int, clock: timedelta) -> datetime:
        """Clock time `clock` on the civil date `day` days after the epoch's, at the epoch's offset."""
        midnight = datetime(self.civil_datetime.year, self.civil_datetime.month,
                            self.civil_datetime.day, tzinfo=self.civil_datetime.tzinfo)
        return midnight + timedelta(days=day) + clock

    @staticmethod
    def _to_python(value) -> datetime:
        """A .NET DateTimeOffset as an aware datetime, to the microsecond."""
        offset = timedelta(minutes=round(float(value.Offset.TotalMinutes)))
        microseconds = int(value.Ticks % 10_000_000) // _TICKS_PER_MICROSECOND
        return datetime(int(value.Year), int(value.Month), int(value.Day), int(value.Hour),
                        int(value.Minute), int(value.Second), microseconds,
                        tzinfo=timezone(offset))
