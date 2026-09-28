"""Civil time as an authoring construct: an author writes 07:00 and the compiler emits 25200.

Every time a specification takes -- a departure, a flow window, a stop, a capture window, an
interval -- may be written in any of these forms, and resolves to simulated seconds under the epoch
(`07_Scenario_Authoring.md` §3.5.1, §4.5):

| Form | Example | Resolves to |
|---|---|---|
| Plain seconds | `25200` | itself |
| Day plus civil clock | `"d0 07:00"`, `"d6 02:30:15"` | that clock on the epoch's date plus that many days |
| Civil clock alone | `"07:00"` | day 0; refused when the run spans more than one day |
| Absolute civil instant | `"2026-03-21T07:00:00+03:30"` | its distance from the epoch; refused at another offset |
| Named instant | `{"instant": "night_shift"}` | what `instants` declares it to be |
| Offset from either | `{"instant": "night_shift", "plus": "15m"}`, `{"at": "d2 11:00", "plus": 274}` | that plus a duration |

A duration is plain seconds or `[Nd][Nh][Nm][Ns]`: `"8h"`, `"30m"`, `"1h30m"`, `"7d"`.

**The day is the epoch's civil date plus N**, so `"d0 07:00"` on an epoch at 06:00 is 3 600 s, and on
one at midnight 25 200 s. One offset holds for the whole run -- daylight saving is carried by the
declared offset (`04_Contracts.md` §11.3) -- so simulated seconds map linearly onto civil time.

**The session's function is the authority.** Every day-clock and absolute instant is resolved here and
then handed back to `SolarEpoch.CivilInstantAt` through `ScenarioEpoch.civil_instant_at`; if the civil
instant that comes back is not the one written, that is a compiler defect and the compile stops. So
the second a report prints beside `07:00` is, by construction, the second the session will call 07:00.

**A clock-shaped literal that is really an offset is refused.** SUMO resolves `--begin 7:00:00` to step
25 200 with no epoch (`07_Scenario_Authoring.md` §1.4), so a bare clock is accepted only when there is
one day it can mean.
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.ScenarioEpoch import ScenarioEpoch

FORM_CHECK = 47
SPAN_CHECK = 37

SECONDS_PER_DAY = 86_400

_CLOCK = r"(?P<h>\d{1,2}):(?P<m>\d{2})(?::(?P<s>\d{2}(?:\.\d{1,6})?))?"
_DAY_CLOCK = re.compile(rf"^d(?P<day>\d+)\s+{_CLOCK}$")
_CLOCK_ALONE = re.compile(rf"^{_CLOCK}$")
_ABSOLUTE = re.compile(
    r"^(?P<y>\d{4})-(?P<mo>\d{2})-(?P<d>\d{2})T(?P<h>\d{2}):(?P<m>\d{2}):(?P<s>\d{2}(?:\.\d{1,6})?)"
    r"(?P<z>Z|[+-]\d{2}:\d{2})$")
_DURATION = re.compile(r"^(?:(?P<d>\d+(?:\.\d+)?)d)?(?:(?P<h>\d+(?:\.\d+)?)h)?"
                       r"(?:(?P<m>\d+(?:\.\d+)?)m)?(?:(?P<s>\d+(?:\.\d+)?)s)?$")

# How closely the session's CivilInstantAt must reproduce an instant resolved here. It works in
# 100 ns ticks and this in microseconds.
_ROUND_TRIP_TOLERANCE = timedelta(microseconds=1)


@dataclass(frozen=True)
class ResolvedInstant:
    """An authored time and what it became: the second, and the civil instant the session gives it."""

    seconds: float
    civil: str
    authored: object
    form: str

    def to_dict(self) -> dict:
        return {"authored": self.authored, "form": self.form, "seconds": self.seconds,
                "civil": self.civil}


class CivilTimeResolver:
    """Resolves authored times to simulated seconds under one epoch, recording what refuses."""

    def __init__(self, epoch: ScenarioEpoch, findings: CompileFindings,
                 instants: dict[str, object] | None = None, span_end_s: float | None = None) -> None:
        self.epoch = epoch
        self.findings = findings
        self.span_end_s = span_end_s
        self._declared = dict(instants or {})
        self._named: dict[str, ResolvedInstant | None] = {}
        self._resolving: list[str] = []

    # -- instants ---------------------------------------------------------------------------------

    def instant(self, value: object, where: str) -> ResolvedInstant | None:
        """Resolve a time, or record why it refuses under check 47 and return None.

        No range check is made here: a departure outside the run refuses (check 37) while a flow
        ending after it only warns (check 32), so the caller decides which rule a time answers to.
        A negative second is the one exception, refused under check 37 wherever it appears.
        """
        resolved = self._instant(value, where)
        if resolved is not None and resolved.seconds < 0:
            self.findings.refuse(
                SPAN_CHECK, where,
                f"{self._show(value)} is {resolved.civil}, {-resolved.seconds:g} s before t = 0. "
                f"t = 0 is {self.epoch.civil_instant_at(0)}: the epoch is the start of the "
                "simulation, not the start of what is interesting")
            return None
        return resolved

    def require_in_span(self, resolved: ResolvedInstant | None, where: str,
                        check: int = SPAN_CHECK) -> bool:
        """Whether a resolved instant lies in [0, end]; refuses under `check` when it does not."""
        if resolved is None:
            return False
        if self.span_end_s is not None and resolved.seconds > self.span_end_s:
            self.findings.refuse(
                check, where,
                f"{self._show(resolved.authored)} is {resolved.civil} ({resolved.seconds:g} s), after "
                f"the run ends at {self.epoch.civil_instant_at(self.span_end_s)} "
                f"({self.span_end_s:g} s)")
            return False
        return True

    def _instant(self, value: object, where: str) -> ResolvedInstant | None:
        if isinstance(value, bool):
            return self._refuse_form(where, value, "a time is a number of seconds or a civil time, "
                                                   "not true or false")
        if isinstance(value, (int, float)):
            seconds = float(value)
            return ResolvedInstant(seconds, self.epoch.civil_instant_at(seconds), value, "seconds")
        if isinstance(value, dict):
            return self._composite(value, where)
        if not isinstance(value, str):
            return self._refuse_form(where, value, "not a time in any accepted form")

        text = value.strip()
        match = _DAY_CLOCK.match(text)
        if match:
            clock = self._clock(match, where, value)
            if clock is None:
                return None
            return self._civil(self.epoch.civil_on_day(int(match["day"]), clock), value, "day_clock")

        match = _CLOCK_ALONE.match(text)
        if match:
            clock = self._clock(match, where, value)
            if clock is None:
                return None
            if self.span_end_s is not None and self.span_end_s > SECONDS_PER_DAY:
                return self._refuse_form(
                    where, value,
                    f"a clock alone names no day, and this run spans "
                    f"{self.span_end_s / SECONDS_PER_DAY:g} days; write the day, as "
                    f"'d0 {text}'. SUMO reads a bare clock as an offset from t = 0, which is that "
                    "clock only when t = 0 is midnight")
            return self._civil(self.epoch.civil_on_day(0, clock), value, "clock")

        match = _ABSOLUTE.match(text)
        if match:
            return self._absolute(match, where, value)

        return self._refuse_form(
            where, value,
            "not a time in any accepted form: seconds, 'dN HH:MM[:SS]', 'HH:MM[:SS]', an ISO-8601 "
            "instant with its offset, or {\"instant\": name} / {\"at\": time} with an optional "
            "\"plus\" duration")

    def _composite(self, value: dict, where: str) -> ResolvedInstant | None:
        keys = set(value)
        base_key = {"instant", "at"} & keys
        unknown = keys - {"instant", "at", "plus"}
        if len(base_key) != 1 or unknown:
            return self._refuse_form(where, value, "a composite time is {\"instant\": name} or "
                                                   "{\"at\": time}, with an optional \"plus\"")
        if "instant" in value:
            base = self._named_instant(value["instant"], where)
        else:
            base = self._instant(value["at"], where)
        if base is None:
            return None
        if "plus" not in value:
            return ResolvedInstant(base.seconds, base.civil, value, base.form)
        plus = self.duration(value["plus"], where)
        if plus is None:
            return None
        seconds = base.seconds + plus
        return ResolvedInstant(seconds, self.epoch.civil_instant_at(seconds), value,
                               f"{base.form}_plus")

    def _named_instant(self, name: object, where: str) -> ResolvedInstant | None:
        if not isinstance(name, str) or name not in self._declared:
            self.findings.refuse(8, where, f"names instant {name!r}, which instants does not declare"
                                 + (f"; it declares {', '.join(sorted(self._declared))}"
                                    if self._declared else ""))
            return None
        if name in self._named:
            return self._named[name]
        if name in self._resolving:
            cycle = " -> ".join([*self._resolving[self._resolving.index(name):], name])
            self.findings.refuse(FORM_CHECK, f"instant {name}", f"is defined in terms of itself: {cycle}")
            return None
        self._resolving.append(name)
        try:
            resolved = self.instant(self._declared[name], f"instant {name}")
        finally:
            self._resolving.pop()
        self._named[name] = resolved
        return resolved

    def resolve_declared_instants(self) -> dict[str, ResolvedInstant]:
        """Every entry of `instants`, resolved whether or not anything names it, for the report."""
        out = {}
        for name in self._declared:
            resolved = self._named_instant(name, f"instant {name}")
            if resolved is not None:
                out[name] = resolved
        return out

    def _absolute(self, match: re.Match, where: str, value: str) -> ResolvedInstant | None:
        zone = match["z"]
        if zone == "Z":
            offset = timedelta(0)
        else:
            sign = -1 if zone[0] == "-" else 1
            offset = sign * timedelta(hours=int(zone[1:3]), minutes=int(zone[4:6]))
        declared = timedelta(hours=self.epoch.utc_offset_hours)
        if offset != declared:
            return self._refuse_form(
                where, value,
                f"carries the offset {self._format_offset(offset)} and the epoch declares "
                f"{self._format_offset(declared)}. One offset holds for the whole run -- daylight "
                "saving is part of the declared offset -- so an instant at another offset means "
                "something the epoch cannot express")
        seconds_text = match["s"]
        whole = int(seconds_text[:2])
        micro = round(float("0" + seconds_text[2:]) * 1_000_000) if len(seconds_text) > 2 else 0
        try:
            instant = datetime(int(match["y"]), int(match["mo"]), int(match["d"]), int(match["h"]),
                               int(match["m"]), whole, micro, tzinfo=timezone(offset))
        except ValueError as problem:
            return self._refuse_form(where, value, f"is not a calendar instant: {problem}")
        return self._civil(instant, value, "absolute")

    def _civil(self, instant: datetime, authored: object, form: str) -> ResolvedInstant:
        seconds = self.epoch.seconds_at(instant)
        civil = self.epoch.civil_instant_at(seconds)
        back = datetime.fromisoformat(civil)
        if abs(back - instant) > _ROUND_TRIP_TOLERANCE:
            raise RuntimeError(
                f"compiler defect: {authored!r} resolved to {seconds!r} s, which SolarEpoch names "
                f"{civil} rather than {instant.isoformat()}")
        return ResolvedInstant(seconds, civil, authored, form)

    def _clock(self, match: re.Match, where: str, value: object) -> timedelta | None:
        hours, minutes = int(match["h"]), int(match["m"])
        seconds = float(match["s"]) if match["s"] else 0.0
        at_midnight = hours == 24 and minutes == 0 and seconds == 0.0
        if not at_midnight and (hours > 23 or minutes > 59 or seconds >= 60.0):
            self._refuse_form(where, value, "is not a clock time: hours run 00-23 (24:00 alone "
                                            "names the following midnight), minutes and seconds "
                                            "00-59")
            return None
        return timedelta(hours=hours, minutes=minutes, seconds=seconds)

    # -- durations --------------------------------------------------------------------------------

    def duration(self, value: object, where: str) -> float | None:
        """A non-negative length of time in seconds, or record why it refuses and return None."""
        if isinstance(value, bool):
            self._refuse_form(where, value, "a duration is seconds or '[Nd][Nh][Nm][Ns]'")
            return None
        if isinstance(value, (int, float)):
            seconds = float(value)
        elif isinstance(value, str) and value.strip() and _DURATION.match(value.strip()):
            parts = _DURATION.match(value.strip()).groupdict()
            seconds = (float(parts["d"] or 0) * SECONDS_PER_DAY + float(parts["h"] or 0) * 3600
                       + float(parts["m"] or 0) * 60 + float(parts["s"] or 0))
        else:
            self._refuse_form(where, value, "is not a duration: seconds, or '[Nd][Nh][Nm][Ns]' "
                                            "such as '8h', '30m' or '1h30m'")
            return None
        if seconds < 0:
            self._refuse_form(where, value, "is a negative duration")
            return None
        return seconds

    # -- helpers ----------------------------------------------------------------------------------

    def _refuse_form(self, where: str, value: object, reason: str) -> None:
        self.findings.refuse(FORM_CHECK, where, f"{self._show(value)} {reason}")
        return None

    @staticmethod
    def _show(value: object) -> str:
        return json.dumps(value) if not isinstance(value, str) else repr(value)

    @staticmethod
    def _format_offset(offset: timedelta) -> str:
        minutes = round(offset.total_seconds() / 60)
        sign = "-" if minutes < 0 else "+"
        return f"{sign}{abs(minutes) // 60:02d}:{abs(minutes) % 60:02d}"
