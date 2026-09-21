"""A world and the SUMO that will read it are compared by release, not by printed string.

The two sides write the version differently. A world package records what the tool printed --
`Eclipse SUMO netconvert 1.27.0` -- because that is what was captured at the moment of conversion,
while `SumoInstallation` carries the release number alone. Comparing them verbatim refuses a
correctly matched pair, which blocks every scenario build against a world that was in fact built by
the installation in hand.

That is a worse failure than the one the check exists for: a false refusal stops work that should
proceed, and the operator's only escape is the flag that disables the check entirely.
"""
from __future__ import annotations

import os
import sys

import pytest

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402


def _installation(version: str | None) -> SumoInstallation:
    """An installation reporting a given release, without one having to exist on disk."""
    class _Fixed(SumoInstallation):
        @property
        def version(self):  # type: ignore[override]
            return version

    return _Fixed(home=os.path.join(_REPO, "Build", "sumo-install"), source="explicit")


@pytest.mark.parametrize("recorded", [
    "Eclipse SUMO netconvert 1.27.0",   # what a world package records
    "Eclipse SUMO sumo 1.27.0",         # the same release, printed by a different tool
    "1.27.0",                           # a bare release, as an older record may carry
    "v1.27.0",
])
def test_the_same_release_written_differently_is_accepted(recorded):
    _installation("1.27.0").require_version(recorded)


@pytest.mark.parametrize("recorded", [
    "Eclipse SUMO netconvert 1.27.1",
    "1.28.0",
])
def test_a_different_release_is_refused(recorded):
    with pytest.raises(RuntimeError) as raised:
        _installation("1.27.0").require_version(recorded)
    assert "1.27.0" in str(raised.value), "the message names the installation it resolved"


def test_a_different_release_proceeds_when_the_operator_accepts_the_risk():
    _installation("1.27.0").require_version("1.28.0", allow_mismatch=True)


def test_a_world_that_records_no_converter_warns_rather_than_refuses():
    """An artifact predating the recording is not evidence of a mismatch."""
    _installation("1.27.0").require_version(None)


def test_an_unreadable_installation_version_is_still_refused_against_a_recorded_one():
    with pytest.raises(RuntimeError):
        _installation(None).require_version("Eclipse SUMO netconvert 1.27.0")
