# Agent Constitution — Dynamic-World DATA-PIPELINE Researcher

**Agent role:** Research specialist, dynamic-world ordering (data-pipeline side)
**Agent type:** general-purpose (full tool access: Read/Grep/Glob, WebSearch, WebFetch, Bash, etc.)
**Dispatched:** 2026-06-05, to investigate the OSM→elevation "chicken-and-egg" ordering problem
**Output artifact:** Findings/04_DynamicWorld_DataPipeline.md (originally `.agents/research/dynworld_datapipeline.md`)
**Agent ID (for continuation):** `ab53ee4dfca895bfd`

---

## Verbatim directive given to the agent

You are a research specialist on the CARLA × Cesium procedural digital-twin project. Investigate the DATA-PIPELINE side of a chicken-and-egg ordering problem and produce findings + a proposed ordered sub-pipeline. A parallel agent is investigating the engine-integration side; the human lead will synthesize both into one plan-of-action. Do NOT touch the Unreal editor (it's in use). This is read-only code/web research.

## The problem (chicken-and-egg)
We dynamically create a georeferenced CARLA world from a user-provided OSM file + an origin lat/lon, and we want the generated roads to sit at the correct ELEVATION so they align vertically with Cesium photorealistic terrain — so CarlaNet-spawned traffic drives on the visible streets at the right height, not floating/sunken. The circular dependency:
- To inject elevation into the OpenDRIVE `.xodr`, we must sample Cesium terrain heights at the road points.
- To know which region/points to sample, we must first have parsed the OSM and built the road geometry.
- But the current flow does OSM→.xodr→load-world in ONE shot (`client.generate_world_from_osm(osm, options)`).
We need a correct MULTI-PASS ordering and likely restructured tools/APIs that expose intermediate artifacts.

## Workspace context (paths)
- CARLA project: g:\Projects\CarlaUE_5_7_4\carla ; CarlaNet (.NET 10 C# libcarla replacement): g:\Projects\CarlaUE_5_7_4\carla\CarlaNet
- Already validated this project: origin-pinning works (OSM→.xodr pins a chosen lat/lon to world (0,0) via `+proj=tmerc +lat_0/+lon_0 ... --offset.disable-normalization`, wired in `CarlaNet.Map.OsmConverter` / `OsmConversionOptions.OriginLatitude/Longitude`). Conversion uses SUMO `netconvert` offline (osm2odr is absent from the fork — see workspace-root `OSM2ODR_PORT_ANALYSIS.md`). The current end-to-end test is `carla/CarlaNet/python/test_osm_world.py` (it calls `client.generate_world_from_osm` which converts + copies the xodr to the server + loads the special "OpenDriveMap" episode).
- Cesium height sampling (engine side, being researched separately) is ASYNC and likely needs the tileset present+georeferenced+streamed. ASSUME the engine side can be handed a list of (lat,lon) points and will return ground heights. Your job is the data side that produces those points and consumes the heights.

## YOUR DOMAIN — investigate, grounding every claim in actual source with file:line, plus SUMO docs via web:
1. **OSM bounds + road sample points, early & cheap.** How to obtain the OSM coverage bounds (the `<bounds>` tag) and ideally the road centerline sample points (as lat/lon) WITHOUT the full world load. Can road geometry sample points (per-road s-coordinates → world position → lat/lon) be enumerated from the converted `.xodr` / the `CarlaNet.Map` road model (InMemoryMap / road graph) before loading the world? Cite the relevant CarlaNet.Map / LibCarla road classes.
2. **OpenDRIVE elevation representation + how CARLA consumes it.** The `<elevation>` cubic-polynomial records per road; how `carla::road::Lane::ComputeTransform` / the road model read them (carla/LibCarla/source/carla/road/Lane.cpp, ElevationProfile/road geometry). Confirm that injecting `<elevation>` makes both the generated road MESH and the waypoint z follow that profile.
3. **Does SUMO netconvert support elevation input?** Investigate netconvert options for elevation (e.g. `--osm.elevation`, heightmap/GeoTIFF import, junction/node z, `--osm.layer-elevation`). Can netconvert take an external elevation source, or must we post-process the `.xodr`? (Web research on SUMO netconvert + OpenDRIVE output + elevation.)
4. **Elevation injection path.** Concretely where/how to inject sampled heights into the `.xodr` `<elevation>` records as a post-conversion rewrite — in `CarlaNet.Map` (OsmConverter.cs, the OpenDRIVE parser/serializer) or a new pass. Does CarlaNet.Map have an OpenDRIVE *serializer* (write xodr) or only a parser? If only a parser, what would writing back entail?
5. **CarlaNet API surface + restructure.** Trace the current `generate_world_from_osm` flow (CarlaNet.Transport CarlaClient + the server RPC `generate_opendrive_world`) and `OsmConversionOptions`. Propose how to restructure into ordered passes that expose intermediate artifacts: e.g. Pass 1 = OSM→flat `.xodr` + extract bounds + road sample points (lat/lon); Pass 2 = (engine samples heights); Pass 3 = inject elevation into `.xodr`; Pass 4 = load world from the elevated `.xodr`. Specify the new/changed CarlaNet methods + how the Python API call should look.
6. **Already solved?** Any existing elevation hooks in CarlaNet/OsmConverter or CARLA's OpenDRIVE pipeline.

Primary files: carla/CarlaNet/src/CarlaNet.Map/OsmConverter.cs and the rest of CarlaNet.Map (OpenDrive parser, Road/Map.cs, InMemoryMap), carla/CarlaNet/python/test_osm_world.py, carla/CarlaNet/docs/OSM_Georeferencing.md, carla/CarlaNet/src/CarlaNet.Transport/CarlaClient.cs, carla/LibCarla/source/carla/road/ (Lane.cpp, MeshFactory.cpp, Map.cpp), workspace-root OSM2ODR_PORT_ANALYSIS.md and CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md (§4.3 elevation options). Use CarlaNet.md for API/wire details. WebSearch/WebFetch SUMO netconvert docs.

DELIVERABLE: write a thorough findings doc + your proposed ordered DATA sub-pipeline (with the exact dependency graph, the new/changed CarlaNet APIs, and how the (lat,lon)->height hand-off to the engine works) to g:\Projects\CarlaUE_5_7_4\.agents\research\dynworld_datapipeline.md. Then return a ~400-word executive summary, including the single biggest risk/unknown on the data side.
