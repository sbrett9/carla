"""A caller's extra netconvert options survive argument parsing with their values attached.

The option a scenario most often needs the world built with is `--remove-edges.by-type`, whose
value is a list of edge types. Its *option* begins with a dash, so a repeated single-token form
cannot carry it: the parser reads the second dash-prefixed token as another option rather than as
the first one's value. These tests pin the quoted form that does carry it, and the failure the
quoting exists to avoid.
"""
from __future__ import annotations

import os
import shlex
import sys

import pytest

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.CarlaControlArgumentParser import CarlaControlArgumentParser  # noqa: E402

_BASE = ["--osm", "Import/Arapahoe_I25.osm"]


def _parse(extra: list[str]):
    return CarlaControlArgumentParser(_REPO).parse_args(_BASE + extra)


def _tokens(args) -> list[str]:
    """What WorldBuilder hands netconvert, by the same rule it uses."""
    out: list[str] = []
    for occurrence in args.netconvert_arg or []:
        out.extend(shlex.split(str(occurrence)))
    return out


def test_an_option_and_its_value_quoted_together_reach_netconvert_as_two_tokens():
    args = _parse(["--netconvert-arg", "--remove-edges.by-type highway.footway"])
    assert _tokens(args) == ["--remove-edges.by-type", "highway.footway"]


def test_the_value_may_be_a_comma_separated_list():
    args = _parse(["--netconvert-arg", "--remove-edges.by-type highway.footway,highway.steps"])
    assert _tokens(args) == ["--remove-edges.by-type", "highway.footway,highway.steps"]


def test_occurrences_accumulate_in_order():
    args = _parse([
        "--netconvert-arg", "--remove-edges.by-type highway.footway",
        "--netconvert-arg", "--keep-edges.min-speed 5",
    ])
    assert _tokens(args) == [
        "--remove-edges.by-type", "highway.footway",
        "--keep-edges.min-speed", "5",
    ]


def test_a_single_token_per_occurrence_still_works_and_composes():
    """The equals form was the only one that worked before; it must keep working."""
    args = _parse([
        "--netconvert-arg=--remove-edges.by-type",
        "--netconvert-arg=highway.footway",
        "--netconvert-arg", "--keep-edges.min-speed 5",
    ])
    assert _tokens(args) == [
        "--remove-edges.by-type", "highway.footway",
        "--keep-edges.min-speed", "5",
    ]


def test_a_valueless_option_needs_the_equals_form():
    """A lone dash-prefixed token is read as an option however it is quoted.

    Shell quoting settles word splitting before the parser sees anything, so `--no-turnarounds`
    arrives as one token beginning with a dash and is read as an option of its own. What carries
    the pair above is the space *inside* the token, which is why a valueless option is the one
    case still needing `=`.
    """
    assert _tokens(_parse(["--netconvert-arg=--no-turnarounds"])) == ["--no-turnarounds"]
    with pytest.raises(SystemExit):
        _parse(["--netconvert-arg", "--no-turnarounds"])


def test_giving_the_option_and_value_unquoted_is_refused_rather_than_silently_wrong():
    """The failure the quoting exists to avoid.

    `--netconvert-arg --remove-edges.by-type` leaves the option with no value, because argparse
    reads the second dash-prefixed token as an option of its own. Refusing beats building a world
    against a flag set nobody asked for.
    """
    with pytest.raises(SystemExit):
        _parse(["--netconvert-arg", "--remove-edges.by-type",
                "--netconvert-arg", "highway.footway"])


def test_no_extra_arguments_yields_nothing():
    assert _tokens(_parse([])) == []
