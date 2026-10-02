"""A world package's netconvert arguments name its output files by fixed names, whenever it was built.

The world build wrote netconvert's two outputs to scratch files named at random and recorded them as
passed, so two builds of an identical world recorded different arguments, and a scenario compiled
against the first looked stale against the second: measured on Bahonar, the two recorded arguments
that differed after a rebuild were exactly the OpenDRIVE and network output paths. New packages record
the outputs by fixed names; the reader names them so for packages already built.
"""
from __future__ import annotations

import json
import os
import sys
import zipfile

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402


def package_with(tmp_path, argv: list[str], name: str) -> WorldPackageReader:
    path = tmp_path / name
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("world.json", json.dumps({"MapName": "m", "NetconvertArgv": argv}))
    return WorldPackageReader(path)


def built(scratch: str) -> list[str]:
    return ["--osm-files", "G:/w/m_clipped.osm",
            "--opendrive-output", f"C:/Temp/carlanet_osm_{scratch}a.xodr",
            "--proj", "+proj=tmerc +lat_0=27.15 +lon_0=56.18",
            "--output-file", f"C:/Temp/carlanet_osm_{scratch}b.net.xml",
            "--output.street-names"]


def test_two_builds_of_one_world_read_as_the_same_arguments(tmp_path):
    first = package_with(tmp_path, built("0f"), "first.cwp").netconvert_argv
    second = package_with(tmp_path, built("9c"), "second.cwp").netconvert_argv
    assert first == second
    assert first[first.index("--opendrive-output") + 1] == "<opendrive-output>"
    assert first[first.index("--output-file") + 1] == "<output-file>"


def test_the_inputs_and_flags_are_kept_as_recorded(tmp_path):
    argv = package_with(tmp_path, built("0f"), "first.cwp").netconvert_argv
    assert argv[:2] == ["--osm-files", "G:/w/m_clipped.osm"]
    assert "+proj=tmerc +lat_0=27.15 +lon_0=56.18" in argv
    assert argv[-1] == "--output.street-names"


def test_a_package_that_already_names_its_outputs_reads_unchanged(tmp_path):
    named = ["--opendrive-output", "<opendrive-output>", "--output-file", "<output-file>"]
    assert package_with(tmp_path, named, "named.cwp").netconvert_argv == named


def test_a_metered_world_reads_with_its_meters_and_their_programme_file(tmp_path):
    """The ramp meters' options read as recorded, and the file the second names is in the package."""
    argv = built("0f") + ["--junctions.join-exclude", "582784737,582785322",
                          "--tllogic-files", "<tllogic-files>"]
    path = tmp_path / "metered.cwp"
    programs = '<tlLogics>\n    <tlLogic id="582785322" type="static" programID="0" offset="0"/>\n</tlLogics>\n'
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("world.json", json.dumps({"MapName": "m", "NetconvertArgv": argv}))
        archive.writestr("map.tll.xml", programs)
    package = WorldPackageReader(path)

    read = package.netconvert_argv
    assert read[-4:] == ["--junctions.join-exclude", "582784737,582785322",
                         "--tllogic-files", WorldPackageReader.RECORDED_PROGRAMS_INPUT]
    assert package.traffic_light_programs() == programs


def test_a_world_with_no_ramp_meter_carries_no_programme_file(tmp_path):
    assert package_with(tmp_path, built("0f"), "plain.cwp").traffic_light_programs() is None
