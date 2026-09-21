"""A scenario must be built against the network its world was built from, or not at all.

The world build runs netconvert once and puts the SUMO network into the world package. The scenario
build loads it. It cannot rebuild it: asking netconvert for OpenDRIVE output makes it default
`rectangular-lane-cut` to true, so a second run with byte-identical flags produces a different graph
-- 743 of 4,978 canonical rows, one lane at 352.19 m against 2.60 m -- while the map boundary
matches to the centimetre. A scenario authored against that network names edges the rendered world
does not have, and every downstream check passes.

So the only protection is a refusal, and a refusal that has never fired is not a check. Four failures
are exercised here: a package built with different flags, a package whose recorded fingerprint does
not match the network it carries, a package carrying no network at all, and a package that does not
exist. The first one also asserts what the refusal *says*, because a message that does not name the
flag leaves the reader no better off than a crash.

The flag set both sides must agree on is not restated here. It is read from
`CarlaNet/test/CarlaNet.Tests/Map/Fixtures/Network/world_build_argv.txt`, which
`NetconvertArgumentsTests.cs` asserts the C# world build produces -- so the two halves of the
contract are checked against one file rather than against each other's transcription.
"""
from __future__ import annotations

import json
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.SumoScenarioBuilder import NetconvertSettings, SumoScenarioBuilder  # noqa: E402

FIXTURES = _REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network"

# The world build's argument list, and the origin it was built for.
WORLD_BUILD_ARGV = [line for line in
                    (FIXTURES / "world_build_argv.txt").read_text(encoding="utf-8").splitlines()
                    if line]
ORIGIN_LAT, ORIGIN_LON = 39.59431, -104.88449

# The network a package carries. Any real netconvert output will do; this one has a signalised
# junction, so the signal programme is part of its fingerprint.
NETWORK = (FIXTURES / "street_names_true.net.xml").read_text(encoding="utf-8")


def settings(**overrides) -> NetconvertSettings:
    """The flag set a scenario for this world requires, matching the world build's defaults."""
    return NetconvertSettings(origin_lat=ORIGIN_LAT, origin_lon=ORIGIN_LON, **overrides)


def write_package(directory: Path, argv: list[str] | None = None, network: str | None = NETWORK,
                  fingerprint: str | None = None) -> Path:
    """A world package, as the world build writes one, with the parts under test substituted."""
    package = directory / "TestWorld.cwp"
    network_text = NETWORK if network is None else network
    manifest = {
        "MapName": "TestWorld",
        "OriginLatitude": ORIGIN_LAT,
        "OriginLongitude": ORIGIN_LON,
        "NetworkFingerprint": (NetworkFingerprint.of_text(network_text)
                               if fingerprint is None else fingerprint),
        "NetconvertArgv": WORLD_BUILD_ARGV if argv is None else argv,
        "NetconvertPath": r"C:\sumo\bin\netconvert.exe",
        "NetconvertVersion": "Eclipse SUMO netconvert Version 1.27.0",
    }
    with zipfile.ZipFile(package, "w", zipfile.ZIP_STORED) as archive:
        archive.writestr("world.json", json.dumps(manifest, indent=2))
        archive.writestr("map.xodr", "<OpenDRIVE/>")
        if network is not None:
            archive.writestr("map.net.xml", network)
    return package


def test_the_shipped_flag_set_matches_the_world_build(tmp_path):
    """The contract. The scenario defaults and the world build defaults are one flag set, so a
    scenario written with no overrides binds to a world built with none."""
    assert settings().differences_from(WORLD_BUILD_ARGV) == []


def test_the_network_comes_out_of_the_package_verbatim(tmp_path):
    package = write_package(tmp_path)
    out = tmp_path / "out" / "TestWorld.net.xml"

    SumoScenarioBuilder().build_network(package, out, settings())

    assert out.read_text(encoding="utf-8") == NETWORK


def test_file_paths_do_not_count_as_a_difference(tmp_path):
    """The world build converts a temporary file into a temporary file; the scenario names neither.
    If paths counted, every package would be refused and the check would be worthless."""
    argv = list(WORLD_BUILD_ARGV)
    argv[argv.index("--osm-files") + 1] = r"C:\Temp\carlanet_osm_a1b2.osm"
    argv[argv.index("--output-file") + 1] = r"C:\Temp\carlanet_osm_c3d4.net.xml"
    argv[argv.index("--opendrive-output") + 1] = r"C:\Temp\carlanet_osm_e5f6.xodr"

    assert settings().differences_from(argv) == []


def test_a_whole_number_written_two_ways_is_not_a_difference(tmp_path):
    """C# writes 25.0 as `25` and Python writes it as `25.0`. A difference in how a language prints
    a float is not a difference in how a map was built."""
    argv = list(WORLD_BUILD_ARGV)
    argv[argv.index("--junctions.join-dist") + 1] = "25.0"

    assert settings().differences_from(argv) == []


def test_a_world_built_with_different_flags_is_refused_by_name(tmp_path):
    """The case the whole mechanism exists for: a world built before the flag sets were unified,
    without street names, against a scenario that needs them. The two networks differ -- 1,017 roads
    and 183 junctions become 1,021 and 184 on Arapahoe -- and nothing else would notice."""
    argv = [token for token in WORLD_BUILD_ARGV]
    index = argv.index("--output.street-names")
    del argv[index:index + 2]
    package = write_package(tmp_path, argv=argv)

    with pytest.raises(ValueError) as refusal:
        SumoScenarioBuilder().build_network(package, tmp_path / "out.net.xml", settings())

    message = str(refusal.value)
    assert "--output.street-names" in message
    # Both fingerprints, so a reader can tell a flag disagreement from a corrupted package.
    assert NetworkFingerprint.of_text(NETWORK) in message
    assert "this scenario wants" in message and "the world was built with" in message
    # And only the flag that differs: naming flags that agree would bury the one that matters.
    assert "--junctions.join-dist" not in message
    assert "--osm-files" not in message


def test_setting_street_names_false_is_still_refused(tmp_path):
    """`false` and `true` produce the same graph, because NBEdge::expandableBy guards on whether the
    option was set at all. They are still different invocations, and a world whose build wrote
    `false` was configured by someone who believed it was off. Refusing says so."""
    argv = list(WORLD_BUILD_ARGV)
    argv[argv.index("--output.street-names") + 1] = "false"
    package = write_package(tmp_path, argv=argv)

    with pytest.raises(ValueError, match="--output.street-names"):
        SumoScenarioBuilder().build_network(package, tmp_path / "out.net.xml", settings())


def test_a_differently_phased_world_is_refused(tmp_path):
    """The signal programme is fixed when the world is built, now that one invocation produces both
    documents. A scenario expecting actuated signals must not silently run on fixed-time ones."""
    argv = list(WORLD_BUILD_ARGV)
    index = argv.index("--tls.default-type")
    del argv[index:index + 2]
    package = write_package(tmp_path, argv=argv)

    with pytest.raises(ValueError, match="--tls.default-type"):
        SumoScenarioBuilder().build_network(package, tmp_path / "out.net.xml", settings())


def test_a_scenario_may_require_a_different_flag_set_and_say_so(tmp_path):
    """The refusal is symmetric: an author who needs the private roads kept must build the world
    that way, and is told which flag stands between them."""
    package = write_package(tmp_path)

    with pytest.raises(ValueError) as refusal:
        SumoScenarioBuilder().build_network(package, tmp_path / "out.net.xml",
                                            settings(drivable_edges_only=False))

    assert "--keep-edges.by-vclass" in str(refusal.value)


def test_a_package_assembled_from_two_builds_is_refused(tmp_path):
    """The recorded fingerprint and the carried network must agree; if they do not, the package was
    put together from parts of two builds and neither half can be trusted."""
    package = write_package(tmp_path, fingerprint="0" * 64)

    with pytest.raises(ValueError) as refusal:
        SumoScenarioBuilder().build_network(package, tmp_path / "out.net.xml", settings())

    message = str(refusal.value)
    assert "0" * 64 in message
    assert NetworkFingerprint.of_text(NETWORK) in message


def test_a_package_with_no_network_says_it_cannot_be_rebuilt(tmp_path):
    """A world built before the network was carried. There is no quiet fallback that would be
    correct, so the refusal says what has to happen instead."""
    package = write_package(tmp_path, network=None)

    with pytest.raises(ValueError) as refusal:
        SumoScenarioBuilder().build_network(package, tmp_path / "out.net.xml", settings())

    assert "cannot be regenerated" in str(refusal.value)


def test_a_missing_package_is_reported_as_missing(tmp_path):
    with pytest.raises(FileNotFoundError):
        SumoScenarioBuilder().build_network(tmp_path / "absent.cwp",
                                            tmp_path / "out.net.xml", settings())
