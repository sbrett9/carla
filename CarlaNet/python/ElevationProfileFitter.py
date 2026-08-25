"""Vertical-profile fitting for OpenDRIVE elevation records.

An OpenDRIVE ``<elevation>`` record is a cubic in the distance from its own start
station, ``z(ds) = a + b·ds + c·ds² + d·ds³`` with ``ds = s − record_s``. That is the
form CARLA evaluates in ``Road::GetDirectedPointIn``, and both the road mesh and the
waypoint z read it through that one function.

This module fits a road's sampled ``(s, z)`` series into such records under the schemes
``CarlaNet.Map.OpenDrive.ElevationInjector`` supports today and the scheme it is being
extended to support, so the two can be compared offline before any production change.
See ``Docs/CAT_Research/Findings/21_Road_Elevation_Profile_Continuity.md``.
"""

from __future__ import annotations

import logging
import math
from typing import NamedTuple

import numpy as np

logger = logging.getLogger(__name__)


class ElevationRecord(NamedTuple):
    """One OpenDRIVE ``<elevation>`` record, keyed at its own start station."""

    s: float
    a: float
    b: float
    c: float
    d: float

    def evaluate(self, s: float) -> float:
        """Height at absolute station ``s``."""
        ds = s - self.s
        return self.a + self.b * ds + self.c * ds * ds + self.d * ds * ds * ds

    def tangent(self, s: float) -> float:
        """Slope (rise over run) at absolute station ``s``."""
        ds = s - self.s
        return self.b + 2.0 * self.c * ds + 3.0 * self.d * ds * ds


class ElevationProfileFitter:
    """Fits sampled road heights into OpenDRIVE elevation records.

    Stateless — every entry point is a static or class method. The fitted records are
    returned in station order and are exact at every sample the fit was given.
    """

    #: Spans shorter than this are merged into their predecessor before fitting. The
    #: generated maps end each road on a remainder span that can be millimetres long,
    #: and dividing a full step's height change by it makes the cubic coefficients
    #: explode.
    MIN_SPAN_METRES = 1.0

    #: Distance over which an end tangent is estimated. One sample step, so the tangent
    #: reflects the grade the road is actually carrying rather than the slope of a
    #: remainder span.
    END_GRADE_WINDOW_METRES = 10.0

    #: Polynomial order for the local-regression low-pass.
    SMOOTH_ORDER = 2

    # ── fitting schemes ──────────────────────────────────────────────────────

    @staticmethod
    def piecewise_linear(stations: list[float], heights: list[float]) -> list[ElevationRecord]:
        """Straight ramp between consecutive samples — what ``ElevationInjector`` emits today.

        Reproduces `ElevationFitMode.PiecewiseLinear` exactly, including the final
        record's ``b = 0``, so it serves as the control the candidate fits are measured
        against.
        """
        records: list[ElevationRecord] = []
        for i, (s, z) in enumerate(zip(stations, heights, strict=True)):
            b = 0.0
            if i + 1 < len(stations):
                ds = stations[i + 1] - s
                if ds > 1e-9:
                    b = (heights[i + 1] - z) / ds
            records.append(ElevationRecord(s, z, b, 0.0, 0.0))
        return records

    @classmethod
    def monotone_cubic(
        cls,
        stations: list[float],
        heights: list[float],
        min_span: float | None = None,
        end_grade_window: float | None = None,
    ) -> list[ElevationRecord]:
        """Monotone cubic Hermite (PCHIP) with Fritsch-Carlson tangent limiting.

        C1 continuous by construction: consecutive records agree in both height and
        slope at every station. The tangent limiting keeps the curve inside the
        bracketing sample values on monotone runs, which an unconstrained spline does
        not — on a noisy photogrammetric height series that would invent humps and dips
        that were never sampled.

        The final record carries a real tangent estimated over
        ``end_grade_window`` metres of approach rather than the zero the linear fit
        leaves, so a road arrives at its successor with the grade it was carrying.
        """
        min_span = cls.MIN_SPAN_METRES if min_span is None else min_span
        window = cls.END_GRADE_WINDOW_METRES if end_grade_window is None else end_grade_window

        s, z = cls._merge_short_spans(stations, heights, min_span)
        n = len(s)
        if n == 0:
            return []
        if n == 1:
            return [ElevationRecord(s[0], z[0], 0.0, 0.0, 0.0)]

        spans = [s[i + 1] - s[i] for i in range(n - 1)]
        secants = [(z[i + 1] - z[i]) / spans[i] for i in range(n - 1)]

        tangents = cls._initial_tangents(s, z, spans, secants, window)
        cls._limit_tangents(tangents, secants)

        records: list[ElevationRecord] = []
        for i in range(n - 1):
            h, delta = spans[i], secants[i]
            m0, m1 = tangents[i], tangents[i + 1]
            records.append(
                ElevationRecord(
                    s=s[i],
                    a=z[i],
                    b=m0,
                    c=(3.0 * delta - 2.0 * m0 - m1) / h,
                    d=(m0 + m1 - 2.0 * delta) / (h * h),
                )
            )
        # The station at road end governs the pitch reported there and the grade handed
        # to the successor road, so it extends the curve at its own tangent.
        records.append(ElevationRecord(s[-1], z[-1], tangents[-1], 0.0, 0.0))
        return records

    # ── low-pass ─────────────────────────────────────────────────────────────

    @classmethod
    def smooth(
        cls,
        stations: list[float],
        heights: list[float],
        window_points: int,
        order: int | None = None,
    ) -> list[float]:
        """Local polynomial (Savitzky-Golay) low-pass over one road's height series.

        ``window_points`` is the full window in samples and is treated as odd; 1 or less
        returns the series unchanged. The regression runs against the actual stations
        rather than sample index, so the ragged terminal span does not bias it.

        The window shrinks symmetrically towards each end of the road, so the first and
        last samples are returned untouched. Road ends are boundary conditions — a grade
        break there is usually real, and holding the endpoints fixed also keeps the
        heights that junction agreement is measured on.

        This cannot honour the ``Raised`` flags that mark deliberately grade-separated
        bridge decks: those live in ``ElevationInjector``'s in-memory sample list and are
        not recoverable from a generated .xodr. A production filter must anchor at them.
        """
        order = cls.SMOOTH_ORDER if order is None else order
        n = len(heights)
        if window_points <= 1 or n < 3:
            return list(heights)

        half = (window_points - 1) // 2
        s = np.asarray(stations, dtype=float)
        z = np.asarray(heights, dtype=float)
        out = list(heights)
        for i in range(n):
            k = min(half, i, n - 1 - i)
            if k == 0:
                continue
            lo, hi = i - k, i + k + 1
            local_order = min(order, hi - lo - 1)
            # Centre the abscissa on the point being smoothed: the fit is evaluated
            # there, so its value is simply the constant term.
            coeffs = np.polyfit(s[lo:hi] - s[i], z[lo:hi], local_order)
            out[i] = float(coeffs[-1])
        return out

    # ── evaluation ───────────────────────────────────────────────────────────

    @staticmethod
    def record_at(records: list[ElevationRecord], s: float) -> ElevationRecord | None:
        """The record governing station ``s`` — the last one starting at or before it."""
        if not records:
            return None
        chosen = records[0]
        for record in records:
            if record.s <= s + 1e-9:
                chosen = record
            else:
                break
        return chosen

    @classmethod
    def evaluate(cls, records: list[ElevationRecord], s: float) -> float | None:
        """Height at station ``s``, the way CARLA evaluates the profile."""
        record = cls.record_at(records, s)
        return None if record is None else record.evaluate(s)

    @classmethod
    def tangent(cls, records: list[ElevationRecord], s: float) -> float | None:
        """Slope at station ``s``, the way CARLA derives waypoint pitch."""
        record = cls.record_at(records, s)
        return None if record is None else record.tangent(s)

    # ── internals ────────────────────────────────────────────────────────────

    @staticmethod
    def _merge_short_spans(
        stations: list[float], heights: list[float], min_span: float
    ) -> tuple[list[float], list[float]]:
        """Drop interior stations that sit closer than ``min_span`` to the one before.

        The first and last stations are always kept: the last is the road end, whose
        height the successor road meets.
        """
        if len(stations) < 3 or min_span <= 0.0:
            return list(stations), list(heights)

        keep_s, keep_z = [stations[0]], [heights[0]]
        for i in range(1, len(stations) - 1):
            if stations[i] - keep_s[-1] >= min_span:
                keep_s.append(stations[i])
                keep_z.append(heights[i])
        # Absorb the penultimate station if the remainder span would be degenerate.
        if len(keep_s) > 1 and stations[-1] - keep_s[-1] < min_span:
            keep_s.pop()
            keep_z.pop()
        keep_s.append(stations[-1])
        keep_z.append(heights[-1])
        return keep_s, keep_z

    @classmethod
    def _initial_tangents(
        cls,
        stations: list[float],
        heights: list[float],
        spans: list[float],
        secants: list[float],
        end_grade_window: float,
    ) -> list[float]:
        """Weighted three-point tangents inside, grade over a window at both ends."""
        n = len(stations)
        tangents = [0.0] * n
        for i in range(1, n - 1):
            h_prev, h_next = spans[i - 1], spans[i]
            tangents[i] = (h_next * secants[i - 1] + h_prev * secants[i]) / (h_prev + h_next)
        tangents[0] = cls._end_grade(stations, heights, end_grade_window, at_start=True)
        tangents[-1] = cls._end_grade(stations, heights, end_grade_window, at_start=False)
        return tangents

    @staticmethod
    def _end_grade(
        stations: list[float], heights: list[float], window: float, at_start: bool
    ) -> float:
        """Least-squares grade over the samples within ``window`` metres of a road end.

        Taking the slope of the end span alone is what produces artificial grades: the
        last span is a remainder of the road length and can be millimetres long, so a
        step's worth of sampling noise across it reads as an arbitrarily steep road.
        """
        anchor = stations[0] if at_start else stations[-1]
        picked = [i for i, s in enumerate(stations) if abs(s - anchor) <= window]
        if len(picked) < 2:
            picked = [0, 1] if at_start else [len(stations) - 2, len(stations) - 1]
        s = np.array([stations[i] for i in picked], dtype=float)
        z = np.array([heights[i] for i in picked], dtype=float)
        if np.ptp(s) < 1e-9:
            return 0.0
        slope, _ = np.polyfit(s, z, 1)
        return float(slope)

    @staticmethod
    def _limit_tangents(tangents: list[float], secants: list[float]) -> None:
        """Fritsch-Carlson limiting, in place — the guarantee against overshoot.

        On each span the tangent pair is pulled back inside the circle of radius 3 in
        ``(α, β) = (m_i/Δ, m_{i+1}/Δ)``, which is the sufficient condition for the
        interpolant to stay monotone on that span. Flat spans and sign reversals against
        the local secant are flattened outright.
        """
        for i, delta in enumerate(secants):
            if abs(delta) < 1e-12:
                tangents[i] = 0.0
                tangents[i + 1] = 0.0
                continue
            for j in (i, i + 1):
                if tangents[j] * delta < 0.0:
                    tangents[j] = 0.0
            alpha, beta = tangents[i] / delta, tangents[i + 1] / delta
            magnitude = alpha * alpha + beta * beta
            if magnitude > 9.0:
                tau = 3.0 / math.sqrt(magnitude)
                tangents[i] = tau * alpha * delta
                tangents[i + 1] = tau * beta * delta
