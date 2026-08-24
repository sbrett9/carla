"""Validate that a client which did NOT build the world reports the same bare-earth truth.

Background:
  Telemetry reports each vehicle's bare-earth ellipsoidal-WGS84 altitude by removing the surface
  shift that seats the road on the photoreal imagery: hae = physical - offset. That offset is a
  single constant for --height-align area/origin (zero for 'none'), and a per-cell field over the
  OSM sandbox for 'drape'.

  Both used to live only as in-memory state on the C# CarlaClient that ran the world build. Any
  OTHER client saw those fields unset, subtracted zero, and reported the photoreal-referenced
  height as bare-earth truth — silently, because nothing in the numbers reveals the error. That is
  not hypothetical: it happened on every reconnect to a running server, and the shift is metres
  (measured -2.01 m over open ground on the default extract).

  The generating client now publishes the offset to the server, and any client recovers it with
  CarlaClient.EnsureBareEarthReference(). This test is the regression guard for that.

Flow: connect -> assert a world that was never built from OSM reports NO record (so 'no shift' stays
distinguishable from 'shift unknown') -> build an elevated world -> open a SECOND, independent client
-> assert it starts with truth unknown, recovers the record, and agrees with the builder on every
field, on the raw grid bytes, and on a sampled ground elevation.

The grid comparison is by SHA-256 over the raw float32 buffers, so it also exercises the wire
round-trip: the grids leave as C# float[], cross msgpack, and come back through std::vector<float>.

Prereqs (same as test_telemetry_dtm_decoupling.py):
  * Headless server running + ticking (RunCarlaServer.ps1).
  * SUMO netconvert staged under Build/sumo-install.
  * CESIUM_ION_TOKEN env var (or --ion-token).

Usage:
    python test_bare_earth_reference.py [--height-align drape|area|origin|none]
        [--osm <path>] [--ion-token <jwt>] [--host h] [--port p]
"""
import argparse
import hashlib
import os
import re
import sys
import time

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
_INSTALL = os.path.join(_REPO, "Build", "sumo-install")
_NETCONVERT = os.path.join(_INSTALL, "bin",
                           "netconvert.exe" if os.name == "nt" else "netconvert")
_PROJ = os.path.join(_INSTALL, "share", "proj")

ap = argparse.ArgumentParser()
ap.add_argument("--osm", default=os.path.join(_REPO, "Import", "Gardnerville_Centerville_Lane.osm"))
ap.add_argument("--lat", type=float, default=None, help="origin lat (default: OSM bounds center)")
ap.add_argument("--lon", type=float, default=None, help="origin lon (default: OSM bounds center)")
ap.add_argument("--step", type=float, default=10.0)
ap.add_argument("--ion-token", default=os.environ.get("CESIUM_ION_TOKEN", ""))
ap.add_argument("--ion-asset-id", type=int, default=2275207)   # Google photoreal (visual)
ap.add_argument("--ground-asset-id", type=int, default=1)      # Cesium World Terrain (bare earth)
ap.add_argument("--height-align", choices=["area", "origin", "none", "drape"], default="drape",
                help="mode under test (default 'drape', the only one carrying a per-cell grid; the "
                     "others exercise the scalar offset)")
ap.add_argument("--terrain-res", type=float, default=8.0, help="drape: heightfield cell size (m)")
ap.add_argument("--terrain-margin", type=float, default=30.48, help="drape: sandbox margin past OSM (m)")
ap.add_argument("--drape-cache-dir", default=os.path.join(_REPO, "Build", "drape-cache"),
                help="drape: grid sampling cache dir (speeds re-runs)")
ap.add_argument("--settle", type=float, default=10.0)
ap.add_argument("--stock-map", default="Town10HD_Opt",
                help="a map that was never generated from OSM, loaded first so the 'no record' "
                     "check does not read a record left by an earlier run on the same server")
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)
ap.add_argument("--timeout", type=float, default=300.0)
args = ap.parse_args()

os.environ.setdefault("CARLA_NETCONVERT", _NETCONVERT)
os.environ.setdefault("PROJ_LIB", _PROJ)
os.environ.setdefault("PROJ_DATA", _PROJ)

# isort: off
# Order matters and is not alphabetical: importing carlanet is what loads the CLR assemblies, so the
# CarlaNet.* namespaces do not exist until it has run. Sorting these two swaps them and the second
# import fails.
import carlanet as carla  # noqa: E402
from CarlaNet.Map import OsmConversionOptions  # noqa: E402
# isort: on


def read_osm_bounds(path):
    """(minlat, minlon, maxlat, maxlon) from the OSM <bounds> element, or None."""
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                if "<bounds" in line:
                    def g(k, text=line):
                        m = re.search(k + r'="([-0-9.]+)"', text)
                        return float(m.group(1)) if m else None
                    vals = (g("minlat"), g("minlon"), g("maxlat"), g("maxlon"))
                    return vals if None not in vals else None
    except OSError:
        return None
    return None


def make_options():
    opts = OsmConversionOptions()
    opts.NetconvertPath = _NETCONVERT
    opts.ProjDataDirectory = _PROJ
    opts.GenerateTrafficLights = False
    opts.OriginLatitude = args.lat
    opts.OriginLongitude = args.lon
    from System.Collections.Generic import List
    extra = List[str]()
    for a in ["--keep-edges.by-vclass", "passenger",
              "--keep-edges.components", "1",
              "--remove-edges.isolated", "true"]:
        extra.Add(a)
    opts.ExtraArgs = extra
    return opts


class BareEarthReferenceTest:
    """Builds a world with one client and verifies a second client recovers identical truth."""

    def __init__(self) -> None:
        self.failures: list[str] = []

    def check(self, label: str, ok: bool, detail: str = "") -> bool:
        print(f"  {'PASS' if ok else 'FAIL'}  {label}{(' - ' + detail) if detail else ''}")
        if not ok:
            self.failures.append(label)
        return ok

    @staticmethod
    def state(inner) -> dict:
        """The bare-earth state a telemetry consumer reads off a CarlaClient."""
        off = bytes(inner.LastDrapedOffsetBytes)
        dtm = bytes(inner.LastDrapedDtmBytes)
        return {
            "known": bool(inner.HasBareEarthReference),
            "drape": bool(inner.LastDrapeActive),
            "offset": float(inner.LastHeightAlignOffset),
            "cols": int(inner.LastDrapeNumCols),
            "rows": int(inner.LastDrapeNumRows),
            "min_x": float(inner.LastDrapeMinX),
            "min_y": float(inner.LastDrapeMinY),
            "cell": float(inner.LastDrapeCellSize),
            "off_len": len(off),
            "dtm_len": len(dtm),
            "off_sha": hashlib.sha256(off).hexdigest()[:16],
            "dtm_sha": hashlib.sha256(dtm).hexdigest()[:16],
        }

    def run(self) -> int:
        print(f"== bare-earth reference test (height-align={args.height_align}) ==")
        if not os.path.exists(args.osm):
            print(f"ERROR: OSM not found: {args.osm}", file=sys.stderr)
            return 2
        if not os.path.exists(_NETCONVERT):
            print(f"ERROR: netconvert not staged: {_NETCONVERT}", file=sys.stderr)
            return 2
        if not args.ion_token:
            print("ERROR: no Cesium Ion token (set CESIUM_ION_TOKEN or --ion-token).", file=sys.stderr)
            return 2
        if args.lat is None or args.lon is None:
            b = read_osm_bounds(args.osm)
            if b is None:
                print("ERROR: no --lat/--lon and no <bounds> in OSM", file=sys.stderr)
                return 2
            args.lat = (b[0] + b[2]) / 2.0
            args.lon = (b[1] + b[3]) / 2.0
        print(f"   origin: {args.lat:.7f}, {args.lon:.7f}")

        client = carla.Client(args.host, args.port)
        client.set_timeout(15.0)
        print(f"   server: {client.get_server_version()}")
        builder = client._inner

        print(f"[1] a world that was never built from OSM carries no record ({args.stock_map})")
        # Load the stock map explicitly rather than trusting whatever the server happens to hold: a
        # previous generated world would still carry its record, and the check below would read that
        # as a failure. Loading also exercises the invalidation path, since a new world must clear
        # any reference cached for the previous one.
        # Hold the generous timeout across the query too: the server is still streaming the level in
        # when load_world returns, and a short timeout here fails on map size rather than on anything
        # this test is about.
        client.set_timeout(args.timeout)
        client.load_world(args.stock_map)
        scalars = builder.GetBareEarthReferenceAsync().GetAwaiter().GetResult()
        self.check("stock map reports no bare-earth record",
                   scalars is None or scalars.Count == 0,
                   f"count={0 if scalars is None else scalars.Count}")
        self.check("client reports truth as unknown rather than a zero shift",
                   not bool(builder.HasBareEarthReference))

        print("[2] building elevated world (convert -> sample -> inject -> mesh)...")
        client.set_timeout(args.timeout)
        t0 = time.time()
        client.generate_world_from_osm_with_elevation(
            args.osm, args.ion_token, args.ion_asset_id,
            ground_ion_asset_id=args.ground_asset_id,
            osm_options=make_options(),
            sample_step_meters=args.step,
            height_align=args.height_align,
            ground_collision=True,
            cesium_settle_seconds=args.settle,
            terrain_res=args.terrain_res,
            terrain_margin=args.terrain_margin,
            drape_cache_dir=args.drape_cache_dir)
        built = self.state(builder)
        print(f"    built in {time.time() - t0:.1f}s | drape={built['drape']} "
              f"grid={built['cols']}x{built['rows']} @ {built['cell']:.1f} m "
              f"offset={built['offset']:.3f} m grid_bytes={built['off_len']:,}")
        self.check("the building client knows the reference", built["known"])
        if args.height_align == "drape":
            self.check("drape produced a per-cell field", built["drape"] and built["off_len"] > 0)
        else:
            self.check("constant-offset mode carries no per-cell field", not built["drape"])

        print("[3] a second, independent client recovers it from the server")
        other = carla.Client(args.host, args.port)
        other.set_timeout(30.0)
        fresh = other._inner
        self.check("a fresh client starts with truth unknown",
                   not bool(fresh.HasBareEarthReference),
                   "would otherwise report physical height as bare earth")
        self.check("EnsureBareEarthReference succeeds", bool(fresh.EnsureBareEarthReference()))
        got = self.state(fresh)
        for key in ("drape", "offset", "cols", "rows", "min_x", "min_y", "cell"):
            self.check(f"recovered {key} matches", got[key] == built[key], f"{got[key]!r}")
        self.check("offset grid is byte-identical",
                   got["off_sha"] == built["off_sha"] and got["off_len"] == built["off_len"],
                   f"{got['off_len']:,} B sha {got['off_sha']}")
        self.check("ground grid is byte-identical",
                   got["dtm_sha"] == built["dtm_sha"] and got["dtm_len"] == built["dtm_len"],
                   f"{got['dtm_len']:,} B sha {got['dtm_sha']}")

        if built["drape"]:
            print("[4] both clients agree on sampled ground elevation")
            mid_x = built["min_x"] + built["cell"] * built["cols"] / 2.0
            mid_y = built["min_y"] + built["cell"] * built["rows"] / 2.0
            a = builder.SampleDrapeGroundElevation(mid_x, mid_y)
            b = fresh.SampleDrapeGroundElevation(mid_x, mid_y)
            self.check("sampled ground elevation identical",
                       a is not None and b is not None and abs(float(a) - float(b)) < 1e-9,
                       f"builder={a}, fresh={b}")

        print()
        if self.failures:
            print(f"FAILED ({len(self.failures)}): " + "; ".join(self.failures), file=sys.stderr)
            return 1
        print("PASS: a client that did not build the world reports the same bare-earth truth.")
        return 0


def main() -> int:
    return BareEarthReferenceTest().run()


if __name__ == "__main__":
    sys.exit(main())
