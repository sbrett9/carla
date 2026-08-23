# Digital Twin feature port — reconciling CARLA's procedural map generation with this fork

**Date:** 2026-08-23 · **Status:** research complete, no code changed. Development plan in §16.
**Question asked:** the CARLA documentation describes an experimental digital-twin map-generation
mechanism; it was believed to have been stripped from the `ue5-dev` branch this fork started from, and
the goal was to establish what it would take to bring it back.
**Answer in one line:** it was never stripped from our fork — we carry 99.4 % of it — but the half that
ingests OpenStreetMap was deleted *upstream* in 2024, and the half we still have has been dormant
because nothing invokes it and two specific things block it.

**Relates to:** [02_CARLA_OSM_MapGen.md](02_CARLA_OSM_MapGen.md) (superseded in several places, §17),
[04_DynamicWorld_DataPipeline.md](04_DynamicWorld_DataPipeline.md),
[08_Layer_Architecture.md](08_Layer_Architecture.md) (§7 anticipates the building layer),
[17_Photoreal_Occlusion_Metric.md](17_Photoreal_Occlusion_Metric.md) (§5.2 amodal pass, §12),
[21_Road_Elevation_Profile_Continuity.md](21_Road_Elevation_Profile_Continuity.md) (§2 corrected here),
[CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md](CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md).
**Code under study:** `Unreal/CarlaUnreal/Plugins/CarlaTools/`,
`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/MapGen/`, `.../Carla/Traffic/DigitalTwinsTrafficLight.*`,
`Unreal/CarlaUnreal/Plugins/StreetMap/`.
**Survey basis:** HEAD `6de546975` on `feature/JNI-347-road-mesh-elevation-profile-continuity`; fork
point `4e081f3dc` (2026-05-08); `upstream` fetched 2026-08-23.

---

## 1. The premise, corrected

The working assumption was *"our derivative is missing the large majority of that digital twin code."*
That is **refuted**, and the correction matters because it changes the whole shape of the work.

| Measure | Result |
|---|---|
| CarlaTools source files, upstream `ue5-dev` vs ours | 42 / **40** |
| CarlaTools source lines, upstream `ue5-dev` vs ours | 9,199 / **8,380** |
| CarlaTools content assets (`.uasset` + `.umap`) | 72 / **72**, byte-identical blob SHAs |
| Our diff against the fork point across all digital-twin paths | **9 files, +41 / −11** |
| Files we are missing relative to upstream `ue5-dev` | **2** — `VehicleImporter.{h,cpp}` |

We deleted nothing. Our 41 inserted lines are UE 5.7 compatibility fixes (`FSavePackageArgs`,
`EAllowShrinking`, `CppCompileWarningSettings.ShadowVariableWarningLevel`, `PublicSystemIncludePaths`).
The two absent files are a **USD vehicle-asset importer, not map generation**, and they were not dropped
— upstream added them in `475c8b808` on 2026-05-26, eighteen days *after* we forked.

The code is missing **upstream**, not here. §4 names the commits.

---

## 2. What the feature actually is

"The digital twin" is not one tool. It is two independent editor tools plus a runtime consumer, and
conflating them has caused confusion in earlier notes.

**The on-road digital twin** — the thing the CARLA docs describe. Entry point is the Editor Utility
Widget `/CarlaTools/OnroadMapGenerator/UW_OnRoadMainWidget` (parent class `UDigitalTwinsBaseWidget`),
which drives `UOpenDriveToMap`. Downloads OSM for a bounding box, converts it to OpenDRIVE, generates
road and lane-marking meshes, a terrain grid, tree/prop marker actors, and tiles the result into a
`LargeMap`. 1,629 lines of C++.

**The off-road map generator** — a separate tool sharing the plugin, entry point `UWB_CARLA`
(`UMapGeneratorWidget`, 1,542 lines in one file). Generates synthetic landscapes from noise and
render-target heightmaps, with regions of interest, rivers and procedural foliage. It has nothing to do
with OSM and is not what was asked about, but it is the largest single file in the plugin and it shares
the tiling machinery.

**The building generator** — and this is the structural fact that reshapes the port. It is **not in the
CarlaTools C++ at all**. `BP_BuildingGen.uasset` is a Blueprint that calls
`UStreetMapImportBlueprintLibrary::ImportStreetMap(Path, DestinationAssetPath, OriginLatLon)` and reads
`FStreetMapBuilding` footprints from the **StreetMap plugin**, which parses `.osm` directly
(`StreetMap/Source/StreetMapImporting/Private/OSMFile.cpp:337-355` handles the `height` and
`building:levels` tags). It never touches OpenDRIVE, osm2odr or netconvert. It is triggered by manually
placing `BP_BuildingGenetratorTrigger` — the on-road widget does not invoke it.

**The runtime consumer** — `ADigitalTwinsTrafficLight` (`Carla/Traffic/`) and `AProceduralBuilding`
(`Carla/MapGen/`) are plain runtime actors in the shipped `Carla` module. `AProceduralBuilding` is
currently spawned by nothing.

---

## 3. Where the code lives — three-way ledger

| Metric | `upstream/ue4-dev` | `upstream/ue5-dev` | **ours (HEAD)** | `upstream/ue58-dev` |
|---|---:|---:|---:|---:|
| CarlaTools source files / lines | 32 / 7,478 | 42 / 9,199 | **40 / 8,380** | 48 / 9,814 |
| CarlaTools content assets | 72 | 72 | **72** (100 % identical) | 72 |
| Carla-plugin digital-twin source | 8 / 1,452 | 12 / 2,387 | **12 / 2,391** | 12+ |
| `osm-world-renderer/` | 12 / 532 | **0** | **0** | 0 |
| OSM2ODR build scripts | 4 / 475 | **0** | **0** | 0 |
| `PythonAPI/util/osm_to_xodr.py` | 106 | **0** | **0** | 0 |
| `Docs/adv_digital_twin.md` | 107 | **absent** | **absent** | absent |
| StreetMap linked into CarlaTools | yes | **commented out** | **commented out** | commented out |
| Runtime map generation in `AOpenDriveGenerator` | no | no | no | **yes (+1,958 lines)** |

Note that `ue5-dev`'s CarlaTools is a **superset** of `ue4-dev`'s, with substantive feature work through
November 2025 (`PoissonDiscSampling`, `MeshToSplineActor`, `CarlaToolsFunctionLibrary`, `CarlaSunSky`,
`MeshToLandscape`, `DigitalTwinsTrafficLight`). The UE5 line did not abandon the tool's engine half. It
abandoned the tool's **data ingestion** half.

---

## 4. What upstream removed, and when

All commit messages quoted verbatim.

| Removed | Commit | Date | Message |
|---|---|---|---|
| StreetMap module links in `CarlaTools.Build.cs` | `b423fe06e` | 2024-02-20 | *"Fix CMake PythonAPI build. **Disable OSM2ODR and OSM World Renderer.**"* |
| `Util/BuildTools/BuildOSM2ODR.{sh,bat}` | `f295f1469` | 2024-04-11 | *"Remove deprecated OSM2ORD UE4 build system (#7405)"* |
| `Util/BuildTools/BuildOSMRenderer.{sh,bat}` | `8378e059c` | 2024-04-17 | *"Remove osm render deprecated ue4 build system (#7458)"* |
| `osm-world-renderer/` (12 files, 532 lines) | `ad2a73f12` | 2024-09-27 | *"Remove not supported files"* |
| `Docs/adv_digital_twin.md` and two map tutorials | `7f26c281f` | 2024-11-27 | *"Docs/ue5 docs (#8411)"* |
| `PythonAPI/util/osm_to_xodr.py` | `206663130` | 2024-12-18 | *"Temporarily remove not working scripts"* |

Release 0.10.0 (2024-12-19) mentions **none** of this. The removal was silent.

Three features were also never ported forward from `ue4-dev` because they were added there *after* the
2023-11-28 divergence: `UOpenDriveToMap::ImportXODR()`, `ImportOSM()` and the `LocalFilePath` property
(`c0abf5998`, *"Aaron/digital twins add local file (#7167)"*). Their absence is why the UE5 tool has no
local-file entry point and only a URL one — a detail that matters directly to §16.

### 4.1 The feature's official status

The tool was **extracted into a separate repository**: `carla-simulator/carla-digitaltwins`, created
2025-05-07, default branch `ue5-digitaltwins`. It is a standalone UE5 editor plugin intended for a blank
UE5 project, whose output is then migrated into a CARLA project. It is **not wired into `carla@ue5-dev`
in any way**. VERIFIED via the GitHub API on 2026-08-23: last push **2025-10-21**, 20 open issues, not
archived, no commits in roughly ten months. Its two founding research tickets — #1 *"Research
feasability of OpenDRIVE for geometry gen"* and #2 *"Research OSM to XODR alternative to SUMO"* — have
never been touched. It pins Boost 1.84.0 exact, numpy < 2.0, PROJ 7.2.0 exact, XercesC 3.3.0 exact.

Maintainer statements, from the primary author of the tool:

- 2025-03-19, issue #8765, closed same day: **"UE5 Digital Twins is not supported"**
- 2026-04-02, issue #9565: **"DIgital Twin is experiemtal; Generating large metropolitan is an issue.
  Currently we are not developing it"**
- 2026-04-02, same issue, asked which version is most stable: *"0.9.16"* — i.e. the **UE4** line.

**Read:** deferred and unstaffed, not formally abandoned. There is no upstream partner to converge with
on the on-road tool, and no prospect of upstream fixing what it deleted. Anything we want, we own.

---

## 5. Present state in our tree: compiled, reachable, blocked

This is the finding that most changes the effort estimate. The feature is **not** dead code awaiting a
port. It builds today.

VERIFIED by build artefact, not by inspection:

- `Plugins/CarlaTools/Binaries/Win64/UnrealEditor-CarlaTools.dll` — 2.9 MB, built **2026-08-17 12:14**.
- The unity stubs under `Intermediate/Build/Win64/x64/UnrealEditor/Development/CarlaTools/` list **every**
  in-scope `.cpp` — `OpenDriveToMap`, `MapGeneratorWidget`, `ProceduralBuildingUtilities`,
  `GenerateTileCommandlet`, `ProceduralWaterManager`, `MapPreviewUserWidget`. None excluded.
- The `.sarif` diagnostics UBT wrote beside the objects show **zero warnings** for all three CarlaTools
  translation units.
- `UnrealEditor-StreetMapRuntime.dll` (2026-08-17) and `UnrealEditor-StreetMapImporting.dll` (2026-08-07)
  also exist, carrying the same `BuildId`. The StreetMap plugin **builds and loads**.
- `CarlaUnreal.uproject` lists `{"Name": "CarlaTools", "Enabled": true}`; the module is `Type: "Editor"`,
  so it is in the editor target and *cannot* affect the packaged server. `OpenDriveToMap.cpp` is wrapped
  `#if WITH_EDITOR` from line 5 to line 937.

There is exactly one live entry point and it is manual: Content Browser → Run Editor Utility Widget →
`UW_OnRoadMainWidget`. Nothing in `Scripts/`, `Util/`, `PythonAPI/`, `CarlaControl/`, `CarlaNet/` or CI
references the tool. No `Config/*.ini` registers it. That is why it has been invisible: it is enabled,
compiled and loaded, and simply never called.

---

## 6. The two blockers

### 6.1 OSM to OpenDRIVE is compiled out, un-enableable, and fails silently

The chain, VERIFIED line by line:

```cpp
// CustomFileDownloader.cpp:15-18 — the guard
#if defined(WITH_OSM2ODR) && __has_include(<OSM2ODR.h>)
  #define HAS_OSM2ODR
  #include <OSM2ODR.h>
#endif
```

`OSM2ODR.h` **exists nowhere in the tree** (verified by `find`), so the guard can never hold and
`UCustomFileDownloader::ConvertOSMInOpenDrive` falls to its `#else`, logging *"…disabled since SUMO's
OSM2ODR is not enabled"* and writing nothing.

The caller does not check:

```cpp
// OpenDriveToMap.cpp:146-155
void UOpenDriveToMap::ConvertOSMInOpenDrive()
{
  FilePath = FPaths::ProjectContentDir() + "CustomMaps/" + MapName + "/OpenDrive/" + MapName + ".osm";
  FileDownloader->ConvertOSMInOpenDrive( FilePath , OriginGeoCoordinates.X, OriginGeoCoordinates.Y);
  FilePath.RemoveFromEnd(".osm", ESearchCase::Type::IgnoreCase);   // unconditional
  FilePath += ".xodr";                                             // unconditional
  DownloadFinished();
  UEditorLoadingAndSavingUtils::SaveDirtyPackages(true, true);
  LoadMap();
}
```

`FilePath` is renamed to a `.xodr` that was never written, `LoadMap()` reads an empty string,
`OpenDriveParser::Load` returns no value, and the tool logs `Invalid Map` and stops. The `.osm` download
itself works fine. **A user running the tool sees a successful download followed by a bare "Invalid Map".**

Three further facts make the path un-enableable even in principle:

1. `CarlaTools.Build.cs:151-155` — `if (EnableOSM2ODR) { /* @TODO */ throw new NotImplementedException(); }`.
   Turning the flag on **fails the build**.
2. **A real build-script bug.** `CarlaTools.Build.cs:80-84` declares
   `Action<bool,string,string> TestOptionalFeature = (enable, name, definition) => { if (enable) PrivateDefinitions.Add(name); … }`
   and is called as `TestOptionalFeature(EnableOSM2ODR, "OSM2ODR support", "WITH_OSM2ODR")`. It adds the
   literal string `"OSM2ODR support"`, **not** `WITH_OSM2ODR`. The equivalent in `Carla.Build.cs:105-110`
   correctly uses `Add(definition)`. Because `CustomFileDownloader.cpp` lives in CarlaTools, the macro
   could never be defined there even with the throw removed. Not previously recorded anywhere.
3. `ENABLE_OSM2ODR=ON` fetches only PROJ and Xerces-C (`CMake/Dependencies.cmake:225-247`) — never the
   library. There is **no SUMO fetch anywhere in `ue5-dev`'s CMake**.

Adjacent dead ends found in the same sweep: `CMakeLists.txt:97-99` still contains
`add_subdirectory(osm-world-renderer)` for a directory deleted in 2024, so `BUILD_OSM_WORLD_RENDERER=ON`
is a hard CMake configure error; `MapPreviewUserWidget` opens a raw Boost.Asio socket to `127.0.0.1:5000`
expecting that deleted renderer, with `OpenServer()` an empty TODO and an unguarded throwing `connect()`
inside a `UFUNCTION` while `bEnableExceptions = true`; and `Content/Python/generate_tile.py` still reads
`os.environ["UE4_ROOT"]` and launches `UE4Editor`, so the multi-tile pipeline cannot start.

### 6.2 Content references — 150 unresolved, but 130 are one broken table

Resolution audit by byte-grepping every digital-twin `.uasset` for soft object paths and testing each
against a 44,005-package index of every mount point:

| Asset | Refs | Resolved | Unresolved |
|---|---:|---:|---:|
| `DT_BuildingStyles.uasset` | 78 | **78** | **0** |
| `BuildingStyleHolder`, `BP_BuildingGen`, `BP_Veg_Scatter`, `BP_RoofPropsGenerator`, `BP_OpenDriveToMap` | 13 | 13 | 0 |
| `LevelCreator.uasset` | 2 | 1 | 1 (`BP_InstancedMesh`) |
| `UW_OnRoadMainWidget.uasset` | 4 | 2 | 2 (road-painter materials) |
| **`DT_TreesGeneration.uasset`** | **138** | **3** | **135** |

The vegetation table is the entire problem, and **it is an inherited upstream defect, not a fork loss**:
`DT_TreesGeneration.uasset` is byte-identical to `upstream/ue5-dev`. Upstream's commit `b72dc90f2`
*"Changed the name and place of assets referenced in those BP and DT (#8864)"* repaired
`DT_BuildingStyles` for the UE5 content reorganisation and left `DT_TreesGeneration` pointing at the UE4
naming scheme (`SM_Maple_Base_M_v1..v8`). The current content branch uses `SM_Maple01_{L,M,S}_{A..H}` —
a structural match, 8 variants × 3 sizes against 8 versions × 3 sizes.

---

## 7. Assets — measured, not estimated

The suspicion that art was the missing mass is **not borne out**.

| Quantity | Measured |
|---|---|
| `Static/Building/` on disk | **5.17 GB / 3,786 assets** |
| `Static/Vegetation/` on disk | **4.85 GB / 1,269 assets** |
| Kit subset the digital twin actually wires up | 78 references, 9.6 MB direct |
| **Art that must be acquired** | **0 GB** |
| Optional legacy vegetation (if repointing is rejected) | 186 MB |
| Missing blueprint recoverable from `upstream/ue4-dev` | `BP_InstancedMesh.uasset`, 22,036 bytes |

Everything arrives through the existing content clone (`CarlaSetup.ps1:538`). Nothing is legally or
practically blocked.

One observation with consequences for later work: selection is **DataTable-only** — there is no
asset-registry directory scan anywhere in the CarlaTools sources; the only `FAssetRegistryModule` uses
are `AssetCreated()` notifications after a mesh is generated. Every generated building and scattered prop
comes from exactly two DataTables. The kit on disk is roughly an order of magnitude larger than what
those tables reference (704 building pieces in `Town13_15` alone, 610 roofs, 60 props against 78 wired
references). **Extending variety is DataTable authoring, not asset acquisition.**

---

## 8. UE 5.7.4 portability

The compile question is settled empirically by §5. What remains is whether the tool still produces a
*correct* map. Assessed against the engine tree at `UE_5_7_4/` and compared with `UE_5_5_4/`.

| Subsystem | Verdict |
|---|---|
| ProceduralMeshComponent, `BuildFromMeshDescriptions`, `FStaticMeshOperations` | compiles as-is, signatures unchanged |
| `IMeshMergeUtilities::MergeComponentsToStaticMesh` | byte-identical signature |
| `UPackage::SavePackage` → `FSavePackageArgs` | already ported (`2315d9d96`) |
| `ALandscapeProxy::Import` | already ported; **behaviour changed** — see below |
| PCG (`UPCGSettings`, `UPCGPointData`, `IPCGElement`) | present in 5.7.4; `UPCGPointData` carries `UE_DEPRECATED(5.6)` and migrates to `UPCGBasePointData` at 5.9+ |
| `RHIUpdateTexture2D`, `UTexture2D::CreateTransient` | present, not deprecated |
| HTTP, AssetTools, AssetRegistry, Blutility, UMG | unchanged |
| StreetMap plugin (4,666 lines, 2 modules) | compiles; 13 `C4996` deprecations that become errors at UE 5.8 |
| **Foliage cooking** | **blocked, and it was blocked upstream** |

**Two findings deserve emphasis.**

**Vegetation cooking is dead code, disabled by upstream during its own UE5 port.**
`MapGeneratorWidget.cpp:1269-1278` wraps `FEdModeFoliage::AddInstances(...)` in `#if 0`, blamed to
upstream commit `56227dec7` *"Fix Windows build errors. (#8381)"*. The cause is that `FEdModeFoliage`
lives in `Editor/FoliageEdit/Private/FoliageEdMode.h:321` with no export macro — in **both** 5.5.4 and
5.7.4, so this is not a 5.7 regression. The consequence is that the off-road generator's vegetation
cooking produces **zero instances** today, and would look like a port failure if tested. A public
replacement exists: `AInstancedFoliageActor::AddInstances` (`Runtime/Foliage/Public/InstancedFoliageActor.h:286`).

**5.7 deprecated non-edit-layer landscapes.** `LandscapeEdit.cpp:3821-3823` now calls
`CreateDefaultLayer()` when both the actor's edit layers and the import layers are empty — which is
exactly how `MapGeneratorWidget` calls `Import`. Generated landscape tiles silently acquire an edit layer
they did not have on 5.5, changing per-tile heightmap/weightmap texture footprint and routing writes
through the edit-layer merge path. Functionally handled; needs measuring before the off-road tool is
trusted.

### 8.1 One engine gap that touches this work

Our UE 5.7.4 port carries CARLA's engine patches, and the digital-twin path needs no engine change we
failed to port. But one **half-ported** patch is directly relevant, VERIFIED in our engine tree:

```
UE_5_7_4/Engine/Source/Developer/NaniteBuilder/Private/Cluster.h:255
    TArray< int32 > ExternalEdges;                                          <- widened, correct

UE_5_7_4/Engine/Source/Developer/NaniteBuilder/Private/Cluster.cpp:658
    TMap< TTuple< FVector3f, FVector3f >, int8 > LockedEdges;               <- still int8
Cluster.cpp:673  LockedEdges.Add( MakeTuple(...), (int8)ExternalEdges[ EdgeIndex ] );   <- narrowing cast
Cluster.cpp:721  int8* AdjCount = LockedEdges.Find( Edge );
```

CARLA's engine fork widened **both**; we widened only the array. `FCluster::Simplify` round-trips
adjacency counts through that map, so a count at a multiple of 256 truncates to 0 and 128–255 flip sign.
It bites on high-triangle merged geometry — precisely what procedural city buildings and Nanite photoreal
tiles produce — and it fails **quietly**, as bad LODs rather than an error. Three lines plus a rebuild.

### 8.2 World Partition — not the blocker it appears to be

The legacy streaming model that CARLA's tiling depends on is fully intact in 5.7.4. VERIFIED:
`Classes/Engine/WorldComposition.h` is **identical** between 5.5.4 and 5.7.4; `WorldComposition.cpp`
differs by 2 lines; `LevelStreaming.h` differs by 27 lines, all additive; `WorldSettings.h:400`
`bEnableWorldComposition` is at the same file and same line in both.

More to the point, `ALargeMapManager` delegates tiling to **neither** engine model. It enumerates tiles
by asset-name convention through `UObjectLibrary` (`LargeMapManager.cpp:391-434`), constructs each
`ULevelStreamingDynamic` by hand (`:668`), and registers them via `World->AddStreamingLevel()` (`:478,485`)
— the correct API. It fetches `World->WorldComposition` at `:663` and never uses it. The tiling is
CARLA's own machinery layered on the one part of the engine that did not move.

And upstream has already written the migration: `upstream/ue58-mapgen-features` commit `37bfb26ac` adds
`CarlaLargeMapConvertCommandlet`, a 121-line subclass of Epic's `UWorldPartitionConvertCommandlet`, and
every engine API it uses exists in 5.7.4 with a matching signature. **Recommendation: stay on legacy
streaming; take the converter as a bake-time step if and when World Partition is wanted.** The trap to
budget for is not the conversion — it is that a World Partition world retires `ALargeMapManager`, making
every global/local rebasing call the identity, which changes a coordinate contract our telemetry and
Cesium georeference paths assume.

---

## 9. Overlap with what this fork already built

| Capability | Digital twin | This fork | Verdict |
|---|---|---|---|
| OSM download | `UCustomFileDownloader` HTTP GET, Overpass URL embedded in the widget | nothing; 15 curated `.osm` files in `Import/` | new, low value (~20 lines of Python) |
| OSM → OpenDRIVE | in-process osm2odr (dead, §6.1) | offline SUMO `netconvert` v1.27.0 via `OsmConverter.cs`, plus `osm_clip.py` boundary clipping | **redundant and conflicting** |
| Road mesh | editor bake to `UStaticMesh`, **adds synthetic sine height** on top of the profile | runtime `AProceduralMeshActor` from `Map::GenerateChunkedMesh` | **conflicting** |
| Lane markings | `GenerateLaneMarks` → `MeshFactory::GenerateLaneMarkForRoad` | none — our roads have no painted markings | **genuinely new, medium value** |
| Terrain / landscape | `CreateTerrain(12800,256)` grid, or a real `ALandscape` | Cesium photoreal + world-terrain tilesets, `ADrapedTerrainActor` Chaos heightfield | **conflicting** |
| Tiling / large map | `LargeMapManager` + `GenerateTileCommandlet` | none; one ephemeral world per run | **conflicting** |
| **Buildings** | `BP_BuildingGen` → StreetMap footprints → `AProceduralBuilding` HISM + 5.2 GB kit | **nothing** | **genuinely new — the prize** |
| **Vegetation** | `Map::GetTreesTransform` road-adjacent markers → `BP_Veg_Scatter` | nothing (`AVegetationManager` is hard-gated on LargeMap, inert) | **genuinely new** |
| Traffic lights / signs | bakes meshes + `map_logic.json` | `SignInjector`, `TrafficLightInjector` writing OpenDRIVE signals and per-phase controllers | **redundant, and a live footgun** |
| Water | `UProceduralWaterManager` | nothing; water is photoreal pixels | redundant by irrelevance |
| Map preview UI | TCP to a deleted renderer service | none | new but unusable |
| Level / asset creation | bakes persistent `.umap`/`.uasset` per area | ephemeral world per run from a `.xodr` string | conflicting in philosophy |

Eight of eleven capabilities are redundant or inferior to what we have. **Two are genuinely wanted.**

---

## 10. The prize, and why it is worth taking

**Buildings.** An exhaustive grep across `CarlaNet/src`, `CarlaNet/python`, `CarlaControl/src` and
`CesiumCarlaBridge` returns only enum declarations (`ObjectLabel.Buildings`, `MapLayer.Buildings`) and
comments where buildings are something to *reject* (`DrapeTerrain.cs:214,237`, `ElevationInjector.cs:809`,
`GradeSeparation.cs:52`). We have no building geometry of our own; buildings are photoreal pixels.

Three things make this more attractive than it first appears:

1. **The feed is a two-argument handoff.** `ImportStreetMap(Path, DestinationAssetPath, OriginLatLon)`
   wants an `.osm` path and a pinned origin. We already produce both — the clipped `.osm` and the
   `(lat, lon)` we pin to world origin. No converter change, no OpenDRIVE involvement.
2. **Semantic tagging is free.** `ATagger::GetLabelByPath` splits the asset path on `/` and reads token
   index 4. `DT_BuildingStyles` references `/Game/Carla/Static/Building/…` → token 4 is `Building` →
   `CityObjectLabel::Buildings`. `UHierarchicalInstancedStaticMeshComponent` derives from
   `UStaticMeshComponent`, so `ATagger::TagActor` already covers it. Zero new code.
3. **It answers a constraint we already recorded.** Photoreal tiles carry no semantic label and no
   instance id — they stream outside `ATagger` entirely. A procedural building is a tagged actor that
   appears in `get_environment_objects(Buildings)` with a bounding box. And
   `CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md` §1 records the hard constraint that Google's terms bar
   building training data from those tiles. **A labelled OSM-footprint building layer over Cesium World
   Terrain is exactly the open-content, own-rights, semantically-truthed stack that constraint demands**,
   and the one configuration where the photoreal layer can be switched off without losing the scene.
   Doc [08](08_Layer_Architecture.md) §7 already names this as future work; the digital twin supplies the
   implementation plus a 5.2 GB facade library.

Coverage argues the same way. The `Import/` set is telling — `IRAN.osm`, `Iran_Route_7.osm`,
`Hormuz_Trunk_Highway_*.osm`, `Qeshm_Bridge.osm`. Photoreal coverage in those areas is absent or
low-detail. Procedural buildings are the only vertical structure available there.

Secondary items worth harvesting, in order: **lane markings** (`MeshFactory::GenerateLaneMarkForRoad`
exists and the runtime path never calls it — caveat: only `Solid` and `Broken` produce geometry, the
other eight styles are no-ops that merely advance `s`); **road-adjacent vegetation**
(`Map::GetTreesTransform`, already in LibCarla, already regression-tested upstream by
`test_road_regression_9565.cpp`, needs no landscape); and **impostor/LOD baking**
(`GenerateImpostorTexture`/`GenerateImpostorGeometry`, directly relevant to thousands of buildings seen
from altitude).

---

## 11. Guard rails — five ways this breaks us

Each verified against our code. These are the reason a naive "turn the tool on" would be destructive.

1. **Never write a `map_logic.json` beside a generated `.xodr`.**
   `ATrafficLightManager::InitializeTrafficLights` (`TrafficLightManager.cpp:384-412`) tests for that file
   and, if present, takes `RemoveRoadrunnerProps(); SpawnSignals();` **instead of**
   `GenerateSignalsAndTrafficLights()`. Nothing in our tree writes it today, which is the only reason our
   signal work runs. Emitting one silently bypasses `SignInjector` and `TrafficLightInjector`.
2. **Never add an `ALargeMapManager` to `OpenDriveMap.umap`.**
   `ACarlaGameModeBase::InitGame` (`CarlaGameModeBase.cpp:93-102`) finds one and calls `GenerateLargeMap()`
   on sight. Origin rebasing past 2 km (`LargeMapManager.cpp:74,937-941`) would strand
   `ADrapedTerrainActor` and `AStagingBoundsActor`, both anchored once in absolute centimetres at build
   time with no `PostWorldOriginOffset` handler.
3. **Never run `CreateTerrain` / `CookVegetationToWorld` / `GenerateWater` in a Cesium world.**
   Each assumes it owns the ground. `UMapGenFunctionLibrary::CreateMesh` sets
   `bBuildSimpleCollision = true` and `AStaticMeshActor` defaults to `BlockAll`, so the digital twin's
   synthetic terrain would stand up a second `ECC_WorldStatic` collision surface alongside our drape, at a
   different height. Worse, the tool's own line traces (`GetSnappedPosition`, `GetHeightForLandscape`)
   would hit *our* drape and seat props wrongly. Nothing reconciles them: the layer system is string tags
   on `ACesium3DTileset` plus string dispatch in `CarlaServer.cpp:614-681`, and a landscape actor is
   neither a tileset nor an `AProceduralMeshActor`, so `set_layer_collision` cannot even address it.
4. **Never let `OpenDriveToMap::GenerateRoadMesh` touch a production `.xodr`.**
   Lines `:558`, `:565` and `:668` do `Vertex.z += GetHeight(...)`. With no `DefaultHeightmap` assigned,
   `GetHeight` returns `carla::geom::deformation::GetZPosInDeformation` —
   `0.6·sin(0.035x − 0.08y + 1000) + 1.1·sin(0.02x + 0.05y − 1500)`, about **±1.7 m of rolling sine
   noise** (`Deformation.h:21-37`). It would corrupt the PCHIP-fitted HAE profile that
   [21](21_Road_Elevation_Profile_Continuity.md) §19 measured to 0.000 m link mismatch. The tool also has
   no datum concept at all, and recentres every mesh on its own centroid with per-tile local origins
   (`:389-404, :588-601, :626`), against our single absolute frame anchored at (0,0).
5. **If baking meshes, bake to a path whose fifth token is a tagger key.**
   `UMapGenFunctionLibrary::CreateMesh` writes to `/Game/CustomMaps/<Map>/Static/<Folder>/…`, whose token 4
   is `Static` → `CityObjectLabel::Static`, not `Buildings`. Set an explicit component tag instead —
   `ATagger` prefers `ComponentTags[0]` (`Tagger.cpp:180-184`).

A sixth, for instance-level truth: one HISM per unique mesh means `SetStencilValue` writes one
`(ActorID, Label)` per *component* (`Tagger.cpp:80`), so every instance of a mesh shares an id. Semantic
segmentation is fine; **instance** segmentation is not. Cooking each building to a single `UStaticMesh`
and spawning one actor per building restores per-building ids.

And for the occlusion metric: the current depth-based estimator is segmentation-agnostic and survives
untouched, but doc [17](17_Photoreal_Occlusion_Metric.md) §5.2's planned amodal pass gets its mask by
`set_layer_visible("photoreal", False)`. **Any building layer must be independently toggleable and
included in that hide list**, or the "amodal" mask stays occluded and the metric silently under-reports.

---

## 12. The other prize: upstream's runtime map generation

Separately from the on-road editor tool, `upstream/ue58-mapgen-features` (59 commits ahead of `ue5-dev`,
tip 2026-08-09) moves procedural map generation **out of the editor and into the runtime server**.
VERIFIED: `Carla/OpenDrive/OpenDriveGenerator.cpp` goes from **185 lines on `ue5-dev` to 2,143 lines**
there. `AOpenDriveGenerator` — an ordinary `AActor` with `BeginPlay`, the same class our world generation
already uses — gains:

```
GenerateGroundPlane, GenerateMedianFill, GenerateCrosswalkMesh, GenerateLaneMarkings,
FRoadSurfaceRaster{Initialize, RasterizeTriangle, BuildDistanceFields}, SampleGroundGridHeight,
GenerateFurnitureAnchors, RunGenerationQA, + UPCGComponent street-furniture graph
```

Its own changelog: *"real ground plane and terrain heightfield following road elevation, crosswalk
zebras, lane markings through splice junctions, OpenDRIVE `lateralProfile <shape>` support,
median/carriageway gap filling, PCG street furniture with working night lighting, and a new
`spawn_custom_mesh` RPC"*. Commit titles include *"Weld at-grade stacked road surfaces"*, *"Ground
heightfield: terrain follows road elevation on generated maps"* and *"Median fill and under-road blanket:
close carriageway gaps on real-world maps"*.

**It is portable to our UE 5.7.4 tree.** VERIFIED: `git grep 'UE_5_8\|ENGINE_MINOR_VERSION'` across that
branch's `CarlaTools` and `OpenDrive` trees returns **nothing**. The UE 5.8 port commit `68cbbc690` is 20
files / +89 −52 across the whole plugin set, and its `CarlaToolsFunctionLibrary.cpp` hunk is
character-equivalent to the `FSavePackageArgs` fix we already wrote independently for 5.7.

This lands directly on live work — JNI-347 road-mesh elevation continuity, the drape terrain, and the
junction retriangulation planned in doc 21 §18. It is, on the evidence, **more valuable to this fork than
the on-road digital twin itself**, and it is the single largest reason not to sink effort into the legacy
editor path.

Do **not** take that branch's `rt_lens`/DLSS commits — they require engine-fork additions
(`FPostProcessSettings::PathTracingLens*`, `bRequiresSegmentationPass`, `CaptureScreenPercentage`) absent
from 5.7.4.

---

## 13. On osm2odr: do not port it

Beyond the cost (~463 files / ~5.8 MB of 2020-era SUMO C++, plus static Xerces-C and PROJ linked into a
UE module with `/MD` vs `/MT` and RTTI/exception ABI to reconcile), there is a **decisive structural
argument**:

`osm2odr::ConvertOSMToOpenDRIVE()` returns **one OpenDRIVE string**. It cannot emit the SUMO `.net.xml`.
`TrafficLightInjector.InjectTrafficLights(elevatedXodr, sumoNet)` consumes `<tlLogic>` phase programs from
that `.net.xml`, which `OsmConverter` requests with `--output-file`. **Our traffic-light pipeline is
structurally impossible through the osm2odr API.** Adopting it would reopen issue #1 (per-junction
grouping and log spam).

Adopting it would also forfeit the measured `--junctions.join` / `--tls.discard-loaded` pairing, the
car-drivable filters, `--geometry.remove`, `--roundabouts.guess`, and the fully specified tmerc string
with `--offset.disable-normalization`. Our converter is SUMO v1.27.0 (2026-05-20); osm2odr's fork is
frozen at 2024-07-23.

If in-editor one-click OSM import is ever wanted, the answer is a **~40-line shim**, not a port: a
CarlaTools-local `OSM2ODR.h`/`.cpp` that writes the OSM to a temp file, maps the 11-field settings struct
onto the flag set already proven in `OsmConverter.BuildArguments`, launches the `netconvert` we already
build and ship, and reads the `.xodr` back. That satisfies `CustomFileDownloader.cpp:50-53` verbatim with
no SUMO, PROJ or Xerces linkage inside Unreal, and lights up the existing `__has_include` guards.

---

## 14. Licensing

| Artifact | Licence | Obligation |
|---|---|---|
| CARLA code | MIT | notice |
| CARLA content pack | CC BY 4.0 (`Content/Carla/LICENSE`) | attribution; commercial use permitted |
| Generated `.xodr` | **ODbL Derivative Database** | §4.4 ODbL-or-compatible, §4.6 machine-readable copy offer, attribution |
| Rendered imagery / video | ODbL **Produced Work** | §4.3 notice only — "© OpenStreetMap contributors" |
| Internal-only use | — | §4.5 exempts internal use from share-alike |
| SUMO `netconvert` binary | EPL-2.0 | notice + source offer; separate-process invocation keeps our code outside file-level copyleft |
| `CarlaControl/` | SNC proprietary | **exclude from any external distribution** |

**The trap:** a packaged CARLA build ships the map's OpenDRIVE, because the server needs it at runtime for
the road graph. Shipping the product therefore ships a Derivative Database of OSM, so §4.4/§4.6 apply —
not merely the §4.3 produced-work notice. Mitigation is to keep it internal (§4.5), or to dual-track a
proprietary product alongside a separately published ODbL road-data drop.

Overpass is alive and healthy (checked 2026-08-23: `/api/status` reports 2 slots available, a live probe
returned HTTP 200). Policy is roughly 10,000 requests and under 1 GB per day per IP. Our downloader sends
**no User-Agent, has no timeout, no retry, and on a non-2xx returns without firing its completion
delegate** — a silent editor stall on a 429. Since CARLA's own guidance is to keep the file under 1 GB, a
single large pull can consume the daily quota.

The building and vegetation kit references only `/Game/Carla/Static/…` — no Quixel, Megascans, Fab or
marketplace paths anywhere. CC BY 4.0 is nonetheless asserted by a single four-line file with no
per-asset provenance and no credits manifest across the 40 GB pack, and the pack includes branded vehicle
meshes whose trademark exposure CC BY does not cure. Irrelevant to the building kit specifically;
relevant to shipping the simulator.

---

## 15. Verdict

**The goal is achievable, and it is smaller than expected — but the thing worth doing is not the thing
that was asked for.**

Restoring the on-road digital twin end to end is roughly **12–18 developer-days**, and essentially none of
that is engine-API porting. Almost all of it is repairing what upstream broke during its own UE4→UE5 port
(the tile launcher, foliage cooking, the vegetation DataTable) plus validation.

But the tool's own output — synthetic sine terrain, a datum-free local frame, per-tile baked assets, its
own uncoordinated ground collision — is *worse than what we already have* on every axis except two. The
honest recommendation is therefore **not** to restore the tool as a tool. It is to:

1. **harvest the two capabilities we lack** — buildings and vegetation — feeding them from our existing
   clipped OSM and pinned origin, as a runtime layer rather than an editor bake; and
2. **take upstream's runtime map-generation work** (§12), which is more valuable to this fork than the
   digital twin itself and lands directly on work already in flight.

The editor tool stays enabled and compiled where it is. It costs nothing, it pins several LibCarla
functions alive, and it remains available for one-off experiments — provided the guard rails in §11 hold.

---

## 16. Development plan

Ordered so that each step is independently verifiable and nothing is blocked on the step after it.
Every item leaves existing capability intact.

### Step 0 — make the dormant tool honest (0.5–1 day, no dependencies)

Cheap, self-contained, and it stops the next person losing a day to a silent failure.

1. Make the dead OSM path fail loudly: have `UOpenDriveToMap::ConvertOSMInOpenDrive` check that the
   `.xodr` exists before renaming into it and calling `LoadMap()`, and surface the real reason.
2. Fix `CarlaTools.Build.cs:83` — `PrivateDefinitions.Add(name)` → `Add(definition)`, matching
   `Carla.Build.cs:108`. It is a latent trap regardless of whether OSM2ODR is ever enabled.
3. Guard `UOpenDriveToMap::OpenFileDialog`'s `FSlateApplication::GetActiveTopLevelWindow()` against null
   so it cannot hard-crash a commandlet, and wrap `UMapPreviewUserWidget`'s throwing `connect()`.
4. Delete or comment the dangling `add_subdirectory(osm-world-renderer)` at `CMakeLists.txt:97-99`, which
   makes `BUILD_OSM_WORLD_RENDERER=ON` a configure error for a directory removed upstream in 2024.

**Verification:** running the widget produces a clear diagnostic instead of `Invalid Map`.

### Step 1 — prove the generator on a real `.xodr` (1–2 days)

`UOpenDriveToMap::LoadMap()` is `BlueprintCallable`. Point `FilePath` at a netconvert-produced elevated
`.xodr` from `Build/sumo-smoketest/` and call it directly, bypassing the dead conversion entirely. Use
`GenerateTile()` in-process rather than `GenerateTileStandalone()`, which needs the broken Python launcher.

This is the cheapest possible answer to "does the downstream generator still work on 5.7.4", and it needs
no osm2odr, no StreetMap and no new code.

**Guard rail:** do this on a scratch map, never a production one — §11 items 3 and 4 apply in full. The
point of this step is diagnosis, not a map we keep.

**Verification:** road and lane-mark meshes appear with correct plan geometry. Expect the vertical to be
wrong by ±1.7 m of sine noise; that is the confirmation of §11 item 4, not a defect to fix here.

### Step 2 — Cesium OSM Buildings as a fourth tileset layer (1–2 days)

Add ion asset 96188 as a third `EnsureTileset` call in `CesiumHeightSampler.cpp` tagged `"buildings"`, plus
one client flag. The string dispatch in `CarlaServer.cpp:649` already forwards unknown layer names, so
`set_layer_visible("buildings", …)` works the moment the tileset is tagged.

This is doc [08](08_Layer_Architecture.md) §7's plan of record, it exercises the N-layer generality the
layer system was designed for, it puts vertical structure into zero-coverage areas immediately, and it
gives an honest baseline against which to judge procedural buildings. It gets no semantic labels — which
is precisely the argument for Step 4.

**Verification:** buildings stream and toggle like the other layers; occlusion metric unchanged.

### Step 3 — repair the vegetation DataTable and the stray content references (1 day)

Re-point `DT_TreesGeneration`'s 6 rows at vegetation already on disk (`Vegetation/Trees`,
`Vegetation/Bushes`) rather than importing the 186 MB of UE4-era assets. Recover `BP_InstancedMesh.uasset`
from `upstream/ue4-dev`. Substitute the two missing road-painter materials with the present
`GenericMaterials/Roads/MI_Road_Asphalt_A/B`, or set the `UPROPERTY` on `BP_OpenDriveToMap` directly.

Cheap, removes 135 of the 150 unresolved references, and is a prerequisite for any vegetation work.

**Verification:** the resolution audit in §6.2 re-run shows the unresolved count in single digits.

### Step 4 — runtime procedural building layer (the substantive work)

The real deliverable. Structure it exactly like the proven `build_draped_terrain` path — client computes,
engine consumes — so it fits the existing world-per-run model and works headless in the packaged server.

1. **Footprint reader** in `CarlaNet.Map`, shaped like `SignInjector.cs:131-169` (streaming `XmlReader`
   over the clipped `.osm`), emitting `{polygon, height, levels, category}`. Port the tag handling from
   `OSMFile.cpp:337-355`. This must be ported rather than reused: the StreetMap importer is an Editor
   module that creates a `UStreetMap` asset and is unreachable from a packaged server.
   **Carry forward:** `osm_clip.py` discards every `<relation>` (`:131-146`), so multipolygon
   courtyard/donut footprints are lost by the clip. Either feed the unclipped `.osm` or teach the clipper
   to carry relations — the same gap doc 19 records for turn restrictions.
2. **`spawn_buildings` RPC**, structurally identical to `build_draped_terrain`
   (`CarlaServer.cpp:703-723`).
3. **Engine-side spawner** reusing `AProceduralBuilding` (`Carla/MapGen/ProceduralBuilding.h`) — already
   in the runtime plugin, currently spawned by nothing — with a runtime-loadable style table. Seat
   buildings from the drape grid or `SampleHeightMostDetailed` against the `ground` layer, **never** a
   line trace. Note the latent null-dereference at `ProceduralBuilding.cpp:30-32` before relying on it.
4. **Layer plumbing:** register `"buildings"` in the `CarlaServer.cpp:614-681` dispatch and add it to the
   amodal hide-list in the recorder (§11).
5. **Instance truth:** cook each building to a single `UStaticMesh` and spawn one actor per building if
   per-building instance ids are wanted; the HISM path gives semantic labels only.

**Verification:** buildings appear in `get_environment_objects(Buildings)` with bounding boxes; semantic
segmentation labels them `Buildings` with no new tagging code; toggling `"buildings"` off restores the
current scene exactly.

### Step 5 — road-adjacent vegetation (follows Step 4)

Expose `Map::GetTreesTransform` through a `spawn_vegetation` RPC. It is already in LibCarla, already
regression-tested upstream (`test_road_regression_9565.cpp`, from issue #9565), and needs only the road
graph and a height source — no landscape. Meshes drawn from `/Game/Carla/Static/Vegetation/…` auto-tag as
`Vegetation`.

Explicitly **not** `UProceduralFoliageSpawner`/`AProceduralFoliageVolume`: those need an `ALandscape` we
do not have and must not add, and their cooking path is the `#if 0` dead code of §8.

### Step 6 — evaluate upstream's runtime map generation (§12) as a separate work item

Assess `upstream/ue58-mapgen-features`'s `AOpenDriveGenerator` expansion against our tree — specifically
`GenerateGroundPlane`, `SampleGroundGridHeight`, the road-surface raster with distance fields, median
fill, crosswalk zebras and lane markings. It is engine-version-agnostic and lands on JNI-347 and the drape
work. This deserves its own findings document rather than being folded into the digital-twin port.

### Fix independently of all of the above

**The Nanite `LockedEdges` narrowing** (§8.1). Three lines in `Cluster.cpp:658,673,721` plus a rebuild. It
is unrelated to the digital twin, it silently corrupts LODs on exactly the high-triangle geometry both
photoreal tiles and procedural buildings produce, and it should not wait on any of this.

### Explicitly not doing

- **Porting osm2odr** (§13) — structurally incompatible with our traffic-light pipeline.
- **Reviving `osm-world-renderer`** — deleted upstream, Linux-only, replaced by a URL field.
- **The off-road `MapGeneratorWidget`** — needs an `ALandscape`, has dead foliage cooking, and generates
  synthetic terrain we do not want.
- **`map_logic.json` emission**, **`ALargeMapManager` in `OpenDriveMap.umap`**, **DT terrain generation**,
  and **`GenerateRoadMesh` on a production `.xodr`** — §11.
- **Adopting the `carla-digitaltwins` standalone plugin** — a separate blank UE5 project, Boost 1.84
  exact, numpy < 2.0, idle since 2025-10-21, then asset migration. Strictly more fragile than what we have.

---

## 17. Corrections to existing documents

These are recorded here rather than silently edited, because other work reads them as ground truth.

**[21_Road_Elevation_Profile_Continuity.md](21_Road_Elevation_Profile_Continuity.md) §2 — the flatness
fast-path does exist.** Doc 21 states that no elevation-flatness check exists and that emitting non-zero
`c`/`d` "changes vertex heights and nothing else". VERIFIED otherwise:
`Lane::IsStraight()` (`LibCarla/source/carla/road/Lane.cpp:88-94`) tests
`|elevation.GetPolynomial().GetC()| > 0 || |…GetD()| > 0`, and it gates a genuine two-vertex shortcut at
`MeshFactory.cpp:77-82` (*"If the lane is straight just add vertices at the begining and at the end"*),
plus `MeshFactory.cpp:395`, `:446` (wall generators) and `Map.cpp:985` (waypoint R-tree). Doc 04's
original `MeshFactory.cpp:88-93` citation appears to be a filename typo for `Lane.cpp:88-93`.
**Consequence — measured, and it favours the fit.** `ElevationFitMode.MonotoneCubicHermite` emitting
curvature takes those lanes off the fast path, but losing it **repairs a defect rather than costing
anything**. `IsStraight` tests only `c` and `d`, never `b`, so before the fit a road carrying many
piecewise-*linear* elevation records still counted as straight, received two vertices for the whole lane,
and discarded its entire elevation profile. Measured chord error between the two-vertex ribbon and the
profile it was meant to follow: 0.62 m max on `Arapahoe_I25`, 1.44 m on `SF_LaurelHeights`, 1.68 m on
`Gardnerville` (a 402 m lane with 42 elevation records rendered as a single quad), and 26.2 m on
`wrigley`. On the production resolved-surface path the added triangle cost is **+0.36 %** (7,198 of
1,977,146). The fast path should be considered for deletion rather than restoration, and `Map.cpp:985`
reviewed for the same `b`-blind test.

**[02_CARLA_OSM_MapGen.md](02_CARLA_OSM_MapGen.md)** needs four corrections:
- §"performs the actual call" — the cited `CustomFileDownloader.cpp` code is inside a guard that can never
  hold (§6.1). Doc 21 §"Sources" already contradicts doc 02 correctly.
- "`WITH_OSM2ODR` is set in Carla.Build.cs and CarlaTools.Build.cs" — wrong three ways: the flag defaults
  false, `Options.def` is empty, and the `Add(name)` bug means it could never be defined in CarlaTools.
- "the StreetMap plugin is disabled" — misleading. `ENABLE_STREETMAP` defaults **ON**, the plugin is
  `EnabledByDefault: true`, both DLLs are built with the current `BuildId`, and `BP_BuildingGen` uses it
  through Blueprint. Only CarlaTools' C++ link against it is commented out.
- "osm2odr is an external library, not vendored" — superseded. SUMO is now in-tree at `Build/sumo-src/`
  and `netconvert` is shelled out to by `CarlaNet.Map.OsmConverter`.
- Its CarlaTools inventory is incomplete: it treats the plugin as two files and never mentions
  `MapGeneratorWidget` (1,542 lines), `DigitalTwinsBaseWidget` (the actual named entry point),
  `RegionOfInterest`, `ProceduralWaterManager`, `MapPreviewUserWidget`, `GenerateTileCommandlet`, or the
  83-file content tree.

**[CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md](CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md)** — its
"flatten Cesium", CarlaNet-georeference-gap, hidden-collision and `carla.Osm2Odr.convert()` sections are
all superseded by the elevation injection, `Geodesy.cs`, the layer architecture and the netconvert path.
Its §1 constraint on Google tile terms remains live and is load-bearing for §10 above. Its statement that
CARLA "never generates buildings" should be narrowed: true of `AOpenDriveGenerator`, `MeshFactory` and
`OpenDriveToMap::GenerateAll`; false as a blanket claim, given `ProceduralBuildingUtilities`,
`AProceduralBuilding`, `BP_BuildingGen` and an intact 5.2 GB kit.

---

## 18. Open questions

- Whether CarlaTools survives a **Linux Installed-Build, non-unity** compile. All build evidence here is
  Windows, source-engine, unity. The four enumerable include-leakage hazards are already covered, but that
  is not proof — the box compile remains the oracle.
- The node-level behaviour of `BP_OpenDriveToMap`, `BP_BuildingGen`, `LevelCreator` and
  `UW_OnRoadMainWidget`. All Blueprint analysis here was done by byte-grepping asset strings, not by
  opening graphs.
- Whether the landscape edit-layer change (§8) measurably alters generated terrain versus 5.5 — needs a
  tile generated and compared, not reasoned about.
- Whether `37bfb26ac` cherry-picks cleanly. Divergence was measured; no trial merge was run.
- `UWB_CARLA.uasset` and two base maps are **UE 4.27-era** packages (file version 522, pre-LWC) never
  resaved in UE5. They load and upgrade, but the first resave is a one-way conversion.

---

## Sources

- OpenDRIVE 1.4 §5.3.5 road elevation.
- Open Database License (ODbL) 1.0 §4.3–§4.6 — produced works, derivative databases, internal use.
- OpenStreetMap Foundation Overpass API usage policy; `overpass-api.de/api/status` probe, 2026-08-23.
- Eclipse Public License 2.0 (SUMO).
- `carla-simulator/carla` issues #8175, #8765, #9199, #9262, #9321, #9565; PRs #9369, #9678, #9707, #9826.
- `carla-simulator/carla-digitaltwins`, branch `ue5-digitaltwins` — repository metadata read via the
  GitHub API, 2026-08-23.
- CARLA release notes 0.9.15 (Digital Twins v0.1), 0.10.0, 0.9.16.
- Unreal Engine 5.7.4 source, `UE_5_7_4/Engine/Source/` — Landscape, Foliage, PCG, MeshMergeUtilities,
  WorldComposition, LevelStreaming, NaniteBuilder.
- Workspace Unreal skill set, `.agents/skills/` — `ue-world-level-streaming`, `ue-procedural-generation`,
  `ue-editor-tools`, `ue-module-build-system`, `ue-actor-component-architecture`.
