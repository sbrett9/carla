# 24a — Shipping a generated world after the package is built

Addendum to `24_Generated_World_As_Editable_Level.md`. Researched 2026-09-01 against UE 5.7.4
(`UE_5_7_4/Engine`) and the state of `ue5-dev` at `2cadbf8c2`.

Doc 24 §7 deferred `-CreateReleaseVersion` and recorded the intent to reach DLC cooking and pak
mounting. This addendum settles what that would actually take. The short answer is that most of it
is unnecessary, and one piece of it would break the package.

Everything below is cited to engine source. Where something is inferred rather than read, it says so.

---

## 1. Corrections to doc 24

Three claims in doc 24 and in the working notes around it are wrong. They are corrected here rather
than edited away, because each one shaped a plan.

**"Plugin mounting is startup-only and cannot unload, so worlds must ship as paks."** False.
`IPluginManager::UnmountExplicitlyLoadedPlugin` (`Runtime/Projects/Public/Interfaces/IPluginManager.h:577-578`,
implementation `Private/PluginManager.cpp:3621`) is the engine's supported runtime unload for exactly
the kind of plugin a generated world is. This single wrong premise is what pointed the whole
distribution plan at paks.

**"`-CreateReleaseVersion` is incompatible with `-iterate`."** False, and the comment saying so was
committed in `Unreal/CMakeLists.txt`. The guard at
`Programs/AutomationTool/AutomationUtils/ProjectParams.cs:3323-3326` reads:

    if (HasBasedOnReleaseVersion && (IterativeCooking || IterativeDeploy || HasIterateSharedCookedBuild))
        throw new AutomationException("Can't use iterative cooking / deploy on dlc or patching or creating a release");

The message mentions creating a release; the condition tests `HasBasedOnReleaseVersion` only. The real
incompatibility binds the DLC cook, not the base cook.

**"The plugin artifact needs no mounting."** Already noted as wrong in doc 24's own open list; restated
here because the correction matters: loose files mean there is no pak to mount, but a plugin's content
root still must be, or `/<Name>/...` resolves to nothing.

---

## 2. Do not make the base package `-pak`. It would break it.

This is the finding with the widest blast radius, and it holds regardless of which distribution route
is chosen.

**The base build is loose today** — `Unreal/CMakeLists.txt` passes neither `-pak` nor `-iostore`, and
the staged output contains no `.pak`, `.utoc` or `.ucas`.

**Adding `-pak` would silently stop worlds being discovered.** A `.upluginmanifest` is written only
when paking (`Scripts/CopyBuildToStagingDirectory.Automation.cs:2187` —
`if (bCreatePluginManifest && Params.UsePak(...))`), and in a cooked build the plugin manager uses a
manifest *instead of* scanning the directory tree (`Runtime/Projects/Private/PluginManager.cpp:792` —
`if (ManifestFileNames.Num() == 0)` gates the recursive search). A world directory copied into a paked
package after it was built would therefore never be found. The loose decision was right; this is a
second, independent reason for it that was not previously recorded.

**Mounting a world pak into a loose base is worse than unnecessary — it is destructive.** Two
mechanisms, both in `Runtime/PakFile/Private/IPlatformFilePak.cpp`:

- With no paks on disk, `FPakPlatformFile::ShouldBeUsed` returns false (`:5581-5594`), so `Initialize`
  never runs (`Runtime/Launch/Private/LaunchEngineLoop.cpp:802-834`), so `FCoreDelegates::MountPak` is
  never bound (`:5714-5721`). Every mount call is a silent no-op. The engine even logs this case:
  `PluginManager.cpp:1970` — *"PAK file could not be mounted because MountPak is not bound"*.
- Forcing the wrapper in with `UE_FORCE_USE_PAKS=1` then hits `EXCLUDE_NONPAK_UE_EXTENSIONS`
  (`:241-242`), which adds `uasset`, `umap`, `ubulk`, `uexp`, `uptnl`, `ushaderbytecode` to a blocked
  list (`:5621-5629`) consulted by `IsNonPakFilenameAllowed` (`:4551-4563`):

      if (PakFiles.Num() || UE_BUILD_SHIPPING)
          bAllowed = !ExcludedNonPakExtensions.Contains(Ext);

  In Development the entire loose base package becomes unreadable the moment the first world pak
  mounts. In Shipping it is unreadable immediately, with zero paks mounted.

There is no configuration in which a world pak is worth having here.

---

## 3. Load and unload need no cooking or packaging change at all

The exported world plugin is already the exact shape the runtime mount API is built for:
`CanContainContent: true`, `ExplicitlyLoaded: true`, `NoCode: true`, empty `Modules`.

**Load.** `IPluginManager::MountExplicitlyLoadedPlugin(Name)` (`IPluginManager.h:545`, impl
`PluginManager.cpp:3292`), then the existing `LoadNewEpisode` by long package name. For a world
dropped into the package *after* the server started, `AddToPluginsList(<path>.uplugin)`
(`IPluginManager.h:288`) first — this is what the GameFeatures subsystem itself does
(`GameFeaturePluginStateMachine.cpp:2529`).

**Unload.** Travel to a lightweight map, let the travel complete, then
`UnmountExplicitlyLoadedPlugin(Name, &Reason)`. It unregisters the mount point, runs `CollectGarbage`
and traces stale references itself (`PluginManager.cpp:3621-3708` →
`CoreUObject/Private/Misc/CoreUObjectPluginManager.cpp:293-305`). The asset registry cleans up off
`OnContentPathDismounted` without being told.

**The travel-first ordering is mandatory, not stylistic.** Unmounting while the world is still current
makes every one of its packages a leak: `HandlePossibleAssetLeaks`
(`CoreUObjectPluginManager.cpp:145-289`) scans every `UPackage` under `/<PluginName>/`, ensures at
severity 2 by default (`:35-49`), then marks survivors garbage and renames them.

**Re-mounting after unmount works.** `RenameLeakedPackage` (`:280-286`) exists so that load → unload →
load cycles are supported.

Cost: no cook change, no packaging change, no engine change, and no invalidation of already-exported
worlds.

---

## 4. Put the road meshes inside the world plugin

Today a world's ~870 road-surface meshes live at `/Game/Carla/Static/Road/<World>/`, outside the
4-asset plugin, because `Tagger.h:76-90` derives semantic labels by splitting the asset path on `/`
and reading token index 4, which must be `"Road"`.

**That constraint is about depth, not about `/Game`.** Measured in the live editor:

| path | token[4] |
|---|---|
| `/Game/Carla/Static/Road/<World>/SM_RoadSurface_0` | `Road` |
| `/<World>/Carla/Static/Road/SM_RoadSurface_0` | `Road` |
| `/<World>/Carla/Static/Road/<World>/SM_RoadSurface_0` | `Road` |
| `/<World>/Static/Road/SM_RoadSurface_0` | *(wrong — one segment short)* |

So laying a world plugin's content out as `Content/Carla/Static/Road/…` keeps every semantic label
correct, with no change to `ATagger` and therefore no risk to stock maps.

This is the single highest-value change in this document. It makes a world self-contained, which:

- removes the DLC content-scope problem entirely (§5),
- makes a world directory a complete, removable unit, and
- means an unmounted world leaves nothing behind in project content.

**The path is re-read at runtime, which makes this both necessary and sufficient.**
`ACarlaGameModeBase::BeginPlay` calls `ATagger::TagActorsInLevel` unconditionally
(`Game/CarlaGameModeBase.cpp:189`) in every build configuration, and streaming and every spawn re-tag
as well (`:772`, `LargeMapManager.cpp:144`, `TaggerDelegate.cpp:23-28`). So the label a packaged server
uses is derived from the *cooked* asset path, not from anything saved at bake time — a world whose
meshes moved without the depth being preserved would look right in the editor and report `None` at
run time.

It also rules out the obvious alternative of baking the label in. `ATagger::SetStencilValue` writes
Custom Primitive Data floats 4–7 (`Tagger.cpp:80`), which is what the semantic and instance
segmentation cameras read; `CustomPrimitiveDataInternal` is `UPROPERTY(Transient)`
(`PrimitiveComponent.h:829-830`) so it is not saved, and the runtime pass overwrites it regardless.
Component tags *would* survive — `TagActor` appends (`Tagger.cpp:136`) while
`GetTagOfTaggedComponent` reads index 0 (`:182`), so a baker-written tag stays authoritative — but
that channel feeds only the CPU-side consumers (semantic lidar, bounding boxes,
`get_environment_objects`, `actor.semantic_tags`), not the cameras. A principled fix therefore needs a
`Tagger` change as well: fall back to `ComponentTags[0]` when the path yields `None`.

That fallback is **gated to components that are currently unlabelled**, not strictly a no-op. Where it
fires it makes the render channel agree with what `GetTagOfTaggedComponent` (`Tagger.cpp:181-182`) and
semantic lidar (`RayCastSemanticLidar.cpp:232`) already report for that same component, so consistency
is the likely outcome — but on a map with authored component tags it is a possible visible change in
the rendered segmentation image, not a provable no-op. A byte census of all 330 `.umap` files under
`Content/Carla/Maps` found the `ComponentTags` property name absent from `Town10HD_Opt`, `Town01`-`Town07`,
`Mine_01`, `Town12` and `OpenDriveMap`, and present once in `Town15` (which *is* shipped, per
`DefaultGame.ini`) and in 25 of the 49 `Town13` tiles (which are not). The census cannot distinguish an
authored tag from a mere reflected-property-name reference, so a zero is meaningful and a one is not.

**Settle it in the editor before implementing**, not from bytes: load Town15, iterate every
`UStaticMeshComponent`, and list those with a non-empty `ComponentTags` whose
`GetLabelByPath(GetStaticMesh())` is `None`. That is exactly the set the change would alter, it takes
minutes, and an empty set closes the question. Regression-test **Town15 as well as Town10HD_Opt**.

**Implementation note: do not bulk-copy the meshes.** `UEditorAssetLibrary::DuplicateAsset` does not
rewire references, and `DuplicateDirectory` just loops it (`EditorAssetSubsystem.cpp:961-1005`); the
exporter already had to hand-repair cross-references once for this reason
(`GeneratedLevelExporter.cpp:197-221`). The clean route is to re-run `BakeIntoWorld` on the duplicated
level with `AssetRootFolder` pointing inside the plugin — the baker already deletes and respawns by
`RoadSurfaceTag` (`RoadSurfaceBaker.cpp:225-246`), and `AssetRootFolder` is already a parameter
(`RoadSurfaceBaker.h:63-68`).

Two traps if the tag is written rather than the path relied on: write the *label* string
(`GetTagAsString(Roads)` is `"Roads"`, the folder is `"Road"`, and `GetTagFromString("Road")` matches
nothing), and never prefix it — `GetTagFromString` is ordered substring matching, so anything
containing `"Car"` resolves to `Car` before it reaches the intended keyword (`Tagger.cpp:208-216`).

Sizing: the tagger fallback plus a baker-written tag is about half a day including a Town10HD
regression pass, and is independently useful. Relocating the meshes so the plugin is self-contained is
roughly a day on top, and the work is in `GeneratedLevelExporter` rather than the tagger. It should
land **before** any DLC work, because it makes most of that work unnecessary.

---

## 5. DLC cooking: only for worlds created after a package ships

Worlds that exist when the package is cooked already ship, through the discovery script added in
`dd447d0d7`. DLC cooking serves exactly one case: a world built *after* a package was shipped.

**The content-scope rule.** `Cooker/CookRequestCluster.cpp:2842-2883` suppresses any package whose
path does not start with `<DLCPlugin>/Content`, unconditionally setting `bOutCookable = false`
(`:2878`). The whole block is gated on `bErrorOnEngineContentUse`, which UAT sets unless
`-DLCIncludeEngineContent` is passed (`Scripts/CookCommand.Automation.cs:218-225`).

With the meshes moved inside the plugin (§4) this rule is satisfied by construction and
`-DLCIncludeEngineContent` is not needed. Without §4, the cook fails loudly — roughly one
`LogCook: Error: Uncooked Engine or Game content ... is being referenced by DLC!` per mesh, escalating
to `AutomationException("Cook failed.")`. That is a good failure mode; it cannot silently ship a
broken world.

**Flags.** Base cook adds `-CreateReleaseVersion=<name>`; `-iterate` may stay (§1). Per-world cook:

    -DLCName=<World> -BasedOnReleaseVersion=<name>

with `-iterate` removed (`ProjectParams.cs:3323`), `-CreateReleaseVersion` absent (`:3318`), and
**not** reusing the base `-stagingdirectory` — with `-DLCName` and no staging directory the output
defaults to `<PluginDir>/Saved/StagedBuilds` (`ProjectParams.cs:1653-1656`); overriding it stages the
DLC on top of the base package.

**Cost.** `-CreateReleaseVersion` copies two files the cook already writes (`AssetRegistry.bin` and
`Metadata/DevelopmentAssetRegistry.bin`, ~29 MB) into `Releases/<name>/<Platform>/`
(`CookOnTheFlyServer.cpp:9550-9563`). It introduces a `BasedOnReleaseVersion=` key into
`CookedSettings.txt` that the previous cook lacks, forcing **one** full recook
(`Cooker/GlobalCookArtifact.cpp:101-109`, `:147-162`); iteration resumes afterwards.

**Two traps for our cook script**, both silent:

- `MapsToCook` and the `AllMaps` / `AlwaysCookMaps` ini sections are dead in a DLC cook — the whole
  block is gated on `!IsCookingDLC()` (`CookOnTheFlyServer.cpp:8460`).
- `DirectoriesToAlwaysCook` still *applies* (`Commandlets/CookCommandlet.cpp:482-497`, not DLC-gated)
  but every entry outside the plugin is then suppressed as `NotInCurrentPlugin` — and that suppression
  is deliberately not logged (`CookOnTheFlyServer.cpp:2936`). Our `/CesiumForUnreal` entry, and the
  credit-system fix that depends on it, would not carry into a DLC-cooked world.

Also: pass an explicit `-map=/<World>/Maps/<World>`. With no map or directory supplied,
`bCookAllByDefault` triggers a whole-project scan (`:8780-8812`), and with the plugin filter relaxed
that would sweep everything not in the base release into the DLC.

---

## 6. Game Feature Plugins are the wrong tool

Rejected, for reasons that are structural rather than stylistic:

- `GameFeatures` and `ModularGameplay` are not enabled in `CarlaUnreal.uproject`, and both descriptors
  carry `"IsBetaVersion": true`.
- Built-in discovery only accepts plugins under `Plugins/GameFeatures/`, hard-coded with no config
  knob (`GameFeaturesSubsystemSettings.cpp:19-62`), so adopting it means relocating a mount convention
  doc 24 §7 records as irreversible.
- A content GFP must carry a `UGameFeatureData` asset or registration hard-fails
  (`GameFeaturePluginStateMachine.cpp:3297-3352`) — a fifth, ceremonial asset per world.
- It is not a shipping mechanism. DLC cooking is; GFP sits on top of it.
- Its deactivate path performs no check that the world is unloaded, so the obvious call order
  `ensure`s and marks the live world's packages garbage.

The one thing it genuinely adds — appending a plugin's cooked asset registry on mount
(`:2606-2660`) — is a capability CARLA deliberately does not depend on: `GetAllMapNames` and
`FindMapPath` scan the filesystem precisely because the registry is written at cook time and cannot
see a later-added level (`Game/CarlaStatics.cpp:40-103`, `Game/CarlaEpisode.cpp:94-130`).

---

## 7. Revised plan

Ordered so that each step is useful on its own and none blocks on the next.

1. **Move road meshes into the world plugin** under `Content/Carla/Static/Road/…` (§4). Makes a world
   self-contained; no tagger change.
2. **Load / unload RPC** via `MountExplicitlyLoadedPlugin` / `UnmountExplicitlyLoadedPlugin`, with
   travel-away-first ordering (§3). No cook or packaging change.
3. **`--level <name>` on `RunCarlaServer`**, resolving through the same mount path.
4. **`-CreateReleaseVersion` on the base cook**, whenever post-ship worlds are actually wanted (§5).
   One full recook, then iteration resumes.
5. **Per-world DLC cook target**, output staged loose and copied in as a directory (§5).

Steps 1–3 deliver the capability doc 24 was aiming at. Steps 4–5 are only for worlds built after a
package ships.

### Defects found along the way

- `UCarlaStatics::GetAllMapNames` and `FindMapPath` enumerate plugin content for all *discovered*
  plugins regardless of mount state (`CarlaStatics.cpp:39-125`), so after an unmount a world would
  still be listed but no longer loadable. Needs a mounted-state filter; it is on the critical path for
  step 2.
- The comment in `Unreal/CMakeLists.txt` about `-CreateReleaseVersion` and `-iterate` is wrong (§1)
  and should be corrected when step 4 lands.
- `RayCastSemanticLidar.cpp:232` indexes `HitInfo.Component->ComponentTags[0]` with no `Num()` check.
  Any hit on an untagged primitive is out of range — and the runtime-generated procedural road is
  exactly that, since `TagActor` only iterates static and skeletal mesh components
  (`Tagger.cpp:124-125`, `:144-145`) and never sees a `UProceduralMeshComponent`. Latent today, and
  cheap to fix while in this code.
- `ComponentTags` grows by one entry per re-tag pass, for every component in the level
  (`Tagger.cpp:136`, `:155`, appended never cleared). Index 0 stays stable — which is what makes the
  component-tag fallback viable — but the array grows on every level load and every stream-in.

### Not established

- Whether a DLC cook can see `/<World>/` at all. The plugin is `ExplicitlyLoaded`, so
  `MountContentPlugins` skips it (`PluginManager.cpp:1990`), and it is `FCarlaModule::StartupModule`
  that mounts these by category (`Carla.cpp:42-68`). The DLC cook therefore depends on module load
  order, and no engine code force-mounts a DLC plugin by name. Verify empirically before committing to
  step 5.
- Whether `CollectFilesToCook`'s `RegisterMountPoint("/Game/", …)` fallback
  (`CookOnTheFlyServer.cpp:8600-8607`) ever fires for our `-cookdir` entries. If it does it would
  create a second `/Game` root pointing at plugin content and change observed package names — which is
  exactly what the tagger reads. Check a cook log before relying on §4.
- That `-CreateReleaseVersion` costs only one recook. Derived from the settings-comparison code, not
  observed. Cheap to confirm: cook twice and check the second logs `INCREMENTAL COOK:`.
