"""The exposure a channel's camera is given, as a run states it and as the camera takes it.

`08_Collection_And_EPoL.md` D8.26-D8.27: exposure is a per-channel collection parameter, set in
numbers over the channel's post-process profile, which sets the rest of the picture. A channel states
five fields, each with its unit in its name, and every one is sent to the camera at spawn, so every
capture's exposure is known from the run:

* `exposure_method` -- `manual`, fixed by the ISO, shutter and aperture; or `histogram`, metered by
  the engine from each frame;
* `exposure_iso` -- the sensor's sensitivity, ISO;
* `exposure_shutter_s` -- the shutter, seconds, sent as the camera's `shutter_speed`, which is per
  second as UE's and upstream CARLA's is (0.003125 s is 320, 1/320 s);
* `exposure_fstop` -- the aperture, as an f-number;
* `exposure_compensation_ev` -- compensation, EV, added under either method.

Each default is the `Default` profile's (08 §2.9): manual, ISO 100, 1/320 s, f/4 and no compensation,
EV100 +12.32, so a run that sets none renders exactly as every capture did before the camera published
its exposure. The camera's attribute names are upstream CARLA 0.9's: `exposure_mode`, `iso`,
`shutter_speed`, `fstop` and `exposure_compensation`.

**What a value may be (check 16).** A value is refused only where the camera cannot take it as stated:
not a finite number, or outside the range UE's own camera settings accept -- an ISO of at least 1 (the
engine reads anything lower as 1), a shutter from 1/8000 s to 100 s (UE's 0.01 to 8000 per second), an
f-stop from 1 to 32, and a compensation from -15 to +15 EV. `histogram` is permitted and warns: the
exposure then follows what is in the picture, which suits a live exercise's operator picture and not
captures meant to be compared (08 D8.26).
"""
from __future__ import annotations

import math
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Any, ClassVar

MANUAL = "manual"
HISTOGRAM = "histogram"
METHODS = (MANUAL, HISTOGRAM)

# The ranges UE's camera settings accept (Engine/Classes/Engine/Scene.h: CameraISO, CameraShutterSpeed,
# DepthOfFieldFstop, AutoExposureBias), in the run's units.
ISO_MIN = 1.0
SHUTTER_MIN_S = 1.0 / 8000.0
SHUTTER_MAX_S = 100.0
FSTOP_MIN = 1.0
FSTOP_MAX = 32.0
COMPENSATION_MAX_EV = 15.0


@dataclass(frozen=True)
class ChannelExposure:
    """One channel's exposure, in the run's units; the defaults are the `Default` profile's."""

    method: str = MANUAL
    iso: float = 100.0
    shutter_s: float = 1.0 / 320.0
    fstop: float = 4.0
    compensation_ev: float = 0.0

    # The run's channel field for each of the fields above.
    FIELDS: ClassVar[dict[str, str]] = {"exposure_method": "method", "exposure_iso": "iso",
                                        "exposure_shutter_s": "shutter_s",
                                        "exposure_fstop": "fstop",
                                        "exposure_compensation_ev": "compensation_ev"}

    # The camera blueprint's name for each channel field: upstream CARLA's attribute names.
    ATTRIBUTES: ClassVar[dict[str, str]] = {"exposure_method": "exposure_mode",
                                            "exposure_iso": "iso",
                                            "exposure_shutter_s": "shutter_speed",
                                            "exposure_fstop": "fstop",
                                            "exposure_compensation_ev": "exposure_compensation"}

    @classmethod
    def default_of(cls, field_name: str) -> Any:
        """The tool default of a channel field, which is the `Default` profile's value."""
        return getattr(cls(), cls.FIELDS[field_name])

    @classmethod
    def from_channel(cls, values: Mapping[str, Any]) -> ChannelExposure:
        """The exposure a channel's resolved values state; a field it lacks takes its default."""
        given = {attribute: values[name] for name, attribute in cls.FIELDS.items() if name in values}
        return cls(**given)

    def problems(self) -> list[tuple[str, str]]:
        """Each value the camera cannot take as stated, as (field, why); empty where there is none."""
        found: list[tuple[str, str]] = []
        if self.method not in METHODS:
            found.append(("exposure_method", f"'{self.method}' is not a method; it is manual or "
                                             "histogram"))
        checks = (("exposure_iso", self.iso, ISO_MIN, math.inf,
                   f"an ISO of at least {ISO_MIN:g}; the engine reads anything lower as {ISO_MIN:g}"),
                  ("exposure_shutter_s", self.shutter_s, SHUTTER_MIN_S, SHUTTER_MAX_S,
                   f"a shutter from 1/8000 s ({SHUTTER_MIN_S:g}) to {SHUTTER_MAX_S:g} s, in seconds: "
                   "1/320 s is 0.003125, where the camera's shutter_speed of 320 is per second"),
                  ("exposure_fstop", self.fstop, FSTOP_MIN, FSTOP_MAX,
                   f"an f-stop from {FSTOP_MIN:g} to {FSTOP_MAX:g}"),
                  ("exposure_compensation_ev", self.compensation_ev, -COMPENSATION_MAX_EV,
                   COMPENSATION_MAX_EV, f"a compensation from -{COMPENSATION_MAX_EV:g} to "
                                        f"+{COMPENSATION_MAX_EV:g} EV"))
        for name, value, low, high, takes in checks:
            if not self._number(value) or not low <= float(value) <= high:
                found.append((name, f"{value!r} is not a value the camera takes as stated; it takes "
                                    f"{takes}, the range UE's own camera settings accept"))
        return found

    def blueprint_attributes(self) -> dict[str, str]:
        """The camera blueprint's attributes for this exposure, by upstream CARLA's names and units,
        every one of them, so the camera is given the whole exposure the run states."""
        return {"exposure_mode": self.method,
                "iso": repr(float(self.iso)),
                "shutter_speed": repr(1.0 / float(self.shutter_s)),
                "fstop": repr(float(self.fstop)),
                "exposure_compensation": repr(float(self.compensation_ev))}

    @property
    def ev100(self) -> float | None:
        """The EV100 the ISO, shutter and aperture give a manual exposure, log2(N^2/t) - log2(ISO/100),
        the compensation apart, as the engine computes it; None under histogram, which the engine
        meters from each frame."""
        if self.method != MANUAL:
            return None
        return math.log2(self.fstop ** 2 / self.shutter_s) - math.log2(max(ISO_MIN, self.iso) / 100.0)

    @staticmethod
    def _number(value: Any) -> bool:
        return (isinstance(value, (int, float)) and not isinstance(value, bool)
                and math.isfinite(float(value)))
