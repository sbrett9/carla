#!/usr/bin/env python3
"""The server serves back the OpenDRIVE it was given, character for character, whatever the script.

A generated world reaches the server as OpenDRIVE text (`generate_opendrive_world`) and comes back
from it the same way (`get_map_data`). Both ends are UTF-8. The server once decoded the incoming text
a byte at a time (`carla::rpc::ToLongFString`), so every character outside ASCII became two: the
Shahid Bahonar world, whose street names are Persian, was sent with 644 non-ASCII characters and
served with 1,288, and the co-simulation session rightly refused to drive it, because the road
network the server held was no longer the one its world package carries.

Two modes:

  * against a world package (the default, and non-destructive): the loaded world's OpenDRIVE is
    compared with the package's `map.xodr` the way the session compares them -- from the root
    element, with the header's build date blanked -- and the non-ASCII characters of each are
    counted, so a mismatch says whether it is an encoding fault or a different build;
  * `--generate`: a one-road world whose road names are Persian and German is loaded and read
    back. This replaces the loaded world, so run it on a server whose world you can rebuild.

Prereqs: a server built from this tree; for the default mode, the world the package was built as,
loaded.

Usage:
    python test_opendrive_text_roundtrip.py --world-package ../../Build/world-packages/Shahid_Bahonar_Port.cwp
    python test_opendrive_text_roundtrip.py --generate
"""
import argparse
import hashlib
import logging
import re
import sys
import zipfile

import carlanet as carla

# A straight two-lane road, 100 m long. The names are what is being tested: Persian (two bytes a
# character in UTF-8), German (one character outside ASCII) and a dash (three bytes).
ROAD_NAME = "خیابان شهید باهنر — Hauptstraße"
ONE_ROAD = f"""<?xml version="1.0" standalone="yes"?>
<OpenDRIVE>
  <header revMajor="1" revMinor="4" name="{ROAD_NAME}" version="1.00" date="" north="10" south="-10" east="100" west="0"/>
  <road name="{ROAD_NAME}" length="100.0" id="1" junction="-1">
    <link/>
    <type s="0" type="town"/>
    <planView>
      <geometry s="0.0" x="0.0" y="0.0" hdg="0.0" length="100.0"><line/></geometry>
    </planView>
    <elevationProfile><elevation s="0" a="0" b="0" c="0" d="0"/></elevationProfile>
    <lateralProfile/>
    <lanes>
      <laneSection s="0.0">
        <left><lane id="1" type="driving" level="false"><link/><width sOffset="0" a="3.5" b="0" c="0" d="0"/></lane></left>
        <center><lane id="0" type="driving" level="false"><link/></lane></center>
        <right><lane id="-1" type="driving" level="false"><link/><width sOffset="0" a="3.5" b="0" c="0" d="0"/></lane></right>
      </laneSection>
    </lanes>
    <objects/>
    <signals/>
  </road>
</OpenDRIVE>
"""


def normalised(xodr: str) -> str:
    """From the root element, with the header's build date blanked, as the session compares."""
    root = xodr.find("<OpenDRIVE")
    body = xodr[root:] if root >= 0 else xodr
    return re.sub(r'(<header\b[^>]*?\bdate=")[^"]*(")', r"\1\2", body, count=1)


def non_ascii(text: str) -> int:
    return sum(1 for character in text if ord(character) > 127)


def digest(text: str) -> str:
    return hashlib.sha256(normalised(text).encode("utf-8")).hexdigest()[:12]


def against_package(world, package: str, logger: logging.Logger) -> bool:
    with zipfile.ZipFile(package) as archive:
        packaged = archive.read("map.xodr").decode("utf-8")
    served = world.get_map().to_opendrive()
    logger.info("package: %d characters, %d outside ASCII, digest %s",
                len(packaged), non_ascii(packaged), digest(packaged))
    logger.info("served:  %d characters, %d outside ASCII, digest %s",
                len(served), non_ascii(served), digest(served))
    if normalised(served) == normalised(packaged):
        return True
    if non_ascii(served) == 2 * non_ascii(packaged) and non_ascii(packaged):
        logger.error("the served text has exactly twice the package's non-ASCII characters: it was "
                     "decoded a byte at a time on its way into the server")
    else:
        logger.error("the served text differs from the package's: another build of the world is "
                     "loaded, or the package is not the one it was built as")
    return False


def round_trip(client, logger: logging.Logger) -> bool:
    world = client.generate_opendrive_world(ONE_ROAD)
    served = world.get_map().to_opendrive()
    logger.info("sent %d characters outside ASCII, served %d", non_ascii(ONE_ROAD), non_ascii(served))
    if ROAD_NAME not in served:
        logger.error("the road's name did not come back intact")
        return False
    if normalised(served) != normalised(ONE_ROAD):
        logger.error("the served text differs from the text sent")
        return False
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--world-package", help="the .cwp the loaded world was built as")
    mode.add_argument("--generate", action="store_true",
                      help="load a one-road world with non-ASCII names and read it back; replaces "
                           "the loaded world")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("test_opendrive_text_roundtrip")

    client = carla.Client(args.host, args.port)
    client.set_timeout(120.0)
    passed = (round_trip(client, logger) if args.generate
              else against_package(client.get_world(), args.world_package, logger))
    logger.info("PASS" if passed else "FAIL")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
