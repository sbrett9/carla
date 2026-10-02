#!/usr/bin/env python3
"""Measure each vehicle's body width without its mirrors, from the meshes the editor exports.

The catalogue sweep measures a vehicle by spawning it and reading its bounding box, which spans the
whole mesh, wing mirrors included. SUMO's width is the body's: it decides the room a vehicle takes in
its lane and, under a lane change spread over time, which neighbouring lanes it overlaps. Measured
with its mirrors, `vehicle.fuso.mitsubishi` is 3.93 m wide, wider than every 3.35 m lane on the shipped
networks, and the Bahonar pattern of life deadlocked behind it; its body is 3.23 m. A server cannot
see vertices, so the body width is measured from the mesh itself, exported from the editor, and the
catalogue carries it beside the bounding box as a measured input (`vehicle_body_widths.json`).

**Editor-side export.** In the Unreal editor, with the CARLA content loaded: for every vehicle
blueprint the catalogue holds, find the skeletal mesh its vehicle mesh component draws, and export that
mesh as FBX in ASCII format, level of detail 0, named `<blueprint id>.fbx`
(`vehicle.fuso.mitsubishi.fbx`). The export keeps the mesh's own frame: X along the vehicle, Y across
it, Z up, in centimetres. The 2026-10-02 measurement exported all nineteen through VibeUE from the
UE 5.7.4 editor.

**The method.** Every LOD0 vertex is binned along the vehicle's length in 5 cm bins, and each side's
widest vertex from the mesh's centre line is kept per bin. A side's body half-width is the largest
half-width held over at least 0.6 m of length -- over every run of twelve consecutive bins, the run's
narrowest bin, and the largest of those -- a morphological opening that removes mirrors and any other
protrusion shorter than 0.6 m along the vehicle. The body width is the two sides' sum. The full width
is the plain vertex extent across the vehicle, which equals the catalogue's `width_m`.

    python measure_vehicle_body_widths.py <directory of exported .fbx> \\
        [--measured "2026-10-02, UE 5.7.4 editor via VibeUE"] [--out ../catalogue/vehicle_body_widths.json]

Then apply it to the catalogue with `apply_vehicle_body_widths.py`, or let the next sweep merge it.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
DEFAULT_OUT = _THIS.parent / "catalogue" / "vehicle_body_widths.json"

BODY_WIDTHS_VERSION = 1
BIN_CM = 5.0
# How far along the vehicle a width has to hold to be the body's rather than a protrusion's.
HELD_OVER_CM = 60.0
METHOD = (
    "LOD0 vertices of the vehicle's own skeletal mesh (the mesh the blueprint drives), exported from "
    "the editor as ASCII FBX; width profile along the vehicle's length in 5 cm bins, each side "
    "separately; body half-width per side = the largest width held over at least 0.6 m of length (a "
    "morphological opening), which removes mirrors and other short protrusions; body width = left + "
    "right body half-widths. Full width = the plain vertex extent, equal to the catalogue's width_m. "
    "Computed by CarlaControl/scripts/measure_vehicle_body_widths.py.")

VERTICES = re.compile(r"Vertices: \*\d+ \{\s*a: ([^}]*)\}")


def vertices(fbx: Path) -> list[tuple[float, float, float]]:
    """Every vertex of every geometry in an ASCII FBX, in its file's units (centimetres)."""
    text = fbx.read_text(encoding="utf-8", errors="ignore")
    points: list[tuple[float, float, float]] = []
    for match in VERTICES.finditer(text):
        values = [float(token) for token in match.group(1).replace("\n", "").split(",") if token.strip()]
        points.extend(tuple(values[i:i + 3]) for i in range(0, len(values) - 2, 3))
    if not points:
        raise ValueError(f"{fbx} holds no vertices: is it an ASCII FBX?")
    return points


def held_width(profile: list[float | None], run: int) -> float:
    """The largest half-width held over `run` consecutive bins: the opening of the profile."""
    best = 0.0
    for start in range(len(profile) - run + 1):
        window = profile[start:start + run]
        if all(value is not None for value in window):
            best = max(best, min(window))
    return best


def measure(fbx: Path) -> dict[str, float]:
    """Length, full width, body width and height of one exported mesh, in metres."""
    points = vertices(fbx)
    low = [min(p[axis] for p in points) for axis in range(3)]
    high = [max(p[axis] for p in points) for axis in range(3)]
    bins = int((high[0] - low[0]) / BIN_CM) + 1
    centre = (high[1] + low[1]) / 2.0
    left: list[float | None] = [None] * bins
    right: list[float | None] = [None] * bins
    for x, y, _ in points:
        k = int((x - low[0]) / BIN_CM)
        across = y - centre
        side = left if across > 0 else right
        reach = abs(across)
        if side[k] is None or reach > side[k]:
            side[k] = reach
    run = int(round(HELD_OVER_CM / BIN_CM))
    return {"length_m": round((high[0] - low[0]) / 100.0, 4),
            "full_width_m": round((high[1] - low[1]) / 100.0, 4),
            "body_width_m": round((held_width(left, run) + held_width(right, run)) / 100.0, 4),
            "height_m": round((high[2] - low[2]) / 100.0, 4)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("fbx_directory", type=Path,
                        help="the exported meshes, one <blueprint id>.fbx per vehicle blueprint")
    parser.add_argument("--measured", required=True,
                        help="when and from what the meshes were exported, recorded with the widths")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="the table to write")
    args = parser.parse_args(argv)
    files = sorted(args.fbx_directory.glob("vehicle.*.fbx"))
    if not files:
        print(f"no vehicle.*.fbx in {args.fbx_directory}", file=sys.stderr)
        return 1
    table = {"body_widths_version": BODY_WIDTHS_VERSION, "method": METHOD, "measured": args.measured,
             "vehicles": {}}
    for fbx in files:
        blueprint_id = fbx.name[:-len(".fbx")]
        table["vehicles"][blueprint_id] = measure(fbx)
        widths = table["vehicles"][blueprint_id]
        print(f"{blueprint_id:34s} full {widths['full_width_m']:.4f} m  body {widths['body_width_m']:.4f} m")
    args.out.write_text(json.dumps(table, indent=2, ensure_ascii=False) + "\n", encoding="utf-8",
                        newline="\n")
    print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
